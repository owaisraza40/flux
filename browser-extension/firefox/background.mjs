import { api, nativeRequest, rememberStatus, callApi } from "./browser-api.mjs";
import { DEFAULT_SETTINGS, normalizeUrls, normalizeDownload, captureMessage, shouldCapture, isMediaUrl, validateResponse, domainMatches } from "./protocol.mjs";

const MENU_ID = "flux-send-download";
const extensionBase = new URL(api.runtime.getURL(""));
const origin = extensionBase.protocol + "//" + extensionBase.hostname;
let socket, connecting, transaction = Promise.resolve();
let recentDownloads=[], desktopSettings=null;
const mediaByTab = new Map();
const intercepted = new Set();

async function settings() {
  const stored = await callApi(api.storage.local, "get", "fluxSettings");
  return { ...DEFAULT_SETTINGS, ...stored.fluxSettings, ...(desktopSettings || {}) };
}

async function badge(connected, message) {
  await api.action.setBadgeText({ text: connected ? "ON" : "OFF" });
  await api.action.setBadgeBackgroundColor({ color: connected ? "#249749" : "#6B6880" });
  try { await api.action.setIcon({path:connected?"icons/connected32.png":"icons/offline32.png"}); } catch { }
  await api.action.setTitle({ title: message || (connected ? "Flux desktop connected" : "Open Flux to connect") });
}

function exchange(connection, message, limit = 3500) {
  return new Promise((resolve,reject) => {
    const timer = setTimeout(() => done(new Error("Flux did not acknowledge the message.")),limit);
    const received = event => {
      try { const parsed=JSON.parse(event.data);if(parsed?.action==="grab-links")return;const response=validateResponse(parsed); if(Array.isArray(response.recentDownloads))recentDownloads=response.recentDownloads.slice(0,10); if(response.captureSettings&&typeof response.captureSettings==="object")desktopSettings=response.captureSettings; done(null,response); } catch { done(new Error("Invalid Flux response.")); }
    };
    const closed = () => done(new Error("Flux connection closed."));
    function done(error,response) {
      clearTimeout(timer); connection.removeEventListener("message",received); connection.removeEventListener("close",closed);
      if(error) reject(error); else resolve(response);
    }
    connection.addEventListener("message",received); connection.addEventListener("close",closed,{once:true});
    try { connection.send(JSON.stringify(message)); } catch(error) { done(error); }
  });
}

