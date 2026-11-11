using System.Security.Cryptography;
using System.Text;
using Npgsql;
using static Synvaldi.Store;

namespace Synvaldi;

public sealed record Credentials(string Username, string Password, string DeviceName, Guid? DeviceId);
public sealed record Rename(string Name);
public sealed record PasswordChange(string CurrentPassword, string NewPassword);
public sealed record DeviceStatus(Guid? ProfileId, bool BrowserOnline, long LastAppliedRevision);

public static class Accounts
{
    private const int Iterations = 600000;
    private static readonly string DummyHash = PasswordHash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    private static string PasswordHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToHexString(salt)}.{Convert.ToHexString(hash)}";
    }
    private static bool Matches(string? password, string digest)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024) return false;
        var parts = digest.Split('.');
        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(parts[1]),
            int.Parse(parts[0]), HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(parts[2]));
    }
    private static void RequirePassword(string? value)
    {
        if (value is null || value.Length is < 8 or > 1024) throw ApiError.Bad("password_length");
    }

    public static async Task<Actor> Authenticate(HttpContext http, Store store)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.Length != 71 || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw ApiError.Denied();
        var digest = Wire.Hash(header[7..]);
        var ct = http.RequestAborted;
        await using var conn = await store.Open(ct);
        var rows = await Rows(conn, """
            SELECT d.account_id, d.id, d.name, d.last_seen < now() - interval '1 minute'
            FROM session s JOIN device d ON d.id=s.device_id
            WHERE s.token_hash=$1 AND s.expires_at>now() AND NOT d.revoked
            """, r => (actor: new Actor(r.GetGuid(0), r.GetGuid(1), r.GetString(2), digest), touch: r.GetBoolean(3)), ct, digest);
        if (rows.Count == 0) throw ApiError.Denied();
        if (rows[0].touch)
            await Exec(conn, "UPDATE device SET last_seen=now() WHERE id=$1 AND last_seen<now()-interval '1 minute'", ct, rows[0].actor.DeviceId);
        return rows[0].actor;
    }

    internal static async Task LockSession(NpgsqlConnection conn, Actor actor, CancellationToken ct)
    {
        // A revoke and an admission or change must have an unambiguous commit order.
        var valid = await Rows(conn, """
            SELECT d.id FROM device d JOIN session s ON s.device_id=d.id
            WHERE d.id=$1 AND d.account_id=$2 AND NOT d.revoked AND s.token_hash=$3 AND s.expires_at>now()
            FOR SHARE OF d,s
            """, r => r.GetGuid(0), ct, actor.DeviceId, actor.AccountId, actor.TokenHash);
        if (valid.Count == 0) throw ApiError.Denied();
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/auth/register", (Credentials input, Store db, IConfiguration config, CancellationToken ct) =>
        {
            if (!config.GetValue("SYNVALDI_REGISTRATION", true)) throw new ApiError(403, "registration_closed");
            return LoginOrRegister(input, db, true, ct);
        }).WithMetadata(new PublicEndpoint()).RequireRateLimiting("login");
        api.MapPost("/auth/login", (Credentials input, Store db, CancellationToken ct) =>
            LoginOrRegister(input, db, false, ct)).WithMetadata(new PublicEndpoint()).RequireRateLimiting("login");

        api.MapPost("/auth/logout", async (HttpContext http, Store db, CancellationToken ct) =>
        {
            await using var conn = await db.Open(ct);
            await Exec(conn, "DELETE FROM session WHERE token_hash=$1", ct, http.Actor().TokenHash);
            return Results.NoContent();
        });
        api.MapPost("/auth/password", async (PasswordChange input, HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            RequirePassword(input.NewPassword);
            await db.Write(async conn =>
            {
                var secrets = await Rows(conn, "SELECT secret FROM account WHERE id=$1 FOR UPDATE", r => r.GetString(0), ct, actor.AccountId);
                if (secrets.Count == 0 || !Matches(input.CurrentPassword, secrets[0])) throw new ApiError(403, "invalid_password");
                await Exec(conn, "UPDATE account SET secret=$2 WHERE id=$1", ct, actor.AccountId, PasswordHash(input.NewPassword));
                return await Exec(conn, """
                    DELETE FROM session USING device WHERE device.id=session.device_id AND device.account_id=$1 AND session.token_hash<>$2
                    """, ct, actor.AccountId, actor.TokenHash);
            }, ct);
            return Results.NoContent();
        }).RequireRateLimiting("login");
        api.MapGet("/devices", async (HttpContext http, Store db, CancellationToken ct) =>
        {
            await using var conn = await db.Open(ct);
            return await Rows(conn, """
                SELECT id,name,last_seen,EXISTS(SELECT FROM online_lease WHERE device_id=d.id AND expires_at>now()),
                       browser_online,active_profile,last_applied FROM device d WHERE account_id=$1 AND NOT revoked ORDER BY name,id
                """, r => new
            {
                id = r.GetGuid(0),
                name = r.GetString(1),
                lastSeen = r.GetDateTime(2),
                online = r.GetBoolean(3),
                browserOnline = r.GetBoolean(4) && r.GetBoolean(3),
                profileId = r.IsDBNull(5) ? null : (Guid?)r.GetGuid(5),
                lastAppliedRevision = r.GetInt64(6)
            }, ct, http.Actor().AccountId);
        });
        api.MapPut("/devices/me/status", async (DeviceStatus status, HttpContext http, Store db, CancellationToken ct) =>
        {
            if (status.LastAppliedRevision < 0) throw ApiError.Bad("invalid_revision");
            var actor = http.Actor();
            await using var conn = await db.Open(ct);
            if (status.ProfileId is Guid profile)
            {
                if (await Exec(conn, """
                    UPDATE device SET active_profile=$3,browser_online=$4,last_applied=$5,last_seen=now()
                    WHERE id=$1 AND account_id=$2 AND EXISTS(
                        SELECT FROM profile WHERE id=$3 AND account_id=$2 AND revision>=$5)
                    """, ct, actor.DeviceId, actor.AccountId, profile, status.BrowserOnline, status.LastAppliedRevision) == 0)
                    throw ApiError.Bad("invalid_profile_revision");
            }
            else await Exec(conn, "UPDATE device SET active_profile=NULL,browser_online=$2,last_applied=0,last_seen=now() WHERE id=$1",
                ct, actor.DeviceId, status.BrowserOnline);
            return Results.NoContent();
        });
        api.MapPatch("/devices/{id:guid}", async (Guid id, Rename input, HttpContext http, Store db, CancellationToken ct) =>
        {
            await using var conn = await db.Open(ct);
            if (await Exec(conn, "UPDATE device SET name=$3 WHERE id=$1 AND account_id=$2 AND NOT revoked", ct, id, http.Actor().AccountId, Wire.Label(input.Name)) == 0)
                throw ApiError.Missing();
            return Results.NoContent();
        });
        api.MapDelete("/devices/{id:guid}", async (Guid id, HttpContext http, Store db, CancellationToken ct) =>
        {
            await db.Write(async conn =>
            {
                if (await Exec(conn, "UPDATE device SET revoked=true WHERE id=$1 AND account_id=$2 AND NOT revoked", ct, id, http.Actor().AccountId) == 0)
                    throw ApiError.Missing();
                await Exec(conn, "DELETE FROM session WHERE device_id=$1", ct, id);
                await Exec(conn, "DELETE FROM focus_state WHERE device_id=$1", ct, id);
                await Exec(conn, "DELETE FROM initial_read WHERE device_id=$1", ct, id);
                return await Exec(conn, "DELETE FROM admission WHERE device_id=$1", ct, id);
            }, ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> LoginOrRegister(Credentials input, Store db, bool register, CancellationToken ct)
    {
        var username = Wire.Label(input.Username, 128);
        var key = username.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var deviceName = Wire.Label(input.DeviceName);
        if (input.DeviceId == Guid.Empty) throw ApiError.Bad("invalid_device");
        if (register) RequirePassword(input.Password);
        return await db.Write(async conn =>
        {
            Guid accountId;
            if (register)
            {
                accountId = Guid.NewGuid();
                var inserted = await Exec(conn, """
                    INSERT INTO account(id,username,username_key,secret) VALUES ($1,$2,$3,$4) ON CONFLICT(username_key) DO NOTHING
                    """, ct, accountId, username, key, PasswordHash(input.Password));
                if (inserted == 0) throw ApiError.Conflict("username_taken");
            }
            else
            {
                var found = await Rows(conn, "SELECT id,secret,username FROM account WHERE username_key=$1 FOR UPDATE",
                    r => (id: r.GetGuid(0), secret: r.GetString(1), name: r.GetString(2)), ct, key);
                if (!Matches(input.Password, found.Count == 0 ? DummyHash : found[0].secret) || found.Count == 0)
                    throw new ApiError(401, "invalid_credentials");
                accountId = found[0].id;
                username = found[0].name;
            }
            var deviceId = input.DeviceId ?? Guid.NewGuid();
            var saved = await Exec(conn, """
                INSERT INTO device(id,account_id,name) VALUES ($1,$2,$3)
                ON CONFLICT(id) DO UPDATE SET revoked=false,last_seen=now()
                WHERE device.account_id=excluded.account_id
                """, ct, deviceId, accountId, deviceName);
            if (saved == 0) throw ApiError.Conflict("device_in_use");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await Exec(conn, "DELETE FROM session WHERE device_id=$1", ct, deviceId);
            await Exec(conn, "INSERT INTO session(token_hash,device_id,expires_at) VALUES ($1,$2,now()+interval '180 days')",
                ct, Wire.Hash(token), deviceId);
            return Results.Ok(new { token, deviceId, accountId, username });
        }, ct);
    }
}
