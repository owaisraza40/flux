# Flux browser integration

Flux has independent Chromium (Chrome/Edge) and Firefox extensions. Selected download metadata is handed to the local Flux desktop app, which opens a pre-filled download dialog. The normal transport is `ws://localhost:6543/`; native messaging establishes a per-user pairing token and provides a fallback that can start Flux.

## Install on Chrome or Edge

1. Install Flux or extract its complete portable archive. Keep `browser-extension/chromium` beside `Flux.exe`.
2. Open `chrome://extensions` or `edge://extensions`, enable Developer mode, choose **Load unpacked**, and select the **chromium** folder.
3. Open Flux → **Browser integration**. Register Chrome or Edge with the extension ID shown in the browser. The shipped key currently produces `ajkhpnkcciofgalpceobgmkbhegfnhoe`; use the ID actually displayed by your build.
4. Open the extension popup, choose **Check desktop connection**, and send a link. The first send pairs that browser profile through its registered native host.
5. Confirm the pre-filled URL, destination, and settings in Flux.

The release also contains `Flux-Chromium-1.0.0.crx`. It is a valid CRX3 signed with the project build key, **not Chrome Web Store signing**. Consumer Chrome on Windows/macOS restricts installing CRX packages from outside the Chrome Web Store. Use the unpacked folder for local testing; public consumer distribution requires a Chrome Web Store publication. Do not change enterprise/security policies to bypass this restriction. See [Chrome's distribution rules](https://developer.chrome.com/docs/extensions/how-to/distribute).

## Install on Firefox

The Firefox package has stable add-on ID `flux@flux.local`.

For local testing, open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on**, and select `browser-extension/firefox/manifest.json` or the release XPI. Temporary add-ons are removed when Firefox restarts. Register Firefox from Flux's Browser integration page.

`Flux-Firefox-1.0.0.xpi` is an **unsigned XPI**, not an AMO-signed release. Release/Beta Firefox requires Mozilla signing for persistent installation. The repository includes source for submitting a listed or unlisted build to AMO; an authenticated Mozilla developer account and review/signing are separate external requirements. Do not disable signature enforcement. See [Mozilla signing and distribution](https://extensionworkshop.com/documentation/publish/signing-and-distribution-overview/).

## Capture behavior

- **Right-click** a link, video, audio, image, or page and choose **Download with Flux**.
- **Popup** accepts one or up to 100 HTTP/HTTPS URLs.
- **Automatic capture** is enabled for configured extensions when the desktop heartbeat is connected. Obvious file links are handed off from the content script; browser-created downloads can be paused, handed off, then cancelled only after desktop acknowledgement.
- If Flux is disconnected, ordinary browser downloads remain in the browser. If a paused handoff is refused or fails before acknowledgement, the extension attempts to resume that browser download. It does not erase the browser's history or delete completed files.
- An acknowledgement means Flux accepted the request for a dialog, not that the user started or completed a transfer. Cancelling Flux's dialog cancels that requested handoff.
- The extension passes the selected URL, matching URL cookies (when enabled), page referrer, browser user agent, and available filename hint. Cookies from unrelated origins are not collected.
- Captured HLS/DASH manifest hints and media buttons mark the request as media, allowing Flux's media processor to handle it.

Choose **Capture rules & privacy settings** from the popup or the browser's extension options. Configure automatic capture, media buttons, cookie passthrough, comma-separated file extensions, and allowed/blocked domain lists. A domain rule also matches its subdomains. Block rules take precedence; an empty allow list means any domain. Reload existing pages after changing media-button settings. Explicit popup/context-menu sends are intentional requests and are not blocked by the automatic-capture domain list.

The app heartbeat badge reads **ON** when connected. A disconnected/idle badge is blank. The popup explains native-host registration failures and provides the current extension ID.

## Media buttons

Content scripts add a compact crimson **Download with Flux** control to visible video/audio elements and to supported YouTube, Vimeo, and Dailymotion pages. The extension also records recent `.m3u8` and `.mpd` request URLs for the current tab.

Availability is not a promise that a particular site, login, stream format, geographic restriction, or DRM-protected media is supported. Blob URLs are not file URLs; Flux may need the original page or discovered manifest and its separate media tools. DRM decryption and access-control circumvention are not implemented.

## Permissions and local data

The manifests request `nativeMessaging`, `contextMenus`, `activeTab`, `storage`, `cookies`, `downloads`, `webRequest`, `alarms`, and HTTP/HTTPS host access. Those permissions enable the explicitly described capture functions. Captured credentials are passed only to your local Flux process for the selected download. Flux may retain URLs/headers in its private local history; do not share that state publicly.

Pairing tokens are stored in extension-local storage and `%LOCALAPPDATA%/Flux/bridge-pairings.json`. Tokens are not copied into source or release assets. Native manifests live under `%LOCALAPPDATA%/Flux/NativeMessaging/<Browser>`; registrations use only that Windows user's HKCU native-messaging keys. Pairing is through native messaging, never through a web page.

The WebSocket listener binds IPv4 loopback on port 6543. It requires an extension Origin already paired by native invocation plus the matching 256-bit token. It rejects ordinary HTTP page origins, wrong tokens, unsupported URLs, oversized messages, duplicate native properties, and browser-supplied executable/local-action settings. Native fallback uses a current-user-only named pipe.

No browser history database is imported and no unrelated browser cookies are sent to Flux. Cookie/header transmission to the actual remote download server is controlled by the requested download and the desktop engine's redirect rules.

## Rebuild and package

From the repository root:

```powershell
node browser-extension/scripts/build.mjs
node browser-extension/scripts/package.mjs
node --test browser-extension/tests/*.test.mjs
dotnet restore browser-extension/tests/NativeBridge.Tests/NativeBridge.Tests.csproj --configfile NuGet.Config
dotnet run --project browser-extension/tests/NativeBridge.Tests/NativeBridge.Tests.csproj -c Release --no-restore
```

The package script creates CRX3, Chromium ZIP, and Firefox XPI under `artifacts/release`. It verifies its RSA signature before writing the CRX. Tests independently decode and verify the CRX3 header/signature and both package contents.

The first build generates an RSA private key under `.local/keys/flux-extension.pem` (git-ignored). **Keep this private build key outside Git** if maintaining the same release identity; losing it changes the extension ID on future builds. Only its public key and generated public ID are included in source/packages. A new contributor's own first build generates their own identity, so they must register the ID shown by that build.

After desktop publishing, test the actual executable's inherited binary stdin/stdout:

```powershell
node browser-extension/tests/native-host-smoke.mjs artifacts/publish/Flux.exe
```

## Verification completed and remaining scope

The native integration harness preserves its original 29 checks and adds metadata, pairing, and real loopback WebSocket cases: **43 passed**. These include matching/wrong tokens, refused ordinary web origins, heartbeat, end-to-end socket-to-app callback metadata, and local-file/script rejection.

Node checks cover URL/metadata validation, domain/type filtering, HLS/DASH classification, Chrome/Firefox manifests, matching cookie passthrough, disconnected native fallback, browser pause/cancel acknowledgement, resume on refusal, and CRX3/XPI package structure/signature: **20 passed**.

Browser API capture tests execute the actual background script against controlled browser/socket fixtures. They do not claim live Chrome/Edge/Firefox UI installation, store signing, all browser versions, or success on every media site. Actual browser loading and a download-dialog click-through should be checked before claiming those specific environments are verified.

## Troubleshooting

- **No ON badge:** open Flux. Ensure another application has not occupied localhost port 6543. An unavailable WebSocket can use the registered native fallback for an explicit send.
- **Native host not registered:** register the browser from the published Flux.exe. Moving the portable executable requires re-registration.
- **ID forbidden:** copy the browser's actual extension ID, not a folder name or a Firefox moz-extension UUID.
- **After Disconnect all:** both native registrations and pairing tokens are removed. Reconnect in Flux and send a link to pair again.
- **Cookie authentication fails:** confirm cookie passthrough is enabled and the URL belongs to the intended site. Partitioned/site-specific login behavior may require separate handling; do not paste private cookies into public issues.
- **Firefox install rejected / CRX blocked:** follow the signing/distribution limitations above; the files are packages, not proof of store approval.
