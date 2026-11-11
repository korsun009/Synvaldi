using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Synvaldi;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if(args.Length==2 && args[0]=="--health")return Health(args[1]).GetAwaiter().GetResult();
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{ Source=new Uri("/Synvaldi;component/Theme.xaml",UriKind.Relative) });
        Exception? error=null;
        app.Dispatcher.BeginInvoke(async ()=>{
            try { await Run(args.FirstOrDefault(), args.Skip(1).FirstOrDefault()); }
            catch(Exception ex){error=ex;}
            finally{app.Shutdown();}
        });
        app.Run();
        if(error!=null){Console.Error.WriteLine(error);return 1;}
        return 0;
    }
    static async Task<int> Health(string configPath)
    {
        var config=ConnectionPolicy.Read(configPath)??throw new InvalidDataException("Missing connection config");
        var endpoint=new Uri(config.Server.TrimEnd('/')+"/health");
        using var handler=ConnectionPolicy.Handler(endpoint,config);
        Require(!handler.UseProxy&&!handler.AllowAutoRedirect,"Provisioned endpoint bypasses system proxy and redirects");
        using var client=new System.Net.Http.HttpClient(handler){Timeout=TimeSpan.FromSeconds(20)};
        using var result=await client.GetAsync(endpoint);result.EnsureSuccessStatusCode();
        var health=JsonNode.Parse(await result.Content.ReadAsByteArrayAsync());
        Require(health?["status"]?.ToString()=="ok","Shipped pinned TLS handler GET /health");
        var invalid=config with{CertificateSha256=new string('0',64)};
        using var rejected=new System.Net.Http.HttpClient(ConnectionPolicy.Handler(endpoint,invalid)){Timeout=TimeSpan.FromSeconds(20)};
        bool failed=false;
        try{using var other=await rejected.GetAsync(endpoint);}
        catch(System.Net.Http.HttpRequestException ex) when(ex.InnerException is System.Security.Authentication.AuthenticationException){failed=true;}
        Require(failed,"Changed fingerprint rejected for provisioned self-signed certificate");
        return 0;
    }
    static void Require(bool condition,string name)
    {
        if(!condition)throw new Exception(name);
        Console.WriteLine("OK "+name);
    }
    static async Task Until(Func<bool> condition)
    {
        var deadline=DateTime.UtcNow.AddSeconds(18);
        while(true) {try{if(condition())break;} catch(InvalidOperationException) {} if(DateTime.UtcNow>deadline)throw new TimeoutException("Ожидание интерфейса истекло");await Task.Delay(60);}
    }
    static IEnumerable<FrameworkElement> Children(DependencyObject obj)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(obj).OfType<FrameworkElement>())
        {yield return child;foreach(var inner in Children(child))yield return inner;}
    }
    static void Click(MainView v,string label)=>Children(v).OfType<Button>().First(x=>Equals(x.Content,label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static T Field<T>(MainView v,string label) where T:Control=>Children(v).OfType<T>().Single(x=>AutomationProperties.GetName(x)==label);
    static async Task Run(string? server,string? fixture)
    {
        var work=Path.Combine(Path.GetTempPath(),"synvaldi-tests-"+Guid.NewGuid().ToString("N"));
        var pipe="Synvaldi.Tests."+Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(work);
        var store=new LocalStore(work);
        var randomId=Guid.NewGuid().ToString();
        store.Enqueue("test",randomId,new JsonObject{["one"]=1});
        var reopened=new LocalStore(work);
        Require(reopened.Next("test")?.Id==randomId,"Durable SQLite outbox survives reopen");
        reopened.Done(randomId); Require(store.Next("test")==null && store.Seen(randomId),"Delivered batch deduplicated");
        var records=new JsonArray(new JsonObject {["kind"]="tab",["id"]=randomId,["value"]=new JsonObject{["url"]="https://example.test/",["index"]=0}});
        var snapshot=RecordCodec.Seed(records);var fields=new JsonObject();
        foreach(var (k,v) in snapshot) fields[k]=new JsonObject{["value"]=v?.DeepClone(),["deleted"]=false,["revision"]=3L};
        var state=new JsonObject{["fields"]=fields,["revision"]=3L};
        Require(JsonNode.DeepEquals(RecordCodec.Records(state),records),"Per-property codec round trip");
        records[0]!["value"]!["index"]=4;
        Require(RecordCodec.Changes(records,state).Count==1,"Independent fields not overwritten");
        using var agent=new Agent(store,pipe);
        agent.Start();
        var view=new MainView(agent){ShowInTaskbar=false,Left=-32000,Top=-32000,WindowStartupLocation=WindowStartupLocation.Manual};
        view.Show();
        Require(Field<TextBox>(view,"Адрес сервера")!=null,"First-run native connection wizard");
        if(Environment.GetEnvironmentVariable("SYNVALDI_UI_SHOT") is {Length:>0} screenshot)
        {
            Field<TextBox>(view,"Имя этого устройства").Text="Рабочий ноутбук";
            view.UpdateLayout();
            var content=(FrameworkElement)view.Content;
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(content);
            using var image=File.Create(screenshot);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));encoder.Save(image);
        }
        Click(view,"Настройки");
        Require(Field<TextBox>(view,"Локальная папка вложений")!=null,"Native settings navigation");
        if(server==null){view.Hide();return;}
        Click(view,"Аккаунт");
        Field<TextBox>(view,"Адрес сервера").Text=server;
        var login="ui"+Guid.NewGuid().ToString("N")[..14];
        var password=Guid.NewGuid().ToString("N");
        Field<TextBox>(view,"Логин").Text=login;
        Field<PasswordBox>(view,"Пароль").Password=password;
        Field<TextBox>(view,"Имя этого устройства").Text="Стенд Москва";
        Children(view).OfType<CheckBox>().Single(x=>Equals(x.Content,"Создать новый аккаунт")).IsChecked=true;
        Click(view,"Подключиться");
        await Until(()=>agent.User!=null && Children(view).OfType<Button>().Any(x=>Equals(x.Content,"Создать профиль")));
        Field<TextBox>(view,"Имя нового профиля").Text="Стенд Кириллица"; Click(view,"Создать профиль");
        await Until(()=>Field<ComboBox>(view,"Выбери профиль Synvaldi").Items.Count>0);
        using var api=agent.Connected();
        var cloud=(await api.Call("GET","profiles"))[0]!["id"]!.ToString();
        var profile=Guid.NewGuid().ToString();
        using var cancel=new CancellationTokenSource();
        using var client=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        await Frames.Write(client,new JsonObject{["type"]="hello",["protocol"]=1,["profileId"]=profile,["capabilities"]=new JsonObject{["tabs"]=true,["workspaces"]=false}},cancel.Token);
        var responder=Task.Run(async()=>{
            while(await Frames.Read(client,cancel.Token) is { } msg)
            {
                var type=msg["type"]?.ToString();
                if(type is "apply" or "snapshot" or "focus")
                {
                    var reply=new JsonObject {["type"]=type=="snapshot"?"snapshot":"ack",["ok"]=true,["batchId"]=msg["batchId"]?.DeepClone()};
                    if(type=="snapshot")reply["changes"]=records.DeepClone();
                    await Frames.Write(client,reply,cancel.Token);
                }
            }
        });
        await Until(()=>agent.Bridge.Peers.ContainsKey(profile));
        await agent.Bind(cloud,profile,false,[],"",false);
        Require(store.Options.Workspaces!.Length==0 && !store.Options.Tabs,"No workspaces pinned on new device");
        await agent.Bind(cloud,profile,true,[],"",true);
        Require(store.Options.Applied && (await api.Call("GET","profiles/"+cloud+"/state"))["revision"]!.GetValue<long>()==1,"Seed, browser ACK, admission through native pipe + PostgreSQL");
        view.Navigate("Устройства"); await Until(()=>Children(view).OfType<TextBox>().Any(x=>AutomationProperties.GetName(x)=="Имя устройства"));
        Field<TextBox>(view,"Имя устройства").Text="Ноутбук Ярославль"; Click(view,"Сохранить имя");
        await Until(()=>Children(view).OfType<TextBlock>().Any(x=>x.Text.Contains("Ноутбук Ярославль")));
        Require((await api.Call("GET","devices"))[0]!["name"]!.ToString()=="Ноутбук Ярославль","Native WPF rename, Cyrillic server round trip");
        view.Navigate("Синхронизация");Click(view,"Приостановить синхронизацию");await Until(()=>store.Options.Paused);
        await Until(()=>Children(view).OfType<Button>().Any(x=>Equals(x.Content,"Возобновить синхронизацию")));
        Click(view,"Возобновить синхронизацию");await Until(()=>!store.Options.Paused);Require(true,"Pause and resume via WPF");
        records[0]!["value"]!["url"]="https://example.test/revision-two";
        await Frames.Write(client,new JsonObject {["type"]="changes",["batchId"]=Guid.NewGuid().ToString(),["epoch"]=store.Options.Epoch,["changes"]=records.DeepClone()},cancel.Token);
        var limit=DateTime.UtcNow.AddSeconds(20);
        while((await api.Call("GET","profiles/"+cloud+"/state"))["revision"]!.GetValue<long>()<2)
        {if(DateTime.UtcNow>limit)throw new Exception("Upload timed out: "+agent.Status);await Task.Delay(120);}
        Require(true,"Extension batch accepted durably and uploaded via desktop agent");
        await api.Call("POST","profiles/"+cloud+"/rollback",new {operationId=Guid.NewGuid(),revision=1});
        var restored=RecordCodec.Records((await api.Call("GET","profiles/"+cloud+"/state")).AsObject());
        Require(restored[0]!["value"]!["url"]!.ToString()=="https://example.test/","Rollback across desktop/API restores original URL");
        view.Navigate("История");await Until(()=>Children(view).OfType<Button>().Any(x=>Equals(x.Content,"Восстановить эту ревизию")));
        Require(true,"72-hour history visible through native UI");
        if(fixture!=null)
        {
            if(!Path.GetFileName(fixture).StartsWith("synvaldi-cold-") || !File.Exists(Path.Combine(fixture,"expected.json")))throw new ArgumentException("Expected generated cold integration fixture");
            // Copy the disposable browser fixture; this test never binds a supplied directory directly.
            var path=Path.Combine(work,"ColdProfile","Default");Directory.CreateDirectory(Path.Combine(path,"Sessions"));
            var source=Path.Combine(fixture,"Default");
            File.Copy(Path.Combine(source,"Preferences"),Path.Combine(path,"Preferences"));
            foreach(var s in Directory.EnumerateFiles(Path.Combine(source,"Sessions"),"Session_*"))File.Copy(s,Path.Combine(path,"Sessions",Path.GetFileName(s)));
            var cold=(await api.Call("POST","profiles",new{name="Закрытые пространства"}))["id"]!.ToString();
            await agent.BindCold(cold,"",path,["12.5"],true,["12.5"]);
            Require(store.Options.Workspaces!.SequenceEqual(["12.5"]) && store.Options.Applied,"Cold workspace first seed and verified admission");
            view.Navigate("Пространства");await Until(()=>Children(view).OfType<CheckBox>().Any());
            var selected=Children(view).OfType<CheckBox>().Single();Require(selected.IsChecked==true,"Native cloud workspace pin rendered");
            selected.IsChecked=false; Click(view,"Сохранить на этом ПК");await Until(()=>store.Options.Workspaces?.Length==0 && store.Options.Applied);
            var data=await api.Call("GET","profiles/"+cold+"/state");
            Require(data["fields"]!["workspace-cold/12.5"]!["deleted"]!.GetValue<bool>()==false,"Unpin does not delete cloud workspace");
            Require(!Agent.Catalogue(path).Any(x=>x?["id"]?.ToString()=="12.5"),"Detached workspace removed only from this local browser profile");
            using var second=new Agent(new LocalStore(Path.Combine(work,"second-store")),"Synvaldi.Tests."+Guid.NewGuid().ToString("N"));
            second.Start();
            await second.Login(server,login,password,"Второй стенд",false);
            string replica=Path.Combine(work,"Replica","Default");
            Directory.CreateDirectory(Path.Combine(replica,"Sessions"));
            File.Copy(Path.Combine(source,"Preferences"),Path.Combine(replica,"Preferences"));
            foreach(var s in Directory.EnumerateFiles(Path.Combine(source,"Sessions"),"Session_*"))File.Copy(s,Path.Combine(replica,"Sessions",Path.GetFileName(s)));
            var localDownloads=Path.Combine(work,"Личные загрузки");
            second.Store.Options=second.Store.Options with{Downloads=localDownloads};
            await second.BindCold(cold,"",replica,[],false,["11.25"]);
            Require(!Agent.Catalogue(replica).Any() && Directory.EnumerateFiles(Path.Combine(replica,"Synvaldi Backups"),"Preferences",SearchOption.AllDirectories).Any(),"New PC initial replacement backs up and detaches unselected local workspaces");
            var appliedPrefs=JsonNode.Parse(File.ReadAllBytes(Path.Combine(replica,"Preferences")));
            Require(appliedPrefs?["download"]?["default_directory"]?.ToString()==localDownloads && File.Exists(Path.Combine(replica,"Preferences.synvaldi.downloads.bak")),"Explicit download mapping applied locally with backup");
            using var api2=second.Connected();
            var current=await api2.Call("GET","profiles/"+cold+"/state");
            var importLimit=DateTime.UtcNow.AddSeconds(15);
            while(current["fields"]?["workspace-cold/11.25"]==null)
            {if(DateTime.UtcNow>importLimit)throw new Exception("New PC selected import was not durable");await Task.Delay(120);current=await api2.Call("GET","profiles/"+cold+"/state");}
            Require(second.Store.Options.Workspaces?.Length==0 && current["fields"]?["workspace-cold/11.25"]?["deleted"]?.GetValue<bool>()==false,"Selected import uploaded after admission with no implicit pins");
            var candidate=await PreferencesAdapter.Capture(replica,second.Categories(),(_,_)=>Task.CompletedTask);
            Require(!current["fields"]!.AsObject().Any(x=>x.Key.Contains("download",StringComparison.OrdinalIgnoreCase)) && candidate.All(x=>x.Value?.ToString()!=localDownloads),"Machine download directory excluded from cloud fields");
        }
        view.Hide();cancel.Cancel();try{await responder;}catch(OperationCanceledException){}
    }
}

