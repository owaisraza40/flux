import {api,callApi,runtimeRequest} from "./browser-api.mjs";
import {DEFAULT_SETTINGS} from "./protocol.mjs";
const config={...DEFAULT_SETTINGS,...(await callApi(api.storage.local,"get","fluxSettings")).fluxSettings};
try{const desktop=await runtimeRequest({type:"settings"});if(desktop?.connected&&desktop.settings){Object.assign(config,desktop.settings);document.getElementById("result").textContent="Flux is connected. Edit filetype/domain rules in desktop Options → Browser Integration; those rules take precedence.";}}catch{}
for(const key of ["autoCapture","captureMedia","sendCookies"])document.getElementById(key).checked=config[key];
document.getElementById("extensions").value=config.extensions.join(", ");
for(const key of ["allowDomains","blockDomains"])document.getElementById(key).value=config[key].join("\n");
document.getElementById("settings").addEventListener("submit",event=>{event.preventDefault();void(async()=>{
  try{
    const settings={};
    for(const key of ["autoCapture","captureMedia","sendCookies"])settings[key]=document.getElementById(key).checked;
    settings.extensions=document.getElementById("extensions").value.split(/[,\s]+/).map(value=>value.toLowerCase().replace(/^\./,"")).filter(Boolean);
    if(settings.extensions.some(value=>!/^\w{1,16}$/.test(value)))throw new Error("Use file extensions such as zip, pdf, mp4.");
    for(const key of ["allowDomains","blockDomains"]){settings[key]=document.getElementById(key).value.split(/\r?\n/).map(value=>value.trim().toLowerCase()).filter(Boolean);if(settings[key].some(value=>!/^(\*\.)?[a-z0-9.-]+$/.test(value)))throw new Error("Use domain names without URLs or paths.");}
    await callApi(api.storage.local,"set",{fluxSettings:settings});document.getElementById("result").textContent="Saved browser preferences. Desktop filetype/domain rules take precedence while Flux is connected. Existing pages update within a few seconds.";
  }catch(error){document.getElementById("result").textContent=error.message;}
})();});
