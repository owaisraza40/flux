using System.Text.Json.Serialization;

namespace Flux.Core;

public enum DownloadStatus { Queued, Resolving, Downloading, Paused, Completed, Failed, Cancelled, Scheduled, Skipped }
public enum DownloadPriority { High = 0, Normal = 1, Low = 2 }
public enum CollisionPolicy { Rename, Overwrite, Skip }
public enum DownloadKind { Auto, Http, Media }
public enum CompletionAction { None, OpenFile, PlaySound, RunScript }

public interface IExternalDownloadProcessor
{
    bool CanHandle(DownloadItem item);
    Task<ExternalDownloadResult> DownloadAsync(DownloadItem item, IReadOnlyDictionary<string, string> headers, DownloadSettings settings, IProgress<ExternalDownloadProgress> progress, CancellationToken cancellationToken);
    Task CleanupAsync(DownloadItem item) => Task.CompletedTask;
}
public sealed record ExternalDownloadProgress(long DownloadedBytes, long TotalBytes, double BytesPerSecond, string? Message = null);
public sealed record ExternalDownloadResult(string FilePath, long Bytes) { public bool Skipped { get; init; } }

/// <summary>A detached, immutable-by-convention view of a download. The manager owns live state.</summary>
public sealed class DownloadItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "download";
    public string DestinationDirectory { get; set; } = "";
    public string? PartialDirectory { get; set; }
    public string FilePath { get; set; } = "";
    public string Category { get; set; } = "Other";
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long TotalBytes { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public int Connections { get; set; } = 1;
    public bool SupportsRanges { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public DownloadPriority Priority { get; set; } = DownloadPriority.Normal;
    public long QueueOrder { get; set; }
    public long? SpeedLimitBytesPerSecond { get; set; }
    public DateTimeOffset? StopAt { get; set; }
    public DownloadKind Kind { get; set; } = DownloadKind.Auto;
    public CompletionAction CompletionAction { get; set; }
    public string? CompletionScript { get; set; }
    public bool ScanOnCompletion { get; set; }
    public List<string> Mirrors { get; set; } = [];
    public string? ActivityMessage { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    [JsonIgnore] public double Progress => Status == DownloadStatus.Completed ? 100 : TotalBytes > 0 ? Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100) : 0;
    public DownloadItem Clone() { var copy = (DownloadItem)MemberwiseClone(); copy.Mirrors = new(Mirrors ?? []); return copy; }
}

public sealed class DownloadRequest
{
    public string Url { get; set; } = "";
    public string DestinationDirectory { get; set; } = "";
    public string? FileName { get; set; }
    public int? Connections { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public List<string>? Mirrors { get; set; }
    public DownloadPriority Priority { get; set; } = DownloadPriority.Normal;
    public long? SpeedLimitBytesPerSecond { get; set; }
    public DateTimeOffset? StopAt { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? Category { get; set; }
    public DownloadKind Kind { get; set; } = DownloadKind.Auto;
    public CompletionAction CompletionAction { get; set; }
    public string? CompletionScript { get; set; }
    public bool ScanOnCompletion { get; set; }
}

public sealed class DownloadSettings
{
    public string DefaultDownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Flux");
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int ConnectionsPerDownload { get; set; } = 8;
    public int RetryCount { get; set; } = 3;
    /// <summary>Aggregate byte limit across connections of each download; zero is unlimited.</summary>
    public long SpeedLimitBytesPerSecond { get; set; }
    public long GlobalSpeedLimitBytesPerSecond { get; set; }
    public CollisionPolicy CollisionPolicy { get; set; } = CollisionPolicy.Rename;
    public Dictionary<string, string> CategoryDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AutoSortCategories { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ClipboardMonitorEnabled { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public string? ScannerExecutable { get; set; }
    public bool DailyScheduleEnabled { get; set; }
    public string DailyStart { get; set; } = "00:00";
    public string DailyStop { get; set; } = "06:00";
    public DownloadSettings Clone() { var copy = (DownloadSettings)MemberwiseClone(); copy.CategoryDirectories = new(CategoryDirectories ?? [], StringComparer.OrdinalIgnoreCase); return copy; }
    internal void Validate()
    {
        if (MaxConcurrentDownloads is < 1 or > 8) throw new ArgumentException("Simultaneous downloads must be between 1 and 8.");
        if (ConnectionsPerDownload is < 1 or > 32) throw new ArgumentException("Connections must be between 1 and 32.");
        if (RetryCount is < 0 or > 10) throw new ArgumentException("Retry count must be between 0 and 10.");
        if (SpeedLimitBytesPerSecond < 0 || SpeedLimitBytesPerSecond > 1_000_000_000_000) throw new ArgumentException("The speed limit must be zero or a positive number of bytes per second.");
        if (GlobalSpeedLimitBytesPerSecond < 0 || GlobalSpeedLimitBytesPerSecond > 1_000_000_000_000) throw new ArgumentException("The global speed limit must be zero or a positive number of bytes per second.");
        if (!Enum.IsDefined(CollisionPolicy)) throw new ArgumentException("Choose a valid filename collision policy.");
        if (!TimeOnly.TryParseExact(DailyStart, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) || !TimeOnly.TryParseExact(DailyStop, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new ArgumentException("Daily schedule times must use HH:mm.");
        CategoryDirectories = new(CategoryDirectories ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var key in CategoryDirectories.Keys.ToArray()) { if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(CategoryDirectories[key])) throw new ArgumentException("Category folders require a category and a path."); CategoryDirectories[key] = Path.GetFullPath(CategoryDirectories[key]); }
        if (string.IsNullOrWhiteSpace(DefaultDownloadDirectory)) throw new ArgumentException("Choose a download folder.");
        DefaultDownloadDirectory = Path.GetFullPath(DefaultDownloadDirectory);
    }
}

public static class DownloadNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public static string Sanitize(string? value)
    {
        value = (value ?? "").Replace('\\', '/');
        value = value[(value.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        value = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..") value = "download";
        if (Reserved.Contains(value.Split('.')[0])) value = "_" + value;
        if (value.Length > 180)
        {
            var extension = Path.GetExtension(value);
            if (extension.Length > 24) extension = extension[..24];
            value = value[..(180 - extension.Length)] + extension;
        }
        return value;
    }

    public static string FromUrl(Uri uri)
    {
        string name;
        try { name = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? ""); }
        catch (UriFormatException) { name = "download"; }
        return Sanitize(name);
    }

    public static string Category(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".m4v" or ".ts" => "Video",
        ".mp3" or ".flac" or ".wav" or ".aac" or ".m4a" or ".ogg" => "Audio",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" => "Archives",
        ".pdf" or ".doc" or ".docx" or ".txt" or ".epub" or ".xlsx" or ".pptx" or ".csv" => "Documents",
        ".exe" or ".msi" or ".msix" or ".appx" or ".iso" => "Applications",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".bmp" => "Images",
        _ => "Other"
    };

    internal static Uri ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Enter a complete HTTP or HTTPS download URL.");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Enter a complete HTTP or HTTPS download URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("URLs with embedded credentials are not supported; use an Authorization header instead.");
        if (url.Length > 32_768) throw new ArgumentException("The download URL is too long.");
        return uri;
    }
}
