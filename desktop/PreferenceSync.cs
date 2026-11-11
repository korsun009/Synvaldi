namespace Synvaldi;

internal sealed class PreferenceSync(LocalStore store)
{
    private static JsonObject Extract(JsonObject state,bool revisions)
    {
        var values=new JsonObject();
        foreach(var (key,node) in state["fields"] as JsonObject ?? [])
            if(key.StartsWith("pref/")||key.StartsWith("asset/")||key.StartsWith("note/"))
                values[key]=revisions?node?["revision"]?.DeepClone():node?["deleted"]?.GetValue<bool>()==true?null:node?["value"]?.DeepClone();
        return values;
    }
    internal async Task<JsonObject> Capture(Api api,string folder,string[] cats,string scope)
    {
        if(folder.Length==0 || cats.Length==0)return new JsonObject();
        return await PreferencesAdapter.Capture(folder,cats,async (digest,bytes)=>{
            var key="blob/"+scope+"/"+digest;
            if(store.Get(key)=="1")return;
            await api.Upload(digest,bytes); store.Set(key,"1");
        });
    }
    public bool InitialMatch(string scope,JsonObject cloud,JsonObject local)
    {
        var remote=Extract(cloud,false);
        if(!remote.All(x=>JsonNode.DeepEquals(x.Value,local[x.Key]))) {Reset(scope);return false;}
        store.Set("pref-local/"+scope,local.ToJsonString());
        store.Set("pref-remote/"+scope,remote.ToJsonString());
        store.Set("pref-bases/"+scope,Extract(cloud,true).ToJsonString());
        return true;
    }
    public void Reset(string scope) => store.Set("pref-remote/"+scope,"");
    public async Task<bool> Run(Api api,string scope,string folder,string[] cats,Options o,JsonObject cloud,bool localDirty,bool browserOpen)
    {
        var downloadsReady=await Task.Run(()=>LocalDownload.Apply(folder,o.Downloads,browserOpen));
        if(string.IsNullOrEmpty(folder) || cats.Length==0)return downloadsReady;
        var remote=Extract(cloud,false);
        var previous=store.Get("pref-remote/"+scope);
        if(!string.IsNullOrEmpty(previous) && localDirty)
        {
            var current=await Capture(api,folder,cats,scope);
            var baseline=JsonNode.Parse(store.Get("pref-local/"+scope) ?? "{}")!.AsObject();
            var bases=JsonNode.Parse(store.Get("pref-bases/"+scope) ?? "{}")!.AsObject();
            var changes=new JsonArray();
            foreach(string key in current.Select(x=>x.Key).Union(baseline.Select(x=>x.Key)))
            {
                if(JsonNode.DeepEquals(current[key],baseline[key]) && current.ContainsKey(key)==baseline.ContainsKey(key))continue;
                changes.Add(new JsonObject {["key"]=key,["value"]=current[key]?.DeepClone(),["deleted"]=!current.ContainsKey(key),["baseRevision"]=bases[key]?.DeepClone() ?? 0L});
            }
            if(changes.Count>0)
            {
                var id=Guid.NewGuid().ToString();
                // A disk snapshot can include writes made during sleep or an agent outage.
                store.Enqueue(scope,id,new JsonObject{["operationId"]=id,["offline"]=true,["changes"]=changes});
                store.Set("pref-local/"+scope,current.ToJsonString());
                return false;
            }
        }
        bool cloudChanged=string.IsNullOrEmpty(previous) || !JsonNode.DeepEquals(JsonNode.Parse(previous),remote);
        if(cloudChanged)
        {
            if(browserOpen)return false;
            var assets=string.IsNullOrEmpty(o.Attachments)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synvaldi","Assets"):o.Attachments;
            await PreferencesAdapter.Apply(folder,cats,remote,assets,api.Download);
            var captured=await Capture(api,folder,cats,scope);
            store.Set("pref-local/"+scope,captured.ToJsonString());
            store.Set("pref-remote/"+scope,remote.ToJsonString());
        }
        store.Set("pref-bases/"+scope,Extract(cloud,true).ToJsonString());
        return downloadsReady;
    }
}
