import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { normalizeUrls, validateResponse, explainNativeError, DEFAULT_SETTINGS, shouldCapture, normalizeDownload, domainMatches, isMediaUrl } from "../shared/protocol.mjs";

test("normalizes batches, ignores blank lines, and removes duplicates", () => {
  assert.deepEqual(normalizeUrls(" https://example.com/a\r\n\nhttps://example.com/a\nHTTP://EXAMPLE.COM/b "), ["https://example.com/a", "http://example.com/b"]);
});
test("rejects non-download protocols, malformed URLs, and credentials", () => {
  for (const value of ["javascript:alert(1)", "file:///C:/private", "blob:https://example.com/id", "ftp://example.com/a", "localhost/path", "https://name:password@example.com/file"])
    assert.throws(() => normalizeUrls([value]), Error);
});
test("bounds batch and individual URL lengths and rejects nontext values", () => {
  assert.throws(() => normalizeUrls([]));
  assert.throws(() => normalizeUrls(Array.from({ length: 101 }, () => "https://example.com/a")));
  assert.throws(() => normalizeUrls(["https://example.com/" + "a".repeat(8192)]));
  assert.throws(() => normalizeUrls([null]));
  assert.throws(() => normalizeUrls([{}]));
});
test("validates native responses instead of trusting arbitrary JSON", () => {
  assert.equal(validateResponse({ ok: true, count: 1, message: "Sent", version: 1 }).ok, true);
  for (const value of [null, {}, { ok: true, count: -1, message: "bad", version: 1 }, { ok: true, count: 1, message: "bad", version: 9 }])
    assert.throws(() => validateResponse(value));
});
test("native failures provide actionable recovery without losing the original URL", () => {
  assert.match(explainNativeError(new Error("Specified native messaging host not found")), /not registered/);
  assert.match(explainNativeError(new Error("Access to the specified native messaging host is forbidden")), /extension ID/);
});
test("Chromium package declares capture permissions and matching generated native identity", async () => {
  const manifest = JSON.parse(await readFile(new URL("../chromium/manifest.json", import.meta.url), "utf8"));
  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.background.service_worker, "background.mjs");
  for(const permission of ["nativeMessaging","contextMenus","activeTab","storage","cookies","downloads","webRequest","alarms"]) assert.ok(manifest.permissions.includes(permission));
  assert.deepEqual(manifest.host_permissions,["<all_urls>"]);
  assert.equal(manifest.content_scripts[0].js[0],"content.js");
  const digest = createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest().subarray(0, 16);
  const id = [...digest].map((value) => String.fromCharCode(97 + (value >> 4), 97 + (value & 15))).join("");
  const nativeIdentity=await readFile(new URL("../../src/Flux.App/Integration/ExtensionIdentity.cs",import.meta.url),"utf8");
  assert.ok(nativeIdentity.includes(`"${id}"`));
});
test("capture filtering respects settings and exact/subdomain allow/block rules",()=>{
  assert.equal(shouldCapture("https://example.com/a.zip"),true);
  assert.equal(shouldCapture("https://example.com/page"),false);
  assert.equal(shouldCapture("https://example.com/without-extension",DEFAULT_SETTINGS,"download.pdf"),true);
  assert.equal(shouldCapture("https://example.com/a.zip",{...DEFAULT_SETTINGS,autoCapture:false}),false);
  assert.equal(shouldCapture("https://private.example.com/a.zip",{...DEFAULT_SETTINGS,blockDomains:["example.com"]}),false);
  assert.equal(shouldCapture("https://example.com/a.zip",{...DEFAULT_SETTINGS,allowDomains:["other.test"]}),false);
  assert.equal(domainMatches("notexample.com","example.com"),false);
  assert.equal(domainMatches("sub.example.com","*.example.com"),true);
});
test("HLS, DASH, and supported media pages are classified for media processor",()=>{
  for(const url of ["https://a.test/movie.m3u8?token=x","https://a.test/manifest.mpd","https://www.youtube.com/watch?v=abc","https://youtu.be/abc","https://vimeo.com/123","https://www.dailymotion.com/video/abc"])assert.equal(isMediaUrl(url),true);
  assert.equal(isMediaUrl("https://youtube.com.evil.test/a"),false);
});
test("browser metadata preserves only intended fields and rejects header injection",()=>{
  const value=normalizeDownload({url:"https://example.com/a",cookies:"session=one",referrer:"https://example.com/page",userAgent:"Browser/1",fileName:"a.mp4",isMedia:true,completionScript:"not accepted"});
  assert.equal(value.cookies,"session=one");assert.equal(value.completionScript,undefined);assert.equal(value.isMedia,true);
  assert.throws(()=>normalizeDownload({url:"https://example.com/a",cookies:"a=b\r\nInjected: value"}));
  assert.throws(()=>normalizeDownload({url:"https://example.com/a",referrer:"file:///secret"}));
});
test("Firefox package uses an event page and stable Gecko ID", async () => {
  const manifest = JSON.parse(await readFile(new URL("../firefox/manifest.json", import.meta.url), "utf8"));
  assert.equal(manifest.background.service_worker, undefined);
  assert.deepEqual(manifest.background.scripts, ["background.mjs"]);
  assert.equal(manifest.browser_specific_settings.gecko.id, "flux@flux.local");
});
