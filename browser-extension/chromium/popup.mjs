import { api, runtimeRequest, queryCurrentTab, rememberStatus, readStatus } from "./browser-api.mjs";
import { normalizeUrls } from "./protocol.mjs";

const urls = document.querySelector("#urls");
const status = document.querySelector("#status");
const buttons = [...document.querySelectorAll("button")];
const extensionId = document.querySelector("#extension-id");
extensionId.value = api.runtime.id;
extensionId.addEventListener("click", () => extensionId.select());

function showStatus(message, ok) {
  status.textContent = message;
  status.className = "status" + (ok === undefined ? "" : ok ? " success" : " error");
}

function updateCount() {
  const count = urls.value.split(/\r?\n/).filter((line) => line.trim()).length;
  document.querySelector("#link-count").textContent = `${count} ${count === 1 ? "link" : "links"}`;
}

async function perform(operation) {
  buttons.forEach((button) => { button.disabled = true; });
  showStatus("Connecting to your desktop…");
  try {
    const response = await operation();
    showStatus(response.message, response.ok);
    await rememberStatus(response.ok, response.message);
  } catch (error) {
    showStatus(error.message, false);
    await rememberStatus(false, error.message);
  } finally {
    buttons.forEach((button) => { button.disabled = false; });
  }
}

urls.addEventListener("input", updateCount);
urls.addEventListener("keydown", (event) => {
  if ((event.ctrlKey || event.metaKey) && event.key === "Enter") document.querySelector("#send").click();
});
document.querySelector("#send").addEventListener("click", () => {
  void perform(() => runtimeRequest({ type: "send", urls: normalizeUrls(urls.value) }));
});
document.querySelector("#check").addEventListener("click", () => {
  void perform(() => runtimeRequest({ type: "status" }));
});
document.querySelector("#current-tab").addEventListener("click", () => {
  void (async () => {
    try {
      const tab = await queryCurrentTab();
      urls.value = normalizeUrls([tab?.url || ""]).join("\n");
      updateCount();
      urls.focus();
      showStatus("Current page URL added. Choose Download to send it.");
    } catch (error) { showStatus(error.message, false); }
  })();
});
void readStatus().then((last) => {
  if (last && typeof last.message === "string") showStatus(last.message, last.ok);
});
document.querySelector("#options").addEventListener("click",()=>api.runtime.openOptionsPage());
