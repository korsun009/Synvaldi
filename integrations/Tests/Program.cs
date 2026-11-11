using System.Text.Json.Nodes;
using Synvaldi;

string dir = Path.Combine(Path.GetTempPath(),"Synvaldi-fixture-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    var fixture = new JsonObject {
      ["vivaldi"] = new JsonObject {
          ["vivaldi_account"] = new JsonObject{["ignored"]="private"},
          ["toolbars"]= new JsonObject{["navigation"]=new JsonArray("Кнопка")},
          ["themes"]= new JsonObject{["user"]= new JsonArray(new JsonObject{["backgroundImage"]="chrome://vivaldi-data/local-image/bg123.png"})}
      }
    };
    File.WriteAllText(Path.Combine(dir,"Preferences"),fixture.ToJsonString());
    string image = Path.Combine(dir,"background.png");
    File.WriteAllBytes(image,new byte[]{137,80,78,71,13,10,26,10,0,0});
    var mapping = new JsonObject{["mappings"]=new JsonObject{["bg123.png"]=new JsonObject{["local_path"]=image}}};
    File.WriteAllText(Path.Combine(dir,"file_mapping.json"),mapping.ToJsonString());
    int uploads=0;
    Task Upload(string key,byte[] bytes) {uploads++;return Task.CompletedTask;}
    var fields = await PreferencesAdapter.Capture(dir,["appearance"],Upload);
    if(fields["asset/bg123.png"]?.ToString().Length!=64 || uploads!=1 || fields["pref/toolbars/navigation"]?[0]?.ToString()!="Кнопка")
        throw new Exception("Appearance capture mismatch");
    if(fields.ToJsonString().Contains("private") || fields.ToJsonString().Contains(image)) throw new Exception("Unexpected profile/host data");
    var none = await PreferencesAdapter.Capture(dir,[],Upload);
    if(none.Count!=0 || uploads!=1) throw new Exception("Category isolation failed");
    Console.WriteLine("PASS appearance, referenced PNG, digest, Unicode, local path removal, disabled categories");
    if(args.Length==3 && args[0]=="--roundtrip-note"){
        var files=new Dictionary<string,byte[]>();
        var note=await PreferencesAdapter.Capture(args[1],["attachments"],(sha,bytes)=>{files[sha]=bytes;return Task.CompletedTask;});
        if(!note.Any(k=>k.Key.StartsWith("note/")))throw new Exception("Source lacks a test attachment");
        await PreferencesAdapter.Apply(args[2],["attachments"],note,Path.Combine(dir,"assets"),hash=>Task.FromResult(files[hash]));
        var restored=await PreferencesAdapter.Capture(args[2],["attachments"],(_,_)=>Task.CompletedTask);
        if(!JsonNode.DeepEquals(note,restored))throw new Exception("Attachment restore failed");
        Console.WriteLine("PASS Vivaldi note/attachment IDs, bytes, metadata and file index");
    }
    if(args.Length==1){
        var result=await PreferencesAdapter.Capture(args[0],["settings","appearance"],(_,_)=>Task.CompletedTask);
        Console.WriteLine($"Read-only real profile: {result.Count(k=>k.Key.StartsWith("pref/"))} preference fields, {result.Count(k=>k.Key.StartsWith("asset/"))} mapped assets");
    }
}
finally {Directory.Delete(dir,true);}