async function connect(pair) {
  if(socket?.readyState === WebSocket.OPEN) return socket;
  if(connecting) return connecting;
  connecting = (async () => {
    let token = (await callApi(api.storage.local,"get","fluxPairToken")).fluxPairToken;
    if(pair || !token) {
      const response = await nativeRequest({version:1,action:"pair",origin});
      if(!response.ok || !/^[A-Fa-f0-9]{64}$/.test(response.token || "")) throw new Error(response.message || "Pairing failed.");
      token=response.token;
      await callApi(api.storage.local,"set",{fluxPairToken:token});
    }
    if(!token) throw new Error("Connect this browser in Flux, then send a link to pair it.");
    const connection = new WebSocket("ws://localhost:6543/");
    await new Promise((resolve,reject) => {
      const timer=setTimeout(()=>{connection.close();reject(new Error("Flux desktop is not listening."));},1400);
      connection.addEventListener("open",()=>{clearTimeout(timer);resolve();},{once:true});
      connection.addEventListener("error",()=>{clearTimeout(timer);reject(new Error("Flux desktop is not connected."));},{once:true});
    });
    let response;try{response=await exchange(connection,{action:"hello",token,userAgent:navigator.userAgent,browser:/Edg\//.test(navigator.userAgent)?"Edge":globalThis.browser?"Firefox":"Chrome"});}catch(error){connection.close();throw error;}
    if(!response.ok) {connection.close();throw new Error("Pairing was rejected. Reconnect the browser in Flux.");}
    socket=connection;
    connection.addEventListener("message",event=>{
      let command;try{command=JSON.parse(event.data);}catch{return;}
      if(command?.action!=="grab-links"||typeof command.requestId!=="string"||!/^[a-f0-9]{32}$/.test(command.requestId))return;
      void(async()=>{
        let urls=[];
        try{
          const tab=(await callApi(api.tabs,"query",{active:true,currentWindow:true}))[0];
          if(tab?.id!=null){const result=await callApi(api.tabs,"sendMessage",tab.id,{type:"collect-links"},{frameId:0});if(Array.isArray(result?.urls)&&result.urls.length)urls=normalizeUrls(result.urls).slice(0,100);}
        }catch{}
        const next=transaction.catch(()=>{}).then(()=>exchange(connection,{action:"page-links",requestId:command.requestId,urls}));
        transaction=next;await next;
      })().catch(error=>rememberStatus(false,error.message));
    });
    connection.addEventListener("close",()=>{if(socket===connection)socket=null;void badge(false);});
    await badge(true);
    return connection;
  })();
  try{return await connecting;}finally{connecting=null;}
}

async function collect(value, tab) {
  const config=await settings();
  const download=normalizeDownload(value);
  download.referrer=download.referrer || (/^https?:/.test(tab?.url || "") ? tab.url : undefined);
  download.userAgent=download.userAgent || navigator.userAgent;
  download.pageTitle=download.pageTitle || (tab?.title?.length<=1024?tab.title:undefined);
  if(config.sendCookies && !download.cookies && api.cookies) {
    try {
      const cookies=await callApi(api.cookies,"getAll",{url:download.url});
      const value=cookies.sort((left,right)=>(right.path?.length||0)-(left.path?.length||0)).map(cookie=>`${cookie.name}=${cookie.value}`).join("; ");
      if(value.length<=16384)download.cookies=value;
    }catch { /* Cookie policy can deny access; URL handoff remains usable. */ }
  }
  return normalizeDownload(download);
}

async function sendDownload(value, tab) {
  const download=await collect(value,tab);
  let connection;
  try { connection=await connect(true); }
  catch {
    const response=await nativeRequest({version:1,action:"download",downloads:[download]});
    await rememberStatus(response.ok,response.message);
    return response;
  }
  // Once sent, an uncertain acknowledgement must not cause a duplicate native send.
  const next=transaction.catch(()=>{}).then(()=>exchange(connection,captureMessage(download)));
  transaction=next;
  const response=await next;
  await rememberStatus(response.ok,response.message);
  return response;
}

async function heartbeat() {
  try {
    const connection=await connect(false);
    const next=transaction.catch(()=>{}).then(()=>exchange(connection,{action:"heartbeat"}));
    transaction=next; const response=await next; await badge(response.ok);
  } catch { await badge(false); }
}

async function installMenu() {
  await callApi(api.contextMenus,"removeAll");
  api.contextMenus.create({id:MENU_ID,title:"Download with Flux",contexts:["link","video","audio","image","page"]});
}
api.runtime.onInstalled.addListener(()=>{void installMenu();void callApi(api.storage.local,"get","fluxSettings").then(value=>{
  if(!value.fluxSettings)return callApi(api.storage.local,"set",{fluxSettings:DEFAULT_SETTINGS});
});});
api.contextMenus.onClicked.addListener((info,tab)=>{
  if(info.menuItemId!==MENU_ID)return;
  void sendDownload({url:info.linkUrl||info.srcUrl||info.pageUrl||tab?.url,isMedia:info.mediaType==="video"||info.mediaType==="audio"},tab)
    .catch(error=>rememberStatus(false,error.message));
});

api.runtime.onMessage.addListener((message,sender,sendResponse)=>{
  const operation=(async()=>{
    if(message?.type==="status") {await heartbeat();return {ok:socket?.readyState===WebSocket.OPEN,message:socket?.readyState===WebSocket.OPEN?"Flux desktop connected.":"Open Flux and register this browser to connect.",count:0,version:1,recentDownloads:socket?.readyState===WebSocket.OPEN?recentDownloads:[]};}
    if(message?.type==="settings"){
      const config={...await settings(),...(desktopSettings||{})};let host="";try{host=new URL(sender.tab?.url||"").hostname;}catch{}
      const domainAllowed=!(config.blockDomains||[]).some(rule=>domainMatches(host,rule))&&(!(config.allowDomains||[]).length||config.allowDomains.some(rule=>domainMatches(host,rule)));
      return {ok:true,settings:{...config,captureMedia:config.captureMedia&&(!sender.tab||domainAllowed)},connected:socket?.readyState===WebSocket.OPEN};
    }
    if(message?.type==="capture-check")return {ok:true,capture:socket?.readyState===WebSocket.OPEN&&shouldCapture(message.url,await settings())};
    if(message?.type==="send") {
      let tab=sender.tab;
      if(!tab){try{tab=(await callApi(api.tabs,"query",{active:true,currentWindow:true}))[0];}catch{}}
      const values=message.downloads || normalizeUrls(message.urls).map(url=>({url}));
      if(!Array.isArray(values)||values.length<1||values.length>100)throw new Error("Send between one and 100 links.");
      let count=0;
      for(const value of values){const response=await sendDownload(value,tab);if(!response.ok)return response;count++;}
      return {ok:true,message:`Opened ${count} download dialog(s) in Flux.`,count,version:1};
    }
    if(message?.type==="media") {
      const config=await settings();if(!config.captureMedia)throw new Error("Media buttons are disabled in extension settings.");
      const captured=mediaByTab.get(sender.tab?.id)||[];
      const url=message.url||captured.at(-1)||sender.tab?.url;
      return await sendDownload({url,isMedia:true,fileName:message.fileName,pageTitle:message.pageTitle},sender.tab);
    }
    throw new Error("Unknown Flux extension request.");
  })();
  operation.then(sendResponse,error=>sendResponse({ok:false,message:error.message,count:0,version:1}));
  return true;
});

api.webRequest?.onBeforeRequest.addListener(details=>{
  if(details.tabId<0||!isMediaUrl(details.url)||!/[.]m3u8|[.]mpd/i.test(details.url))return;
  const values=mediaByTab.get(details.tabId)||[];
  if(!values.includes(details.url)){values.push(details.url);mediaByTab.set(details.tabId,values.slice(-12));}
},{urls:["<all_urls>"]});
api.tabs.onRemoved.addListener(id=>mediaByTab.delete(id));

api.downloads?.onCreated.addListener(item=>{
  void(async()=>{
    const config=await settings();
    if(intercepted.has(item.id)||socket?.readyState!==WebSocket.OPEN||!shouldCapture(item.finalUrl||item.url,config,item.filename))return;
    intercepted.add(item.id);
    let paused=false;
    try {
      await callApi(api.downloads,"pause",item.id);paused=true;
      const response=await sendDownload({url:item.finalUrl||item.url,referrer:item.referrer,fileName:item.filename?.split(/[\\/]/).at(-1)},null);
      if(!response.ok)throw new Error(response.message);
      await callApi(api.downloads,"cancel",item.id);
    }catch(error){if(paused){try{await callApi(api.downloads,"resume",item.id);}catch{}}await rememberStatus(false,`Browser download retained: ${error.message}`);}
    finally{intercepted.delete(item.id);}
  })();
});

if(api.alarms){api.alarms.create("flux-heartbeat",{periodInMinutes:0.5});api.alarms.onAlarm.addListener(alarm=>{if(alarm.name==="flux-heartbeat")void heartbeat();});}
setInterval(()=>void heartbeat(),20000);
void heartbeat();
