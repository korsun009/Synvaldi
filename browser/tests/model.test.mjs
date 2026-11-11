import test from "node:test";
import assert from "node:assert/strict";
import {eligibleUrl,changesBetween,validateBatch,assertActive,windowUuid,Mirror,newState} from "../extension/model.js";

const id = "0ab93334-c0fe-4c59-8ed6-e66247bdcb5a";
const win = "e5c5917e-8e47-4f02-9f76-6c488dfd9611";
const tab = {url:"https://example.org/path?a=1#fragment",windowId:win,index:0,pinned:false};
test("only transferable web URLs, excluding authority credentials",() => {
  assert.equal(eligibleUrl(tab.url), true);
  for (const url of ["https://user:password@example.org","file:///c:/a","vivaldi://settings","javascript:1","chrome-extension://id/index","about:blank",null])
    assert.equal(eligibleUrl(url),false);
});
test("remote batch validation runs before browser operations",() => {
  validateBatch([{id,kind:"tab",value:tab}]);
  for (const value of [{...tab,url:"file:///confidential"},{...tab,index:-1},{...tab,windowId:2}])
    assert.throws(()=>validateBatch([{id,kind:"tab",value}]),/INVALID/);
  assert.throws(()=>validateBatch([{id,kind:"tab",value:tab},{id,kind:"tab",deleted:true}]),/INVALID/);
});
test("local deletion is explicit; background title and load events produce no delta",() => {
  assert.deepEqual(changesBetween({[id]:tab},{[id]:{...tab}}),[]);
  assert.deepEqual(changesBetween({[id]:tab},{}),[{id,kind:"tab",deleted:true}]);
});
test("unknown and unselected workspace scopes fail closed",() => {
  assert.throws(()=>assertActive({enabled:true,selection:"workspaces",workspaces:["1"]}),/UNAVAILABLE/);
  assert.throws(()=>assertActive({enabled:false,selection:"all"}),/DISABLED/);
});
test("Vivaldi native window identity accepts only a UUID",() => {
  assert.equal(windowUuid({vivExtData:JSON.stringify({ext_id:win})}),win);
  assert.equal(windowUuid({vivExtData:"broken"}),null);
});
test("restored numeric tab IDs rebind by stable window, exact URL and position",async () => {
  const state = newState();
  state.windows[10] = {id:win,token:win};
  state.tabs[20] = id;
  state.baseline[id] = tab;
  const windows = [{id:99,type:"normal",vivExtData:JSON.stringify({ext_id:win}),tabs:[{id:1001,url:tab.url},{id:1002,url:"chrome://settings"}]}];
  const browser = {windows:{getAll:async()=>windows}};
  const mirror = new Mirror(browser,state,async()=>{});
  assert.deepEqual((await mirror.scan()).records,{[id]:tab});
  assert.equal(state.tabs[1001],id);
});
test("events during browser shutdown do not generate bulk deletion",async () => {
  const s = newState();
  s.baseline[id] = tab;
  const m = new Mirror({windows:{getAll:async()=>[]}},s,async()=>{});
  m.config={enabled:true,selection:"all",capture:true};
  await m.capture();
  assert.deepEqual(s.outbox,[]);
  assert.deepEqual(s.baseline,{[id]:tab});
});
