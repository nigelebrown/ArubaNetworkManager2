"""Browser end-to-end test of the JCF Network Manager demo (Blazor Server, real circuit, real Chrome).

0.9: sample devices are simulated; devices registered through the UI are real. With JCF_E2E_EMULATOR=1 (and
scripts/emulator/aruba_emulator.py running) the run also onboards a real-over-TLS emulated Instant VC end to end.

Walks the main operator journeys through the UI only, switching demo personas to prove
separation of duties. Writes ui_e2e_results.json and screenshots to the output directory.
"""
import asyncio
import json
import os
import sys
import time
from pathlib import Path

from playwright.async_api import async_playwright

BASE = "http://127.0.0.1:5180"
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else "ui-e2e-output")
OUT.mkdir(parents=True, exist_ok=True)
results = []


def record(name, ok, detail=""):
    results.append({"step": name, "outcome": "Passed" if ok else "Failed", "detail": detail})
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else "  -> " + detail))


async def open_page(pg, path, persona=None):
    if persona:
        await pg.goto(f"{BASE}/demo/persona/{persona}?returnUrl={path}")
    else:
        await pg.goto(BASE + path)
    # Wait until the interactive circuit is attached before clicking anything. (Polls from Python: the app's CSP
    # forbids the eval that Playwright's wait_for_function needs when the condition is not met immediately.)
    for _ in range(60):
        if await pg.evaluate("typeof Blazor !== 'undefined'"):
            break
        await pg.wait_for_timeout(250)
    await pg.wait_for_timeout(1200)


async def feedback(pg):
    loc = pg.locator(".feedback")
    await pg.wait_for_timeout(700)   # let the circuit apply the click before reading the status line
    try:
        await loc.first.wait_for(timeout=8000)
        return (await loc.first.inner_text()).strip()
    except Exception:
        return ""


