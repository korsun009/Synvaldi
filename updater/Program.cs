using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    const string Repository = "https://github.com/korsun009/Synvaldi";
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(6) };
    static readonly string Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synvaldi","Updates");

    [STAThread]
    static void Main(string[] args)
    {
        bool interactive = args.Contains("--interactive");
        using var mutex = new Mutex(true, @"Local\Synvaldi.ReleaseUpdate", out bool acquired);
        if (!acquired) return;
        try
        {
            Directory.CreateDirectory(Cache);
            string stamp = Path.Combine(Cache,"last-check");
            if (!interactive && File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromDays(1)) return;
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Synvaldi-Update/0.1");
            Check(interactive).GetAwaiter().GetResult();
            File.WriteAllText(stamp, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            if (interactive) MessageBox.Show("Проверка обновления: " + ex.Message, "Synvaldi");
        }
    }

    static async Task Check(bool interactive)
    {
        using var response = await Client.GetAsync("https://api.github.com/repos/korsun009/Synvaldi/releases/latest");
        if(response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if(interactive) MessageBox.Show("Опубликованных обновлений пока нет.","Synvaldi");
            return;
        }
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        string tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..],out var available) || tag != $"v{available}" || available.Build < 0)
            throw new InvalidDataException("Некорректная версия выпуска.");
        if (available <= Assembly.GetExecutingAssembly().GetName().Version)
        {
            if(interactive) MessageBox.Show("Установлена актуальная версия.","Synvaldi");
            return;
        }
        string url = $"{Repository}/releases/download/{tag}/";
        byte[] bytes = await Client.GetByteArrayAsync(url + "update.json");
        byte[] signature = await Client.GetByteArrayAsync(url + "update.sig");
        VerifySignature(bytes,signature);
        var info = JsonSerializer.Deserialize<Manifest>(bytes) ?? throw new InvalidDataException();
        if(info.Version != available.ToString() || info.File != $"Synvaldi-{available}.exe" || info.Length <= 0 || info.Length > 1073741824L)
            throw new InvalidDataException("Параметры дистрибутива не соответствуют выпуску.");
        if(MessageBox.Show($"Установить Synvaldi {available}?","Обновление",MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        string target = Path.Combine(Cache,info.File);
        string temp = target + ".part";
        try
        {
            using var download = await Client.GetAsync(url + info.File,HttpCompletionOption.ResponseHeadersRead);
            download.EnsureSuccessStatusCode();
            await using(var output = File.Create(temp))
            {
                using var input = await download.Content.ReadAsStreamAsync();
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    if(output.Length + read > info.Length) throw new InvalidDataException("Размер дистрибутива превышен.");
                    await output.WriteAsync(buffer.AsMemory(0,read));
                }
                if(output.Length != info.Length) throw new InvalidDataException("Неполная загрузка.");
            }
            using(var file = File.OpenRead(temp))
                if(!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file), Convert.FromHexString(info.Sha256)))
                    throw new InvalidDataException("Контрольная сумма не совпадает.");
            File.Move(temp,target,true);
            Process.Start(new ProcessStartInfo(target){UseShellExecute=true});
        }
        finally { File.Delete(temp); }
    }
    internal static void VerifySignature(byte[] bytes,byte[] signature)
    {
        using var key = RSA.Create();
        using var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("release-public.xml")!);
        key.FromXmlString(reader.ReadToEnd());
        if (!key.VerifyData(bytes,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Подлинность обновления не подтверждена.");
    }
    private sealed record Manifest(string Version,string File,long Length,string Sha256);
}
