export const HOST_NAME = "com.flux.downloads";
export const MAXIMUM_URLS = 100;
export const MAXIMUM_URL_LENGTH = 8192;
export const DEFAULT_SETTINGS = Object.freeze({ autoCapture: true, captureMedia: true, sendCookies: true,
  extensions: ["zip","7z","rar","exe","msi","iso","pdf","epub","mp4","mkv","webm","mp3","flac","m3u8","mpd"], allowDomains: [], blockDomains: [] });

export function isMediaUrl(value) {
  try { const url = new URL(value); return /\.(m3u8|mpd)(?:$|[?#])/i.test(url.href) || /(^|\.)(youtube\.com|youtu\.be|vimeo\.com|dailymotion\.com|dai\.ly)$/i.test(url.hostname); }
  catch { return false; }
}

export function domainMatches(host, rule) {
  const domain = String(rule).trim().toLowerCase().replace(/^\*\./, "");
  return domain.length > 0 && (host === domain || host.endsWith("." + domain));
}

export function shouldCapture(value, settings = DEFAULT_SETTINGS, filename = "") {
  let url; try { url = new URL(normalizeUrls([value])[0]); } catch { return false; }
  if (!settings.autoCapture || (settings.blockDomains || []).some(rule => domainMatches(url.hostname, rule))) return false;
  if ((settings.allowDomains || []).length && !settings.allowDomains.some(rule => domainMatches(url.hostname, rule))) return false;
  const path = filename || url.pathname;
  const extension = /\.([a-z0-9]+)$/i.exec(path)?.[1]?.toLowerCase();
  return Boolean(extension && (settings.extensions || []).map(value => String(value).toLowerCase().replace(/^\./, "").trim()).includes(extension));
}

export function normalizeDownload(value) {
  if (!value || typeof value !== "object") throw new Error("Invalid download metadata.");
  const result = { url: normalizeUrls([value.url])[0], isMedia: Boolean(value.isMedia || isMediaUrl(value.url)) };
  for (const [key, maximum] of [["cookies",16384],["referrer",8192],["userAgent",2048],["fileName",512]]) {
    if (value[key] == null || value[key] === "") continue;
    if (typeof value[key] !== "string" || value[key].length > maximum || /[\r\n\0]/.test(value[key])) throw new Error(`Invalid ${key}.`);
    result[key] = key === "referrer" ? normalizeUrls([value[key]])[0] : value[key];
  }
  return result;
}

export function normalizeUrls(input) {
  const lines = typeof input === "string" ? input.split(/\r?\n/) : input;
  if (!Array.isArray(lines)) throw new Error("Provide a URL or one URL per line.");
  const supplied = lines.map((value) => typeof value === "string" ? value.trim() : value).filter((value) => value !== "");
  if (supplied.length === 0) throw new Error("Paste at least one download link.");
  if (supplied.length > MAXIMUM_URLS) throw new Error(`Send no more than ${MAXIMUM_URLS} links at a time.`);
  return [...new Set(supplied.map((value) => {
    if (typeof value !== "string" || value.length > MAXIMUM_URL_LENGTH) throw new Error("A link is too long or is not text.");
    let url;
    try { url = new URL(value); } catch { throw new Error("Each line must contain a complete HTTP or HTTPS URL."); }
    if (!(["http:", "https:"].includes(url.protocol)) || !url.hostname) throw new Error("Only HTTP and HTTPS download links are supported.");
    if (url.username || url.password) throw new Error("Links containing a username or password are not supported by the browser bridge.");
    return url.href;
  }))];
}

export function validateResponse(value) {
  if (!value || typeof value !== "object" || typeof value.ok !== "boolean"
      || typeof value.message !== "string" || value.message.length > 4096
      || !Number.isInteger(value.count) || value.count < 0 || value.count > MAXIMUM_URLS
      || value.version !== 1) throw new Error("The native application returned an invalid response. Update Flux and its extension together.");
  return value;
}

export function explainNativeError(error) {
  const message = error?.message || String(error);
  if (/forbidden|permission|access to the specified native messaging host/i.test(message))
    return "The extension ID does not match its registration. Copy this browser's extension ID into Flux → Browser integration and reconnect.";
  if (/not found|not registered|not been registered/i.test(message))
    return "Connect this browser in Flux → Browser integration, then try again. The native host is not registered.";
  if (/exited|disconnected|failed to start|failed to communicate|broken pipe/i.test(message))
    return "The native bridge could not start. Open the installed Flux.exe, reconnect this browser, and try again.";
  return message;
}
