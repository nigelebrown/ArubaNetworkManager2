# SRS M01–M26 source implementation and acceptance matrix — 0.6

Terms: **Implemented** = code exists, still requires compiled/integration acceptance; **Partial** = some required code exists; **Not implemented** = no functioning module. No item has received production acceptance in the current runtime.

| Module | Source coverage | Missing/mandatory acceptance |
|---|---|---|
| M01 Local identity, roles, platform | Implemented local Identity/password login, lockout, change password, administrator creation, audit and API auth | MFA, all site-scoped views, pentest, account recovery |
| M02 Locations and physical groups | Implemented locations, group membership, building/floor/zone, historical device placements | Bulk imports, full hierarchy/section integration and permissions |
| M03 Discovery/inventory | Implemented registration, scoped IP allowlist, Instant/CX probe, firmware and status | Serial/MAC-based replacement, bounded IP-range scan and duplicate-serial validation |
| M04 API connectors/capabilities | Implemented Instant and CX read connectors and lab certification registry | All firmware/model read and write API behaviour tests and reviewer sign-off |
| M05 Instant VC/cluster topology | Implemented member enumeration and impact/cluster-count checks | Verified per-member identity, mixed-zone cluster change plan and multi-AP write scope |
| M06 SSID/security/AAA | Partial: approved WPA2+VLAN lab method and proposals | Full WPA3/802.1X/RADIUS/guest/edit/delete, client verification, per-firmware support |
| M07 VLAN/service path | Partial: SSID-to-VLAN proposals and switch VLAN lab path | Trunk/L2 path verification, VLAN/DHCP/DNS/wireless test station |
| M08 RF/ARM controls | Partial: ARM min power/band steering lab method, safe policy, channel/ClientMatch proposals | Firmware-certified ClientMatch/channel policy/per-radio write/readback/restoration |
| M09 AP/radio health | Partial: REST snapshots, nullable RF metrics, historical observations | Radio-specific counters/units, firmware-specific parsing and live quality assurance |
| M10 Client experience | Partial: client summary and HMAC-pseudonymized discovery | Real RSSI/SNR/roaming/auth/retry, synthetic test client |
| M11 Switch topology/VSF | Partial: CX inventory, REST interfaces, topology entity | LLDP/VSF discovery and verified physical uplink mapping |
| M12 Switch VLAN/port/PoE | Partial: lab VLAN and guarded port/PoE proposals; nullable port counters | Safe port/PoE live adapters, uplink protection and rollback certification |
| M13 Switch QoS/security | Not implemented beyond governed policy-model hooks | Firmware-specific QoS, 802.1X, L2 event ingestion, restricted writes |
| M14 Switch telemetry | Partial: read-only interface counters, PoE fields, trend model | AOS-CX parser certification, utilization delta/counter-reset logic |
| M15 Service-path diagnostics | Partial: controlled server-origin TCP diagnostics | Real client DHCP/DNS/gateway/app probes and correlation |
| M16 Security monitoring | Partial: security observation schema and read API | Trusted syslog/IDS/RADIUS feed ingestion, dedup and SIEM integration |
| M17 Telemetry history | Partial: snapshots, nullable quality, hour rollups, 30-day retention | 5-minute rollups, durable multi-node queue, ingestion scaling and history partitions |
| M18 Config templates/snapshots | Partial: non-secret snapshots, SHA256, RF compliance, template model | Encrypted full backups, approvals/exception workflow and tested per-setting restore |
| M19 Rules/optimization | Partial: rules for width/DFS, utilization, missing measurements | Verified multimetric root cause, evidence/persistence, outcomes |
| M20 Analyze Now/hourly | Implemented for single-host application | Scope/window selection, distributed locks and missed job reconciliation |
| M21 Recommendations/approvals | Partial: immutable-payload approvals, dual-review, audit | Location-scoped authorization and more risk policy controls |
| M22 Execution/recovery | Partial: restricted lab adapter, readback/compensation attempts, crash reconciliation | Multi-AP/VSF guarded production writes and full restoration certification |
| M23 Incidents/alerts | Partial: API outage and recovery alerts, incident CRUD/state | Persistence/dedup, scheduled escalation and Teams/ICT task API |
| M24 Dashboard/export | Partial: summary/kiosk, location/detail pages, device CSV, rollup API | Complete RBAC-scoped export and performance at scale |
| M25 Predictive ML | Partial: isolated shadow heuristic only | Trained/validated ML model, drift management and audit |
| M26 Operations/DR/testing | Partial: Linux systemd packaging, reviewed migration scripts, SDK xUnit and static validation | Real SDK compile, signed release, hardware E2E, load/security and SQL AG/DR testing |

Production policy: disable device writes until the live acceptance evidence is signed. The SRS is larger than the API surface confirmed on a single lab AP-505. Neither an API endpoint's documentation nor an unverified HTML form certifies working device control.
