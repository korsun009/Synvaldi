using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using static Synvaldi.Store;

namespace Synvaldi;

public record Field(JsonElement? Value, long Revision, bool Deleted,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] long GuardRevision = 0);
public record Seed(Guid OperationId, Dictionary<string, JsonElement>? Fields);
public record Change(string Key, JsonElement? Value, bool Deleted, long BaseRevision, string? Group = null);
public record Batch(Guid OperationId, bool Offline, List<Change>? Changes, Guid? OnlineLease = null);
public record RevisionRequest(long Revision);
public record Rollback(Guid OperationId, long Revision);
public record Mutation(long Revision, List<string> Applied, List<string> Conflicts);
public record ProfileDto(Guid Id, string Name, long Revision, bool Admitted);
public record Focus(string? WindowId, string? WorkspaceId, string? TabId);
internal record Snapshot(long Revision, Dictionary<string, Field> Fields, bool Admitted, Guid Creator);

public static partial class Profiles
{
    private const int MaxFields = 50000;
    private const int MaxStateChars = 16 * 1024 * 1024;
    private const string Projection = """
        SELECT p.id, p.name, p.revision,
            EXISTS(SELECT FROM admission a WHERE a.profile_id=p.id AND a.device_id=$2)
        FROM profile p WHERE p.account_id=$1
        """;
    private static ProfileDto ReadProfile(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetBoolean(3));

