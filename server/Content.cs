using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using static Synvaldi.Store;

namespace Synvaldi;

public sealed class Push(Store store, ILogger<Push> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, (Guid Account, Channel<bool> Channel)> listeners = new();

    public async Task Stream(HttpContext http, CancellationToken ct)
    {
        var actor = http.Actor();
        var key = Guid.NewGuid();
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        listeners[key] = (actor.AccountId, signal);
        var seen = new Dictionary<Guid, long>();
        try
        {
            await using (var initial = await store.Open(ct))
                await Exec(initial, """
                    INSERT INTO online_lease(id,device_id,session_hash,expires_at) VALUES($1,$2,$3,now()+interval '35 seconds')
                    ON CONFLICT(device_id) DO UPDATE SET id=excluded.id,session_hash=excluded.session_hash,expires_at=excluded.expires_at
                    """, ct, key, actor.DeviceId, actor.TokenHash);
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            while (!ct.IsCancellationRequested)
            {
                // A second process can revoke a token or publish while this listener reconnects.
                // Recheck both token and head revisions on every wake, including the heartbeat.
                try { await Accounts.Authenticate(http, store); }
                catch (ApiError e) when (e.Status == 401) { return; }
                await using (var conn = await store.Open(ct))
                {
                    if (await Exec(conn, "UPDATE online_lease SET expires_at=now()+interval '35 seconds' WHERE id=$1", ct, key) == 0)
                        return;
                    var lease = Wire.Encode(new { type = "online", onlineLease = key });
                    await http.Response.WriteAsync($"data: {lease}\n\n", ct);
                    var heads = await Rows(conn, "SELECT id,revision FROM profile WHERE account_id=$1",
                        r => (id: r.GetGuid(0), revision: r.GetInt64(1)), ct, actor.AccountId);
                    foreach (var head in heads)
                    {
                        if (seen.TryGetValue(head.id, out var last) && head.revision == last) continue;
                        seen[head.id] = head.revision;
                        var payload = Wire.Encode(new { profileId = head.id, head.revision });
                        await http.Response.WriteAsync($"data: {payload}\n\n", ct);
                    }
                }
                await http.Response.WriteAsync(": heartbeat\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try { await signal.Reader.ReadAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }
        finally
        {
            listeners.TryRemove(key, out _);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await using var conn = await store.Open(cleanup.Token);
                await Exec(conn, "DELETE FROM online_lease WHERE id=$1", cleanup.Token, key);
            }
            catch (Exception e) when (e is NpgsqlException or OperationCanceledException)
            { log.LogDebug("Lease will expire automatically"); }
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await store.Open(stoppingToken);
                conn.Notification += (_, message) =>
                {
                    if (!Guid.TryParse(message.Payload, out var accountId)) return;
                    foreach (var listener in listeners.Values)
                        if (listener.Account == accountId) listener.Channel.Writer.TryWrite(true);
                };
                await Exec(conn, "LISTEN synvaldi_change", stoppingToken);
                while (!stoppingToken.IsCancellationRequested) await conn.WaitAsync(stoppingToken);
            }
            catch (NpgsqlException e)
            {
                log.LogWarning("Push listener reconnecting ({Type})", e.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}

public static class Content
{
    private const long MaxBlob = 64L * 1024 * 1024;
    private static string Digest(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw ApiError.Bad("invalid_digest");
        return sha256.ToLowerInvariant();
    }
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/events", (HttpContext http, Push push, CancellationToken ct) => push.Stream(http, ct));
        api.MapGet("/blobs/{sha256}", async (string sha256, HttpContext http, Store db, CancellationToken ct) =>
        {
            var digest = Digest(sha256);
            await using var conn = await db.Open(ct);
            await using var cmd = Sql(conn, "SELECT octet_length(body),body FROM blob WHERE account_id=$1 AND sha256=$2", http.Actor().AccountId, digest);
            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (!await reader.ReadAsync(ct)) throw ApiError.Missing();
            http.Response.ContentType = "application/octet-stream";
            http.Response.ContentLength = reader.GetInt32(0);
            await using var body = await reader.GetStreamAsync(1, ct);
            await body.CopyToAsync(http.Response.Body, ct);
        });
        api.MapPut("/blobs/{sha256}", async (string sha256, HttpContext http, Store db, IConfiguration config, CancellationToken ct) =>
        {
            var digest = Digest(sha256);
            if (http.Request.ContentLength > MaxBlob) throw new ApiError(413, "blob_too_large");
            await using var file = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                int size;
                while ((size = await http.Request.Body.ReadAsync(buffer.AsMemory(0, 65536), ct)) > 0)
                {
                    if (file.Length + size > MaxBlob) throw new ApiError(413, "blob_too_large");
                    hash.AppendData(buffer.AsSpan(0, size));
                    await file.WriteAsync(buffer.AsMemory(0, size), ct);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(digest)))
                throw ApiError.Bad("digest_mismatch");
            file.Position = 0;
            var account = http.Actor().AccountId;
            await db.Write(async conn =>
            {
                await Exec(conn, "SELECT id FROM account WHERE id=$1 FOR UPDATE", ct, account);
                var existing = await Rows(conn, "SELECT 1 FROM blob WHERE account_id=$1 AND sha256=$2", r => r.GetInt32(0), ct, account, digest);
                if (existing.Count != 0) return 0;
                var used = (await Rows(conn, "SELECT coalesce(sum(octet_length(body)),0)::bigint FROM blob WHERE account_id=$1",
                    r => r.GetInt64(0), ct, account)).Single();
                if (used + file.Length > config.GetValue("SYNVALDI_BLOB_QUOTA", 2L * 1024 * 1024 * 1024))
                    throw new ApiError(413, "blob_quota");
                await using var cmd = Sql(conn, "INSERT INTO blob(account_id,sha256,body) VALUES ($1,$2,$3)", account, digest);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = file });
                return await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            return Results.NoContent();
        });
    }
}
