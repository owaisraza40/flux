# Architecture

Flux is a Windows WPF application with an independent, UI-free HTTP engine. The UI works with detached state snapshots; it does not own HTTP writers or schedule transfer tasks itself.

```text
User / clipboard / command line
              |
       WPF download dialog
              |
       DownloadManager ------------------ CompletionService
          |         |                     scanner / explicit action
          |         +-- MediaDownloadProcessor
          |                 |
     DownloadEngine     yt-dlp + FFmpeg (managed first-use setup)
          |
     HTTP range workers
          |
     partial file + durable checkpoint -> final file

Browser extension -> native pairing -> authenticated loopback WebSocket
                                            |
                                   pre-filled WPF dialog
```

## Core ownership

`Flux.Core` owns queue state, settings, priorities, ordering, scheduling, operation cancellation, persistence, and transfer lifetime. `DownloadManager` serializes state transitions with its gate and joins cancelled operations before starting competing writers. Public snapshots clone mutable lists rather than exposing live internal state to WPF.

New jobs receive High/Normal/Low priority and a queue order. Queue selection respects priority, explicit reordering, start time, stop time, the global Pause All gate, daily windows, and configured concurrency. A newly queued high-priority job does not preempt a running job. Scheduler-paused and manually paused work are distinguished.

History/settings are stored atomically in `downloads.json`. Unfinished jobs restore paused; future scheduled entries retain scheduling. A malformed history file is preserved with an `.invalid-<timestamp>` suffix. The application presents recovery rather than silently starting every previous transfer.

## Native HTTP transfer

The engine probes with GET `Range: bytes=0-0`, avoiding dependence on HEAD. Range support, known size, and a usable entity validator determine whether segmented/resumable transfer is safe. Unsupported ranges and unknown lengths use a sequential response.

Parallel work starts with disjoint regions, constrained by file size and up to 32 workers. When a worker finishes, the adaptive coordinator can stop/join a slow active writer, divide its remaining region, and run both resulting regions. It never deliberately assigns overlapping live writers. Saved region topology and completed offsets are retained for later recovery.

Every range validates response status, Content-Range coordinates/total, entity validators, encoding, Content-Length where supplied, and received byte counts. If a server changes or stops honoring the representation/range contract, the engine joins old writers before restarting one coherent transfer. It requests identity encoding and keeps operating-system TLS validation enabled.

`.flux-<id>.sfpart` files and `.sfresume` checkpoints are owned per download. Checkpoint snapshots do not intentionally claim writes beyond a completed disk flush. After a crash, uncheckpointed data may be fetched again. Disk flushes cannot repair hardware that misreports persistence, and server validators cannot detect a server that incorrectly reuses an ETag/date for changed content.

Finalization applies the chosen collision policy. Rename avoids existing files, Skip retains an existing chosen destination, and Overwrite is explicit and happens after transfer success. Mirrors are tried within bounded policy; original credential headers do not follow an unrelated mirror origin.

Bandwidth caps exist at the job and global levels. A conservative per-job share also keeps external media processes within configured overall limits; this can underuse bandwidth with fewer jobs than configured maximum concurrency. It is a correctness-oriented cap, not a throughput/fairness benchmark.

## Media and completion services

`IExternalDownloadProcessor` lets `DownloadManager` keep its queue/lifecycle while delegating HLS/DASH or supported media-page jobs. The app supplies `MediaDownloadProcessor`, which manages separate yt-dlp/FFmpeg processes, parses progress, preserves resumable media work, and reports the resulting file.

`MediaTools` obtains official upstream release assets and requires a published SHA-256 digest before running downloaded tools. Executables are installed into the current user's Flux data directory on first media use, not redistributed in the Flux installer. Arguments use `ProcessStartInfo.ArgumentList`, not shell-concatenated commands. Cookies use a temporary domain-scoped jar that is removed afterwards. Media context allows scoped cookies, User-Agent and Referer; arbitrary custom headers remain a direct-HTTP feature.

`CompletionService` handles only locally requested scanning/actions. A nonzero scanner exit prevents subsequent actions. Scripts must be explicitly selected local `.ps1`/`.exe` files; PowerShell's ordinary execution policy is respected. The browser DTO cannot specify a destination, program, scanner, or completion action. A hook error does not delete the saved output.

The update service checks the `owaisraza40/flux` release feed and returns a notification link. It does not replace the app executable automatically.

## Browser boundary

The extension uses explicit capture rules, context menus, popup sends and media hints. Only matching target-URL cookies are read. Content scripts cannot provide local executable/action settings through the accepted DTO.

Native messaging uses a bounded four-byte little-endian length plus JSON. The native host establishes a per-user token for the caller's browser-extension origin and can start/forward to the desktop through a current-user-only named pipe. The desktop listens on IPv4 loopback port 6543; the WebSocket upgrade requires a paired extension Origin, and the first message proves the matching token. Ordinary web-page Origins are rejected before accepting download data.

The app displays a pre-filled confirmation dialog. A transport acknowledgement means the dialog request was accepted, not that a transfer completed. Browser-created downloads are cancelled only after acknowledgement; disconnected/refused handoffs are left or resumed in the browser where possible.

## Desktop lifecycle

WPF owns dispatching, table presentation, dialogs, tray UI, the opt-in clipboard monitor and global hotkey. The native host dispatch is selected before WPF startup so native stdout remains binary protocol output. A current-user single-instance mutex prevents duplicate managers. Explicit exit cancels bridge servers and awaits manager disposal; ordinary close can minimize to tray according to settings.

The diagnostic screenshot mode renders Flux's own visual tree and uses isolated test state. It is not a screenshot of unrelated desktop windows and does not seed fake completed downloads.

## Distribution and evidence boundaries

The .NET self-contained publish output supplies the app/runtime. NSIS packages it per user. Browser package generation creates a CRX3 with a locally retained private build key and an unsigned Firefox XPI. Store signing and actual browser acceptance are external distribution steps, not properties established by a valid ZIP/CRX signature.

See [CORE-TESTING.md](CORE-TESTING.md), [BROWSER-INTEGRATION.md](BROWSER-INTEGRATION.md), and [VALIDATION.md](VALIDATION.md) for exercised behavior and remaining limits. Local reference artifacts and reverse-engineering evidence stay outside the public repository. The engine is not a recovered copy of IDM's proprietary implementation.
