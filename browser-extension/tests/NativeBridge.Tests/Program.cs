using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using Flux.App.Integration;

var testDirectoryArg = args.FirstOrDefault(value => value.StartsWith("--bridge-test-directory=", StringComparison.Ordinal));
if (testDirectoryArg != null) BridgePairing.TestDirectory = testDirectoryArg["--bridge-test-directory=".Length..];
if (args.Contains("--native-host")) return await NativeBridge.RunHostAsync();

BridgePairing.TestDirectory = Path.Combine(Path.GetTempPath(), "FluxBridge-tests-" + Guid.NewGuid().ToString("N"));

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}
void Reject(Action operation, string name)
{
    try { operation(); } catch (ArgumentException) { Check(true, name); return; }
    throw new InvalidOperationException("FAIL: " + name + " did not reject input");
}

Check(NativeBridge.IsNativeHostInvocation(["--native-host"]), "explicit native host startup");
Check(NativeBridge.IsNativeHostInvocation(["chrome-extension://" + BrowserIntegration.ChromiumExtensionId + "/", "--parent-window=0"]), "Chrome native host startup");
Check(NativeBridge.IsNativeHostInvocation([@"C:\Users\example\com.flux.downloads.json", BrowserIntegration.FirefoxExtensionId]), "Firefox native host startup");
Check(!NativeBridge.IsNativeHostInvocation(["https://example.com/file.zip"]), "normal URL is not native host mode");
Check(NativeBridge.NormalizeUrls([" HTTPS://EXAMPLE.COM/a ", "https://example.com/a"]).Length == 1, "URL normalization and deduplication");
foreach (var invalid in new[] { "file:///C:/private", "javascript:alert(1)", "ftp://example.com/a", "https://user:password@example.com/a", "not a url" })
    Reject(() => NativeBridge.NormalizeUrls([invalid]), "reject " + invalid.Split(':')[0]);
Reject(() => NativeBridge.NormalizeUrls([]), "reject empty download batch");
Reject(() => NativeBridge.NormalizeUrls(Enumerable.Repeat("https://example.com", 101)), "reject oversized batch");
Check(BrowserIntegration.ValidateChromiumId(BrowserIntegration.ChromiumExtensionId) == BrowserIntegration.ChromiumExtensionId, "Chromium ID validation");
Reject(() => BrowserIntegration.ValidateChromiumId("../../bad"), "reject invalid extension ID");

using var serverStop = new CancellationTokenSource();
var arrivals = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
var server = NativeBridge.RunServerAsync(values => arrivals.Enqueue(values), serverStop.Token);
Check(await NativeBridge.SendUrlsAsync(["https://example.com/test.bin"]), "current-user IPC accepts URLs");
Check(arrivals.TryDequeue(out var first) && first.SequenceEqual(new[] { "https://example.com/test.bin" }), "IPC preserves the URL");
Check(await NativeBridge.SendUrlsAsync([]), "empty IPC request activates existing UI");
Check(arrivals.TryDequeue(out var activation) && activation.Length == 0, "activation callback has no URLs");

var ping = await InvokeHostAsync(Frame("{\"action\":\"ping\",\"version\":1}"));
Check(ping.ExitCode == 0 && GetOk(ping.Bytes), "native messaging process ping and EOF exit");
var download = await InvokeHostAsync(Frame("{\"action\":\"download\",\"urls\":[\"https://example.com/native.bin\"],\"version\":1}"));
Check(download.ExitCode == 0 && GetOk(download.Bytes), "native messaging process forwards a download");
Check(arrivals.TryDequeue(out var native) && native[0] == "https://example.com/native.bin", "native framing preserves download URL");
foreach (var bad in new[]
{
    "{\"action\":\"download\",\"urls\":[\"file:///private\"]}",
    "{\"action\":\"ping\",\"action\":\"download\"}",
    "{\"action\":\"ping\",\"version\":\"one\"}",
    "{\"action\":\"download\",\"urls\":[1]}",
    "{\"action\":\"run\",\"command\":\"anything\"}"
})
{
    var rejected = await InvokeHostAsync(Frame(bad));
    Check(rejected.ExitCode == 0 && !GetOk(rejected.Bytes), "native host rejects malformed or unsupported request");
}
var tooBigHeader = new byte[4];
BinaryPrimitives.WriteUInt32LittleEndian(tooBigHeader, (uint)NativeBridge.MaximumMessageBytes + 1);
var tooBig = await InvokeHostAsync(tooBigHeader);
Check(tooBig.ExitCode == 1 && !GetOk(tooBig.Bytes), "oversized native frame is bounded and rejected");
var truncated = await InvokeHostAsync([5, 0]);
Check(truncated.ExitCode == 1 && !GetOk(truncated.Bytes), "truncated native header is rejected");

