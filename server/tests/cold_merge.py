"""Exercises the session-file workspace snapshot contract using two independently admitted devices."""
import copy
import secrets
import unittest

import api


class ColdMerge(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        user = {"username": "тест-" + api.guid(), "password": secrets.token_hex(16), "deviceName": "ПК №1"}
        cls.a = api.request("POST", "/auth/register", user)["token"]
        cls.b = api.request("POST", "/auth/login", {**user, "deviceName": "ПК №2"})["token"]

    def setUp(self):
        self.win = api.guid()
        self.one, self.two, self.three = ["workspace-cold/" + str(n) for n in (1,2,3)]
        def workspace(key, count):
            tabs = [{"id": api.guid(), "windowId": self.win, "index": i, "url": "https://example.test/"+str(i),
                     "pinned": False} for i in range(count)]
            return {"id": key.split("/")[-1], "name": "Пространство", "icon": "", "emoji": "", "tabs": tabs}
        self.seed = {self.one:workspace(self.one,4),self.two:workspace(self.two,0),self.three:workspace(self.three,0)}
        self.path = "/profiles/" + api.request("POST", "/profiles", {"name": api.guid()}, self.a)["id"]
        api.request("POST", self.path+"/seed", {"operationId":api.guid(),"fields":self.seed},self.a)
        api.request("GET", self.path+"/state", token=self.b)
        api.request("POST", self.path+"/admit", {"revision":1},self.b,status=204)

    def send(self, fields, token, base=1):
        for key,space in fields.items():
            for index,tab in enumerate(space["tabs"]):
                tab["index"]=index
        body = {"operationId":api.guid(),"offline":True,"changes":[
            {"key":k,"value":v,"baseRevision":base,"deleted":False} for k,v in fields.items()]}
        receipt = api.request("POST",self.path+"/changes",body,token)
        self.assertEqual(api.request("POST",self.path+"/changes",body,token),receipt)
        return receipt

    def current(self):
        return {k:v["value"] for k,v in api.request("GET",self.path+"/state",token=self.b)["fields"].items()}

    def test_independent_and_overlapping_edits(self):
        a=copy.deepcopy(self.seed[self.one])
        b=copy.deepcopy(a)
        a["name"]="Новое имя"
        a["tabs"][0]["url"]="https://cloud.example/"
        a["tabs"][1]["fixedTitle"]="С сервера"
        self.send({self.one:a},self.a)
        b["emoji"]="🐺"
        b["tabs"][0]["pinned"]=True
        b["tabs"][1]["fixedTitle"]="С ноутбука"
        receipt=self.send({self.one:b},self.b)
        self.assertEqual(receipt["applied"],[self.one])
        self.assertEqual(receipt["conflicts"],[self.one])
        merged=self.current()[self.one]
        self.assertEqual((merged["name"],merged["emoji"]),("Новое имя","🐺"))
        self.assertEqual((merged["tabs"][0]["url"],merged["tabs"][0]["pinned"]),("https://cloud.example/",True))
        self.assertEqual(merged["tabs"][1]["fixedTitle"],"С сервера")

    def test_order_additions_and_remove_versus_edit(self):
        a=copy.deepcopy(self.seed[self.one])
        b=copy.deepcopy(a)
        a["tabs"] = [a["tabs"][1],a["tabs"][0],a["tabs"][2]]
        self.send({self.one:a},self.a)
        b["tabs"][3]["pinned"]=True
        new={**b["tabs"][0],"id":api.guid(),"url":"https://new.example/"}
        b["tabs"]=[b["tabs"][2],b["tabs"][0],new,b["tabs"][1],b["tabs"][3]]
        self.send({self.one:b},self.b)
        result=self.current()[self.one]["tabs"]
        self.assertEqual([r["id"] for r in result if r["id"]!=new["id"]],[r["id"] for r in a["tabs"]])
        self.assertEqual(len(result),4)
        self.assertEqual(len({r["id"] for r in result}),4)
        self.assertEqual([r["index"] for r in result],list(range(4)))

    def test_move_preserves_remote_payload_and_remote_destination(self):
        a=copy.deepcopy(self.seed[self.one])
        b=copy.deepcopy(self.seed)
        a["tabs"][0]["url"]="https://new.example/"
        self.send({self.one:a},self.a)
        moved=b[self.one]["tabs"].pop(0)
        b[self.two]["tabs"].append(moved)
        self.send({self.one:b[self.one],self.two:b[self.two]},self.b)
        result=self.current()
        self.assertEqual(result[self.two]["tabs"][0]["url"],"https://new.example/")
        self.assertEqual(result[self.two]["tabs"][0]["id"],moved["id"])
        c=copy.deepcopy(self.seed)
        c[self.three]["tabs"].append(c[self.one]["tabs"].pop(0))
        self.send({self.one:c[self.one],self.three:c[self.three]},self.a)
        result=self.current()
        self.assertEqual(result[self.three]["tabs"],[])
        self.assertEqual([r["id"] for r in result[self.two]["tabs"]],[moved["id"]])
        self.assertEqual(sum(r["id"]==moved["id"] for v in result.values() for r in v["tabs"]),1)

    def test_concurrent_close_cannot_be_resurrected_by_move(self):
        a=copy.deepcopy(self.seed[self.one])
        b=copy.deepcopy(self.seed)
        closed=a["tabs"].pop(0)["id"]
        a["tabs"][0]["url"]="https://current.example/"
        self.send({self.one:a},self.a)
        b[self.two]["tabs"].append(b[self.one]["tabs"].pop(0))
        b[self.two]["tabs"].append(b[self.one]["tabs"].pop(0))
        self.send({self.one:b[self.one],self.two:b[self.two]},self.b)
        result=self.current()
        self.assertEqual(result[self.two]["tabs"],[])
        self.assertNotIn(closed,[r["id"] for r in result[self.one]["tabs"]])
        self.assertEqual(result[self.one]["tabs"][0]["url"],"https://current.example/")

    def test_competing_tile_placements_do_not_split_composite_change(self):
        a=copy.deepcopy(self.seed[self.one])
        b=copy.deepcopy(a)
        for slot,idx in enumerate((0,1)):
            a["tabs"][idx]["tiling"]={"id":"cloud-tile","index":slot,"layout":"row","type":"selection"}
        self.send({self.one:a},self.a)
        for slot,idx in enumerate((1,2)):
            b["tabs"][idx]["tiling"]={"id":"other-tile","index":slot,"layout":"column","type":"selection"}
        b["tabs"][2]["pinned"]=True
        self.send({self.one:b},self.b)
        tabs=self.current()[self.one]["tabs"]
        self.assertEqual([t["tiling"]["id"] for t in tabs if "tiling" in t],["cloud-tile","cloud-tile"])
        self.assertTrue(tabs[2]["pinned"])


if __name__ == "__main__":
    unittest.main(verbosity=2)

