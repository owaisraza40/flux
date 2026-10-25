using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Flux.App.Integration;

/// <summary>Browser native messaging and a bounded, current-user-only desktop IPC channel.</summary>
public static class NativeBridge
{
    public const int MaximumMessageBytes = 1024 * 1024;
    public const int MaximumUrls = 100;
    public const int MaximumUrlCharacters = 8192;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string UserInstanceKey
    {
        get
        {
            var identity = OperatingSystem.IsWindows()
                ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
                : $"{Environment.UserDomainName}\\{Environment.UserName}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        }
    }

    private static string PipeName => "Flux.Downloads." + UserInstanceKey + (BridgePairing.TestDirectory == null ? "" : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(BridgePairing.TestDirectory))))[..12]);

    public static bool IsNativeHostInvocation(string[] args)
    {
        if (args.Any(value => value.Equals("--native-host", StringComparison.Ordinal))) return true;
        if (args.Length == 0) return false;
        if (Uri.TryCreate(args[0], UriKind.Absolute, out var origin)
            && origin.Scheme is "chrome-extension" or "moz-extension") return true;
        return args.Length >= 2
            && (Path.GetFileName(args[0]).Equals(BrowserIntegration.HostName + ".json", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(args[0]).Equals(BrowserIntegration.LegacyHostName + ".json", StringComparison.OrdinalIgnoreCase))
            && args[1].Equals(BrowserIntegration.FirefoxExtensionId, StringComparison.Ordinal);
    }

    /// <summary>Validates URLs before crossing an application boundary. It never executes commands.</summary>
    public static string[] NormalizeUrls(IEnumerable<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var result = new List<string>();
        foreach (var supplied in urls)
        {
            if (result.Count >= MaximumUrls) throw new ArgumentException($"Send no more than {MaximumUrls} URLs at a time.");
            var value = supplied?.Trim() ?? "";
            if (value.Length == 0 || value.Length > MaximumUrlCharacters
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
                throw new ArgumentException("Every download must be an absolute HTTP or HTTPS URL (maximum 8,192 characters).");
            if (uri.UserInfo.Length != 0)
                throw new ArgumentException("URLs containing a username or password are not accepted by the browser bridge.");
            result.Add(uri.AbsoluteUri);
        }
        if (result.Count == 0) throw new ArgumentException("Provide at least one download URL.");
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>An empty sequence asks the existing desktop window to activate.</summary>
    public static async Task<bool> SendUrlsAsync(IEnumerable<string> urls, CancellationToken cancellationToken = default)
    {
        var values = urls.Take(MaximumUrls + 1).ToArray();
        return await SendDownloadsAsync(values.Select(url => new BrowserDownload { Url = url }), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> SendDownloadsAsync(IEnumerable<BrowserDownload> downloads, CancellationToken cancellationToken = default)
    {
        var values = downloads.Take(MaximumUrls + 1).ToArray();
        if (values.Length > MaximumUrls) throw new ArgumentException("Too many downloads.");
        values = values.Select(value => BrowserDownload.Parse(JsonSerializer.SerializeToElement(value, JsonOptions))).ToArray();
        var request = values.Length == 0
            ? new BridgeRequest("activate", [])
            : new BridgeRequest("download", values.Select(value => value.Url).ToArray(), values);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(800, timeout.Token).ConfigureAwait(false);
            await WriteFrameAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            var response = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (response is null) return false;
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    public static async Task RunServerAsync(Action<string[]> onUrls, CancellationToken cancellationToken)
        => await RunDownloadServerAsync(downloads => onUrls(downloads.Select(item => item.Url).ToArray()), cancellationToken).ConfigureAwait(false);

    public static async Task RunDownloadServerAsync(Action<BrowserDownload[]> onDownloads, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onDownloads);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    16 * 1024, 16 * 1024);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var bytes = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
                    if (bytes is null) continue;
                    var request = ParseRequest(bytes, allowActivate: true);
                    if (request.Action == "pair") throw new InvalidDataException("Pairing requires native messaging.");
                    onDownloads(request.Downloads ?? request.Urls.Select(url => new BrowserDownload { Url = url }).ToArray());
                    await WriteFrameAsync(pipe, new BridgeResponse(true, "Accepted by Flux.", request.Urls.Length), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
                {
                    await WriteFrameAsync(pipe, new BridgeResponse(false, error.Message, 0), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { /* Drop an idle or incomplete same-user client. */ }
            catch (IOException) { /* A disconnected client does not stop the desktop server. */ }
        }
    }

    /// <summary>Runs with inherited binary stdin/stdout. No WPF window should exist in this mode.</summary>
    public static async Task<int> RunHostAsync()
    {
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();
        try
        {
            for (var requestNumber = 0; requestNumber < 128; requestNumber++)
            {
                var bytes = await ReadFrameAsync(input, CancellationToken.None).ConfigureAwait(false);
                if (bytes is null) return 0;
                BridgeResponse response;
                try
                {
                    var request = ParseRequest(bytes, allowActivate: false);
                    if (request.Action == "ping")
                    {
                        response = new BridgeResponse(true, "Flux native bridge is ready.", 0);
                    }
                    else if (request.Action == "pair")
                    {
                        response = new BridgeResponse(true, "Browser paired with Flux.", 0, Token: BridgePairing.Pair(request.Origin!));
                    }
                    else
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        var downloads = request.Downloads ?? request.Urls.Select(url => new BrowserDownload { Url = url }).ToArray();
                        var sent = await SendDownloadsAsync(downloads, timeout.Token).ConfigureAwait(false);
                        if (!sent)
                        {
                            var applicationPath = Path.Combine(AppContext.BaseDirectory, "Flux.exe");
                            if (string.IsNullOrWhiteSpace(applicationPath) || !File.Exists(applicationPath))
                                throw new InvalidOperationException("Cannot locate the Flux application.");
                            var start = new ProcessStartInfo(applicationPath)
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                WorkingDirectory = Path.GetDirectoryName(applicationPath)!
                            };
                            start.ArgumentList.Add("--background");
                            using var process = Process.Start(start);
                            for (var attempt = 0; attempt < 12 && !sent; attempt++)
                            {
                                await Task.Delay(400, timeout.Token).ConfigureAwait(false);
                                sent = await SendDownloadsAsync(downloads, timeout.Token).ConfigureAwait(false);
                            }
                        }
                        response = sent
                            ? new BridgeResponse(true, $"Sent {request.Urls.Length} download link(s) to Flux.", request.Urls.Length)
                            : new BridgeResponse(false, "Flux did not respond. Open the desktop app and try again.", 0);
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                {
                    response = new BridgeResponse(false, error is OperationCanceledException
                        ? "Flux startup timed out. Open the desktop app and try again." : error.Message, 0);
                }
                await WriteFrameAsync(output, response, CancellationToken.None).ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            await Console.Error.WriteLineAsync("Flux bridge: " + error.Message).ConfigureAwait(false);
            try { await WriteFrameAsync(output, new BridgeResponse(false, "Invalid native messaging frame.", 0), CancellationToken.None).ConfigureAwait(false); }
            catch (IOException) { }
            return 1;
        }
    }

    private static BridgeRequest ParseRequest(ReadOnlyMemory<byte> json, bool allowActivate)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The message must be a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate message properties are not allowed.");
            if (property.Name is not ("action" or "urls" or "downloads" or "version" or "origin")) throw new InvalidDataException("Unknown message property.");
        }
        if (root.TryGetProperty("version", out var version) && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1))
            throw new InvalidDataException("Unsupported bridge protocol version.");
        if (!root.TryGetProperty("action", out var actionElement) || actionElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("A message action is required.");
        var action = actionElement.GetString();
        if (action == "ping") return new BridgeRequest("ping", []);
        if (action == "pair")
        {
            if (!root.TryGetProperty("origin", out var origin) || origin.ValueKind != JsonValueKind.String) throw new InvalidDataException("Pairing requires an extension origin.");
            return new BridgeRequest("pair", [], Origin: BridgePairing.NormalizeOrigin(origin.GetString()!));
        }
        if (allowActivate && action == "activate") return new BridgeRequest("activate", []);
        if (action != "download") throw new InvalidDataException("Unsupported message action.");
        if (root.TryGetProperty("downloads", out var downloadsElement))
        {
            if (downloadsElement.ValueKind != JsonValueKind.Array || downloadsElement.GetArrayLength() is < 1 or > MaximumUrls)
                throw new InvalidDataException("Invalid browser downloads array.");
            var downloads = downloadsElement.EnumerateArray().Select(BrowserDownload.Parse).ToArray();
            return new BridgeRequest("download", downloads.Select(item => item.Url).ToArray(), downloads);
        }
        if (!root.TryGetProperty("urls", out var urlElement) || urlElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("A download message must contain a URLs array.");
        if (urlElement.GetArrayLength() > MaximumUrls) throw new InvalidDataException($"Send no more than {MaximumUrls} URLs.");
        var urls = urlElement.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException("Each URL must be a string."));
        return new BridgeRequest("download", NormalizeUrls(urls));
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        if (first < 4) await stream.ReadExactlyAsync(header.AsMemory(first), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaximumMessageBytes) throw new InvalidDataException("Native message length must be between 1 byte and 1 MiB.");
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("Response exceeds the native messaging limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record BridgeRequest(string Action, string[] Urls, BrowserDownload[]? Downloads = null, string? Origin = null);
    private sealed record BridgeResponse(bool Ok, string Message, int Count, int Version = 1, string? Token = null);
}
