using System.Text;
using System.Text.Json.Nodes;
using Synvaldi;

if(args.Length==2 && args[0]=="--probe-sharing" && Path.GetFileName(args[1]).StartsWith("synvaldi-cold-"))
{
    var files=Directory.GetFiles(Path.Combine(args[1],"Default","Sessions"),"Session_*").OrderDescending().ToArray();
    if(files.Length==0)throw new Exception("No live generation");
    foreach(var file in files) {
        try {using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete); Console.WriteLine($"GENERATION {Path.GetFileName(file)} READABLE {stream.Length}");}
        catch(IOException e) when ((e.HResult & 0xffff)==32) {Console.WriteLine($"GENERATION {Path.GetFileName(file)} SHARING_VIOLATION");}
    }
    return;
}
if(args.Length==2 && args[0]=="--verify-fixture" && Path.GetFileName(args[1]).StartsWith("synvaldi-cold-"))
{
    var expected=JsonNode.Parse(File.ReadAllText(Path.Combine(args[1],"expected.json")));
    var actual=ColdWorkspaces.Capture(Path.Combine(args[1],"Default"),["12.5"]);
    if(!JsonNode.DeepEquals(expected,actual)) {
        Console.Error.WriteLine("Expected fixture: "+expected?.ToJsonString());
        Console.Error.WriteLine("Restored fixture: "+actual.ToJsonString());
        throw new Exception("Vivaldi did not preserve complete workspace metadata");
    }
    Console.WriteLine("PASS: browser-persisted workspace membership, stack title, tile metadata, order and pins.");return;
}
// Create a new, uniquely named profile in test storage. Never accept an existing profile as a write target.
if(args.Length!=1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Provide an absolute scratch directory.");
string root=Path.Combine(args[0],"synvaldi-cold-"+Guid.NewGuid().ToString("N"));
string profile=Path.Combine(root,"Default");
Directory.CreateDirectory(Path.Combine(profile,"Sessions"));
var prefs=new JsonObject{["vivaldi"]=new JsonObject{["startup"]=new JsonObject{["last_seen_version"]="8.3.4191.35"},
  ["workspaces"]=new JsonObject{["list"]=new JsonArray(new JsonObject{["id"]=11.25,["name"]="Local"})}},
  ["session"]=new JsonObject{["restore_on_startup"]=1},["profile"]=new JsonObject{["exit_type"]="Normal"}};
File.WriteAllText(Path.Combine(profile,"Preferences"),prefs.ToJsonString());
var session=Path.Combine(profile,"Sessions","Session_"+(DateTime.UtcNow.ToFileTimeUtc()/10));
using(var w=new BinaryWriter(File.Create(session))){w.Write("SNSS"u8);w.Write(3);w.Write((ushort)1);w.Write((byte)255);}
string win=Guid.NewGuid().ToString(), group=Guid.NewGuid().ToString(),tile=Guid.NewGuid().ToString();
JsonObject Workspace(string id,string name,string url,bool decoration)=>new(){["id"]=id,["name"]=name,["icon"]="",["emoji"]="",["tabs"]=new JsonArray(
   Enumerable.Range(0,decoration?2:1).Select(i=>{
       var r=new JsonObject{["id"]=Guid.NewGuid().ToString(),["windowId"]=win,["url"]=url+i,["pinned"]=i==0,["index"]=i};
       if(decoration){r["group"]=group;r["fixedGroupTitle"]="Fixture";r["tiling"]=new JsonObject{["id"]=tile,["index"]=i,["layout"]="row",["type"]="selection"};}
       return (JsonNode)r;
   }).ToArray())};
var other=new JsonObject{["workspace-cold/11.25"]=Workspace("11.25","Local","http://127.0.0.1:49112/untouched",false)};
ColdWorkspaces.Apply(profile,other,["11.25"]);
var target=new JsonObject{["workspace-cold/12.5"]=Workspace("12.5","Cold fixture","http://127.0.0.1:49112/fixture",true)};
target["workspace-cold/12.5"]!["layouts"]=new JsonArray(new JsonObject{["windowId"]=win,["id"]=tile,["tree"]=new JsonObject{["direction"]="row",["first"]=0,["second"]=1,["splitPercentage"]=37.0}});
ColdWorkspaces.Apply(profile,target,["12.5"]);
if(!JsonNode.DeepEquals(ColdWorkspaces.Capture(profile,["11.25"]),other) ||
   !JsonNode.DeepEquals(ColdWorkspaces.Capture(profile,["12.5"]),target))throw new Exception("Cold export differs from selected inputs");
ColdWorkspaces.Detach(profile,["12.5"]);
if(ColdWorkspaces.Capture(profile,["12.5"]).Count!=0 || !JsonNode.DeepEquals(ColdWorkspaces.Capture(profile,["11.25"]),other))
    throw new Exception("Detach failed to preserve unrelated workspace");
ColdWorkspaces.Apply(profile,target,["12.5"]);
string resources=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Vivaldi","Application","8.3.4191.35","resources","vivaldi");
if(Directory.Exists(resources))
{
    if(!ColdWorkspaces.CompatiblePatch("8.3.4191.36","156.0.8078.123",resources) ||
        ColdWorkspaces.CompatiblePatch("8.4.4200.1","156.0.8078.123",resources) ||
        ColdWorkspaces.CompatiblePatch("8.3.4191.36","157.0.8078.123",resources) ||
        ColdWorkspaces.CompatiblePatch("8.3.4191.36","156.0.8078.123",root))
        throw new Exception("Patch compatibility policy");
}
var moved=target.DeepClone().AsObject();
moved["workspace-cold/12.5"]!.AsObject().Remove("layouts");
var movedRow=moved["workspace-cold/12.5"]!["tabs"]![1]!.DeepClone();
moved["workspace-cold/12.5"]!["tabs"]!.AsArray().RemoveAt(1);
var otherAfterMove=other.DeepClone().AsObject();
otherAfterMove["workspace-cold/11.25"]!["tabs"]!.AsArray().Add(movedRow);
moved["workspace-cold/11.25"]=otherAfterMove["workspace-cold/11.25"]!.DeepClone();
try{ColdWorkspaces.Apply(profile,moved,["11.25"]);throw new Exception("moved tab from unselected space");}catch(InvalidDataException){}
ColdWorkspaces.Apply(profile,moved,["12.5","11.25"]);
if(!JsonNode.DeepEquals(ColdWorkspaces.Capture(profile,["12.5","11.25"]),moved))throw new Exception("Cross-workspace move lost identity");
var restored=target.DeepClone().AsObject();restored["workspace-cold/11.25"]=other["workspace-cold/11.25"]!.DeepClone();
ColdWorkspaces.Apply(profile,restored,["12.5","11.25"]);
long length=new FileInfo(session).Length;
ColdWorkspaces.Apply(profile,target,["12.5"]);
if(new FileInfo(session).Length!=length)throw new Exception("Retry appended new records");
var before=File.ReadAllBytes(session);
var bad=target.DeepClone().AsObject();
bad["workspace-cold/12.5"]!["tabs"]![0]!["url"]="file:///private";
try{ColdWorkspaces.Apply(profile,bad,["12.5"]);throw new Exception("invalid URL accepted");}catch(InvalidDataException){}
if(!before.AsSpan().SequenceEqual(File.ReadAllBytes(session)))throw new Exception("Failed operation changed session");
using(var lockFile=new FileStream(session,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
  try{ColdWorkspaces.Capture(profile,["12.5"]);throw new Exception("locked generation accepted");}catch(IOException){}
}
File.WriteAllText(Path.Combine(root,"expected.json"),target.ToJsonString());
Console.WriteLine(root);






