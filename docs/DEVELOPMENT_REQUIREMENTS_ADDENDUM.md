# Development Requirements Addendum — Global Templates, Site Management, Device Configuration and Safe Deployment

**Application:** JCF Intelligent Network Manager · **Priority:** High  
**Target hardware:** Aruba CX 6100 / CX 6200 (24- and 48-port, actual SKU/PoE/uplink/VSF capabilities subject to discovery), Aruba AP-505 / AP-535.  
**Implementation:** existing C#/.NET 10, Blazor and SQL Server application; independent modules sharing a deterministic configuration/deployment engine. **Keep native CentOS Stream 9/systemd support** from the existing approved implementation. Docker Compose may be added as an optional packaging target, not a prerequisite or a replacement for native deployment. Production requires a supported OS, TLS, secure secret store and validated hardware adapters. Real equipment writes remain disabled until certification and explicit approval.

## 1. Global Configuration Template Library (Sprint 1)
- Create/edit/clone/version/retire multiple named switch/AP templates (for example `SW-ACCESS-24-STANDARD`, `SW-ACCESS-48-STANDARD`, `SW-ACCESS-SPECIAL`, `AP-505-GLOBAL-STANDARD`, `AP-535-GLOBAL-STANDARD`).
- Template sections: VLANs, access/trunk/port roles, PoE, LLDP, LACP, management/security, RF channel/width/power, SSID/VLAN profiles, band steering, airtime fairness and ClientMatch.
- Validate target family, physical port count, capabilities and firmware; never assume PoE or uplink location from nominal access-port count.
- Immutable revisions, named authors, reasons, retirement, capability constraints and audit. No template edit may write to a device.

## 2. Configuration Inheritance (Sprint 1)
**Global template → site settings → device settings → independently approved optimization override**.
- A device chooses its own compatible global template; multiple switches in the same site can use different templates.
- Device may attach additional compatible port/uplink/functional profiles with deterministic priority and conflicts flagged; arrays are replaced as a whole, not concatenated.
- Effective setting must expose source **G / S / D / O / I / ! / P**, revision, author, reason, timestamp, approval and history.
- Mandatory global security restrictions are non-overridable. JCF RF widths are strictly 20 MHz on 2.4 GHz and 20/40 MHz on 5 GHz; 80/160 prohibited; enforce channel exclusions and legal regulatory domain.
- Pin each device to an approved template version. Editing global templates generates impact to sites and devices plus conflicting overrides, not automatic propagation. Every rollout requires approval.

## 3. Guided Site Wizard (Sprint 2)
1. Create site (code, name, location, formation, buildings, addressing and metadata).
2. Register or authorize read-only discovery (model, hostname, management IP, serial, firmware, port inventory, Instant cluster); assign existing inventory without reset.
3. Discover topology using supported LLDP/interface state: switch↔switch, switch↔AP ports, LAGs, trunks, redundant paths, management dependencies. Unverified links need manual confirmation; editable topology.
4. Choose approved device-specific global template for each AP/switch.
5. Enter justified site overrides and display inherited settings.
6. Configure individual device interfaces/radios with individual overrides.
7. Preview resolved policy, support, differences and connectivity impact.
8. Approve and execute dependency-aware, controlled deployment when certified.
9. Readback, service tests, drift evaluation, audit and result.
- Target mandatory acceptance: 6 mixed switches and multiple APs at a site; different global switch templates within the same site; one device override leaves others unchanged.

## 4. Existing Configuration Import and Reconciliation (Sprint 3)
- Securely collect running configuration + operational values; encrypted, timestamped, access-controlled **full-fidelity** prechange backups kept outside sanitized readback evidence.
- Parse and normalize known VLAN/SSID/interface/uplink/LAG/RF/management fields; unknown commands retained and flagged.
- Preview differences between imported running state and desired global/site/device standard.
- Explicit choices: retain current, adopt as scoped override, replace with standard. No silent device changes.
- Preserve source, provenance, timestamp, restore method, before/after and lifecycle audit.

## 5. Visual CX Port Manager and Naming (Sprint 4)
- Display actual enumerated physical ports for 24/48 copper banks, uplinks, supported stack/VSF interfaces and LAGs (not made-up interface identifiers).
- Each port: ID, friendly description, admin/oper state, speed/duplex/errors/utilization, access/native/tagged VLANs, PoE class/state/usage/budget, LLDP neighbor, connected device, role/uplink, LAG and effective profile origin.
- Naming templates: `UPLINK-TO-SW02`, `AP-RECEPTION-01`, `DATA-ADMIN-01`, `VOICE-OPERATIONS-01`, `PRINTER-GENOFFICE-01`, `CAMERA-ENTRANCE-01`, `SPARE`.
- Suggest/bulk rename, assign port profile, compare against inherited, reset override; confidence-driven naming suggestions from LLDP, operator confirmation for unknown endpoints; CSV/Excel import/export.

