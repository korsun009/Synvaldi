using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Synvaldi;

internal static class NoteAttachments
{
    const int Limit = 32 * 1024 * 1024;
    const string ReferenceType = "229171";

    static IEnumerable<JsonObject> Nodes(JsonObject node)
    {
        yield return node;
        foreach(var child in (node["children"] as JsonArray ?? []).OfType<JsonObject>())
            foreach(var item in Nodes(child)) yield return item;
    }
    static (string Md5,string Sha) Checksums(JsonObject root)
    {
        using var md5=IncrementalHash.CreateHash(HashAlgorithmName.MD5); // Vivaldi storage compatibility
        using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string? value,System.Text.Encoding encoding)
        {
            var bytes=encoding.GetBytes(value??"");md5.AppendData(bytes);sha.AppendData(bytes);
        }
        foreach(var node in Nodes(root))
        {
            Append(node["id"]?.ToString(),System.Text.Encoding.UTF8);
            Append(node["subject"]?.ToString(),System.Text.Encoding.Unicode);
            string? type=node["type"]?.ToString();
            Append(type,System.Text.Encoding.UTF8);
            if(type is "note" or "attachment")
            {
                Append(node["content"]?.ToString(),System.Text.Encoding.Unicode);
                Append(node["url"]?.ToString(),System.Text.Encoding.UTF8);
            }
        }
        return (Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }
    static string ContentId(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new System.Text.StringBuilder();
        int accumulator=0,bits=0;
        foreach(byte part in SHA256.HashData(bytes))
        {
            accumulator=(accumulator<<8)|part; bits+=8;
            while(bits>=5) {bits-=5; result.Append(alphabet[(accumulator>>bits)&31]);}
        }
        if(bits>0) result.Append(alphabet[(accumulator<<(5-bits))&31]);
        return result+"."+bytes.Length.ToString(CultureInfo.InvariantCulture);
    }
    static string Hex(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static async Task Capture(string folder,JsonObject fields,Func<string,byte[],Task> upload)
    {
        string notesFile=Path.Combine(folder,"Notes"), store=Path.Combine(folder,"SyncedFiles");
        if(!File.Exists(notesFile)) return;
        var notes=JsonNode.Parse(await File.ReadAllBytesAsync(notesFile))!.AsObject();
        foreach(var parent in Nodes(notes).Where(n=>n["type"]?.ToString()=="note"))
        {
            if(!Guid.TryParse(parent["guid"]?.ToString(),out var parentId)) continue;
            foreach(var item in (parent["children"] as JsonArray ?? []).OfType<JsonObject>().Where(n=>n["type"]?.ToString()=="attachment"))
            {
                if(!Guid.TryParse(item["guid"]?.ToString(),out var id)) continue;
                string key=item["content"]?.ToString()??"";
                if(!Regex.IsMatch(key,@"\A[A-Z2-7]{52}\.[0-9]{1,12}\z")) throw new InvalidDataException("Неизвестный формат вложения заметки.");
                string source=Path.Combine(store,key);
                if(!File.Exists(source)) throw new IOException("Ожидается локальный файл вложения заметки.");
                if(new FileInfo(source).Length>Limit) throw new InvalidDataException("Вложение превышает 32 МиБ.");
                var bytes=await File.ReadAllBytesAsync(source);
                if(ContentId(bytes)!=key) throw new InvalidDataException("Повреждено вложение заметки.");
                string digest=Hex(bytes);
                await upload(digest,bytes);
                fields[$"note/{parentId}/{id}"]=new JsonObject{["sha256"]=digest,["content"]=key,
                    ["subject"]=item["subject"]?.DeepClone(),["date_added"]=item["date_added"]?.DeepClone(),["last_modified"]=item["last_modified"]?.DeepClone()};
            }
        }
    }

    public static async Task Apply(string folder,JsonObject fields,string assetsRoot,Func<string,Task<byte[]>> download,Action ensureCold)
    {
        var incoming=fields.Where(k=>k.Key.StartsWith("note/",StringComparison.Ordinal)).ToList();
        if(incoming.Count==0) return;
        string file=Path.Combine(folder,"Notes"), store=Path.Combine(folder,"SyncedFiles"), index=Path.Combine(store,"SyncedFilesData");
        if(!File.Exists(file)) throw new IOException("Ожидание синхронизации исходных заметок Vivaldi.");
        var notes=JsonNode.Parse(await File.ReadAllBytesAsync(file))!.AsObject();
        var checksum=Checksums(notes);
        if(notes["checksum"]?.ToString()!=checksum.Md5 || notes["checksum_sha256"]?.ToString()!=checksum.Sha)
            throw new InvalidDataException("Формат заметок изменился; запись отложена.");
        var old=notes.DeepClone();
        var all=Nodes(notes).ToList();
        var parents=all.Where(n=>n["type"]?.ToString()=="note").ToDictionary(n=>n["guid"]!.ToString(),StringComparer.OrdinalIgnoreCase);
        long last=all.Select(n=>long.TryParse(n["id"]?.ToString(),out var i)?i:0).Max();
        Directory.CreateDirectory(store);
        var db=File.Exists(index)?JsonNode.Parse(await File.ReadAllBytesAsync(index))!.AsObject():new JsonObject();
        var oldDb=db.DeepClone();
        var info=(db["files_info"]??=new JsonObject()).AsObject();
        foreach(var (path,data) in incoming)
        {
            var parts=path.Split('/');
            if(parts.Length!=3 || !Guid.TryParse(parts[1],out _) || !Guid.TryParse(parts[2],out var guid)) throw new InvalidDataException("Неверный ID вложения.");
            if(!parents.TryGetValue(parts[1],out var parent)){
                if(data==null) continue;
                throw new IOException("Ожидание заметки от штатной синхронизации Vivaldi.");
            }
            var children=(parent["children"]??=new JsonArray()).AsArray();
            var current=children.OfType<JsonObject>().FirstOrDefault(n=>n["guid"]?.ToString()==guid.ToString() && n["type"]?.ToString()=="attachment");
            if(data==null){if(current!=null) children.Remove(current); continue;}
            string hash=data["sha256"]?.ToString()??"";
            if(!Regex.IsMatch(hash,@"\A[0-9a-f]{64}\z")) throw new InvalidDataException("Неверный SHA256.");
            var bytes=await download(hash);
            string key=ContentId(bytes);
            if(bytes.Length>Limit || Hex(bytes)!=hash || data["content"]?.ToString()!=key) throw new InvalidDataException("Вложение не прошло проверку.");
            ensureCold();
            Directory.CreateDirectory(assetsRoot);
            await InstallBytes(Path.Combine(assetsRoot,hash+".attachment"),bytes);
            await InstallBytes(Path.Combine(store,key),bytes);
            var entry=(info[key]??=new JsonObject()).AsObject();
            entry["has_content_locally"]=true;
            entry["mimetype"]=bytes.AsSpan().StartsWith(new byte[]{137,80,78,71})?"image/png":
                bytes.AsSpan().StartsWith(new byte[]{255,216,255})?"image/jpeg":"application/octet-stream";
            entry["sync_refrences"]??=new JsonObject(); // Vivaldi's on-disk spelling
            var references=(entry["local_references"]??=new JsonObject()).AsObject();
            var ids=(references[ReferenceType]??=new JsonArray()).AsArray();
            if(!ids.Any(n=>n?.ToString()==guid.ToString())) ids.Add(guid.ToString());
            if(current==null){current=new JsonObject{["id"]=(++last).ToString(CultureInfo.InvariantCulture),["guid"]=guid.ToString(),["type"]="attachment",["url"]=""};children.Add(current);}
            current["content"]=key;
            foreach(string name in new[]{"subject","date_added","last_modified"})current[name]=data[name]?.DeepClone();
        }
        ensureCold();
        if(!JsonNode.DeepEquals(oldDb,db)) PreferencesAdapter.Write(index,db);
        if(!JsonNode.DeepEquals(old,notes))
        {
            var next=Checksums(notes);
            notes["checksum"]=next.Md5;
            notes["checksum_sha256"]=next.Sha;
            PreferencesAdapter.Write(file,notes);
        }
    }
    static async Task InstallBytes(string target,byte[] bytes)
    {
        if(File.Exists(target) && new FileInfo(target).Length==bytes.Length && Hex(await File.ReadAllBytesAsync(target))==Hex(bytes)) return;
        var temp=target+"."+Guid.NewGuid().ToString("N");
        try { await File.WriteAllBytesAsync(temp,bytes); File.Move(temp,target,true); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}
