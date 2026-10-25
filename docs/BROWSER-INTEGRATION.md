# Flux browser integration

Flux captures selected HTTP/HTTPS links and opens a separate desktop download popup. Video requests resolve their real title and available qualities before you start the transfer. The extension pairs through the separate `Flux.NativeHost.exe`, then uses an authenticated `ws://localhost:6543/` connection. Keep both executables and the extension folders together.

## First launch

Install Flux or extract the complete portable ZIP, then open `Flux.exe`. On normal startup Flux registers its own native messaging manifests for Chrome, Edge and Firefox under the current Windows user's keys:

```text
HKCU\Software\Google\Chrome\NativeMessagingHosts\com.flux.bridge
HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.flux.bridge
HKCU\Software\Mozilla\NativeMessagingHosts\com.flux.bridge
```

The manifests live under `%LOCALAPPDATA%\Flux\NativeMessaging\<Browser>` and point to `Flux.NativeHost.exe`. No administrator rights or browser policy changes are required. First launch displays extension setup instructions. It cannot silently install extensions, and no Chrome/Edge store listing is claimed until a store publication exists. Browser Integration provides explicit registration repair if a portable folder moves.

## Chrome and Edge

1. In Flux, open **Browser integration** and choose **Open extension folder** if needed.
2. Open `chrome://extensions` or `edge://extensions`. Enable **Developer mode**, choose **Load unpacked**, and select `browser-extension/chromium` from the installed/extracted Flux folder.
3. Open the extension popup and choose **Check desktop connection**. Flux's automatically registered host pairs this browser profile. Its icon and **ON** badge turn green when the authenticated desktop connection is live.
4. If registration needs repair, use Flux's Browser Integration controls with the extension ID actually displayed in the browser. The shipped build identity is `ajkhpnkcciofgalpceobgmkbhegfnhoe`; contributor builds can have a different ID.
5. Send a direct file URL or use a page's **Download with Flux** button, then review the desktop popup and choose **Start Download**.

Release assets include both a Chromium ZIP and a CRX3 signed by the project's private build key. This is **not Chrome Web Store signing**. Consumer Chrome on Windows/macOS restricts off-store CRX installation; unpacked loading is provided for local testing. Public ordinary installation needs a Chrome Web Store/Edge Add-ons publication. Do not alter browser security policies to bypass distribution rules. See [Chrome distribution](https://developer.chrome.com/docs/extensions/how-to/distribute).

## Firefox

For local testing, open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on**, and select `browser-extension/firefox/manifest.json` or the release XPI. The stable add-on ID is `flux@flux.local`; use this ID for registration rather than Firefox's generated `moz-extension` UUID. Return to the extension popup and check the desktop connection.

