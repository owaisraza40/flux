using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Flux.Core;

namespace Flux.App.Services;

public sealed class MediaDownloadProcessor(string dataDirectory) : IExternalDownloadProcessor
{
    private readonly MediaTools _tools = new(dataDirectory);

    public bool CanHandle(DownloadItem item)
    {
        if (item.Kind == DownloadKind.Media) return true;
        if (item.Kind == DownloadKind.Http || !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)) return false;
        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("vimeo.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".vimeo.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("dailymotion.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".dailymotion.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ExternalDownloadResult> DownloadAsync(DownloadItem item, IReadOnlyDictionary<string, string> headers, DownloadSettings settings, IProgress<ExternalDownloadProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new ExternalDownloadProgress(0, -1, 0, "Preparing media engine…"));
        var tools = await _tools.EnsureAsync(new Progress<string>(message => progress.Report(new ExternalDownloadProgress(0, -1, 0, message))), cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(item.DestinationDirectory);
        var workDirectory = Path.Combine(item.DestinationDirectory, ".flux-media-" + item.Id.ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var candidates = new[] { item.Url }.Concat(item.Mirrors ?? []).Distinct(StringComparer.Ordinal).ToArray();
        Exception? lastError = null;
        foreach (var url in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var args = new List<string> { "--ignore-config", "--no-playlist", "--no-colors", "--newline", "--windows-filenames", "--restrict-filenames", "--continue", "--no-overwrites", "--no-mtime", "--retries", settings.RetryCount.ToString(CultureInfo.InvariantCulture), "--fragment-retries", settings.RetryCount.ToString(CultureInfo.InvariantCulture), "--ffmpeg-location", tools.FfmpegDirectory, "--paths", workDirectory, "--output", "%(title).160B [%(id)s].%(ext)s", "--progress-template", "download:FLUX_PROGRESS:%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(progress.speed)s", "--print", "after_move:FLUX_FILE:%(filepath)s" };
                if (!string.IsNullOrWhiteSpace(item.FileName) && item.FileName != DownloadNames.FromUrl(new Uri(item.Url)))
                {
                    args[args.IndexOf("--output") + 1] = DownloadNames.Sanitize(Path.GetFileNameWithoutExtension(item.FileName)) + ".%(ext)s";
                }
                var cap = item.SpeedLimitBytesPerSecond ?? settings.SpeedLimitBytesPerSecond;
                if (settings.GlobalSpeedLimitBytesPerSecond > 0)
                {
                    var share = Math.Max(1, settings.GlobalSpeedLimitBytesPerSecond / Math.Max(1, settings.MaxConcurrentDownloads));
                    cap = cap > 0 ? Math.Min(cap, share) : share;
                }
                if (cap > 0) { args.Add("--limit-rate"); args.Add(cap.ToString(CultureInfo.InvariantCulture)); }
                if (SameOrigin(item.Url, url))
                    foreach (var pair in headers)
                    {
                        if (pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                        {
                            // yt-dlp's cookie jar handles domain/path scoping. Never use a global Cookie header across media CDN origins.
                            continue;
                        }
                        // A media extractor may contact several CDNs. Only non-secret media
                        // context headers are global; scoped cookies use the jar below.
                        if (pair.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase))
                        { args.Add("--add-header"); args.Add(pair.Key + ":" + pair.Value); }
                        else throw new IOException("Media downloads accept domain-scoped cookies, User-Agent and Referer. Other custom headers are supported by direct HTTP mode only.");
                    }
                string? cookieJar = null;
                try
                {
                    if (SameOrigin(item.Url, url) && headers.TryGetValue("Cookie", out var cookies) && !string.IsNullOrWhiteSpace(cookies))
                    {
                        cookieJar = Path.Combine(dataDirectory, "media-cookies-" + item.Id.ToString("N") + ".txt");
                        var source = new Uri(item.Url);
                        var lines = new List<string> { "# Netscape HTTP Cookie File" };
                        foreach (var pair in cookies.Split(';'))
                        {
                            var parts = pair.Trim().Split('=', 2);
                            if (parts.Length != 2 || parts.Any(part => part.Contains('\t') || part.Contains('\r') || part.Contains('\n'))) continue;
                            lines.Add(string.Join('\t', source.Host, "FALSE", "/", source.Scheme == "https" ? "TRUE" : "FALSE", "0", parts[0], parts[1]));
                        }
                        await File.WriteAllLinesAsync(cookieJar, lines, cancellationToken).ConfigureAwait(false);
                        args.Add("--cookies"); args.Add(cookieJar);
                    }
                    args.Add("--"); args.Add(url);
                    string? output = null;
                    var errors = new Queue<string>();
                    var info = new ProcessStartInfo(tools.YtDlp) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var argument in args) info.ArgumentList.Add(argument);
                    using var process = Process.Start(info) ?? throw new IOException("The media engine could not start.");
                    using var cancellation = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
                    var readOutput = Task.Run(async () =>
                    {
                        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                        {
                            if (line.StartsWith("FLUX_FILE:", StringComparison.Ordinal)) output = line[10..].Trim();
                            if (line.StartsWith("FLUX_PROGRESS:", StringComparison.Ordinal))
                            {
                                var fields = line[14..].Split('|');
                                static long Bytes(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes) && double.IsFinite(bytes) ? (long)Math.Max(0, bytes) : 0;
                                if (fields.Length == 3) progress.Report(new ExternalDownloadProgress(Bytes(fields[0]), fields[1] is "NA" or "None" ? -1 : Bytes(fields[1]), Bytes(fields[2]), "Downloading media…"));
                            }
                        }
                    }, CancellationToken.None);
                    var readError = Task.Run(async () =>
                    {
                        while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line) { if (errors.Count >= 8) errors.Dequeue(); errors.Enqueue(line); }
                    }, CancellationToken.None);
                    try
                    {
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        await Task.WhenAll(readOutput, readError).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        try { await Task.WhenAll(readOutput, readError).ConfigureAwait(false); } catch (OperationCanceledException) { }
                        throw;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new IOException("Media download failed: " + string.Join(" ", errors).Replace(item.Url, "[source]", StringComparison.Ordinal));
                    if (output == null) throw new IOException("The media engine did not report an output file.");
                    output = Path.GetFullPath(output);
                    var workspacePrefix = Path.GetFullPath(workDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!output.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(output)) throw new IOException("The media engine returned an unexpected output path.");
                    var filename = DownloadNames.Sanitize(Path.GetFileName(output));
                    var finalPath = Path.Combine(item.DestinationDirectory, filename);
                    if (settings.CollisionPolicy == CollisionPolicy.Skip && File.Exists(finalPath))
                    {
                        var existingBytes = new FileInfo(finalPath).Length;
                        Directory.Delete(workDirectory, recursive: true);
                        return new ExternalDownloadResult(finalPath, existingBytes) { Skipped = true };
                    }
                    if (settings.CollisionPolicy == CollisionPolicy.Rename)
                    {
                        var attempt = 0;
                        while (true)
                        {
                            if (attempt > 0) finalPath = Path.Combine(item.DestinationDirectory, Path.GetFileNameWithoutExtension(filename) + " (" + attempt + ")" + Path.GetExtension(filename));
                            try { File.Move(output, finalPath, overwrite: false); break; }
                            catch (IOException) when (File.Exists(finalPath) && ++attempt < 1000) { }
                        }
                    }
                    else File.Move(output, finalPath, overwrite: settings.CollisionPolicy == CollisionPolicy.Overwrite);
                    var finalBytes = new FileInfo(finalPath).Length;
                    Directory.Delete(workDirectory, recursive: true);
                    return new ExternalDownloadResult(finalPath, finalBytes);
                }
                finally { if (cookieJar != null && File.Exists(cookieJar)) File.Delete(cookieJar); }
            }
            catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException) { lastError = ex; }
        }
        throw new IOException(lastError?.Message ?? "The media download could not be completed.", lastError);
    }

    private static bool SameOrigin(string left, string right) => new Uri(left).GetLeftPart(UriPartial.Authority).Equals(new Uri(right).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    public Task CleanupAsync(DownloadItem item)
    {
        var path = Path.Combine(Path.GetFullPath(item.DestinationDirectory), ".flux-media-" + item.Id.ToString("N"));
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }
}
