using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Synvaldi;

internal sealed class ApiError(HttpStatusCode status, string code) : Exception(code)
{
    public HttpStatusCode Status { get; } = status;
}
internal sealed class Api : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string? token;
    public Api(string server, string? token = null)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("Укажи адрес HTTPS. Для localhost также поддерживается HTTP.");
        client = new HttpClient(ConnectionPolicy.Handler(uri)) { Timeout=Timeout.InfiniteTimeSpan };
        endpoint = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/api/v1/");
        this.token = token;
    }
    private HttpRequestMessage Request(HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint, route));
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    public async Task<JsonNode> Call(string method, string route, object? body = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = Request(new HttpMethod(method), route);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var result = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var raw = await result.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!result.IsSuccessStatusCode)
        {
            string? code = null;
            try { code = JsonNode.Parse(raw)?["error"]?.ToString(); } catch (JsonException) {}
            throw new ApiError(result.StatusCode, code ?? "HTTP " + (int)result.StatusCode);
        }
        return string.IsNullOrWhiteSpace(raw) ? new JsonObject() : JsonNode.Parse(raw) ?? new JsonObject();
    }
    public async Task Upload(string digest,byte[] bytes)
    {
        using var request=Request(HttpMethod.Put,"blobs/"+digest);
        request.Content=new ByteArrayContent(bytes);
        request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
        using var result=await client.SendAsync(request);
        result.EnsureSuccessStatusCode();
    }
    public async Task<byte[]> Download(string digest)
    {
        using var request=Request(HttpMethod.Get,"blobs/"+digest);
        using var result=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
        result.EnsureSuccessStatusCode();
        await result.Content.LoadIntoBufferAsync(32L*1024*1024);
        return await result.Content.ReadAsByteArrayAsync();
    }
    public async Task Events(Action<JsonObject> onEvent, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "events");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ApiError(response.StatusCode, "HTTP " + (int)response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            if (line.StartsWith("data:", StringComparison.Ordinal) && JsonNode.Parse(line[5..].Trim()) is JsonObject evt) onEvent(evt);
        throw new IOException("Поток уведомлений завершён.");
    }
    public void Dispose() => client.Dispose();
}