    public static void Map(RouteGroupBuilder api)
    {
        MapArchive(api);
        api.MapGet("/profiles", async (HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            await using var conn = await db.Open(ct);
            return await Rows(conn, Projection + " ORDER BY p.name,p.id", ReadProfile, ct, actor.AccountId, actor.DeviceId);
        });
        api.MapPost("/profiles", async (Rename input, HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            var id = Guid.NewGuid();
            var name = Wire.Label(input.Name);
            return await db.Write(async conn =>
            {
                await Exec(conn, """
                    INSERT INTO profile(id,account_id,name,created_by) VALUES ($1,$2,$3,$4)
                    ON CONFLICT(account_id,name) DO NOTHING
                    """, ct, id, actor.AccountId, name, actor.DeviceId);
                return (await Rows(conn, Projection + " AND p.name=$3", ReadProfile, ct, actor.AccountId, actor.DeviceId, name)).Single();
            }, ct);
        });
        api.MapPatch("/profiles/{id:guid}", async (Guid id, Rename input, HttpContext http, Store db, CancellationToken ct) =>
        {
            await using var conn = await db.Open(ct);
            try
            {
                if (await Exec(conn, "UPDATE profile SET name=$3 WHERE id=$1 AND account_id=$2", ct, id, http.Actor().AccountId, Wire.Label(input.Name)) == 0)
                    throw ApiError.Missing();
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            { throw ApiError.Conflict("profile_name_taken"); }
            return Results.NoContent();
        });
        api.MapGet("/profiles/{id:guid}/state", async (Guid id, HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            return await db.Write(async conn =>
            {
                await Accounts.LockSession(conn, actor, ct);
                var view = await Read(conn, id, actor, false, ct);
                if (!view.Admitted && view.Revision > 0)
                    await Exec(conn, """
                        INSERT INTO initial_read(profile_id,device_id,revision) VALUES ($1,$2,$3)
                        ON CONFLICT(profile_id,device_id) DO UPDATE SET revision=greatest(initial_read.revision,excluded.revision)
                        """, ct, id, actor.DeviceId, view.Revision);
                return Results.Ok(new { view.Revision, view.Fields });
            }, ct);
        });
        api.MapPost("/profiles/{id:guid}/seed", async (Guid id, Seed input, HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (input.Fields is null || input.Fields.Count is 0 or > MaxFields) throw ApiError.Bad("invalid_fields");
            foreach (var key in input.Fields.Keys) Key(key);
            return await Mutate(db, id, actor, input.OperationId, "seed", input, async (conn, state) =>
            {
                if (state.Revision != 0) throw ApiError.Conflict("already_seeded");
                if (state.Creator != actor.DeviceId)
                {
                    var previous = await Rows(conn, "SELECT revoked FROM device WHERE id=$1", r => r.GetBoolean(0), ct, state.Creator);
                    if (previous.Count == 1 && !previous[0]) throw ApiError.Conflict("seed_reserved");
                }
                foreach (var (key, value) in input.Fields) state.Fields[key] = new(value, 1, false);
                await Admit(conn, id, actor.DeviceId, ct);
                return new Mutation(1, input.Fields.Keys.ToList(), []);
            }, ct);
        });
        api.MapPost("/profiles/{id:guid}/admit", async (Guid id, RevisionRequest input, HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            await db.Write(async conn =>
            {
                await Accounts.LockSession(conn, actor, ct);
                var state = await Read(conn, id, actor, true, ct);
                if (state.Revision == 0 || input.Revision != state.Revision) throw ApiError.Conflict("state_changed");
                if (!state.Admitted && await Exec(conn, "DELETE FROM initial_read WHERE profile_id=$1 AND device_id=$2 AND revision=$3",
                    ct, id, actor.DeviceId, state.Revision) == 0) throw ApiError.Conflict("download_required");
                return await Admit(conn, id, actor.DeviceId, ct);
            }, ct);
            return Results.NoContent();
        });
        api.MapPost("/profiles/{id:guid}/changes", async (Guid id, Batch input, HttpContext http, Store db, CancellationToken ct) =>
        {
            if (input.Changes is null || input.Changes.Count is 0 or > MaxFields) throw ApiError.Bad("invalid_changes");
            var keys = new HashSet<string>();
            foreach (var change in input.Changes)
            {
                if (change is null || change.BaseRevision < 0 || !keys.Add(Key(change.Key)) || change.Group is { Length: > 256 })
                    throw ApiError.Bad("invalid_changes");
            }
            var actor = http.Actor();
            return await Mutate(db, id, actor, input.OperationId, "changes", input, async (conn, state) =>
            {
                RequireAdmission(state);
                var next = state.Revision + 1;
                var leaseValid = !input.Offline && input.OnlineLease is Guid lease &&
                    (await Rows(conn, "SELECT 1 FROM online_lease WHERE id=$1 AND session_hash=$2 AND expires_at>now()",
                        r => r.GetInt32(0), ct, lease, actor.TokenHash)).Count != 0;
                List<string> applied = [], conflicts = [];
                var cold = input.Changes.Where(c => c.Key.StartsWith(ColdReconciliation.Prefix, StringComparison.Ordinal)).ToArray();
                var handled = new HashSet<string>();
                if (cold.Length != 0 && cold.All(c => !c.Deleted && c.Group is null))
                {
                    var merged = await ColdReconciliation.Merge(conn, id, state.Fields, cold, ct);
                    conflicts.AddRange(merged.Conflicts);
                    foreach (var edit in cold) handled.Add(edit.Key);
                    foreach (var (key, value) in merged.Values)
                    {
                        state.Fields.TryGetValue(key, out var old);
                        if (old is not null && !old.Deleted && Equal(old.Value, value)) continue;
                        state.Fields[key] = new(value, next, false, old?.GuardRevision ?? 0);
                        applied.Add(key);
                    }
                }
                foreach (var group in input.Changes.Where(c => !handled.Contains(c.Key))
                    .GroupBy(c => (grouped: c.Group is not null, id: c.Group ?? c.Key)))
                {
                    var edits = group.ToArray();
                    if (edits.Any(c => Conflicts(state.Fields, c, leaseValid)))
                    {
                        conflicts.AddRange(edits.Select(c => c.Key));
                        continue;
                    }
                    foreach (var change in edits)
                    {
                        state.Fields.TryGetValue(change.Key, out var old);
                        var value = change.Deleted ? null : change.Value;
                        if (old is not null && old.Deleted == change.Deleted && Equal(old.Value, value))
                            continue;
                        state.Fields[change.Key] = new(value, next, change.Deleted, change.Deleted ? next : old?.GuardRevision ?? 0);
                        applied.Add(change.Key);
                    }
                }
                return new Mutation(applied.Count == 0 ? state.Revision : next, applied, conflicts);
            }, ct);
        });
        api.MapGet("/profiles/{id:guid}/history", async (Guid id, HttpContext http, Store db, CancellationToken ct) =>
        {
            await using var conn = await db.Open(ct);
            await Exists(conn, id, http.Actor().AccountId, ct);
            return await Rows(conn, """
                SELECT number,created_at,device_name,jsonb_array_length(changed_keys),
                       ARRAY(SELECT jsonb_array_elements_text(changed_keys) LIMIT 20)
                FROM revision WHERE profile_id=$1 AND created_at>=now()-interval '72 hours'
                ORDER BY number DESC
                """, r =>
            {
                return new
                {
                    revision = r.GetInt64(0),
                    createdAt = r.GetDateTime(1),
                    deviceName = r.GetString(2),
                    changedCount = r.GetInt32(3),
                    changedKeys = r.GetFieldValue<string[]>(4)
                };
            }, ct, id);
        });
        api.MapPost("/profiles/{id:guid}/rollback", async (Guid id, Rollback input, HttpContext http, Store db, CancellationToken ct) =>
            await Mutate(db, id, http.Actor(), input.OperationId, "rollback", input, async (conn, state) =>
            {
                RequireAdmission(state);
                var history = await LoadRevision(conn, id, input.Revision, ct);
                return Restore(state, history, full: true);
            }, ct));
        api.MapGet("/profiles/{id:guid}/focus", async (Guid id, Guid? deviceId, HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            await using var conn = await db.Open(ct);
            await Exists(conn, id, actor.AccountId, ct);
            var result = await Rows(conn, """
                SELECT f.payload::text,f.updated_at FROM focus_state f JOIN device d ON d.id=f.device_id
                WHERE f.profile_id=$1 AND d.id=$2 AND d.account_id=$3 AND NOT d.revoked
                """, r => (focus: Wire.Decode<Focus>(r.GetString(0)), date: (DateTime?)r.GetDateTime(1)), ct, id, deviceId ?? actor.DeviceId, actor.AccountId);
            var current = result.FirstOrDefault();
            return new { windowId = current.focus?.WindowId, workspaceId = current.focus?.WorkspaceId, tabId = current.focus?.TabId, updatedAt = current.date };
        });
        api.MapPut("/profiles/{id:guid}/focus", async (Guid id, Focus focus, HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            foreach (var item in new[] { focus.WindowId, focus.WorkspaceId, focus.TabId })
                if (item is { Length: > 1024 } || item?.Any(char.IsControl) == true) throw ApiError.Bad("invalid_focus");
            await using var conn = await db.Open(ct);
            if (await Exec(conn, """
                INSERT INTO focus_state(profile_id,device_id,payload)
                SELECT p.id,$2,$3::jsonb FROM profile p JOIN admission a ON a.profile_id=p.id AND a.device_id=$2
                WHERE p.id=$1 AND p.account_id=$4
                ON CONFLICT(profile_id,device_id) DO UPDATE SET payload=excluded.payload,updated_at=now()
                """, ct, id, actor.DeviceId, Wire.Encode(focus), actor.AccountId) == 0)
                throw ApiError.Conflict("admission_required");
            return Results.NoContent();
        });
    }

    private static bool Equal(JsonElement? a, JsonElement? b) =>
        a is null ? b is null || b.Value.ValueKind == JsonValueKind.Null :
        b is null ? a.Value.ValueKind == JsonValueKind.Null : JsonElement.DeepEquals(a.Value, b.Value);

    private static string Key(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024 || key.Any(char.IsControl)) throw ApiError.Bad("invalid_key");
        return key;
    }
    private static Task<int> Admit(NpgsqlConnection conn, Guid id, Guid device, CancellationToken ct) =>
        Exec(conn, "INSERT INTO admission(profile_id,device_id) VALUES ($1,$2) ON CONFLICT DO NOTHING", ct, id, device);
    private static void RequireAdmission(Snapshot state)
    {
        if (!state.Admitted || state.Revision == 0) throw ApiError.Conflict("admission_required");
    }
    private static async Task Exists(NpgsqlConnection conn, Guid id, Guid account, CancellationToken ct)
    {
        if ((await Rows(conn, "SELECT id FROM profile WHERE id=$1 AND account_id=$2", r => r.GetGuid(0), ct, id, account)).Count == 0)
            throw ApiError.Missing();
    }
    private static async Task<Snapshot> Read(NpgsqlConnection conn, Guid id, Actor actor, bool locked, CancellationToken ct)
    {
        var rows = await Rows(conn, """
            SELECT p.revision,p.fields::text,EXISTS(SELECT FROM admission WHERE profile_id=p.id AND device_id=$3),p.created_by
            FROM profile p WHERE p.id=$1 AND p.account_id=$2
            """ + (locked ? " FOR UPDATE" : ""), r => new Snapshot(r.GetInt64(0),
                Wire.Decode<Dictionary<string, Field>>(r.GetString(1)), r.GetBoolean(2), r.GetGuid(3)),
            ct, id, actor.AccountId, actor.DeviceId);
        return rows.SingleOrDefault() ?? throw ApiError.Missing();
    }

