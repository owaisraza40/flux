using System.Collections.Concurrent;

namespace Flux.App.Integration;

/// <summary>Connection state is established by a native-paired, authenticated WebSocket, not registry presence.</summary>
public static class BrowserConnectionStatus
{
    private static readonly ConcurrentDictionary<Guid, string> Connections = new();
    internal static Guid Connected(string origin, string? userAgent, string? browser)
    {
        var name = origin.StartsWith("moz-extension://", StringComparison.Ordinal) ? "Firefox"
            : browser == "Edge" || userAgent?.Contains("Edg/", StringComparison.Ordinal) == true ? "Edge" : "Chrome";
        var id = Guid.NewGuid();
        Connections[id] = name;
        return id;
    }
    internal static void Disconnected(Guid id) => Connections.TryRemove(id, out _);
    public static bool IsConnected(string browser) => Connections.Values.Contains(browser, StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyDictionary<string, bool> GetStatuses() => new Dictionary<string, bool>
    {
        ["Chrome"] = IsConnected("Chrome"), ["Edge"] = IsConnected("Edge"), ["Firefox"] = IsConnected("Firefox")
    };
}
