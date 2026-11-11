using System.Net;
using System.Threading.Channels;

namespace Synvaldi;

internal sealed partial class Agent : IDisposable
{
    public LocalStore Store { get; }
    public Bridge Bridge { get; }
    public Account? User { get; private set; }
    public string Status { get; private set; } = "Ожидание подключения";
    public event Action? Changed;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<(Peer,JsonObject)> inbox = Channel.CreateUnbounded<(Peer,JsonObject)>();
    private CancellationTokenSource? eventStop;
    private volatile string? lease;
    private int prefsDirty=1;
    private string? lastDeviceStatus;
    private readonly PreferenceSync preferences;
    private bool verifyLocal;
    private void PrefChanged() { Interlocked.Increment(ref prefsDirty); Wake(); }
    private MetadataWatch? metadata;
    public Agent(LocalStore store,string pipeName="Synvaldi.Agent.v1")
    {
        Store = store;
        Bridge=new Bridge(pipeName);
        preferences = new PreferenceSync(store);
        User = store.Login;
        Bridge.Message += OnMessage;
        Bridge.Changed += () => { Changed?.Invoke(); Wake(); };
        Bridge.Error += ex => Show(ex.Message);
    }
    public string[] Categories() => JsonSerializer.Deserialize<string[]>(Store.Get("categories") ?? "[\"settings\",\"appearance\",\"attachments\"]") ?? [];
    public void SetCategories(string[] categories)
    {
        Store.Set("categories",JsonSerializer.Serialize(categories));
        var o=Store.Options;
        if(User!=null && !string.IsNullOrEmpty(o.Cloud)) preferences.Reset(Scope(o));
        PrefChanged();
    }
    public void Start()
    {
        _ = Watch(Bridge.Listen(stop.Token));
        _ = Watch(Incoming());
        _ = Watch(Worker());
        Subscribe();
        ConfigureMetadata();
        Wake();
    }
    private async Task Watch(Task task)
    {
        try { await task.ConfigureAwait(false); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Show(ex.Message); }
    }
    public void Show(string message) { Status = message; Changed?.Invoke(); }
    private void Wake() => wake.Writer.TryWrite(true);
    private async Task Worker()
    {
        await foreach (var _ in wake.Reader.ReadAllAsync(stop.Token))
        {
            await Task.Delay(180, stop.Token);
            try { await Locked(Sync); }
            catch (Exception ex)
            {
                Show("Синхронизация ожидает повторной попытки · " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(8), stop.Token);
                Wake();
            }
        }
    }
    private async Task Locked(Func<Task> action)
    {
        await gate.WaitAsync(stop.Token);
        try { await action(); } finally { gate.Release(); }
    }
    public Api Connected() => User is { } account ? new Api(account.Server, account.Token) : throw new InvalidOperationException("Сначала войди в аккаунт.");
    private string Scope(Options o) => User!.AccountId + "/" + o.Cloud + "/" + o.Browser;
    private string Route(Options o) => "profiles/" + o.Cloud;
    private Peer? Current(Options o) => Bridge.Peers.GetValueOrDefault(o.Browser ?? "");
    private void Subscribe()
    {
        eventStop?.Cancel();
        eventStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        if (User == null) return;
        var ct = eventStop.Token;
        _ = Task.Run(async () => {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var api = Connected();
                    await api.Events(evt => {
                        if(evt["onlineLease"] is { } currentLease && lease!=currentLease.ToString())
                        {
                            lease=currentLease.ToString();
                            foreach(var p in Bridge.Peers.Values) _ = Watch(p.Send(new JsonObject{["type"]="lease",["onlineLease"]=lease}));
                            Wake();
                        }
                        if (evt["profileId"]?.ToString() == Store.Options.Cloud) Wake();
                    }, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    lease = null;
                    foreach(var p in Bridge.Peers.Values) _ = Watch(p.Send(new JsonObject{["type"]="lease",["onlineLease"]=null}));
                    Show("Связь с сервером прервана · " + ex.Message);
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
                    Wake();
                }
            }
        },ct);
    }
    public async Task Login(string server, string username, string password, string deviceName, bool register)
    {
        if (User != null) throw new InvalidOperationException("Сначала выйди из текущего аккаунта.");
        using var api = new Api(server);
        var result = await api.Call("POST","auth/" + (register ? "register" : "login"),new {username,password,deviceName});
        var user = new Account(server, result["username"]!.ToString(), result["deviceId"]!.ToString(), result["accountId"]!.ToString(), result["token"]!.ToString());
        Store.Login = user;
        User = user;
        Subscribe(); Wake(); Show("Аккаунт подключён");
    }
    public async Task Logout() => await Locked(async () => {
        var opts = Store.Options;
        if (!string.IsNullOrEmpty(opts.Cloud) && Store.Next(Scope(opts)) != null) throw new InvalidOperationException("Дождись отправки локальных изменений перед выходом.");
        using var api = Connected();
        await api.Call("POST","auth/logout");
        Store.Login = null; Store.Options = new Options(); User = null;
        Subscribe(); ConfigureMetadata(); await ConfigureAll(); Show("Выход выполнен");
    });
    public async Task Bind(string cloud, string browser, bool tabs, string[] pins, string localFolder, bool seed)
    {
        await Locked(async () => {
            if (!Guid.TryParse(cloud,out _) || !Guid.TryParse(browser,out _) || !Bridge.Peers.ContainsKey(browser))
                throw new InvalidOperationException("Выбери подключённый профиль Vivaldi.");
            var previous = Store.Options;
            if (!string.IsNullOrEmpty(previous.Cloud) && Store.Next(Scope(previous)) != null)
                throw new InvalidOperationException("Дождись отправки накопленных изменений перед сменой настроек.");
            var o = previous with { Cloud = cloud, Browser = browser, Tabs = tabs, Workspaces = pins, Epoch = Guid.NewGuid().ToString(), Applied = false };
            var scope = Scope(o);
            Store.Set("folder/" + scope, localFolder);
            Store.Options = o;
            ConfigureMetadata();
            await ConfigureAll();
            if (!tabs && pins.Length == 0) { Show("Пространства на этом ПК не выбраны"); return; }
            var peer = Current(o)!;
            if(!o.Tabs && peer.Hello["capabilities"]?["workspaces"]?.GetValue<bool>() != true)
            {
                Show("Закрепления сохранены. Эта версия Vivaldi не поддерживает применение выбранных пространств.");
                return;
            }
             
            await peer.Send(Config(peer, o));
            var local = (await peer.Request(new JsonObject { ["type"]="snapshot" }))["changes"]?.AsArray()
                ?? throw new InvalidDataException("Vivaldi не вернул снимок.");
            Store.Backup(scope,local);
            using var api = Connected();
            var state = (await api.Call("GET",Route(o)+"/state")).AsObject();
            if (seed && (state["revision"]?.GetValue<long>() ?? 0) == 0)
            {
                if (local.Count == 0) throw new InvalidOperationException("Первое заполнение требует непустого исходного профиля.");
                var fields=RecordCodec.Seed(local);
                var prefs=await preferences.Capture(api,localFolder,Categories(),scope);
                foreach(var (key,value) in prefs) fields[key]=value?.DeepClone();
                await api.Call("POST",Route(o)+"/seed",new {operationId = Guid.NewGuid(), fields});
            }
            else if ((state["revision"]?.GetValue<long>() ?? 0) == 0)
                throw new InvalidOperationException("Облако пока пусто. Выбери исходный профиль на первом ПК.");
            for (int attempt=0; attempt<4; attempt++)
            {
                state = (await api.Call("GET",Route(o)+"/state")).AsObject();
                await Apply(peer, Select(RecordCodec.Records(state),o), true);
                if(localFolder.Length>0 && Categories().Length>0)
                {
                    var localPrefs=await preferences.Capture(api,localFolder,Categories(),scope);
                    if(!preferences.InitialMatch(scope,state,localPrefs))
                    {
                        Store.State(scope,state);Store.Set("bootstrap/"+scope,o.Epoch);
                        Show("Вкладки загружены. Закрой Vivaldi для применения настроек до допуска устройства.");Wake();return;
                    }
                }
                try { await api.Call("POST",Route(o)+"/admit",new {revision = state["revision"]!.GetValue<long>()}); }
                catch (ApiError ex) when (ex.Status == HttpStatusCode.Conflict && attempt < 3) { continue; }
                Store.State(scope,state); o = o with { Applied = true }; Store.Options = o;
                await ConfigureAll(); Show("Профиль подключён"); Wake(); return;
            }
        });
    }
    private static JsonArray Select(JsonArray all, Options o)
    {
        if (o.Tabs) return all;
        var pins = (o.Workspaces ?? []).ToHashSet();
        return new JsonArray(all.OfType<JsonObject>().Where(obj => obj["kind"]?.ToString() == "workspace" ?
            pins.Contains(obj["id"]?.ToString() ?? "") : pins.Contains(obj["value"]?["workspaceId"]?.ToString() ?? "")).Select(obj=>obj.DeepClone()).ToArray());
    }
    private async Task CompleteBootstrap(Options o)
    {
        var scope=Scope(o);var folder=Store.Get("folder/"+scope)??"";
        using var api=Connected();
        var state=(await api.Call("GET",Route(o)+"/state")).AsObject();
        var last=Store.State(scope);
        var peer=Current(o);
        var from=Select(RecordCodec.Records(last),o);var to=Select(RecordCodec.Records(state),o);
        if(!JsonNode.DeepEquals(from,to))
        {
            if(peer==null){Show("Первичная загрузка ожидает открытия Vivaldi для обновления вкладок");return;}
            await Apply(peer,to,true);Store.State(scope,state);
        }
        var ready=await preferences.Run(api,scope,folder,Categories(),o,state,false,peer!=null);
        if(!ready){Show("Первичная загрузка ожидает закрытия Vivaldi для применения настроек");return;}
        await api.Call("POST",Route(o)+"/admit",new{revision=state["revision"]!.GetValue<long>()});
        Store.State(scope,state);Store.Options=o with {Applied=true};Store.Set("bootstrap/"+scope,"");
        await ConfigureAll();Show("Первоначальная загрузка завершена");Wake();
    }
    public void ConfigureMetadata()
    {
        metadata?.Dispose(); metadata = null;
        var o = Store.Options;
        if (User == null || string.IsNullOrEmpty(o.Cloud)) return;
        var folder = Store.Get("folder/" + Scope(o));
        if (folder == null || !Directory.Exists(folder)) return;
        metadata = new MetadataWatch(folder, PrefChanged);
    }
    private JsonObject Config(Peer peer, Options o)
    {
        bool supported=o.Tabs || peer.Hello["capabilities"]?["workspaces"]?.GetValue<bool>()==true;
        bool mapped = supported && User != null && peer.Id == o.Browser && !string.IsNullOrEmpty(o.Cloud) && !o.Paused && (o.Tabs || (o.Workspaces?.Length ?? 0)>0);
        return new JsonObject { ["type"]="config", ["enabled"]=mapped, ["capture"]=mapped && o.Applied, ["epoch"]=o.Epoch,
            ["selection"]=o.Tabs ? "all" : "workspaces", ["workspaces"]=JsonSerializer.SerializeToNode(o.Workspaces ?? []) };
    }
    private async Task ConfigureAll()
    {
        foreach (var peer in Bridge.Peers.Values) await peer.Send(Config(peer,Store.Options));
    }
    public async Task Pause()
    {
        await Locked(async () => {
            var o=Store.Options; Store.Options=o with {Paused=!o.Paused};
            await ConfigureAll(); Show(o.Paused ? "Синхронизация возобновлена" : "Синхронизация приостановлена"); Wake();
        });
    }
    private async Task OnMessage(Peer peer,JsonObject msg)
    {
        if (msg["type"]?.ToString() == "hello")
        {
            await peer.Send(new JsonObject{["type"]="lease",["onlineLease"]=lease});
            await peer.Send(Config(peer,Store.Options)); Wake(); return;
        }
        await inbox.Writer.WriteAsync((peer,msg),stop.Token);
    }
    private async Task Incoming()
    {
        await foreach(var (peer,msg) in inbox.Reader.ReadAllAsync(stop.Token))
        {
            try { await Locked(async () => {
                var o = Store.Options;
                if (User == null || peer.Id != o.Browser || !o.Applied || o.Paused) return;
                var type = msg["type"]?.ToString();
                if(msg["epoch"] is { } eventEpoch && eventEpoch.ToString()!=o.Epoch) return;
                if (type == "focus")
                {
                    using var api = Connected();
                    await api.Call("PUT",Route(o)+"/focus",new {windowId=msg["windowId"], workspaceId=msg["workspaceId"],tabId=msg["tabId"]},stop.Token);
                }
                if (type != "changes") return;
                var id = msg["batchId"]?.ToString() ?? "";
                if (!Guid.TryParse(id,out _) || msg["changes"] is not JsonArray records) return;
                 
                if (!Store.Seen(id))
                {
                    var state = Store.Projected(Scope(o));
                    var updates = RecordCodec.Changes(records,state);
                    var body = new JsonObject { ["operationId"]=id,["offline"]=msg["onlineLease"]==null, ["onlineLease"]=msg["onlineLease"]?.DeepClone(), ["changes"]=updates };
                    Store.Enqueue(Scope(o),id,body);
                }
                await peer.Send(new JsonObject { ["type"]="received",["batchId"]=id }); Wake();
            }); } catch (Exception ex) { Show(ex.Message); }
        }
    }
    private async Task Sync()
    {
        var o=Store.Options;
        if (User == null || o.Paused) return;
        if(!o.Applied)
        {
            if(!string.IsNullOrEmpty(o.Cloud) && Store.Get("bootstrap/"+Scope(o))==o.Epoch) await CompleteBootstrap(o);
            return;
        }
        using var api=Connected();
        var scope=Scope(o);
        bool sent = false;
        while (Store.Next(scope) is { } op)
        {
            if (op.Body["changes"] is JsonArray { Count: >0 })
            {
                var receipt=await api.Call("POST",Route(o)+"/changes",op.Body,stop.Token);
                if(receipt["conflicts"] is JsonArray {Count:>0}) {verifyLocal=true;Store.Set("verify/"+scope,"1");Store.Set("cold-verify/"+scope,"1");}
            }
            Store.Done(op.Id);
            sent = true;
        }
        var peer=Current(o);
        var state=(await api.Call("GET",Route(o)+"/state",ct:stop.Token)).AsObject();
         
        var old=Store.State(scope);
        var status=JsonSerializer.Serialize(new{profileId=o.Cloud,browserOnline=peer!=null,lastAppliedRevision=old["revision"]?.GetValue<long>()??0L});
        if(status!=lastDeviceStatus)
        {
            await api.Call("PUT","devices/me/status",JsonNode.Parse(status),stop.Token);
            lastDeviceStatus=status;
        }
        var dirtiness=Volatile.Read(ref prefsDirty);
        var localFolder=Store.Get("folder/"+scope)??"";
        bool browserOpen=peer!=null || (localFolder!="" && PreferencesAdapter.ProfileInUse(localFolder));
        if(!o.Tabs && !browserOpen)
        {
            var folder=Store.Get("folder/"+scope)??"";
            await SyncCold(folder,scope,o,old,state,dirtiness!=0);
            if(Store.Next(scope)!=null) {Wake();return;}
        }
        bool prefsOk;
        string? prefsIssue=null;
        try { prefsOk=await preferences.Run(api,scope,localFolder,Categories(),o,state,dirtiness!=0,browserOpen); }
        catch(Exception ex) when(o.Tabs && ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
        {
            prefsOk=false; prefsIssue=ex.Message;
        }
        if(prefsOk) Interlocked.CompareExchange(ref prefsDirty,0,dirtiness);
        if(!o.Tabs && prefsOk && !browserOpen)
        {
            Store.State(scope,state);
            Show("Пространства и настройки · ревизия "+state["revision"]);return;
        }
        if(Store.Next(scope)!=null) Wake();
        if(!o.Tabs && browserOpen){Show("Выбранные пространства ожидают закрытия Vivaldi");return;}
        if(peer==null){Show(sent?"Изменения отправлены. Ожидание Vivaldi":"Ожидание Vivaldi");return;}
        verifyLocal |= Store.Get("verify/"+scope)=="1";
        if (verifyLocal || (state["revision"]?.GetValue<long>() ?? 0) != (old["revision"]?.GetValue<long>() ?? 0))
        {
            var browserState=verifyLocal?(await peer.Request(new JsonObject{["type"]="snapshot"}))["changes"]!.AsArray():Select(RecordCodec.Records(old),o);
            var delta = RecordCodec.Delta(browserState, Select(RecordCodec.Records(state),o));
            await Apply(peer,delta,false); Store.State(scope,state);
            verifyLocal=false;Store.Set("verify/"+scope,"0");Wake();
        }
        Show("Ревизия "+state["revision"]+(prefsOk?" · синхронизировано":" · "+(prefsIssue??"настройки ожидают закрытия Vivaldi")));
    }
    private static async Task Apply(Peer peer,JsonArray records,bool replace)
    {
        if (!replace && records.Count == 0) return;
        await peer.Request(new JsonObject { ["type"]="apply",["replace"]=replace,["changes"]=records.DeepClone() });
    }
    public async Task LocalRestore() => await Locked(async () => {
        var o = Store.Options;
        var peer = Current(o) ?? throw new InvalidOperationException("Открой подключённый профиль Vivaldi.");
        var backup = Store.BackupFor(Scope(o)) ?? throw new InvalidOperationException("Резервная копия отсутствует.");
        Store.Options = o with {Applied=false,Paused=false,Epoch=Guid.NewGuid().ToString()};
        await ConfigureAll(); await Apply(peer,backup,true);
        Show("Локальная копия восстановлена. Для синхронизации подключи профиль заново.");
    });
    public async Task Continue(string device)
    {
        var o=Store.Options; var peer=Current(o) ?? throw new InvalidOperationException("Открой подключённый профиль Vivaldi.");
        using var api=Connected();
        var focus=(await api.Call("GET",Route(o)+"/focus?deviceId="+Uri.EscapeDataString(device))).AsObject();
        if (focus["tabId"] == null && focus["windowId"] == null) throw new InvalidOperationException("На устройстве пока нет сохранённой вкладки.");
        focus["type"]="focus"; await peer.Request(focus); Show("Открыта вкладка выбранного устройства");
    }
    public void Dispose() { metadata?.Dispose(); eventStop?.Cancel(); stop.Cancel(); }
}
