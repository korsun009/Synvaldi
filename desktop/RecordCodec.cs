namespace Synvaldi;

internal static class RecordCodec
{
    // A presence field marks the record lifetime. Properties carry independent base revisions.
    public static Dictionary<string,JsonNode?> Flatten(JsonArray records)
    {
        var result = new Dictionary<string,JsonNode?>();
        foreach (var item in records.OfType<JsonObject>())
        {
            string kind = item["kind"]?.ToString() ?? "", id = item["id"]?.ToString() ?? "";
            if (kind is not ("tab" or "workspace") || !Guid.TryParse(id, out _))
                throw new InvalidDataException("Неизвестный тип записи или идентификатор.");
            var prefix = kind + "/" + id + "/";
            if (item["deleted"]?.GetValue<bool>() == true) { result[prefix + "exists"] = null; continue; }
            result[prefix + "exists"] = true;
            if (item["value"] is not JsonObject value) throw new InvalidDataException("Отсутствует значение записи.");
            foreach (var pair in value)
                if (pair.Key.Length < 80 && pair.Key != "exists" && !pair.Key.Contains('/'))
                    result[prefix + pair.Key] = pair.Value?.DeepClone();
        }
        return result;
    }
    public static JsonObject Seed(JsonArray records)
    {
        var result = new JsonObject();
        foreach (var field in Flatten(records)) if (field.Value != null) result[field.Key] = field.Value;
        return result;
    }
    public static JsonArray Changes(JsonArray records, JsonObject state)
    {
        var result = new JsonArray();
        foreach (var field in Flatten(records))
        {
            var before = state["fields"]?[field.Key];
            var deleted = before == null || before["deleted"]?.GetValue<bool>() == true;
            if (field.Value == null ? deleted : !deleted && JsonNode.DeepEquals(before?["value"], field.Value)) continue;
            result.Add(new JsonObject { ["key"] = field.Key, ["value"] = field.Value?.DeepClone(), ["deleted"] = field.Value == null,
                ["baseRevision"] = before?["revision"]?.GetValue<long>() ?? 0L,
                ["group"] = field.Key.EndsWith("/index") || field.Key.EndsWith("/windowId") || field.Key.EndsWith("/workspaceId") ? field.Key[..field.Key.LastIndexOf('/')] + "/move" : null });
        }
        return result;
    }
    public static JsonArray Records(JsonObject state)
    {
        var data = new Dictionary<string,JsonObject>();
        var live = new HashSet<string>();
        foreach (var field in (state["fields"] as JsonObject ?? new JsonObject()))
        {
            var parts = field.Key.Split('/');
            if (parts.Length != 3 || parts[0] is not ("workspace" or "tab") || field.Value?["deleted"]?.GetValue<bool>() == true) continue;
            string key = parts[0] + "/" + parts[1];
            if (!data.TryGetValue(key,out var obj))
                data[key] = obj = new JsonObject { ["kind"] = parts[0], ["id"] = parts[1], ["value"] = new JsonObject() };
            if (parts[2] == "exists") { live.Add(key); continue; }
            obj["value"]![parts[2]] = field.Value?["value"]?.DeepClone();
        }
        var result=data.Where(x=>live.Contains(x.Key)).Select(x=>x.Value).ToArray();
        foreach(var group in result.Where(x=>x["kind"]?.ToString()=="tab").GroupBy(x=>x["value"]?["windowId"]?.ToString()??""))
        {
            var ordered=group.OrderByDescending(x=>x["value"]?["pinned"]?.GetValue<bool>()??false)
                .ThenBy(x=>x["value"]?["index"]?.GetValue<int>()??int.MaxValue)
                .ThenBy(x=>x["id"]?.ToString(),StringComparer.Ordinal);
            int index=0;
            foreach(var item in ordered)item["value"]!["index"]=index++;
        }
        return new JsonArray(result.Select(x=>(JsonNode)x).ToArray());
    }
    public static JsonArray Delta(JsonArray from, JsonArray to)
    {
        string Key(JsonNode obj) => obj["kind"] + "/" + obj["id"];
        var prior = from.OfType<JsonObject>().ToDictionary(Key);
        var result = new JsonArray();
        foreach (var current in to.OfType<JsonObject>())
        {
            if (!prior.Remove(Key(current), out var old) || !JsonNode.DeepEquals(old,current)) result.Add(current.DeepClone());
        }
        foreach (var old in prior.Values) result.Add(new JsonObject { ["kind"] = old["kind"]?.DeepClone(), ["id"] = old["id"]?.DeepClone(), ["deleted"] = true });
        return result;
    }
}
