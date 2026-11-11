using System.Text.Json;
using Npgsql;
using static Synvaldi.Store;

namespace Synvaldi;

public record TrashRequest(Guid OperationId, string Name, List<string>? Keys);
public record OperationRequest(Guid OperationId);

public static partial class Profiles
{
    // Start at the nearest newer full image (or at the head) and undo at most 127 deltas.
    // Pre-delta server versions wrote a full image on every revision; the migration marks these as checkpoints.
    private static async Task<Dictionary<string, Field>> LoadRevision(NpgsqlConnection conn, Guid id, long number, CancellationToken ct)
    {
        var starts = await Rows(conn, """
            SELECT i.number,i.fields::text FROM (
                SELECT r.number,r.fields FROM revision r WHERE r.profile_id=$1 AND r.number>=$2 AND r.checkpoint
                UNION ALL SELECT p.revision,p.fields FROM profile p WHERE p.id=$1
            ) i WHERE EXISTS (SELECT FROM revision WHERE profile_id=$1 AND number=$2 AND created_at>=now()-interval '72 hours')
            ORDER BY i.number LIMIT 1
            """, r => (number: r.GetInt64(0), fields: Wire.Decode<Dictionary<string, Field>>(r.GetString(1))), ct, id, number);
        if (starts.Count == 0) throw ApiError.Missing();
        var snapshot = starts[0];
        if (snapshot.number == number) return snapshot.fields;
        var deltas = await Rows(conn, """
            SELECT previous_fields::text FROM revision WHERE profile_id=$1 AND number>$2 AND number<=$3 ORDER BY number DESC
            """, r => Wire.Decode<Dictionary<string, Field?>>(r.GetString(0)), ct, id, number, snapshot.number);
        if (deltas.Count != snapshot.number - number) throw ApiError.Missing();
        foreach (var delta in deltas)
            foreach (var (key, original) in delta)
                if (original is null) snapshot.fields.Remove(key); else snapshot.fields[key] = original;
        return snapshot.fields;
    }
    private static bool Conflicts(Dictionary<string, Field> fields, Change change, bool online)
    {
        fields.TryGetValue(change.Key, out var old);
        if (change.BaseRevision < (old?.GuardRevision ?? 0)) return true;
        if (!online && (old?.Revision ?? 0) != change.BaseRevision) return true;
        var cut = change.Key.LastIndexOf('/');
        if (cut < 1 || change.Key.EndsWith("/exists", StringComparison.Ordinal)) return false;
        var presenceKey = change.Key[..cut] + "/exists";
        return fields.TryGetValue(presenceKey, out var presence) && presence.Deleted && !change.Deleted;
    }

    private static Mutation Restore(Snapshot state, Dictionary<string, Field> target, bool full)
    {
        var next = state.Revision + 1;
        List<string> applied = [];
        var keys = full ? state.Fields.Keys.Union(target.Keys).ToArray() : target.Keys.ToArray();
        foreach (var key in keys)
        {
            state.Fields.TryGetValue(key, out var old);
            target.TryGetValue(key, out var restored);
            var deleted = restored?.Deleted ?? true;
            if ((old?.Deleted ?? true) == deleted && Equal(old?.Value, restored?.Value)) continue;
            state.Fields[key] = new(restored?.Value, next, deleted, next);
            applied.Add(key);
        }
        return new(next, applied, []);
    }

    private static HashSet<string> ExpandTrash(Dictionary<string, Field> fields, IEnumerable<string> requested)
    {
        var keys = requested.ToHashSet(StringComparer.Ordinal);
        var prefixes = new HashSet<string>();
        var workspaces = new HashSet<string>();
        foreach (var key in keys)
        {
            var parts = key.Split('/');
            if (parts.Length != 3 || parts[2] != "exists") continue;
            prefixes.Add(parts[0] + "/" + parts[1]);
            if (parts[0] == "workspace") workspaces.Add(parts[1]);
        }
        foreach (var (key, field) in fields)
        {
            if (field.Deleted || field.Value is not JsonElement value || value.ValueKind != JsonValueKind.String ||
                !workspaces.Contains(value.GetString()!)) continue;
            var parts = key.Split('/');
            if (parts.Length == 3 && parts[0] == "tab" && parts[2] == "workspaceId") prefixes.Add("tab/" + parts[1]);
        }
        foreach (var key in fields.Keys)
        {
            var cut = key.LastIndexOf('/');
            if (cut > 0 && prefixes.Contains(key[..cut])) keys.Add(key);
        }
        return keys;
    }

