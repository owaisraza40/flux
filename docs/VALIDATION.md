# Flux 1.0.0 validation

Release qualification completed on Windows x64, October 24, 2026. This record distinguishes executed checks from untested scope.

## Executed automated checks

| Area | Observed result |
| --- | --- |
| Real HTTP/core integration | 30 passed, 0 failed |
| Native messaging, metadata and authenticated WebSocket | 43 passed, 0 failed |
| Extension background/protocol/filter/media/package checks | 20 passed, 0 failed |
| Routing, scanner/script process hooks and real HLS media integration | 9 passed, 0 failed |
| Published Windows EXE native-host invocation/framing | 5 passed, 0 failed |

The core tests compare actual downloaded bytes, including pause/resume, forced process death, restarted checkpoints, changed server representations, adaptive range splitting, 32 connections, ordered priorities, collision policies, mirrors, global/per-job caps, category paths and scheduled start/stop/windows. See [core testing](CORE-TESTING.md).

The final `scripts/build.ps1 -MediaIntegration` gate passed all 107 checks and produced the self-contained application, NSIS installer, extension packages and checksums. A scheduled-stop race found during qualification was fixed: the manager now reports a running transfer paused only after its writers finish cancellation and checkpointing. Native bridge tests use isolated pipe names and an ephemeral WebSocket port so they can run alongside the desktop app.

The media test downloads official yt-dlp/FFmpeg tools with upstream SHA-256 verification, generates a small HLS source through FFmpeg, serves it from a loopback fixture, downloads/merges it through the real external processor, and validates playback duration using FFprobe. A first-use archive-handle cleanup bug found during the test was fixed before the successful run. The scanner/script process test uses an owned harmless test executable; it does not assert Windows Defender detection accuracy.

## Actual desktop handoff and download

The published `Flux.exe` was started with isolated UI test state. A test executed the actual extension background source with controlled browser APIs, paired the real native host, authenticated a real loopback WebSocket/heartbeat, and sent URL-specific cookie/referrer/UA/filename metadata. The application acknowledged it and presented the normal download flow.

The live desktop subsequently reported `Flux-e2e.bin` complete. The resulting file was 16,777,216 bytes and its SHA-256 exactly matched the deterministic HTTP fixture:

`570534c83d513a58a034225172d826687be23e086407e9cebcadd10f44848fee`

This test uses real application/network code and actual extension source, but controlled browser API fixtures; it is not a claim that the extension was installed through Chrome Web Store or AMO.

## UI inspection

The actual WPF content was exported by the application's explicit isolated screenshot diagnostic and visually inspected. The crimson/near-black theme, table layout, sidebar, empty state, typography and controls are present. Live UI accessibility inspection observed the completed-download status and native-host registration feedback. The external Windows screenshot/input helper returned graphics/geometry errors; a full automated mouse click-through was not established. No mock downloads are seeded in normal app mode.

## Installer and archive qualification

- NSIS compiled successfully. Silent installation into a dedicated current-user QA directory returned zero; installed executable version was 1.0.0 and its SHA-256 matched the published build.
- All five native-host smoke checks passed again against the installed executable.
- Silent uninstall returned zero, removed the application and bundled files, and preserved an unrelated user-file canary. The test invoked the uninstaller in place, so its own executable remained alongside the canary; normal uninstall uses NSIS's temporary-copy mechanism.
- The portable ZIP executable matched the published executable. Its entries contained no private keys, user state, proprietary IDM files or old product files.
- All six release asset hashes matched `SHA256SUMS.txt` before publication.
- The final published application exported the README screenshot successfully and exited normally. No separate .NET installation was used to launch the packaged executable.

## Limits of these results

This is one Windows x64 environment. Cross-PC/DPI/accessibility testing, a long-duration soak, real Internet throughput comparisons, every authenticated website/media service, actual power/hardware failure, antivirus detection quality, and extension-store approval/signing are not established by this suite. Supported video-site handling depends on upstream yt-dlp and site behavior. Chrome off-store CRX and unsigned Firefox distribution restrictions remain; unpacked/temporary testing instructions are provided. Flux does not claim identical IDM internals/performance or universal site compatibility.
