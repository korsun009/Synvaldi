using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Synvaldi;

/// <summary>Versioned Vivaldi UI preferences. All incoming property names and local file destinations are bounded.</summary>
public static class PreferencesAdapter
{
    const int MaxFile = 32 * 1024 * 1024;
    const string ImagePrefix = "chrome://vivaldi-data/local-image/";
    static readonly HashSet<string> Appearance = new(StringComparer.Ordinal)
        { "themes","theme","toolbars","panel_editor","panels","startpage","dashboard","layouts","menu","appearance","status_bar","auto_hide" };
    static readonly HashSet<string> Settings = new(StringComparer.Ordinal)
        { "actions","chained_commands","keyboard","mouse_gestures","address_bar","tabs","quick_commands","bookmarks","reading_list" };

    static bool Allowed(string key,string[] categories) =>
        (categories.Contains("appearance") && Appearance.Contains(key)) || (categories.Contains("settings") && Settings.Contains(key));

    static JsonObject Read(string file) => JsonNode.Parse(File.ReadAllBytes(file))?.AsObject() ?? throw new InvalidDataException("РќРµРІРµСЂРЅС‹Р№ С„РѕСЂРјР°С‚ РЅР°СЃС‚СЂРѕРµРє Vivaldi.");
    static IEnumerable<string> Strings(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var s)) yield return s;
        else if (value is JsonArray array)
            foreach(var item in array) foreach(var text in Strings(item)) yield return text;
        else if (value is JsonObject obj)
            foreach(var item in obj) foreach(var text in Strings(item.Value)) yield return text;
    }
    static bool ValidImageId(string id) => Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,120}(?:\.[A-Za-z0-9]{1,8})?\z");
    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    internal static string? Extension(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.StartsWith(new byte[]{137,80,78,71,13,10,26,10})) return ".png";
        if (data.StartsWith(new byte[]{255,216,255})) return ".jpg";
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }
    static IEnumerable<string> ImageIds(JsonNode data) => Strings(data)
        .Where(s => s.StartsWith(ImagePrefix,StringComparison.Ordinal)).Select(s=>s[ImagePrefix.Length..]).Where(ValidImageId).Distinct();

    public static async Task<JsonObject> Capture(string profilePath,string[] enabledCategories,Func<string,byte[],Task> upload)
    {
        var prefs = Read(Path.Combine(profilePath,"Preferences"));
        var fields = new JsonObject();
        if(enabledCategories.Contains("appearance")) await SpeedDialImages.Capture(profilePath,fields,upload);
        if(enabledCategories.Contains("attachments")) await NoteAttachments.Capture(profilePath,fields,upload);
        if(prefs["vivaldi"] is not JsonObject vivaldi) return fields;
        var owned = NativeSyncKeys(prefs);
        void Visit(string path,JsonNode? value)
        {
            if(owned.Contains("vivaldi."+path.Replace('/','.'))) return;
            if(value is JsonObject group && group.Count > 0)
            {
                foreach(var child in group)
                    if(!child.Key.Contains('/')) Visit(path+"/"+child.Key,child.Value);
            }
            else fields["pref/"+path] = value?.DeepClone();
        }
        foreach(var section in vivaldi.Where(kv=>Allowed(kv.Key,enabledCategories)))
            Visit(section.Key,section.Value);
        var mapFile = Path.Combine(profilePath,"file_mapping.json");
        if(!File.Exists(mapFile)) return fields;
        var mappings = Read(mapFile)["mappings"];
        foreach(string id in ImageIds(fields).ToArray())
        {
            string? path = mappings?[id]?["local_path"]?.GetValue<string>();
            if(path == null || !Path.IsPathFullyQualified(path) || !File.Exists(path)) continue;
            if(new FileInfo(path).Length > MaxFile) throw new InvalidDataException("Р¤РѕРЅ РїСЂРµРІС‹С€Р°РµС‚ Р»РёРјРёС‚ 32 РњРёР‘.");
            byte[] image = await File.ReadAllBytesAsync(path);
            if(Extension(image) is null) continue;
            var digest = Hash(image);
            await upload(digest,image);
            fields["asset/"+id] = digest;
        }
        return fields;
    }

    static HashSet<string> NativeSyncKeys(JsonObject prefs)
    {
        if(prefs["sync"]?["has_setup_completed"]?.GetValue<bool>() != true ||
            (prefs["sync"]?["keep_everything_synced"]?.GetValue<bool>() != true &&
             prefs["sync"]?["preferences"]?.GetValue<bool>() != true)) return [];
        var app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vivaldi","Application");
        var binary = Path.Combine(app,"vivaldi.exe");
        string? version = File.Exists(binary) ? FileVersionInfo.GetVersionInfo(binary).ProductVersion : null;
        var file = Path.Combine(app,version ?? "", "resources","vivaldi","prefs_definitions.json");
        if(!File.Exists(file)) throw new IOException("Для этой версии Vivaldi пока неизвестен перечень настроек штатного Sync.");
        return (Read(file)["syncable"] as JsonObject ?? throw new InvalidDataException("Схема Sync изменилась."))
            .Select(k=>k.Key).ToHashSet(StringComparer.Ordinal);
    }

    internal static void Write(string target,JsonObject data)
    {
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp,data.ToJsonString());
            if(File.Exists(target)) File.Replace(temp,target,target+".synvaldi.bak");
            else File.Move(temp,target);
        }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr CommandLineToArgvW(string command,out int count);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr data);
    static string[] Arguments(string command)
    {
        var buffer=CommandLineToArgvW(command,out int count);
        if(buffer==IntPtr.Zero) throw new IOException("Unable to verify browser profile lock.");
        try
        {
            var result=new string[count];
            for(int i=0;i<count;i++)result[i]=System.Runtime.InteropServices.Marshal.PtrToStringUni(
                System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer,i*IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(buffer); }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool ProfileInUse(string profilePath)
    {
        string parent=Path.GetFullPath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profilePath))!);
        using var search=new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='vivaldi.exe'");
        using var processes=search.Get();
        foreach(System.Management.ManagementObject p in processes)
        {
            using(p)
            {
                string? cmd=p["CommandLine"] as string;
                if(string.IsNullOrEmpty(cmd)) return true;
                var args=Arguments(cmd);
                if(args.Any(arg=>arg.StartsWith("--type=",StringComparison.OrdinalIgnoreCase)))continue;
                const string prefix="--user-data-dir=";
                string? location=args.FirstOrDefault(arg=>arg.StartsWith(prefix,StringComparison.OrdinalIgnoreCase));
                var dir=location!=null?location[prefix.Length..]:
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vivaldi","User Data");
                if(string.Equals(Path.GetFullPath(dir),parent,StringComparison.OrdinalIgnoreCase))return true;
            }
        }
        return false;
    }
    static void CheckCold(string profilePath)
    {
        if(OperatingSystem.IsWindows() && ProfileInUse(profilePath))throw new IOException("Настройки ожидают закрытия Vivaldi.");
    }

    public static async Task Apply(string profilePath,string[] enabledCategories,JsonObject fields,string assetsRoot,Func<string,Task<byte[]>> download)
    {
        CheckCold(profilePath);
        string file = Path.Combine(profilePath,"Preferences"), mapFile = Path.Combine(profilePath,"file_mapping.json");
        var prefs = Read(file);
        var original = prefs.DeepClone();
        var vivaldi = (prefs["vivaldi"] ??= new JsonObject()).AsObject();
        var selected = new JsonObject();
        var owned = NativeSyncKeys(prefs);
        foreach(var kv in fields)
        {
            var path = kv.Key.Split('/');
            if(path.Length < 2 || path.Length > 12 || path[0] != "pref" || !Allowed(path[1],enabledCategories) ||
                path.Skip(1).Any(part => part.Length == 0 || part.Length > 150)) continue;
            string key = "vivaldi."+string.Join('.',path.Skip(1));
            if(owned.Any(k=>k==key || k.StartsWith(key+".",StringComparison.Ordinal) || key.StartsWith(k+".",StringComparison.Ordinal))) continue;
            var branch = vivaldi;
            foreach(var part in path.Skip(1).SkipLast(1))
                branch = (branch[part] ??= new JsonObject()).AsObject();
            if(kv.Value is null) branch.Remove(path[^1]); else branch[path[^1]] = kv.Value.DeepClone();
            selected[kv.Key] = kv.Value?.DeepClone();
        }
        var map = File.Exists(mapFile) ? Read(mapFile) : new JsonObject();
        var originalMap = map.DeepClone();
        foreach(string id in ImageIds(selected))
        {
            if(fields["asset/"+id]?.GetValue<string>() is not { } digest || !Regex.IsMatch(digest,@"\A[0-9a-f]{64}\z")) continue;
            byte[] data = await download(digest);
            string? ext = Extension(data);
            if(data.Length > MaxFile || Hash(data) != digest || ext is null) throw new InvalidDataException("Р¤Р°Р№Р» РѕС„РѕСЂРјР»РµРЅРёСЏ РїРѕРІСЂРµР¶РґС‘РЅ.");
            Directory.CreateDirectory(assetsRoot);
            string target = Path.GetFullPath(Path.Combine(assetsRoot,digest+ext));
            if(!File.Exists(target) || Hash(await File.ReadAllBytesAsync(target)) != digest)
            {
                string tmp = target + "." + Guid.NewGuid().ToString("N");
                try { await File.WriteAllBytesAsync(tmp,data); File.Move(tmp,target,true); }
                finally { if(File.Exists(tmp)) File.Delete(tmp); }
            }
            (map["mappings"] ??= new JsonObject())[id] = new JsonObject{["local_path"]=target};
        }
        CheckCold(profilePath);
        if(!JsonNode.DeepEquals(map,originalMap)) Write(mapFile,map);
        if(!JsonNode.DeepEquals(prefs,original)) Write(file,prefs);
        if(enabledCategories.Contains("appearance")) await SpeedDialImages.Apply(profilePath,fields,download,()=>CheckCold(profilePath));
        if(enabledCategories.Contains("attachments")) await NoteAttachments.Apply(profilePath,fields,assetsRoot,download,()=>CheckCold(profilePath));
    }
}
