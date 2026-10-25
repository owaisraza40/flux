(function initializeFlux() {
  if(!document.documentElement){setTimeout(initializeFlux,10);return;}
  const api=globalThis.browser||globalThis.chrome;
  const call=message=>globalThis.browser?api.runtime.sendMessage(message):new Promise((resolve,reject)=>api.runtime.sendMessage(message,response=>{
    const error=api.runtime.lastError;if(error)reject(new Error(error.message));else resolve(response);
  }));
  const bypass=new WeakSet();
  api.runtime.onMessage.addListener((message,_sender,reply)=>{
    if(message?.type!=="collect-links")return;
    const urls=[];
    for(const element of document.querySelectorAll("a[href],area[href]")){
      const url=element.href;if(!/^https?:/.test(url)||urls.includes(url))continue;
      try{const parsed=new URL(url);if(parsed.username||parsed.password||url.length>8192)continue;}catch{continue;}
      urls.push(url);if(urls.length>=100)break;
    }
    reply({urls});return false;
  });
  let captureConnected=false,captureConfig=null;
  function locallyMatches(url){
    if(!captureConnected||!captureConfig?.autoCapture)return false;
    let parsed;try{parsed=new URL(url);}catch{return false;}
    const matches=rule=>{const domain=String(rule).trim().toLowerCase().replace(/^\*\./,"");return domain&&(parsed.hostname===domain||parsed.hostname.endsWith("."+domain));};
    if(captureConfig.blockDomains?.some(matches))return false;
    if(captureConfig.allowDomains?.length&&!captureConfig.allowDomains.some(matches))return false;
    const extension=/\.([a-z0-9]+)$/i.exec(parsed.pathname)?.[1]?.toLowerCase();
    return extension&&captureConfig.extensions?.map(value=>String(value).toLowerCase().replace(/^\./,"")).includes(extension);
  }
  document.addEventListener("click",event=>{
    if(event.defaultPrevented||event.button!==0||event.ctrlKey||event.shiftKey||event.altKey||event.metaKey)return;
    const link=event.target.closest?.("a[href]");if(!link||bypass.has(link)||!/^https?:/.test(link.href))return;
    if(!locallyMatches(link.href))return;
    event.preventDefault();event.stopImmediatePropagation();
    void(async()=>{
      try{
        const check=await call({type:"capture-check",url:link.href});
        if(!check?.capture)throw new Error("Normal browser download");
        const response=await call({type:"send",downloads:[{url:link.href,fileName:link.download||undefined,referrer:location.href,userAgent:navigator.userAgent,pageTitle:document.title.slice(0,1024)}]});
        if(!response?.ok)throw new Error(response?.message||"Flux did not accept the link");
      }catch{bypass.add(link);link.click();setTimeout(()=>bypass.delete(link),0);}
    })();
  },true);
  const style=document.createElement("style");
  style.textContent=".flux-media-download{position:fixed;z-index:2147483646;background:#e6f5e7;color:#13371b;border:1px solid #42a34a;border-radius:3px;padding:6px 10px;font:600 12px system-ui;cursor:pointer;box-shadow:none}.flux-media-download:hover{background:#c6ecc9}.flux-media-download:disabled{opacity:.6}";
  document.documentElement.append(style);
  let enabled=true,scheduled=false;
  const buttons=new Map();
  function refresh(){
    scheduled=false;
    for(const [element,button]of buttons){if(!element.isConnected){button.remove();buttons.delete(element);}}
    if(!enabled){for(const button of buttons.values())button.hidden=true;return;}
    const media=[...document.querySelectorAll("video,audio")].slice(0,6);
    const videoSite=/(^|\.)(youtube\.com|youtu\.be|vimeo\.com|dailymotion\.com|facebook\.com|fb\.watch|twitter\.com|x\.com|instagram\.com)$/.test(location.hostname);
    for(const element of media){
      let button=buttons.get(element);
      if(!button){
        button=document.createElement("button");button.className="flux-media-download";button.textContent="↓ Flux · Download";button.title="Download with Flux";
        button.addEventListener("click",()=>{void(async()=>{
          button.disabled=true;
          try{
            const source=element.currentSrc||element.src||element.querySelector?.("source[src]")?.src;
            const response=await call({type:"media",url:videoSite?location.href:/^https?:/.test(source||"")?source:location.href,pageTitle:(document.querySelector('meta[property="og:title"]')?.content||document.title).slice(0,1024)});
            button.textContent=response?.ok?"✓ Opened in Flux":"Flux unavailable — retry";
          }catch{button.textContent="Flux unavailable — retry";}
          finally{button.disabled=false;setTimeout(()=>button.textContent="↓ Flux · Download",3000);}
        })();});
        document.documentElement.append(button);buttons.set(element,button);
      }
      const rect=element.getBoundingClientRect();
      button.hidden=rect.width<120||rect.bottom<0||rect.top>innerHeight;
      button.style.top=Math.max(12,Math.min(innerHeight-48,rect.top+12))+"px";
      button.style.left=Math.max(12,Math.min(innerWidth-190,rect.right-190))+"px";
    }
  }
  function schedule(){if(!scheduled){scheduled=true;requestAnimationFrame(refresh);}}
  new MutationObserver(schedule).observe(document.documentElement,{childList:true,subtree:true});
  addEventListener("scroll",schedule,{passive:true});addEventListener("resize",schedule,{passive:true});
  function updateSettings(){void call({type:"settings"}).then(value=>{captureConfig=value?.settings;captureConnected=value?.connected===true;enabled=value?.settings?.captureMedia!==false;schedule();}).catch(()=>{captureConnected=false;});}
  updateSettings();setInterval(updateSettings,3000);schedule();
})();
