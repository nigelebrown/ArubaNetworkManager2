# Change log

## 0.9.0 — Real-device integration in every installation (6 Nov 2026)

Verification: 124/124 automated tests (27 new), 38/38 browser steps in headless Chrome, 35/35 live steps against
network-level Aruba Instant / AOS-CX emulators over TLS, 24/24 static checks, EF model has no pending changes.
See `docs/RELEASE_0_9_TEST_REPORT.md`.

Changed (behaviour)
- **Demo data no longer means "simulated everything".** Devices carry `IsSimulated`. Sample devices (192.0.2.0/24) use
  the simulator; every registered device uses the real Aruba connectors, in demo mode too. Execution gates are keyed on
  the target device, not on `Jcf:DemoMode`: a real device in a demo install gets every production control.
  0.8 demo seeds occupied 10.250.2.71 (the real lab AP), which blocked registering it.
- The scheduled poller runs in all modes, starts 20 s after launch, and polls APs without a cluster name independently
  (0.8 polled them all serially in one group). Scheduled polls no longer write an audit row each (hundreds of thousands a day at fleet scale).
- Device writes: `Jcf:DeviceWrites:Enabled` + `AllowedCidrs`/`AllowedIps` + `AllowClusterWide`. The 0.8 `LiveLabWrites` keys still work.
  `Jcf:EnableDeviceWrites` on its own is refused at startup.

Added
- Encrypted credential profiles (ASP.NET Core Data Protection) managed in the UI/API; environment variables still supported.
- Device certificate capture (no credentials sent) and per-device SHA-256 pinning.
- Device edit, enable/disable, remove; bulk CSV import; capability certification API.
- Change execution for ClientMatch, 20/40 MHz non-DFS channel policy, CX port access/trunk VLAN and PoE, with baseline,
  readback and verified restore; trunk-flattening and all-VLAN-trunk safeguards.
- Instant cluster-wide changes with blast-radius check: live member count must equal the approved count.
- `POST /api/v1/change-proposals/{id}/execute`, alert acknowledge/resolve, admin sample-data remove/load.
- UI: Devices page rebuilt (credentials, certificates, lifecycle, import); switch interface tables; alert triage;
  incident create/transition; all eight actions in capabilities/configuration; Instant scope shown on proposals.
- Migration `DeviceIntegration` (additive) and DBA scripts `UPGRADE_0_8_TO_0_9.sql`, `SCHEMA_0_9_FULL_IDEMPOTENT.sql`.
- `scripts/emulator/`: network-level Aruba emulator and live operator walk-through for training and regression.

Fixed
- **80 MHz drift never detected on real APs.** The ARM parser ignored bare CLI flags (`80mhz-support`, `client-match`),
  so the analysis and compliance rules could only fire on demo data.
- **Device JSON bodies were sent chunked** (`PostAsJsonAsync`), which embedded device web servers commonly reject. All
  device requests now carry Content-Length.
- **Administration page never became interactive**: `blazor.web.js` was loaded by a relative path without `<base href>`,
  so nested routes requested `/admin/_framework/...` (404).
- Executive display loaded the entire telemetry table every 10 s; now the latest sample per AP from the last hour.
- Re-enabling a device left its status "Disabled".
- Probe errors now say what to do (certificate not trusted, port unreachable, credentials rejected, profile missing) without exposing secrets.

## 0.8.1 — End-to-end verification and fixes (5 Nov 2026)

First release that has actually been compiled and executed: .NET 10 build, 97 xUnit tests, 25-step browser E2E, SQL Server model/migration generation. See `docs/END_TO_END_TEST_REPORT_v0_8_1.md`.

Fixed
- **Lab write gateway rejected every real Aruba Instant login.** It compared the login `Status` case-sensitively to `"success"`; Instant replies `"Success"`. The read connector already compared case-insensitively. `"Failed"` write replies are now also detected case-insensitively.
- **Lab write rollback skipped when the request was cancelled.** If the operator's request was cancelled after the AP applied a change, compensation reused the cancelled token and returned "rollback unverified". Rollback now runs on its own 90 s token.
- **Concurrent Execute of one approved change.** On SQL Server the existing `rowversion` blocked the second write but surfaced as HTTP 500; on the in-memory store both requests reached the device gateway. `ConfigProposal.State` is now also a concurrency token and the losing request gets a clear 409 conflict.
- **Browser E2E launcher (and CI `browser-e2e` job) ran the app outside its content root**, so `appsettings.json` was never loaded, the management-CIDR allowlist was empty and 7/25 UI steps failed. Script fixed; the app now refuses to start if `Jcf:AllowedManagementCidrs` is empty or malformed.
- **Retention purge scanned whole telemetry tables.** Added time indexes; purge now deletes in 4,000-row batches (`DELETE TOP(@p)`).
- SQL Server schema: enum-as-text columns bounded to `nvarchar(48)` (were `nvarchar(max)`); explicit precision on 9 measurement decimals (removed all EF `DecimalTypeDefaultWarning`s).
- Duplicate Content-Security-Policy header (Blazor's `frame-ancestors 'self'` alongside the app's `'none'`) removed.
- Sidebar said "Enterprise Source 0.7"; `/healthz`, sidebar and device User-Agent now all derive from the assembly version.
- `scripts/verify_enterprise_source.py` linted `bin/obj` output and failed on EF-generated `#nullable` migrations; it now lints hand-written source only and tolerates preprocessor directives.

Added
- EF Core baseline migration `InitialProduction` (generated with EF Core 10.0.0 design-time services; no pending model changes) and DBA review script `docs/pilot-sql/INITIAL_SCHEMA_REVIEW.sql` (idempotent). `PREPARE_AND_VALIDATE_PILOT_SQL_WINDOWS.ps1` detects the existing migration and only regenerates the SQL.
- 22 new tests: simulated Aruba Instant / AOS-CX transport tests for both connectors and the lab gateway (login, reads, CSRF, write, readback, restore, cancellation, multi-member block, logout), concurrency, CIDR validation, startup guard, CSP/version, production DI wiring.
- `DeviceHttpFactory` test seam (optional transport override; not registered in DI).

Unchanged: device writes remain disabled by default; no hardware or live SQL Server was contacted.

## 0.8.0 — Real SQL Server / Aruba read-path acceptance increment (5 Nov 2026)

- Adds fail-closed, `PlatformAdmin`-only hardware acceptance service against explicitly allowlisted AP/CX management IPs.
- Records device API and independent SQL-persistence evidence, cluster members/port observations and a durable acceptance run.
- Adds pilot SQL schema readiness, approval-gated migration script and standalone Windows/Python acceptance procedures.
- Serializes same-device REST probes within an app instance to avoid overlapping read sessions.
- Preserves existing features and vendor-integration safety. No AP/switch configuration change is performed by the pilot acceptance script.
- Production writes remain disabled by default, without implying that all 26 SRS modules are complete.

See `docs/RELEASE_0_8_SQL_HARDWARE_ACCEPTANCE.md` and `docs/RELEASE_0_8_RESULT_AND_GAPS.md`.
