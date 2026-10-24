using System.Diagnostics;
using System.IO;
using System.Media;
using Flux.Core;

namespace Flux.App.Services;

/// <summary>Only explicit, locally configured completion actions are executed.</summary>
public sealed class CompletionService(DownloadManager manager)
{
    public async Task HandleAsync(DownloadItem item)
    {
        if (!File.Exists(item.FilePath)) return;
        if (item.ScanOnCompletion)
        {
            var scanner = manager.Settings.ScannerExecutable;
            if (string.IsNullOrWhiteSpace(scanner)) scanner = FindDefender();
            if (string.IsNullOrWhiteSpace(scanner) || !File.Exists(scanner))
                throw new IOException("The download finished, but the virus scanner was not found. Choose a scanner in Settings.");
            var info = new ProcessStartInfo(scanner) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileName(scanner).Equals("MpCmdRun.exe", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var argument in new[] { "-Scan", "-ScanType", "3", "-File", item.FilePath }) info.ArgumentList.Add(argument);
            }
            else info.ArgumentList.Add(item.FilePath);
            using var process = Process.Start(info) ?? throw new IOException("The virus scanner could not start.");
            using var scanTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await process.WaitForExitAsync(scanTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { TryKillOwned(process); throw new IOException("The download finished, but its virus scan exceeded ten minutes. Completion actions were not run."); }
            if (process.ExitCode != 0) throw new IOException($"The scanner reported exit code {process.ExitCode}. Check its results; completion actions were not run.");
        }

        switch (item.CompletionAction)
        {
            case CompletionAction.None: return;
            case CompletionAction.PlaySound: SystemSounds.Asterisk.Play(); return;
            case CompletionAction.OpenFile: Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); return;
            case CompletionAction.RunScript:
                if (string.IsNullOrWhiteSpace(item.CompletionScript) || !Path.IsPathFullyQualified(item.CompletionScript) || !File.Exists(item.CompletionScript))
                    throw new IOException("The selected completion script does not exist.");
                var extension = Path.GetExtension(item.CompletionScript);
                ProcessStartInfo script;
                if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    script = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
                    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", item.CompletionScript, "-DownloadedFile", item.FilePath }) script.ArgumentList.Add(argument);
                }
                else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    script = new ProcessStartInfo(item.CompletionScript);
                    script.ArgumentList.Add(item.FilePath);
                }
                else throw new IOException("Completion scripts must be .ps1 or .exe files. PowerShell's normal execution policy is respected.");
                script.UseShellExecute = false;
                script.CreateNoWindow = true;
                using (var process = Process.Start(script) ?? throw new IOException("The completion script could not start."))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { TryKillOwned(process); throw new IOException("The completion script exceeded five minutes and was stopped."); }
                    if (process.ExitCode != 0) throw new IOException($"The completion script exited with code {process.ExitCode}.");
                }
                return;
        }
    }

    private static string? FindDefender()
    {
        var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
        if (Directory.Exists(platform))
        {
            var candidate = Directory.EnumerateDirectories(platform).OrderByDescending(Path.GetFileName).Select(path => Path.Combine(path, "MpCmdRun.exe")).FirstOrDefault(File.Exists);
            if (candidate != null) return candidate;
        }
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(legacy) ? legacy : null;
    }

    private static void TryKillOwned(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
}
