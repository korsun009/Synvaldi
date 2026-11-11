using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using static Synvaldi.Store;

namespace Synvaldi;

internal static class ColdReconciliation
{
    internal const string Prefix = "workspace-cold/";
    private record Location(string Workspace, Guid Window);
    private record Tab(Guid Id, Location Place, int Index, JsonObject Row);
    private sealed class Image
    {
        public Dictionary<string, JsonObject> Workspaces { get; } = [];
        public Dictionary<Guid, Tab> Tabs { get; } = [];

        public bool Add(string key, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object || JsonNode.Parse(value.GetRawText()) is not JsonObject space ||
                space["id"]?.ToString() != key[Prefix.Length..] || space["tabs"] is not JsonArray tabs ||
                tabs.Count > 10000 || !Workspaces.TryAdd(key, space)) return false;
            foreach (var node in tabs)
            {
                if (node is not JsonObject row || !Guid.TryParse(row["id"]?.ToString(), out var id) || id == Guid.Empty ||
                    !Guid.TryParse(row["windowId"]?.ToString(), out var win) || win == Guid.Empty ||
                    row["index"] is not JsonValue ordinal || !ordinal.TryGetValue<int>(out var index) || index < 0 ||
                    !Tabs.TryAdd(id, new Tab(id, new Location(key, win), index, row))) return false;
            }
            return true;
        }

