// Tests the actual published GUI executable's native-host startup, not just the linked source harness.
import { spawn } from "node:child_process";
import { resolve } from "node:path";
import { access, readFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import assert from "node:assert/strict";

const executable = process.argv[2];
if (!executable) throw new Error("Usage: node browser-extension/tests/native-host-smoke.mjs <published Flux.exe>");
const path = resolve(executable);
await access(path);

function frame(message) {
  const payload = Buffer.from(JSON.stringify(message));
  const header = Buffer.alloc(4);
  header.writeUInt32LE(payload.length);
  return Buffer.concat([header, payload]);
}

async function exchange(arguments_, input, expectedOk, expectedExit = 0) {
  const child = spawn(path, arguments_, { windowsHide: true, shell: false, stdio: ["pipe", "pipe", "pipe"] });
  const stdout = [];
  const stderr = [];
  const timeout = setTimeout(() => child.kill(), 25_000);
  try {
    child.stdout.on("data", (value) => stdout.push(value));
    child.stderr.on("data", (value) => stderr.push(value));
    const result = new Promise((resolve_, reject) => {
      child.once("error", reject);
      child.once("close", (code, signal) => resolve_({ code, signal }));
    });
    child.stdin.on("error", () => {});
    child.stdin.end(input);
    const { code, signal } = await result;
    assert.equal(signal, null, "Native host did not finish before timeout");
    assert.equal(code, expectedExit, Buffer.concat(stderr).toString("utf8").slice(0, 4096));
    const output = Buffer.concat(stdout);
    assert.ok(output.length >= 4, "Missing native framing header");
    assert.equal(output.readUInt32LE(0), output.length - 4, "Stdout contains a malformed frame or non-protocol logging");
    const response = JSON.parse(output.subarray(4).toString("utf8"));
    assert.equal(response.ok, expectedOk);
    assert.equal(response.version, 1);
    return response;
  } finally {
    clearTimeout(timeout);
  }
}

await exchange(["--native-host"], frame({ action: "ping", version: 1 }), true);
console.log("PASS: published EXE explicit native-host startup and framed ping");
const chromiumManifest=JSON.parse(await readFile(new URL("../chromium/manifest.json",import.meta.url),"utf8"));
const chromiumId=[...createHash("sha256").update(Buffer.from(chromiumManifest.key,"base64")).digest().subarray(0,16)].map(value=>String.fromCharCode(97+(value>>4),97+(value&15))).join("");
await exchange([`chrome-extension://${chromiumId}/`, "--parent-window=0"], frame({ action: "ping", version: 1 }), true);
console.log("PASS: published EXE Chrome/Edge invocation detection");
await exchange([resolve("com.flux.downloads.json"), "flux@flux.local"], frame({ action: "ping", version: 1 }), true);
console.log("PASS: published EXE Firefox invocation detection");
await exchange(["--native-host"], frame({ action: "download", urls: ["file:///C:/private"] }), false);
console.log("PASS: published EXE rejects file URLs without launching a desktop download");
const oversized = Buffer.alloc(4);
oversized.writeUInt32LE(1024 * 1024 + 1);
await exchange(["--native-host"], oversized, false, 1);
console.log("PASS: published EXE rejects oversized native frames");
console.log("All 5 published native-host smoke checks passed.");