## 6. AP Inheritance and RF Optimization (Sprint 5)
- Site/global standard SSID, VLAN, radio/ARM bounds, RF zones, cluster and physical placement; per-AP customization only if firmware and cluster scoping permit.
- Support global RF power `6–12 dBm`, site `8–10 dBm`, with separately reported actual ARM-selected power. Fixed radio value is distinct from min/max bounds.
- Analyze Now and hourly advisory runs using measured client distribution, RF utilization, interference, retries, throughput, coverage and AP proximity (confidence-rated). No synthetic metric presented as live.
- Proposed benefit, confidence, affected cluster/APs, risks, before/after, approval and verified rollback. Independent manual approval mandatory; no autonomous writes.

## 7. Configuration Variance Dashboard (Sprint 8)
- Per-property columns Global, Site, Device, Optimization, Effective, Actual, Provenance/Approval with indicators **G, S, D, O, I, !, P**.
- Filters for changed-only, optimization overrides, violations, unresolved drift, pending rollout; drilldown provenance and approval; export PDF/CSV/Excel.
- Examples: RF global 6–12, site 8–10, observed 8 dBm => S; fixed per-AP 9 dBm after approval => O; VLAN 300 desired but absent live => !.
- No invented actual values; only firmware-verified readback can establish deployed state.

## 8. Dependency-Aware Deployment (Sprints 6–7)
- Preflight: version/capability, reachability, live drift, topology, management VLAN/uplink/gateway dependencies, approved dry run, secure backup and firmware-tested checkpoint/timed rollback.
- Graph-based sequence, not a fixed order. Add upstream VLAN/trunk capacity before downstream activation; disruptive changes target downstream-first where possible; LAG, management and removal require special impact plans.
- Execute one device/controlled batch at a time: backup → validate → protect rollback → write *certified differences only* → test management reachability → verify services → readback → proceed. Stop on critical failure.
- On loss of management or ambiguous result: stop, mark **ManualInterventionRequired**, do not label successful or promise zero interruption; restore only with tested firmware-supported procedures.
- Report per-device and site results, before/after changes, observed status, rollback outcome and audit.
- Aruba Instant writes must respect VC/cluster and firmware supported scope.

## 9. Separate but Integrated UI Modules
1. Global Template Library
2. Site Manager
3. Site Configuration Wizard
4. Switch Port Manager
5. AP Configuration Manager
6. Configuration Import & Reconciliation
7. Topology & Dependency View
8. Configuration Comparison
9. Optimization Recommendations
10. Deployment Manager
11. Configuration Audit & Reporting

## 10. Data and Layering
Desired **must not** be conflated with proposed, deployed or observed. Use SQL persisted Sites, SiteGroups, Devices, DeviceInterfaces, DeviceLinks, TemplateGroups, Templates, TemplateVersions, TemplateAssignments, Settings, Overrides, ImportedConfigurations, PortProfiles, Recommendations, Approvals, DeploymentPlans, Steps, Backups, Drift and Audit (reuse existing compatible tables; additive EF migrations for missing permanent models). Store resolved desired state and provenance for an approved deployment version. Pluggable model/firmware adapters and capability matrix; unsupported operations must fail closed.

## 11. Incremental Development Plan
| Sprint | Deliverable |
|---|---|
| 1 | Template library, immutable versions, inheritance, device-specific assignment, provenance/impact preview |
| 2 | Guided site creation, per-device template assignment, inventory and device discovery |
| 3 | Existing configuration import, reconciler, drift evidence and secure backups |
| 4 | CX 24/48 port map, naming and profiles, CSV/Excel |
| 5 | AP RF site/device/optimization overrides, Analyze Now, scheduled analysis |
| 6 | Verified topology, impact and dependency ordering |
| 7 | Guarded deployment engine, approvals, rollout, checkpoints, verification and rollback |
| 8 | Variance/drift dashboards, audit and PDF/CSV/Excel |
| 9 | End-to-end integration, AP-505/535 and CX-6100/6200 hardware firmware matrix, pilot tests |

## 12. Mandatory Acceptance
1. Site with six assorted CX 6100/6200 and multiple APs.
2. Different compatible global templates per switch at the same site.
3. Scoped per-device VLAN/port override does not mutate neighboring switches.
4. AP RF 6–12 globally, 8–10 site-level, with provenance.
5. Individual AP optimization approval, reason, before/after.
6. Read-only, recoverable legacy configuration import.
7. Every physical port, uplink and confirmed LLDP neighbor; bulk naming.
8. Effective/actual and G/S/D/O/I/!/P variance.
9. Actual topology-derived deployment plan.
10. Stop-and-rollback on verification failure.
11. Immutable history and provenance after global revisions.
12. RBAC, approval, firmware compatibility and unauthorized-write prevention.

**Development directive:** Extend the *existing* Intelligent Network Manager on feature branches, with automated tests per sprint and acceptance gates. Existing monitoring and optimization stay functional. No live modifications without explicit operator approval, independently verified capabilities, prechange backup, readback and tested recovery.
