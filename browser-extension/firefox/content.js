(() => {
  const api=globalThis.browser||globalThis.chrome;
  const call=message=>globalThis.browser?api.runtime.sendMessage(message):new Promise((resolve,reject)=>api.runtime.sendMessage(message,response=>{
    const error=api.runtime.lastError;if(error)reject(new Error(error.message));else resolve(response);
  }));
  const bypass=new WeakSet();
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
    event.preventDefault();
    void(async()=>{
      try{
        const check=await call({type:"capture-check",url:link.href});
        if(!check?.capture)throw new Error("Normal browser download");
        const response=await call({type:"send",downloads:[{url:link.href,fileName:link.download||undefined,referrer:location.href,userAgent:navigator.userAgent}]});
        if(!response?.ok)throw new Error(response?.message||"Flux did not accept the link");
      }catch{bypass.add(link);link.click();setTimeout(()=>bypass.delete(link),0);}
    })();
  },true);

  const style=document.createElement("style");
  style.textContent=".flux-media-download{position:fixed;z-index:2147483646;background:#C8102E;color:#F0EEF5;border:1px solid #E8193A;border-radius:3px;padding:9px 12px;font:600 12px system-ui;cursor:pointer;box-shadow:none}.flux-media-download:hover{background:#E8193A}.flux-media-download:disabled{opacity:.6}";
  document.documentElement.append(style);
  let enabled=true,scheduled=false;
  const buttons=new Map();
  function refresh(){
    scheduled=false;
    for(const [element,button]of buttons){if(!element.isConnected){button.remove();buttons.delete(element);}}
    if(!enabled){for(const button of buttons.values())button.hidden=true;return;}
    const media=[...document.querySelectorAll("video,audio")].slice(0,6);
    if(!media.length&&/(^|\.)(youtube\.com|vimeo\.com|dailymotion\.com)$/.test(location.hostname))media.push(document.body);
    for(const element of media){
      let button=buttons.get(element);
      if(!button){button=document.createElement("button");button.className="flux-media-download";button.textContent="↓ Download with Flux";
        button.addEventListener("click",()=>{void(async()=>{button.disabled=true;try{
          const source=element.currentSrc||element.src||element.querySelector?.("source[src]")?.src;
          const response=await call({type:"media",url:/^https?:/.test(source||"")?source:location.href});
          button.textContent=response?.ok?"✓ Opened in Flux":"Flux unavailable — retry";
        }catch{button.textContent="Flux unavailable — retry";}finally{button.disabled=false;setTimeout(()=>button.textContent="↓ Download with Flux",3000);}})();});
        document.documentElement.append(button);buttons.set(element,button);}
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
  updateSettings();setInterval(updateSettings,10000);
  schedule();
})();
