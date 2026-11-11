// Wire records contain only the supported browser state. Numeric browser IDs are local.
export function eligibleUrl(value) {
  try {
    if (typeof value !== "string" || value.length > 32768) return false;
    const url = new URL(value);
    return ["http:", "https:"].includes(url.protocol) && !url.username && !url.password;
  } catch {
    return false;
  }
}

export function uuid(value) {
  return typeof value === "string" && /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i.test(value);
}

function validTab(change) {
  const v = change.value;
  return v && uuid(v.windowId) && eligibleUrl(v.url) && typeof v.pinned === "boolean" &&
    Number.isSafeInteger(v.index) && v.index >= 0 && v.index < 100000;
}

export function validateBatch(changes) {
  if (!Array.isArray(changes) || changes.length > 10000) throw new Error("INVALID_BATCH");
  const ids = new Set();
  for (const item of changes) {
    if (!item || !uuid(item.id) || ids.has(item.id) || item.kind !== "tab" ||
        (item.deleted !== true && !validTab(item))) throw new Error("INVALID_TAB_RECORD");
    ids.add(item.id);
  }
}

export function changesBetween(before, after) {
  const result = [];
  for (const [id, value] of Object.entries(after)) {
    const old = before[id];
    if (!old || old.url !== value.url || old.index !== value.index ||
        old.pinned !== value.pinned || old.windowId !== value.windowId) {
      result.push({id, kind: "tab", value});
    }
  }
  for (const id of Object.keys(before)) {
    if (!after[id]) result.push({id, kind: "tab", deleted: true});
  }
  return result;
}

export function windowUuid(window) {
  try {
    const value = JSON.parse(window.vivExtData).ext_id;
    return uuid(value) ? value : null;
  } catch {
    return null;
  }
}

export const capabilities = Object.freeze({
  tabs: true, focus: true, multipleWindows: true, workspaces: false, stacks: false, tiling: false,
  settings: false, notes: false, assets: false
});

export function assertActive(config) {
  if (!config?.enabled) throw new Error("PROFILE_DISABLED");
  if (config.selection !== "all") throw new Error("LIVE_WORKSPACE_API_UNAVAILABLE");
}

export function newState() {
  return {profileId: crypto.randomUUID(), tabs: {}, windows: {}, baseline: {}, outbox: [], applied: []};
}

export class Mirror {
  constructor(browser, state, save) {
    this.browser = browser;
    this.s = state;
    this.save = save;
    this.config = null;
  }

  async scan(suppliedWindows) {
    const windows = suppliedWindows ?? await this.browser.windows.getAll({populate: true, windowTypes: ["normal"]});
    const records = {};
    const slots = new Map();
    for (const [id,value] of Object.entries(this.s.baseline)) {
      const key=JSON.stringify([value.windowId,value.index,value.url]);
      slots.set(key, slots.has(key) ? null : id);
    }
    this.s.windowTokens ??= {};
    const local = {};
    const winMap = {};
    const newTabs = {};
    const newWindows = {};
    for (const win of windows) {
      if (win.incognito || win.type !== "normal") continue;
      const token = windowUuid(win);
      const previous = this.s.windows[win.id];
      const match = token && Object.values(this.s.windows).find(item => item.token === token);
      const windowId = (token && this.s.windowTokens[token]) || match?.id || (previous && (!token || !previous.token) ? previous.id : null) || crypto.randomUUID();
      if (token) this.s.windowTokens[token] = windowId;
      newWindows[win.id] = {id: windowId, token};
      winMap[windowId] = win.id;
      const tabs = (win.tabs || []).filter(tab => !tab.incognito && eligibleUrl(tab.pendingUrl || tab.url));
      const reserved = new Set(tabs.map(tab => this.s.tabs[tab.id]).filter(Boolean));
      for (let index = 0; index < tabs.length; index++) {
        const tab = tabs[index];
        const url = tab.pendingUrl || tab.url;
        let id = this.s.tabs[tab.id];
        if (!id) {
          // Session restore allocates fresh browser IDs. Exact slot+URL within a
          // persistent window recovers duplicates independently without guessing.
          const candidate = slots.get(JSON.stringify([windowId,index,url]));
          id = candidate && !reserved.has(candidate) ? candidate : crypto.randomUUID();
        }
        reserved.add(id);
        newTabs[tab.id] = id;
        records[id] = {url, pinned: !!tab.pinned, windowId, index};
        local[id] = tab;
      }
    }
    this.s.tabs = newTabs;
    this.s.windows = newWindows;
    return {records, local, windows: winMap};
  }

  async capture() {
    assertActive(this.config);
    if (this.config.capture === false || this.s.inflight) return;
    const windows = await this.browser.windows.getAll({populate:true,windowTypes:["normal"]});
    // During startup/shutdown a normal window can exist before any page is ready.
    if (!windows.some(w=>!w.incognito && w.tabs?.some(t=>t.pendingUrl || t.url))) return;
    const {records} = await this.scan(windows);
    const changes = changesBetween(this.s.baseline, records);
    if (changes.length) {
      // Native frames have bounded size; queue separate retryable chunks.
      for (let i = 0; i < changes.length;) {
        let length = 0;
        const chunk = [];
        do {
          const item = changes[i++];
          chunk.push(item);
          length += JSON.stringify(item).length * 4;
        } while (i < changes.length && length < 350000);
        this.s.outbox.push({type: "changes", batchId: crypto.randomUUID(), onlineLease: this.onlineLease ?? null, changes: chunk});
      }
    }
    this.s.baseline = records;
    await this.save();
  }

