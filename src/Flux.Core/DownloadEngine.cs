using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Flux.Core;

internal sealed record TransferMetadata(long Length, bool Ranges, string? EntityTag, DateTimeOffset? LastModified, string? FileName);
internal sealed record TransferProgress(long Downloaded, long Total, double BytesPerSecond, int Connections, bool Resumable);
internal sealed record TransferResult(string FilePath, long Bytes);

internal sealed class SegmentState
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Completed;
    public long SavedCompleted { get; set; }
    internal long Length => End - Start + 1;
}

internal sealed class TransferState
{
    [System.Text.Json.Serialization.JsonIgnore] internal object Gate { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore] internal int ActiveConnections;
    public int Version { get; set; } = 1;
    public string Url { get; set; } = "";
    public long Length { get; set; }
    public string? EntityTag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public List<SegmentState> Segments { get; set; } = [];
}

internal sealed class RestartTransferException(string message) : IOException(message);
internal sealed class DownloadSkippedException(string message) : IOException(message);

/// <summary>Each invocation owns exactly one partial file; no target executable is ever loaded.</summary>
internal sealed class DownloadEngine(HttpClient http, BandwidthLimiter sharedBandwidth)
{
    private const int BufferSize = 64 * 1024;
    private const long MinSegmentSize = 512 * 1024;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(35);
    private static readonly HashSet<string> EngineHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Range", "If-Range", "Content-Length", "Content-Range", "Accept-Encoding", "Connection", "Transfer-Encoding", "Proxy-Authorization" };

    internal static string PartialPath(DownloadItem item) => Path.Combine(item.PartialDirectory ?? item.DestinationDirectory, $".flux-{item.Id:N}.sfpart");
    internal static string ResumePath(DownloadItem item) => PartialPath(item) + ".sfresume";

