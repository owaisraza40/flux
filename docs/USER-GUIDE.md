# Flux user guide

## Install

Run `Flux-Setup-1.0.0-win-x64.exe`. The per-user NSIS installer defaults to `%LOCALAPPDATA%\Programs\Flux`, adds a Start menu shortcut, and offers a desktop shortcut. It includes the .NET runtime and does not need administrator rights. For portable use, extract the complete ZIP and run `Flux.exe`.

This release is unsigned. Verify its origin and SHA-256 checksum. Windows may show an unknown publisher; do not disable security protections.

## Add and control downloads

Use **Add URL**, **Ctrl+N** inside Flux, or **Ctrl+Alt+D** from another application. Paste one HTTP/HTTPS URL per line. Choose filename, folder/category, priority, connections, speed limit, start/stop time, mirrors, request headers, scanning, and completion actions. Large range-capable downloads can use 8–32 connections; small files or servers without safe range support can use fewer.

Use each row's pause/resume/retry controls or the global buttons. Pausing joins active writers and retains exact partial offsets. If a server changes content or stops honoring validators, Flux restarts safely rather than combining incompatible bytes. After restart/reboot unfinished jobs reopen paused and Flux offers recovery. Do not move a partial file while a transfer is active. Clearing completed history keeps downloaded files.

## Queue, schedule, limits and categories

Drag rows in **Queued** to reorder them. The context menu sets High/Normal/Low priority; priority groups precede ordering inside a group. A newly queued high-priority job does not forcibly interrupt an active transfer.

Set a start and optional stop time per job, or enable a daily off-peak window in Settings. Overnight windows are supported. Scheduled stops join writers before pausing. Scheduling needs Flux open and does not wake the PC or install a background service. Manually paused jobs are not silently resumed by the scheduler.

Settings controls job concurrency, default connections, retries up to ten, global/default per-job speed limits, collision policy, and category folders. Zero means unlimited. To keep a global limit valid across native and external media transfers, Flux conservatively divides it by configured simultaneous jobs; some capacity can remain unused with fewer jobs.

Filename policies: **Rename** (default), **Overwrite** (deliberately replace a final file after successful transfer), and **Skip** (keep the existing file). Category auto-sort uses the configured paths when enabled. Filenames are sanitized.

## Browser and media

Follow [Browser integration](BROWSER-INTEGRATION.md). The extension pairs through native messaging and uses an authenticated loopback WebSocket on port6543. It sends only the selected URL's matching cookies, referrer, User-Agent, and filename hint. Flux shows a prefilled confirmation dialog. Configure filetype capture and domain allow/block lists in extension options. A failed desktop handoff falls back to the browser.

Video/audio buttons appear where a media URL or supported video page is detected. HLS, DASH and video pages use yt-dlp and FFmpeg. First media use installs verified upstream tools from GitHub; preparation can take time. Compatibility depends on the site and extractor. DRM content is not supported. Ordinary file transfers continue through Flux's own engine.

Use credentials only for resources you can access. Direct HTTP requests do not forward sensitive headers to unrelated redirect/mirror origins. Media cookies are placed in a domain-scoped temporary cookie jar, removed after the operation. Media context supports cookies, User-Agent and referrer; other custom headers belong to direct HTTP mode.

## Scanner and completion actions

All open/script actions default to **None**. A job can request a virus scan, open its result, play a sound, or run a locally chosen `.exe`/`.ps1` script. Flux locates Defender's `MpCmdRun.exe`, or uses a scanner selected in Settings. Custom scanners receive the file path as one argument. A nonzero scanner exit prevents subsequent configured actions.

An executable script receives the file path as one argument. PowerShell receives `-DownloadedFile <path>` under the normal execution policy; Flux does not bypass it. Script timeout is five minutes, scan timeout ten. Hook errors are displayed while keeping the saved file.

## Tray, clipboard and updates

With minimize-to-tray enabled, close/minimize keeps Flux running. The tray menu opens Flux, adds URLs, pauses/resumes, or exits. **Ctrl+Q** exits explicitly. Clipboard monitoring is opt-in, prompts only for recognized HTTP/HTTPS download extensions, and avoids repeated prompts for the same URL. It does not transmit clipboard history.

Update checks query the public `owaisraza40/flux` GitHub release feed and notify about newer versions; executables are never silently installed.

## Data, uninstall and troubleshooting

State is in `%LOCALAPPDATA%\Flux`: history, URLs, explicit headers, pairing state and downloaded media tools. These can contain credentials; never publish that directory. Uninstall removes app files and per-user native-host registrations while keeping downloads and user state.

- **403/401:** check URL expiry and matching authentication/referrer; never post credentials in issues.
- **One connection:** a small/unknown file or a server without safe byte ranges/validators.
- **Browser offline:** open Flux, confirm extension ID and native host, pair again, and check whether another program occupies port6543.
- **Resume restarts:** source content changed or could not be validated safely.
- **Media preparation error:** GitHub access or upstream checksum verification failed; see the visible status.
- **Hotkey busy:** another app registered Ctrl+Alt+D; the in-app Add URL remains available.

For bug reports provide version, a public/local reproduction, expected result, and visible error—not cookies, tokens or personal files.
