using System.Security.Cryptography;
using System.Text;

namespace Synvaldi;

internal sealed partial class Agent
{
    internal static JsonArray Catalogue(string folder) => VivaldiMetadata.Catalogue(folder);
    private static string ColdIdentity(string folder) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())).AsSpan(0,16)).ToString();
    private static JsonObject ColdFields(JsonObject state,string[] pins)
    {
        var result=new JsonObject();
        foreach(var id in pins)
        {
            string key="workspace-cold/"+id;
            var field=state["fields"]?[key];
            if(field?["deleted"]?.GetValue<bool>()==true)result[key]=new JsonObject{["id"]=id,["name"]=id,["tabs"]=new JsonArray()};
            else if(field?["value"] is { } value)result[key]=value.DeepClone();
        }
        return result;
    }
    private static JsonObject ColdCapture(string folder,string[] pins)
    {
#if COLD_WORKSPACES
        return ColdWorkspaces.Capture(folder,pins);
#else
        throw new NotSupportedException("Адаптер закрытых пространств недоступен в этой сборке.");
#endif
    }
    private static void ColdDetach(string folder,string[] pins)
    {
        if(pins.Length==0)return;
#if COLD_WORKSPACES
        ColdWorkspaces.Detach(folder,pins);
#else
        throw new NotSupportedException("Адаптер закрытых пространств недоступен.");
#endif
    }
    private static async Task ApplySelection(string folder,JsonObject state,string[] pins)
    {
        var deleted=pins.Where(id=>state["fields"]?["workspace-cold/"+id]?["deleted"]?.GetValue<bool>()==true).ToArray();
        var active=pins.Except(deleted).ToArray();
        var fields=ColdFields(state,active);
        await Task.Run(()=>{
            ColdDetach(folder,deleted);
            if(fields.Count>0)ColdApply(folder,fields,active);
        });
    }
    private static void ColdApply(string folder,JsonObject fields,string[] pins)
    {
#if COLD_WORKSPACES
        ColdWorkspaces.Apply(folder,fields,pins);
#else
        throw new NotSupportedException("Адаптер закрытых пространств недоступен в этой сборке.");
#endif
    }
    public async Task BindCold(string cloud,string browser,string folder,string[] pins,bool seed,string[] importIds)
    {
        if(!Guid.TryParse(cloud,out _) || !Directory.Exists(folder) || !File.Exists(Path.Combine(folder,"Preferences")))
            throw new ArgumentException("Выбери облачный профиль и каталог профиля Vivaldi с файлом Preferences.");
        if(!Guid.TryParse(browser,out _)) browser=ColdIdentity(folder);
        await Locked(async()=>{
            using var api=Connected();
            var old=Store.Options;
            if(!string.IsNullOrEmpty(old.Cloud) && Store.Next(Scope(old))!=null)throw new InvalidOperationException("Сначала дождись отправки очереди.");
            var o=old with{Cloud=cloud,Browser=browser,Workspaces=pins,Tabs=false,Paused=false,Applied=false,Epoch=Guid.NewGuid().ToString()};
            string scope=Scope(o);
            Store.Options=o; Store.Set("folder/"+scope,Path.GetFullPath(folder)); ConfigureMetadata(); await ConfigureAll();
            var state=(await api.Call("GET",Route(o)+"/state")).AsObject();
            var pending=JsonNode.Parse(Store.Get("import/"+scope)??"null") as JsonObject;
            var local=pending?["fields"]?.AsObject() ?? (importIds.Length==0?new JsonObject():await Task.Run(()=>ColdCapture(folder,importIds)));
            string importOp=pending?["id"]?.ToString()??Guid.NewGuid().ToString();
            if(local.Count>0 && pending==null)
                Store.Set("import/"+scope,new JsonObject{["id"]=importOp,["fields"]=local.DeepClone()}.ToJsonString());
            bool firstSeed=state["revision"]!.GetValue<long>()==0;
            if(firstSeed)
            {
                if(!seed || local.Count==0)throw new InvalidOperationException("Для первого заполнения облака отметь пространства исходного ПК.");
                var fields=local.DeepClone().AsObject();
                foreach(var (key,val) in await preferences.Capture(api,folder,Categories(),scope))fields[key]=val?.DeepClone();
                await api.Call("POST",Route(o)+"/seed",new{operationId=Guid.NewGuid(),fields});
            }
            var firstCloudDownload=!firstSeed && Store.Get("cold-joined/"+scope)!="1";
            var dropLocal=firstCloudDownload?Catalogue(folder).OfType<JsonObject>().Select(x=>x["id"]!.ToString()).Except(pins).ToArray():[];
            var importData=local;
            for(int retry=0;retry<4;retry++)
            {
                state=(await api.Call("GET",Route(o)+"/state")).AsObject();
                await ApplySelection(folder,state,pins);
                preferences.Reset(scope);
                if(!await preferences.Run(api,scope,folder,Categories(),o,state,false,false))
                    throw new IOException("Закрой этот профиль Vivaldi для применения локальных путей.");
                if(dropLocal.Length>0) await Task.Run(()=>ColdDetach(folder,dropLocal));
                try{await api.Call("POST",Route(o)+"/admit",new{revision=state["revision"]!.GetValue<long>()});}
                catch(ApiError ex) when(ex.Status==System.Net.HttpStatusCode.Conflict && retry<3){continue;}
                if(importData.Count>0)
                {
                    var changes=new JsonArray();
                    foreach(var (key,val) in importData)
                        if(state["fields"]?[key]==null) changes.Add(new JsonObject{["key"]=key,["value"]=val?.DeepClone(),["deleted"]=false,["baseRevision"]=0L});
                    if(changes.Count>0)Store.Enqueue(scope,importOp,new JsonObject{["operationId"]=importOp,["offline"]=true,["changes"]=changes});
                }
                if(old.Applied && !old.Tabs && old.Cloud==cloud && old.Browser==browser)
                    await Task.Run(()=>ColdDetach(folder,(old.Workspaces??[]).Except(pins).ToArray()));
                var after=pins.Length==0?new JsonObject():await Task.Run(()=>ColdCapture(folder,pins));
                Store.Set("cold-local/"+scope,after.ToJsonString());
                Store.Set("cold-joined/"+scope,"1");
                Store.Set("import/"+scope,"null");
                Store.State(scope,state);Store.Options=o with {Applied=true};await ConfigureAll();
                Show("Выбор пространств сохранён на этом ПК");Wake();return;
            }
        });
    }
    private async Task SyncCold(string folder,string scope,Options o,JsonObject old,JsonObject state,bool dirty)
    {
        var pins=o.Workspaces??[];
        if(pins.Length==0)return;
        var previous=ColdFields(old,pins);
        var remote=ColdFields(state,pins);
        var baseline=JsonNode.Parse(Store.Get("cold-local/"+scope)??"{}")!.AsObject();
        if(dirty)
        {
            var local=await Task.Run(()=>ColdCapture(folder,pins));
            var changes=new JsonArray();
            foreach(var(key,value) in local)
                if(!JsonNode.DeepEquals(baseline[key],value))
                    changes.Add(new JsonObject{["key"]=key,["value"]=value?.DeepClone(),["deleted"]=false,["baseRevision"]=old["fields"]?[key]?["revision"]?.DeepClone()??0L});
            if(changes.Count>0)
            {
                var id=Guid.NewGuid().ToString();Store.Enqueue(scope,id,new JsonObject{["operationId"]=id,["offline"]=true,["changes"]=changes});
                Store.Set("cold-local/"+scope,local.ToJsonString()); Wake(); return;
            }
        }
        if(JsonNode.DeepEquals(previous,remote) && Store.Get("cold-verify/"+scope)!="1")return;
        await ApplySelection(folder,state,pins);
        Store.Set("cold-local/"+scope,(await Task.Run(()=>ColdCapture(folder,pins))).ToJsonString());
        Store.Set("cold-verify/"+scope,"0");
    }
}