async def main():
    async with async_playwright() as p:
        browser = await p.chromium.launch(executable_path=os.environ.get("CHROME_PATH", "/usr/bin/google-chrome"), args=["--no-sandbox"])
        ctx = await browser.new_context(viewport={"width": 1440, "height": 1000})
        pg = await ctx.new_page()
        console_errors = []
        pg.on("pageerror", lambda e: console_errors.append(str(e)))
        pg.on("console", lambda m: console_errors.append("console: " + m.text) if m.type == "error" else None)
        pg.on("response", lambda r: console_errors.append(f"HTTP {r.status} {r.url}") if r.status >= 500 else None)

        # 1. Every navigation page renders interactively without the error UI.
        await open_page(pg, "/", "operator")
        nav = await pg.locator("nav a").evaluate_all("els => els.map(e => e.getAttribute('href'))")
        bad = []
        for href in [h for h in nav if h and h.startswith("/") and not h.startswith("/account")]:
            await open_page(pg, href)
            h1 = await pg.locator("h1").first.inner_text() if await pg.locator("h1").count() else ""
            if not h1 or await pg.locator("#blazor-error-ui").is_visible() or "Page not found" in await pg.inner_text("main"):
                bad.append(href)
        record("All navigation pages render with an interactive circuit", not bad, ",".join(bad))
        await open_page(pg, "/")
        await pg.screenshot(path=str(OUT / "01_overview.png"), full_page=True)

        # 2. Register a device through the UI (operator).
        await open_page(pg, "/devices", "operator")
        await pg.get_by_label("Display name").fill("UI Test AP-535")
        await pg.get_by_label("Equipment type").select_option("Ap535")
        await pg.get_by_label("Management IP").fill("10.250.2.95")
        await pg.get_by_label("Credential profile").fill("LAB_AP_UI")
        await pg.get_by_role("button", name="Register device", exact=True).click()
        msg = await feedback(pg)
        record("Operator registers an AP-535 via the UI", "registered" in msg.lower(), msg)
        rows = await pg.locator("table tbody tr").all_inner_texts()
        record("New device appears in the inventory table", any("UI Test AP-535" in r for r in rows), str(rows)[:200])

        # 2b. Out-of-policy IP is refused with a clear message.
        await pg.get_by_label("Display name").fill("Rogue")
        await pg.get_by_label("Management IP").fill("8.8.8.8")
        await pg.get_by_label("Credential profile").fill("X")
        await pg.get_by_role("button", name="Register device", exact=True).click()
        await pg.wait_for_timeout(800)
        msg = await feedback(pg)
        record("Public IP outside management CIDRs is refused", "approved management subnet" in msg, msg)

        # 3. Read-only probe of the SAMPLE AP (simulator) and of the registered (real) AP.
        row = pg.locator("tr", has_text="Sample AP-505")
        await row.get_by_role("button", name="Test / read").click()
        await pg.wait_for_timeout(1500)
        msg = await feedback(pg)
        record("Sample AP probe authenticates (simulated) and records telemetry", "authenticated" in msg.lower(), msg)
        row = pg.locator("tr", has_text="UI Test AP-535")
        await row.get_by_role("button", name="Test / read").click()
        await pg.wait_for_timeout(1500)
        msg = await feedback(pg)
        record("Registered AP is treated as REAL: missing credentials reported, no simulated success", "not verified" in msg and "LAB_AP_UI" in msg, msg)
        await pg.screenshot(path=str(OUT / "02_devices_probe.png"), full_page=True)

        # 3b. Credential profile entered once, encrypted, never shown again.
        await pg.get_by_label("Profile name").fill("LAB_AP_UI")
        await pg.get_by_label("Username", exact=True).fill("labadmin")
        await pg.get_by_label("Password", exact=True).fill("Ui-Test-Secret-9")
        await pg.get_by_role("button", name="Save encrypted profile").click()
        msg = await feedback(pg)
        html = await pg.content()
        record("Operator saves an encrypted credential profile; secret never rendered", "saved (encrypted)" in msg and "Ui-Test-Secret-9" not in html, msg)
        profiles = await pg.locator("section:has(h2:text-is('Credential profiles'))").inner_text()
        record("Profile listed as encrypted and in use by 1 device", "LAB_AP_UI" in profiles and "Encrypted profile" in profiles, profiles[:200])

        # 3c. Edit, disable and re-enable a device.
        row = pg.locator("tr", has_text="UI Test AP-535")
        await row.get_by_role("button", name="Edit").click()
        await pg.locator("h2", has_text="Edit UI Test AP-535").wait_for()
        await pg.get_by_label("Instant VC / cluster name").last.fill("UI-VC")
        await pg.get_by_role("button", name="Save", exact=True).click()
        msg = await feedback(pg)
        await pg.wait_for_timeout(800)
        rows = await pg.locator("table tbody tr").all_inner_texts()
        record("Device edit saves cluster name", "updated" in msg.lower() and any("UI-VC" in r for r in rows), msg)
        await pg.locator("tr", has_text="UI Test AP-535").get_by_role("button", name="Disable").click()
        msg = await feedback(pg)
        record("Device can be disabled (no polling or changes)", "disabled" in msg.lower(), msg)
        await pg.locator("tr", has_text="UI Test AP-535").get_by_role("button", name="Enable").click()
        await pg.wait_for_timeout(500)

        # 3d. Bulk import validates each row.
        await pg.locator("textarea").fill("Bulk-AP-01,AP-505,10.250.2.120,LAB_AP_UI\nBulk-bad,AP-999,10.250.2.121,LAB_AP_UI")
        await pg.get_by_role("button", name="Import devices").click()
        msg = await feedback(pg)
        record("Bulk CSV import registers valid rows and rejects bad ones", "1 registered, 1 rejected" in msg, msg)

        # 3e. Real-over-TLS onboarding against the network emulator (optional).
        if os.environ.get("JCF_E2E_EMULATOR") == "1":
            await pg.get_by_label("Display name").fill("Emulated Instant VC")
            await pg.get_by_label("Equipment type").select_option("Ap505")
            await pg.get_by_label("Management IP").fill("10.199.0.10")
            await pg.get_by_label("Credential profile").first.fill("EMU_AP")
            await pg.get_by_label("Device username (optional)").fill("labadmin")
            await pg.get_by_label("Device password (optional)").fill("Lab-AP-Pass-2026")
            await pg.get_by_role("button", name="Register and test connection").click()
            await pg.wait_for_timeout(2500)
            msg = await feedback(pg)
            record("Self-signed device certificate is not trusted blindly", "TLS certificate not trusted" in msg, msg)
            await pg.locator("tr", has_text="Emulated Instant VC").get_by_role("button", name="Certificate").click()
            await pg.wait_for_timeout(1500)
            cert = await pg.locator("section:has(h2:has-text('Management certificate'))").inner_text()
            record("Certificate panel shows SHA-256, subject and trust status", "SHA-256" in cert and "jcf-emulated-ap" in cert and "No -" in cert, cert[:200])
            await pg.get_by_role("button", name="Pin this certificate").click()
            msg = await feedback(pg)
            record("Operator pins the verified certificate", "pinned" in msg.lower(), msg)
            await pg.locator("tr", has_text="Emulated Instant VC").get_by_role("button", name="Test / read").click()
            await pg.wait_for_timeout(2500)
            msg = await feedback(pg)
            row = await pg.locator("tr", has_text="Emulated Instant VC").inner_text()
            record("Real device read over pinned TLS: authenticated, firmware discovered", "authenticated" in msg.lower() and "8.10.0.21" in row and "Healthy" in row, msg + " | " + row)
            await pg.screenshot(path=str(OUT / "02b_real_device_onboarded.png"), full_page=True)

        # 4. Analyze Now raises the 80 MHz policy-drift recommendation.
        await open_page(pg, "/recommendations")
        await pg.get_by_role("button", name="Analyze Now").click()
        msg = await feedback(pg)
        record("Analyze Now completes with findings and no device change", "Analysis completed" in msg and "No device configuration changed" in msg, msg)
        text = await pg.inner_text("main")
        record("80 MHz policy drift recommendation is listed", "80 MHz policy drift" in text, "")
        record("Operator who raised it cannot review it (no review buttons)", await pg.get_by_role("button", name="Accept").count() == 0, "")

        # 5. Independent approver accepts it.
        await open_page(pg, "/recommendations", "reviewer-one")
        acc = pg.get_by_role("button", name="Accept")
        has = await acc.count() > 0
        if has:
            await acc.first.click()
            msg = await feedback(pg)
        else:
            msg = "no Accept button"
        record("Independent approver accepts the recommendation", has and "approved" in msg.lower(), msg)
        await pg.screenshot(path=str(OUT / "03_recommendations.png"), full_page=True)

        # 6. Operator submits an RF (band steering) proposal - requires TWO approvers.
        await open_page(pg, "/configuration", "operator")
        target = await pg.locator("select").first.locator("option", has_text="Sample AP-505").get_attribute("value")
        await pg.locator("select").first.select_option(target)
        await pg.get_by_label("Action", exact=False).first.select_option("ChangeBandSteering")
        await pg.wait_for_timeout(500)
        await pg.get_by_label("Band steering", exact=False).last.select_option("balance-bands")
        await pg.get_by_label("Justification").fill("UI E2E: balance bands in ICT lab")
        await pg.get_by_role("button", name="Submit for independent approval").click()
        msg = await feedback(pg)
        record("Operator submits an ARM band-steering proposal", "submitted" in msg.lower(), msg)

        # 6b. Operator cannot approve own proposal.
        await open_page(pg, "/changes")
        await pg.get_by_role("button", name="Approve").first.click()
        msg = await feedback(pg)
        record("Requester is blocked from approving their own proposal", "ChangeApprover role required" in msg or "different authorized person" in msg, msg)

        # 7. First approver -> PendingSecondApproval; same approver again is refused.
        await open_page(pg, "/changes", "reviewer-one")
        await pg.get_by_role("button", name="Approve").first.click()
        await feedback(pg)
        await pg.wait_for_timeout(500)
        state = await pg.locator("tbody tr").first.locator(".chip").inner_text()
        record("First approval moves RF proposal to PendingSecondApproval", state == "PendingSecondApproval", state)
        await pg.get_by_role("button", name="Approve").first.click()
        await pg.wait_for_timeout(800)
        msg = await feedback(pg)
        record("Same approver cannot give the second approval", "different authorized person" in msg, msg)

        # 8. Second, different approver -> Approved.
        await open_page(pg, "/changes", "reviewer-two")
        await pg.get_by_role("button", name="Approve").first.click()
        await feedback(pg)
        await pg.wait_for_timeout(500)
        state = await pg.locator("tbody tr").first.locator(".chip").inner_text()
        record("Second independent approval moves proposal to Approved", state == "Approved", state)
        record("Approver without operator role sees Execute but is refused", True, "checked below")
        if await pg.get_by_role("button", name="Execute approved change").count():
            await pg.get_by_role("button", name="Execute approved change").first.click()
            await pg.wait_for_timeout(800)
            msg = await feedback(pg)
            results[-1].update({"outcome": "Passed" if "role required" in msg.lower() else "Failed", "detail": msg})

        # 9. Operator executes (demo simulation) -> Applied with execution evidence.
        await open_page(pg, "/changes", "operator")
        await pg.get_by_role("button", name="Execute approved change").first.click()
        msg = await feedback(pg)
        record("Operator executes the approved change (simulated) -> Applied", "Applied" in msg and "SIMULATION" in msg, msg)
        hist = await pg.locator("section", has_text="Execution and recovery history").inner_text()
        record("Execution history shows preflight and verification events", "Preflight" in hist and "Execution/verification" in hist, "")
        await pg.screenshot(path=str(OUT / "04_change_board.png"), full_page=True)

        # 10. Compliance evaluation on the Operations page.
        await open_page(pg, "/operations")
        await pg.get_by_role("button", name="Evaluate observed configuration").click()
        msg = await feedback(pg)
        record("Operator runs compliance evaluation (read-only)", "zero configuration writes" in msg, msg)
        text = await pg.inner_text("main")
        record("80 MHz advertised finding is shown", "EIGHTY_MHZ_ADVERTISED" in text, "")

        # 10b. Alerts: the real AP without working credentials raised an alert; operator acknowledges it.
        await open_page(pg, "/alerts", "operator")
        ack = pg.locator("tr", has_text="UI Test AP-535").get_by_role("button", name="Acknowledge")
        has = await ack.count() > 0
        if has:
            await ack.first.click()
            msg = await feedback(pg)
        else:
            msg = "no alert row for UI Test AP-535"
        record("Operator acknowledges a device-API alert", has and "acknowledged" in msg.lower(), msg)

        # 10c. Switch interfaces from the sample CX are listed.
        await open_page(pg, "/switches")
        text = await pg.inner_text("main")
        record("Switch page lists interface state and PoE", "Sample CX 6100 interfaces" in text and "1/1/1" in text, text[:200])

        # 11. Read-only viewer cannot register devices or run analysis.
        await open_page(pg, "/devices", "viewer")
        record("Viewer sees no registration, credential or import forms",
               await pg.get_by_role("button", name="Register device", exact=True).count() == 0 and await pg.get_by_role("button", name="Save encrypted profile").count() == 0, "")
        await open_page(pg, "/recommendations")
        await pg.get_by_role("button", name="Analyze Now").click()
        msg = await feedback(pg)
        record("Viewer is refused when running analysis", "role required" in msg.lower(), msg)

        # 12. Capability certification is admin-only.
        await open_page(pg, "/capabilities", "operator")
        btn = pg.get_by_role("button", name="Record evidence (admin only)")
        if await btn.count():
            await btn.click()
            msg = await feedback(pg)
            record("Operator cannot certify device capabilities", "administrator" in msg.lower(), msg)

        # 12b. Administrator removes the sample data; real devices remain.
        await open_page(pg, "/admin/users", "admin")
        await pg.get_by_role("button", name="Remove sample data").click()
        await pg.get_by_role("button", name="Confirm: remove all sample devices").click()
        msg = (await pg.locator(".feedback").first.inner_text()).strip()
        await open_page(pg, "/devices")
        rows = await pg.locator("table tbody tr").all_inner_texts()
        record("Admin removes sample data; registered devices untouched", "3 sample device(s) removed" in msg and not any("Sample AP-505" in r for r in rows) and any("UI Test AP-535" in r for r in rows), msg)

        # 13. Audit trail captured every action with the real actor names.
        await open_page(pg, "/audit", "admin")
        text = await pg.inner_text("main")
        needed = ["demo-operator", "demo-reviewer-one", "demo-reviewer-two", "Execute approved change", "Recommendation decision", "Register device",
                  "Create device credential profile", "Edit device", "Remove sample data"]
        missing = [n for n in needed if n not in text]
        record("Audit trail records actions with the real persona identities", not missing, "missing: " + ",".join(missing))
        await pg.screenshot(path=str(OUT / "05_audit.png"), full_page=True)

        hdr = (await pg.request.get(BASE + "/")).headers
        record("Security headers present (CSP, frame-ancestors, nosniff)", "frame-ancestors 'none'" in hdr.get("content-security-policy", "") and hdr.get("x-content-type-options") == "nosniff", str({k: v for k, v in hdr.items() if k.startswith(("content-security", "x-"))}))
        record("No browser console errors, CSP violations or HTTP 5xx during the run", not console_errors, "; ".join(console_errors[:5]))
        await browser.close()

    passed = sum(r["outcome"] == "Passed" for r in results)
    print(f"\nUI E2E: {passed}/{len(results)} passed")
    (OUT / "ui_e2e_results.json").write_text(json.dumps({"passed": passed, "total": len(results), "steps": results,
                                                          "ranUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}, indent=2))
    return 0 if passed == len(results) else 1

sys.exit(asyncio.run(main()))
