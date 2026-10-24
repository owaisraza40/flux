using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Flux.App.Integration;

/// <summary>A native-messaging-established per-user capability for the loopback bridge.</summary>
public static class BridgePairing
{
    private static readonly object Gate = new();
    internal static string? TestDirectory { get; set; }
    private static string StorePath => Path.Combine(TestDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flux"), "bridge-pairings.json");

    public static string Pair(string origin)
    {
        var normalized = NormalizeOrigin(origin);
        if (!CallerAllows(normalized)) throw new InvalidDataException("Pairing must be requested by the registered browser extension.");
        lock (Gate)
        {
            var pairs = Read();
            if (!pairs.TryGetValue(normalized, out var token))
            {
                token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                if (pairs.Count >= 32) throw new InvalidDataException("Too many browser pairings. Disconnect old browser profiles first.");
                pairs[normalized] = token;
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(pairs));
                File.Move(temporary, StorePath, true);
            }
            return token;
        }
    }

    public static bool IsPairedOrigin(string origin)
    {
        try { lock (Gate) return Read().ContainsKey(NormalizeOrigin(origin)); }
        catch (ArgumentException) { return false; }
    }

    public static bool Authenticate(string origin, string token)
    {
        if (token.Length != 64) return false;
        try
        {
            lock (Gate)
            {
                if (!Read().TryGetValue(NormalizeOrigin(origin), out var expected)) return false;
                return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(token));
            }
        }
        catch (ArgumentException) { return false; }
    }

    public static string NormalizeOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("chrome-extension" or "moz-extension")
            || uri.Host.Length is < 1 or > 128 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath is not ("" or "/")) throw new ArgumentException("Only a browser extension origin can pair with Flux.");
        return uri.Scheme + "://" + uri.Host;
    }

    private static bool CallerAllows(string origin)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        foreach (var arg in args)
        {
            if (arg.StartsWith("chrome-extension://", StringComparison.Ordinal))
            {
                try { return NormalizeOrigin(arg) == origin; }
                catch (ArgumentException) { return false; }
            }
        }
        // Firefox invokes its host with the registered manifest path and add-on ID.
        return origin.StartsWith("moz-extension://", StringComparison.Ordinal)
            && args.Any(arg => arg.Equals(BrowserIntegration.FirefoxExtensionId, StringComparison.Ordinal))
            && args.Any(arg => Path.GetFileName(arg).Equals(BrowserIntegration.HostName + ".json", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> Read()
    {
        if (!File.Exists(StorePath)) return new(StringComparer.Ordinal);
        var info = new FileInfo(StorePath);
        if (info.Length > 32_768) throw new InvalidDataException("Invalid Flux browser pairing store.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new(StringComparer.Ordinal);
    }
}
