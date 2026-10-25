import {test} from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import {readFile} from "node:fs/promises";
import * as protocol from "../shared/protocol.mjs";

const source=(await readFile(new URL("../shared/background.mjs",import.meta.url),"utf8")).replace(/^import .*?;\r?\n/gm,"");
const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));

async function environment({online=true,accept=true,native=true}={}){
  const observed={native:[],socket:[],cookies:[],downloads:[],badges:[],connections:[],tabMessages:[]};
  const callbacks={};
  const store={fluxPairToken:"A".repeat(64),fluxSettings:{...protocol.DEFAULT_SETTINGS}};
  const event=name=>({addListener(listener){callbacks[name]=listener;}});
  const api={
    runtime:{getURL:()=>"chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",onInstalled:event("installed"),onMessage:event("message")},
    storage:{local:{async get(key){return {[key]:store[key]};},async set(values){Object.assign(store,values);}}},
    action:{async setBadgeText(value){observed.badges.push(value.text);},async setBadgeBackgroundColor(){},async setTitle(){}},
    cookies:{async getAll(value){observed.cookies.push(value.url);return [{name:"session",value:"fixture"}];}},
    tabs:{async query(){return [{id:1,url:"https://example.com/page"}];},async sendMessage(id,message,options){observed.tabMessages.push({id,message,options});return {urls:["https://example.com/a.zip","https://example.com/b.pdf"]};},onRemoved:event("removed")},
    contextMenus:{async removeAll(){},create(){},onClicked:event("menu")},
    webRequest:{onBeforeRequest:event("request")},
    downloads:{onCreated:event("download"),async pause(id){observed.downloads.push(["pause",id]);},async resume(id){observed.downloads.push(["resume",id]);},async cancel(id){observed.downloads.push(["cancel",id]);}},
    alarms:{create(){},onAlarm:event("alarm")}
  };
  class FakeWebSocket extends EventTarget{
    static OPEN=1;readyState=0;
    constructor(url){super();this.url=url;observed.connections.push(this);queueMicrotask(()=>{if(!online)this.dispatchEvent(new Event("error"));else{this.readyState=1;this.dispatchEvent(new Event("open"));}});}
    send(raw){const value=JSON.parse(raw);observed.socket.push(value);const response=value.action==="hello"?{ok:true,version:1,type:"ready",message:"ready",count:0}:value.action==="heartbeat"?{ok:true,version:1,type:"heartbeat",message:"active",count:0}:{ok:accept,version:1,message:accept?"Opened dialog":"Test desktop refusal",count:accept?1:0};queueMicrotask(()=>{const event=new Event("message");event.data=JSON.stringify(response);this.dispatchEvent(event);});}
    close(){this.readyState=3;this.dispatchEvent(new Event("close"));}
  }
  const context=vm.createContext({api,...protocol,URL,WebSocket:FakeWebSocket,navigator:{userAgent:"FixtureBrowser/1"},console,
    setTimeout,clearTimeout,setInterval:()=>0,callApi:(owner,method,...args)=>owner[method](...args),
    nativeRequest:async value=>{observed.native.push(value);if(!native)throw new Error("Native host unavailable");return value.action==="pair"?{ok:true,token:"A".repeat(64),message:"paired",count:0,version:1}:{ok:true,message:"Native dialog opened",count:1,version:1};},
    rememberStatus:async()=>{}});
  new vm.Script(source,{filename:"background.mjs"}).runInContext(context);
  await wait(20);
  const request=(message,sender={tab:{id:1,url:"https://example.com/page"}})=>new Promise(resolve=>callbacks.message(message,sender,resolve));
  return {observed,callbacks,request,api,store};
}

