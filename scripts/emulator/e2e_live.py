"""Operator walk-through against the running app and the network emulators (real TCP + TLS).
Prints PASS/FAIL per step and writes a JSON evidence file."""
import json, subprocess, sys, time, urllib.request, urllib.error

BASE = "http://127.0.0.1:5180"
RESULTS = []

def call(method, path, persona="operator", body=None):
    req = urllib.request.Request(BASE + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                 headers={"Cookie": "jcf_demo_persona=" + persona, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read().decode(); return r.status, (json.loads(raw) if raw.strip().startswith(("{", "[")) else raw)
    except urllib.error.HTTPError as e:
        raw = e.read().decode(); return e.code, (json.loads(raw) if raw.strip().startswith("{") else raw)

def check(name, ok, detail=""):
    RESULTS.append({"step": name, "passed": bool(ok), "detail": str(detail)[:400]})
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else "  -> " + str(detail)[:400]), flush=True)

def emu(): return json.loads(urllib.request.urlopen("http://127.0.0.1:8799/").read())
def fp(name): return subprocess.check_output(["openssl", "x509", "-in", f"/home/claude/emu/{name}.crt", "-noout", "-fingerprint", "-sha256"]).decode().split("=")[1].strip().replace(":", "")

# 1. Sample inventory is simulated and polled
time.sleep(4)
s, devices = call("GET", "/api/v1/devices")
samples = [d for d in devices if d["isSimulated"]]
check("sample devices seeded on 192.0.2.0/24 and simulated", len(samples) == 3 and all(d["managementIp"].startswith("192.0.2.") for d in samples), devices)
check("scheduled poller has read sample devices at startup", all(d["lastStatus"] == "Healthy" for d in samples), [d["lastStatus"] for d in samples])

# 2. Register the emulated Instant VC with a WRONG password
s, ap = call("POST", "/api/v1/devices", body={"name": "EMU Instant VC", "kind": "Ap505", "managementIp": "10.199.0.10", "secretRef": "emu_ap",
                                             "clusterName": "EMU-VC", "username": "labadmin", "password": "wrong-password"})
check("register real AP with inline credentials", s == 200 and not ap["isSimulated"] and "wrong-password" not in json.dumps(ap), ap)
apid = ap["id"]
s, probe = call("POST", f"/api/v1/devices/{apid}/probe")
check("unpinned self-signed device certificate is rejected (no blind trust)", not probe["connected"] and "TLS certificate not trusted" in (probe["issue"] or ""), probe["issue"])

# 3. Capture certificate (no credentials sent) and pin it
before = len([l for l in emu()["log"] if l.startswith("AP POST /rest/login")])
s, cert = call("POST", f"/api/v1/devices/{apid}/tls/capture")
check("certificate capture returns the device's real SHA-256", s == 200 and cert["sha256"] == fp("ap") and cert["trustedByServer"] is False, cert)
check("certificate capture sent no login/credentials", len([l for l in emu()["log"] if l.startswith("AP POST /rest/login")]) == before)
s, _ = call("POST", f"/api/v1/devices/{apid}/tls/pin", body={"sha256": cert["sha256"]})
check("pin certificate", s == 204, s)
s, probe = call("POST", f"/api/v1/devices/{apid}/probe")
check("pinned TLS works; wrong password reported as authentication failure", not probe["connected"] and "authentication rejected" in (probe["issue"] or ""), probe["issue"])

# 4. Rotate the credential profile and read the device for real
s, _ = call("PUT", "/api/v1/credentials/EMU_AP", body={"username": "labadmin", "password": "Lab-AP-Pass-2026", "description": "emulated VC"})
check("rotate encrypted credential profile", s == 204, s)
s, creds = call("GET", "/api/v1/credentials")
check("credential listing never exposes secrets", "Lab-AP-Pass" not in json.dumps(creds) and "labadmin" not in json.dumps(creds), creds)
s, probe = call("POST", f"/api/v1/devices/{apid}/probe")
check("real AP read: authenticated, firmware, members, clients, RF", probe["connected"] and probe["issue"] is None and probe["firmware"] == "8.10.0.21"
      and probe["apCount"] == 1 and probe["clientCount"] == 2 and probe["observation"]["channel"] == 149, probe)