    internal static Dictionary<string, string> ValidateHeaders(Dictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null) return result;
        if (headers.Count > 32) throw new ArgumentException("At most 32 request headers are supported.");
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || value is null || name.Length > 128 || value.Length > 16_384 || name.Any(c => c <= 32 || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c)) || value.Contains('\r') || value.Contains('\n'))
                throw new ArgumentException("A request header name or value is invalid.");
            if (EngineHeaders.Contains(name)) throw new ArgumentException($"The {name} header is managed by the download engine.");
            using var probe = new HttpRequestMessage();
            if (!probe.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException($"The {name} header cannot be used for a download request.");
            result.Add(name, value);
        }
        return result;
    }

    internal async Task<TransferResult> RunAsync(
        DownloadItem item,
        IReadOnlyDictionary<string, string> headers,
        int requestedConnections,
        DownloadSettings settings,
        Action<TransferMetadata> resolved,
        Action<TransferProgress> progress,
        Func<string> chooseDestination,
        CancellationToken cancellationToken)
    {
        var original = DownloadNames.ValidateUrl(item.Url);
        var sources = new[] { item.Url }.Concat(item.Mirrors ?? []).Distinct(StringComparer.Ordinal).ToArray();
        Exception? last = null;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = item.Clone(); candidate.Url = source;
            var uri = DownloadNames.ValidateUrl(source);
            IReadOnlyDictionary<string, string> safeHeaders = uri.Scheme == original.Scheme && uri.Host.Equals(original.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == original.Port
                ? headers : headers.Where(h => h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
            try { return await RunSourceAsync(candidate, safeHeaders, requestedConnections, settings, resolved, progress, chooseDestination, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or HttpRequestException && exception is not DownloadSkippedException && !cancellationToken.IsCancellationRequested) { last = exception; }
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(last ?? new IOException("No download source is available.")).Throw();
        throw new InvalidOperationException();
    }

    private async Task<TransferResult> RunSourceAsync(DownloadItem item, IReadOnlyDictionary<string, string> headers, int requestedConnections, DownloadSettings settings, Action<TransferMetadata> resolved, Action<TransferProgress> progress, Func<string> chooseDestination, CancellationToken cancellationToken)
    {
        var uri = DownloadNames.ValidateUrl(item.Url);
        Directory.CreateDirectory(item.DestinationDirectory);
        var partial = PartialPath(item);
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        var checkpoint = ResumePath(item);
        var cap = item.SpeedLimitBytesPerSecond ?? settings.SpeedLimitBytesPerSecond;
        if (settings.GlobalSpeedLimitBytesPerSecond > 0)
        {
            var share = Math.Max(1, settings.GlobalSpeedLimitBytesPerSecond / settings.MaxConcurrentDownloads);
            cap = cap <= 0 ? share : Math.Min(cap, share);
        }
        var bandwidth = new BandwidthLimiter(cap);

        // A range server may stop honoring If-Range or may change its entity during a transfer.
        // In that case all range writers are joined before beginning one fresh sequential stream.
        var forceSequential = false;
        for (var restart = 0; restart < 3; restart++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await ProbeWithRetryAsync(uri, headers, settings.RetryCount, cancellationToken).ConfigureAwait(false);
            var resumable = !forceSequential && metadata.Ranges && metadata.Length >= 0 && (metadata.EntityTag is not null || metadata.LastModified.HasValue);
            var effective = metadata with { Ranges = resumable };
            resolved(effective);
            var state = await LoadStateAsync(checkpoint, partial, item.Url, metadata, resumable, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                var connections = resumable && metadata.Length > 0
                    ? Math.Clamp((int)Math.Min(metadata.Length / MinSegmentSize, requestedConnections), 1, 32)
                    : 1;
                state = NewState(item.Url, metadata, connections);
                // No reusable state means existing bytes must never be adopted just because the size matches.
                await using var fresh = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.Read);
                if (metadata.Length >= 0) fresh.SetLength(metadata.Length);
            }

            await using var file = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
            state.ActiveConnections = Math.Max(1, Math.Min(requestedConnections, state.Segments.Count(s => Interlocked.Read(ref s.Completed) < s.Length)));
            var meter = new ProgressMeter(state, progress, effective.Ranges);
            var checkpointGate = new SemaphoreSlim(1, 1);
            async Task CheckpointAsync()
            {
                await checkpointGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Capture counters before the disk flush, so the durable checkpoint never claims
                    // writes that began after that flush. Extra bytes on disk are safe to download again.
                    var snapshot = CopyForSave(state);
                    file.Flush(flushToDisk: true);
                    await StateStore.WriteAsync(checkpoint, snapshot).ConfigureAwait(false);
                }
                finally { checkpointGate.Release(); }
            }

            using var checkpointCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var checkpointTask = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(checkpointCancellation.Token).ConfigureAwait(false))
                    await CheckpointAsync().ConfigureAwait(false);
            });

            Exception? transferFailure = null;
            try
            {
                meter.Report(force: true);
                await CheckpointAsync().ConfigureAwait(false);
                if (metadata.Length == 0)
                {
                    file.SetLength(0);
                }
                else if (resumable)
                {
                    await DownloadAdaptiveAsync(uri, headers, file, state, requestedConnections, settings.RetryCount, meter, bandwidth, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DownloadSequentialAsync(uri, headers, file, state, settings.RetryCount, meter, bandwidth, cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Length >= 0 && CompletedBytes(state) != state.Length)
                    throw new IOException("The server ended the download before all expected bytes arrived.");
            }
            catch (Exception exception) { transferFailure = exception; }
            finally
            {
                checkpointCancellation.Cancel();
                try { await checkpointTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { transferFailure ??= exception; }
                try { await CheckpointAsync().ConfigureAwait(false); }
                catch (Exception exception) { transferFailure ??= exception; }
                checkpointGate.Dispose();
                meter.Report(force: true);
            }

            if (transferFailure is RestartTransferException && !cancellationToken.IsCancellationRequested)
            {
                forceSequential = true;
                // File is still owned exclusively here and is disposed at the end of this iteration.
                if (File.Exists(checkpoint)) File.Delete(checkpoint);
                continue;
            }
            if (transferFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(transferFailure).Throw();

            var bytes = CompletedBytes(state);
            if (state.Length < 0) state.Length = bytes;
            file.SetLength(bytes);
            file.Flush(flushToDisk: true);
            // Close our handle before the same-volume atomic move, which refuses overwrite.
            await file.DisposeAsync().ConfigureAwait(false);
            string destination;
            var attempts = 0;
            while (true)
            {
                destination = chooseDestination();
                if (settings.CollisionPolicy == CollisionPolicy.Skip && (File.Exists(destination) || Directory.Exists(destination))) throw new DownloadSkippedException("The destination exists; skipped by filename policy.");
                try { File.Move(partial, destination, overwrite: settings.CollisionPolicy == CollisionPolicy.Overwrite); break; }
                catch (IOException) when (settings.CollisionPolicy == CollisionPolicy.Rename && File.Exists(destination) && ++attempts < 1000) { }
            }
            if (File.Exists(checkpoint)) File.Delete(checkpoint);
            return new TransferResult(destination, bytes);
        }
        throw new IOException("The server repeatedly changed the download. Try again when the file is stable.");
    }

    private sealed record RangeWorker(SegmentState Segment, CancellationTokenSource Cancellation, Task Task);

    private async Task DownloadAdaptiveAsync(Uri uri, IReadOnlyDictionary<string, string> headers, FileStream file, TransferState state, int requestedConnections, int retries, ProgressMeter meter, BandwidthLimiter bandwidth, CancellationToken cancellationToken)
    {
        using var all = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workers = new List<RangeWorker>();
        var pending = new Queue<SegmentState>(state.Segments.Where(s => Interlocked.Read(ref s.Completed) < s.Length));
        var limit = Math.Clamp(requestedConnections, 1, 32);
        void Start(SegmentState segment)
        {
            var local = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
            var task = Task.Run(() => DownloadRangeAsync(uri, headers, file, state, segment, retries, meter, bandwidth, local.Token), CancellationToken.None);
            workers.Add(new RangeWorker(segment, local, task));
            Volatile.Write(ref state.ActiveConnections, workers.Count);
        }
        try
        {
            while (pending.Count > 0 && workers.Count < limit) Start(pending.Dequeue());
            while (workers.Count > 0)
            {
                await Task.WhenAny(workers.Select(w => w.Task)).ConfigureAwait(false);
                foreach (var finished in workers.Where(w => w.Task.IsCompleted).ToArray())
                {
                    await finished.Task.ConfigureAwait(false);
                    workers.Remove(finished); finished.Cancellation.Dispose();
                }
                cancellationToken.ThrowIfCancellationRequested();
                while (pending.Count > 0 && workers.Count < limit) Start(pending.Dequeue());
                // A freed connection takes half of a still-active region. Join its writer
                // before changing ownership, so no pending write can overlap the new range.
                if (workers.Count > 0 && workers.Count < limit && state.Segments.Count < 1024)
                {
                    var victim = workers.OrderByDescending(w => w.Segment.Length - Interlocked.Read(ref w.Segment.Completed)).First();
                    if (victim.Segment.Length - Interlocked.Read(ref victim.Segment.Completed) >= 2 * MinSegmentSize)
                    {
                        victim.Cancellation.Cancel();
                        try { await victim.Task.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!all.IsCancellationRequested) { }
                        workers.Remove(victim); victim.Cancellation.Dispose();
                        SegmentState? stolen = null;
                        lock (state.Gate)
                        {
                            var left = victim.Segment.Length - Interlocked.Read(ref victim.Segment.Completed);
                            if (left >= 2 * MinSegmentSize)
                            {
                                var split = victim.Segment.Start + Interlocked.Read(ref victim.Segment.Completed) + left / 2;
                                stolen = new SegmentState { Start = split, End = victim.Segment.End };
                                victim.Segment.End = split - 1;
                                state.Segments.Add(stolen);
                                state.Segments.Sort((a, b) => a.Start.CompareTo(b.Start));
                            }
                        }
                        if (Interlocked.Read(ref victim.Segment.Completed) < victim.Segment.Length) Start(victim.Segment);
                        if (stolen is not null) Start(stolen);
                    }
                }
                Volatile.Write(ref state.ActiveConnections, Math.Max(1, workers.Count));
                meter.Report(force: true);
            }
        }
        finally
        {
            all.Cancel();
            foreach (var worker in workers) worker.Cancellation.Cancel();
            try { await Task.WhenAll(workers.Select(w => w.Task)).ConfigureAwait(false); } catch { }
            foreach (var worker in workers) worker.Cancellation.Dispose();
        }
    }

    private async Task<TransferMetadata> ProbeWithRetryAsync(Uri uri, IReadOnlyDictionary<string, string> headers, int retries, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // GET bytes=0-0 works even on servers that reject HEAD and measures actual range support.
                using var response = await SendAsync(uri, headers, 0, 0, null, null, cancellationToken).ConfigureAwait(false);
                var encodingOkay = response.Content.Headers.ContentEncoding.All(e => e.Equals("identity", StringComparison.OrdinalIgnoreCase));
                if (!encodingOkay) throw new IOException("The server returned a compressed representation despite an identity download request.");
                var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
                var modified = response.Content.Headers.LastModified;
                var disposition = response.Content.Headers.ContentDisposition;
                var filename = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange is { HasLength: true, Length: 0 })
                    return new TransferMetadata(0, false, etag, modified, filename);
                EnsureSuccessful(response);
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || range.From != 0 || range.To != 0 || range.Length is null or <= 0 || response.Content.Headers.ContentLength is not (null or 1))
                        throw new IOException("The server returned an invalid range response while resolving the download.");
                    return new TransferMetadata(range.Length.Value, true, etag, modified, filename);
                }
                if (response.StatusCode != HttpStatusCode.OK) throw new IOException($"The server returned unexpected HTTP {(int)response.StatusCode}.");
                return new TransferMetadata(response.Content.Headers.ContentLength ?? -1, false, etag, modified, filename);
            }
            catch (Exception exception) when (CanRetry(exception, cancellationToken) && attempt < retries)
            {
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadRangeAsync(Uri uri, IReadOnlyDictionary<string, string> headers, FileStream file, TransferState state, SegmentState segment, int retries, ProgressMeter meter, BandwidthLimiter bandwidth, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = Interlocked.Read(ref segment.Completed);
            if (completed == segment.Length) return;
            try
            {
                var start = segment.Start + completed;
                using var response = await SendAsync(uri, headers, start, segment.End, state.EntityTag, state.LastModified, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    throw new RestartTransferException("The server no longer honors this range; restarting safely.");
                EnsureSuccessful(response);
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || range.From != start || range.To != segment.End || range.Length != state.Length)
                    throw new RestartTransferException("A range response did not match the requested file region.");
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != segment.End - start + 1)
                    throw new RestartTransferException("The server returned an inconsistent range length.");
                if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new RestartTransferException("The server changed the representation encoding.");
                if (state.EntityTag is not null && response.Headers.ETag?.ToString() != state.EntityTag)
                    throw new RestartTransferException("The server changed the file while it was downloading.");
                if (state.EntityTag is null && state.LastModified.HasValue && response.Content.Headers.LastModified != state.LastModified)
                    throw new RestartTransferException("The server changed the file modification date.");
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var remaining = segment.Length - Interlocked.Read(ref segment.Completed);
                    if (remaining == 0)
                    {
                        var extra = await ReadWithTimeoutAsync(body, buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                        if (extra != 0) throw new RestartTransferException("A range response contained more bytes than requested.");
                        return;
                    }
                    var count = await ReadWithTimeoutAsync(body, buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (count == 0) throw new IOException("The server interrupted a range before all expected bytes arrived.");
                    await bandwidth.WaitAsync(count, cancellationToken).ConfigureAwait(false);
                    await sharedBandwidth.WaitAsync(count, cancellationToken).ConfigureAwait(false);
                    await RandomAccess.WriteAsync(file.SafeFileHandle, buffer.AsMemory(0, count), segment.Start + Interlocked.Read(ref segment.Completed), cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref segment.Completed, count);
                    meter.Report();
                }
            }
            catch (RestartTransferException) { throw; }
            catch (Exception exception) when (CanRetry(exception, cancellationToken) && attempt < retries)
            {
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadSequentialAsync(Uri uri, IReadOnlyDictionary<string, string> headers, FileStream file, TransferState state, int retries, ProgressMeter meter, BandwidthLimiter bandwidth, CancellationToken cancellationToken)
    {
        var segment = state.Segments[0];
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Sequential retries always restart. Appending to a response without validated ranges can corrupt a file.
                Interlocked.Exchange(ref segment.Completed, 0);
                file.SetLength(0);
                meter.ResetBaseline();
                using var response = await SendAsync(uri, headers, null, null, null, null, cancellationToken).ConfigureAwait(false);
                EnsureSuccessful(response);
                if (response.StatusCode != HttpStatusCode.OK) throw new IOException("The server did not return a complete file response.");
                if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("The server returned an unsupported compressed representation.");
                var length = response.Content.Headers.ContentLength ?? -1;
                state.Length = length;
                segment.Start = 0;
                segment.End = length >= 0 ? length - 1 : long.MaxValue - 1;
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var count = await ReadWithTimeoutAsync(body, buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    var current = Interlocked.Read(ref segment.Completed);
                    if (length >= 0 && count > length - current) throw new IOException("The server sent more bytes than the declared file size.");
                    await bandwidth.WaitAsync(count, cancellationToken).ConfigureAwait(false);
                    await sharedBandwidth.WaitAsync(count, cancellationToken).ConfigureAwait(false);
                    await RandomAccess.WriteAsync(file.SafeFileHandle, buffer.AsMemory(0, count), current, cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref segment.Completed, count);
                    meter.Report();
                }
                if (length >= 0 && Interlocked.Read(ref segment.Completed) != length) throw new IOException("The server interrupted the download before all expected bytes arrived.");
                if (length < 0)
                {
                    state.Length = Interlocked.Read(ref segment.Completed);
                    segment.End = state.Length - 1;
                }
                return;
            }
            catch (Exception exception) when (CanRetry(exception, cancellationToken) && attempt < retries)
            {
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri original, IReadOnlyDictionary<string, string> headers, long? start, long? end, string? entityTag, DateTimeOffset? modified, CancellationToken cancellationToken)
    {
        var current = original;
        for (var redirects = 0; redirects <= 8; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            if (!headers.ContainsKey("User-Agent")) request.Headers.UserAgent.ParseAdd("Flux/1.0.0");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            // User-supplied headers (including cookies/API keys) never cross an origin boundary.
            foreach (var header in headers)
                if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) || (current.Scheme == original.Scheme && current.Host.Equals(original.Host, StringComparison.OrdinalIgnoreCase) && current.Port == original.Port)) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (start.HasValue) request.Headers.Range = new RangeHeaderValue(start, end);
            if (entityTag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(entityTag));
            else if (modified.HasValue) request.Headers.IfRange = new RangeConditionHeaderValue(modified.Value);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(IoTimeout);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("The download server did not respond within 35 seconds."); }
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            try
            {
                var location = response.Headers.Location ?? throw new IOException("A download redirect has no destination.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                DownloadNames.ValidateUrl(next.AbsoluteUri);
                if (current.Scheme == "https" && next.Scheme != "https") throw new IOException("The server redirected a secure download to an insecure HTTP address.");
                current = next;
            }
            finally { response.Dispose(); }
        }
        throw new IOException("The download server redirected too many times.");
    }

    private static async Task<int> ReadWithTimeoutAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IoTimeout);
        try { return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("No download data arrived for 35 seconds."); }
    }

    private static void EnsureSuccessful(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException($"The server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
    }

    private static bool CanRetry(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (exception is HttpRequestException { StatusCode: { } status }) return (int)status is 408 or 425 or 429 or >= 500;
        return exception is HttpRequestException or IOException or OperationCanceledException;
    }

    private static Task BackoffAsync(int attempt, CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, attempt), 5000)), cancellationToken);

    private static TransferState NewState(string url, TransferMetadata metadata, int connections)
    {
        var state = new TransferState { Url = url, Length = metadata.Length, EntityTag = metadata.EntityTag, LastModified = metadata.LastModified };
        if (metadata.Length <= 0)
        {
            state.Segments.Add(new SegmentState { Start = 0, End = metadata.Length == 0 ? -1 : long.MaxValue - 1 });
            return state;
        }
        var position = 0L;
        for (var index = 0; index < connections; index++)
        {
            var size = metadata.Length / connections + (index < metadata.Length % connections ? 1 : 0);
            state.Segments.Add(new SegmentState { Start = position, End = position + size - 1 });
            position += size;
        }
        return state;
    }

    private static async Task<TransferState?> LoadStateAsync(string checkpoint, string partial, string url, TransferMetadata metadata, bool resumable, CancellationToken cancellationToken)
    {
        if (!resumable || !File.Exists(partial)) return null;
        TransferState? state;
        try { state = await StateStore.ReadAsync<TransferState>(checkpoint, 1_048_576, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) { return null; }
        if (state is null || state.Version != 1 || state.Url != url || state.Length != metadata.Length || state.EntityTag != metadata.EntityTag || state.LastModified != metadata.LastModified || state.Segments is null || state.Segments.Count is < 1 or > 1024 || new FileInfo(partial).Length != metadata.Length) return null;
        var position = 0L;
        foreach (var segment in state.Segments)
        {
            if (segment is null || segment.Start != position || segment.End < segment.Start || segment.End >= state.Length || segment.SavedCompleted < 0 || segment.SavedCompleted > segment.Length) return null;
            segment.Completed = segment.SavedCompleted;
            position = segment.End + 1;
        }
        return position == state.Length ? state : null;
    }

    private static TransferState CopyForSave(TransferState state)
    {
        lock (state.Gate) return new() { Url = state.Url, Length = state.Length, EntityTag = state.EntityTag, LastModified = state.LastModified, Segments = state.Segments.Select(s => new SegmentState { Start = s.Start, End = s.End, SavedCompleted = Interlocked.Read(ref s.Completed) }).ToList() };
    }
    private static long CompletedBytes(TransferState state) { lock (state.Gate) return state.Segments.Sum(s => Interlocked.Read(ref s.Completed)); }

    private sealed class ProgressMeter(TransferState state, Action<TransferProgress> report, bool resumable)
    {
        private readonly object _gate = new();
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private long _baseline = CompletedBytes(state);
        private long _lastReport;
        private int _peakConnections = Math.Max(1, state.ActiveConnections);
        internal void ResetBaseline() { lock (_gate) { _baseline = 0; _watch.Restart(); _lastReport = 0; } }
        internal void Report(bool force = false)
        {
            lock (_gate)
            {
                if (!force && _watch.ElapsedMilliseconds - _lastReport < 100) return;
                _lastReport = _watch.ElapsedMilliseconds;
                var done = CompletedBytes(state);
                _peakConnections = Math.Max(_peakConnections, Volatile.Read(ref state.ActiveConnections));
                report(new TransferProgress(done, state.Length, Math.Max(0, done - _baseline) / Math.Max(0.1, _watch.Elapsed.TotalSeconds), _peakConnections, resumable));
            }
        }
    }

}

internal sealed class BandwidthLimiter(long bytesPerSecond)
    {
        private readonly object _gate = new();
        private double _next = Stopwatch.GetTimestamp();
        internal void SetLimit(long value) { lock (_gate) { bytesPerSecond = value; _next = Stopwatch.GetTimestamp(); } }
        internal async Task WaitAsync(int bytes, CancellationToken cancellationToken)
        {
            double delay;
            lock (_gate)
            {
                if (bytesPerSecond <= 0) return;
                var now = Stopwatch.GetTimestamp();
                _next = Math.Max(now, _next) + (double)bytes * Stopwatch.Frequency / bytesPerSecond;
                delay = (_next - now) / Stopwatch.Frequency;
            }
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
        }
}
