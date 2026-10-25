# Third-party components

Flux source and original artwork are MIT licensed. Upstream components retain their licenses.

- Microsoft .NET 10 Windows Desktop runtime: MIT and upstream third-party notices, https://github.com/dotnet/runtime and https://github.com/dotnet/wpf. Self-contained packages include the runtime; notices are under `licenses/`.
- Inter 4.1, by Rasmus Andersson and contributors: SIL Open Font License 1.1. Embedded unmodified. Full license: `assets/fonts/Inter-OFL.txt`, distributed as `licenses/Inter-OFL.txt`. https://github.com/rsms/inter
- JetBrains Mono, by JetBrains and contributors: SIL Open Font License 1.1. Embedded unmodified. Full license: `assets/fonts/JetBrainsMono-OFL.txt`, distributed as `licenses/JetBrainsMono-OFL.txt`. https://github.com/JetBrains/JetBrainsMono

## Media engines installed on first use

The installer does not redistribute yt-dlp or FFmpeg executables. A media job downloads these separate upstream tools over HTTPS into the current user's Flux tools directory and verifies the upstream SHA-256 digest before use. Internet access to GitHub and sufficient free disk space are needed on first use.

- yt-dlp: https://github.com/yt-dlp/yt-dlp; license/dependency notices: https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE and https://github.com/yt-dlp/yt-dlp#license. The official executable can include dependencies with different licenses from the source.
- FFmpeg: https://ffmpeg.org/legal.html; Windows build scripts/source: https://github.com/BtbN/FFmpeg-Builds. Flux requests the upstream GPL Windows build and retains its license. FFmpeg source: https://github.com/FFmpeg/FFmpeg.
- Deno, when no supported existing JavaScript runtime is available: MIT; https://github.com/denoland/deno and https://github.com/denoland/deno/blob/main/LICENSE.md. The separately downloaded runtime executes yt-dlp's YouTube challenge solver. An existing Node.js 22+ runtime can be used instead: https://github.com/nodejs/node/blob/main/LICENSE.

These run as separate processes; Flux's MIT license does not replace their licenses. Respect the upstream licenses if redistributing downloaded tools yourself.

The .NET SDK, Node.js and NSIS are build tools, not development installations shipped to users. NSIS installer license information: https://nsis.sourceforge.io/License.

No IDM executables, DLLs, extensions, icons, decompiled source, or local evidence are distributed.
