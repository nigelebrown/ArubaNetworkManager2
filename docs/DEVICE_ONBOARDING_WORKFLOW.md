# Device onboarding workflow — JCF Intelligent Network Manager

## Goal
Replace the former flat navigation and large "Add a device" form with a guided, operator-led process. Native CentOS Stream 9, ASP.NET Core Identity and SQL Server remain unchanged; sample Aruba equipment remains simulated.

## Two-level navigation (top-level group → page)

| Group | Pages |
|---|---|
| Overview | Dashboard and Start device onboarding shortcut |
| Discover & onboard | Discover APs, Discover CX switches, Inventory, Credential profiles, Locations |
| Configure & standardize | Device groups, WLAN/RF templates, AP settings, CX VLAN and ports, Proposals |
| Monitor & optimize | Topology, Operations, Wireless, Clients, Alerts, Optimization, Kiosk |
| Approvals & audit | Approve/execute, API evidence, Audit, Operator accounts |
| My account | Change password and sign out |

## Eight-step device onboarding
1. **Equipment** — Choose Aruba Instant AP-505/AP-535 (TCP 4343) or AOS-CX 6100/6200 (TCP 443). Never infer an exact model from an open socket.
2. **Credentials** — Choose an encrypted credential profile or create/rotate one. Reuse existing profiles; credentials are never displayed. This is the device API account, **not** the platform administrator login.
3. **Discovery** — Provide an inclusive start/end IPv4 range, or a manual IP, or select known inventory without scanning. The read-only TCP check is explicit, limited to 64 addresses and six parallel connects, and every target must belong to the configured Jcf:AllowedManagementCidrs. No unauthenticated service/banner parsing, credentials, or writes.
4. **Review** — Choose which candidates to include; label *new* vs *existing* and indicate network port status. Port open only means *port reachable*, not that Aruba is verified.
5. **Placement** — Name new devices, confirm the model and location, and prepopulate existing-device name/model/cluster/credential/location from SQL inventory. Users can set a separate credential reference for each candidate. Model/IP identity changes require remove/re-register.
6. **Register** — Explicit review. New devices register in SQL; existing devices are not overwritten unless the operator selects update. Each action is auditable. Back/forward navigation cannot register a completed candidate twice. This step does not alter real device configuration.
7. **Import** — Choose read-only API verification and save sanitized telemetry/configuration snapshot now, or defer to later. The existing EnterpriseOperationsService.SnapshotAsync only captures whitelisted non-secret fields (firmware, basic radio/ARM values), not a restorable running configuration. Offline/failed authentications are reported per device; no fabricated success.
8. **Configure** — Finish and review inventory or go directly to AP management / CX switch status / preselected configuration proposal / approval. No automatic configuration deployment.

## Validation checklist
- [ ] Two-level menu with groups and direct onboarding access, usable at desktop and mobile width
- [ ] AP and CX discovery are separate, using the appropriate ports and always labelled "port only"
- [ ] Range 1–64, invalid/reverse/out-of-allowlist addresses blocked before probes
- [ ] Existing sample devices usable from "Use existing inventory" without any network I/O
- [ ] Existing devices prefill settings; operator must explicitly opt into inventory edits
- [ ] Credential references checked against encrypted credential profiles before registering new equipment
- [ ] API read and sanitized snapshot optional; error on wrong creds/port/certificate shown, not hidden
- [ ] Config proposal preselects exactly one newly onboarded device when returning from wizard
- [ ] No real device changes without existing proposal and independent approval gates
- [ ] Real Identity + SQL data persist after service restart; no migration or SQL reset triggered by update
- [ ] Regression tests and GitHub CI pass

## Current limitations / next incremental features
- *Discovery* is a bounded TCP REST-port reachability check, **not** automatic Aruba model/serial number discovery, LLDP topology discovery or an inventory-discovery engine.
- *Import settings* is a limited sanitized read-only snapshot. Full configuration backup, full WLAN/VLAN/port state import, comparison/drift, recovery/restore, and certified vendor API reads remain distinct work.
- The wizard is per-browser-session state; successfully registered inventory, credentials and snapshots persist in SQL. Draft onboarding jobs and resume across browsers need a future SQL-backed job table and workflow state transition model.
- Operator approval of new management CIDRs still requires controlled server configuration of Jcf:AllowedManagementCidrs. This cannot be widened automatically by a scan request.
- Network writes remain disabled in test configuration. Do not bypass firmware/hardware certification or change approvals.
- Existing deployment test currently uses temporary HTTP. Authenticate from a restricted test LAN only and move to TLS before operational rollout.

## Upgrade existing v0.9.1 native installation

The all-in-one test installer refuses to overwrite a running instance. Use the non-destructive application update tool:

```bash
curl -fsSLo /tmp/UPDATE_JCF.sh https://raw.githubusercontent.com/nigelebrown/ArubaNetworkManager2/main/UPDATE_NATIVE_091.sh
bash -n /tmp/UPDATE_JCF.sh && sudo bash /tmp/UPDATE_JCF.sh
```

This rebuilds/tests current GitHub source, keeps SQL Server and data/keys/Identity/Nginx/firewall intact, restarts the native application only after a successful build, and rolls back the binary release if health checks fail. No SQL migration is necessary for these UI/service improvements.