check("ARM bare flag 80mhz-support parsed from real config", probe["arm"].get("80mhz-support") == "enabled", probe["arm"])
check("device logged out after read", emu()["ap"]["sid"] is None)
s, devs = call("GET", "/api/v1/devices")
check("AP healthy with pinned TLS", next(d for d in devs if d["id"] == apid)["lastStatus"] == "Healthy" and next(d for d in devs if d["id"] == apid)["tlsPinned"])
s, alerts = call("GET", "/api/v1/alerts")
check("API-down alert raised earlier is auto-resolved by the healthy read",
      all(a["state"] == "Resolved" for a in alerts if a.get("deviceId") == apid) and any(a.get("deviceId") == apid for a in alerts), alerts)

# 5. Switch onboarding
s, cx = call("POST", "/api/v1/devices", body={"name": "EMU CX 6200", "kind": "Cx6200", "managementIp": "10.199.0.11", "secretRef": "EMU_CX",
                                              "username": "cxadmin", "password": "Lab-CX-Pass-2026"})
cxid = cx["id"]
s, cert = call("POST", f"/api/v1/devices/{cxid}/tls/capture"); call("POST", f"/api/v1/devices/{cxid}/tls/pin", body={"sha256": cert["sha256"]})
s, probe = call("POST", f"/api/v1/devices/{cxid}/probe")
check("real CX read: session+CSRF, firmware, VLANs, interfaces", probe["connected"] and probe["issue"] is None and probe["firmware"] == "PL.10.13.1010"
      and probe["vlanCount"] == 2 and probe["portCount"] == 3, probe)
s, ports = call("GET", f"/api/v1/devices/{cxid}/ports")
check("CX port observations stored", len(ports) == 3 and any(p["poeWatts"] == 7.5 for p in ports), ports)

# 6. Analysis and compliance now see the real AP's 80 MHz flag
call("POST", "/api/v1/analysis-runs")
s, recs = call("GET", "/api/v1/recommendations")
check("analysis flags 80 MHz on the real AP", any(r["fingerprint"].startswith(apid.replace("-", "")) and r["fingerprint"].endswith("RF_WIDTH_80_ENABLED") for r in recs))
call("POST", "/api/v1/compliance/evaluate")
s, findings = call("GET", "/api/v1/compliance/findings")
check("compliance finding EIGHTY_MHZ_ADVERTISED for the real AP", any(f["deviceId"] == apid and f["rule"] == "EIGHTY_MHZ_ADVERTISED" for f in findings))

def approve(pid):
    a = call("POST", f"/api/v1/change-proposals/{pid}/decision", "reviewer-one", {"decision": "approve"})[0]
    b = call("POST", f"/api/v1/change-proposals/{pid}/decision", "reviewer-two", {"decision": "approve"})[0]
    return a, b

# 7. Real AP change: channel policy (removes 80 MHz), gated by certification
s, p = call("POST", "/api/v1/change-proposals", body={"deviceId": apid, "action": "SetChannelPolicy",
            "payloadJson": json.dumps({"channelWidthMhz": 40, "channels5Ghz": [36, 40, 149, 153]}), "justification": "Enforce 20/40 policy"})
check("channel-policy proposal accepted with Instant scope recorded", s == 200 and p["baselineClusterMemberCount"] == 1, p)
check("two independent approvals", approve(p["id"]) == (200, 200))
s, r = call("POST", f"/api/v1/change-proposals/{p['id']}/execute")
check("execution refused until firmware capability is certified", s == 409 and "certified" in json.dumps(r), r)
s, _ = call("POST", "/api/v1/admin/capabilities", "admin", {"deviceId": apid, "action": "SetChannelPolicy", "state": "WriteVerified", "evidence": "EMU-LAB-0001 witnessed"})
check("admin records lab certification", s == 204, s)
s, r = call("POST", f"/api/v1/change-proposals/{p['id']}/execute")
arm = emu()["ap"]["arm"]
check("channel policy applied and verified on the device", s == 200 and r["state"] == "Applied" and arm.get("a-channels") == "36,40,149,153"
      and "80mhz-support" not in arm and arm.get("wide-bands") == "5ghz", {"r": r, "arm": arm})

