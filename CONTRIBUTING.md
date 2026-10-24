# Contributing

Install the .NET 10 SDK and Node.js 24 or newer. Read the README and docs before changing download semantics.

Run `dotnet run --project tests/Flux.Tests/Flux.Tests.csproj -c Release` and `node --test browser-extension/tests/*.test.mjs`. For Windows release builds, install NSIS and run `./scripts/build.ps1` in PowerShell 7. Add `-MediaIntegration` to exercise the real, checksum-verified yt-dlp/FFmpeg backend against a generated local HLS fixture.

Add deterministic tests using local HTTP fixtures for changes to resume, HTTP validators, ranges, retries, queue state, and file persistence. Compare output bytes rather than progress percentages. Keep the core independent of WPF. Preserve existing user files and do not silently weaken TLS validation.

Open an issue describing a reproducible problem or proposed feature, then submit a focused pull request with test results. Changes must be independently written or accompanied by compatible license notices. Do not submit proprietary IDM files, copied assets, license bypasses, personal state, or raw local analysis dumps.