    private static void MapArchive(RouteGroupBuilder api)
    {
        api.MapGet("/profiles/{id:guid}/history/{number:long}", async (Guid id, long number, HttpContext http, Store db, CancellationToken ct) =>
        {
            await using var conn = await db.Open(ct);
            await Exists(conn, id, http.Actor().AccountId, ct);
            return new { revision = number, fields = await LoadRevision(conn, id, number, ct) };
        });
        api.MapPost("/profiles/{id:guid}/snapshots", async (Guid id, Rename input, HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            var name = Wire.Label(input.Name);
            var snap = Guid.NewGuid();
            return await db.Write(async conn =>
            {
                await Accounts.LockSession(conn, actor, ct);
                var state = await Read(conn, id, actor, true, ct);
                RequireAdmission(state);
                return (await Rows(conn, """
                    INSERT INTO saved_snapshot(id,profile_id,name,revision,fields)
                    SELECT $2,p.id,$3,p.revision,p.fields FROM profile p WHERE p.id=$1
                    RETURNING created_at
                    """, r => new { id = snap, name, revision = state.Revision, createdAt = r.GetDateTime(0) }, ct, id, snap, name)).Single();
            }, ct);
        });
        api.MapGet("/profiles/{id:guid}/snapshots", async (Guid id, HttpContext http, Store db, CancellationToken ct) =>
        {
            await using var conn = await db.Open(ct);
            await Exists(conn, id, http.Actor().AccountId, ct);
            return await Rows(conn, "SELECT id,name,revision,created_at FROM saved_snapshot WHERE profile_id=$1 ORDER BY created_at DESC",
                r => new { id = r.GetGuid(0), name = r.GetString(1), revision = r.GetInt64(2), createdAt = r.GetDateTime(3) }, ct, id);
        });
        api.MapDelete("/profiles/{id:guid}/snapshots/{snapshot:guid}", async (Guid id, Guid snapshot, HttpContext http, Store db, CancellationToken ct) =>
        {
            var actor = http.Actor();
            await db.Write(async conn =>
            {
                await Accounts.LockSession(conn, actor, ct);
                RequireAdmission(await Read(conn, id, actor, true, ct));
                if (await Exec(conn, "DELETE FROM saved_snapshot WHERE profile_id=$1 AND id=$2", ct, id, snapshot) == 0)
                    throw ApiError.Missing();
                return 0;
            }, ct);
            return Results.NoContent();
        });
        api.MapPost("/profiles/{id:guid}/snapshots/{snapshot:guid}/restore", async (Guid id, Guid snapshot, OperationRequest input, HttpContext http, Store db, CancellationToken ct) =>
            await Mutate(db, id, http.Actor(), input.OperationId, "snapshot-restore", snapshot, async (conn, state) =>
            {
                RequireAdmission(state);
                var records = await Rows(conn, "SELECT fields::text FROM saved_snapshot WHERE profile_id=$1 AND id=$2",
                    r => Wire.Decode<Dictionary<string, Field>>(r.GetString(0)), ct, id, snapshot);
                return Restore(state, records.SingleOrDefault() ?? throw ApiError.Missing(), full: true);
            }, ct));
        api.MapGet("/profiles/{id:guid}/trash", async (Guid id, HttpContext http, Store db, CancellationToken ct) =>
        {
            await using var conn = await db.Open(ct);
            await Exists(conn, id, http.Actor().AccountId, ct);
            return await Rows(conn, """
                SELECT id,name,created_at FROM trash WHERE profile_id=$1 AND created_at>=now()-interval '30 days' ORDER BY created_at DESC
                """, r => new { id = r.GetGuid(0), name = r.GetString(1), createdAt = r.GetDateTime(2) }, ct, id);
        });
        api.MapPost("/profiles/{id:guid}/trash", async (Guid id, TrashRequest input, HttpContext http, Store db, CancellationToken ct) =>
        {
            var name = Wire.Label(input.Name);
            if (input.Keys is null || input.Keys.Count is 0 or > MaxFields) throw ApiError.Bad("invalid_keys");
            foreach (var key in input.Keys) Key(key);
            return await Mutate(db, id, http.Actor(), input.OperationId, "trash", input, async (conn, state) =>
            {
                RequireAdmission(state);
                var selected = ExpandTrash(state.Fields, input.Keys);
                var contents = state.Fields.Where(pair => selected.Contains(pair.Key) && !pair.Value.Deleted).ToDictionary();
                if (contents.Count == 0) throw ApiError.Missing();
                var next = state.Revision + 1;
                await Exec(conn, "INSERT INTO trash(id,profile_id,name,deleted_revision,fields) VALUES ($1,$2,$3,$4,$5::jsonb)",
                    ct, input.OperationId, id, name, next, Wire.Encode(contents));
                foreach (var key in contents.Keys) state.Fields[key] = new(null, next, true, next);
                return new Mutation(next, contents.Keys.ToList(), []);
            }, ct);
        });
        api.MapPost("/profiles/{id:guid}/trash/{item:guid}/restore", async (Guid id, Guid item, OperationRequest input, HttpContext http, Store db, CancellationToken ct) =>
            await Mutate(db, id, http.Actor(), input.OperationId, "trash-restore", item, async (conn, state) =>
            {
                RequireAdmission(state);
                var rows = await Rows(conn, """
                    SELECT fields::text,deleted_revision FROM trash WHERE id=$1 AND profile_id=$2 AND created_at>=now()-interval '30 days'
                    """, r => (fields: Wire.Decode<Dictionary<string, Field>>(r.GetString(0)), revision: r.GetInt64(1)), ct, item, id);
                if (rows.Count == 0) throw ApiError.Missing();
                var saved = rows[0];
                if (saved.fields.Keys.Any(key => !state.Fields.TryGetValue(key, out var f) || f.Revision != saved.revision))
                    throw ApiError.Conflict("trash_target_changed");
                await Exec(conn, "DELETE FROM trash WHERE id=$1 AND profile_id=$2", ct, item, id);
                return Restore(state, saved.fields, full: false);
            }, ct));
    }
}
