import assert from "node:assert/strict";
import {nativeFixture} from "./native-fixture.mjs";
import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {spawn} from "node:child_process";
import {createServer} from "node:http";
import {once} from "node:events";

const exe = process.env.VIVALDI_EXE || path.join(process.env.LOCALAPPDATA,"Vivaldi","Application","vivaldi.exe");
const scratch = process.env.SYNVALDI_TEST_ROOT;
if (!scratch || !path.isAbsolute(scratch)) throw new Error("Set SYNVALDI_TEST_ROOT to an absolute test data directory.");
await fs.mkdir(scratch,{recursive:true});
const root = await fs.mkdtemp(path.join(scratch,"synvaldi-vivaldi-"));
const extension = path.join(root,"extension");
await fs.cp(fileURLToPath(new URL("../extension",import.meta.url)),extension,{recursive:true});
const bg = path.join(extension,"background.js");
const native = process.env.SYNVALDI_TEST_NATIVE === "1" ? await nativeFixture(root) : null;
// Avoid attaching test profiles to a production native host on the same PC.
await fs.writeFile(bg,(await fs.readFile(bg,"utf8")).replace('"com.synvaldi.bridge"',JSON.stringify(native?.host || "com.synvaldi.integration_unused")));
await fs.appendFile(bg,"\nglobalThis.testTypes = {Mirror,newState};\n");
const id = "jcfipkamaklpdghopicgfllhnigfjcio";
const origin = "chrome-extension://"+id+"/status.html";
const pause = ms => new Promise(r=>setTimeout(r,ms));

async function wait(fn, timeout=20000) {
  const end = Date.now()+timeout;
  let error;
  do { try {const found = await fn(); if (found) return found;} catch(e) {error=e;} await pause(120); } while(Date.now()<end);
  throw error || new Error("Timed out waiting for isolated test browser");
}

async function rpc(wsUrl) {
  const ws = new WebSocket(wsUrl);
  await once(ws,"open");
  let counter=0;
  const pending=new Map();
  ws.addEventListener("message",e=>{
    const m=JSON.parse(e.data);
    if (pending.has(m.id)) {const [resolve,reject]=pending.get(m.id);pending.delete(m.id);m.error?reject(Error(m.error.message)):resolve(m.result);}
  });
  return {close:()=>ws.close(), call:(method,params={})=>new Promise((resolve,reject)=>{
    const id=++counter;
    const timeout=setTimeout(()=>{pending.delete(id);reject(Error("timed out: "+method));},15000);
    pending.set(id,[result=>{clearTimeout(timeout);resolve(result);},error=>{clearTimeout(timeout);reject(error); }]);
    ws.send(JSON.stringify({id,method,params}));
  })};
}
const browsers=[];
async function launch(label) {
  const profile=path.join(root,label);
  await fs.mkdir(profile);
  const proc=spawn(exe,["--user-data-dir="+profile,"--load-extension="+extension,"--remote-debugging-port=0",
    "--no-first-run","--no-default-browser-check","--disable-sync","about:blank"], {windowsHide:true,stdio:"ignore"});
  const info={proc}; browsers.push(info);
  const port=await wait(async()=> (await fs.readFile(path.join(profile,"DevToolsActivePort"),"utf8")).split(/\r?\n/)[0]);
  const debug="http://127.0.0.1:"+port;
  const version=await (await fetch(debug+"/json/version")).json();
  info.owner=await rpc(version.webSocketDebuggerUrl);
  await wait(async() => (await (await fetch(debug+"/json/list")).json()).find(t=>t.type === "app" && t.url.endsWith("/window.html")));
  await pause(1800);
  console.log("Browser "+label+" is ready");
  const target=await wait(async()=>(await (await fetch(debug+"/json/list")).json()).find(t=>t.type==="service_worker" && t.url==="chrome-extension://"+id+"/background.js"));
  info.tab=await rpc(target.webSocketDebuggerUrl);
  info.eval=async expression=>{
    const result=await info.tab.call("Runtime.evaluate",{expression,awaitPromise:true,returnByValue:true});
    if(result.exceptionDetails)throw Error(result.exceptionDetails.exception?.description||result.exceptionDetails.text);
    return result.result.value;
  };

  console.log("Extension "+label+" opened");
  await wait(()=>info.eval("chrome.runtime?.id === '"+id+"' && !!chrome.tabs?.query"));
  await info.eval(`(async()=>{
    const {Mirror,newState}=globalThis.testTypes; globalThis.state=newState();
    globalThis.mirror=new Mirror(chrome,state,async()=>{});
    mirror.config={enabled:true,capture:true,selection:"all"};
    const win=(await chrome.windows.getAll({windowTypes:["normal"]}))[0];
    globalThis.control=await chrome.tabs.create({windowId:win.id,url:"about:blank",active:true});
    return true;
  })()`);
  return info;
}

