using System.IO;
using System.Security.Principal;
using System.Windows;
using Flux.App.Integration;

namespace Flux.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var diagnosticMode = args.Contains("--ui-test-data", StringComparer.OrdinalIgnoreCase);
        void Trace(string message) { if (!diagnosticMode) return; try { var path = Path.Combine(Path.GetTempPath(), "Flux-ui-preview"); Directory.CreateDirectory(path); File.AppendAllText(Path.Combine(path, "program-diagnostic.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); } catch { } }
        Trace("Main entered, native=" + NativeBridge.IsNativeHostInvocation(args));
        if (args.Contains("--native-host", StringComparer.OrdinalIgnoreCase) || NativeBridge.IsNativeHostInvocation(args))
        {
            try { return NativeBridge.RunHostAsync().GetAwaiter().GetResult(); }
            catch { return 1; } // Never print non-protocol output to a browser host's stdout.
        }

        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var name = "Local\\Flux.UI." + identity;
        using var instance = new Mutex(true, name, out var ownsInstance);
        Trace("Single-instance created=" + ownsInstance);
        var urls = args.Where(IsHttpUrl).ToArray();
        if (!ownsInstance)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                if (!NativeBridge.SendUrlsAsync(urls, timeout.Token).GetAwaiter().GetResult())
                    MessageBox.Show("Flux is already starting. Please try again in a moment.", "Flux", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
            return 0;
        }

        try
        {
            var testData = args.Contains("--ui-test-data", StringComparer.OrdinalIgnoreCase);
            var screenshotOption = args.FirstOrDefault(value => value.StartsWith("--ui-screenshot=", StringComparison.OrdinalIgnoreCase));
            string? screenshotPath = null;
            if (screenshotOption != null)
            {
                if (!testData) throw new ArgumentException("UI screenshot export requires --ui-test-data.");
                screenshotPath = screenshotOption["--ui-screenshot=".Length..];
                if (!Path.IsPathFullyQualified(screenshotPath) || !Path.GetExtension(screenshotPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--ui-screenshot must specify a full PNG file path.");
                screenshotPath = Path.GetFullPath(screenshotPath);
            }
            var exitAfterScreenshot = args.Contains("--ui-screenshot-exit", StringComparer.OrdinalIgnoreCase);
            if (exitAfterScreenshot && screenshotPath == null) throw new ArgumentException("--ui-screenshot-exit requires --ui-test-data and --ui-screenshot=<absolute PNG path>.");
            var startInBackground = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            var app = new FluxApplication(urls, testData, screenshotPath, exitAfterScreenshot, startInBackground);
            Trace("Calling Application.Run");
            var exitCode = app.Run();
            Trace("Application.Run exited=" + exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flux");
            try { Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "startup-error.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); } catch { }
            MessageBox.Show("Flux could not start. " + ex.Message + "\n\nDetails are saved in " + folder, "Flux", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { try { instance.ReleaseMutex(); } catch (ApplicationException) { } }
    }

    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
}

