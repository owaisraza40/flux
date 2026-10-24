# Download core: behavior and verification

The core is the existing independent C# HTTP engine extended for Flux v1. Its original tests and transfer logic were preserved. It does not load or redistribute IDM components, and the adaptive scheduler is not claimed to be IDM's proprietary algorithm.

## Run the suite

From the repository root, with the .NET SDK selected by `global.json` available as `dotnet`:

```powershell
dotnet restore tests/Flux.Tests/Flux.Tests.csproj --configfile NuGet.Config
dotnet run --project tests/Flux.Tests/Flux.Tests.csproj -c Release --no-restore
```

The console test project has no external test-framework dependency. It uses raw TCP HTTP servers on assigned loopback ports, generated deterministic data, the real `DownloadManager` and HTTP client, and SHA-256 comparisons of completed files. It returns a nonzero exit code on failure.

Generated artifacts are retained in the printed temporary test directory for inspection. The crash case owns and forcibly stops its own child process; no personal downloads or IDM executables are executed.

## Observed v1 result

The current Windows x64 suite completed **30 passed, 0 failed** with .NET SDK 10.0.402. Its Release build completed without warnings/errors. Final release-wide results belong in [VALIDATION.md](VALIDATION.md).

The 30 scenarios are:

1. Parallel ranges produce the exact expected hash and clean owned partial/checkpoint files.
2. Servers ignoring Range fall back to a full sequential GET.
3. HEAD is not needed; unknown chunked lengths complete.
4. Pause/resume retains durable offsets and exact output content.
5. An orderly restart restores paused history and resumes partial data.
6. A changed ETag discards old partial regions.
7. Malformed transfer Content-Range causes a safe sequential restart.
8. Truncated responses fail without producing a completed file.
9. Transient HTTP 503 responses are retried.
10. An interrupted range retries from its received offset.
11. Mid-transfer entity changes restart into one coherent representation.
12. Corrupt checkpoints are discarded and traversal/reserved server filenames are sanitized.
13. A per-download bandwidth cap is shared across its connections.
14. Existing files and duplicate requested destinations are preserved under Rename.
15. Pause All gates active/new work and queue pumping.
16. Future scheduled work does not contact the server prematurely.
17. Remove cleanup affects only the job's owned partial/checkpoint paths.
18. Zero-byte content, safe filenames, permanent errors and local-file URL rejection behave correctly.
19. Cross-origin redirects do not receive original custom credential headers.
20. A sufficiently large file uses the configured 32 connections and retains its exact hash.
21. Forced child-process death recovers flushed offsets without orderly manager disposal.
22. A freed worker splits a still-active slow range, checkpoints the additional region, and survives restart.
23. Priority and explicit reorder affect a held queue's actual request order.
24. Overwrite is explicit, Skip avoids a known-existing chosen target, and Rename remains the default.
25. Alternate mirrors work without receiving original credentials; configured User-Agent remains available.
26. A global cap bounds two simultaneous downloads.
27. Per-download overrides and scheduled stop pause/join writers.
28. A closed daily window holds work; reopening resumes scheduler-paused work without overriding manual pause.
29. User-Agent/referrer/matching cookies are applied and completion-subscriber failures cannot damage the completed job.
30. Category destinations and external processors retain normal queue semantics.

These are exercised fixture cases, not a declaration that every possible server or race has been exhausted.

## Transfer and persistence contract

- The probe uses GET `Range: bytes=0-0`, not HEAD.
- Known length, observed ranges and a usable strong ETag or Last-Modified validator are required for safe range resume. Otherwise a single stream is used and an interruption restarts it.
- Initial regions are at least about 512 KiB, constrained by configured connections and size; the maximum is 32 workers. Small files can use fewer.
- Adaptive redistribution stops and joins a selected active writer before dividing its remaining region. No two live workers are intentionally assigned overlapping ownership. The resulting region topology is saved in checkpoints.
- Range responses validate status, coordinates, total length, entity identity, declared length and received bytes. Identity encoding is requested. Entity/range inconsistencies cause a joined, coherent restart.
- Checkpoints capture counters before the corresponding disk flush, then write atomically. Recovery can repeat uncheckpointed bytes rather than claiming they were durable.
- Per-job partial files use `.flux-<download-id>.sfpart`, with `.sfresume` checkpoints. They must not be moved or edited while a job is active.
- Finalization applies Rename, explicit Overwrite or Skip to the selected destination. A new process racing to create a final filename is handled by the relevant policy. Completed history removal does not delete the output.
- Response headers/stream reads have a 35-second timeout. Transient connection/I/O/timeouts, 408/425/429 and server failures receive bounded exponential-backoff retries. The supported retry setting is zero through ten.
- HTTPS certificate validation remains enabled; HTTPS-to-HTTP redirects are refused. Custom credentials stay within the permitted origin boundary and are not automatically sent to unrelated mirrors.
- Default per-download caps, per-job overrides and the global cap are supported. The global budget is conservatively shared across configured concurrency, including media processors, which can underuse available bandwidth with fewer jobs.

## Queue and application state

Queue concurrency is one through eight jobs; connection concurrency is separately configurable up to 32 per eligible download. Priorities are High/Normal/Low. Explicit moves update persisted ordering; priority groups still affect selection. A higher priority queued job does not forcibly interrupt a currently running job.

Per-job start/stop times and daily windows require the manager/application to remain running. Overnight windows are supported. Scheduler pauses are distinct from manual pauses. Pause All is a persisted gate that also holds newly added jobs.

History and settings are saved atomically in `downloads.json` under the manager's state directory. The desktop uses `%LOCALAPPDATA%/Flux`. Full URLs and supplied headers/cookies can be stored there. Treat the directory as private.

Unfinished active/queued work is restored paused, while future Scheduled entries retain their schedule. A malformed history file is preserved under an `.invalid-<timestamp>` name, and the UI can show `LastPersistenceError`. Removing an entry does not delete a completed output; explicit partial cleanup is limited to owned paths.

`IExternalDownloadProcessor` permits the app to route media work through its separately managed process engine without rewriting queue semantics. The core suite uses a controlled processor stub for that boundary; actual yt-dlp/FFmpeg testing is part of the services/media integration and final validation report.

## Verification limits

The suite does not prove exact IDM parity, maximum Internet throughput, every external site/proxy, all Windows versions, or resilience against every disk/power/hardware failure. A forced process kill differs from losing power during a physical device write. Disk flushes cannot correct hardware that misreports durability, and reused/incorrect server validators cannot identify all changed content. Cross-volume finalization, disk-full/fault injection and long-duration soak behavior need additional testing.

The core itself does not implement FTP, torrents, DRM, site crawling, or a Windows background service. HLS/DASH and media pages are handled by the app's separate media processor; its successful fixture results must not be inferred from the core suite.
