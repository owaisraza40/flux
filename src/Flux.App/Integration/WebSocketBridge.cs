using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Flux.App.Integration;

/// <summary>Loopback only, native-paired WebSocket transport. No HTTP URL ACL/admin reservation needed.</summary>
public static class WebSocketBridge
{
    public const int Port = 6543;
    internal static int? TestPort { get; set; }
    internal static int BoundPort { get; private set; }
    public static Func<object[]>? RecentDownloadsProvider { get; set; }
    public static Func<object?>? CaptureSettingsProvider { get; set; }
    public static Action<string[]>? PageLinksReceived { get; set; }
    private sealed record Peer(WebSocket Socket, DateTimeOffset ConnectedAt);
    private sealed record PageRequest(Guid PeerId, TaskCompletionSource<bool> Completion);
    private static readonly ConcurrentDictionary<Guid, Peer> Peers = new();
    private static readonly ConcurrentDictionary<string, PageRequest> PageRequests = new();
    private static readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> SendLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<bool> RequestPageLinksAsync(CancellationToken cancellationToken = default)
    {
        var peer = Peers.Where(pair => pair.Value.Socket.State == WebSocketState.Open).OrderByDescending(pair => pair.Value.ConnectedAt).FirstOrDefault();
        if (peer.Value == null) return false;
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PageRequests[requestId] = new PageRequest(peer.Key, completion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await SendAsync(peer.Value.Socket, new { action="grab-links", requestId }, timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException) { return false; }
        finally { PageRequests.TryRemove(requestId, out _); }
    }

    public static async Task RunAsync(Action<BrowserDownload> onDownload, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, TestPort ?? Port);
        listener.Start(16);
        var boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        BoundPort = boundPort;
        using var slots = new SemaphoreSlim(8, 8);
        var clients = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                if (!await slots.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { client.Dispose(); continue; }
                clients.RemoveAll(task => task.IsCompleted);
                clients.Add(Task.Run(async () =>
                {
                    try { await HandleAsync(client, onDownload, boundPort, cancellationToken).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or SocketException or WebSocketException or OperationCanceledException or JsonException or InvalidDataException or ArgumentException) { }
                    finally { client.Dispose(); slots.Release(); }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            if (clients.Count != 0) await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    private static async Task HandleAsync(TcpClient client, Action<BrowserDownload> onDownload, int boundPort, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        var bytes = new List<byte>();
        var single = new byte[1];
        while (bytes.Count < 16_384)
        {
            if (await stream.ReadAsync(single, timeout.Token).ConfigureAwait(false) == 0) return;
            bytes.Add(single[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var split = line.IndexOf(':');
            if (split <= 0 || !headers.TryAdd(line[..split].Trim(), line[(split + 1)..].Trim())) { await RefuseAsync(stream, timeout.Token); return; }
        }
        if (bytes.Count >= 16_384 || lines[0] is not ("GET / HTTP/1.1" or "GET /flux HTTP/1.1")
            || !headers.TryGetValue("Host", out var host) || (host != $"localhost:{boundPort}" && host != $"127.0.0.1:{boundPort}")
            || !headers.TryGetValue("Origin", out var origin) || !BridgePairing.IsPairedOrigin(origin)
            || !headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Connection", out var connection) || !connection.Split(',').Any(value => value.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            || !headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13"
            || !headers.TryGetValue("Sec-WebSocket-Key", out var key) || !ValidKey(key))
        { await RefuseAsync(stream, timeout.Token); return; }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), timeout.Token).ConfigureAwait(false);
        using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(20));
        var hello = await ReceiveAsync(socket, timeout.Token).ConfigureAwait(false);
        if (hello is null) return;
        string? browser = null, userAgent = null;
        using (var document = JsonDocument.Parse(hello))
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("action", out var action) || action.GetString() != "hello"
                || !root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String
                || !BridgePairing.Authenticate(origin, token.GetString()!))
            { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Pairing required", timeout.Token).ConfigureAwait(false); return; }
            if (root.TryGetProperty("browser", out var browserValue) && browserValue.ValueKind == JsonValueKind.String) browser = browserValue.GetString();
            if (root.TryGetProperty("userAgent", out var agentValue) && agentValue.ValueKind == JsonValueKind.String) userAgent = agentValue.GetString();
        }
        var connectionId = BrowserConnectionStatus.Connected(origin, userAgent, browser);
        Peers[connectionId] = new Peer(socket, DateTimeOffset.UtcNow);
        try
        {
        await SendAsync(socket, StatusResponse("ready"), timeout.Token).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(TimeSpan.FromSeconds(70));
            var message = await ReceiveAsync(socket, idle.Token).ConfigureAwait(false);
            if (message is null) return;
            try
            {
                using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                var actionName = root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String ? action.GetString() : null;
                if (actionName == "heartbeat") { await SendAsync(socket, StatusResponse("heartbeat"), idle.Token); continue; }
                if (actionName == "page-links")
                {
                    if (!root.TryGetProperty("requestId", out var request) || request.ValueKind != JsonValueKind.String
                        || !PageRequests.TryGetValue(request.GetString()!, out var pending) || pending.PeerId != connectionId)
                        throw new InvalidDataException("Unknown page-link request.");
                    if (!root.TryGetProperty("urls", out var urlsElement) || urlsElement.ValueKind != JsonValueKind.Array || urlsElement.GetArrayLength() > NativeBridge.MaximumUrls)
                        throw new InvalidDataException("Page links must be a bounded URL list.");
                    if (urlsElement.GetArrayLength() == 0) pending.Completion.TrySetResult(false);
                    else
                    {
                        var urls = NativeBridge.NormalizeUrls(urlsElement.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("Invalid page URL.")));
                        PageLinksReceived?.Invoke(urls);
                        pending.Completion.TrySetResult(true);
                    }
                    await SendAsync(socket, new { ok=true, version=1, count=urlsElement.GetArrayLength(), message="Page links received by Flux." }, idle.Token);
                    continue;
                }
                JsonElement item;
                if (root.TryGetProperty("type", out var captureType) && captureType.ValueKind == JsonValueKind.String && captureType.GetString() == "capture") item = root;
                else if (actionName != "download" || !root.TryGetProperty("download", out item)) throw new InvalidDataException("Unsupported action.");
                onDownload(BrowserDownload.Parse(item));
                await SendAsync(socket, new { ok = true, version = 1, message = "Download dialog opened in Flux.", count = 1 }, idle.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
            { await SendAsync(socket, new { ok = false, version = 1, message = error.Message, count = 0 }, idle.Token).ConfigureAwait(false); }
        }
        }
        finally
        {
            BrowserConnectionStatus.Disconnected(connectionId);
            Peers.TryRemove(connectionId, out _);
            SendLocks.TryRemove(socket, out _);
            foreach (var pending in PageRequests.Values.Where(value => value.PeerId == connectionId)) pending.Completion.TrySetResult(false);
        }
    }

    private static object StatusResponse(string type)
    {
        object[] recent = [];
        object? settings = null;
        try { recent = (RecentDownloadsProvider?.Invoke() ?? []).Take(10).ToArray(); } catch { }
        try { settings = CaptureSettingsProvider?.Invoke(); } catch { }
        return new { ok = true, version = 1, type, message = "Flux is connected.", count = 0, recentDownloads = recent, captureSettings = settings };
    }

    private static bool ValidKey(string key) { try { return Convert.FromBase64String(key).Length == 16; } catch (FormatException) { return false; } }
    private static Task RefuseAsync(Stream stream, CancellationToken token) => stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token).AsTask();
    private static async Task<byte[]?> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > NativeBridge.MaximumMessageBytes)
                throw new InvalidDataException("Invalid WebSocket message.");
            await message.WriteAsync(buffer.AsMemory(0, result.Count), token).ConfigureAwait(false);
            if (result.EndOfMessage) return message.ToArray();
        }
    }
    private static async Task SendAsync<T>(WebSocket socket, T value, CancellationToken token)
    {
        var gate = SendLocks.GetOrAdd(socket, _ => new SemaphoreSlim(1,1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
