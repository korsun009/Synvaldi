using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Synvaldi;

/// <summary>Selected-workspace transfer for closed Vivaldi 8.3 profiles. Never sends raw session records.</summary>
public static class ColdWorkspaces
{
    const int Limit = 256 * 1024 * 1024;
    const string Prefix = "workspace-cold/";
    static readonly string[] MetaKeys = ["group", "fixedGroupTitle", "groupColor", "fixedTitle", "tiling"];

    sealed class Tab(int nativeId)
    {
        public int Id = nativeId, Window, Order, Selected;
        public bool Pinned;
        public JsonObject Meta = new();
        public SortedDictionary<int,string> Nav = new();
        public string? Url => Nav.GetValueOrDefault(Selected);
        public string? Workspace => Number(Meta["workspaceId"]);
    }
    sealed class Image
    {
        public Dictionary<int,Tab> Tabs = new();
        public Dictionary<int,JsonObject> Windows = new();
        public HashSet<int> ClosedWindows = new();
        public Tab Item(int id) => Tabs.TryGetValue(id,out var tab) ? tab : Tabs[id] = new Tab(id);
    }
    static string? Number(JsonNode? value) =>
        double.TryParse(value?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var n) && double.IsFinite(n) && n > 0
        ? n.ToString("R",CultureInfo.InvariantCulture) : null;
    static string? Uuid(JsonNode? value) => Guid.TryParse(value?.ToString(),out var id) && id!=Guid.Empty ? id.ToString() : null;
    static bool Web(string? url) => url?.Length <= 32768 && Uri.TryCreate(url,UriKind.Absolute,out var u) &&
        (u.Scheme == "http" || u.Scheme == "https") && u.UserInfo.Length == 0;
    static string? Text(JsonNode? node,int max) => node is JsonValue v && v.TryGetValue<string>(out var text) && text.Length <= max ? text : null;
    static int Int(byte[] b, int pos) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos,4));
    static string Str(byte[] data, int start)
    {
        int len = Int(data,start);
        if (len < 0 || len > data.Length-start-4) throw new InvalidDataException("Повреждена строка сессии.");
        return new UTF8Encoding(false,true).GetString(data,start+4,len);
    }
    static string Latest(string profile) =>
        Directory.EnumerateFiles(Path.Combine(profile,"Sessions"),"Session_*")
          .Where(p=>long.TryParse(Path.GetFileName(p).AsSpan(8),out _))
          .OrderByDescending(p=>Path.GetFileName(p),StringComparer.Ordinal).FirstOrDefault()
          ?? throw new IOException("В профиле нет сохранённой сессии.");
    static readonly Dictionary<string,string> ResourceHashes = new()
    {
        ["bundle.js"]="F1693B9D74487BF7B59BBCB20DB75BF9347CB327CCDF16BEDE9B4B0845A728E2",
        ["background-common-bundle.js"]="720555C25A7935AD09A557068E79353780C9D7D1003CE5DA360276300D2D2499",
        ["prefs_definitions.json"]="3933E9110CCB4222971D0681875FEBEFAB706B6E99AFAF069C3EEDDE8F44779E"
    };
    internal static bool CompatiblePatch(string? reported,string? engine,string resources)
    {
        if(!Version.TryParse(reported,out var version) || version.Major!=8 || version.Minor!=3 || version.Build!=4191 ||
           !Version.TryParse(engine,out var chromium) || chromium.Major!=156 || chromium.Minor!=0 || chromium.Build!=8078)
            return false;
        foreach(var field in ResourceHashes)
        {
            string file=Path.Combine(resources,field.Key);
            if(!File.Exists(file) || HashFile(file)!=field.Value) return false;
        }
        return true;
    }
    static void CheckCompatibility(string profile,JsonObject prefs,bool writing)
    {
        string? reported=prefs["vivaldi"]?["startup"]?["last_seen_version"]?.ToString();
        var appRoots=new[]{Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86}
            .Select(folder=>Path.Combine(Environment.GetFolderPath(folder),"Vivaldi","Application")).Where(Directory.Exists).ToArray();
        var engineFile=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profile))!,"Last Version");
        string? engine=File.Exists(engineFile) ? File.ReadAllText(engineFile).Trim() : null;
        bool Supported(string? value,bool write)
        {
            if(value=="8.3.4191.35" || (!write && value=="8.3.4191.30"))return true;
            // Only a parsed four-component version can contribute a path segment.
            return Version.TryParse(value,out var v) && v.ToString()==value && appRoots.Any(root=>
                CompatiblePatch(value,engine,Path.Combine(root,value!,"resources","vivaldi")));
        }
        if(!Supported(reported,writing))
            throw new InvalidDataException("Ожидается совместимый адаптер рабочих пространств для Vivaldi "+reported+". Обычные вкладки доступны.");
        // An updater may already have replaced the browser that will next read the profile.
        if(writing && appRoots.FirstOrDefault(root=>File.Exists(Path.Combine(root,"vivaldi.exe"))) is {} actualRoot)
        {
            var installed=System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(actualRoot,"vivaldi.exe")).ProductVersion;
            if(!Supported(installed,true))throw new InvalidDataException("Новый установленный Vivaldi ожидает совместимый адаптер пространств.");
        }
    }
    static JsonObject Prefs(string profile)
    {
        string file=Path.Combine(profile,"Preferences");
        if (new FileInfo(file).Length > 64*1024*1024) throw new InvalidDataException("Слишком большой файл Preferences.");
        var data=JsonNode.Parse(File.ReadAllBytes(file))?.AsObject() ?? throw new InvalidDataException();
        CheckCompatibility(profile,data,false);
        return data;
    }
    static Image Parse(Stream stream)
    {
        if (stream.Length < 8 || stream.Length > Limit) throw new InvalidDataException("Недопустимый размер сессии.");
        using var r=new BinaryReader(stream,Encoding.UTF8,true);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual("SNSS"u8) || r.ReadInt32()!=3) throw new InvalidDataException("Неизвестный формат SNSS.");
        var state=new Image();
        while (stream.Position<stream.Length)
        {
            int length=r.ReadUInt16();
            if (length<1 || length>stream.Length-stream.Position) throw new InvalidDataException("Незавершённая сессия.");
            int kind=r.ReadByte(); var b=r.ReadBytes(length-1);
            switch(kind)
            {
                case 0: state.Item(Int(b,4)).Window=Int(b,0);break;
                case 2: state.Item(Int(b,0)).Order=Int(b,4);break;
                case 6: state.Item(Int(b,4)).Nav[Int(b,8)]=Str(b,12);break;
                case 7: state.Item(Int(b,0)).Selected=Int(b,4);break;
                case 9: state.Windows.TryAdd(Int(b,0),new JsonObject());break;
                case 12: state.Item(Int(b,0)).Pinned=b[4]!=0;break;
                case 16: state.Tabs.Remove(Int(b,0));break;
                case 17: state.ClosedWindows.Add(Int(b,0));break;
                case 21:case 22:
                    if (b.Length<13 || b[12]!='{') break;
                    var meta=JsonNode.Parse(Str(b,8))?.AsObject() ?? throw new InvalidDataException();
                    if (kind==21) state.Item(Int(b,4)).Meta=meta; else state.Windows[Int(b,4)]=meta;
                    break;
                case 5: case 11: case 24:
                    // An incomplete interpretation of history pruning could export a stale URL.
                    // Skip this generation unless its final compacted journal has unambiguous navigation.
                    throw new InvalidDataException("Сессия содержит сокращение истории навигации. Запусти и штатно закрой профиль для нового снимка.");
            }
        }
        foreach (int win in state.ClosedWindows) state.Windows.Remove(win);
        foreach (var t in state.Tabs.Values.Where(t=>!state.Windows.ContainsKey(t.Window)).ToList()) state.Tabs.Remove(t.Id);
        return state;
    }
    static JsonObject Decorations(JsonObject meta)
    {
        var result=new JsonObject();
        foreach (var key in new[]{"group","groupColor","fixedGroupTitle","fixedTitle"})
            if (Text(meta[key],512) is {} s) result[key]=s;
        if (meta["tiling"] is JsonObject tile && Text(tile["id"],128) is {} tileId &&
            tile["index"] is JsonValue index && index.TryGetValue<int>(out var n) && n is >=0 and <10000 &&
            Text(tile["layout"],32) is {} layout && Text(tile["type"],32) is {} type)
            result["tiling"]=new JsonObject{["id"]=tileId,["index"]=n,["layout"]=layout,["type"]=type};
        return result;
    }
    static JsonObject Export(JsonObject prefs,Image image,string[] pins)
    {
        var fields=new JsonObject();
        var selected=pins.ToHashSet(StringComparer.Ordinal);
        foreach (var w in prefs["vivaldi"]?["workspaces"]?["list"] as JsonArray ?? [])
        {
            string? id=Number(w?["id"]);
            if (id==null || !selected.Contains(id)) continue;
            var rows=new JsonArray();
            var entry=new JsonObject {["id"]=id,["name"]=Text(w!["name"],512)??"",["icon"]=Text(w["icon"],128)??"",["emoji"]=Text(w["emoji"],64)??"",["tabs"]=rows};
            foreach (var group in image.Tabs.Values.Where(t=>t.Workspace==id && Web(t.Url)).GroupBy(t=>t.Window)
                .OrderBy(g=>Uuid(image.Windows[g.Key]["ext_id"]),StringComparer.Ordinal))
            {
                int index=0;
                string windowId=Uuid(image.Windows[group.Key]["ext_id"]) ?? throw new InvalidDataException("У окна нет стабильного идентификатора.");
                foreach (var t in group.OrderBy(t=>t.Order).ThenBy(t=>t.Id))
                {
                    var row=Decorations(t.Meta);
                    row["id"]=Uuid(t.Meta["ext_id"]) ?? throw new InvalidDataException("У вкладки нет стабильного идентификатора.");
                    row["windowId"]=windowId; row["url"]=t.Url; row["pinned"]=t.Pinned; row["index"]=index++;
                    rows.Add(row);
                }
            }
            fields.Add(Prefix+id,entry);
        }
        return fields;
    }
    static string HashFile(string path)
    {
        using var file=File.OpenRead(path); return Hash(file);
    }
    static string Hash(Stream stream)
    {
        stream.Position=0;
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        stream.Position=0;return hash;
    }
    static void Recover(string profileDir,string marker,string latest,FileStream session)
    {
        var op=JsonNode.Parse(File.ReadAllText(marker))?.AsObject() ?? throw new InvalidDataException("Не удалось прочитать журнал восстановления.");
        var backupId=op["backup"]?.ToString();
        if(op["generation"]?.ToString()!=Path.GetFileName(latest) || !Guid.TryParseExact(backupId,"N",out _))
            throw new IOException("После незавершённого импорта поколение сессии изменилось. Доступна локальная копия в Synvaldi Backups.");
        string prefPath=Path.Combine(profileDir,"Preferences");
        var fileHash=Hash(session);
        var prefHash=HashFile(prefPath);
        if(fileHash==op["newSession"]?.ToString() && prefHash==op["newPrefs"]?.ToString()) {File.Delete(marker);return;}
        if(fileHash!=op["oldSession"]?.ToString())
            throw new IOException("Сессия изменилась после прерванного импорта. Исходная копия сохранена.");
        if(prefHash!=op["oldPrefs"]?.ToString())
        {
            string backup=Path.Combine(profileDir,"Synvaldi Backups",backupId!,"Preferences");
            if(prefHash!=op["newPrefs"]?.ToString() || !File.Exists(backup) || HashFile(backup)!=op["oldPrefs"]?.ToString())
                throw new IOException("Preferences изменились после прерванного импорта. Исходная копия сохранена.");
            var recovery=Path.Combine(Path.GetDirectoryName(backup)!,"restore-"+Guid.NewGuid().ToString("N"));
            File.Copy(backup,recovery);
            try {File.Replace(recovery,prefPath,null);} finally{File.Delete(recovery);}
        }
        File.Delete(marker);
    }
    public static JsonObject Capture(string profileDir,string[] pins)
    {
        if(File.Exists(Path.Combine(profileDir,"Synvaldi Backups","pending.json")))throw new IOException("Незавершённый импорт. Повтори применение снимка после закрытия Vivaldi.");
        using var file=new FileStream(Latest(profileDir),FileMode.Open,FileAccess.Read,FileShare.Read);
        return Export(Prefs(profileDir),Parse(file),pins);
    }
    static byte[] Pack(Action<BinaryWriter> write)
    {
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
        write(writer);return stream.ToArray();
    }
    static void String(BinaryWriter writer,string text)
    {
        var bytes=Encoding.UTF8.GetBytes(text);writer.Write(bytes.Length);writer.Write(bytes);
        for(int i=0;i<(-bytes.Length&3);i++) writer.Write((byte)0);
    }
    static void Command(BinaryWriter target,int kind,byte[] data)
    {
        target.Write(checked((ushort)(data.Length+1)));target.Write((byte)kind);target.Write(data);
    }
    static void Pair(BinaryWriter w,int kind,int x,int y)=>Command(w,kind,Pack(p=>{p.Write(x);p.Write(y);}));
    static void Pickle(BinaryWriter w,int kind,Action<BinaryWriter> write)
    {
        var payload=Pack(write);Command(w,kind,Pack(b=>{b.Write(payload.Length);b.Write(payload);}));
    }
    static void SetMeta(BinaryWriter w,int kind,int id,JsonObject meta) =>
        Pickle(w,kind,p=>{p.Write(id);String(p,meta.ToJsonString());});
    static void Nav(BinaryWriter w,int id,int index,string url)
    {
        // Chromium's documented pickle prefix. Only the virtual URL crosses devices.
        Pickle(w,6,p=>{p.Write(id);p.Write(index);String(p,url);p.Write(0);p.Write(0);p.Write(0);});
        Pair(w,7,id,index);
    }
    static JsonObject Normalize(JsonObject input,string key)
    {
        if (Number(input["id"])!=key || Text(input["name"],512) is not {} name ||
            input["tabs"] is not JsonArray tabs || tabs.Count>10000) throw new InvalidDataException("Некорректный снимок пространства.");
        var rows=new JsonArray();
        var ids=new HashSet<string>();
        foreach (var row in tabs.OfType<JsonObject>())
        {
            string? id=Uuid(row["id"]), win=Uuid(row["windowId"]), url=Text(row["url"],32768);
            if(id==null || win==null || !ids.Add(id) || !Web(url) ||
                row["pinned"] is not JsonValue pin || !pin.TryGetValue<bool>(out var pinned) ||
                row["index"] is not JsonValue order || !order.TryGetValue<int>(out var n) || n is <0 or >10000)
                throw new InvalidDataException("Некорректная вкладка в снимке.");
            var clean=Decorations(row);
            clean["id"]=id;clean["windowId"]=win;clean["url"]=url;clean["pinned"]=pinned;clean["index"]=n;rows.Add(clean);
        }
        if(rows.Count!=tabs.Count) throw new InvalidDataException("Некорректный список вкладок.");
        return new JsonObject{["id"]=key,["name"]=name,["icon"]=Text(input["icon"],128)??"",["emoji"]=Text(input["emoji"],64)??"",["tabs"]=rows};
    }
    public static void Apply(string profileDir,JsonObject snapshot,string[] pins) => ApplyCore(profileDir,snapshot,pins,false);

    public static void Detach(string profileDir,string[] pins)
    {
        string marker=Path.Combine(profileDir,"Synvaldi Backups","pending.json");
        if(File.Exists(marker)){
            string generation=Latest(profileDir);
            using var input=new FileStream(generation,FileMode.Open,FileAccess.Read,FileShare.Delete);
            Recover(profileDir,marker,generation,input);
        }
        var old=Capture(profileDir,pins);
        foreach(var item in old) item.Value!["tabs"]=new JsonArray();
        ApplyCore(profileDir,old,pins,true);
    }

    static void ApplyCore(string profileDir,JsonObject snapshot,string[] pins,bool detach)
    {
        var keys=pins.Distinct().Where(id=>snapshot.ContainsKey(Prefix+id)).ToArray();
        if(keys.Length==0) return;
        var incoming=keys.ToDictionary(k=>k,k=>Normalize(snapshot[Prefix+k]!.AsObject(),k));
        var allTabIds=incoming.Values.SelectMany(x=>x["tabs"]!.AsArray()).Select(x=>x!["id"]!.ToString()).ToArray();
        if(allTabIds.Distinct().Count()!=allTabIds.Length) throw new InvalidDataException("Вкладка указана в нескольких пространствах.");
        var prefs=Prefs(profileDir);
        CheckCompatibility(profileDir,prefs,true);
        string session=Latest(profileDir), prefFile=Path.Combine(profileDir,"Preferences");
        string backupRoot=Path.Combine(profileDir,"Synvaldi Backups");
        string marker=Path.Combine(backupRoot,"pending.json");
        using var input=new FileStream(session,FileMode.Open,FileAccess.Read,FileShare.Delete);
        if(File.Exists(marker))
        {
            Recover(profileDir,marker,session,input);
            // Recovery may have restored the previous catalogue.
            prefs=Prefs(profileDir);
        }
        var image=Parse(input);
        var before=Export(prefs,image,keys);
        if(!detach && keys.All(k=>JsonNode.DeepEquals(before[Prefix+k],incoming[k]))) return;
        var nativeWins=image.Windows.Where(k=>Uuid(k.Value["ext_id"])!=null).ToDictionary(k=>Uuid(k.Value["ext_id"])!,k=>k.Key);
        var nativeTabs=image.Tabs.Values.Where(t=>Uuid(t.Meta["ext_id"])!=null).ToDictionary(t=>Uuid(t.Meta["ext_id"])!);
        var destinations=allTabIds.ToHashSet();
        var selected=pins.ToHashSet(StringComparer.Ordinal);
        var occupied=image.Windows.Keys.Concat(image.Tabs.Keys).ToHashSet();
        int NewId(){int id;do {id=System.Security.Cryptography.RandomNumberGenerator.GetInt32(1,Int32.MaxValue);}while(!occupied.Add(id));return id;}
        var positions=image.Tabs.Values.GroupBy(t=>t.Window).ToDictionary(g=>g.Key,g=>g.Max(t=>t.Order)+1);
        var catalog=((prefs["vivaldi"]!["workspaces"]??=new JsonObject())["list"]??=new JsonArray()).AsArray();
        var journal=Pack(w=>{
            foreach(var pair in incoming)
            {
                string key=pair.Key;var cloud=pair.Value;
                var workspace=catalog.OfType<JsonObject>().SingleOrDefault(v=>Number(v["id"])==key);
                if(workspace==null){workspace=new JsonObject{["id"]=double.Parse(key,CultureInfo.InvariantCulture)};catalog.Add(workspace);}
                foreach(var field in new[]{"name","icon","emoji"})workspace[field]=cloud[field]!.DeepClone();
                if(detach)
                {
                    catalog.Remove(workspace);
                    // Local-only tabs (file, internal pages, credentialed URLs)
                    // retain their navigation and move to the default workspace.
                    foreach(var privateTab in image.Tabs.Values.Where(t=>t.Workspace==key && !Web(t.Url)))
                    {
                        var localMeta=privateTab.Meta.DeepClone().AsObject();
                        localMeta.Remove("workspaceId");
                        SetMeta(w,21,privateTab.Id,localMeta);
                    }
                }
                var tabList=cloud["tabs"]!.AsArray().OfType<JsonObject>().ToArray();
                var keep=tabList.Select(t=>t["id"]!.ToString()).ToHashSet();
                foreach(var old in image.Tabs.Values.Where(t=>t.Workspace==key && Web(t.Url) && !keep.Contains(Uuid(t.Meta["ext_id"])??"") && !destinations.Contains(Uuid(t.Meta["ext_id"])??"")))
                    Command(w,16,Pack(b=>{b.Write(old.Id);b.Write(0);b.Write(DateTime.UtcNow.ToFileTimeUtc()/10);}));
                foreach(var row in tabList.OrderBy(t=>t["windowId"]!.ToString(),StringComparer.Ordinal).ThenBy(t=>t["index"]!.GetValue<int>()))
                {
                    var id=row["id"]!.ToString();var windowId=row["windowId"]!.ToString();
                    if(!nativeWins.TryGetValue(windowId,out var win))
                    {
                        win=NewId();nativeWins[windowId]=win;
                        Pair(w,9,win,0);SetMeta(w,22,win,new JsonObject{["ext_id"]=windowId});
                    }
                    nativeTabs.TryGetValue(id,out var old);
                    if(old!=null && (old.Workspace==null || !selected.Contains(old.Workspace) || !Web(old.Url))) throw new InvalidDataException("Идентификатор вкладки уже занят вне выбранного пространства.");
                    int tab=old?.Id??NewId();
                    Pair(w,0,win,tab);
                    int index=positions.GetValueOrDefault(win);Pair(w,2,tab,index);positions[win]=index+1;
                    Pair(w,12,tab,row["pinned"]!.GetValue<bool>()?1:0);
                    string url=row["url"]!.ToString();if(url!=old?.Url)Nav(w,tab,(old?.Nav.Keys.DefaultIfEmpty(-1).Max()??-1)+1,url);
                    var meta=(old?.Meta.DeepClone() as JsonObject)??new JsonObject();
                    foreach(var k in MetaKeys)meta.Remove(k);
                    foreach(var field in Decorations(row))meta[field.Key]=field.Value?.DeepClone();
                    meta["ext_id"]=id;meta["workspaceId"]=double.Parse(key,CultureInfo.InvariantCulture);SetMeta(w,21,tab,meta);
                }
            }
        });
        string temp=session+"."+Guid.NewGuid().ToString("N")+".tmp", newPrefs=prefFile+"."+Guid.NewGuid().ToString("N")+".tmp";
        string backupDir=Path.Combine(backupRoot,Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDir);
        try
        {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {input.Position=0;input.CopyTo(output);output.Write(journal);output.Flush(true);}
            File.WriteAllText(newPrefs,prefs.ToJsonString(),new UTF8Encoding(false));
            using(var verify=File.OpenRead(temp)){
                var result=Export(prefs,Parse(verify),keys);
                if(keys.Any(k=>detach ? result.ContainsKey(Prefix+k) : !JsonNode.DeepEquals(result[Prefix+k],incoming[k])))throw new InvalidDataException("Проверка результирующего снимка не прошла.");
            }
            if(Latest(profileDir)!=session)throw new IOException("Поколение сессии сменилось во время импорта.");
            string backupPrefs=Path.Combine(backupDir,"Preferences");
            var intent=new JsonObject{["generation"]=Path.GetFileName(session),["backup"]=Path.GetFileName(backupDir),
                ["oldSession"]=Hash(input),["newSession"]=HashFile(temp),["oldPrefs"]=HashFile(prefFile),["newPrefs"]=HashFile(newPrefs)};
            string intentFile=Path.Combine(backupDir,"intent.json");
            using(var journalFile=new FileStream(intentFile,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {journalFile.Write(Encoding.UTF8.GetBytes(intent.ToJsonString())); journalFile.Flush(true);}
            File.Move(intentFile,marker);
            bool committed=false;
            try
            {
                File.Replace(newPrefs,prefFile,backupPrefs);
                File.Replace(temp,session,Path.Combine(backupDir,Path.GetFileName(session)));
                committed=true;
                File.Delete(marker);
            }
            catch
            {
                // If the session replacement already committed, its hash is the commit marker.
                // Leave recovery to the next cold operation if a sharing violation persists.
                if(!committed && File.Exists(marker)) Recover(profileDir,marker,session,input);
                throw;
            }
        }
        finally {File.Delete(temp);File.Delete(newPrefs);}
    }
}