serverStop.Cancel();
await server;
Check(!await NativeBridge.SendUrlsAsync(["https://example.com/a"]), "IPC reports absent desktop server");
using var metadataStop = new CancellationTokenSource();
var metadataArrivals = new System.Collections.Concurrent.ConcurrentQueue<BrowserDownload[]>();
var metadataServer = NativeBridge.RunDownloadServerAsync(values => metadataArrivals.Enqueue(values), metadataStop.Token);
var metadata = new BrowserDownload { Url = "https://example.com/a.mp4", Cookies = "session=test-only", Referrer = "https://example.com/page", UserAgent = "FluxTest/1", FileName = "a.mp4", IsMedia = true };
Check(await NativeBridge.SendDownloadsAsync([metadata]), "metadata IPC accepts an HTTP download");
Check(metadataArrivals.TryDequeue(out var captured) && captured[0].Cookies == metadata.Cookies && captured[0].Referrer == metadata.Referrer && captured[0].UserAgent == metadata.UserAgent && captured[0].FileName == metadata.FileName && captured[0].IsMedia, "metadata survives native IPC");
var metadataNative = await InvokeHostAsync(Frame(JsonSerializer.Serialize(new { version=1, action="download", downloads=new[] { new { url=metadata.Url, cookies=metadata.Cookies, referrer=metadata.Referrer, userAgent=metadata.UserAgent, fileName=metadata.FileName, isMedia=true } } })));
Check(metadataNative.ExitCode == 0 && GetOk(metadataNative.Bytes) && metadataArrivals.TryDequeue(out captured) && captured[0].Cookies == metadata.Cookies, "framed native host preserves metadata");
var forbidden = await InvokeHostAsync(Frame("{\"action\":\"download\",\"downloads\":[{\"url\":\"https://example.com/a\",\"completionScript\":\"evil.exe\"}]}"));
Check(!GetOk(forbidden.Bytes), "browser metadata cannot specify scripts or local actions");
var pairOrigin = "chrome-extension://" + BrowserIntegration.ChromiumExtensionId;
var paired = await InvokeHostAsync(Frame(JsonSerializer.Serialize(new { action="pair", version=1, origin=pairOrigin })), pairOrigin + "/");
using var pairedJson = JsonDocument.Parse(paired.Bytes.AsMemory(4));
Check(GetOk(paired.Bytes) && pairedJson.RootElement.GetProperty("token").GetString()?.Length == 64, "registered-origin native invocation pairs a browser");
var pairToken = pairedJson.RootElement.GetProperty("token").GetString()!;
Check(BridgePairing.Authenticate(pairOrigin, pairToken), "paired token authenticates matching extension origin");
Check(!BridgePairing.Authenticate(pairOrigin, new string('0',64)) && !BridgePairing.Authenticate("https://example.com",pairToken), "token and origin mismatch are rejected");
var wrongPair = await InvokeHostAsync(Frame(JsonSerializer.Serialize(new { action="pair", version=1, origin="chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" })), pairOrigin + "/");
Check(!GetOk(wrongPair.Bytes), "native caller cannot pair a different extension origin");
using var wsStop = new CancellationTokenSource();
var wsArrivals = new System.Collections.Concurrent.ConcurrentQueue<BrowserDownload>();
WebSocketBridge.TestPort = 0;
WebSocketBridge.RecentDownloadsProvider = () => [new { fileName="Completed fixture.bin",status="Completed",progress=100 }];
var wsServer = WebSocketBridge.RunAsync(value=>wsArrivals.Enqueue(value),wsStop.Token);
var wsUri = new Uri($"ws://127.0.0.1:{WebSocketBridge.BoundPort}/");
using (var socket = new ClientWebSocket())
{
    socket.Options.SetRequestHeader("Origin",pairOrigin);
    await socket.ConnectAsync(wsUri,CancellationToken.None);
    await WsSend(socket,new {action="hello",token=pairToken,browser="Edge",userAgent="Fixture Edg/1"});
    Check((await WsRead(socket)).GetProperty("ok").GetBoolean(), "authenticated loopback WebSocket connects");
    Check(BrowserIntegration.GetStatuses()["Edge"] && !BrowserIntegration.GetStatuses()["Chrome"], "browser status requires an actual authenticated connection");
    await WsSend(socket,new {action="heartbeat"});
    var heartbeat = await WsRead(socket);
    Check(heartbeat.GetProperty("type").GetString()=="heartbeat", "desktop heartbeat returns active state");
    Check(heartbeat.GetProperty("recentDownloads")[0].GetProperty("fileName").GetString()=="Completed fixture.bin", "heartbeat exposes real desktop recent downloads");
    string[]? grabbed = null;
    WebSocketBridge.PageLinksReceived = urls => grabbed = urls;
    var grab = WebSocketBridge.RequestPageLinksAsync();
    var grabCommand = await WsRead(socket);
    Check(grabCommand.GetProperty("action").GetString()=="grab-links", "Grab All sends a command to the authenticated browser");
    await WsSend(socket,new {action="page-links",requestId=grabCommand.GetProperty("requestId").GetString(),urls=new[]{"https://example.com/one.zip","https://example.com/two.pdf","https://example.com/one.zip"}});
    Check((await WsRead(socket)).GetProperty("ok").GetBoolean() && await grab && grabbed?.Length==2, "Grab All validates and deduplicates page links before one UI batch callback");
    await WsSend(socket,new {action="page-links",requestId=new string('0',32),urls=new[]{"https://example.com/one.zip"}});
    Check(!(await WsRead(socket)).GetProperty("ok").GetBoolean(), "unsolicited page links cannot impersonate a desktop Grab All request");
    await WsSend(socket,new {action="download",download=new{url=metadata.Url,cookies=metadata.Cookies,referrer=metadata.Referrer,userAgent=metadata.UserAgent,fileName=metadata.FileName,isMedia=true}});
    Check((await WsRead(socket)).GetProperty("ok").GetBoolean() && wsArrivals.TryDequeue(out var webDownload) && webDownload.Cookies==metadata.Cookies && webDownload.IsMedia, "WebSocket capture reaches app callback with metadata");
    await WsSend(socket,new {type="capture",url=metadata.Url,filename="Real title.mp4",pageTitle="Real title",isMedia=true});
    Check((await WsRead(socket)).GetProperty("ok").GetBoolean() && wsArrivals.TryDequeue(out webDownload) && webDownload.FileName=="Real title.mp4" && webDownload.PageTitle=="Real title", "flat capture protocol preserves title and filename");
    await WsSend(socket,new{action="download",download=new{url="file:///private"}});
    Check(!(await WsRead(socket)).GetProperty("ok").GetBoolean(), "WebSocket rejects local file downloads");
    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"test done",CancellationToken.None);
}
using (var socket = new ClientWebSocket())
{
    socket.Options.SetRequestHeader("Origin",pairOrigin);
    await socket.ConnectAsync(wsUri,CancellationToken.None);
    await WsSend(socket,new {action="hello",token=new string('0',64)});
    var buffer=new byte[256];var result=await socket.ReceiveAsync(buffer,CancellationToken.None);
    Check(result.MessageType==WebSocketMessageType.Close && socket.CloseStatus==WebSocketCloseStatus.PolicyViolation,"WebSocket closes clients with wrong tokens");
}
using (var socket = new ClientWebSocket())
{
    socket.Options.SetRequestHeader("Origin","https://example.com");
    try{await socket.ConnectAsync(wsUri,CancellationToken.None);throw new InvalidOperationException("Unpaired web page connected");}
    catch(WebSocketException){Check(true,"ordinary web-page origin is refused before WebSocket upgrade");}
}
wsStop.Cancel(); await wsServer;
Check(!BrowserIntegration.GetStatuses()["Edge"], "disconnect clears actual browser connection status");
Check(!await WebSocketBridge.RequestPageLinksAsync(), "Grab All reports no live browser for clipboard fallback");
metadataStop.Cancel(); await metadataServer;
Console.WriteLine($"All {passed} native bridge checks passed.");
return 0;

