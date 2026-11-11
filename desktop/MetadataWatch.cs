namespace Synvaldi;

// Browser file notifications are coalesced before the preference adapter reads the profile.
internal sealed class MetadataWatch : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly Timer debounce;
    public MetadataWatch(string folder,Action changed)
    {
        debounce=new Timer(_=>changed());
        watcher=new FileSystemWatcher(folder){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.FileName};
        foreach(var name in new[]{"Preferences","Notes","Bookmarks","SyncedFilesData","file_mapping.json","Session_*"}) watcher.Filters.Add(name);
        watcher.Changed+=Notify; watcher.Created+=Notify; watcher.Renamed+=Notify; watcher.Deleted+=Notify;
        watcher.EnableRaisingEvents=true;
    }
    private void Notify(object sender,FileSystemEventArgs args)
    {
        try { debounce.Change(650,Timeout.Infinite); } catch(ObjectDisposedException) {}
    }
    public void Dispose() { watcher.Dispose(); debounce.Dispose(); }
}