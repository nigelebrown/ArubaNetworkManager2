# AP Management expansion — native JCF Network Manager (AP-505, AP-535)

Scope: Aruba Instant AOS-8 AP-505 and AP-535; CentOS .NET 10; SQL Server; real ASP.NET Identity; simulated inventory for early testing. The JCF 20/40 MHz policy overrides any Aruba default. No 80/160 MHz, and do not implicitly enable DFS or an excluded 5 GHz channel.

## Current implementation (after this change)
- New **/ap-management** operational page: filter/select AP; existing SQL inventory and location; connectivity/firmware; read-only RF channel, width, utilization, noise, retries; recent radio observations; pseudonymized client sessions; Instant VC members and confidence; sanitized baseline fingerprints; pending recommendations.
- Existing **/wireless**: ARM values, cluster membership; **/configuration**: Create SSID+VLAN, band steering, minimum TX power, ClientMatch, bounded channel policy; **/changes**: independent approval; **/recommendations**: manual/hourly analysis.
- The page does not query APs directly; the existing validated DeviceService probe pathway is the only approved network reader. Missing telemetry is unknown, not zero.
- No additional schema migration or write permission added.

## Prioritized enhancements (planned, not implemented)

| Priority | Module | Requested features and acceptance |
|---|---|---|
| P0 | AP summary and targeting | Inventory by site/Area/Division, physical placement and VC membership; search by model, firmware, status, cluster, SSID; per-device health, last successful poll, certificates, drift; scope RBAC |
| P0 | WLAN/SSID lifecycle | Create/edit/disable/delete SSIDs; SSID-to-VLAN map; WPA2/WPA3 transition; 802.1X/RADIUS profiles; PMF; guest/captive portal isolation; admin Wi-Fi vs guest local breakout; verify client join, DHCP, DNS, VLAN |
| P0 | RF profiles | Per-radio 2.4/5 GHz enabling, 2.4 GHz fixed 20 MHz, 5 GHz 20/40 only, permitted non-DFS channel pools, minimum/maximum transmit power bounds, ARM scanning and client-aware behavior; template with device/zone exceptions; preview before approval |
| P0 | ARM optimization | ARM band steering, ClientMatch, airtime fairness, spectrum balancing and dynamic channel/power analytics; produce evidence, scope, risk, rollback and two-independent-approver proposal; hourly recommendations/manual Analyze Now; disable ALL automatic writes |
| P1 | Client experience | Active clients per AP, SSID, band, VLAN, sanitized MAC, RSSI, SNR, retry%, association/802.1X failures, sticky clients, roaming and disconnects, timeline and historical comparisons |
| P1 | Cluster and zones | VC/conductor, member inventory and actual effective scope, zones, election/failover, mismatch detection, preview blast radius; deny write if scope cannot be established |
| P1 | Radio diagnostics | Channel congestion, CCI/ACI, noise floor, interference, utilization trends, AP uplink/PoE/radio state, logs and RF scan evidence; robust unit-aware parsing for AOS-8.10 |
| P1 | Configuration baseline | Capture redacted snapshots, compare drift with approved templates, per-location policy replication from reference AP, audit trail, known-good restore plan and change window |
| P2 | RF optimizer (advisory) | Confidence-rated analytics from multiple consecutive samples; choose channel/power/ARM advice only if firmware-verified metrics, client outcomes and regulatory constraints are met; no autonomous configuration writes |
| P2 | Lifecycle operations | Firmware compatibility matrix, maintenance scheduling, availability alerts, inventory reconciliation, report/export and health trends |

## Implementation and safety requirements
1. Never mix real and simulated equipment results; display `IsSimulated` conspicuously; use no network I/O for simulated records.
2. Every new AOS-8 read command/API endpoint must have a firmware-specific fixture test (at minimum supported AP-505 and AP-535 firmware), strict parser, bounded timeout and redacted errors. A successful show-cmd request alone is not evidence of a parsed value.
3. Never store plaintext PSKs, REST session IDs, raw client MACs, RADIUS shared secrets or full running configs in audit or export.
4. All configuration actions are proposal-only until real-hardware certification, version/model capability evidence, independent approvals, cluster impact plan, and tested readback/rollback exist.
5. ClientMatch and radio operations are cluster-sensitive and may have firmware-specific effects; verify VC state before changes.
6. Policy: only 20/40 MHz permitted; 2.4 GHz = 20 MHz, 5 GHz must use a regulatory and JCF compatibility-approved pool, with DFS disabled by policy unless separately approved.
7. Backend: ASP.NET Core/.NET 10 service modules, EF Core SQL Server, Razor/Blazor UI, systemd; no Docker. Real authentication/authorization persists.
8. Distinguish proposed, lab-supported, firmware-verified and production-certified capabilities in UI and API.

## Reference documentation
- HPE Aruba Networking AOS-8 Instant REST API 8.10: https://www.arubanetworks.com/techdocs/Aruba-Instant-8.x-Books/810/Aruba-Instant-8.10.0.x-REST-API-Guide.pdf
- HPE Aruba Networking ARM configuration: https://arubanetworking.hpe.com/techdocs/central/latest/content/nms/access-points/cfg/radio-mgmt/conf_arm_features.htm