Temporary add-ons disappear when Firefox restarts. The XPI is **unsigned**: persistent Release/Beta installation requires AMO signing and its external account/review process. Do not disable signature enforcement. See [Mozilla signing and distribution](https://extensionworkshop.com/documentation/publish/signing-and-distribution-overview/).

## Capture and fallback

- Matching ordinary file links are intercepted by content scripts at document start, including embedded frames, before their default browser navigation. Filetype and domain rules apply only to automatic capture.
- Browser-created downloads that cannot be intercepted as page links are observed after creation. Flux pauses them, requests a desktop dialog, and cancels the browser transfer only after acknowledgment. Browser APIs do not permit a promise of universal interception before any bytes arrive.
- Disconnected Flux leaves ordinary browser downloads alone. Refused/failed handoff attempts resume a paused browser transfer where the browser permits. Flux does not erase browser history or completed files.
- Right-click a link/video/audio/image/page and choose **Download with Flux**, or paste up to 100 URLs into the extension popup. These explicit sends are independent of automatic capture rules.
- Desktop acknowledgement means the separate download popup was requested, not that a transfer was started. Cancelling that popup cancels that requested handoff.
- The popup shows connection state, Add URL input, and up to ten recent downloads from the running desktop app, including their real filename/status/progress. It does not fabricate history.

Configure filetypes, whitelist and blacklist in desktop **Options → Browser Integration**. Desktop rules take precedence while connected. Browser options retain automatic-capture/media-button toggles and matching-cookie passthrough; local filetype/domain rules apply when no desktop rules have been received. A domain also matches its subdomains, block rules win, and an empty allow list allows every domain. Existing content scripts refresh settings within a few seconds.

## Video and audio

A compact green **Flux · Download** control follows the top-right of visible `<video>`/`<audio>` players. It supports embedded players and page URLs on YouTube, Vimeo, Dailymotion, Facebook, Twitter/X and Instagram. Site playback URLs such as blobs are not downloaded as files: the original page or a detected HLS/DASH manifest is sent to the desktop media resolver. Titles come from page metadata as hints; the desktop obtains authoritative title/formats from yt-dlp.

Clicking the media control opens Flux's separate popup with the resolved title, thumbnail when available, quality choices, save path and Start/Cancel controls. A supported site/player is not a guarantee of downloadable media: upstream extractor support, authentication, geographic rules, changing websites and available formats still apply. DRM decryption and access-control circumvention are not implemented.

## Protocol and privacy

The native host uses the browser-standard four-byte little-endian length prefix and UTF-8 JSON body. The host process can start/forward to `Flux.exe` through a bounded current-user-only named pipe; it does not create WPF windows itself. This is Flux's independent protocol, not a claim that IDM's proprietary payload is plain JSON of the same shape.

Native invocation establishes a per-profile 256-bit token. The WebSocket handshake requires a paired extension Origin and the first JSON message proves possession of its matching token. Ordinary webpage origins, wrong tokens, local-file URLs, oversized messages, duplicate/unknown capture fields and remotely specified executable/actions are rejected. Connected status in Flux comes from a currently authenticated socket, not merely a registry key.

After authentication a capture has this shape:

```json
{
  "type": "capture",
  "url": "https://example.com/video.mp4",
  "filename": "Video.mp4",
  "cookies": "session=example",
  "referrer": "https://example.com/page",
  "userAgent": "Browser user agent",
  "isMedia": true,
  "pageTitle": "Video title"
}
```

Cookie access is limited to the selected URL and can be disabled in extension options. Matching cookies, referrer, browser User-Agent and filename/title hints are sent only to the local app for that request. Unrelated origin cookies and browser history databases are not imported. Desktop state may retain URL/header credentials; never upload that state or private cookies in public issues. Pairing tokens live in extension-local storage and `%LOCALAPPDATA%\Flux\bridge-pairings.json`, never release assets.

## Build and test

```powershell
node browser-extension/scripts/package.mjs
node --test browser-extension/tests/*.test.mjs
dotnet restore browser-extension/tests/NativeBridge.Tests/NativeBridge.Tests.csproj --configfile NuGet.Config
dotnet run --project browser-extension/tests/NativeBridge.Tests/NativeBridge.Tests.csproj -c Release --no-restore
node browser-extension/tests/native-host-smoke.mjs artifacts/publish/Flux.NativeHost.exe
```

The package script generates Chromium/Firefox folders, CRX3/ZIP/XPI assets and verifies the CRX signature. The private signing key remains in git-ignored `.local/keys/flux-extension.pem`; only its public identity is shipped. Preserve that key privately to maintain the extension ID. A contributor's first local build generates a new key and matching registration identity.

The expanded local suites passed **47 native/WebSocket checks** and **22 Node checks** before final packaging. They cover both legacy and flat capture payloads, title metadata, matching cookies, wrong-token/origin rejection, actual active connection status, real recent-download responses, capture fallback and package signatures. Browser API tests execute the actual source with controlled browser/socket fixtures; they do not establish live browser installation or universal site support. See [validation](VALIDATION.md) for the final packaged/live-browser evidence and its limits.

## Troubleshooting

- **Grey OFF icon:** open Flux and check whether another application uses port 6543. Use the popup's connection check. The registered native fallback can start Flux for explicit sends.
- **Host not found:** keep `Flux.NativeHost.exe` beside `Flux.exe`, open the complete installed package, and repair Browser Integration registration. Moving a portable folder requires re-registration.
- **Host forbidden:** register the actual Chromium extension ID; Firefox uses `flux@flux.local`.
- **Disconnected after Disconnect all:** registrations and pairing tokens were removed. Re-register, then check the connection to pair again.
- **Login/media fails:** use only the intended site's matching cookies and the desktop's explicit cookie-source choice when needed. Never paste private credentials into public reports.
- **CRX/XPI rejected:** follow the distribution/signing limits above. Package generation alone does not establish store approval.
