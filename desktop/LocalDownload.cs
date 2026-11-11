namespace Synvaldi;

internal static class LocalDownload
{
    public static bool Apply(string profile,string directory,bool browserOpen)
    {
        if(string.IsNullOrWhiteSpace(directory)||string.IsNullOrWhiteSpace(profile))return true;
        if(!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("Для загрузок нужен абсолютный путь.");
        string field=Path.Combine(profile,"Preferences");
        var prefs=JsonNode.Parse(File.ReadAllBytes(field))?.AsObject()??throw new InvalidDataException("Неверный файл Preferences.");
        var path=Path.GetFullPath(directory);
        if(string.Equals(prefs["download"]?["default_directory"]?.ToString(),path,StringComparison.OrdinalIgnoreCase))return true;
        if(browserOpen || PreferencesAdapter.ProfileInUse(profile))return false;
        (prefs["download"]??=new JsonObject())["default_directory"]=path;
        var temp=field+".synvaldi."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(output,prefs);output.Flush(true);}
            if(PreferencesAdapter.ProfileInUse(profile))return false;
            File.Replace(temp,field,field+".synvaldi.downloads.bak");
            return true;
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}

