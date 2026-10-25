using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Flux.Core;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Flux-tests-" + Guid.NewGuid().ToString("N"));
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--crash-worker")
        {
            await using var crashManager = new DownloadManager(args[1]); await crashManager.InitializeAsync();
            var crashSettings = crashManager.Settings; crashSettings.SpeedLimitBytesPerSecond = 524_288; crashSettings.DefaultDownloadDirectory = args[2]; await crashManager.SaveSettingsAsync(crashSettings);
            var crashItem = await crashManager.AddAsync(new DownloadRequest { Url = args[3], DestinationDirectory = args[2], Connections = 8 });
            Console.WriteLine("READY:" + crashItem.Id); await Task.Delay(Timeout.Infinite); return 0;
        }
        Directory.CreateDirectory(Root);
        Console.WriteLine("Flux deterministic core integration checks");
        Console.WriteLine("Artifacts: " + Root);
        await using var server = new FixtureServer();
        server.Start();
        await Run("parallel ranges produce exact SHA-256 and clean partials", async () =>
        {
            await using var manager = await Manager("parallel");
            var item = await manager.AddAsync(Request(server, "parallel", "parallel"));
            var completed = await Complete(manager, item.Id);
            Assert(completed.Connections > 1, "Expected multiple range connections.");
            Assert(completed.SupportsRanges, "Expected resumable range support.");
            await ExactFile(completed.FilePath, server.Body);
            Assert(!Directory.EnumerateFiles(completed.DestinationDirectory).Any(p => p.EndsWith(".sfpart") || p.EndsWith(".sfresume")), "Partial/checkpoint files remain after completion.");
            Assert(server.Requests.Any(r => r.Path == "/parallel" && r.Range is not null && r.Range != "bytes=0-0"), "No actual ranged transfer was observed.");
        });
        await Run("servers ignoring Range fall back to complete sequential GET", async () =>
        {
            await using var manager = await Manager("ignore");
            var item = await manager.AddAsync(Request(server, "ignore", "ignore"));
            var done = await Complete(manager, item.Id);
            Assert(!done.SupportsRanges && done.Connections == 1, "Ignoring ranges should use one non-resumable stream.");
            await ExactFile(done.FilePath, server.Body);
        });
        await Run("HEAD is not required and unknown chunked lengths complete", async () =>
        {
            await using var manager = await Manager("unknown");
            var headItem = await manager.AddAsync(Request(server, "head405", "unknown"));
            await ExactFile((await Complete(manager, headItem.Id)).FilePath, server.Body);
            Assert(!server.Requests.Any(r => r.Path == "/head405" && r.Method == "HEAD"), "Probe unexpectedly depended on HEAD.");
            var unknown = await manager.AddAsync(Request(server, "unknown", "unknown"));
            var done = await Complete(manager, unknown.Id);
            Assert(done.TotalBytes == server.SmallBody.Length, "Unknown length was not resolved at EOF.");
            await ExactFile(done.FilePath, server.SmallBody);
        });
        await Run("pause/resume retains durable ranges and exact content", async () =>
        {
            await using var manager = await Manager("pause", speed: 1_048_576);
            var item = await manager.AddAsync(Request(server, "slow", "pause"));
            await Until(() => Item(manager, item.Id).DownloadedBytes >= 196_608);
            await manager.PauseAsync(item.Id);
            var paused = Item(manager, item.Id);
            Assert(paused.Status == DownloadStatus.Paused, "Pause did not join writers.");
            var bytes = paused.DownloadedBytes;
            await Task.Delay(250);
            Assert(Item(manager, item.Id).DownloadedBytes == bytes, "Bytes changed after PauseAsync completed.");
            var settings = manager.Settings; settings.SpeedLimitBytesPerSecond = 0; await manager.SaveSettingsAsync(settings);
            var boundary = server.Requests.Count;
            await manager.ResumeAsync(item.Id);
            var done = await Complete(manager, item.Id);
            await ExactFile(done.FilePath, server.Body);
            Assert(server.Requests.Skip(boundary).Any(r => r.Path == "/slow" && RangeStart(r.Range) > 0), "Resume did not issue nonzero range offsets.");
        });
        await Run("restart restores paused history and resumes partial data", async () =>
        {
            var data = Data("restart");
            var manager = await Manager("restart", speed: 1_048_576);
            var item = await manager.AddAsync(Request(server, "restart", "restart"));
            await Until(() => Item(manager, item.Id).DownloadedBytes >= 131_072);
            await manager.DisposeAsync();
            await using var restored = new DownloadManager(data);
            await restored.InitializeAsync();
            Assert(Item(restored, item.Id).Status == DownloadStatus.Paused, "Previously active download restarted automatically.");
            var settings = restored.Settings; settings.SpeedLimitBytesPerSecond = 0; await restored.SaveSettingsAsync(settings);
            await restored.ResumeAsync(item.Id);
            await ExactFile((await Complete(restored, item.Id)).FilePath, server.Body);
        });
        await Run("changed ETag discards old partial regions", async () =>
        {
            server.EntityVersion = 1;
            await using var manager = await Manager("changed", speed: 1_048_576);
            var item = await manager.AddAsync(Request(server, "changed", "changed"));
            await Until(() => Item(manager, item.Id).DownloadedBytes >= 131_072);
            await manager.PauseAsync(item.Id);
            server.EntityVersion = 2;
            var settings = manager.Settings; settings.SpeedLimitBytesPerSecond = 0; await manager.SaveSettingsAsync(settings);
            await manager.ResumeAsync(item.Id);
            await ExactFile((await Complete(manager, item.Id)).FilePath, server.ChangedBody);
            server.EntityVersion = 1;
        });
        await Run("malformed transfer Content-Range safely restarts sequentially", async () =>
        {
            await using var manager = await Manager("bad-range");
            var item = await manager.AddAsync(Request(server, "bad", "bad-range"));
            var done = await Complete(manager, item.Id);
            Assert(done.Connections == 1, "Malformed range did not fall back safely.");
            await ExactFile(done.FilePath, server.Body);
        });
        await Run("truncated bodies fail without producing a completed output", async () =>
        {
            await using var manager = await Manager("truncated", retries: 0);
            var item = await manager.AddAsync(Request(server, "truncated", "truncated"));
            await Until(() => Item(manager, item.Id).Status == DownloadStatus.Failed);
            var failed = Item(manager, item.Id);
            Assert(!File.Exists(failed.FilePath), "A truncated download was presented as a completed file.");
            Assert(!string.IsNullOrWhiteSpace(failed.Error), "Truncation has no readable error.");
        });
        await Run("transient HTTP 503 responses are retried", async () =>
        {
            await using var manager = await Manager("retry", retries: 3);
            var item = await manager.AddAsync(Request(server, "flaky", "retry"));
            await ExactFile((await Complete(manager, item.Id)).FilePath, server.Body);
            Assert(server.Requests.Count(r => r.Path == "/flaky") >= 3, "Retry requests were not observed.");
        });
        await Run("an interrupted range retries from its received offset", async () =>
        {
            await using var manager = await Manager("disconnect", retries: 2);
            var item = await manager.AddAsync(Request(server, "disconnect", "disconnect"));
            await ExactFile((await Complete(manager, item.Id)).FilePath, server.Body);
            Assert(server.Requests.Count(r => r.Path == "/disconnect") > 9, "A disconnected range was not retried.");
        });
        await Run("a mid-transfer entity change restarts with one coherent representation", async () =>
        {
            await using var manager = await Manager("midchange");
            var item = await manager.AddAsync(Request(server, "midchange", "midchange"));
            await ExactFile((await Complete(manager, item.Id)).FilePath, server.ChangedBody);
        });
        await Run("corrupt checkpoints are discarded and server filenames stay in the destination", async () =>
        {
            await using var manager = await Manager("corrupt", speed: 1_048_576);
            var item = await manager.AddAsync(Request(server, "disposition", "corrupt"));
            await Until(() => Item(manager, item.Id).DownloadedBytes > 0);
            await manager.PauseAsync(item.Id);
            var saved = Item(manager, item.Id);
            Assert(saved.FileName == "_CON.txt", "Content-Disposition filename was not sanitized.");
            Assert(Path.GetDirectoryName(saved.FilePath) == Output("corrupt"), "The server filename escaped the selected directory.");
            await File.WriteAllTextAsync(Path.Combine(Output("corrupt"), $".flux-{item.Id:N}.sfpart.sfresume"), "{ invalid-json");
            var settings = manager.Settings; settings.SpeedLimitBytesPerSecond = 0; await manager.SaveSettingsAsync(settings);
            await manager.ResumeAsync(item.Id);
            await ExactFile((await Complete(manager, item.Id)).FilePath, server.Body);
        });
        await Run("the bandwidth cap is shared across parallel connections", async () =>
        {
            await using var manager = await Manager("bandwidth", speed: 1_048_576);
            var watch = Stopwatch.StartNew();
            var item = await manager.AddAsync(Request(server, "bandwidth", "bandwidth"));
            var done = await Complete(manager, item.Id);
            Assert(done.Connections > 1, "Bandwidth test did not use parallel connections.");
            Assert(watch.Elapsed.TotalSeconds >= 3.6, "Multiple connections bypassed the configured aggregate cap.");
            await ExactFile(done.FilePath, server.Body);
        });
        await Run("existing files and duplicate destinations are never overwritten", async () =>
        {
            await using var manager = await Manager("collision");
            var output = Output("collision"); Directory.CreateDirectory(output);
            var original = Path.Combine(output, "same.bin"); await File.WriteAllTextAsync(original, "keep this file");
            var request = Request(server, "parallel", "collision"); request.FileName = "same.bin";
            var first = await manager.AddAsync(request);
            var second = await manager.AddAsync(request);
            var complete = await Task.WhenAll(Complete(manager, first.Id), Complete(manager, second.Id));
            Assert(complete[0].FilePath != complete[1].FilePath, "Two downloads selected the same output.");
            Assert(await File.ReadAllTextAsync(original) == "keep this file", "An existing user file was overwritten.");
            foreach (var done in complete) await ExactFile(done.FilePath, server.Body);
        });
        await Run("Pause All gates new downloads and scheduled queue pumping", async () =>
        {
            await using var manager = await Manager("pause-all", speed: 524_288, concurrent: 1);
            var first = await manager.AddAsync(Request(server, "queue1", "pause-all"));
            var second = await manager.AddAsync(Request(server, "queue2", "pause-all"));
            await Until(() => Item(manager, first.Id).DownloadedBytes > 0);
            await manager.PauseAllAsync();
            var third = await manager.AddAsync(Request(server, "queue3", "pause-all"));
            var requests = server.Requests.Count(r => r.Path.StartsWith("/queue", StringComparison.Ordinal));
            await Task.Delay(650);
            Assert(server.Requests.Count(r => r.Path.StartsWith("/queue", StringComparison.Ordinal)) == requests, "The paused queue started network work.");
            Assert(Item(manager, second.Id).Status == DownloadStatus.Paused && Item(manager, third.Id).Status == DownloadStatus.Paused, "Queued/new downloads were not paused.");
            var settings = manager.Settings; settings.SpeedLimitBytesPerSecond = 0; await manager.SaveSettingsAsync(settings);
            await manager.ResumeAllAsync();
            foreach (var item in new[] { first, second, third }) await ExactFile((await Complete(manager, item.Id)).FilePath, server.Body);
        });
        await Run("scheduled downloads wait until their start time", async () =>
        {
            await using var manager = await Manager("scheduled");
            var request = Request(server, "scheduled", "scheduled"); request.ScheduledAt = DateTimeOffset.UtcNow.AddSeconds(1.5);
            var item = await manager.AddAsync(request);
            await Task.Delay(350);
            Assert(Item(manager, item.Id).Status == DownloadStatus.Scheduled, "Scheduled download started too early.");
            Assert(!server.Requests.Any(r => r.Path == "/scheduled"), "Scheduled download contacted server too early.");
            await ExactFile((await Complete(manager, item.Id)).FilePath, server.Body);
        });
        await Run("remove cleanup deletes only owned partials", async () =>
        {
            await using var manager = await Manager("cleanup", speed: 524_288);
            var item = await manager.AddAsync(Request(server, "cleanup", "cleanup"));
            await Until(() => Item(manager, item.Id).DownloadedBytes > 0);
            await manager.RemoveAsync(item.Id, deletePartial: true);
            Assert(manager.GetSnapshot().Count == 0, "Removed item is still in history.");
            Assert(!Directory.EnumerateFiles(Output("cleanup")).Any(), "Owned temporary files remain after removal.");
        });
        await Run("filename safety, zero-byte downloads, and permanent errors", async () =>
        {
            Assert(DownloadNames.Sanitize("../../CON.txt") == "_CON.txt", "Windows reserved filename was not sanitized.");
            Assert(!DownloadNames.Sanitize("..\\escape?.exe").Contains('\\'), "Path separators were retained.");
            await using var manager = await Manager("zero");
            var item = await manager.AddAsync(Request(server, "zero", "zero"));
            var done = await Complete(manager, item.Id);
            Assert(done.TotalBytes == 0 && new FileInfo(done.FilePath).Length == 0, "Empty file was not completed correctly.");
            var blocked = await manager.AddAsync(Request(server, "unauthorized", "zero"));
            await Until(() => Item(manager, blocked.Id).Status == DownloadStatus.Failed);
            Assert(Item(manager, blocked.Id).Error!.Contains("refused access", StringComparison.OrdinalIgnoreCase), "HTTP access error was not explained.");
            try { await manager.AddAsync(new DownloadRequest { Url = "file:///C:/Windows/system.ini", DestinationDirectory = Output("zero") }); throw new Exception("A local-file URL was accepted."); }
            catch (ArgumentException) { }
        });
        await Run("cross-origin redirects do not forward custom credential headers", async () =>
        {
            await using var target = new FixtureServer(); target.Start();
            server.RedirectTarget = target.Url("redirected");
            await using var manager = await Manager("redirect");
            var request = Request(server, "redirect", "redirect"); request.Headers = new() { ["Authorization"] = "Bearer test-token", ["X-Private-Key"] = "secret-test-value" };
            var item = await manager.AddAsync(request);
            await ExactFile((await Complete(manager, item.Id)).FilePath, target.Body);
            Assert(target.Requests.All(r => !r.Headers.ContainsKey("Authorization") && !r.Headers.ContainsKey("X-Private-Key")), "A credential/custom header crossed origins.");
        });

        await Run("32 connections are admitted and produce exact content", async () =>
        {
            await using var manager = await Manager("connections32");
            var request = Request(server, "large", "connections32"); request.Connections = 32;
            var item = await manager.AddAsync(request); var done = await Complete(manager, item.Id);
            Assert(done.Connections == 32, "The 32-connection setting was not honored for a sufficiently large file.");
            await ExactFile(done.FilePath, server.LargeBody);
        });
        await Run("forced process death recovers flushed offsets without orderly disposal", async () =>
        {
            var host = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { typeof(Program).Assembly.Location, "--crash-worker", Data("hard-crash"), Output("hard-crash"), server.Url("hard-crash") }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new Exception("Could not start the source-owned crash fixture.");
            try
            {
                var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert(ready?.StartsWith("READY:") == true, "Crash fixture did not initialize: " + ready);
                var id = Guid.Parse(ready![6..]);
                await Until(() =>
                {
                    var path = Directory.GetFiles(Output("hard-crash"), "*.sfresume").FirstOrDefault(); if (path is null) return false;
                    using var checkpoint = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    return checkpoint.RootElement.GetProperty("Segments").EnumerateArray().Sum(s => s.GetProperty("SavedCompleted").GetInt64()) > 131_072;
                });
                process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var checkpointPath = Directory.GetFiles(Output("hard-crash"), "*.sfresume").Single();
                using var state = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(checkpointPath));
                var resumedOffsets = state.RootElement.GetProperty("Segments").EnumerateArray().Where(s => s.GetProperty("SavedCompleted").GetInt64() > 0).Select(s => s.GetProperty("Start").GetInt64() + s.GetProperty("SavedCompleted").GetInt64()).ToHashSet();
                var boundary = server.Requests.Count;
                await using var recovered = new DownloadManager(Data("hard-crash")); await recovered.InitializeAsync();
                Assert(Item(recovered, id).Status == DownloadStatus.Paused, "Incomplete crashed job was not offered as paused for recovery.");
                var settings = recovered.Settings; settings.SpeedLimitBytesPerSecond = 0; await recovered.SaveSettingsAsync(settings); await recovered.ResumeAsync(id);
                await ExactFile((await Complete(recovered, id)).FilePath, server.Body);
                Assert(server.Requests.Skip(boundary).Any(r => r.Path == "/hard-crash" && resumedOffsets.Contains(RangeStart(r.Range))), "Recovery did not use a flushed pre-crash segment offset.");
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        });
        await Run("a freed worker splits an active slow range and survives restart", async () =>
        {
            var manager = await Manager("adaptive");
            var request = Request(server, "adaptive", "adaptive"); request.Connections = 8;
            var item = await manager.AddAsync(request);
            await Until(() => server.Requests.Count(r => r.Path == "/adaptive" && r.Range != "bytes=0-0") > 8);
            await manager.PauseAsync(item.Id);
            var saved = Directory.GetFiles(Output("adaptive"), "*.sfresume").Single();
            using (var state = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(saved)))
                Assert(state.RootElement.GetProperty("Segments").GetArrayLength() > 8, "No additional dynamic segment was checkpointed.");
            await manager.DisposeAsync();
            await using var resumed = new DownloadManager(Data("adaptive")); await resumed.InitializeAsync();
            var settings = resumed.Settings; settings.SpeedLimitBytesPerSecond = 0; await resumed.SaveSettingsAsync(settings);
            await resumed.ResumeAsync(item.Id);
            await ExactFile((await Complete(resumed, item.Id)).FilePath, server.LargeBody);
        });
        await Run("priority and drag reorder control a paused queue", async () =>
        {
            await using var manager = await Manager("priority", concurrent: 1); await manager.PauseAllAsync();
            var lowRequest = Request(server, "priority-low", "priority"); lowRequest.Priority = DownloadPriority.Low;
            var low = await manager.AddAsync(lowRequest);
            var normal = await manager.AddAsync(Request(server, "priority-normal", "priority"));
            var highRequest = Request(server, "priority-high", "priority"); highRequest.Priority = DownloadPriority.High;
            var high = await manager.AddAsync(highRequest);
            await manager.SetPriorityAsync(normal.Id, DownloadPriority.High); await manager.MoveAsync(normal.Id, 0);
            Assert(manager.GetSnapshot()[0].Id == normal.Id, "MoveAsync did not persist queue order.");
            await manager.ResumeAllAsync();
            foreach (var entry in new[] { low, normal, high }) await Complete(manager, entry.Id);
            var order = server.Requests.Where(r => r.Path.StartsWith("/priority-", StringComparison.Ordinal)).Select(r => r.Path).Distinct().ToArray();
            Assert(order.SequenceEqual(new[] { "/priority-normal", "/priority-high", "/priority-low" }), "Priority/order queue selection was incorrect.");
        });
        await Run("overwrite is explicit, skip avoids network, and rename remains default", async () =>
        {
            await using var manager = await Manager("policies");
            Directory.CreateDirectory(Output("policies")); var path = Path.Combine(Output("policies"), "chosen.bin"); await File.WriteAllTextAsync(path, "existing");
            var settings = manager.Settings; settings.CollisionPolicy = CollisionPolicy.Skip; await manager.SaveSettingsAsync(settings);
            var request = Request(server, "skip-policy", "policies"); request.FileName = "chosen.bin";
            var skipped = await manager.AddAsync(request); await Task.Delay(150);
            Assert(Item(manager, skipped.Id).Status == DownloadStatus.Skipped && !server.Requests.Any(r => r.Path == "/skip-policy"), "Skip policy contacted the server or failed to skip.");
            Assert(await File.ReadAllTextAsync(path) == "existing", "Skip modified the existing file.");
            settings.CollisionPolicy = CollisionPolicy.Overwrite; await manager.SaveSettingsAsync(settings);
            request.Url = server.Url("overwrite-policy"); var replaced = await manager.AddAsync(request);
            var done = await Complete(manager, replaced.Id); Assert(done.FilePath == path, "Overwrite unexpectedly renamed the destination."); await ExactFile(path, server.Body);
        });
        await Run("alternate mirrors work without forwarding original credentials", async () =>
        {
            await using var target = new FixtureServer(); target.Start();
            await using var manager = await Manager("mirrors", retries: 0);
            var request = Request(server, "unauthorized", "mirrors"); request.Mirrors = [target.Url("mirror")];
            request.Cookies = "auth=source-only"; request.Headers = new() { ["Authorization"] = "Bearer source-only" }; request.UserAgent = "Flux-Mirror-Test/1.0";
            var item = await manager.AddAsync(request); await ExactFile((await Complete(manager, item.Id)).FilePath, target.Body);
            Assert(target.Requests.All(r => !r.Headers.ContainsKey("Cookie") && !r.Headers.ContainsKey("Authorization")), "Mirror received original-origin credentials.");
            Assert(target.Requests.All(r => r.Headers.GetValueOrDefault("User-Agent") == request.UserAgent), "Configured User-Agent was lost at mirror.");
        });
        await Run("global cap bounds two simultaneous downloads", async () =>
        {
            await using var manager = await Manager("global-cap", concurrent: 2);
            var settings = manager.Settings; settings.GlobalSpeedLimitBytesPerSecond = 2 * 1_048_576; await manager.SaveSettingsAsync(settings);
            var watch = Stopwatch.StartNew();
            var first = await manager.AddAsync(Request(server, "global-a", "global-cap"));
            var second = await manager.AddAsync(Request(server, "global-b", "global-cap"));
            await Task.WhenAll(Complete(manager, first.Id), Complete(manager, second.Id));
            Assert(watch.Elapsed.TotalSeconds >= 3.7 && watch.Elapsed.TotalSeconds < 15, "Global aggregate cap was not enforced at the expected scale.");
            await ExactFile(Item(manager, first.Id).FilePath, server.Body); await ExactFile(Item(manager, second.Id).FilePath, server.Body);
        });
        await Run("per-download limits override defaults and scheduled stop joins writers", async () =>
        {
            await using var manager = await Manager("stop", speed: 0);
            var request = Request(server, "scheduled-stop", "stop"); request.SpeedLimitBytesPerSecond = 262_144; request.StopAt = DateTimeOffset.UtcNow.AddMilliseconds(650);
            var item = await manager.AddAsync(request); await Until(() => Item(manager, item.Id).Status == DownloadStatus.Paused);
            var bytes = Item(manager, item.Id).DownloadedBytes; Assert(bytes > 0 && bytes < server.Body.Length, "Scheduled stop did not interrupt the transfer.");
            await Task.Delay(500); Assert(Item(manager, item.Id).DownloadedBytes == bytes, "Scheduled stop left writers active.");
            await manager.ResumeAsync(item.Id); await Until(() => Item(manager, item.Id).Status == DownloadStatus.Downloading);
            Assert(Item(manager, item.Id).StopAt is null, "Explicit resume did not consume the old one-shot stop."); await manager.PauseAsync(item.Id);
        });
        await Run("closed daily window holds work and opening it resumes only scheduler-paused items", async () =>
        {
            await using var manager = await Manager("daily");
            var now = DateTime.Now; var settings = manager.Settings; settings.DailyScheduleEnabled = true;
            settings.DailyStart = now.AddMinutes(2).ToString("HH:mm"); settings.DailyStop = now.AddMinutes(3).ToString("HH:mm"); await manager.SaveSettingsAsync(settings);
            var item = await manager.AddAsync(Request(server, "daily", "daily")); await Task.Delay(350);
            Assert(!server.Requests.Any(r => r.Path == "/daily"), "Work started outside the configured daily window.");
            settings.DailyStart = now.AddMinutes(-2).ToString("HH:mm"); settings.DailyStop = now.AddMinutes(2).ToString("HH:mm"); await manager.SaveSettingsAsync(settings);
            await ExactFile((await Complete(manager, item.Id)).FilePath, server.Body);
        });
        await Run("UA referrer and matching cookies are sent and completion subscribers are isolated", async () =>
        {
            await using var manager = await Manager("metadata"); var completions = 0;
            manager.DownloadCompleted += (_, _) => throw new InvalidOperationException("Subscriber failure");
            manager.DownloadCompleted += (_, item) => { Interlocked.Increment(ref completions); item.Mirrors.Add("https://untrusted.invalid/"); };
            var request = Request(server, "metadata", "metadata"); request.UserAgent = "Flux-User-Agent/9.0"; request.Referrer = server.Url("source-page"); request.Cookies = "session=test";
            var item = await manager.AddAsync(request); await Complete(manager, item.Id);
            await Until(() => Volatile.Read(ref completions) == 1);
            Assert(server.Requests.Where(r => r.Path == "/metadata").All(r => r.Headers.GetValueOrDefault("User-Agent") == request.UserAgent && r.Headers.GetValueOrDefault("Referer") == request.Referrer && r.Headers.GetValueOrDefault("Cookie") == request.Cookies), "Captured request metadata was not sent correctly.");
            Assert(Item(manager, item.Id).Mirrors.Count == 0, "Event subscriber mutated manager-owned state.");
        });
        await Run("category paths and external processors retain standard queue semantics", async () =>
        {
            await using var manager = await Manager("external");
            var settings = manager.Settings; settings.AutoSortCategories = true; settings.CategoryDirectories["Documents"] = Path.Combine(Output("external"), "Documents"); await manager.SaveSettingsAsync(settings);
            var request = Request(server, "category", "external"); request.FileName = "guide.pdf";
            var item = await manager.AddAsync(request); var done = await Complete(manager, item.Id); Assert(Path.GetDirectoryName(done.FilePath) == settings.CategoryDirectories["Documents"], "Category folder was not used.");
            manager.ExternalProcessor = new FakeMediaProcessor();
            var mediaRequest = Request(server, "watch", "external"); mediaRequest.Kind = DownloadKind.Media;
            var media = await manager.AddAsync(mediaRequest); var result = await Complete(manager, media.Id);
            Assert(await File.ReadAllTextAsync(result.FilePath) == "media-fixture", "External processor result was not finalized.");
        });

        await Run("editing a paused transfer preserves identity and switches owned data safely", async () =>
        {
            await using var manager = await Manager("edit-properties", speed: 524_288);
            var request = Request(server,"slow","edit-properties");
            request.Headers = new() { ["Authorization"] = "Bearer owned-fixture-only" };
            var item = await manager.AddAsync(request);
            await Until(() => Item(manager,item.Id).DownloadedBytes > 65_536);
            await manager.PauseAsync(item.Id);
            request.Url = server.Url("edited-source"); request.FileName = "edited-name.bin";
            request.Headers = null;
            request.SpeedLimitBytesPerSecond = 0; request.Priority = DownloadPriority.High;
            request.MediaQuality = MediaQuality.Video720;
            await manager.UpdateItemAsync(item.Id,request);
            var edited = Item(manager,item.Id);
            Assert(edited.Id == item.Id && edited.DownloadedBytes == 0 && edited.Status == DownloadStatus.Paused,"Edited job lost identity or stale partial bytes.");
            Assert(edited.Priority == DownloadPriority.High && edited.MediaQuality == MediaQuality.Video720,"Edited options were not retained.");
            await manager.ResumeAsync(item.Id);
            var done = await Complete(manager,item.Id); await ExactFile(done.FilePath,server.Body);
            Assert(done.FileName == "edited-name.bin","Edited filename was ignored.");
            Assert(server.Requests.Where(r=>r.Path=="/edited-source").All(r=>r.Headers.GetValueOrDefault("Authorization")=="Bearer owned-fixture-only"),"Property editing lost private request headers.");
        });
        await Run("restart retains prior complete file and explicit delete removes only selected final file", async () =>
        {
            await using var manager = await Manager("restart-delete");
            var item = await manager.AddAsync(Request(server,"restart-fixture","restart-delete"));
            var original = await Complete(manager,item.Id);
            await manager.RestartAsync(item.Id);
            var restarted = await Complete(manager,item.Id);
            Assert(restarted.FilePath != original.FilePath && File.Exists(original.FilePath),"Restart overwrote a completed file.");
            await ExactFile(restarted.FilePath,server.Body);
            await manager.DeleteAsync(item.Id,true);
            Assert(!File.Exists(restarted.FilePath) && File.Exists(original.FilePath),"Delete removed the wrong completed file.");
            Assert(!manager.GetSnapshot().Any(i=>i.Id==item.Id),"Deleted job remains in history.");
            var keep = await manager.AddAsync(Request(server,"keep-fixture","restart-delete"));
            var kept = await Complete(manager,keep.Id); await manager.DeleteAsync(keep.Id,false);
            Assert(File.Exists(kept.FilePath),"Remove-from-list deleted the completed file.");
        });
        await Run("twenty-job settings and configured retry delay survive persistence", async () =>
        {
            await using var manager = await Manager("settings-20");
            var settings = manager.Settings; settings.MaxConcurrentDownloads = 20; settings.RetryDelaySeconds = 0.5;
            settings.ScheduledSpeedLimitBytesPerSecond = 128_000; await manager.SaveSettingsAsync(settings);
            await using var reopened = new DownloadManager(Data("settings-20")); await reopened.InitializeAsync();
            Assert(reopened.Settings.MaxConcurrentDownloads == 20 && reopened.Settings.RetryDelaySeconds == 0.5,"New network settings did not persist.");
            Assert(DownloadNames.Category("disk.iso")=="Archives" && DownloadNames.FromUrl(new Uri("https://youtube.com/watch?v=test"))!="watch","Filename/category regression.");
            await using var retryServer = new FixtureServer(); retryServer.Start();
            var watch = Stopwatch.StartNew();
            var request = Request(retryServer,"flaky","settings-20");
            await Complete(manager,(await manager.AddAsync(request)).Id);
            Assert(watch.Elapsed.TotalSeconds >= 1.3,"Configured exponential retry delay was ignored.");
        });

        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
        Console.WriteLine("Test data is retained for inspection: " + Root);
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Run(string name, Func<Task> body)
    {
        var watch = Stopwatch.StartNew();
        try { await body(); _passed++; Console.WriteLine($"PASS {name} ({watch.Elapsed.TotalSeconds:F2}s)"); }
        catch (Exception exception) { _failed++; Console.Error.WriteLine($"FAIL {name}: {exception}"); }
    }
    private static async Task<DownloadManager> Manager(string name, long speed = 0, int retries = 2, int concurrent = 3)
    {
        var manager = new DownloadManager(Data(name));
        await manager.InitializeAsync();
        var settings = manager.Settings; settings.DefaultDownloadDirectory = Output(name); settings.SpeedLimitBytesPerSecond = speed; settings.RetryCount = retries; settings.MaxConcurrentDownloads = concurrent;
        await manager.SaveSettingsAsync(settings);
        return manager;
    }
    private static string Data(string name) => Path.Combine(Root, name, "state");
    private static string Output(string name) => Path.Combine(Root, name, "downloads");
    private static DownloadRequest Request(FixtureServer server, string route, string name) => new() { Url = server.Url(route), DestinationDirectory = Output(name), Connections = 8 };
    private static DownloadItem Item(DownloadManager manager, Guid id) => manager.GetSnapshot().Single(i => i.Id == id);
    private static async Task<DownloadItem> Complete(DownloadManager manager, Guid id)
    {
        await Until(() =>
        {
            var item = Item(manager, id);
            if (item.Status == DownloadStatus.Failed) throw new Exception(item.Error);
            return item.Status == DownloadStatus.Completed;
        }, seconds: 20);
        return Item(manager, id);
    }
    private static async Task Until(Func<bool> condition, int seconds = 15)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("The expected download state was not reached.");
            await Task.Delay(25);
        }
    }
    private static async Task ExactFile(string path, byte[] expected)
    {
        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file);
        Assert(hash.SequenceEqual(SHA256.HashData(expected)), "Downloaded bytes do not match the fixture SHA-256.");
        Assert(file.Length == expected.Length, "Downloaded file size does not match.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static long RangeStart(string? range) => range is not null && long.TryParse(range.Split('=')[^1].Split('-')[0], out var start) ? start : -1;
}

