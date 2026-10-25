# Flux user guide

## Install

Run `Flux-Setup-1.0.0-win-x64.exe`. The per-user NSIS installer defaults to `%LOCALAPPDATA%\Programs\Flux`, adds a Start menu shortcut, and offers a desktop shortcut. It includes the .NET runtime and does not need administrator rights. For portable use, extract the complete ZIP and run `Flux.exe`.

This release is unsigned. Verify its origin and SHA-256 checksum. Windows may show an unknown publisher; do not disable security protections.

## Add and control downloads

Use **Add URL**, **Ctrl+N** or **Ins** inside Flux, or **Ctrl+Alt+D** from another application. A separate movable popup opens; a browser capture does not switch the main window to a download panel. Paste one HTTP/HTTPS URL per line. Choose filename, folder/category, priority, connections, speed limit, start/stop time, mirrors, request headers, scanning, and completion actions. Large range-capable downloads can use 8–32 connections; small files or servers without safe range support can use fewer.

Use each row's pause/resume/retry controls or the global buttons. Pausing joins active writers and retains exact partial offsets. If a server changes content or stops honoring validators, Flux restarts safely rather than combining incompatible bytes. After restart/reboot unfinished jobs reopen paused and Flux offers recovery. Do not move a partial file while a transfer is active. Clearing completed history keeps downloaded files.

## Queue, schedule, limits and categories

The toolbar is ordered Add URL, Grab All, Resume All, Stop All, Schedule, Options, followed by search and total speed. Grab All asks the connected browser for up to 100 HTTP/HTTPS anchor links from its active page and opens one batch popup; without a connected browser it extracts URLs from copied text. The sidebar can collapse. List columns include source domain, added date, rate and time left; the status bar includes counts, speed and target-drive free space.

Right-click a row for Resume, Pause, Restart, Open file/folder, Copy URL, Properties and removal actions. Restart starts over while keeping the previous completed file. Remove from list / Keep downloaded file keeps the final file; Delete downloaded file explicitly deletes that selected completed file. Removing a job cleans up its owned unfinished data. Pause a job before editing its name, destination, priority or limit in Properties. Changing the source/path/name resets its partial data; unrelated property edits retain custom request headers.

Double-click opens a completed file or shows properties. **Ctrl+P** toggles the selected job, **Del** removes its row, and **Ctrl+A** opens Grab All when focus is outside an editable text box.

Drag rows in **Queued** to reorder them. The context menu sets High/Normal/Low priority; priority groups precede ordering inside a group. A newly queued high-priority job does not forcibly interrupt an active transfer.

Set a start and optional stop time per job, or enable a daily off-peak window in Settings. Overnight windows are supported. Scheduled stops join writers before pausing. Scheduling needs Flux open and does not wake the PC or install a background service. Manually paused jobs are not silently resumed by the scheduler.

Options has six tabs. General controls category paths, collisions, 1–20 simultaneous jobs, opt-in Windows startup and minimize-to-tray. Connections controls 1–32 connections, global/default limits, retries up to ten and their exponential base delay. Browser Integration shows live authenticated Chrome/Edge/Firefox connections and capture/domain rules. Media selects tools, default quality and an optional browser-cookie source. Scheduler controls the daily window and its speed cap. Notifications controls sound, notifications and clipboard prompts. Zero bandwidth means unlimited. Flux conservatively divides the global limit across configured simultaneous jobs, so some capacity can remain unused.

Filename policies: **Rename** (default), **Overwrite** (deliberately replace a final file after successful transfer), and **Skip** (keep the existing file). Category auto-sort uses the configured paths when enabled. Filenames are sanitized.

## Browser and media

First normal launch registers the separate `Flux.NativeHost.exe` as `com.flux.bridge` for Chrome, Edge and Firefox, then displays extension installation instructions. Keep the complete installed/portable folder together. After loading the extension, choose Connect; its green badge and Flux's connected status indicate an authenticated live connection. The extension popup lists recent app transfers.

Follow [Browser integration](BROWSER-INTEGRATION.md). The extension pairs through native messaging and uses an authenticated loopback WebSocket on port6543. It sends only the selected URL's matching cookies, referrer, User-Agent, and filename hint. Flux shows a prefilled confirmation dialog. Configure filetype capture and domain allow/block lists in extension options. A failed desktop handoff falls back to the browser.

Small green Download buttons appear near detected video/audio players and supported YouTube, Vimeo, Dailymotion, Facebook, X/Twitter and Instagram pages. Detection depends on page markup and source availability. Clicking opens Flux's separate video popup, which loads the real title, thumbnail and available formats before enabling Start. Choose Best, an offered 1080p/720p/480p/360p MP4 option, or Audio only MP3, select a folder and start. The popup closes and the main list displays parsed download progress. Final filenames use the real title rather than `/watch`. Height-limited choices can use a lower available format; unavailable levels are omitted.

HLS, DASH and supported video pages use yt-dlp and FFmpeg. Flux checks the latest official yt-dlp build and uses its bundled EJS solver with Node 22+ or Deno 2.3+. If no supported runtime is present, it installs a SHA-256-verified per-user Deno runtime. First use downloads verified upstream tools from GitHub and can take time. Options → Media supports custom tool/runtime paths and explicitly selected Chrome, Edge or Firefox cookies for login-required media; None is the default. Profile locks or browser encryption may block cookie extraction. Compatibility depends on the site and extractor. DRM is not supported. Ordinary file transfers retain Flux's existing HTTP engine.

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
- **YouTube n-challenge / only images:** use automatic tools so Flux checks the current verified engine, verify the JavaScript runtime, and refresh video information. Certificate validation remains enabled; disabling it does not supply the JavaScript challenge solver.
- **Video unavailable / sign-in required:** verify the URL, access rights and selected cookie source. Removed videos cannot be repaired through format flags.
- **Hotkey busy:** another app registered Ctrl+Alt+D; the in-app Add URL remains available.

For bug reports provide version, a public/local reproduction, expected result, and visible error—not cookies, tokens or personal files.
