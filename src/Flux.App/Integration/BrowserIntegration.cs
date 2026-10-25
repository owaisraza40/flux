using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Flux.App.Integration;

/// <summary>Explicit, per-user browser native messaging registration. No browser preferences are changed.</summary>
public static partial class BrowserIntegration
{
    public const string HostName = "com.flux.bridge";
    public const string LegacyHostName = "com.flux.downloads";
    public const string ChromiumExtensionId = ExtensionIdentity.ChromiumExtensionId;
    public const string FirefoxExtensionId = "flux@flux.local";
    private static readonly (string Name, string RegistryPath)[] Browsers =
    [
        ("Chrome", @"Software\Google\Chrome\NativeMessagingHosts\" + HostName),
        ("Edge", @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName),
        ("Firefox", @"Software\Mozilla\NativeMessagingHosts\" + HostName)
    ];

    /// <summary>Registers all supported browsers for this user's installed host. Returns true once per installation.</summary>
    public static bool EnsureRegistered()
    {
        RegisterChrome(ChromiumExtensionId);
        RegisterEdge(ChromiumExtensionId);
        RegisterFirefox(FirefoxExtensionId);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Flux");
        var application = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Flux.exe"));
        var firstRun = !string.Equals(key.GetValue("BrowserSetupShownFor") as string, application, StringComparison.OrdinalIgnoreCase);
        if (firstRun) key.SetValue("BrowserSetupShownFor", application);
        return firstRun;
    }

    public static IReadOnlyDictionary<string, bool> GetStatuses() => BrowserConnectionStatus.GetStatuses();
    public static string SetupInstructionsPath => Path.Combine(AppContext.BaseDirectory, "docs", "BROWSER-INTEGRATION.md");

    public static string RegisterChrome(string extensionId) => Register("Chrome", ValidateChromiumId(extensionId), false);
    public static string RegisterEdge(string extensionId) => Register("Edge", ValidateChromiumId(extensionId), false);
    public static string RegisterFirefox(string extensionId)
    {
        if (!extensionId.Equals(FirefoxExtensionId, StringComparison.Ordinal))
            throw new ArgumentException($"Use the Firefox add-on ID {FirefoxExtensionId}, not its temporary moz-extension UUID.", nameof(extensionId));
        return Register("Firefox", extensionId, true);
    }

    public static void UnregisterAll()
    {
        EnsureWindows();
        foreach (var browser in Browsers)
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            user.DeleteSubKeyTree(browser.RegistryPath, throwOnMissingSubKey: false);
            user.DeleteSubKeyTree(browser.RegistryPath.Replace(HostName, LegacyHostName, StringComparison.Ordinal), throwOnMissingSubKey: false);
        }
        var pairing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flux", "bridge-pairings.json");
        if (File.Exists(pairing)) File.Delete(pairing);
    }

    public static string ValidateChromiumId(string extensionId)
    {
        var value = extensionId?.Trim() ?? "";
        if (!ChromiumIdPattern().IsMatch(value))
            throw new ArgumentException("A Chrome/Edge extension ID is exactly 32 lowercase letters from a to p. Find it on the browser's Extensions page.", nameof(extensionId));
        return value;
    }

    private static string Register(string browserName, string extensionId, bool firefox)
    {
        EnsureWindows();
        var applicationPath = Path.Combine(AppContext.BaseDirectory, "Flux.NativeHost.exe");
        if (!File.Exists(applicationPath))
            throw new InvalidOperationException("Flux.NativeHost.exe is missing. Extract or reinstall the complete Flux package before registering browser integration.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flux", "NativeMessaging", browserName);
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, HostName + ".json");
        var manifest = new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = "Flux download manager bridge",
            ["path"] = Path.GetFullPath(applicationPath),
            ["type"] = "stdio",
            [firefox ? "allowed_extensions" : "allowed_origins"] = new[]
            {
                firefox ? extensionId : "chrome-extension://" + extensionId + "/"
            }
        };
        var temporaryPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        var browser = Browsers.Single(item => item.Name == browserName);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = user.CreateSubKey(browser.RegistryPath, writable: true);
            key.SetValue("", manifestPath, RegistryValueKind.String);
            var legacyPath = browser.RegistryPath.Replace(HostName, LegacyHostName, StringComparison.Ordinal);
            using var legacy = user.OpenSubKey(legacyPath);
            var oldManifest = legacy?.GetValue("") as string;
            // Migrate only Flux's own previous registration, never unrelated native hosts.
            if (oldManifest != null && Path.GetFullPath(oldManifest).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(oldManifest).Equals(LegacyHostName + ".json", StringComparison.OrdinalIgnoreCase))
            {
                legacy?.Dispose();
                user.DeleteSubKeyTree(legacyPath, throwOnMissingSubKey: false);
            }
        }
        return manifestPath;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native host registration is available on Windows.");
    }

    [GeneratedRegex("^[a-p]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChromiumIdPattern();
}
