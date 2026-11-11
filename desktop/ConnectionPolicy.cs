using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Synvaldi;

internal sealed record ConnectionConfig(string Server, string CertificateSha256);
internal static class ConnectionPolicy
{
    internal static ConnectionConfig? Read(string? file=null)
    {
        file??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Synvaldi","connection.json");
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<ConnectionConfig>(File.ReadAllBytes(file),new JsonSerializerOptions { PropertyNameCaseInsensitive=true }); }
        catch(JsonException) { return null; }
    }
    internal static HttpClientHandler Handler(Uri server) => Handler(server,Read());
    internal static HttpClientHandler Handler(Uri server,ConnectionConfig? config)
    {
        var handler=new HttpClientHandler { AllowAutoRedirect=false };
        if(config==null || !Uri.TryCreate(config.Server,UriKind.Absolute,out var bound) ||
            Uri.Compare(server,bound,UriComponents.SchemeAndServer,UriFormat.SafeUnescaped,StringComparison.OrdinalIgnoreCase)!=0) return handler;
        byte[] fingerprint;
        try { fingerprint=Convert.FromHexString(config.CertificateSha256); } catch(Exception ex) when(ex is ArgumentException or FormatException) { return handler; }
        if(fingerprint.Length!=32) return handler;
        handler.UseProxy=false;
        handler.ServerCertificateCustomValidationCallback=(request,certificate,_,errors)=>{
            if(errors==SslPolicyErrors.None) return true;
            if(certificate==null || errors!=SslPolicyErrors.RemoteCertificateChainErrors || request.RequestUri==null ||
                Uri.Compare(request.RequestUri,bound,UriComponents.SchemeAndServer,UriFormat.SafeUnescaped,StringComparison.OrdinalIgnoreCase)!=0) return false;
            var now=DateTime.UtcNow;
            return now>=certificate.NotBefore.ToUniversalTime() && now<=certificate.NotAfter.ToUniversalTime() &&
                CryptographicOperations.FixedTimeEquals(fingerprint,certificate.GetCertHash(HashAlgorithmName.SHA256));
        };
        return handler;
    }
}