# 8. Cluster drift is caught at execution (VC grows from 1 to 3 APs after approval)
s, p = call("POST", "/api/v1/change-proposals", body={"deviceId": apid, "action": "ChangeClientMatch", "payloadJson": "{\"value\":\"enabled\"}", "justification": "x"})
approve(p["id"]); call("POST", "/api/v1/admin/capabilities", "admin", {"deviceId": apid, "action": "ChangeClientMatch", "state": "WriteVerified", "evidence": "EMU-LAB-0002"})
urllib.request.urlopen(urllib.request.Request("http://127.0.0.1:8799/?aps=3", method="POST"))
call("POST", f"/api/v1/devices/{apid}/probe")
s, r = call("POST", f"/api/v1/change-proposals/{p['id']}/execute")
check("cluster membership drift (1 -> 3 APs) blocks execution", s == 409 and "membership changed" in json.dumps(r) and emu()["ap"]["arm"].get("client-match") is None, r)
urllib.request.urlopen(urllib.request.Request("http://127.0.0.1:8799/?aps=1", method="POST"))
call("POST", f"/api/v1/devices/{apid}/probe")
s, r = call("POST", f"/api/v1/change-proposals/{p['id']}/execute")
check("same approved change executes once scope matches again", s == 200 and r["state"] == "Applied" and emu()["ap"]["arm"].get("client-match") == "", r)

# 9. Switch changes: access VLAN, PoE, and the trunk guard
def cx_change(action, payload):
    s, p = call("POST", "/api/v1/change-proposals", body={"deviceId": cxid, "action": action, "payloadJson": json.dumps(payload), "justification": "lab"})
    approve(p["id"]); call("POST", "/api/v1/admin/capabilities", "admin", {"deviceId": cxid, "action": action, "state": "WriteVerified", "evidence": "EMU-LAB-CX"})
    call("POST", f"/api/v1/devices/{cxid}/probe")
    return call("POST", f"/api/v1/change-proposals/{p['id']}/execute")
s, r = cx_change("SetSwitchPortVlan", {"port": "1/1/2", "mode": "access", "vlan": 20})
check("CX access VLAN applied and verified", r.get("state") == "Applied" and emu()["cx"]["ports"]["1/1/2"][1] == 20, r)
s, r = cx_change("SetSwitchPoe", {"port": "1/1/1", "state": "disabled"})
c = emu()["cx"]["poe"]["1/1/1"]
check("CX PoE disabled, other PoE settings preserved", r.get("state") == "Applied" and c["admin_disable"] is True and c["priority"] == "low", {"r": r, "poe": c})
s, r = cx_change("SetSwitchPortVlan", {"port": "1/1/49", "mode": "access", "vlan": 20})
check("trunk-to-access conversion on uplink refused without a write", r.get("state") == "Failed" and emu()["cx"]["ports"]["1/1/49"][0] == "native-untagged", r)
s, r = cx_change("CreateSwitchVlan", {"vlan": 30, "name": "CCTV"})
check("CX VLAN create verified", r.get("state") == "Applied" and emu()["cx"]["vlans"].get("30") == "CCTV", r)
check("switch session always logged out", emu()["cx"]["session"] is None)

check("every device request body carried Content-Length (no chunked bodies)", not any(l.startswith("CHUNKED") for l in emu()["log"]))

# 10. Audit and secret hygiene
s, audit = call("GET", "/api/v1/audit?take=500")
text = json.dumps(audit)
check("audit has onboarding, pinning, credential and execution events",
      all(k in text for k in ["Register device", "Pin device certificate", "Rotate device credential profile", "Execute approved change", "Capability certification"]))
check("no device password anywhere in audit", "Lab-AP-Pass" not in text and "Lab-CX-Pass" not in text and "wrong-password" not in text)
s, logtext = 0, open("/tmp/app.log").read()
check("no device password in application log", "Lab-AP-Pass" not in logtext and "Lab-CX-Pass" not in logtext and "wrong-password" not in logtext)

json.dump({"utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "passed": sum(r["passed"] for r in RESULTS), "total": len(RESULTS), "steps": RESULTS},
          open(sys.argv[1] if len(sys.argv) > 1 else "/tmp/live-e2e.json", "w"), indent=2)
print(f"TOTAL {len(RESULTS)} PASSED {sum(r['passed'] for r in RESULTS)}")
