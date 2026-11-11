using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Synvaldi;

/// <summary>Reads a cold, Vivaldi-owned v3 session. Refuses locked, incomplete and unknown formats.</summary>
internal static class VivaldiMetadata
{
    private static readonly string[] TabFields = ["ext_id", "workspaceId", "group", "fixedGroupTitle", "groupColor", "fixedTitle", "tiling"];
    private static readonly string[] WindowFields = ["ext_id", "workspaceId"];

    internal static JsonObject Read(string profileDirectory)
    {
        var prefs = Path.Combine(profileDirectory, "Preferences");
        var directory = Path.Combine(profileDirectory, "Sessions");
        var latest = Directory.EnumerateFiles(directory, "Session_*")
            .Where(p => long.TryParse(Path.GetFileName(p).AsSpan(8), out _))
            .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidDataException("Файл сессии Vivaldi отсутствует.");
        // Use the newest generation only. Older unlocked files can refer to a
        // completely different set of tabs, even when the current file is busy.
        using var input = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(input, Encoding.UTF8, true);
        if (input.Length < 8 || input.Length > 256L * 1024 * 1024 ||
            !reader.ReadBytes(4).AsSpan().SequenceEqual("SNSS"u8) || reader.ReadInt32() != 3)
            throw new InvalidDataException("Неизвестный формат сессии Vivaldi.");
        var tabs = new JsonObject();
        var windows = new JsonObject();
        while (input.Position < input.Length)
        {
            var length = reader.ReadUInt16();
            if (length == 0 || length > input.Length - input.Position)
                throw new InvalidDataException("Незавершённая запись сессии Vivaldi.");
            var kind = reader.ReadByte();
            if (kind is not (21 or 22))
            {
                input.Seek(length - 1, SeekOrigin.Current);
                continue;
            }
            var bytes = reader.ReadBytes(length - 1);
            if (bytes.Length < 12) continue;
            var dataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
            if (dataLength <= 0 || dataLength > bytes.Length - 12) continue;
            var id = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)).ToString(CultureInfo.InvariantCulture);
            if (bytes[12] != (byte)'{') continue;
            var source = JsonNode.Parse(bytes.AsSpan(12, dataLength)) as JsonObject;
            if (source == null) continue;
            var allowed = new JsonObject();
            foreach (var key in kind == 21 ? TabFields : WindowFields)
                if (source[key] is { } node) allowed[key] = node.DeepClone();
            (kind == 21 ? tabs : windows)[id] = allowed;
        }

        using var prefStream = new FileStream(prefs, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (prefStream.Length > 64L * 1024 * 1024) throw new InvalidDataException("Файл Preferences слишком большой.");
        var root = JsonNode.Parse(prefStream);
        var workspaces = new JsonArray();
        foreach (var entry in root?["vivaldi"]?["workspaces"]?["list"] as JsonArray ?? [])
        {
            if (entry?["id"] is not { } id || entry["name"] is not { } name) continue;
            var item = new JsonObject { ["id"] = id.ToString(), ["name"] = name.DeepClone() };
            foreach (var key in new[]{"icon","emoji"})
                if (entry[key] != null) item[key] = entry[key]!.DeepClone();
            workspaces.Add(item);
        }
        return new JsonObject { ["type"]="metadata", ["live"]=false, ["workspaces"]=workspaces, ["tabs"]=tabs, ["windows"]=windows };
    }
}
