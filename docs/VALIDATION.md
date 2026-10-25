# Flux 1.0.0 validation

Windows x64 qualification, updated October 25, 2026. This record distinguishes executed checks from untested scope.

## Executed automated checks

| Area | Observed result |
| --- | --- |
| Real HTTP/core integration | 33 passed, 0 failed |
| Native messaging, metadata, authenticated WebSocket and Grab All | 51 passed, 0 failed |
| Extension background/protocol/filter/media/package checks | 23 passed, 0 failed |
| Routing, scanner/script hooks, bounded media processes and real HLS | 19 passed, 0 failed |
| Published app and separate native-host invocation/framing | 5 checks per executable |

The core tests compare actual downloaded bytes, including pause/resume, forced process death, restarted checkpoints, changed server representations, adaptive range splitting, 32 connections, ordered priorities, collision policies, mirrors, global/per-job caps, category paths and scheduled start/stop/windows. See [core testing](CORE-TESTING.md).

The full revision build passed 33 core, 47 bridge, 19 service/HLS, 22 extension and 10 packaged-executable checks. After the final Grab All changes, the bridge and extension suites were rerun successfully with 51 and 23 checks respectively; the packaged executables are rebuilt and smoke-checked again by the package script. These are 136 distinct checks across the current suites, rather than counting repeated runs. A scheduled-stop race was fixed so Paused is reported only after writers finish cancellation/checkpointing. Bridge tests use isolated pipe names and an ephemeral port.

Additional core regressions exercise paused-property editing without losing private request headers, restart while preserving the previous completed file, explicit final-file deletion versus remove-history, twenty-job settings persistence and configurable exponential retry delay.

## Public YouTube media test

The actual C# metadata service and external download processor inspected `https://www.youtube.com/watch?v=aqz-KE-bpKQ`, offered the available quality list, downloaded the selected 720p MP4 and validated it with FFprobe. The output was `Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film.mp4`, 102,240,387 bytes, 1280×720, duration 634.624 seconds. SHA-256:

`c2b508b0489017033bc8c427eccd047e9f36f29439bfe63d3070897d9e053b84`

This used yt-dlp 2026.10.21 with Node 24 and its bundled EJS solver, no browser cookies, and normal TLS validation. The earlier historical extractor test URL returned Video unavailable and was not counted as a successful download. A real HLS regression also checks positive byte progress after fixing yt-dlp's print option suppressing progress. Media output-reader overflow/cancellation is tested against an owned child process to prevent blocked pipe deadlocks.

The media test downloads official yt-dlp/FFmpeg tools with upstream SHA-256 verification, generates a small HLS source through FFmpeg, serves it from a loopback fixture, downloads/merges it through the real external processor, and validates playback duration using FFprobe. A first-use archive-handle cleanup bug found during the test was fixed before the successful run. The scanner/script process test uses an owned harmless test executable; it does not assert Windows Defender detection accuracy.

## Actual desktop handoff and download

The original v1 desktop handoff test used isolated state and the actual extension background source with controlled browser APIs, paired the real native host, authenticated a real loopback WebSocket/heartbeat, and sent URL-specific cookie/referrer/UA/filename metadata. The app acknowledged it and presented the normal download flow. The following byte-hash result records that earlier test, not a repeated final-browser click-through.

The live desktop subsequently reported `Flux-e2e.bin` complete. The resulting file was 16,777,216 bytes and its SHA-256 exactly matched the deterministic HTTP fixture:

`570534c83d513a58a034225172d826687be23e086407e9cebcadd10f44848fee`

This test uses real application/network code and actual extension source, but controlled browser API fixtures; it is not a claim that the extension was installed through Chrome Web Store or AMO.

## UI inspection

The actual WPF content was exported by the application's explicit isolated screenshot diagnostic and visually inspected. The crimson/near-black theme, table layout, sidebar, empty state, typography and controls are present. Live UI accessibility inspection observed the completed-download status and native-host registration feedback. The external Windows screenshot/input helper returned graphics/geometry errors; a full automated mouse click-through was not established. No mock downloads are seeded in normal app mode.

The revised published UI exported the main window, a separate 650×710 Add popup and all six Options tabs. Main/popup/General/Media images were visually inspected. A normal launch was observed through accessibility showing the real first-run setup window; all three registry manifests pointed to `Flux.NativeHost.exe`. Chrome for Testing 156 loaded the actual Flux service worker in an isolated profile. The later live YouTube button-to-popup test was stopped when desktop automation was interrupted, so that complete sequence is not claimed as passed.

## Installer and archive qualification

`scripts/verify-installer.ps1` qualifies the current package by silently installing in a unique current-user QA directory, comparing every installed payload file to the publish output, checking app version and both native-protocol executables, confirming safe refusal of a locked-file upgrade, and uninstalling while preserving an unrelated user-file canary. Existing Flux registry values and shortcuts are restored after the test. The in-place test uninstaller can remain beside the canary; normal NSIS uninstall uses a temporary copy.

Final package hashes are written to `SHA256SUMS.txt`; public uploaded asset digests are checked against those local values before handoff. Source review excludes private signing keys, user state, media-tool caches, proprietary IDM files and raw analysis output.

## Limits of these results

This is one Windows x64 environment. Cross-PC/DPI/accessibility testing, a long-duration soak, real Internet throughput comparisons, every authenticated website/media service, actual power/hardware failure, antivirus detection quality, and extension-store approval/signing are not established by this suite. Supported video-site handling depends on upstream yt-dlp and site behavior. Chrome off-store CRX and unsigned Firefox distribution restrictions remain; unpacked/temporary testing instructions are provided. Flux does not claim identical IDM internals/performance or universal site compatibility.
