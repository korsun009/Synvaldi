using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]
const string allowedOrigin = "chrome-extension://jcfipkamaklpdghopicgfllhnigfjcio/";
if (args.Length == 0 || args[0] != allowedOrigin) return 2;
try
{
    using var pipe = new NamedPipeClientStream(".", "Synvaldi.Agent.v1", PipeDirection.InOut, PipeOptions.Asynchronous);
    try { await pipe.ConnectAsync(500); }
    catch (TimeoutException)
    {
        var agent = Path.Combine(AppContext.BaseDirectory,"Synvaldi.exe");
        if (!File.Exists(agent)) agent=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Synvaldi.exe"));
        if (!File.Exists(agent)) return 3;
        Process.Start(new ProcessStartInfo(agent, "--tray") { UseShellExecute = false });
        await pipe.ConnectAsync(8000);
    }
    using var cancel = new CancellationTokenSource();
    var inbound = Console.OpenStandardInput().CopyToAsync(pipe, cancel.Token);
    var outbound = pipe.CopyToAsync(Console.OpenStandardOutput(), cancel.Token);
    await await Task.WhenAny(inbound, outbound);
    cancel.Cancel();
    return 0;
}
catch (IOException) { return 4; }
catch (TimeoutException) { return 5; }
catch (UnauthorizedAccessException) { return 6; }