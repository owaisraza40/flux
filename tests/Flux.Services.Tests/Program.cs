using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Flux.Core;
using Flux.App.Services;

if (args.Contains("--emit-large-output")) { await Console.Error.WriteAsync(new string('x', 1100 * 1024)); await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
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
var metadata = MediaMetadataService.Parse("""{"title":"A real video: title","ext":"mp4","thumbnail":"https://example.test/thumb.jpg","filesize":null,"formats":[{"format_id":"137","height":1080,"vcodec":"avc1","acodec":"none","ext":"mp4"},{"format_id":"136","height":720,"vcodec":"avc1","acodec":"none","ext":"mp4"},{"format_id":"140","vcodec":"none","acodec":"aac","ext":"m4a"}]}""");
Assert(metadata.Title == "A real video: title" && metadata.SuggestedFileName == "A real video_ title.mp4" && metadata.TotalBytes == -1, "extractor title supplies a safe real filename and unknown size remains unknown");
Assert(metadata.Qualities.Select(value => value.Quality).SequenceEqual(new[] { MediaQuality.Best, MediaQuality.Video1080, MediaQuality.Video720, MediaQuality.AudioMp3 }), "quality options reflect real playable formats rather than invented resolutions");
var imageRejected = false;try { MediaMetadataService.Parse("""{"title":"Unavailable","ext":"mhtml","formats":[{"vcodec":"none","acodec":"none","ext":"mhtml"}]}"""); } catch (IOException) { imageRejected = true; }
Assert(imageRejected, "image-only extraction fails with a challenge/runtime explanation");
Assert(MediaArguments.Format(MediaQuality.Video720) == "bestvideo[height<=720]+bestaudio/best[height<=720]" && MediaArguments.Format(MediaQuality.AudioMp3) == "bestaudio/best", "quality choices produce bounded video and audio selectors");
var automaticArguments = MediaArguments.Common(new MediaToolPaths("yt-dlp.exe", root, @"C:\Program Files\nodejs\node.exe"), new DownloadSettings(), null, null);
Assert(automaticArguments.Contains("node:C:\\Program Files\\nodejs\\node.exe") && !automaticArguments.Contains("--cookies-from-browser") && !automaticArguments.Contains("--no-check-certificates"), "JavaScript runtime is explicit while browser-cookie access and TLS bypass stay off by default");
var optedInArguments = MediaArguments.Common(new MediaToolPaths("yt-dlp.exe", root), new DownloadSettings { MediaCookieBrowser = BrowserCookieSource.Edge }, null, null);
Assert(optedInArguments[optedInArguments.IndexOf("--cookies-from-browser") + 1] == "edge", "browser-cookie extraction uses only the explicitly selected browser");
string? temporaryCookieJar;
using (var context = await MediaRequestContext.CreateAsync(root, "https://www.youtube.com/watch?v=test", new Dictionary<string,string> { ["Cookie"]="fixture=local_test_only" }, CancellationToken.None))
{
    temporaryCookieJar=context.CookieJar;
    Assert(temporaryCookieJar!=null && (await File.ReadAllTextAsync(temporaryCookieJar)).Contains("www.youtube.com\tFALSE\t/\tTRUE\t0\tfixture\tlocal_test_only"), "temporary browser metadata cookies are scoped to the source host");
}
Assert(temporaryCookieJar!=null && !File.Exists(temporaryCookieJar), "temporary cookie jars are deleted after inspection/download");
await using (var manager = new DownloadManager(Path.Combine(root, "state")))
{
    await manager.InitializeAsync();
    var completion = new CompletionService(manager);
    var file = Path.Combine(root, "test & literal.bin");
    await File.WriteAllBytesAsync(file, [1,2,3,4]);
    var executable = Path.ChangeExtension(typeof(MediaTools).Assembly.Location, ".exe");
    var rejectedLargeOutput = false;
    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    {
        try { await MediaProcess.RunAsync(executable, new[] { "--emit-large-output" }, deadline.Token); }
        catch (IOException) { rejectedLargeOutput = true; }
    }
    Assert(rejectedLargeOutput, "oversized extractor output terminates its owned child instead of deadlocking redirected pipes");
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
    var recordedProgress = new RecordingProgress();
    var result = await media.DownloadAsync(item, new Dictionary<string,string>(), new DownloadSettings { CollisionPolicy = CollisionPolicy.Rename }, recordedProgress, CancellationToken.None);
    Assert(File.Exists(result.FilePath) && result.Bytes > 0, "real yt-dlp and FFmpeg download and merge a local HLS fixture");
    Assert(recordedProgress.HasReceivedBytes, "actual yt-dlp progress emits positive byte counts while downloading");
    var probe = new ProcessStartInfo(Path.Combine(tools.FfmpegDirectory, "ffprobe.exe")) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
    foreach(var value in new[]{"-v","error","-show_entries","format=duration","-of","json",result.FilePath})probe.ArgumentList.Add(value);
    using(var inspect=Process.Start(probe)!){var output=await inspect.StandardOutput.ReadToEndAsync();await inspect.WaitForExitAsync();using var json=JsonDocument.Parse(output);Assert(inspect.ExitCode==0&&double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture)>1,"merged media has valid playback duration");}
}
if (args.Contains("--youtube-integration"))
{
    const string publicUrl="https://www.youtube.com/watch?v=aqz-KE-bpKQ"; // Blender Foundation's public Creative Commons short film.
    Console.WriteLine("PUBLIC YOUTUBE TEST: "+publicUrl+" (no browser cookies)");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    var settings = new DownloadSettings { MediaCookieBrowser=BrowserCookieSource.None, CollisionPolicy=CollisionPolicy.Rename, RetryCount=0 };
    var video = await new MediaMetadataService(root).GetAsync(publicUrl,settings,null,timeout.Token);
    Console.WriteLine("EXTRACTED TITLE: "+video.Title+"; qualities: "+string.Join(", ",video.Qualities.Select(value=>value.Label)));
    Assert(video.Qualities.Any(value=>value.Quality==MediaQuality.Video720),"plain public YouTube metadata exposes a real 720p choice");
    var destination=Path.Combine(root,"youtube-output");Directory.CreateDirectory(destination);
    var item=new DownloadItem { Url=publicUrl,Kind=DownloadKind.Media,MediaQuality=MediaQuality.Video720,DestinationDirectory=destination,FileName=video.SuggestedFileName };
    var result=await media.DownloadAsync(item,new Dictionary<string,string>(),settings,new Progress<ExternalDownloadProgress>(value=>Console.WriteLine($"YOUTUBE PROGRESS {value.DownloadedBytes}/{value.TotalBytes} {value.Message}")),timeout.Token);
    Assert(File.Exists(result.FilePath)&&result.Bytes>0&&!Path.GetFileNameWithoutExtension(result.FilePath).Equals("watch",StringComparison.OrdinalIgnoreCase),"plain public YouTube 720p download saves a nonempty real-title file");
    Console.WriteLine("YOUTUBE OUTPUT: "+result.FilePath);
    var tools=await new MediaTools(root).EnsureAsync(settings,null,timeout.Token);
    var inspect=await MediaProcess.RunAsync(Path.Combine(tools.FfmpegDirectory,"ffprobe.exe"),new[]{"-v","error","-select_streams","v:0","-show_entries","stream=height","-of","json",result.FilePath},timeout.Token);
    using var streamJson=JsonDocument.Parse(inspect.Output);
    var height=streamJson.RootElement.GetProperty("streams")[0].GetProperty("height").GetInt32();
    Assert(inspect.ExitCode==0&&height==720,"actual downloaded YouTube video is 720p according to FFprobe");
}
Console.WriteLine($"Services checks: {passed} passed, 0 failed.");
return 0;

sealed class RecordingProgress : IProgress<ExternalDownloadProgress>
{
    private int _hasBytes;
    public bool HasReceivedBytes => Volatile.Read(ref _hasBytes) != 0;
    public void Report(ExternalDownloadProgress progress) { if (progress.DownloadedBytes > 0) Interlocked.Exchange(ref _hasBytes, 1); }
}

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
