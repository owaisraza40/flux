# Release guide

Release target: `owaisraza40/flux`, tag `v1.0.0`. Publish only after the code, artifacts, checksums, validation report, and documentation describe the same build.

## 1. Prepare the source

Confirm the product/assembly/package version, installer version, extension manifest version, artifact names, README links and release notes agree. Preserve existing tests when changing engine behavior. Review `THIRD-PARTY-NOTICES.md` and included font/runtime notices.

Do not stage `.local`, `artifacts`, `bin`, `obj`, private extension keys, user state, downloaded media tools, browser cookies/tokens, proprietary IDM files or raw local analysis dumps. The extension private key under `.local/keys/flux-extension.pem` must remain outside Git. Retain it securely if maintaining the same Chromium package identity across releases.

## 2. Build and run automated checks

Requirements: Windows x64, PowerShell 7, .NET 10 SDK, Node.js 24+, NSIS `makensis.exe`.

```powershell
pwsh -File scripts/build.ps1 -NsisCompiler 'C:\Program Files (x86)\NSIS\makensis.exe'
```

For the real local HLS/media test and first-use media-tool verification:

```powershell
pwsh -File scripts/build.ps1 -MediaIntegration -NsisCompiler 'C:\Program Files (x86)\NSIS\makensis.exe'
```

This additional test needs access to official GitHub media-tool downloads. Do not describe it as executed unless its actual run passed. Normal automated tests cover core HTTP behavior, native metadata/WebSocket protocol, app services and extension/package logic. Actual published-EXE native messaging is also checked by the build.

Do not use `-SkipTests` when qualifying a release. `.github/workflows/build.yml` repeats the Windows package build and uploads artifacts; CI results must be checked rather than assumed.

## 3. Validate packaged behavior

- Run the **published** `artifacts/publish/Flux.exe` without depending on the SDK runtime.
- Inspect the dark UI, Add URL dialog, settings and browser page; check scaling, keyboard access and any visible errors.
- Use a controlled loopback server to download a sufficiently large range-capable file. Pause/resume and compare the result's hash. Include restart/crash recovery evidence from the core suite.
- Run `node browser-extension/tests/native-host-smoke.mjs artifacts/publish/Flux.exe`.
- Run the actual-background/desktop handoff harness:

```powershell
node browser-extension/tests/desktop-e2e.mjs artifacts/publish/Flux.exe http://127.0.0.1:YOUR_FIXTURE_PORT/file.bin --keep-open
```

Inspect the real pre-filled dialog and confirm a controlled download when appropriate. The harness's browser API fixtures are not equivalent to loading an extension in an actual browser.

- Test the NSIS installer in a current-user test location, confirm launch/runtime independence and bundled files, then test uninstall. Retain evidence of any remaining user files/state. Never use a broad recursive deletion against a user data directory.
- If testing live Chrome/Edge/Firefox integration, use an isolated test profile and record the browser version, actual extension loading path, capture and fallback results. Do not claim store signing or browser compatibility based only on package tests.

Update `docs/VALIDATION.md` with commands/results and explicit untested scope. Avoid marking a test complete simply because the code compiled.

## 4. Review release files

Expected assets include:

- `Flux-Setup-1.0.0-win-x64.exe`
- `Flux-1.0.0-win-x64-portable.zip`
- `Flux-Chromium-1.0.0.crx`
- `Flux-Chromium-1.0.0.zip`
- `Flux-Firefox-1.0.0.xpi`
- `Flux-Browser-Extension-1.0.0.zip`
- `SHA256SUMS.txt`

The build writes checksums under `artifacts/release`. Recalculate them after the final package changes, and inspect that archives contain no secrets, old product files or test data. The app executable is initially unsigned. Chromium CRX signing uses the build key, not Chrome Web Store signing; Firefox XPI remains unsigned unless AMO signing has separately completed.

## 5. Publish

After validation, use the existing authenticated Git helper/approved GitHub workflow. The supplied `scripts/publish-github.ps1` has an explicit validation gate:

```powershell
pwsh -File scripts/publish-github.ps1 -Owner owaisraza40 -Repository flux -Tag v1.0.0 -Validated
```

Inspect its planned repository/release settings first. Never put tokens in repository files, log them, embed them in remote URLs, or substitute a different account. A validation switch is an operator assertion, not an automatic test result.

Confirm the source commit/tag, public repository visibility, release title/body, attachment names/sizes and downloadable checksums after publishing. Check the resulting Windows CI run. Link the actual repository and release when handing off; report external authentication/signing/policy blockers accurately.

## Browser-store distribution

Direct consumer Chrome CRX installation on Windows/macOS is restricted. Publish through the Chrome Web Store for ordinary public installation; use unpacked loading for local testing. Firefox requires AMO signing for persistent Release/Beta installation. Store developer accounts, review and signing are separate from GitHub packaging and cannot be claimed until those services confirm completion.
