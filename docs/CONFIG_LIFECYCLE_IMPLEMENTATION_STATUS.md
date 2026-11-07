# Configuration Lifecycle Addendum — Implementation and Acceptance Status

**Branch:** `feature/ap-wlan-readback-compliance`. **Main:** unchanged; the branch already contains the full `main` history (no merge conflict). **PR:** draft #1.  
**Reference:** [DEVELOPMENT_REQUIREMENTS_ADDENDUM.md](DEVELOPMENT_REQUIREMENTS_ADDENDUM.md).

## Implemented in branch (code + automated tests)

### Existing integrated modules
- ASP.NET Core/Blazor, .NET 10, SQL Server/ASP.NET Identity; native CentOS Stream 9/systemd. Existing demo-device simulation and read-only device API probes retained.
- Aruba AP-505/AP-535 radio/WLAN management, two-person approval workflow, 20/40 MHz-only and DFS exclusions, analysis/recommendation modules.
- Aruba CX 6100/6200 (24/48-port declarations): VLAN, trunk/native VLAN, LACP, LLDP, port admin and PoE planning, switch port/readback dashboards. Firmware-uncertified new actions are **proposal-only** on real equipment.
- Read-only AP/CX configuration evidence, nonsecret sanitized SQL snapshots, draft compliance findings.

### Addendum Sprint 1 — implemented foundations
- **Template library:** `/template-library`; create, clone, revise, retire catalog templates for a specific AP/CX model and, for CX, a declared 24/48-port form factor.
- **Revisions:** Current catalog records and immutable `CatalogHistory` records in the existing SQL `PolicyTemplates` table; retired templates cannot receive new assignments and continue to resolve existing pinned device revisions.
- **Per-device assignment:** `/device-inheritance` allows different global templates per switch/AP at the same site; validates model and declared port-count compatibility.
- **Inheritance:** Deterministic `G → S → D → O` for per-device effective desired configuration. Source, revision, author, reason, timestamp and approval reference shown for each normalized setting. Arrays are replaced atomically; functional profile conflict detection is available as a pure resolver but **profile assignment/storage is not yet built**.
- **Policy guard:** Mandatory inherited global paths, 2.4GHz 20MHz, 5GHz 20/40MHz only, RF power bounds, fixed-versus-ARM desired power mode, DFS compatibility restrictions, SSID security validation, and no raw secrets.
- **Optimization gate:** O-layer override requires an approved recommendation reviewed by a different user, and must exactly match the stored canonical approved non-secret payload.
- **Impact assessment:** Global revision changes leave device pinned versions untouched; calculate per-device changes including added/removed fields and blocked overrides.
- **Two-person promotion:** Request desired-version propagation; first and second approver must be distinct from the requester; before each decision, recompute a SHA-256 hash of the reviewed impact scope to prevent changes after request. Approval only updates SQL desired version pins, **never hardware**.
- **Audit:** Existing append-only `AuditEvents` and versioned `PolicyTemplate` records. Neither configuration intent nor template version approval is a physical execution record.
- **Tests:** automated resolver tests for RF 6–12 → site 8–10 → approved fixed 9 dBm; six mixed CX switches + AP505/AP535 at one site, isolated device override, model/port restrictions, immutable pinned revisions, two-person promotion, approval-scope-change rejection, retired template and optimization payload identity.

**Database:** The above uses existing SQL tables with scoped nonsecret records. A full normalized new EF Core schema for immutable per-setting author/approval history, imported running configurations, backups, device interfaces and deployment plans is **not yet implemented**. Existing app data stays separate from simulated-device responses.

## Partial / not yet implemented (do not present as completed)
| Addendum sprint | Status | Work still needed |
|---|---|---|
| 1: templates & inheritance | **Partial delivered** | Persistent functional profile assignment, per-property revision-reason records, normalized permanent entities, template-level workflow approvals for publication, reviewer-controlled variance history |
| 2: guided site wizard | **Planned** | Site code/formation/addressing, creation wizard, inventory and port/SKU discovery, topology confirmation and full guided flow |
| 3: configuration import | **Planned** | Encrypted full-fidelity versioned backup and restore, unknown command preservation, legacy reconciliation and drift sign-off |
| 4: visual switch port manager | **Partial planning UI** | Exact physical port map, LLDP confidence, bulk naming, profile assignment, CSV/Excel import/export, speed/duplex and verified PoE capabilities |
| 5: AP RF site/optimization | **Partial** | Firmware-verified per-AP vs cluster-scoped tuning, actual ARM-selected RF telemetry, proximity/throughput models, approved RF readback |
| 6: topology & dependency plan | **Planned** | Confirmed LLDP graph, management path safety, validated dependency ordering |
| 7: deployment/rollback | **Not enabled** | Firmware-certified write adapters, backups/checkpoints/timed rollback, per-step verification and emergency stop |
| 8: variance/reporting | **Partial desired provenance** | Imported/deployed/observed state comparison, `I ! P` indicators, PDF/Excel/CSV exports |
| 9: hardware acceptance | **Pending physical test** | Approved real CX 6100/6200 and AP-505/535 model/SKU/firmware lab certification, pilot change rollback, network access and security sign-off |

## Testing and deployment rules
- This is a **feature branch**, not the currently running server release; no automatic merge or deployment to the user's CentOS host.
- Run `dotnet test tests/Jcf.NetworkManager.Tests.csproj -c Release` and inspect GitHub Actions CI.
- Keep live device writes disabled by default. Template assignment, template promotion, site placement and simulated change history must never invoke a hardware adapter.
- Do not treat SSH/CentOS-native SQL Developer HTTP lab configuration as production-grade transport or a supported final SQL Server OS.
- User previously requested native/systemd without Docker. The addendum mentions Docker Compose, so it is recorded as an **optional future packaging alternative**, never installed or made mandatory by this branch.