static byte[] Frame(string json)
{
    var payload = Encoding.UTF8.GetBytes(json);
    var bytes = new byte[4 + payload.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)payload.Length);
    payload.CopyTo(bytes, 4);
    return bytes;
}

static bool GetOk(byte[] bytes)
{
    if (bytes.Length < 4) throw new InvalidDataException("Missing native response header.");
    var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    if (bytes.Length != length + 4) throw new InvalidDataException("Native response framing length mismatch.");
    using var document = JsonDocument.Parse(bytes.AsMemory(4));
    return document.RootElement.GetProperty("ok").GetBoolean();
}

static async Task<(byte[] Bytes, int ExitCode)> InvokeHostAsync(byte[] input, string? origin = null)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        CreateNoWindow = true
    };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add("--native-host");
    if(origin!=null)info.ArgumentList.Add(origin);
    info.ArgumentList.Add("--bridge-test-directory=" + BridgePairing.TestDirectory);
    using var process = Process.Start(info) ?? throw new IOException("Cannot start native host test child.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    using var output = new MemoryStream();
    var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
    var errors = process.StandardError.ReadToEndAsync(timeout.Token);
    await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
    process.StandardInput.Close();
    await process.WaitForExitAsync(timeout.Token);
    await copy;
    await errors;
    return (output.ToArray(), process.ExitCode);
}

static async Task WsSend(ClientWebSocket socket, object value) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(),WebSocketMessageType.Text,true,CancellationToken.None);
static async Task<JsonElement> WsRead(ClientWebSocket socket)
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var buffer=new byte[8192];var result=await socket.ReceiveAsync(buffer,timeout.Token);
    if(result.MessageType!=WebSocketMessageType.Text)throw new InvalidDataException("Missing WebSocket response");
    using var json=JsonDocument.Parse(buffer.AsMemory(0,result.Count));return json.RootElement.Clone();
}
