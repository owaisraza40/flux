# Flux 1.0.0

An independently implemented, MIT-licensed Windows download manager with a native crimson/near-black interface.

## Updated build, October 25, 2026

- Fixed YouTube challenge support with the current SHA-verified yt-dlp release, bundled EJS solver and an explicitly configured supported JavaScript runtime. Public 720p extraction/download and FFprobe validation succeeded.
- Added real title/thumbnail/available-quality lookup in a separate floating download window; corrected video filenames and suppressed media progress output.
- Added separate `Flux.NativeHost.exe`, automatic Chrome/Edge/Firefox host registration and first-launch setup instructions, live connection status and recent transfers.
- Added authenticated Grab All from the browser's active page with one batch popup, plus clipboard fallback.
- Restructured the toolbar, columns, sidebar, properties and six Options tabs; preserved the existing engine and added regression checks for editing/restarting/deleting jobs.
- Installer safely rejects replacing a locked running application. Updated assets replace the original v1.0.0 packages; reinstall this build to receive these fixes.

The reference investigation and its unresolved parity boundaries are documented in `docs/IDM-REFERENCE.md`. The full real-browser button → popup → selected-quality click-through was interrupted; the successful public YouTube download was exercised through the actual C# media services, not a claim that this whole desktop interaction was completed automatically.

## Included

- Adaptive parallel HTTP transfers with up to 32 connections, safe pause/resume checkpoints, restart/crash recovery, and bounded retries up to ten.
- Global/per-download speed limits; priority and ordered queue; scheduled start/stop and daily off-peak windows.
- Rename/overwrite/skip policies, alternate mirrors, request User-Agent/referrer/cookies, category paths and searchable history.
- Tray controls, global Add URL hotkey, opt-in clipboard prompts, completion notifications and update checks.
- Optional virus scanner hook and explicit open/sound/local-script completion actions.
- Browser capture with URL-specific metadata, native pairing, authenticated localhost WebSocket heartbeat, filetype/domain rules and media buttons.
- HLS/DASH and supported media pages through separate yt-dlp/FFmpeg tools installed and checksum-verified on first media use.
- Self-contained Windows x64 app, per-user NSIS installer, portable archive, Chromium CRX3/ZIP and unsigned Firefox XPI.

## Install

Use `Flux-Setup-1.0.0-win-x64.exe`, or extract the complete portable ZIP and run `Flux.exe`. No administrator rights or separate .NET runtime are required for normal per-user installation. Verify SHA-256 values against `SHA256SUMS.txt`.

Browser setup and runtime details are in the README and `docs/BROWSER-INTEGRATION.md` / `docs/USER-GUIDE.md`.

## Verification and limits

The source includes deterministic core HTTP/recovery tests, native messaging/WebSocket checks, service checks and browser/package tests. See `docs/VALIDATION.md` in the repository for the actual final run results, packaged-app/installer checks and any unresolved scope. Screenshot rendering or mocked browser APIs do not establish all real-browser behavior.

Flux is not code-signed initially. The Chromium CRX is signed with its project build key, not the Chrome Web Store; Windows/macOS consumer Chrome restricts off-store CRX installation. Unpacked loading remains available for local testing. The Firefox XPI is unsigned and needs AMO signing for persistent Release/Beta installation; temporary loading is documented.

First media use needs official upstream GitHub downloads. Site/media compatibility varies; DRM handling and access-control circumvention are not implemented. Scheduling needs Flux running and does not wake the PC. Global cap allocation is conservative and can leave capacity unused. The project does not claim identical IDM algorithms, performance or universal site compatibility.

Personal downloads/history are retained on uninstall. Local state may contain URL/header credentials; do not include it in public reports. No proprietary IDM files or assets are included.