    private static Task<Mutation> Mutate<T>(Store db, Guid id, Actor actor, Guid operationId, string kind, T body,
        Func<NpgsqlConnection, Snapshot, Task<Mutation>> apply, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw ApiError.Bad("invalid_operation_id");
        var digest = Wire.Hash(kind + "\n" + Wire.Encode(body));
        return db.Write(async conn =>
        {
            await Accounts.LockSession(conn, actor, ct);
            var state = await Read(conn, id, actor, true, ct);
            var previous = await Rows(conn, "SELECT device_id,digest,result::text FROM operation WHERE profile_id=$1 AND id=$2",
                r => (device: r.GetGuid(0), digest: r.GetFieldValue<byte[]>(1), result: r.GetString(2)), ct, id, operationId);
            if (previous.Count != 0)
            {
                RequireAdmission(state);
                var entry = previous[0];
                if (entry.device != actor.DeviceId || !CryptographicOperations.FixedTimeEquals(entry.digest, digest))
                    throw ApiError.Conflict("operation_id_reused");
                return Wire.Decode<Mutation>(entry.result);
            }
            var before = new Dictionary<string, Field>(state.Fields);
            var result = await apply(conn, state);
            if (result.Revision != state.Revision)
            {
                var json = Wire.Encode(state.Fields);
                if (json.Length > MaxStateChars || state.Fields.Count > MaxFields) throw new ApiError(413, "profile_too_large");
                await Exec(conn, "UPDATE profile SET revision=$2,fields=$3::jsonb WHERE id=$1", ct, id, result.Revision, json);
                // Store reverse deltas. A full checkpoint every 128 changes bounds reconstruction work.
                // A later checkpoint stays alive at least as long as each revision that depends on it.
                var checkpoint = result.Revision == 1 || result.Revision % 128 == 0;
                var undo = result.Revision == 1 ? "{}" : Wire.Encode(result.Applied.ToDictionary(key => key, key => before.GetValueOrDefault(key)));
                await Exec(conn, """
                    INSERT INTO revision(profile_id,number,device_name,fields,changed_keys,checkpoint,previous_fields)
                    VALUES ($1,$2,$3,$4::jsonb,$5::jsonb,$6,$7::jsonb)
                    """, ct, id, result.Revision, actor.DeviceName, checkpoint ? json : "{}", Wire.Encode(result.Applied), checkpoint, undo);
                await Exec(conn, "SELECT pg_notify('synvaldi_change',$1)", ct, actor.AccountId.ToString());
            }
            await Exec(conn, "INSERT INTO operation(profile_id,id,device_id,digest,result) VALUES ($1,$2,$3,$4,$5::jsonb)",
                ct, id, operationId, actor.DeviceId, digest, Wire.Encode(result));
            return result;
        }, ct);
    }
}