internal sealed record ObservedRequest(string Path, string Method, string? Range, Dictionary<string, string> Headers);

internal sealed class FakeMediaProcessor : IExternalDownloadProcessor
{
    public bool CanHandle(DownloadItem item) => item.Kind == DownloadKind.Media;
    public async Task<ExternalDownloadResult> DownloadAsync(DownloadItem item, IReadOnlyDictionary<string, string> headers, DownloadSettings settings, IProgress<ExternalDownloadProgress> progress, CancellationToken cancellationToken)
    {
        var path = Path.Combine(item.DestinationDirectory, "media-fixture.mp4"); await File.WriteAllTextAsync(path, "media-fixture", cancellationToken);
        progress.Report(new ExternalDownloadProgress(13,13,13,"fixture")); return new ExternalDownloadResult(path, new FileInfo(path).Length);
    }
}

internal sealed class FixtureServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _workers = [];
    private Task? _accept;
    private int _flaky;
    private int _version = 1;
    private int _disconnect;
    private int _midchange;
    internal byte[] Body { get; } = Bytes(4 * 1024 * 1024, 17);
    internal byte[] ChangedBody { get; } = Bytes(4 * 1024 * 1024, 93);
    internal byte[] SmallBody { get; } = Bytes(131_071, 57);
    internal byte[] LargeBody { get; } = Bytes(16 * 1024 * 1024, 101);
    internal ConcurrentQueue<ObservedRequest> Requests { get; } = new();
    internal string? RedirectTarget { get; set; }
    internal int EntityVersion { get => Volatile.Read(ref _version); set => Volatile.Write(ref _version, value); }
    internal string Url(string route) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{route}";
    internal void Start()
    {
        _listener.Start();
        _accept = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _workers.Add(Handle(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        });
    }
    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);
                var first = await reader.ReadLineAsync(_stop.Token);
                if (first is null) return;
                var pieces = first.Split(' ');
                var route = pieces[1].Split('?')[0];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
                    if (headers.Count > 64) return;
                }
                headers.TryGetValue("Range", out var range);
                Requests.Enqueue(new ObservedRequest(route, pieces[0], range, headers));
                if (pieces[0] == "HEAD") { await Head(stream, "405 Method Not Allowed", 0); return; }
                if (route == "/unauthorized") { await Head(stream, "403 Forbidden", 0); return; }
                if (route == "/flaky" && Interlocked.Increment(ref _flaky) <= 2) { await Head(stream, "503 Service Unavailable", 0); return; }
                if (route == "/redirect") { await Head(stream, "302 Found", 0, $"Location: {RedirectTarget}\r\n"); return; }
                if (route == "/zero") { await Head(stream, "416 Range Not Satisfiable", 0, "Content-Range: bytes */0\r\n"); return; }
                if (route == "/midchange" && range != "bytes=0-0") Interlocked.CompareExchange(ref _midchange, 1, 0);
                var version = route == "/changed" ? EntityVersion : route == "/midchange" && Volatile.Read(ref _midchange) == 1 ? 2 : 1;
                var body = route is "/adaptive" or "/large" ? LargeBody : version == 2 ? ChangedBody : Body;
                var etag = $"\"fixture-v{version}\"";
                var common = $"ETag: {etag}\r\nLast-Modified: Wed, 21 Oct 2026 07:28:00 GMT\r\nAccept-Ranges: bytes\r\n";
                if (route == "/disposition") common += "Content-Disposition: attachment; filename=\"../../CON.txt\"\r\n";
                if (route == "/unknown")
                {
                    await Head(stream, "200 OK", null, "Transfer-Encoding: chunked\r\n");
                    for (var offset = 0; offset < SmallBody.Length; offset += 8192)
                    {
                        var count = Math.Min(8192, SmallBody.Length - offset);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(count.ToString("x") + "\r\n"), _stop.Token);
                        await stream.WriteAsync(SmallBody.AsMemory(offset, count), _stop.Token);
                        await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
                    }
                    await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), _stop.Token);
                    return;
                }
                var useRange = range is not null && route != "/ignore";
                if (headers.TryGetValue("If-Range", out var ifRange) && ifRange != etag) useRange = false;
                var start = 0;
                var end = body.Length - 1;
                if (useRange)
                {
                    var bounds = range!.Split('=')[^1].Split('-');
                    start = int.Parse(bounds[0]);
                    end = int.Parse(bounds[1]);
                    if (start < 0 || start > end || end >= body.Length) { await Head(stream, "416 Range Not Satisfiable", 0, $"Content-Range: bytes */{body.Length}\r\n"); return; }
                    var reportedStart = route == "/bad" && range != "bytes=0-0" ? start + 1 : start;
                    await Head(stream, "206 Partial Content", end - start + 1, common + $"Content-Range: bytes {reportedStart}-{end}/{body.Length}\r\n");
                }
                else await Head(stream, "200 OK", body.Length, common);
                var send = end - start + 1;
                if (route == "/truncated" && range != "bytes=0-0") send = Math.Max(0, send / 2);
                if (route == "/disconnect" && range != "bytes=0-0" && Interlocked.Increment(ref _disconnect) == 1) send /= 2;
                for (var offset = start; offset < start + send; offset += 32_768)
                {
                    await stream.WriteAsync(body.AsMemory(offset, Math.Min(32_768, start + send - offset)), _stop.Token);
                    if (route == "/adaptive" && start < body.Length / 8 && range != "bytes=0-0") await Task.Delay(18, _stop.Token);
                    if (route is "/slow" or "/restart" or "/changed" or "/cleanup" || route.StartsWith("/queue", StringComparison.Ordinal)) await Task.Delay(2, _stop.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
        }
    }
    private async Task Head(NetworkStream stream, string status, long? length, string extra = "")
    {
        var text = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Type: application/octet-stream\r\n" + (length.HasValue ? $"Content-Length: {length}\r\n" : "") + extra + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token);
    }
    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++) bytes[index] = (byte)((index * 31L + index / 251 + seed) % 256);
        return bytes;
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_workers);
        _stop.Dispose();
    }
}
