using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Synvaldi;

internal static class Frames
{
    public static async Task<JsonObject?> Read(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0,1), ct) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Недопустимый размер сообщения.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("Ожидался объект.");
    }
    public static async Task Write(Stream stream, JsonObject obj, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(obj);
        if (body.Length > 1024 * 1024) throw new InvalidDataException("Пакет превышает лимит Native Messaging (1 МиБ).");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
}
internal sealed class Peer(NamedPipeServerStream pipe, JsonObject hello, CancellationToken ct)
{
    private readonly SemaphoreSlim write = new(1, 1);
    private readonly ConcurrentDictionary<string,TaskCompletionSource<JsonObject>> awaiting = new();
    public string Id { get; } = hello["profileId"]!.ToString();
    public JsonObject Hello => hello;
    public async Task Send(JsonObject obj)
    {
        await write.WaitAsync(ct);
        try { await Frames.Write(pipe, obj, ct); }
        finally { write.Release(); }
    }
    public async Task<JsonObject> Request(JsonObject obj)
    {
        var id = Guid.NewGuid().ToString();
        obj["batchId"] = id;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        awaiting[id] = tcs;
        try
        {
            await Send(obj);
            var reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            if (reply["ok"]?.GetValue<bool>() == false) throw new InvalidOperationException(reply["error"]?.ToString() ?? "Браузер отклонил операцию.");
            return reply;
        }
        finally { awaiting.TryRemove(id, out _); }
    }
    public bool Reply(JsonObject obj) => obj["type"]?.ToString() is "ack" or "snapshot"
        && awaiting.TryGetValue(obj["batchId"]?.ToString() ?? "", out var tcs) && tcs.TrySetResult(obj);
    public void Closed()
    {
        foreach(var tcs in awaiting.Values) tcs.TrySetException(new IOException("Связь с Vivaldi потеряна."));
    }
}
internal sealed class Bridge(string name)
{
    public readonly ConcurrentDictionary<string,Peer> Peers = new();
    public event Func<Peer,JsonObject,Task>? Message;
    public event Action? Changed;
    public event Action<Exception>? Error;
    public async Task Listen(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(ct); }
            catch { pipe.Dispose(); throw; }
            _ = Serve(pipe, ct);
        }
    }
    private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        Peer? peer = null;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var hello = await Frames.Read(pipe, session.Token);
            if (hello?["type"]?.ToString() != "hello" || !Guid.TryParse(hello["profileId"]?.ToString(), out _)) return;
            peer = new Peer(pipe, hello, session.Token);
            if (!Peers.TryAdd(peer.Id, peer)) return;
            Changed?.Invoke();
            if (Message != null) await Message(peer, hello);
            while (await Frames.Read(pipe, session.Token) is { } msg)
                if (!peer.Reply(msg) && Message != null) await Message(peer, msg);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!ct.IsCancellationRequested && ex is not EndOfStreamException) Error?.Invoke(ex); }
        finally
        {
            session.Cancel();
            if (peer != null && ((ICollection<KeyValuePair<string,Peer>>)Peers).Remove(new(peer.Id,peer))) { peer.Closed(); Changed?.Invoke(); }
            pipe.Dispose();
        }
    }
}
