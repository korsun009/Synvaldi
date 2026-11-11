using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Synvaldi;

internal partial class MainView : Window
{
    private readonly Agent agent;
    private string page="";
    private int generation;
    private string? wizardCloud;
    private static readonly string[] pages=["Синхронизация","Пространства","Устройства","История","Настройки","Аккаунт"];
    private static readonly Brush accent=new SolidColorBrush(Color.FromRgb(39,87,71));
    internal MainView(Agent agent)
    {
        this.agent=agent;
        InitializeComponent();
        foreach(var name in pages)
        {
            var button=new Button{Content=name, Tag=name,HorizontalAlignment=HorizontalAlignment.Stretch, HorizontalContentAlignment=HorizontalAlignment.Left,
                Background=Brushes.Transparent,Margin=new Thickness(0,2,0,2),Padding=new Thickness(10,10,8,10)};
            button.Click+=(_,_)=>Navigate(name); Nav.Children.Add(button);
        }
        agent.Changed+=()=>Dispatcher.BeginInvoke(()=>{Status.Text=agent.Status; Status.ToolTip=agent.Status;});
        Closing+=(_,args)=>{ if (IsVisible) { args.Cancel=true; Hide(); } };
        Status.Text=agent.Status;
        Navigate(agent.User==null?"Подключение":"Синхронизация");
    }
    internal void Navigate(string target)
    {
        page=target; int epoch=++generation; Body.Children.Clear(); Scroll.ScrollToTop(); Heading.Text=target;
        foreach(var button in Nav.Children.OfType<Button>())
        {
            bool active=target==(string)button.Tag || target=="Подключение" && (string)button.Tag=="Аккаунт";
            button.Background=active?accent:Brushes.Transparent; button.Foreground=active?Brushes.White:Foreground;
        }
        if (target=="Настройки") { Settings(); return; }
        if (agent.User==null)
        {
            if(target is "Подключение" or "Аккаунт") Login();
            else { var card=Card("Аккаунт Synvaldi"); Note(card,"Подключи свой сервер, чтобы открыть этот раздел."); Action(card,"Подключиться",()=>Navigate("Подключение"),true); }
            return;
        }
        switch(target)
        {
            case "Синхронизация": Dashboard(); break;
            case "Аккаунт": AccountPage(); break;
            case "Подключение": Load(Wizard(epoch)); break;
            case "Пространства": Load(Spaces(epoch)); break;
            case "Устройства": Load(Devices(epoch)); break;
            case "История": Load(History(epoch)); break;
        }
    }
    private async void Load(Task action)
    {
        try { await action; } catch(Exception ex) { Status.Text=Friendly(ex); }
    }
    private static string Friendly(Exception ex) => ex is ApiError api ? api.Message switch {
        "invalid_credentials"=>"Проверь логин и пароль.",
        "username_taken"=>"Этот логин уже занят.",
        "seed_reserved"=>"Облачный профиль должен впервые заполнить создавший его компьютер.",
        _=>"Сервер отклонил запрос · "+api.Message } : ex.Message;
    private StackPanel Card(string title)
    {
        var panel=new StackPanel();
        var frame=new Border{Background=new SolidColorBrush(Color.FromRgb(255,253,246)),BorderBrush=new SolidColorBrush(Color.FromRgb(217,222,208)),
            BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(18,14,18,15),Margin=new Thickness(0,0,0,10),Child=panel};
        Body.Children.Add(frame);
        if(title!="") panel.Children.Add(new TextBlock{Text=title,FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,5)});
        return panel;
    }
    private static void Note(Panel panel,string text) => panel.Children.Add(new TextBlock{Text=text,Foreground=new SolidColorBrush(Color.FromRgb(75,93,84)),Margin=new Thickness(0,2,0,6)});
    private static T Label<T>(Panel panel,string title,T field) where T:Control
    {
        var label=new Label{Content=title, Target=field,Padding=new Thickness(0,3,0,0),FontWeight=FontWeights.Medium};
        AutomationProperties.SetName(field,title); panel.Children.Add(label); panel.Children.Add(field); return field;
    }
    private TextBox Input(Panel p,string title,string value="") => Label(p,title,new TextBox{Text=value});
    private ComboBox Combo(Panel p,string title,IEnumerable<(string Id,string Name)> rows,string? current=null)
    {
        var box=Label(p,title,new ComboBox());
        foreach(var (id,name) in rows) box.Items.Add(new ComboBoxItem{Content=name,Tag=id,ToolTip=name});
        box.SelectedIndex=Math.Max(0,box.Items.Cast<ComboBoxItem>().ToList().FindIndex(x=>(string)x.Tag==current));
        return box;
    }
    private static string Id(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private CheckBox Check(Panel p,string label,bool selected) { var check=new CheckBox{Content=label,IsChecked=selected}; p.Children.Add(check); return check; }
    private Button Action(Panel p,string name,Action invoke,bool primary=false) => TaskButton(p,name,()=>{invoke();return Task.CompletedTask;},primary);
    private Button TaskButton(Panel p,string name,Func<Task> invoke,bool primary=false)
    {
        var b=new Button{Content=name}; if(primary) {b.Background=accent;b.Foreground=Brushes.White;} p.Children.Add(b);
        b.Click+=async (_,_)=>{ b.IsEnabled=false; try { await invoke(); } catch(Exception ex) { Status.Text=Friendly(ex); } finally {b.IsEnabled=true;} }; return b;
    }
    private bool Confirm(string text) => MessageBox.Show(this,text,"Synvaldi",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
    private static string LocalTime(JsonNode? text) => DateTimeOffset.TryParse(text?.ToString(),out var t)?t.ToLocalTime().ToString("dd.MM.yyyy HH:mm"):"—";
    private void Login()
    {
        var c=Card("Вход на сервер");
        var server=Input(c,"Адрес сервера",agent.Store.Get("last-server") ?? ConnectionPolicy.Read()?.Server ?? "");
        var user=Input(c,"Логин"); var pwd=Label(c,"Пароль",new PasswordBox());
        var device=Input(c,"Имя этого устройства",Environment.MachineName);
        var register=Check(c,"Создать новый аккаунт",false);
        TaskButton(c,"Подключиться",async()=>{await agent.Login(server.Text.Trim(),user.Text.Trim(),pwd.Password,device.Text.Trim(),register.IsChecked==true);
            pwd.Clear(); agent.Store.Set("last-server",server.Text.Trim()); Navigate("Подключение");},true);
    }
    private void Dashboard()
    {
        var o=agent.Store.Options;
        var c=Card("Текущее устройство");
        Note(c,agent.User!.User+"   ·   "+agent.User.Server);
        Note(c,string.IsNullOrEmpty(o.Cloud)?"Профиль ещё не выбран":o.Applied?"Облачный профиль подключён":"Первоначальная настройка");
        Note(c,"Vivaldi · "+(agent.Bridge.Peers.Count>0?"расширение подключено":"ожидание расширения"));
        TaskButton(c,o.Paused?"Возобновить синхронизацию":"Приостановить синхронизацию",async()=>{await agent.Pause();Navigate(page);},true);
        var setup=Card("Подключение профиля"); Action(setup,"Открыть мастер",()=>Navigate("Подключение"));
        Action(setup,"Продолжить с устройства…",()=>Navigate("Устройства"));
        if (!string.IsNullOrEmpty(o.Cloud)) TaskButton(setup,"Восстановить локальную копию",async()=>{if(Confirm("Вернуть вкладки из резервной копии на этом ПК и остановить отправку?")) await agent.LocalRestore();});
    }
    private async Task<JsonArray> GetArray(string route)
    {
        using var api=agent.Connected(); return (await api.Call("GET",route)).AsArray();
    }
    private async Task Wizard(int epoch)
    {
        var profiles=await GetArray("profiles");
        if (epoch!=generation) return;
        var o=agent.Store.Options;
        var c=Card("Облачный профиль");
        var cloud=Combo(c,"Выбери профиль Synvaldi",profiles.OfType<JsonObject>().Select(x=>(x["id"]!.ToString(),x["name"]!.ToString())),wizardCloud ?? o.Cloud);
        var newName=Input(c,"Имя нового профиля");
        TaskButton(c,"Создать профиль",async()=>{using var api=agent.Connected();var profile=await api.Call("POST","profiles",new{name=newName.Text.Trim()});wizardCloud=profile["id"]?.ToString();Navigate(page);});
        c=Card("Профиль на этом ПК");
        var peers=agent.Bridge.Peers.Values.OrderBy(x=>x.Id).Select(x=>(x.Id,x.Hello["profileName"]?.ToString() ?? ("Vivaldi · "+x.Id[..8])));
        var browser=Combo(c,"Открытый профиль Vivaldi",peers,o.Browser);
        if(browser.Items.Count==0)
        {
            Note(c,"Открой vivaldi://extensions, включи режим разработчика и загрузи папку расширения Synvaldi. Затем обнови список.");
            Action(c,"Открыть папку расширения",()=>{var directory=Path.Combine(AppContext.BaseDirectory,"extension");
                if(!Directory.Exists(directory)) throw new DirectoryNotFoundException("Папка расширения отсутствует в этой сборке.");
                Process.Start(new ProcessStartInfo(directory){UseShellExecute=true});});
        }
        Action(c,"Обновить список",()=>{wizardCloud=Id(cloud);Navigate(page);});
        var folder=Input(c,"Каталог локального профиля Vivaldi",string.IsNullOrEmpty(o.Cloud)?"":agent.Store.Get("folder/"+agent.User!.AccountId+"/"+o.Cloud+"/"+o.Browser) ?? "");
        Action(c,"Выбрать каталог…",()=>Folder(folder));
        c=Card("Данные для подключения");
        var all=Check(c,"Синхронизировать все обычные вкладки этого профиля",o.Tabs);
        Note(c,"По умолчанию на новом ПК ни одно пространство не закреплено. Режим всех вкладок затрагивает обычные окна выбранного профиля.");
        var localSpaces=new StackPanel(); var marked=new List<(string Id,CheckBox Box)>();
        TaskButton(c,"Показать пространства закрытого профиля",async()=>{
            var catalogue=await Task.Run(()=>Agent.Catalogue(folder.Text.Trim()));
            marked.Clear(); localSpaces.Children.Clear();
            foreach(var ws in catalogue.OfType<JsonObject>())
            {
                var id=ws["id"]!.ToString();marked.Add((id,Check(localSpaces,"Импортировать · "+(ws["name"]?.ToString()??id),false)));
            }
            if(marked.Count==0) Note(localSpaces,"В локальном профиле нет именованных пространств.");
        });
        c.Children.Add(localSpaces);
        Note(c,"Для выборочного переноса закрой Vivaldi. Выбранные здесь пространства будут импортированы в облако. На новом ПК Synvaldi сохранит резервную копию и отсоединит локальные пространства. Закрепить нужные можно отдельно в разделе «Пространства».");
        var seed=Check(c,"Заполнить пустое облако данными этого ПК",false);
        
        TaskButton(c,"Применить подключение",async()=>{
            if(all.IsChecked==true && !Confirm("Сохранить локальную копию и применить облачные вкладки к обычным окнам этого профиля?")) return;
            if(all.IsChecked==true) await agent.Bind(Id(cloud),Id(browser),true,[],folder.Text.Trim(),seed.IsChecked==true);
            else if(!string.IsNullOrWhiteSpace(folder.Text)) await agent.BindCold(Id(cloud),Id(browser),folder.Text.Trim(),o.Cloud==Id(cloud) && o.Applied ? o.Workspaces??[] : [],seed.IsChecked==true,marked.Where(x=>x.Box.IsChecked==true).Select(x=>x.Id).ToArray());
            else await agent.Bind(Id(cloud),Id(browser),false,[],"",false);
            wizardCloud=null; Navigate("Синхронизация");},true);
    }
    private async Task Spaces(int epoch)
    {
        var opts=agent.Store.Options;
        if(string.IsNullOrEmpty(opts.Cloud)) {var c=Card("Облачный профиль");Action(c,"Подключить профиль",()=>Navigate("Подключение"),true);return;}
        using var api=agent.Connected();
        var state=(await api.Call("GET","profiles/"+opts.Cloud+"/state")).AsObject();
        if (epoch!=generation) return;
        var card=Card("Закрепление на этом ПК");
        Note(card,"Снятая отметка закроет это пространство на данном ПК после применения. Облачная копия сохранится. Перед записью Synvaldi создаёт локальную резервную копию.");
        var boxes=new List<(string Id,CheckBox Check)>();
        foreach(var ws in RecordCodec.Records(state).OfType<JsonObject>().Where(x=>x["kind"]?.ToString()=="workspace"))
        {
            var id=ws["value"]?["workspaceId"]?.ToString() ?? ws["id"]!.ToString();
            boxes.Add((id,Check(card,ws["value"]?["name"]?.ToString() ?? id,opts.Workspaces?.Contains(id)==true)));
        }
        foreach(var (key,field) in state["fields"]?.AsObject() ?? [])
        {
            if(!key.StartsWith("workspace-cold/") || field?["deleted"]?.GetValue<bool>()==true)continue;
            var id=key["workspace-cold/".Length..];var name=field?["value"]?["name"]?.ToString()??id;
            boxes.Add((id,Check(card,name,opts.Workspaces?.Contains(id)==true)));
            TaskButton(card,"В корзину · "+name,async()=>{if(!Confirm("Переместить пространство «"+name+"» со всеми вкладками в корзину для всех устройств?"))return;
                using var online=agent.Connected();await online.Call("POST","profiles/"+opts.Cloud+"/trash",new{operationId=Guid.NewGuid(),name,keys=new[]{key}});Navigate(page);});
        }
        if(boxes.Count==0) Note(card,"В облачном профиле пока нет доступных пространств.");
        else TaskButton(card,"Сохранить на этом ПК",async()=>{await agent.BindCold(opts.Cloud,opts.Browser,agent.Store.Get("folder/"+agent.User!.AccountId+"/"+opts.Cloud+"/"+opts.Browser) ?? "",boxes.Where(x=>x.Check.IsChecked==true).Select(x=>x.Id).ToArray(),false,[]);Navigate(page);},true);
        var trash=await GetArray("profiles/"+opts.Cloud+"/trash");if(epoch!=generation)return;
        var bin=Card("Корзина · 30 дней");
        if(trash.Count==0)Note(bin,"Корзина пуста.");
        foreach(var item in trash.OfType<JsonObject>())
        {
            var id=item["id"]!.ToString();Note(bin,item["name"]+" · "+LocalTime(item["createdAt"]));
            TaskButton(bin,"Вернуть «"+item["name"]+"»",async()=>{using var online=agent.Connected();await online.Call("POST","profiles/"+opts.Cloud+"/trash/"+id+"/restore",new{operationId=Guid.NewGuid()});Navigate(page);});
        }
    }
    private async Task Devices(int epoch)
    {
        var devices=await GetArray("devices"); var profiles=await GetArray("profiles"); if(epoch!=generation)return;
        foreach(var item in devices.OfType<JsonObject>())
        {
            string id=item["id"]!.ToString(); bool own=id==agent.User!.Device;
            var c=Card(item["name"]!.ToString()+(own?"  ·  этот ПК":"")); Note(c,"Последняя связь · "+LocalTime(item["lastSeen"]));
            Note(c,(item["online"]?.GetValue<bool>()==true?"В сети":"Нет связи")+"   ·   Vivaldi "+(item["browserOnline"]?.GetValue<bool>()==true?"открыт":"закрыт")+"   ·   ревизия "+(item["lastAppliedRevision"]?.ToString()??"—"));
            var rename=Input(c,"Имя устройства",item["name"]!.ToString());
            TaskButton(c,"Сохранить имя",async()=>{using var api=agent.Connected();await api.Call("PATCH","devices/"+id,new{name=rename.Text.Trim()}); Navigate(page);});
            if(!own)
            {
                TaskButton(c,"Продолжить с этого устройства",()=>agent.Continue(id),true);
                TaskButton(c,"Отозвать доступ",async()=>{if(!Confirm("Отозвать доступ этого устройства к аккаунту?"))return; using var api=agent.Connected();await api.Call("DELETE","devices/"+id);Navigate(page);});
            }
        }
        foreach(var obj in profiles.OfType<JsonObject>())
        {
            var c=Card("Профиль · "+obj["name"]); var rename=Input(c,"Имя профиля",obj["name"]!.ToString());
            TaskButton(c,"Переименовать профиль",async()=>{using var api=agent.Connected();await api.Call("PATCH","profiles/"+obj["id"],new{name=rename.Text.Trim()});Navigate(page);});
        }
    }
    private async Task History(int epoch)
    {
        var o=agent.Store.Options;
        if(string.IsNullOrEmpty(o.Cloud)) {var c=Card("История за 72 часа");Note(c,"Выбери облачный профиль в мастере подключения.");return;}
        await Snapshots(o,epoch); if(epoch!=generation)return;
        var versions=await GetArray("profiles/"+o.Cloud+"/history");if(epoch!=generation)return;
        if(versions.Count==0) {Note(Card("История за 72 часа"),"Ревизий пока нет.");return;}
        foreach(var entry in versions.OfType<JsonObject>())
        {
            var c=Card("Ревизия "+entry["revision"]);Note(c,LocalTime(entry["createdAt"])+"   ·   "+entry["deviceName"]);
            if(entry["changedKeys"] is JsonArray keys) Note(c,"Изменено полей: "+entry["changedCount"]+" · "+string.Join(", ",keys.Take(3).Select(x=>x?.ToString())));
            TaskButton(c,"Посмотреть ревизию",async()=>{using var api=agent.Connected();var data=await api.Call("GET","profiles/"+o.Cloud+"/history/"+entry["revision"]);
                var details=new Window{Title="Ревизия "+entry["revision"],Owner=this,Width=680,Height=460,WindowStartupLocation=WindowStartupLocation.CenterOwner};
                details.Content=new TextBox{Text=data.ToJsonString(new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};details.ShowDialog();});
            TaskButton(c,"Восстановить эту ревизию",async()=>{if(!Confirm("Восстановить эту версию облачного профиля для всех устройств?"))return;
                using var api=agent.Connected();await api.Call("POST","profiles/"+o.Cloud+"/rollback",new{operationId=Guid.NewGuid(),revision=entry["revision"]!.GetValue<long>()});Navigate(page);});
        }
    }
    private async Task Snapshots(Options o,int epoch)
    {
        var path="profiles/"+o.Cloud+"/snapshots";
        var items=await GetArray(path);if(epoch!=generation)return;
        var c=Card("Сохранённые снимки");var name=Input(c,"Название снимка");
        TaskButton(c,"Создать снимок",async()=>{using var api=agent.Connected();await api.Call("POST",path,new{name=name.Text.Trim()});Navigate(page);});
        foreach(var entry in items.OfType<JsonObject>())
        {
            Note(c,entry["name"]+" · "+LocalTime(entry["createdAt"])+" · ревизия "+entry["revision"]);
            var itemPath=path+"/"+entry["id"];
            TaskButton(c,"Восстановить «"+entry["name"]+"»",async()=>{if(!Confirm("Восстановить сохранённый снимок для всех устройств?"))return;using var api=agent.Connected();await api.Call("POST",itemPath+"/restore",new{operationId=Guid.NewGuid()});Navigate(page);});
            TaskButton(c,"Удалить снимок «"+entry["name"]+"»",async()=>{if(!Confirm("Удалить этот сохранённый снимок?"))return;using var api=agent.Connected();await api.Call("DELETE",itemPath);Navigate(page);});
        }
    }
    private static void Folder(TextBox input)
    {
        var dialog=new OpenFolderDialog { Title="Выбор каталога" };
        if (Directory.Exists(input.Text)) dialog.InitialDirectory=input.Text;
        if(dialog.ShowDialog()==true) input.Text=dialog.FolderName;
    }
    private void Settings()
    {
        var o=agent.Store.Options;
        var c=Card("Расположение файлов"); var downloads=Input(c,"Локальная папка загрузок",o.Downloads); Action(c,"Выбрать папку загрузок…",()=>Folder(downloads));
        var attachments=Input(c,"Локальная папка вложений",o.Attachments);Action(c,"Выбрать папку вложений…",()=>Folder(attachments));
        var prefs=Card("Переносимые категории"); var cats=agent.Categories().ToHashSet();
        var settings=Check(prefs,"Настройки браузера",cats.Contains("settings"));var appearance=Check(prefs,"Оформление",cats.Contains("appearance"));var assets=Check(prefs,"Вложения",cats.Contains("attachments"));
        Action(prefs,"Сохранить настройки",()=>{
            foreach(var path in new[]{downloads.Text,attachments.Text}) if(path.Length>0 && !Path.IsPathFullyQualified(path)) throw new ArgumentException("Укажи полный путь к каталогу.");
            agent.Store.Options=agent.Store.Options with {Downloads=downloads.Text.Trim(),Attachments=attachments.Text.Trim()};
            agent.SetCategories(new[]{("settings",settings),("appearance",appearance),("attachments",assets)}.Where(x=>x.Item2.IsChecked==true).Select(x=>x.Item1).ToArray());
            Status.Text="Настройки этого устройства сохранены";},true);
        c=Card("Приложение");
        using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var startup=Check(c,"Запускать Synvaldi при входе в Windows",key?.GetValue("Synvaldi")!=null);
        startup.Click+=(_,_)=>{using var k=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if(startup.IsChecked==true) k.SetValue("Synvaldi","\""+Environment.ProcessPath+"\" --tray");else k.DeleteValue("Synvaldi",false);};
        Action(c,"Проверить обновления",()=>{if(!App.Update(true))throw new FileNotFoundException("Модуль обновлений отсутствует.");});
    }
    private void AccountPage()
    {
        var c=Card(agent.User!.User); Note(c,agent.User.Server); Action(c,"Мастер подключения",()=>Navigate("Подключение"));
        var sec=Card("Смена пароля"); var old=Label(sec,"Текущий пароль",new PasswordBox());var next=Label(sec,"Новый пароль",new PasswordBox());
        TaskButton(sec,"Изменить пароль",async()=>{using var api=agent.Connected();await api.Call("POST","auth/password",new{currentPassword=old.Password,newPassword=next.Password});old.Clear();next.Clear(); Status.Text="Пароль изменён";});
        TaskButton(c,"Выйти из аккаунта",async()=>{if(Confirm("Завершить сессию этого устройства?")) {await agent.Logout();Navigate("Подключение");}});
    }
}

