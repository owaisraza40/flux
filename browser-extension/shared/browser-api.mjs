import { HOST_NAME, validateResponse, explainNativeError } from "./protocol.mjs";

export const api = globalThis.browser || globalThis.chrome;
const firefox = typeof globalThis.browser !== "undefined";

export function callApi(owner, method, ...args) {
  if (firefox) return Promise.resolve(owner[method](...args));
  return new Promise((resolve,reject) => owner[method](...args, result => {
    const error = api.runtime.lastError;
    if(error) reject(new Error(error.message)); else resolve(result);
  }));
}

export const runtimeRequest = message => callApi(api.runtime, "sendMessage", message);

export async function nativeRequest(request) {
  try {
    const response = firefox
      ? await api.runtime.sendNativeMessage(HOST_NAME, request)
      : await new Promise((resolve, reject) => api.runtime.sendNativeMessage(HOST_NAME, request, (reply) => {
          const error = api.runtime.lastError;
          if (error) reject(new Error(error.message)); else resolve(reply);
        }));
    return validateResponse(response);
  } catch (error) {
    throw new Error(explainNativeError(error));
  }
}

export async function queryCurrentTab() {
  if (firefox) return (await api.tabs.query({ active: true, currentWindow: true }))[0];
  return await new Promise((resolve, reject) => api.tabs.query({ active: true, currentWindow: true }, (tabs) => {
    const error = api.runtime.lastError;
    if (error) reject(new Error(error.message)); else resolve(tabs[0]);
  }));
}

export async function rememberStatus(ok, message) {
  const value = { lastResult: { ok, message, time: Date.now() } };
  if (firefox) return await api.storage.local.set(value);
  return await new Promise((resolve) => api.storage.local.set(value, () => { void api.runtime.lastError; resolve(); }));
}

export async function readStatus() {
  if (firefox) return (await api.storage.local.get("lastResult")).lastResult;
  return await new Promise((resolve) => api.storage.local.get("lastResult", (value) => {
    void api.runtime.lastError; resolve(value?.lastResult);
  }));
}
