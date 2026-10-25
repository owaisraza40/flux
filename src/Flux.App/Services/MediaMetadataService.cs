using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Flux.Core;

namespace Flux.App.Services;

public sealed record MediaQualityOption(MediaQuality Quality, string Label, int? Height = null);
public sealed record MediaMetadata(string Title, string? ThumbnailUrl, string SuggestedFileName, long TotalBytes, IReadOnlyList<MediaQualityOption> Qualities);

/// <summary>Queries the real extractor before asking the user to start a media download.</summary>
public sealed class MediaMetadataService(string dataDirectory)
{
    public Task<MediaMetadata> GetAsync(string url, DownloadSettings settings, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken) => GetWithRetryAsync(url, settings, headers, cancellationToken, false);

    private async Task<MediaMetadata> GetWithRetryAsync(string url, DownloadSettings settings, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken, bool retry)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) throw new ArgumentException("Use a complete HTTP/HTTPS media URL without embedded credentials.");
        var tools = await new MediaTools(dataDirectory).EnsureAsync(settings, null, cancellationToken, forceRefresh: retry).ConfigureAwait(false);
        using var context = await MediaRequestContext.CreateAsync(dataDirectory, url, headers, cancellationToken).ConfigureAwait(false);
        var arguments = MediaArguments.Common(tools, settings, headers, context.CookieJar);
        if (retry) arguments.AddRange(["--extractor-args", "youtube:skip=hls,dash"]);
        arguments.AddRange(["--dump-single-json", "--skip-download", "--no-warnings", "--", uri.AbsoluteUri]);
        var result = await MediaProcess.RunAsync(tools.YtDlp, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (!retry && (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)) && (result.Error.Contains("n challenge", StringComparison.OrdinalIgnoreCase) || result.Error.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase) || result.Error.Contains("Only images", StringComparison.OrdinalIgnoreCase))) return await GetWithRetryAsync(url, settings, headers, cancellationToken, true).ConfigureAwait(false);
            throw new IOException(MediaArguments.ErrorMessage("Could not inspect video", result.Error, url));
        }
        return Parse(result.Output);
    }

    public static MediaMetadata Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        var title = Text(value, "title");
        if (string.IsNullOrWhiteSpace(title)) throw new IOException("The media extractor did not return a title.");
        var formats = value.TryGetProperty("formats", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
        bool HasVideo(JsonElement format) => Text(format, "vcodec") is { Length: > 0 } codec && codec != "none" && Text(format, "ext") is not ("mhtml" or "jpg" or "png");
        bool HasAudio(JsonElement format) => Text(format, "acodec") is { Length: > 0 } codec && codec != "none";
        var videos = formats.Where(HasVideo).ToArray();
        if (formats.Length > 0 && videos.Length == 0 && !formats.Any(HasAudio)) throw new IOException("Only image/storyboard formats are available. Update the media tools and verify the JavaScript runtime; a playable format is required.");
        var qualities = new List<MediaQualityOption> { new(MediaQuality.Best, "Best available") };
        foreach (var pair in new[] { (1080, MediaQuality.Video1080), (720, MediaQuality.Video720), (480, MediaQuality.Video480), (360, MediaQuality.Video360) })
            if (videos.Any(format => Number(format, "height") == pair.Item1)) qualities.Add(new(pair.Item2, pair.Item1 + "p MP4", pair.Item1));
        if (formats.Any(HasAudio) || Text(value, "acodec") is { Length: > 0 } audio && audio != "none") qualities.Add(new(MediaQuality.AudioMp3, "Audio only MP3"));
        var extension = Text(value, "ext");
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10 || !extension.All(char.IsAsciiLetterOrDigit)) extension = "mp4";
        var filename = DownloadNames.Sanitize(title + "." + extension);
        var size = Number(value, "filesize") ?? Number(value, "filesize_approx") ?? -1;
        var thumbnail = Text(value, "thumbnail");
        if (!Uri.TryCreate(thumbnail, UriKind.Absolute, out var thumb) || thumb.Scheme is not ("http" or "https")) thumbnail = null;
        return new MediaMetadata(title, thumbnail, filename, size, qualities);
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static long? Number(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : null;
}

