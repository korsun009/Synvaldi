import {Mirror, capabilities, newState, windowUuid} from "./model.js";

const host = "com.synvaldi.bridge";
let port, model, lastSent, timer, focusTimer, admitted = false, serial = Promise.resolve();
const initialized = (async () => {
  const {state} = await chrome.storage.local.get("state");
  model = new Mirror(chrome, state || newState(), () => chrome.storage.local.set({state: model.s}));
  if (!state) await model.save();
})();

function enqueue(fn) {
  const result = serial.then(() => initialized).then(fn);
  serial = result.catch(() => {});
  return result;
}

function post(message) {
  if (port) port.postMessage(message);
}

function flush() {
  const batch = model.s.outbox[0];
  if (!admitted || !port || !model.config?.enabled || model.config.capture === false || !batch || lastSent === batch.batchId) return;
  post({...batch, epoch: model.config.epoch});
  lastSent = batch.batchId;
}

async function publishFocus() {
  if (!admitted || !port || !model.config?.enabled || !model.config.capture || model.config.selection !== "all") return;
  const win = await chrome.windows.getLastFocused({windowTypes:["normal"]});
  if (!win?.focused || win.incognito) return;
  const [tab] = await chrome.tabs.query({windowId:win.id, active:true});
  const tabId = model.s.tabs[tab?.id];
  const windowId = model.s.windows[win.id]?.id;
  if (tabId && windowId) post({type:"focus",windowId,tabId,workspaceId:null,epoch:model.config.epoch});
}

async function receive(message, sender) {
  if (sender !== port) return;
  switch (message.type) {
    case "config": {
      const epochChanged = model.s.epoch !== message.epoch;
      if (epochChanged) {
        model.s.epoch = message.epoch;
        model.s.outbox = [];
        model.s.applied = [];
        model.s.inflight = null;
        lastSent = null;
      }
      admitted = true;
      model.config = {...message, capture: message.capture !== false};
      if (message.enabled && message.selection !== "all")
        post({type:"capabilityError", error:"LIVE_WORKSPACE_API_UNAVAILABLE"});
      if (message.enabled && message.selection === "all") {
        if (message.capture !== false) await model.capture();
        else await model.scan();
      }
      await model.save();
      flush();
      return;
    }
    case "lease":
      model.onlineLease = message.onlineLease || null;
      return;
    case "received":
      if (model.s.outbox[0]?.batchId === message.batchId) {
        model.s.outbox.shift();
        lastSent = null;
        await model.save();
        flush();
      }
      return;
    case "snapshot": {
      const changes = await model.snapshot();
      const result = {type:"snapshot",batchId:message.batchId,ok:true,changes};
      if (new TextEncoder().encode(JSON.stringify(result)).length > 950000) throw new Error("SNAPSHOT_EXCEEDS_FRAME");
      if (model.config?.capture === false) {
        model.s.baseline = Object.fromEntries(changes.map(c => [c.id,c.value]));
        await model.save();
      }
      post(result);
      return;
    }
    case "apply":
      await model.apply(message);
      break;
    case "focus":
      await model.focus(message);
      break;
    default:
      return;
  }
  post({type:"ack",batchId:message.batchId,ok:true});
}

function connect() {
  if (port) return;
  const current = chrome.runtime.connectNative(host);
  port = current;
  admitted = false;
  lastSent = null;
  current.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    if (port === current) {
      port = null;
      if (model) model.onlineLease = null;
      // Keep capturing previously admitted local state offline. A fresh host must
      // explicitly rebind the matching epoch before the outbox can be sent.
      chrome.alarms.create("reconnect", {delayInMinutes:0.5});
    }
  });
  current.onMessage.addListener(message => {
    enqueue(() => receive(message, current)).catch(error => {
      if (current === port && message.batchId)
        post({type:"ack",batchId:message.batchId,ok:false,error:error.message});
    });
  });
  enqueue(async () => {
    const windows = await chrome.windows.getAll({windowTypes:["normal"]});
    const windowProof = windows.filter(w=>!w.incognito && windowUuid(w)).map(w=>({id:w.id,ext_id:windowUuid(w)}));
    if (port === current) post({type:"hello",protocol:1,profileId:model.s.profileId,capabilities,windowProof});
  });
}

function changed() {
  clearTimeout(timer);
  timer = setTimeout(() => enqueue(async () => {
    if (!model.config?.enabled || !model.config.capture || model.config.selection !== "all") return;
    await model.capture();
    flush();
  }).catch(() => {}), 180);
}

function focused() {
  clearTimeout(focusTimer);
  focusTimer = setTimeout(() => enqueue(publishFocus).catch(() => {}), 350);
}

chrome.tabs.onUpdated.addListener((_id,change) => {
  if (change.url !== undefined || change.pinned !== undefined || change.status === "complete") changed();
});
for (const event of [chrome.tabs.onCreated,chrome.tabs.onRemoved,chrome.tabs.onMoved,
  chrome.tabs.onAttached,chrome.tabs.onDetached,chrome.tabs.onReplaced,chrome.windows.onRemoved]) event.addListener(changed);
chrome.tabs.onActivated.addListener(focused);
chrome.windows.onFocusChanged.addListener(focused);
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === "reconnect") connect(); });
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id || message.type !== "status") return;
  enqueue(() => respond({connected: !!port, enabled: !!model.config?.enabled,
    mode: model.config?.selection === "all" ? "Обмен обычными вкладками включён." : "Выбор рабочих пространств недоступен в этой версии Vivaldi."}));
  return true;
});



