using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Flux.Core;
using Flux.App.Services;

if (args.Length == 1 && File.Exists(args[0])) { await File.WriteAllTextAsync(args[0] + ".action-ok", args[0]); return 0; }
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.local/services-tests"));
Directory.CreateDirectory(root);
var passed = 0;
void Assert(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
var media = new MediaDownloadProcessor(root);
Assert(media.CanHandle(new DownloadItem { Url = "https://example.test/stream.m3u8" }), "HLS routing");
Assert(media.CanHandle(new DownloadItem { Url = "https://example.test/stream.mpd" }), "DASH routing");
Assert(media.CanHandle(new DownloadItem { Url = "https://www.youtube.com/watch?v=example" }), "video page routing");
Assert(!media.CanHandle(new DownloadItem { Url = "https://evilyoutube.com/watch" }), "lookalike host is not a video-site match");
Assert(!media.CanHandle(new DownloadItem { Url = "https://example.test/file.zip" }), "ordinary files stay in native engine");
Assert(!media.CanHandle(new DownloadItem { Url = "https://example.test/stream.m3u8", Kind = DownloadKind.Http }), "explicit HTTP choice is respected");
await using (var manager = new DownloadManager(Path.Combine(root, "state")))
{
    await manager.InitializeAsync();
    var completion = new CompletionService(manager);
    var file = Path.Combine(root, "test & literal.bin");
    await File.WriteAllBytesAsync(file, [1,2,3,4]);
    var executable = Path.ChangeExtension(typeof(MediaTools).Assembly.Location, ".exe");
    var settings = manager.Settings; settings.ScannerExecutable = executable; await manager.SaveSettingsAsync(settings);
    var item = new DownloadItem { FilePath = file, ScanOnCompletion = true, CompletionAction = CompletionAction.RunScript, CompletionScript = executable };
    await completion.HandleAsync(item);
    Assert(File.Exists(file + ".action-ok") && await File.ReadAllTextAsync(file + ".action-ok") == file, "scanner and completion executable receive a literal file argument");
}

if (args.Contains("--media-integration"))
{
    Console.WriteLine("Installing/verifying media tools from official upstream releases if not cached...");
    var tools = await new MediaTools(root).EnsureAsync(new Progress<string>(Console.WriteLine), CancellationToken.None);
    var source = Path.Combine(root, "hls-source"); Directory.CreateDirectory(source);
    var ffmpeg = new ProcessStartInfo(Path.Combine(tools.FfmpegDirectory, "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    foreach (var value in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10", "-t", "2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-threads", "1", "-f", "hls", "-hls_time", "1", "-hls_list_size", "0", Path.Combine(source,"stream.m3u8") }) ffmpeg.ArgumentList.Add(value);
    using (var generate = Process.Start(ffmpeg)!) { var error = await generate.StandardError.ReadToEndAsync(); await generate.WaitForExitAsync(); if (generate.ExitCode != 0) throw new Exception("FFmpeg fixture generation: " + error); }
    await using var server = new FixtureServer(source);
    var destination = Path.Combine(root,"media-output");Directory.CreateDirectory(destination);
    var item = new DownloadItem { Url = server.Url + "stream.m3u8", DestinationDirectory = destination, FileName = "hls-test.mp4", Kind = DownloadKind.Media };
    var result = await media.DownloadAsync(item, new Dictionary<string,string>(), new DownloadSettings { CollisionPolicy = CollisionPolicy.Rename }, new Progress<ExternalDownloadProgress>(p => { if(p.Message!=null)Console.WriteLine(p.Message); }), CancellationToken.None);
    Assert(File.Exists(result.FilePath) && result.Bytes > 0, "real yt-dlp and FFmpeg download and merge a local HLS fixture");
    var probe = new ProcessStartInfo(Path.Combine(tools.FfmpegDirectory, "ffprobe.exe")) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
    foreach(var value in new[]{"-v","error","-show_entries","format=duration","-of","json",result.FilePath})probe.ArgumentList.Add(value);
    using(var inspect=Process.Start(probe)!){var output=await inspect.StandardOutput.ReadToEndAsync();await inspect.WaitForExitAsync();using var json=JsonDocument.Parse(output);Assert(inspect.ExitCode==0&&double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture)>1,"merged media has valid playback duration");}
}
Console.WriteLine($"Services checks: {passed} passed, 0 failed.");
return 0;

sealed class FixtureServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();private readonly string _root;private readonly Task _accept;
    public string Url {get;}
    public FixtureServer(string root){_root=Path.GetFullPath(root)+Path.DirectorySeparatorChar;_listener.Start();Url=$"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";_accept=AcceptAsync();}
    private async Task AcceptAsync(){try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_=HandleAsync(client);}}catch(OperationCanceledException){}catch(SocketException)when(_stop.IsCancellationRequested){}}
    private async Task HandleAsync(TcpClient client){using(client)try{await using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);var request=await reader.ReadLineAsync(_stop.Token);if(request==null)return;while(await reader.ReadLineAsync(_stop.Token) is {Length:>0}){}var path=Path.GetFullPath(Path.Combine(_root,Uri.UnescapeDataString(request.Split(' ')[1].Split('?')[0]).TrimStart('/')));if(!path.StartsWith(_root,StringComparison.OrdinalIgnoreCase)||!File.Exists(path)){await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),_stop.Token);return;}var data=await File.ReadAllBytesAsync(path,_stop.Token);var type=path.EndsWith(".m3u8")?"application/vnd.apple.mpegurl":"video/mp2t";await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"),_stop.Token);await stream.WriteAsync(data,_stop.Token);}catch(Exception ex)when(ex is IOException or OperationCanceledException or SocketException){}}
    public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await _accept;_stop.Dispose();}
}
