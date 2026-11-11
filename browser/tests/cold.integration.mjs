import assert from "node:assert/strict";
import path from "node:path";
import {fileURLToPath} from "node:url";
import fs from "node:fs/promises";
import {spawn,execFileSync} from "node:child_process";
import {once} from "node:events";
const scratch=process.env.SYNVALDI_TEST_ROOT;
if(!scratch || !path.isAbsolute(scratch))throw Error("Set SYNVALDI_TEST_ROOT to an absolute scratch directory");
const project=fileURLToPath(new URL("./cold/ColdTests.csproj",import.meta.url));
const out=execFileSync("dotnet",["run","--project",project,"--",scratch],{encoding:"utf8"});
const root=out.trim().split(/\r?\n/).at(-1);
if(path.dirname(root).toLowerCase()!==path.resolve(scratch).toLowerCase() || !/^synvaldi-cold-[a-f0-9]{32}$/.test(path.basename(root)))
  throw Error("Unexpected fixture identity");
const expected=JSON.parse(await fs.readFile(path.join(root,"expected.json"),"utf8"));
const want=expected["workspace-cold/12.5"].tabs;
const ext=path.join(root,"extension");
await fs.cp(fileURLToPath(new URL("../extension",import.meta.url)),ext,{recursive:true});
const bg=path.join(ext,"background.js");
await fs.writeFile(bg,(await fs.readFile(bg,"utf8")).replace('"com.synvaldi.bridge"','"com.synvaldi.integration_unused"'));
const exe=process.env.VIVALDI_EXE||path.join(process.env.LOCALAPPDATA,"Vivaldi","Application","vivaldi.exe");
const proc=spawn(exe,["--user-data-dir="+root,"--load-extension="+ext,"--remote-debugging-port=0","--no-first-run",
  "--no-default-browser-check","--disable-sync","--restore-last-session"],{windowsHide:true,stdio:"ignore"});
const pause=ms=>new Promise(r=>setTimeout(r,ms));
async function wait(fn,ms=45000){let last;for(const until=Date.now()+ms;Date.now()<until;){try{const v=await fn();if(v)return v;}catch(e){last=e;}await pause(350);}throw last||Error("Timed out waiting for restored test browser");}
async function call(url,method,params={}){
  const ws=new WebSocket(url);await once(ws,"open");
  try{return await new Promise((r,j)=>{const timer=setTimeout(()=>j(Error(method+" timeout")),10000);
    ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id!==1)return;clearTimeout(timer);m.error?j(Error(m.error.message)):r(m.result);};
    ws.send(JSON.stringify({id:1,method,params}));});}finally{ws.close();}
}
let owner;
try{
  const port=await wait(async()=>(await fs.readFile(path.join(root,"DevToolsActivePort"),"utf8")).split(/\r?\n/)[0]);
  const debug="http://127.0.0.1:"+port;
  owner=(await(await fetch(debug+"/json/version")).json()).webSocketDebuggerUrl;
  const worker=await wait(async()=>(await(await fetch(debug+"/json/list")).json()).find(t=>t.type==="service_worker"&&t.url.includes("jcfipkamaklpdghopicgfllhnigfjcio")));
  let last=[];
  try {
    await wait(async()=>{
      const r=await call(worker.webSocketDebuggerUrl,"Runtime.evaluate",{expression:"chrome.tabs.query({}).then(ts=>ts.map(t=>({url:t.pendingUrl||t.url,pinned:t.pinned})))",awaitPromise:true,returnByValue:true});
      last=r.result.value;
      return want.every(item=>last.some(t=>t.url===item.url&&t.pinned===item.pinned))&&last.some(t=>t.url?.includes("/untouched0"));
    },30000);
  } catch(e) {console.error("Restored fixture tabs",JSON.stringify(last));throw e;}
  const layout=expected["workspace-cold/12.5"].layouts[0];
  const readTree=await call(worker.webSocketDebuggerUrl,"Runtime.evaluate",{expression:"chrome.windows.getAll().then(ws=>ws.map(w=>JSON.parse(w.vivExtData||'{}')).find(w=>w.ext_id==="+JSON.stringify(layout.windowId)+")?.tiles?.["+JSON.stringify(layout.id)+"])",awaitPromise:true,returnByValue:true});
  assert.deepEqual(readTree.result.value,layout.tree);
  console.log("PASS: normal Vivaldi windows API reported the imported 37/63 split tree.");
  for(let attempt=0;attempt<2;attempt++){
    if(attempt)await pause(2500);
    const share=execFileSync("dotnet",["run","--no-build","--project",project,"--","--probe-sharing",root],{encoding:"utf8"}).trim();
    console.log(share);
    assert.match(share.split(/\r?\n/)[0],/SHARING_VIOLATION/);
  }
  console.log("PASS: Vivaldi reopened two reconstructed workspace tabs and preserved the unselected third tab and pins.");
}finally{
  if(owner)try{await call(owner,"Browser.close");}catch{}
  if(proc.exitCode===null)await Promise.race([once(proc,"exit"),pause(4000)]);
  if(proc.exitCode===null)proc.kill();
}
const verify=execFileSync("dotnet",["run","--project",project,"--","--verify-fixture",root],{encoding:"utf8"});
assert.match(verify,/PASS/);console.log(verify.trim());



