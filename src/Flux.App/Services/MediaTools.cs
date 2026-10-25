using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Flux.Core;

namespace Flux.App.Services;

public sealed record MediaToolPaths(string YtDlp, string FfmpegDirectory, string? JavaScriptRuntime = null, string JavaScriptRuntimeKind = "node");

/// <summary>Downloads upstream media tools on first media use, verifies release digests before use.</summary>
public sealed class MediaTools(string dataDirectory)
{
    private static readonly SemaphoreSlim InstallationGate = new(1, 1);
    private readonly string _directory = Path.Combine(dataDirectory, "tools");

    public Task<MediaToolPaths> EnsureAsync(IProgress<string>? progress, CancellationToken token) => EnsureAsync(new DownloadSettings(), progress, token);

    public async Task<MediaToolPaths> EnsureAsync(DownloadSettings settings, IProgress<string>? progress, CancellationToken token, bool forceRefresh = false)
    {
        await InstallationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var ytDlp = CustomTool(settings.YtDlpPath) ?? Path.Combine(_directory, "yt-dlp.exe");
            var ffmpeg = CustomTool(settings.FfmpegPath) ?? Path.Combine(_directory, "ffmpeg.exe");
            var ffmpegDirectory = Path.GetDirectoryName(ffmpeg)!;
            var ffprobe = Path.Combine(ffmpegDirectory, "ffprobe.exe");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Flux/1.0.0");
            var checkedPath = Path.Combine(_directory, "yt-dlp-last-checked.txt");
            if (string.IsNullOrWhiteSpace(settings.YtDlpPath) && (!File.Exists(ytDlp) || forceRefresh || !File.Exists(checkedPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(checkedPath) > TimeSpan.FromHours(12)))
            {
                try
                {
                    progress?.Report("Checking the latest verified yt-dlp media engine…");
                    var asset = await FindAssetAsync(client, "yt-dlp/yt-dlp", "yt-dlp.exe", token).ConfigureAwait(false);
                    string? currentHash = null;
                    if (File.Exists(ytDlp)) { await using var current = File.OpenRead(ytDlp); currentHash = Convert.ToHexString(await SHA256.HashDataAsync(current, token).ConfigureAwait(false)); }
                    if (!asset.Sha256.Equals(currentHash, StringComparison.OrdinalIgnoreCase)) await DownloadVerifiedAsync(client, asset, ytDlp, token).ConfigureAwait(false);
                    await File.WriteAllTextAsync(checkedPath, asset.Sha256, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!forceRefresh && File.Exists(ytDlp) && !token.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
                { progress?.Report("Media update unavailable; using the cached engine. " + ex.Message); }
            }
            if (string.IsNullOrWhiteSpace(settings.FfmpegPath) && (!File.Exists(ffmpeg) || !File.Exists(ffprobe)))
            {
                progress?.Report("Installing verified FFmpeg stream merger…");
                var asset = await FindAssetAsync(client, "BtbN/FFmpeg-Builds", "ffmpeg-master-latest-win64-gpl.zip", token).ConfigureAwait(false);
                var archivePath = Path.Combine(_directory, "ffmpeg-download.zip");
                await DownloadVerifiedAsync(client, asset, archivePath, token).ConfigureAwait(false);
                using var archive = ZipFile.OpenRead(archivePath);
                foreach (var filename in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var matches = archive.Entries.Where(entry => entry.Name.Equals(filename, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) throw new InvalidDataException("The FFmpeg archive did not contain the expected tools.");
                    var temporary = Path.Combine(_directory, filename + ".installing");
                    matches[0].ExtractToFile(temporary, overwrite: true);
                    File.Move(temporary, Path.Combine(_directory, filename), overwrite: true);
                }
                foreach (var license in archive.Entries.Where(entry => entry.Name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase)).Take(1))
                    license.ExtractToFile(Path.Combine(_directory, "FFmpeg-LICENSE.txt"), overwrite: true);
                archive.Dispose();
                File.Delete(archivePath);
            }
            var runtime = CustomTool(settings.JavaScriptRuntimePath) ?? FindRuntime();
            if (runtime != null && Path.GetFileNameWithoutExtension(runtime).Equals("node", StringComparison.OrdinalIgnoreCase) && FileVersionInfo.GetVersionInfo(runtime).FileMajorPart < 22)
            {
                if (!string.IsNullOrWhiteSpace(settings.JavaScriptRuntimePath)) throw new IOException("YouTube requires Node.js 22 or newer. Choose a supported runtime or leave the path blank for automatic setup.");
                runtime = null;
            }
            var runtimeKind = runtime != null && Path.GetFileNameWithoutExtension(runtime).Equals("deno", StringComparison.OrdinalIgnoreCase) ? "deno" : "node";
            if (runtime == null)
            {
                runtimeKind = "deno";
                runtime = Path.Combine(_directory, "deno.exe");
                if (!File.Exists(runtime))
                {
                    progress?.Report("Installing verified JavaScript runtime for YouTube challenges…");
                    var asset = await FindAssetAsync(client, "denoland/deno", "deno-x86_64-pc-windows-msvc.zip", token).ConfigureAwait(false);
                    var archivePath = Path.Combine(_directory, "deno-download.zip");
                    await DownloadVerifiedAsync(client, asset, archivePath, token).ConfigureAwait(false);
                    using (var archive = ZipFile.OpenRead(archivePath))
                    {
                        var entry = archive.Entries.SingleOrDefault(value => value.Name == "deno.exe") ?? throw new IOException("The runtime archive does not contain deno.exe.");
                        entry.ExtractToFile(runtime + ".installing", overwrite: true);
                    }
                    File.Move(runtime + ".installing", runtime, overwrite: true);
                    File.Delete(archivePath);
                }
            }
            return new MediaToolPaths(ytDlp, ffmpegDirectory, runtime, runtimeKind);
        }
        finally { InstallationGate.Release(); }
    }

    private sealed record ToolAsset(string Name, string Url, string Sha256);

    private static string? CustomTool(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new IOException("The selected custom media tool does not exist: " + path);
        return Path.GetFullPath(path);
    }

    private static string? FindRuntime()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs") });
        foreach (var directory in directories.Where(value => !string.IsNullOrWhiteSpace(value)))
        foreach (var file in new[] { "deno.exe", "node.exe" })
        {
            try
            {
                var path = Path.Combine(directory.Trim('"'), file);
                if (File.Exists(path) && (file == "deno.exe" || FileVersionInfo.GetVersionInfo(path).FileMajorPart >= 22)) return Path.GetFullPath(path);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task<ToolAsset> FindAssetAsync(HttpClient client, string repository, string name, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        token = timeout.Token;
        using var response = await client.GetAsync("https://api.github.com/repos/" + repository + "/releases/latest", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: token).ConfigureAwait(false);
        var assets = json.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var selected = assets.SingleOrDefault(asset => asset.GetProperty("name").GetString() == name);
        if (selected.ValueKind == JsonValueKind.Undefined) throw new IOException("The upstream media tool release does not contain " + name + ".");
        var digest = selected.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
        {
            var sumsAsset = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString() is "SHA2-256SUMS" or "checksums.sha256");
            if (sumsAsset.ValueKind == JsonValueKind.Undefined) throw new IOException("Upstream did not publish a verifiable media-tool checksum.");
            var sums = await client.GetStringAsync(sumsAsset.GetProperty("browser_download_url").GetString(), token).ConfigureAwait(false);
            var line = sums.Split('\n').Select(value => value.Trim()).FirstOrDefault(value => value.EndsWith(" " + name, StringComparison.Ordinal) || value.EndsWith(" *" + name, StringComparison.Ordinal));
            digest = line is { Length: >= 64 } ? "sha256:" + line[..64] : null;
        }
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) throw new IOException("The media tool checksum is missing or invalid.");
        var url = selected.GetProperty("browser_download_url").GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/" + repository + "/releases/download/", StringComparison.Ordinal)) throw new IOException("Unexpected media tool download origin.");
        return new ToolAsset(name, uri.AbsoluteUri, digest[7..]);
    }

    private static async Task DownloadVerifiedAsync(HttpClient client, ToolAsset asset, string destination, CancellationToken token)
    {
        var temporary = destination + ".installing";
        try
        {
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 512L * 1024 * 1024) throw new IOException("The media tool package exceeds the supported size.");
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > 512L * 1024 * 1024) throw new IOException("The media tool package exceeds the supported size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
            }
            await using (var file = File.OpenRead(temporary))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
                if (!digest.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("The downloaded media tool did not match its upstream SHA-256 checksum.");
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