  async snapshot() {
    assertActive(this.config);
    const {records} = await this.scan();
    await this.save();
    return Object.entries(records).map(([id, value]) => ({id, kind: "tab", value}));
  }

  async apply(message) {
    assertActive(this.config);
    if (!uuid(message.batchId)) throw new Error("INVALID_BATCH_ID");
    validateBatch(message.changes);
    if (this.s.applied.includes(message.batchId)) return;
    // Capture unrelated local edits before suppression of command-generated events.
    if (this.config.capture !== false) await this.capture();
    this.s.inflight = message.batchId;
    await this.save();
    const current = await this.scan();
    const additions = message.changes.filter(c => !c.deleted).sort((a,b) =>
      a.value.windowId.localeCompare(b.value.windowId) || a.value.index - b.value.index);
    const removals = new Set(message.changes.filter(c => c.deleted).map(c => c.id));
    if (message.replace === true) {
      const retain = new Set(additions.map(c => c.id));
      for (const id of Object.keys(current.local)) if (!retain.has(id)) removals.add(id);
    }

    // Save mapping immediately after each create so a rejected later operation is retryable.
    for (const {id, value} of additions) {
      let destination = current.windows[value.windowId];
      let createdTab, placeholder;
      if (destination === undefined) {
        const moving = !!current.local[id];
        const win = await this.browser.windows.create({url: moving ? "about:blank" : value.url, focused: false, state: "minimized", type: "normal"});
        if (win.incognito || !win.tabs?.length) throw new Error("WINDOW_CREATE_FAILED");
        destination = win.id;
        const token = windowUuid(win);
        this.s.windows[win.id] = {id: value.windowId, token};
        if (token) this.s.windowTokens[token] = value.windowId;
        current.windows[value.windowId] = win.id;
        if (moving) placeholder = win.tabs[0];
        else createdTab = win.tabs[0];
      }
      let tab = current.local[id] || createdTab;
      if (!tab) tab = await this.browser.tabs.create({windowId: destination, url: value.url, active: false, pinned: value.pinned});
      if (this.s.tabs[tab.id] !== id) {
        this.s.tabs[tab.id] = id;
        await this.save();
      }
      if (tab.windowId !== destination) {
        await this.keepWindow(tab.windowId);
        await this.browser.tabs.move(tab.id, {windowId: destination, index: -1});
        if (placeholder) await this.browser.tabs.remove(placeholder.id);
      }
      const update = {};
      if ((tab.pendingUrl || tab.url) !== value.url) update.url = value.url;
      if (!!tab.pinned !== value.pinned) update.pinned = value.pinned;
      if (Object.keys(update).length) await this.browser.tabs.update(tab.id, update);
      current.local[id] = await this.browser.tabs.get(tab.id);
    }

    for (const id of removals) {
      const tab = current.local[id];
      if (tab) {
        // Closing the last tab would close the window. Keep a local start page
        // so Windows cannot transfer foreground to a different application.
        await this.keepWindow(tab.windowId);
        await this.browser.tabs.remove(tab.id);
        delete this.s.tabs[tab.id];
      }
    }

    for (const {id, value} of additions) {
      const tab = current.local[id];
      const peers = (await this.browser.tabs.query({windowId: tab.windowId})).filter(t => eligibleUrl(t.pendingUrl || t.url));
      const target = peers[Math.min(value.index, peers.length - 1)];
      if (target && target.id !== tab.id) await this.browser.tabs.move(tab.id, {index: target.index});
    }
    const updated = await this.scan();
    for (const {id, value} of additions) {
      const actual = updated.records[id];
      if (!actual || actual.url !== value.url || actual.pinned !== value.pinned || actual.windowId !== value.windowId ||
          actual.index !== value.index) throw new Error("BROWSER_STATE_MISMATCH");
    }
    if ([...removals].some(id => updated.records[id])) throw new Error("REMOVE_UNCONFIRMED");
    this.s.inflight = null;
    if (message.replace) this.s.baseline = updated.records;
    else {
      // Edits made in another tab while browser calls were pending must still
      // become outgoing events. Suppress only changes this command actually applied.
      this.s.baseline = {...current.records};
      for (const {id} of additions) this.s.baseline[id] = updated.records[id];
      for (const id of removals) delete this.s.baseline[id];
    }
    this.s.applied = [...this.s.applied.slice(-127), message.batchId];
    await this.save();
  }

  async keepWindow(windowId) {
    if ((await this.browser.tabs.query({windowId})).length === 1)
      await this.browser.tabs.create({windowId,active:false,url:"about:blank"});
  }

  async focus(message) {
    assertActive(this.config);
    const {local} = await this.scan();
    const tab = local[message.tabId];
    if (!tab || this.s.windows[tab.windowId]?.id !== message.windowId) throw new Error("TAB_NOT_FOUND");
    await this.browser.windows.update(tab.windowId, {focused: true});
    await this.browser.tabs.update(tab.id, {active: true});
  }
}