        public Dictionary<Location, List<Guid>> Orders() => Tabs.Values.GroupBy(t => t.Place)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Index).ThenBy(t => t.Id).Select(t => t.Id).ToList());
    }

    // A field's last write may be much older than the snapshot the client downloaded.
    // The first retained undo image that actually contains its exact field revision is sufficient.
    private static async Task<Field?> Baseline(NpgsqlConnection conn, Guid profile, Change edit, CancellationToken ct)
    {
        var rows = await Rows(conn, """
            SELECT image::text FROM (
                SELECT r.previous_fields -> $3::text AS image
                FROM revision r WHERE r.profile_id=$1 AND r.number>$2 AND r.previous_fields ? $3::text
                    AND r.created_at>=now()-interval '72 hours'
                UNION ALL
                SELECT r.fields -> $3::text FROM revision r WHERE r.profile_id=$1 AND r.number=$2 AND r.checkpoint
                    AND r.created_at>=now()-interval '72 hours'
            ) candidates WHERE (image ->> 'revision')::bigint=$2 LIMIT 1
            """, r => Wire.Decode<Field>(r.GetString(0)), ct, profile, edit.BaseRevision, edit.Key);
        return rows.FirstOrDefault();
    }

    public static async Task<(Dictionary<string, JsonElement> Values, HashSet<string> Conflicts)> Merge(
        NpgsqlConnection conn, Guid profile, Dictionary<string, Field> state, Change[] edits, CancellationToken ct)
    {
        var requested = edits.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var basis = new Image();
        var local = new Image();
        var remote = new Image();
        // Include current unselected spaces for identity checks; they never become automatic write targets.
        foreach (var (key, field) in state)
            if (key.StartsWith(Prefix, StringComparison.Ordinal) && !field.Deleted &&
                (field.Value is not JsonElement value || !remote.Add(key, value))) return ([], requested);

        foreach (var edit in edits)
        {
            if (edit.Value is not JsonElement proposed || !local.Add(edit.Key, proposed)) return ([], requested);
            state.TryGetValue(edit.Key, out var head);
            if (head is null)
            {
                if (edit.BaseRevision != 0) return ([], requested);
                continue;
            }
            if (head.Deleted || edit.BaseRevision == 0 || edit.BaseRevision > head.Revision ||
                edit.BaseRevision < head.GuardRevision) return ([], requested);
            var original = edit.BaseRevision == head.Revision ? head : await Baseline(conn, profile, edit, ct);
            if (original?.Value is not JsonElement value || original.Deleted || !basis.Add(edit.Key, value))
                return ([], requested);
        }

        var conflicts = new HashSet<string>();
        var spaces = new Dictionary<string, JsonObject>();
        foreach (var key in requested)
        {
            var incoming = local.Workspaces[key];
            var target = remote.Workspaces.TryGetValue(key, out var current) ? current.DeepClone().AsObject() : incoming.DeepClone().AsObject();
            if (basis.Workspaces.TryGetValue(key, out var original))
                MergeProperties(original, incoming, target, ["id", "tabs"], () => conflicts.Add(key));
            target["tabs"] = new JsonArray();
            spaces.Add(key, target);
        }

        var tabs = new Dictionary<Guid, Tab>(remote.Tabs);
        var moves = basis.Tabs.Values.Where(before => local.Tabs.TryGetValue(before.Id, out var after) && before.Place != after.Place).ToArray();
        // A move proposed in this capture may span several tabs and windows. Keep its two ends together.
        bool blockMoves = moves.Any(before => !remote.Tabs.TryGetValue(before.Id, out var now) ||
            (now.Place != before.Place && now.Place != local.Tabs[before.Id].Place));
        var stackConflicts = ContestedLayouts(basis, local, remote, requested, "group");
        var tileConflicts = ContestedLayouts(basis, local, remote, requested, "tiling");
        foreach (var id in basis.Tabs.Keys.Union(local.Tabs.Keys))
        {
            basis.Tabs.TryGetValue(id, out var before);
            local.Tabs.TryGetValue(id, out var after);
            remote.Tabs.TryGetValue(id, out var now);
            if (before is null)
            {
                if (now is null)
                {
                    var added = after!;
                    var fresh = added.Row.DeepClone().AsObject();
                    foreach (var key in SuppressedLayoutFields(null, added, stackConflicts, tileConflicts))
                    {
                        fresh.Remove(key);
                        conflicts.Add(added.Place.Workspace);
                    }
                    tabs[id] = added with { Row = fresh };
                }
                else if (now.Place != after!.Place || !JsonNode.DeepEquals(now.Row, after.Row)) conflicts.Add(after.Place.Workspace);
                continue;
            }
            if (now is null)
            {
                if (after is not null && !JsonNode.DeepEquals(before.Row, after.Row)) conflicts.Add(after.Place.Workspace);
                continue;
            }
            if (after is null)
            {
                if (now.Place == before.Place) tabs.Remove(id);
                else conflicts.Add(before.Place.Workspace);
                continue;
            }
            if (!requested.Contains(now.Place.Workspace))
            {
                if (!JsonNode.DeepEquals(before.Row, after.Row) || before.Place != after.Place) conflicts.Add(after.Place.Workspace);
                continue;
            }
            var place = now.Place;
            if (after.Place != before.Place)
            {
                if (!blockMoves) place = after.Place;
                else
                {
                    conflicts.Add(before.Place.Workspace);
                    conflicts.Add(after.Place.Workspace);
                }
            }
            var row = now.Row.DeepClone().AsObject();
            var suppressed = SuppressedLayoutFields(before, after, stackConflicts, tileConflicts);
            if (suppressed.Count != 0) conflicts.Add(after.Place.Workspace);
            MergeProperties(before.Row, after.Row, row, ["id", "index", "windowId", .. suppressed], () => conflicts.Add(after.Place.Workspace));
            tabs[id] = new Tab(id, place, now.Index, row);
        }

        var baseOrder = basis.Orders();
        var localOrder = local.Orders();
        var remoteOrder = remote.Orders();
        foreach (var lane in tabs.Values.Where(t => requested.Contains(t.Place.Workspace)).GroupBy(t => t.Place)
                     .OrderBy(g => g.Key.Window.ToString(), StringComparer.Ordinal))
        {
            var place = lane.Key;
            var survivors = lane.Select(t => t.Id).ToHashSet();
            var original = baseOrder.GetValueOrDefault(place) ?? [];
            var incoming = localOrder.GetValueOrDefault(place) ?? [];
            var current = remoteOrder.GetValueOrDefault(place) ?? [];
            var common = original.Intersect(incoming).Intersect(current).Where(survivors.Contains).ToHashSet();
            var oldCore = original.Where(common.Contains).ToArray();
            var newCore = incoming.Where(common.Contains).ToArray();
            var remoteCore = current.Where(common.Contains).ToArray();
            var localReorder = !oldCore.SequenceEqual(newCore);
            var remoteReorder = !oldCore.SequenceEqual(remoteCore);
            if (localReorder && remoteReorder && !remoteCore.SequenceEqual(newCore)) conflicts.Add(place.Workspace);
            bool preferLocal = localReorder && !remoteReorder;
            var order = Weave(preferLocal ? incoming : current, preferLocal ? current : incoming, survivors);
            var rows = spaces[place.Workspace]["tabs"]!.AsArray();
            int index = 0;
            foreach (var id in order)
            {
                var row = tabs[id].Row.DeepClone().AsObject();
                row["windowId"] = place.Window.ToString();
                row["index"] = index++;
                rows.Add(row);
            }
        }
        if (spaces.Values.Any(s => s["tabs"]!.AsArray().Count > 10000)) return ([], requested);
        return (spaces.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)), conflicts);
    }

    private static string? LayoutId(Tab? tab, string key)
    {
        var value = tab?.Row[key];
        return key == "tiling" ? (value as JsonObject)?["id"]?.ToString() : value?.ToString();
    }

    private static HashSet<string> ContestedLayouts(Image basis, Image local, Image remote, HashSet<string> requested, string key)
    {
        // Group/tile membership is composite. If two editors change the same layout in incompatible
        // ways, keep its cloud membership rather than writing half of a rearrangement.
        Dictionary<string, JsonObject> Groups(Image image)
        {
            var groups = new Dictionary<string, JsonObject>();
            foreach (var tab in image.Tabs.Values.Where(t => requested.Contains(t.Place.Workspace)))
            {
                if (LayoutId(tab, key) is not string id) continue;
                if (!groups.TryGetValue(id, out var group)) groups[id] = group = new JsonObject();
                group[tab.Id.ToString()] = tab.Row[key]?.DeepClone();
            }
            return groups;
        }
        var old = Groups(basis);
        var incoming = Groups(local);
        var current = Groups(remote);
        var contested = new HashSet<string>();
        foreach (var group in old.Keys.Union(incoming.Keys))
        {
            var a = old.GetValueOrDefault(group);
            var b = incoming.GetValueOrDefault(group);
            var c = current.GetValueOrDefault(group);
            if (!JsonNode.DeepEquals(a, b) && !JsonNode.DeepEquals(a, c) && !JsonNode.DeepEquals(b, c))
                contested.Add(group);
        }
        // A newly proposed layout cannot use a tab that the other PC has closed.
        foreach (var tab in local.Tabs.Values)
        {
            if (!basis.Tabs.TryGetValue(tab.Id, out var prior)) continue;
            var hasNow = remote.Tabs.TryGetValue(tab.Id, out var now);
            if (hasNow && (JsonNode.DeepEquals(prior.Row[key], tab.Row[key]) ||
                JsonNode.DeepEquals(prior.Row[key], now!.Row[key]) || JsonNode.DeepEquals(tab.Row[key], now.Row[key]))) continue;
            if (LayoutId(tab, key) is string proposed) contested.Add(proposed);
            if (LayoutId(prior, key) is string oldGroup) contested.Add(oldGroup);
        }
        // A transition joining two layouts couples their membership. Expand along that graph.
        var edges = new Dictionary<string, List<string>>();
        foreach (var tab in local.Tabs.Values)
        {
            var source = LayoutId(basis.Tabs.GetValueOrDefault(tab.Id), key);
            var target = LayoutId(tab, key);
            if (source is null || target is null || source == target) continue;
            if (!edges.TryGetValue(source, out var forward)) edges[source] = forward = [];
            if (!edges.TryGetValue(target, out var reverse)) edges[target] = reverse = [];
            forward.Add(target); reverse.Add(source);
        }
        var queue = new Queue<string>(contested);
        while (queue.TryDequeue(out var group))
            if (edges.TryGetValue(group, out var linked))
                foreach (var other in linked) if (contested.Add(other)) queue.Enqueue(other);
        return contested;
    }

    private static List<string> SuppressedLayoutFields(Tab? before, Tab after, HashSet<string> stacks, HashSet<string> tiles)
    {
        List<string> blocked = [];
        bool Hits(string key, HashSet<string> ids) =>
            !JsonNode.DeepEquals(before?.Row[key], after.Row[key]) &&
            (LayoutId(before, key) is string old && ids.Contains(old) || LayoutId(after, key) is string next && ids.Contains(next));
        if (Hits("group", stacks)) blocked.AddRange(["group", "groupColor", "fixedGroupTitle"]);
        if (Hits("tiling", tiles)) blocked.Add("tiling");
        return blocked;
    }

    // Retain the winning sequence and insert other surviving additions beside the nearest following stable anchor.
    private static IEnumerable<Guid> Weave(List<Guid> preferred, List<Guid> secondary, HashSet<Guid> survivors)
    {
        var first = preferred.Where(survivors.Contains).ToHashSet();
        var waiting = new List<Guid>();
        var emitted = new HashSet<Guid>();
        var before = new Dictionary<Guid, List<Guid>>();
        foreach (var id in secondary.Where(survivors.Contains))
        {
            if (!first.Contains(id)) { waiting.Add(id); continue; }
            if (waiting.Count != 0) { before[id] = waiting; waiting = []; }
        }
        foreach (var id in preferred.Where(survivors.Contains))
        {
            if (before.TryGetValue(id, out var additions))
                foreach (var added in additions) if (emitted.Add(added)) yield return added;
            if (emitted.Add(id)) yield return id;
        }
        foreach (var id in waiting) if (emitted.Add(id)) yield return id;
        foreach (var id in survivors.Order()) if (emitted.Add(id)) yield return id;
    }

    private static void MergeProperties(JsonObject original, JsonObject local, JsonObject current, string[] skip, Action conflict)
    {
        foreach (var key in original.Select(p => p.Key).Union(local.Select(p => p.Key)))
        {
            if (skip.Contains(key, StringComparer.Ordinal)) continue;
            bool had = original.TryGetPropertyValue(key, out var old);
            bool has = local.TryGetPropertyValue(key, out var edited);
            if (had == has && JsonNode.DeepEquals(old, edited)) continue;
            bool present = current.TryGetPropertyValue(key, out var now);
            if (present == has && JsonNode.DeepEquals(now, edited)) continue;
            if (present != had || !JsonNode.DeepEquals(now, old)) { conflict(); continue; }
            if (has) current[key] = edited?.DeepClone(); else current.Remove(key);
        }
    }
}