test("capture passes selected URL cookies, referrer, UA and filename through authenticated WebSocket",async()=>{
  const env=await environment();const result=await env.request({type:"send",downloads:[{url:"https://files.example.com/a.zip",fileName:"a.zip"}]});
  assert.equal(result.ok,true);assert.deepEqual(env.observed.cookies,["https://files.example.com/a.zip"]);
  const item=env.observed.socket.find(value=>value.type==="capture");
  assert.equal(item.cookies,"session=fixture");assert.equal(item.referrer,"https://example.com/page");assert.equal(item.userAgent,"FixtureBrowser/1");assert.equal(item.filename,"a.zip");
  assert.equal(env.observed.native.length,0);
});
test("disconnected WebSocket uses metadata-preserving native fallback for an explicit user send",async()=>{
  const env=await environment({online:false});const result=await env.request({type:"send",urls:["https://example.com/a.pdf"]});
  assert.equal(result.ok,true);const sent=env.observed.native.find(value=>value.action==="download");assert.equal(sent.downloads[0].url,"https://example.com/a.pdf");assert.equal(sent.downloads[0].cookies,"session=fixture");
});
test("ordinary browser download is untouched when the app is disconnected",async()=>{
  const env=await environment({online:false,native:false});env.callbacks.download({id:1,url:"https://example.com/a.zip",filename:"a.zip"});await wait(30);
  assert.deepEqual(env.observed.downloads,[]);assert.equal(env.observed.native.length,0);
});
test("browser pause/handoff cancels only after desktop acknowledgement",async()=>{
  const env=await environment();env.callbacks.download({id:2,url:"https://example.com/a.zip",filename:"a.zip"});await wait(40);
  assert.deepEqual(env.observed.downloads,[["pause",2],["cancel",2]]);
});
test("desktop refusal resumes the paused browser download instead of losing it",async()=>{
  const env=await environment({accept:false});env.callbacks.download({id:3,url:"https://example.com/a.zip",filename:"a.zip"});await wait(40);
  assert.deepEqual(env.observed.downloads,[["pause",3],["resume",3]]);assert.equal(env.observed.native.length,0);
});
test("domain blacklist suppresses browser capture",async()=>{
  const env=await environment();env.store.fluxSettings.blockDomains=["example.com"];env.callbacks.download({id:4,url:"https://example.com/a.zip",filename:"a.zip"});await wait(20);
  assert.deepEqual(env.observed.downloads,[]);
});
test("HLS request discovery routes capture to media processing",async()=>{
  const env=await environment();env.callbacks.request({tabId:1,url:"https://cdn.example.com/manifest.m3u8?token=fixture"});
  const result=await env.request({type:"media"});assert.equal(result.ok,true);
  const download=env.observed.socket.find(value=>value.type==="capture");assert.equal(download.isMedia,true);assert.match(download.url,/manifest.m3u8/);
});
test("cookie passthrough can be disabled without losing URL handoff",async()=>{
  const env=await environment();env.store.fluxSettings.sendCookies=false;
  const result=await env.request({type:"send",urls:["https://example.com/a.zip"]});assert.equal(result.ok,true);assert.deepEqual(env.observed.cookies,[]);
  assert.equal(env.observed.socket.find(value=>value.type==="capture").cookies,undefined);
});

test("video page title is carried without becoming an executable or local action",async()=>{
  const env=await environment();
  const response=await env.request({type:"media",url:"https://www.youtube.com/watch?v=fixture",pageTitle:"A public video",completionScript:"evil.exe"});
  assert.equal(response.ok,true);
  const sent=env.observed.socket.find(value=>value.type==="capture");
  assert.equal(sent.pageTitle,"A public video");assert.equal(sent.isMedia,true);assert.equal(sent.completionScript,undefined);
});

test("authenticated desktop Grab All asks current page content script and returns bounded links",async()=>{
  const env=await environment();
  const event=new Event("message");event.data=JSON.stringify({action:"grab-links",requestId:"b".repeat(32)});
  env.observed.connections[0].dispatchEvent(event);await wait(30);
  assert.equal(env.observed.tabMessages.length,1);assert.equal(env.observed.tabMessages[0].options.frameId,0);
  const reply=env.observed.socket.find(value=>value.action==="page-links");
  assert.equal(reply.requestId,"b".repeat(32));assert.deepEqual(reply.urls,["https://example.com/a.zip","https://example.com/b.pdf"]);
});
