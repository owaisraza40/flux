using System.Net;
using System.Text.Json;

namespace Flux.Core;

/// <summary>Owns the queue, durable history, and the lifetime of every download writer.</summary>
public sealed class DownloadManager : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string _statePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private readonly HttpClient _http;
    private readonly DownloadEngine _engine;
    private readonly BandwidthLimiter _globalBandwidth = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<QueueEntry> _entries = [];
    private readonly Dictionary<Guid, RunningOperation> _running = [];
    private DownloadSettings _settings = new();
    private Task? _pumpTask;
    private bool _queuePaused;
    private bool _initialized;
    private bool _disposed;
    private Task? _disposeTask;

    public event EventHandler? Changed;
    public event EventHandler<DownloadItem>? DownloadCompleted;
    public IExternalDownloadProcessor? ExternalProcessor { get; set; }
    public string? LastPersistenceError { get; private set; }
    public bool IsQueuePaused { get { lock (_gate) return _queuePaused; } }
    public DownloadSettings Settings { get { lock (_gate) return _settings.Clone(); } }

    public DownloadManager(string dataDirectory, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("Choose a state directory.", nameof(dataDirectory));
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _statePath = Path.Combine(_dataDirectory, "downloads.json");
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _engine = new DownloadEngine(_http, _globalBandwidth);
    }

    public async Task InitializeAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
        }
        Directory.CreateDirectory(_dataDirectory);
        QueueState? saved = null;
        try { saved = await StateStore.ReadAsync<QueueState>(_statePath, 32 * 1024 * 1024).ConfigureAwait(false); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            // Preserve malformed history for recovery rather than silently replacing the only copy.
            var preserved = _statePath + ".invalid-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.Move(_statePath, preserved, overwrite: false);
            LastPersistenceError = $"Saved history could not be read. The original was preserved at {preserved}.";
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
            if (saved is not null && saved.Version == 1)
            {
                try { (saved.Settings ?? new DownloadSettings()).Validate(); _settings = saved.Settings ?? new DownloadSettings(); }
                catch (ArgumentException) { LastPersistenceError = "Some saved settings were invalid and have been reset."; }
                _queuePaused = saved.QueuePaused;
                foreach (var entry in (saved.Entries ?? []).Take(100_000))
                {
                    try
                    {
                        if (entry?.Item is null) { LastPersistenceError = "One or more invalid history entries were skipped."; continue; }
                        DownloadNames.ValidateUrl(entry.Item.Url);
                        if (entry.Item.Id == Guid.Empty || _entries.Any(e => e.Item.Id == entry.Item.Id)) continue;
                        entry.Item.DestinationDirectory = Path.GetFullPath(entry.Item.DestinationDirectory);
                        entry.Item.PartialDirectory = Path.GetFullPath(entry.Item.PartialDirectory ?? entry.Item.DestinationDirectory);
                        entry.Item.FileName = DownloadNames.Sanitize(entry.Item.FileName);
                        entry.Item.FilePath = Path.Combine(entry.Item.DestinationDirectory, entry.Item.FileName);
                        entry.Headers = DownloadEngine.ValidateHeaders(entry.Headers);
                        entry.RequestedConnections = Math.Clamp(entry.RequestedConnections, 1, 32);
                        entry.Item.Mirrors = (entry.Item.Mirrors ?? []).Take(16).Select(url => DownloadNames.ValidateUrl(url).AbsoluteUri).ToList();
                        if (!Enum.IsDefined(entry.Item.Priority)) entry.Item.Priority = DownloadPriority.Normal;
                        if (!Enum.IsDefined(entry.CollisionPolicy)) entry.CollisionPolicy = CollisionPolicy.Rename;
                        if (!Enum.IsDefined(entry.Item.Status)) entry.Item.Status = DownloadStatus.Paused;
                        if (entry.Item.Status is DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Queued)
                            entry.Item.Status = DownloadStatus.Paused;
                        entry.Item.BytesPerSecond = 0;
                        _entries.Add(entry);
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { LastPersistenceError = "One or more invalid history entries were skipped."; }
                }
            }
            _initialized = true;
            _globalBandwidth.SetLimit(_settings.GlobalSpeedLimitBytesPerSecond);
            _pumpTask = Task.Run(PumpLoopAsync);
        }
        await PersistAsync().ConfigureAwait(false);
        NotifyChanged();
    }

    public IReadOnlyList<DownloadItem> GetSnapshot()
    {
        lock (_gate) return _entries.OrderBy(e => e.Item.QueueOrder).Select(entry => entry.Item.Clone()).ToArray();
    }

    public async Task<DownloadItem> AddAsync(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = DownloadNames.ValidateUrl(request.Url);
        var suppliedHeaders = new Dictionary<string, string>(request.Headers ?? [], StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.UserAgent)) suppliedHeaders["User-Agent"] = request.UserAgent;
        if (!string.IsNullOrWhiteSpace(request.Referrer)) suppliedHeaders["Referer"] = DownloadNames.ValidateUrl(request.Referrer).AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(request.Cookies)) suppliedHeaders["Cookie"] = request.Cookies;
        var headers = DownloadEngine.ValidateHeaders(suppliedHeaders);
        if (!Enum.IsDefined(request.Priority) || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.CompletionAction)) throw new ArgumentException("Choose a valid download option.");
        if (request.SpeedLimitBytesPerSecond is < 0 or > 1_000_000_000_000) throw new ArgumentException("The download speed limit must be zero or positive.");
        if (request.Mirrors?.Count > 16) throw new ArgumentException("At most sixteen alternate mirrors are supported.");
        if (request.StopAt.HasValue && request.StopAt <= (request.ScheduledAt ?? DateTimeOffset.Now)) throw new ArgumentException("The scheduled stop must be after the download start.");
        var mirrors = (request.Mirrors ?? []).Select(url => DownloadNames.ValidateUrl(url).AbsoluteUri).Distinct(StringComparer.Ordinal).ToList();
        QueueEntry entry;
        lock (_gate)
        {
            EnsureReady();
            var destination = string.IsNullOrWhiteSpace(request.DestinationDirectory) ? _settings.DefaultDownloadDirectory : request.DestinationDirectory;
            destination = Path.GetFullPath(destination);
            var fileName = DownloadNames.Sanitize(request.FileName ?? DownloadNames.FromUrl(uri));
            var category = string.IsNullOrWhiteSpace(request.Category) ? DownloadNames.Category(fileName) : request.Category.Trim();
            if (_settings.AutoSortCategories && _settings.CategoryDirectories.TryGetValue(category, out var categoryDirectory)) destination = categoryDirectory;
            var item = new DownloadItem
            {
                Url = uri.AbsoluteUri,
                FileName = fileName,
                DestinationDirectory = destination,
                PartialDirectory = destination,
                Category = category,
                Priority = request.Priority, QueueOrder = _entries.Count == 0 ? 0 : _entries.Max(e => e.Item.QueueOrder) + 1,
                SpeedLimitBytesPerSecond = request.SpeedLimitBytesPerSecond, StopAt = request.StopAt,
                Mirrors = mirrors, Kind = request.Kind, CompletionAction = request.CompletionAction,
                CompletionScript = request.CompletionScript, ScanOnCompletion = request.ScanOnCompletion,
                UserAgent = request.UserAgent, Referrer = request.Referrer, Cookies = request.Cookies,
                ScheduledAt = request.ScheduledAt,
                Status = request.ScheduledAt > DateTimeOffset.UtcNow ? DownloadStatus.Scheduled : _queuePaused ? DownloadStatus.Paused : DownloadStatus.Queued
            };
            entry = new QueueEntry { Item = item, Headers = headers, RequestedConnections = Math.Clamp(request.Connections ?? _settings.ConnectionsPerDownload, 1, 32), NameResolved = request.FileName is not null, CategoryExplicit = !string.IsNullOrWhiteSpace(request.Category), CollisionPolicy = _settings.CollisionPolicy };
            ChooseDestinationLocked(entry);
            _entries.Add(entry);
        }
        try
        {
            Directory.CreateDirectory(entry.Item.DestinationDirectory);
            await PersistAsync().ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) _entries.Remove(entry);
            throw;
        }
        NotifyChanged();
        Pump();
        return entry.Item.Clone();
    }

    public async Task PauseAsync(Guid id)
    {
        Task? runningTask = null;
        lock (_gate)
        {
            EnsureReady();
            var entry = FindLocked(id);
            if (entry.Item.Status is DownloadStatus.Completed or DownloadStatus.Skipped or DownloadStatus.Cancelled or DownloadStatus.Failed) return;
            entry.SchedulePaused = false;
            entry.Item.Status = DownloadStatus.Paused;
            entry.Item.BytesPerSecond = 0;
            if (_running.TryGetValue(id, out var operation))
            {
                operation.Cancellation.Cancel();
                runningTask = operation.Task;
            }
        }
        NotifyChanged();
        if (runningTask is not null) await runningTask.ConfigureAwait(false);
        await PersistAsync().ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid id)
    {
        Task? finishing = null;
        lock (_gate)
        {
            EnsureReady();
            var entry = FindLocked(id);
            if (entry.Item.Status == DownloadStatus.Completed) return;
            entry.SchedulePaused = false;
            // A one-shot stop is consumed; an explicit resume is a new user decision.
            if (entry.Item.StopAt <= DateTimeOffset.UtcNow) entry.Item.StopAt = null;
            if (_running.TryGetValue(id, out var operation))
            {
                if (!operation.Cancellation.IsCancellationRequested) return;
                finishing = operation.Task;
            }
        }
        if (finishing is not null) await finishing.ConfigureAwait(false);
        lock (_gate)
        {
            EnsureReady();
            var entry = FindLocked(id);
            if (entry.Item.Status == DownloadStatus.Completed) return;
            _queuePaused = false;
            entry.Item.Error = null;
            entry.Item.Status = entry.Item.ScheduledAt > DateTimeOffset.UtcNow ? DownloadStatus.Scheduled : DownloadStatus.Queued;
        }
        await PersistAsync().ConfigureAwait(false);
        NotifyChanged();
        Pump();
    }

    public Task RetryAsync(Guid id) => ResumeAsync(id);

    public async Task SetPriorityAsync(Guid id, DownloadPriority priority)
    {
        if (!Enum.IsDefined(priority)) throw new ArgumentException("Choose a valid priority.");
        lock (_gate) { EnsureReady(); FindLocked(id).Item.Priority = priority; }
        await PersistAsync().ConfigureAwait(false); NotifyChanged(); Pump();
    }

    public async Task MoveAsync(Guid id, int newIndex)
    {
        lock (_gate)
        {
            EnsureReady(); var ordered = _entries.OrderBy(e => e.Item.QueueOrder).ToList();
            var entry = FindLocked(id); ordered.Remove(entry); ordered.Insert(Math.Clamp(newIndex, 0, ordered.Count), entry);
            for (var index = 0; index < ordered.Count; index++) ordered[index].Item.QueueOrder = index;
        }
        await PersistAsync().ConfigureAwait(false); NotifyChanged(); Pump();
    }

    public async Task RemoveAsync(Guid id, bool deletePartial = false)
    {
        await PauseAsync(id).ConfigureAwait(false);
        QueueEntry? entry;
        lock (_gate)
        {
            EnsureReady();
            entry = _entries.FirstOrDefault(e => e.Item.Id == id);
            if (entry is null) return;
            _entries.Remove(entry);
        }
        if (deletePartial)
        {
            var partial = DownloadEngine.PartialPath(entry.Item);
            var resume = DownloadEngine.ResumePath(entry.Item);
            if (File.Exists(partial)) File.Delete(partial);
            if (File.Exists(resume)) File.Delete(resume);
            if (ExternalProcessor is { } processor && processor.CanHandle(entry.Item))
                await processor.CleanupAsync(entry.Item.Clone()).ConfigureAwait(false);
        }
        await PersistAsync().ConfigureAwait(false);
        NotifyChanged();
    }

    public async Task PauseAllAsync()
    {
        Task[] running;
        lock (_gate)
        {
            EnsureReady();
            _queuePaused = true;
            foreach (var entry in _entries)
            {
                entry.SchedulePaused = false;
                if (entry.Item.Status is DownloadStatus.Queued or DownloadStatus.Resolving or DownloadStatus.Downloading)
                {
                    entry.Item.Status = DownloadStatus.Paused;
                    entry.Item.BytesPerSecond = 0;
                }
            }
            running = _running.Values.Select(o => o.Task).ToArray();
            foreach (var operation in _running.Values) operation.Cancellation.Cancel();
        }
        NotifyChanged();
        await Task.WhenAll(running).ConfigureAwait(false);
        await PersistAsync().ConfigureAwait(false);
    }

    public async Task ResumeAllAsync()
    {
        Task[] finishing;
        lock (_gate)
        {
            EnsureReady();
            finishing = _running.Values.Where(o => o.Cancellation.IsCancellationRequested).Select(o => o.Task).ToArray();
        }
        await Task.WhenAll(finishing).ConfigureAwait(false);
        lock (_gate)
        {
            EnsureReady();
            _queuePaused = false;
            foreach (var entry in _entries.Where(e => e.Item.Status == DownloadStatus.Paused))
            {
                entry.SchedulePaused = false;
                if (entry.Item.StopAt <= DateTimeOffset.UtcNow) entry.Item.StopAt = null;
                entry.Item.Error = null;
                entry.Item.Status = entry.Item.ScheduledAt > DateTimeOffset.UtcNow ? DownloadStatus.Scheduled : DownloadStatus.Queued;
            }
        }
        await PersistAsync().ConfigureAwait(false);
        NotifyChanged();
        Pump();
    }

    public async Task ClearCompletedAsync()
    {
        lock (_gate)
        {
            EnsureReady();
            _entries.RemoveAll(e => e.Item.Status is DownloadStatus.Completed or DownloadStatus.Skipped);
        }
        await PersistAsync().ConfigureAwait(false);
        NotifyChanged();
    }

    public async Task SaveSettingsAsync(DownloadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        copy.Validate();
        lock (_gate) { EnsureReady(); _settings = copy; _globalBandwidth.SetLimit(copy.GlobalSpeedLimitBytesPerSecond); }
        await PersistAsync().ConfigureAwait(false);
        NotifyChanged();
        Pump();
    }

    private async Task PumpLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false)) Pump();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private void Pump()
    {
        var changed = false;
        lock (_gate)
        {
            if (!_initialized || _disposed) return;
            var now = DateTimeOffset.Now;
            var windowOpen = IsDailyWindowOpen(_settings, now);
            foreach (var entry in _entries)
            {
                var stopped = entry.Item.StopAt <= now;
                if ((!windowOpen || stopped) && entry.Item.Status is DownloadStatus.Queued or DownloadStatus.Resolving or DownloadStatus.Downloading)
                {
                    entry.Item.BytesPerSecond = 0;
                    entry.SchedulePaused = !stopped;
                    entry.Item.ActivityMessage = stopped ? "Paused at the scheduled stop time." : "Waiting for the daily download window.";
                    if (_running.TryGetValue(entry.Item.Id, out var running)) running.Cancellation.Cancel();
                    else entry.Item.Status = DownloadStatus.Paused;
                    changed = true;
                }
                else if (windowOpen && !_queuePaused && entry.SchedulePaused && entry.Item.Status == DownloadStatus.Paused && !_running.ContainsKey(entry.Item.Id))
                {
                    entry.SchedulePaused = false; entry.Item.Status = DownloadStatus.Queued; entry.Item.ActivityMessage = null; changed = true;
                }
            }
            if (_queuePaused || !windowOpen) { if (changed) NotifyChanged(); return; }
            foreach (var entry in _entries.Where(e => e.Item.Status == DownloadStatus.Scheduled && e.Item.ScheduledAt <= DateTimeOffset.UtcNow))
            {
                entry.Item.Status = DownloadStatus.Queued;
                changed = true;
            }
            foreach (var entry in _entries.Where(e => e.Item.Status == DownloadStatus.Queued).OrderBy(e => e.Item.Priority).ThenBy(e => e.Item.QueueOrder).ToArray())
            {
                if (entry.Item.StopAt <= now) { entry.Item.Status = DownloadStatus.Paused; entry.Item.ActivityMessage = "Paused at the scheduled stop time."; changed = true; continue; }
                if (entry.CollisionPolicy == CollisionPolicy.Skip && (File.Exists(entry.Item.FilePath) || Directory.Exists(entry.Item.FilePath))) { entry.Item.Status = DownloadStatus.Skipped; entry.Item.ActivityMessage = "The destination exists; skipped by filename policy."; changed = true; continue; }
                if (_running.Count >= _settings.MaxConcurrentDownloads) break;
                if (_running.ContainsKey(entry.Item.Id)) continue;
                entry.Item.Status = DownloadStatus.Resolving;
                entry.Item.Error = null;
                var operation = new RunningOperation(CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
                _running.Add(entry.Item.Id, operation);
                var settings = _settings.Clone();
                settings.CollisionPolicy = entry.CollisionPolicy;
                operation.Task = Task.Run(() => RunEntryAsync(entry, operation, settings));
                changed = true;
            }
        }
        if (changed) NotifyChanged();
    }

    private static bool IsDailyWindowOpen(DownloadSettings settings, DateTimeOffset now)
    {
        if (!settings.DailyScheduleEnabled) return true;
        var start = TimeOnly.ParseExact(settings.DailyStart, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var stop = TimeOnly.ParseExact(settings.DailyStop, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var time = TimeOnly.FromDateTime(now.LocalDateTime);
        return start == stop || (start < stop ? time >= start && time < stop : time >= start || time < stop);
    }

    private async Task RunEntryAsync(QueueEntry entry, RunningOperation operation, DownloadSettings settings)
    {
        try
        {
            await PersistAsync().ConfigureAwait(false);
            var input = entry.Item.Clone();
            TransferResult result;
            var processor = ExternalProcessor;
            if (processor is not null && processor.CanHandle(input))
            {
                lock (_gate) entry.Item.Status = DownloadStatus.Downloading;
                var progress = new InlineProgress<ExternalDownloadProgress>(value =>
                {
                    lock (_gate) { if(operation.Cancellation.IsCancellationRequested)return; entry.Item.TotalBytes = value.TotalBytes; entry.Item.DownloadedBytes = value.DownloadedBytes; entry.Item.BytesPerSecond = value.BytesPerSecond; entry.Item.ActivityMessage = value.Message; }
                    NotifyChanged();
                });
                var external = await processor.DownloadAsync(input, entry.Headers, settings, progress, operation.Cancellation.Token).ConfigureAwait(false);
                if (external.Skipped)
                {
                    lock (_gate) { entry.Item.FilePath = external.FilePath; entry.Item.FileName = Path.GetFileName(external.FilePath); }
                    throw new DownloadSkippedException("The destination exists; skipped by filename policy.");
                }
                result = new TransferResult(external.FilePath, external.Bytes);
            }
            else result = await _engine.RunAsync(input, entry.Headers, entry.RequestedConnections, settings,
                metadata =>
                {
                    lock (_gate)
                    {
                        if (!entry.NameResolved && !string.IsNullOrWhiteSpace(metadata.FileName))
                        {
                            entry.Item.FileName = DownloadNames.Sanitize(metadata.FileName);
                            if (!entry.CategoryExplicit) entry.Item.Category = DownloadNames.Category(entry.Item.FileName);
                            if (settings.AutoSortCategories && settings.CategoryDirectories.TryGetValue(entry.Item.Category, out var sortedDirectory) && !File.Exists(DownloadEngine.PartialPath(entry.Item))) entry.Item.DestinationDirectory = sortedDirectory;
                            ChooseDestinationLocked(entry);
                            if (entry.Item.Status == DownloadStatus.Skipped) throw new DownloadSkippedException("The destination exists; skipped by filename policy.");
                        }
                        entry.NameResolved = true;
                        entry.Item.TotalBytes = metadata.Length;
                        entry.Item.SupportsRanges = metadata.Ranges;
                        if (!operation.Cancellation.IsCancellationRequested) entry.Item.Status = DownloadStatus.Downloading;
                    }
                    NotifyChanged();
                },
                progress =>
                {
                    lock (_gate)
                    {
                        entry.Item.TotalBytes = progress.Total;
                        entry.Item.DownloadedBytes = progress.Downloaded;
                        entry.Item.Connections = progress.Connections;
                        entry.Item.SupportsRanges = progress.Resumable;
                        entry.Item.BytesPerSecond = operation.Cancellation.IsCancellationRequested ? 0 : progress.BytesPerSecond;
                    }
                    NotifyChanged();
                },
                () => { lock (_gate) return ChooseDestinationLocked(entry); },
                operation.Cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                entry.Item.FilePath = result.FilePath;
                entry.Item.FileName = Path.GetFileName(result.FilePath);
                if (!entry.CategoryExplicit) entry.Item.Category = DownloadNames.Category(entry.Item.FileName);
                entry.Item.TotalBytes = result.Bytes;
                entry.Item.DownloadedBytes = result.Bytes;
                entry.Item.Status = DownloadStatus.Completed;
                entry.Item.CompletedAt = DateTimeOffset.UtcNow;
                entry.Item.Error = null;
                entry.Item.ActivityMessage = "Download complete.";
            }
            NotifyCompleted(entry.Item.Clone());
        }
        catch (DownloadSkippedException exception)
        {
            lock (_gate) { entry.Item.Status = DownloadStatus.Skipped; entry.Item.ActivityMessage = exception.Message; entry.Item.Error = null; }
            foreach (var owned in new[] { DownloadEngine.PartialPath(entry.Item), DownloadEngine.ResumePath(entry.Item) }) { if (File.Exists(owned)) File.Delete(owned); }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            lock (_gate) if (entry.Item.Status != DownloadStatus.Cancelled) entry.Item.Status = DownloadStatus.Paused;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                entry.Item.Status = operation.Cancellation.IsCancellationRequested ? DownloadStatus.Paused : DownloadStatus.Failed;
                entry.Item.Error = operation.Cancellation.IsCancellationRequested ? null : FriendlyError(exception);
            }
        }
        finally
        {
            lock (_gate)
            {
                entry.Item.BytesPerSecond = 0;
                _running.Remove(entry.Item.Id);
            }
            operation.Cancellation.Dispose();
            try { await PersistAsync().ConfigureAwait(false); }
            catch (Exception exception) { LastPersistenceError = FriendlyError(exception); }
            NotifyChanged();
            Pump();
        }
    }

    private string ChooseDestinationLocked(QueueEntry entry)
    {
        Directory.CreateDirectory(entry.Item.DestinationDirectory);
        var baseName = DownloadNames.Sanitize(entry.Item.FileName);
        var extension = Path.GetExtension(baseName);
        var stem = Path.GetFileNameWithoutExtension(baseName);
        var candidate = baseName;
        var explicitPath = Path.Combine(entry.Item.DestinationDirectory, baseName);
        if (Directory.Exists(explicitPath) && entry.CollisionPolicy == CollisionPolicy.Overwrite) throw new IOException("A folder already uses this filename. Choose a different download name.");
        var otherWriter = _entries.Any(other => other != entry && other.Item.Status is not (DownloadStatus.Completed or DownloadStatus.Skipped or DownloadStatus.Cancelled or DownloadStatus.Failed) && string.Equals(other.Item.FilePath, explicitPath, StringComparison.OrdinalIgnoreCase));
        if (!otherWriter && entry.CollisionPolicy is CollisionPolicy.Overwrite or CollisionPolicy.Skip)
        {
            entry.Item.FilePath = explicitPath;
            if (entry.CollisionPolicy == CollisionPolicy.Skip && (File.Exists(explicitPath) || Directory.Exists(explicitPath))) { entry.Item.Status = DownloadStatus.Skipped; entry.Item.ActivityMessage = "The destination exists; skipped by filename policy."; }
            return explicitPath;
        }
        for (var suffix = 0; suffix < 100_000; suffix++)
        {
            var path = Path.Combine(entry.Item.DestinationDirectory, candidate);
            if (!File.Exists(path) && !Directory.Exists(path) && !_entries.Any(other => other != entry && string.Equals(other.Item.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                entry.Item.FileName = candidate;
                entry.Item.FilePath = path;
                return path;
            }
            candidate = $"{stem} ({suffix + 1}){extension}";
        }
        throw new IOException("Could not choose an unused filename in the destination folder.");
    }

    private async Task PersistAsync()
    {
        await _storeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            QueueState state;
            lock (_gate)
            {
                if (!_initialized) return;
                state = new QueueState
                {
                    Settings = _settings.Clone(), QueuePaused = _queuePaused,
                    Entries = _entries.Select(e => new QueueEntry { Item = e.Item.Clone(), Headers = new(e.Headers), RequestedConnections = e.RequestedConnections, NameResolved = e.NameResolved, CollisionPolicy = e.CollisionPolicy, CategoryExplicit = e.CategoryExplicit, SchedulePaused = e.SchedulePaused }).ToList()
                };
            }
            await StateStore.WriteAsync(_statePath, state).ConfigureAwait(false);
        }
        catch (Exception exception) { LastPersistenceError = FriendlyError(exception); throw; }
        finally { _storeGate.Release(); }
    }

    private QueueEntry FindLocked(Guid id) => _entries.FirstOrDefault(e => e.Item.Id == id) ?? throw new ArgumentException("This download is no longer in the queue.");
    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Initialize the download manager first.");
    }
    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler callback in handlers.GetInvocationList())
        {
            try { callback(this, EventArgs.Empty); }
            catch { /* A UI subscriber must not interrupt a download or its cleanup. */ }
        }
    }
    private void NotifyCompleted(DownloadItem item)
    {
        if (DownloadCompleted is not { } handlers) return;
        foreach (EventHandler<DownloadItem> callback in handlers.GetInvocationList()) { try { callback(this, item.Clone()); } catch { } }
    }
    private static string FriendlyError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Flux cannot write to this folder. Choose a writable download folder.",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "The server refused access. The link may have expired or may require authentication.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "The file was not found on the server (HTTP 404).",
        _ => exception.Message
    };

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] running;
        Task? pump;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Closing the app is not a permanent Pause All action. Preserve the user's queue switch.
            foreach (var entry in _entries.Where(e => e.Item.Status is DownloadStatus.Downloading or DownloadStatus.Resolving or DownloadStatus.Queued))
                entry.Item.Status = DownloadStatus.Paused;
            running = _running.Values.Select(operation => operation.Task).ToArray();
            pump = _pumpTask;
            _shutdown.Cancel();
        }
        await Task.WhenAll(running).ConfigureAwait(false);
        if (pump is not null) await pump.ConfigureAwait(false);
        try { await PersistAsync().ConfigureAwait(false); }
        finally { _http.Dispose(); _shutdown.Dispose(); }
    }

    private sealed class QueueEntry
    {
        public DownloadItem Item { get; set; } = new();
        public Dictionary<string, string> Headers { get; set; } = [];
        public int RequestedConnections { get; set; } = 8;
        public bool NameResolved { get; set; }
        public bool CategoryExplicit { get; set; }
        public CollisionPolicy CollisionPolicy { get; set; } = CollisionPolicy.Rename;
        public bool SchedulePaused { get; set; }
    }
    private sealed class QueueState
    {
        public int Version { get; set; } = 1;
        public DownloadSettings Settings { get; set; } = new();
        public bool QueuePaused { get; set; }
        public List<QueueEntry> Entries { get; set; } = [];
    }
    private sealed class RunningOperation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
}
