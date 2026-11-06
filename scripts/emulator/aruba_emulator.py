"""Network-level emulator of an Aruba Instant 8.x VC (REST :4343) and an AOS-CX 10.13 switch (REST :443) for
end-to-end verification of the JCF Network Manager over real TCP/TLS. Request/response shapes follow the vendor REST
guides as implemented by the application; this is NOT firmware certification."""
import json, ssl, sys, threading, urllib.parse, uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG = []
AP = {"user": "labadmin", "pass": "Lab-AP-Pass-2026", "sid": None, "arm": {"band-steering-mode": "prefer-higher-band", "min-tx-power": "9",
      "80mhz-support": "", "wide-bands": "5ghz", "a-channels": "36,40,44,48,149,153,157,161"}, "aps": 1}
CX = {"user": "cxadmin", "pass": "Lab-CX-Pass-2026", "csrf": None, "session": None, "vlans": {1: "DEFAULT_VLAN_1", 20: "STAFF"},
      "ports": {"1/1/1": ["access", 1, []], "1/1/2": ["access", 1, []], "1/1/49": ["native-untagged", 1, [1, 20]]},
      "poe": {"1/1/1": {"admin_disable": False, "priority": "low"}, "1/1/2": {"admin_disable": False, "priority": "low"}}}

class Base(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send(self, code, obj=None, headers=None):
        body = b"" if obj is None else json.dumps(obj).encode()
        self.send_response(code)
        for k, v in (headers or {}).items(): self.send_header(k, v)
        if obj is not None: self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def body(self):
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower(): LOG.append("CHUNKED-BODY " + self.path.split("?")[0])
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n) or b"{}") if n else {}

class Instant(Base):
    def run_cmd(self, cmd):
        arm = "".join(f" {k}\n" if v == "" else f" {k} {v}\n" for k, v in AP["arm"].items())
        return {"show version": "ArubaOS (MODEL: 505), Version 8.10.0.21_89341\n",
                "show aps": f"{AP['aps']} Access Points\nName IP Type\n" + "\n".join(f"EMU-AP-{i} 10.199.0.{20+i} AP-505" for i in range(1, AP['aps']+1)),
                "show clients": "Client List\nlaptop 10.250.20.5 aa:bb:cc:dd:ee:01\nphone 10.250.20.6 aa:bb:cc:dd:ee:02\n2 Clients\n",
                "show running-config": "version 8.10.0.0\narm\n" + arm + "!\n",
                "show arm config": "ARM config\n"}.get(cmd, "Channel: 149\nChannel Width (MHz): 40\nChannel Utilization (percent): 31\nNoise Floor (dBm): -94\n" if cmd.startswith("show stats ap") else "")
    def do_POST(self):
        u = urllib.parse.urlparse(self.path); q = urllib.parse.parse_qs(u.query); LOG.append("AP POST " + u.path)
        if u.path == "/rest/login":
            b = self.body()
            if b.get("user") == AP["user"] and b.get("passwd") == AP["pass"]:
                AP["sid"] = uuid.uuid4().hex; return self.send(200, {"Status": "Success", "sid": AP["sid"]})
            return self.send(200, {"Status": "Failed", "message": "Invalid credentials"})
        if u.path == "/rest/logout": AP["sid"] = None; return self.send(200, {"Status": "Success"})
        if q.get("sid", [None])[0] != AP["sid"] or not AP["sid"]: return self.send(401, {"Status": "Failed"})
        if u.path == "/rest/arm":
            for k, v in self.body()["arm"].items():
                if k == "action": continue
                if "value" in v: AP["arm"][k] = v["value"]
                elif v.get("action") == "delete": AP["arm"].pop(k, None)
                else: AP["arm"][k] = ""
            return self.send(200, {"Status-code": 0, "message": "Success"})
        self.send(404, {})
    def do_GET(self):
        u = urllib.parse.urlparse(self.path); q = urllib.parse.parse_qs(u.query); LOG.append("AP GET " + q.get("cmd", [u.path])[0])
        if u.path == "/rest/show-cmd" and AP["sid"] and q.get("sid", [None])[0] == AP["sid"]:
            return self.send(200, {"Status-code": 0, "Command output": self.run_cmd(q["cmd"][0])})
        self.send(401, {"Status": "Failed"})

