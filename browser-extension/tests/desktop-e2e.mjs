// Actual published app + actual extension background source + real loopback WebSocket.
// Browser APIs are controlled fixtures; no personal browser profile is modified.
import {spawn} from "node:child_process";
import {readFile,access} from "node:fs/promises";
import {resolve} from "node:path";
import vm from "node:vm";
import {createHash} from "node:crypto";
import assert from "node:assert/strict";
import {LoopbackWebSocket} from "./helpers/loopback-websocket.mjs";
import * as protocol from "../shared/protocol.mjs";

if(!process.argv[2])throw new Error("Usage: node browser-extension/tests/desktop-e2e.mjs <Flux.exe> [http-download-url] [--keep-open]");
const executable=resolve(process.argv[2]);await access(executable);
const target=process.argv.slice(3).find(value=>/^https?:/.test(value))||"https://example.invalid/Flux-e2e.bin";
const keepOpen=process.argv.includes("--keep-open");
const manifest=JSON.parse(await readFile(new URL("../chromium/manifest.json",import.meta.url),"utf8"));
const id=[...createHash("sha256").update(Buffer.from(manifest.key,"base64")).digest().subarray(0,16)].map(value=>String.fromCharCode(97+(value>>4),97+(value&15))).join("");
const origin="chrome-extension://"+id;
const frames=message=>{const body=Buffer.from(JSON.stringify(message)),header=Buffer.alloc(4);header.writeUInt32LE(body.length);return Buffer.concat([header,body]);};
async function nativeRequest(message){
  const child=spawn(executable,[origin+"/","--parent-window=0"],{windowsHide:true,shell:false,stdio:["pipe","pipe","pipe"]});
  const stdout=[],stderr=[];const timer=setTimeout(()=>child.kill(),25000);
  try{child.stdout.on("data",value=>stdout.push(value));child.stderr.on("data",value=>stderr.push(value));child.stdin.on("error",()=>{});
    const ended=new Promise((resolve_,reject)=>{child.once("error",reject);child.once("close",code=>resolve_(code));});
    child.stdin.end(frames(message));const code=await ended;if(code!==0)throw new Error("Native process failed: "+Buffer.concat(stderr).toString().slice(0,256));
    const bytes=Buffer.concat(stdout);if(bytes.length<4||bytes.readUInt32LE()!==bytes.length-4)throw new Error("Invalid actual-app native framing");
    return protocol.validateResponse(JSON.parse(bytes.subarray(4).toString("utf8")));
  }finally{clearTimeout(timer);}
}

const app=spawn(executable,["--ui-test-data"],{windowsHide:true,shell:false,stdio:"ignore"});
let appExited=false;app.on("close",()=>{appExited=true;});app.on("error",error=>console.error("App start:",error.message));
const sleep=ms=>new Promise(resolve_=>setTimeout(resolve_,ms));
const sockets=[];
try{
  const paired=await nativeRequest({version:1,action:"pair",origin});assert.equal(paired.ok,true);assert.match(paired.token,/^[A-Fa-f0-9]{64}$/);
  console.log("PASS: actual published EXE paired the extension native origin (token not logged)");
  const state={fluxPairToken:paired.token,fluxSettings:{...protocol.DEFAULT_SETTINGS}};let receiver;const selectedCookies=[];
  const noopEvent={addListener(){}};
  const api={runtime:{getURL:()=>origin+"/",onInstalled:noopEvent,onMessage:{addListener(value){receiver=value;}}},
    storage:{local:{async get(key){return {[key]:state[key]};},async set(value){Object.assign(state,value);}}},
    action:{async setBadgeText(){},async setBadgeBackgroundColor(){},async setTitle(){}},
    cookies:{async getAll({url}){selectedCookies.push(url);return [{name:"flux_fixture",value:"local_test_only"}];}},
    tabs:{async query(){return [{id:1,url:"https://fixture.example/page"}];},onRemoved:noopEvent},
    contextMenus:{onClicked:noopEvent,async removeAll(){},create(){}},webRequest:{onBeforeRequest:noopEvent},downloads:{onCreated:noopEvent},alarms:{create(){},onAlarm:noopEvent}};
  class DesktopSocket extends LoopbackWebSocket {constructor(url){super(url,origin);sockets.push(this);}}
  const source=(await readFile(new URL("../shared/background.mjs",import.meta.url),"utf8")).replace(/^import .*?;\r?\n/gm,"");
  const context=vm.createContext({...protocol,api,URL,WebSocket:DesktopSocket,navigator:{userAgent:"Flux-E2E-Fixture/1"},console,setTimeout,clearTimeout,setInterval:()=>0,
    callApi:(owner,method,...args)=>owner[method](...args),nativeRequest,rememberStatus:async()=>{}});
  new vm.Script(source,{filename:"actual-background.mjs"}).runInContext(context);
  const request=message=>new Promise(resolve_=>receiver(message,{tab:{id:1,url:"https://fixture.example/page"}},resolve_));
  let connected=false;
  for(let attempt=0;attempt<20&&!connected;attempt++){await sleep(500);connected=(await request({type:"status"})).ok;}
  assert.equal(connected,true,"Actual desktop WebSocket did not become ready");
  console.log("PASS: actual extension background authenticated its real loopback WebSocket and heartbeat");
  const response=await request({type:"send",downloads:[{url:target,fileName:"Flux-e2e.bin"}]});
  assert.equal(response.ok,true,response.message);assert.equal(response.count,1);assert.deepEqual(selectedCookies,[target]);
  console.log("PASS: actual background captured URL-specific cookies/referrer/UA/filename and received actual app acknowledgement");
  console.log("Dialog expected: Flux-e2e.bin; referrer https://fixture.example/page; UA Flux-E2E-Fixture/1; cookie flux_fixture=local_test_only.");
  console.log("The app owns the pre-filled dialog. No download starts until it is confirmed.");
  if(keepOpen){console.log("KEEP_OPEN: inspect/confirm the isolated Flux dialog, then exit Flux to complete this test session.");while(!appExited)await sleep(500);}
}finally{
  for(const socket of sockets)socket.close();
  if(!keepOpen&&!appExited)app.kill();
}
