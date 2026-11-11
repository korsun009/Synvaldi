using System.Diagnostics;
using System.Drawing;
using System.Security.Principal;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Synvaldi;

public partial class App : Application
{
    private Agent? agent;
    private MainView? view;
    private Forms.NotifyIcon? tray;
    private Mutex? mutex;
    private EventWaitHandle? activate, quit, setup;
    private RegisteredWaitHandle? openWait, quitWait, setupWait;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string prefix = "Local\\Synvaldi." + WindowsIdentity.GetCurrent().User!.Value;
        mutex = new Mutex(true,prefix+".instance",out var first);
        bool exit = e.Args.Contains("--quit");
        if (!first || exit)
        {
            if (!first && (exit || e.Args.Contains("--setup") || e.Args.Contains("--open")) && EventWaitHandle.TryOpenExisting(prefix+(exit?".quit":e.Args.Contains("--setup")?".setup":".open"),out var ev))
                using(ev) ev.Set();
            if(!first && exit)
            {
                bool ended;
                try { ended=mutex.WaitOne(TimeSpan.FromSeconds(12)); } catch(AbandonedMutexException) {ended=true;}
                if(ended)mutex.ReleaseMutex();
                Shutdown(ended?0:2);return;
            }
            Shutdown(); return;
        }
        activate=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+".open");
        quit=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+".quit");
        setup=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+".setup");
        setupWait=ThreadPool.RegisterWaitForSingleObject(setup,(_,_)=>Dispatcher.BeginInvoke(()=>Open("Подключение")),null,-1,false);
        openWait=ThreadPool.RegisterWaitForSingleObject(activate,(_,_)=>Dispatcher.BeginInvoke(()=>Open()),null,-1,false);
        quitWait=ThreadPool.RegisterWaitForSingleObject(quit,(_,_)=>Dispatcher.BeginInvoke(()=>Shutdown()),null,-1,false);
        var store = new LocalStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synvaldi"));
        agent=new Agent(store);
        var menu=new Forms.ContextMenuStrip();
        var status=menu.Items.Add("Synvaldi"); status.Enabled=false;
        menu.Items.Add("Открыть Synvaldi",null,(_,_)=>Open());
        var pause=menu.Items.Add("Приостановить",null,async (_,_)=>{try { await agent.Pause(); } catch(Exception ex) { agent.Show(ex.Message); }});
        menu.Items.Add("Продолжить с устройства…",null,(_,_)=>Open("Устройства"));
        menu.Items.Add("Настройки",null,(_,_)=>Open("Настройки"));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Выход",null,(_,_)=>Shutdown());
        using var stream=GetResourceStream(new Uri("Assets/app.ico",UriKind.Relative)).Stream;
        tray=new Forms.NotifyIcon { Icon=new Icon(stream),Text="Synvaldi",ContextMenuStrip=menu,Visible=true };
        tray.DoubleClick+=(_,_)=>Open();
        menu.Opening+=(_,_)=>{status.Text=agent.Status; pause.Text=store.Options.Paused?"Возобновить":"Приостановить";};
        agent.Start();
        if(e.Args.Contains("--setup"))Open("Подключение"); else if (e.Args.Contains("--open") || (agent.User==null && !e.Args.Contains("--tray"))) Open();
        if (DateTime.TryParse(store.Get("update-check"),out var checkedAt) && DateTime.UtcNow-checkedAt<TimeSpan.FromDays(1)) return;
        if (Update(false)) store.Set("update-check",DateTime.UtcNow.ToString("o"));
    }
    private void Open(string? page=null)
    {
        if (agent==null) return;
        view??=new MainView(agent);
        if(page!=null) view.Navigate(page);
        view.Show(); if (view.WindowState==WindowState.Minimized) view.WindowState=WindowState.Normal;
        view.Activate();
    }
    internal static bool Update(bool interactive)
    {
        var exe=Path.Combine(AppContext.BaseDirectory,"Synvaldi.Update.exe");
        if(!File.Exists(exe))exe=Path.Combine(AppContext.BaseDirectory,"updater","Synvaldi.Update.exe");
        if (!File.Exists(exe)) return false;
        try { Process.Start(new ProcessStartInfo(exe,interactive?"--interactive":"--background"){UseShellExecute=false}); return true; }
        catch (Exception) when (!interactive) { return false; }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose(); agent?.Dispose(); openWait?.Unregister(null); quitWait?.Unregister(null);
        setupWait?.Unregister(null); setup?.Dispose(); activate?.Dispose(); quit?.Dispose(); mutex?.Dispose(); base.OnExit(e);
    }
}