internal static class MediaArguments
{
    internal static List<string> Common(MediaToolPaths tools, DownloadSettings settings, IReadOnlyDictionary<string, string>? headers, string? cookieJar)
    {
        var result = new List<string> { "--ignore-config", "--no-playlist", "--no-colors", "--windows-filenames", "--encoding", "utf-8", "--socket-timeout", "20", "--extractor-retries", settings.RetryCount.ToString(CultureInfo.InvariantCulture), "--ffmpeg-location", tools.FfmpegDirectory };
        if (tools.JavaScriptRuntime != null) result.AddRange(["--js-runtimes", tools.JavaScriptRuntimeKind + ":" + tools.JavaScriptRuntime]);
        // Official yt-dlp.exe includes its matching EJS solver. Custom builds may fetch the upstream component explicitly.
        if (!string.IsNullOrWhiteSpace(settings.YtDlpPath)) result.AddRange(["--remote-components", "ejs:github"]);
        if (settings.MediaCookieBrowser != BrowserCookieSource.None) result.AddRange(["--cookies-from-browser", settings.MediaCookieBrowser.ToString().ToLowerInvariant()]);
        if (cookieJar != null) result.AddRange(["--cookies", cookieJar]);
        foreach (var header in headers ?? new Dictionary<string, string>())
        {
            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            if (!new[] { "User-Agent", "Referer", "Accept-Language" }.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) throw new IOException("Media downloads support scoped cookies, User-Agent and Referer; other custom headers require direct HTTP mode.");
            if (header.Value.Contains('\r') || header.Value.Contains('\n')) throw new IOException("A media header contains a newline.");
            result.AddRange(["--add-header", header.Key + ":" + header.Value]);
        }
        return result;
    }

    internal static string Format(MediaQuality quality) => quality switch
    {
        MediaQuality.Video1080 => "bestvideo[height<=1080]+bestaudio/best[height<=1080]",
        MediaQuality.Video720 => "bestvideo[height<=720]+bestaudio/best[height<=720]",
        MediaQuality.Video480 => "bestvideo[height<=480]+bestaudio/best[height<=480]",
        MediaQuality.Video360 => "bestvideo[height<=360]+bestaudio/best[height<=360]",
        MediaQuality.AudioMp3 => "bestaudio/best",
        _ => "bestvideo+bestaudio/best"
    };

    internal static string ErrorMessage(string prefix, string error, string url)
    {
        var summary = error.Length > 2500 ? error[^2500..] : error;
        summary = summary.Replace(url, "[source]", StringComparison.Ordinal);
        if (summary.Contains("n challenge", StringComparison.OrdinalIgnoreCase) || summary.Contains("Only images", StringComparison.OrdinalIgnoreCase)) summary += " Update yt-dlp and check Settings → Media → JavaScript runtime (Node 22+ or Deno 2.3+).";
        if (summary.Contains("cookies", StringComparison.OrdinalIgnoreCase) || summary.Contains("Sign in", StringComparison.OrdinalIgnoreCase)) summary += " If this video requires your login, explicitly select your browser under Settings → Media → Cookie source. Browser security may prevent cookie decryption.";
        return prefix + ": " + summary.Trim();
    }
}

internal sealed class MediaRequestContext(string? cookieJar) : IDisposable
{
    public string? CookieJar { get; } = cookieJar;
    public static async Task<MediaRequestContext> CreateAsync(string dataDirectory, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken token)
    {
        if (headers == null || !headers.TryGetValue("Cookie", out var cookies) || string.IsNullOrWhiteSpace(cookies)) return new(null);
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "media-cookies-" + Guid.NewGuid().ToString("N") + ".txt");
        var source = new Uri(url);
        var lines = new List<string> { "# Netscape HTTP Cookie File" };
        foreach (var pair in cookies.Split(';'))
        {
            var parts = pair.Trim().Split('=', 2);
            if (parts.Length != 2 || parts.Any(part => part.Contains('\t') || part.Contains('\r') || part.Contains('\n'))) continue;
            lines.Add(string.Join('\t', source.Host, "FALSE", "/", source.Scheme == "https" ? "TRUE" : "FALSE", "0", parts[0], parts[1]));
        }
        try { await File.WriteAllLinesAsync(path, lines, token).ConfigureAwait(false); return new(path); }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public void Dispose() { if (CookieJar != null && File.Exists(CookieJar)) File.Delete(CookieJar); }
}

internal sealed record MediaProcessResult(int ExitCode, string Output, string Error);
internal static class MediaProcess
{
    internal static async Task<MediaProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        token = deadline.Token;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The media engine could not start.");
        void StopOwnedProcess() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        using var registration = token.Register(StopOwnedProcess);
        async Task<string> ReadBoundedAsync(StreamReader reader, int maximum)
        {
            var output = new StringBuilder();
            var buffer = new char[8192];
            int count;
            try
            {
                while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + count > maximum) throw new IOException("Media metadata exceeded its supported size.");
                    output.Append(buffer, 0, count);
                }
                return output.ToString();
            }
            catch { StopOwnedProcess(); throw; } // A failed reader must never leave its child blocked on a full redirected pipe.
        }
        var stdout = ReadBoundedAsync(process.StandardOutput, 20 * 1024 * 1024);
        var stderr = ReadBoundedAsync(process.StandardError, 1024 * 1024);
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false)); }
        catch
        {
            StopOwnedProcess();
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            throw;
        }
    }
}