const http=createServer((req,res)=>{res.setHeader("content-type","text/html");res.end("<title>fixture</title>");});
http.listen(0,"127.0.0.1"); await once(http,"listening");
const base="http://127.0.0.1:"+http.address().port;
async function apply(receiver,changes,replace=false) {
  const message={batchId:crypto.randomUUID(),changes,replace};
  await receiver.eval("mirror.apply("+JSON.stringify(message)+")");
  await receiver.eval("mirror.apply("+JSON.stringify(message)+")"); // replay is idempotent
}
try {
  const a=await launch("A");
  const b=await launch("B");
  await a.eval(`(async()=>{await chrome.tabs.create({windowId:control.windowId,url:${JSON.stringify(base+"/a")},active:false});
    await chrome.tabs.create({windowId:control.windowId,url:${JSON.stringify(base+"/b")},active:false,pinned:true});
    return true;})()`);
  console.log("Source fixture tabs created");
  if(native) {
    await wait(()=>native.clients.length===2);
    const [src,dst]=native.clients;
    const changes=(await native.request(src,{type:"snapshot"})).changes;
    await native.request(dst,{type:"snapshot"});
    await native.request(dst,{type:"apply",replace:true,changes});
    native.relay();
    for(const c of native.clients){c.send({type:"lease",onlineLease:native.epoch});c.send({type:"config",enabled:true,selection:"all",epoch:native.epoch,capture:true});}
    await pause(400);
    const focusBefore=await b.eval("chrome.windows.getAll({windowTypes:['normal']}).then(ws=>ws.filter(w=>w.focused).map(w=>w.id))");
    const changedUrl=base+"/native?exact=1#f";
    await a.eval("chrome.tabs.query({}).then(ts=>chrome.tabs.update(ts.find(t=>(t.pendingUrl||t.url).endsWith('/a')).id,{url:"+JSON.stringify(changedUrl)+"}))");
    await wait(()=>b.eval("chrome.tabs.query({}).then(ts=>ts.some(t=>(t.pendingUrl||t.url)==="+JSON.stringify(changedUrl)+"))"));
    await a.eval("chrome.tabs.query({}).then(ts=>chrome.tabs.remove(ts.find(t=>(t.pendingUrl||t.url)==="+JSON.stringify(changedUrl)+").id))");
    await wait(()=>b.eval("chrome.tabs.query({}).then(ts=>!ts.some(t=>(t.pendingUrl||t.url)==="+JSON.stringify(changedUrl)+"))"));
    await pause(500);
    assert.equal(dst.messages.filter(m=>m.type==="changes").length,0);
    assert.deepEqual(await b.eval("chrome.windows.getAll({windowTypes:['normal']}).then(ws=>ws.filter(w=>w.focused).map(w=>w.id))"),focusBefore);
    assert.ok(src.messages.some(m=>m.type==="changes"&&m.onlineLease===native.epoch));
    console.log("PASS: event-driven updates/close through real Vivaldi → native host → named pipe → peer, focus and no echo.");
  } else {
  const snap=await a.eval("mirror.snapshot()");
  assert.equal(snap.length,2);
  const before=await b.eval("chrome.tabs.get(control.id).then(t=>t.active)");
  await apply(b,snap,true);
  assert.equal(await b.eval("chrome.tabs.get(control.id).then(t=>t.active)"),before);
  assert.deepEqual((await b.eval("mirror.snapshot()")).sort((x,y)=>x.id.localeCompare(y.id)),snap.sort((x,y)=>x.id.localeCompare(y.id)));
  await b.eval("mirror.capture()");
  assert.equal(await b.eval("state.outbox.length"),0);
  const target=snap.find(c=>c.value.url.endsWith("/a"));
  const newUrl=base+"/changed?q=9#here";
  await a.eval("mirror.capture().then(()=>{state.outbox=[];})");
  await a.eval(`(async()=>{const {local}=await mirror.scan(); await chrome.tabs.update(local[${JSON.stringify(target.id)}].id,{url:${JSON.stringify(newUrl)}});return true})()`);
  await wait(()=>a.eval("mirror.snapshot().then(rs=>rs.some(c=>c.value.url==="+JSON.stringify(newUrl)+"))"));
  await a.eval("mirror.capture()");
  const changes=await a.eval("state.outbox.flatMap(b=>b.changes)");
  assert.equal(changes.length,1);
  await apply(b,changes);
  assert.equal((await b.eval("mirror.snapshot()")).find(c=>c.id===target.id).value.url,newUrl);
  // A second source window must remain distinct on the receiver.
  await a.eval("chrome.windows.create({url:"+JSON.stringify(base+"/window-two")+",focused:false,state:'minimized'})");
  await a.eval("state.outbox=[]; mirror.capture()");
  const second=await a.eval("state.outbox.flatMap(b=>b.changes)");
  await apply(b,second);
  assert.equal(new Set((await b.eval("mirror.snapshot()")).map(c=>c.value.windowId)).size,2);
  await a.eval(`(async()=>{const {local}=await mirror.scan();await chrome.tabs.remove(local[${JSON.stringify(target.id)}].id);state.outbox=[];await mirror.capture();})()`);
  await apply(b,await a.eval("state.outbox.flatMap(b=>b.changes)"));
  assert.ok(!(await b.eval("mirror.snapshot()")).some(c=>c.id===target.id));
  await b.eval("mirror.capture()");
  assert.equal(await b.eval("state.outbox.length"),0);
  console.log("PASS: two isolated Vivaldi profiles, snapshot, exact URLs, pin/order, two windows, local active tab, update, close, replay and echo suppression.");
  }
} finally {
  for (const b of browsers.reverse()) {
    b.tab?.close();
    try {await b.owner?.call("Browser.close");} catch {}
    b.owner?.close();
    if (b.proc.exitCode===null) await Promise.race([once(b.proc,"exit"),pause(2500)]);
    if (b.proc.exitCode===null) b.proc.kill();
  }
  http.close();
  native?.close();
}






