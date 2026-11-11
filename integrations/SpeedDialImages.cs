using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Synvaldi;

internal static class SpeedDialImages
{
    const string Prefix = "chrome://vivaldi-data/thumbnail/";
    const string Field = "asset/thumbnail/";
    static IEnumerable<JsonObject> Bookmarks(JsonNode? node)
    {
        if(node is not JsonObject item) yield break;
        if(item["type"]?.ToString()=="url"){yield return item; yield break;}
        if(item["roots"] is JsonObject roots)
            foreach(var root in roots) foreach(var bookmark in Bookmarks(root.Value)) yield return bookmark;
        if(item["children"] is JsonArray children)
            foreach(var child in children) foreach(var bookmark in Bookmarks(child)) yield return bookmark;
    }
    public static async Task Capture(string folder,JsonObject fields,Func<string,byte[],Task> upload)
    {
        var path=Path.Combine(folder,"Bookmarks");
        if(!File.Exists(path))return;
        foreach(var bookmark in Bookmarks(JsonNode.Parse(await File.ReadAllBytesAsync(path))))
        {
            string image=bookmark["meta_info"]?["Thumbnail"]?.ToString()??"";
            if(!image.StartsWith(Prefix,StringComparison.Ordinal) || !Guid.TryParse(bookmark["guid"]?.ToString(),out var id))continue;
            string name=image[Prefix.Length..];
            if(!Regex.IsMatch(name,@"\A[A-Za-z0-9_-]{1,120}\.(jpg|jpeg|png|webp)\z"))continue;
            string file=Path.Combine(folder,"VivaldiThumbnails",name);
            if(!File.Exists(file)) continue;
            if(new FileInfo(file).Length>32*1024*1024)throw new InvalidDataException("Изображение плитки превышает 32 МиБ.");
            byte[] bytes=await File.ReadAllBytesAsync(file);
            if(PreferencesAdapter.Extension(bytes)==null)continue;
            var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await upload(hash,bytes);
            fields[Field+id]=hash;
        }
    }
    public static async Task Apply(string folder,JsonObject fields,Func<string,Task<byte[]>> download,Action checkCold)
    {
        var incoming=fields.Where(x=>x.Key.StartsWith(Field,StringComparison.Ordinal)).ToList();
        if(incoming.Count==0)return;
        var path=Path.Combine(folder,"Bookmarks");
        if(!File.Exists(path))throw new IOException("Ожидается доставка закладок через Vivaldi Sync.");
        var data=JsonNode.Parse(await File.ReadAllBytesAsync(path))!.AsObject();
        var bookmarks=Bookmarks(data).Where(n=>Guid.TryParse(n["guid"]?.ToString(),out _)).ToDictionary(n=>n["guid"]!.ToString(),StringComparer.OrdinalIgnoreCase);
        bool changed=false;
        foreach(var(key,value) in incoming)
        {
            if(!bookmarks.TryGetValue(key[Field.Length..],out var item))
            {
                if(value==null)continue;
                throw new IOException("Ожидается закладка с изображением от штатной синхронизации.");
            }
            if(item["meta_info"] is not JsonObject meta)item["meta_info"]=meta=new JsonObject();
            if(value==null){
                if(meta["Thumbnail"]?.ToString().StartsWith(Prefix,StringComparison.Ordinal)==true)changed|=meta.Remove("Thumbnail");
                continue;
            }
            var hash=value.ToString();
            if(!Regex.IsMatch(hash,@"\A[0-9a-f]{64}\z"))throw new InvalidDataException("Некорректная ссылка на изображение плитки.");
            var bytes=await download(hash);
            var ext=PreferencesAdapter.Extension(bytes);
            if(ext==null || bytes.Length>32*1024*1024 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл плитки не прошёл проверку.");
            string name=hash[..32]+ext;
            var dir=Path.Combine(folder,"VivaldiThumbnails");Directory.CreateDirectory(dir);
            string target=Path.Combine(dir,name);
            if(!File.Exists(target)||!SHA256.HashData(await File.ReadAllBytesAsync(target)).SequenceEqual(SHA256.HashData(bytes))){
                string tmp=target+"."+Guid.NewGuid().ToString("N");
                try{await File.WriteAllBytesAsync(tmp,bytes); File.Move(tmp,target,true);}finally{File.Delete(tmp);}
            }
            if(meta["Thumbnail"]?.ToString()!=Prefix+name){meta["Thumbnail"]=Prefix+name;changed=true;}
        }
        checkCold();
        if(changed)PreferencesAdapter.Write(path,data);
    }
}
