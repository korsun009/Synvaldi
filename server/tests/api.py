"""HTTP acceptance suite. Point SYNVALDI_TEST_URL at a disposable server with registration on."""
import concurrent.futures
import hashlib
import json
import os
import secrets
import threading
import unittest
import urllib.error
import urllib.request
import uuid


BASE = os.environ.get("SYNVALDI_TEST_URL", "http://127.0.0.1:54184").rstrip("/") + "/api/v1"


def guid():
    return str(uuid.uuid4())


def request(method, path, body=None, token=None, status=200, binary=False):
    data = body if isinstance(body, bytes) else (json.dumps(body).encode() if body is not None else None)
    headers = {"Content-Type": "application/octet-stream" if isinstance(body, bytes) else "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    try:
        response = urllib.request.urlopen(req, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read()
        if response.status != status:
            # Error text can be useful, but successful auth results contain private tokens.
            raise AssertionError(f"{method} {path} returned {response.status}, expected {status}")
        if binary:
            return payload
        return json.loads(payload) if payload else None


class Sync(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.login = {"username": "тест-" + guid(), "password": secrets.token_hex(18), "deviceName": "ПК №1 🐺"}
        cls.a = request("POST", "/auth/register", cls.login)
        cls.b = request("POST", "/auth/login", {**cls.login, "deviceName": "Второй ПК"})
        cls.other = request("POST", "/auth/register", {**cls.login, "username": guid()})
        cls.ta = cls.a["token"]
        cls.tb = cls.b["token"]
        cls.to = cls.other["token"]

    def profile(self):
        name = "Профиль " + guid()
        p = request("POST", "/profiles", {"name": name}, self.ta)
        self.assertEqual(p, request("POST", "/profiles", {"name": name}, self.ta))
        return "/profiles/" + p["id"]

    def seed(self, path):
        return request("POST", path + "/seed", {"operationId": guid(), "fields": {"ui/color": "red"}}, self.ta)

    def test_01_access_boundaries_and_admission(self):
        p = self.profile()
        request("GET", p + "/state", status=401)
        request("GET", p + "/state", token=self.to, status=404)
        request("PATCH", "/devices/" + self.a["deviceId"], {"name":"X"}, self.to, status=404)
        body = {"operationId":guid(), "fields":{"ui/color":"red"}}
        self.assertEqual(request("POST", p+"/seed", body, self.tb, status=409)["error"], "seed_reserved")
        self.assertEqual(request("POST", p+"/seed", body, self.ta), request("POST", p+"/seed", body, self.ta))
        request("POST", p+"/seed", {**body, "operationId":guid()}, self.ta, status=409)
        request("POST", p+"/admit", {"revision":0}, self.tb, status=409)
        batch = {"operationId":guid(), "offline":False, "changes":[{"key":"k","value":2,"baseRevision":0}]}
        request("POST", p+"/changes", batch, self.tb, status=409)
        request("POST", p+"/admit", {"revision":1}, self.tb, status=409)
        request("GET", p+"/state", token=self.tb)
        request("POST", p+"/admit", {"revision":1}, self.tb, status=204)
        self.assertEqual(request("POST", p+"/changes", batch, self.tb)["revision"], 2)
        request("PUT", p+"/focus", {"windowId":"w2","workspaceId":"пространство","tabId":"t3"}, self.tb, status=204)
        focused = request("GET", p+"/focus?deviceId="+self.b["deviceId"], token=self.ta)
        self.assertEqual(focused["tabId"], "t3")
        self.assertIsNone(request("GET", p+"/focus", token=self.ta)["windowId"])
        request("PATCH", p, {"name":"Изменён "+guid()}, self.ta, status=204)

    def test_02_partial_stale_merge_replay_and_rollback(self):
        p = self.profile()
        self.seed(p)
        change = lambda key,value,base: {"key":key, "value":value, "baseRevision":base}
        def submit(changes, offline, operation=None):
            return request("POST", p+"/changes", {"operationId":operation or guid(), "offline":offline,"changes":changes},self.ta)
        new = submit([change("ui/color","blue",1)],False)
        self.assertEqual(new["revision"],2)
        op = guid()
        edits = [change("ui/color","green",1), change("ui/layout",{"slots":[1,2]},0)]
        merged = submit(edits, True, op)
        self.assertEqual(merged, {"revision":3,"applied":["ui/layout"],"conflicts":["ui/color"]})
        self.assertEqual(submit(edits, True, op), merged)
        request("POST", p+"/changes", {"operationId":op,"offline":False,"changes":edits},self.ta,status=409)
        self.assertEqual(submit([{**change("ui/color",None,2), "deleted":True}],True)["revision"],4)
        self.assertEqual(submit([change("ui/color","red",2)],True)["conflicts"],["ui/color"])
        rolled = request("POST", p+"/rollback", {"operationId":guid(),"revision":1},self.ta)
        self.assertEqual(rolled["revision"],5)
        fields = request("GET", p+"/state", token=self.ta)["fields"]
        self.assertEqual(fields["ui/color"],{"value":"red","revision":5,"deleted":False,"guardRevision":5})
        self.assertTrue(fields["ui/layout"]["deleted"])
        self.assertEqual([r["revision"] for r in request("GET",p+"/history",token=self.ta)],[5,4,3,2,1])
        second=request("GET",p+"/history/2",token=self.ta)["fields"]
        self.assertEqual(second["ui/color"]["value"],"blue")
        self.assertNotIn("ui/layout",second)
        self.assertTrue(request("GET",p+"/history/4",token=self.ta)["fields"]["ui/color"]["deleted"])
        if os.environ.get("SYNVALDI_TEST_CHECKPOINT") == "1":
            previous = 0
            for revision in range(6, 132):
                self.assertEqual(submit([change("counter",revision,previous)],True)["revision"],revision)
                previous = revision
            for revision in (6, 63, 127, 128, 129, 131):
                historic = request("GET",p+"/history/"+str(revision),token=self.ta)["fields"]
                self.assertEqual(historic["counter"]["value"],revision)
                self.assertEqual(historic["ui/color"]["value"],"red")

    def test_03_concurrent_offline_writes_and_retries(self):
        p = self.profile()
        self.seed(p)
        def write(op,value,base):
            return request("POST",p+"/changes",{"operationId":op,"offline":True,
                "changes":[{"key":"x","value":value,"baseRevision":base}]},self.ta)
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as workers:
            writes = [workers.submit(write,guid(),i,0) for i in range(8)]
            results = [f.result() for f in writes]
            self.assertEqual(sum(len(item["applied"]) for item in results),1)
            self.assertEqual(sum(len(item["conflicts"]) for item in results),7)
            op = guid()
            writes = [workers.submit(write,op,"next",2) for _ in range(6)]
            self.assertTrue(all(f.result()=={"revision":3,"applied":["x"],"conflicts":[]} for f in writes))
        self.assertEqual(len(request("GET",p+"/history",token=self.ta)),3)

    def test_04_blob_digest_dedup_and_account_isolation(self):
        content = b"\x00\x80\xff" + secrets.token_bytes(65537)
        path = "/blobs/" + hashlib.sha256(content).hexdigest()
        request("PUT",path,b"wrong",self.ta,status=400)
        for _ in range(2):
            request("PUT",path,content,self.ta,status=204)
        self.assertEqual(request("GET",path,token=self.tb,binary=True),content)
        request("GET",path,token=self.to,status=404)

    def test_05_push_stream(self):
        p = self.profile()
        ready = threading.Event()
        def listen():
            req = urllib.request.Request(BASE+"/events", headers={"Authorization":"Bearer "+self.ta})
            with urllib.request.urlopen(req,timeout=12) as event_stream:
                for raw in event_stream:
                    if raw.startswith(b": heartbeat"):
                        ready.set()
                    if raw.startswith(b"data: "):
                        event = json.loads(raw[6:])
                        if event.get("profileId")==p.split("/")[-1] and event["revision"]==1:
                            return True
        with concurrent.futures.ThreadPoolExecutor() as workers:
            push = workers.submit(listen)
            self.assertTrue(ready.wait(5))
            self.seed(p)
            self.assertTrue(push.result(timeout=10))

    def test_06_online_lease_groups_and_rollback_fence(self):
        p=self.profile()
        self.seed(p)
        def stream():
            req=urllib.request.Request(BASE+"/events", headers={"Authorization":"Bearer "+self.ta})
            response=urllib.request.urlopen(req,timeout=8)
            return response,json.loads(response.readline()[6:])["onlineLease"]
        def send(lease,edits):
            return request("POST",p+"/changes",{"operationId":guid(),"offline":False,"onlineLease":lease,"changes":edits},self.ta)
        edit=lambda key,v,base:{"key":key,"value":v,"baseRevision":base}
        first,old=stream()
        try:
            self.assertEqual(send(old,[edit("ui/color","blue",0)])["applied"],["ui/color"])
            second,current=stream()
            try:
                grouped=[{**edit("ui/color","green",1),"group":"move"},{**edit("unrelated","yes",0),"group":"move"},edit("extra","ok",0)]
                result=send(old,grouped)
                self.assertEqual(result["conflicts"],["ui/color","unrelated"])
                self.assertEqual(result["applied"],["extra"])
                request("POST",p+"/rollback",{"operationId":guid(),"revision":1},self.ta)
                self.assertEqual(send(current,[edit("ui/color","stale",1)])["conflicts"],["ui/color"])
            finally:
                second.close()
        finally:
            first.close()

    def test_07_history_snapshots_trash_and_device_state(self):
        p=self.profile()
        wid,tid=guid(),guid()
        wp="workspace/"+wid+"/"
        tp="tab/"+tid+"/"
        fields={wp+"exists":True,wp+"name":"Работа",tp+"exists":True,tp+"workspaceId":wid,tp+"url":"https://example.test"}
        request("POST",p+"/seed",{"operationId":guid(),"fields":fields},self.ta)
        s=request("POST",p+"/snapshots",{"name":"Вручную"},self.ta)
        archived=guid()
        request("POST",p+"/trash",{"operationId":archived,"name":"Работа","keys":[wp+"exists"]},self.ta)
        current=request("GET",p+"/state",token=self.ta)
        self.assertTrue(all(f["deleted"] for f in current["fields"].values()))
        self.assertEqual(request("GET",p+"/trash",token=self.ta)[0]["id"],archived)
        request("POST",p+"/trash/"+archived+"/restore",{"operationId":guid()},self.ta)
        self.assertEqual(request("GET",p+"/trash",token=self.ta),[])
        self.assertEqual(request("GET",p+"/snapshots",token=self.ta)[0]["id"],s["id"])
        request("POST",p+"/snapshots/"+s["id"]+"/restore",{"operationId":guid()},self.ta)
        self.assertEqual(request("GET",p+"/history/1",token=self.ta)["fields"][tp+"url"]["value"],fields[tp+"url"])
        self.assertEqual(request("GET",p+"/history",token=self.ta)[-1]["changedCount"],len(fields))
        request("DELETE",p+"/snapshots/"+s["id"],token=self.ta,status=204)
        request("PUT","/devices/me/status",{"profileId":p.split("/")[-1],"browserOnline":True,"lastAppliedRevision":3},self.ta,status=204)
        devices=request("GET","/devices",token=self.ta)
        self.assertEqual(next(d["lastAppliedRevision"] for d in devices if d["id"]==self.a["deviceId"]),3)

    def test_08_credentials_and_device_revocation(self):
        # A previously admitted device logs back in under the same ID after revocation.
        # Cached operation receipts, including the original seed, cannot confer admission.
        profile=request("POST","/profiles",{"name":guid()},self.tb)["id"]
        p="/profiles/"+profile
        seed={"operationId":guid(),"fields":{"hello":"world"}}
        edits={"operationId":guid(),"offline":True,"changes":[{"key":"draft","value":1,"baseRevision":0}]}
        request("POST",p+"/seed",seed,self.tb)
        request("POST",p+"/changes",edits,self.tb)
        request("POST","/auth/register",self.login,status=409)
        request("POST","/auth/login",{**self.login,"password":"incorrect"},status=401)
        request("PATCH","/devices/"+self.b["deviceId"],{"name":"🦊 Мой ноутбук"}, self.ta,status=204)
        self.assertIn("🦊 Мой ноутбук",[row["name"] for row in request("GET","/devices",token=self.ta)])
        request("DELETE","/devices/"+self.b["deviceId"],token=self.ta,status=204)
        request("GET","/profiles",token=self.tb,status=401)
        returned=request("POST","/auth/login",{**self.login,"deviceId":self.b["deviceId"]})["token"]
        self.assertEqual(request("POST",p+"/seed",seed,returned,status=409)["error"],"admission_required")
        self.assertEqual(request("POST",p+"/changes",edits,returned,status=409)["error"],"admission_required")
        state=request("GET",p+"/state",token=returned)
        request("POST",p+"/admit",{"revision":state["revision"]},returned,status=204)
        self.assertEqual(request("POST",p+"/changes",edits,returned)["revision"],2)
        third = request("POST","/auth/login",self.login)
        new_password = secrets.token_hex(18)
        request("POST","/auth/password",{"currentPassword":"invalid","newPassword":new_password},self.ta,status=403)
        request("POST","/auth/password",{"currentPassword":self.login["password"],"newPassword":new_password},self.ta,status=204)
        request("GET","/profiles",token=third["token"],status=401)
        request("POST","/auth/login",self.login,status=401)
        new_login = request("POST","/auth/login",{**self.login,"password":new_password,"deviceId":self.a["deviceId"]})
        request("GET","/profiles",token=self.ta,status=401)
        request("POST","/auth/logout",token=new_login["token"],status=204)
        request("GET","/profiles",token=new_login["token"],status=401)


if __name__ == "__main__":
    unittest.main(verbosity=2)