class Cx(Base):
    def ref(self, i): return f"/rest/v10.13/system/vlans/{i}"
    def authed(self): return CX["csrf"] and self.headers.get("x-csrf-token") == CX["csrf"] and CX["session"] and CX["session"] in (self.headers.get("Cookie") or "")
    def do_POST(self):
        u = urllib.parse.urlparse(self.path); q = urllib.parse.parse_qs(u.query); LOG.append("CX POST " + u.path)
        if u.path == "/rest/latest/login":
            if q.get("username", [""])[0] == CX["user"] and q.get("password", [""])[0] == CX["pass"]:
                CX["session"] = "id=" + uuid.uuid4().hex; CX["csrf"] = uuid.uuid4().hex
                return self.send(200, None, {"Set-Cookie": CX["session"] + "; Path=/; Secure; HttpOnly", "x-csrf-token": CX["csrf"]})
            return self.send(401, {"message": "Login failed"})
        if not self.authed(): return self.send(401, {})
        if u.path == "/rest/latest/logout": CX["session"] = None; CX["csrf"] = None; return self.send(200)
        if u.path == "/rest/latest/system/vlans":
            b = self.body(); CX["vlans"][int(b["id"])] = b["name"]; return self.send(201)
        self.send(404, {})
    def do_GET(self):
        u = urllib.parse.urlparse(self.path); LOG.append("CX GET " + u.path)
        if not self.authed(): return self.send(401, {})
        p = urllib.parse.unquote(u.path)
        if p == "/rest": return self.send(200, {"latest": {"version": "v10.13", "prefix": "/rest/v10.13"}})
        if p == "/rest/latest/system": return self.send(200, {"software_version": "PL.10.13.1010", "hostname": "emu-cx"})
        if p == "/rest/latest/system/vlans": return self.send(200, {str(k): {"id": k, "name": v} for k, v in CX["vlans"].items()})
        if p.startswith("/rest/latest/system/vlans/"):
            i = int(p.rsplit("/", 1)[1]); return self.send(200, {"id": i, "name": CX["vlans"][i]}) if i in CX["vlans"] else self.send(404, {})
        if p == "/rest/latest/system/interfaces":
            return self.send(200, {k: {"link_state": "up" if k != "1/1/2" else "down", "rx_bytes": 123456, "tx_bytes": 654321, "rx_errors": 0, "tx_errors": 0,
                                       **({"poe_power": 7.5} if k in CX["poe"] else {})} for k in CX["ports"]})
        if p.startswith("/rest/latest/system/interfaces/"):
            rest = p[len("/rest/latest/system/interfaces/"):]
            if rest.endswith("/poe_interface"):
                port = rest[:-len("/poe_interface")]
                return self.send(200, {"config": CX["poe"][port]}) if port in CX["poe"] else self.send(404, {})
            if rest not in CX["ports"]: return self.send(404, {})
            m, t, tr = CX["ports"][rest]
            return self.send(200, {"vlan_mode": m, "vlan_tag": self.ref(t), "vlan_trunks": {str(x): self.ref(x) for x in tr}})
        self.send(404, {})
    def do_PATCH(self):
        u = urllib.parse.urlparse(self.path); LOG.append("CX PATCH " + urllib.parse.unquote(u.path))
        if not self.authed(): return self.send(401, {})
        p = urllib.parse.unquote(u.path); rest = p[len("/rest/latest/system/interfaces/"):]; b = self.body()
        if rest.endswith("/poe_interface"):
            CX["poe"][rest[:-len("/poe_interface")]] = b["config"]; return self.send(204)
        st = CX["ports"][rest]
        if "vlan_mode" in b: st[0] = b["vlan_mode"]
        if "vlan_tag" in b: st[1] = int(b["vlan_tag"].rsplit("/", 1)[1])
        if "vlan_trunks" in b: st[2] = [int(x.rsplit("/", 1)[1]) for x in b["vlan_trunks"]]
        self.send(204)

class Control(Base):
    def do_POST(self):
        q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query); AP["aps"] = int(q["aps"][0]); self.send(200, {"aps": AP["aps"]})
    def do_GET(self):
        self.send(200, {"log": LOG[-200:], "ap": AP, "cx": {**CX, "vlans": {str(k): v for k, v in CX["vlans"].items()}}})

def serve(ip, port, handler, cert=None):
    srv = ThreadingHTTPServer((ip, port), handler)
    if cert:
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain(cert + ".crt", cert + ".key")
        srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
    threading.Thread(target=srv.serve_forever, daemon=True).start()

if __name__ == "__main__":
    serve("10.199.0.10", 4343, Instant, "ap")
    serve("10.199.0.11", 443, Cx, "cx")
    serve("127.0.0.1", 8799, Control)
    print("emulators up", flush=True)
    threading.Event().wait()
