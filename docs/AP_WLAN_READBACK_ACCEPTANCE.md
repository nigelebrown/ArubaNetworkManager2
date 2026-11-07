# AP WLAN readback and compliance — feature branch acceptance

**Branch:** `feature/ap-wlan-readback-compliance` (not production-accepted).

## Scope delivered in this branch

1. Aruba Instant AOS-8 WLAN readback from the **existing** authenticated `show running-config` GET operation via Instant REST API. There are no additional device CLI commands, scans, or write operations.
2. Strictly allowlisted and bounded projection of SSID profile name, broadcast ESSID, numeric VLAN (or *unknown*), `opmode` token, band, WLAN type, explicitly reported enablement, explicit guest intra-VLAN denial, and dynamic VLAN presence. Passwords, RADIUS server identities and full CLI output are neither returned nor stored.
3. SQL `ConfigurationSnapshot` with `instant-wlan-readonly` source marker, `Uncertified` quality, SHA-256 digest and deduplication. Ambiguous, oversized or unrecognized parses are rejected; no schema migration is required.
4. Role-restricted, deliberate **Read AP now** action in `/wlan-readback`; shows saved evidence, firmware, freshness, null/unknown fields, and proposal status.
5. Read-only advisory findings: explicitly open `opmode`, VLAN 1 review, explicit guest intra-VLAN denial absent, unknown VLAN/security mode, and attribute-based VLAN warning. These are **advisories**, not automated fixes or attestation.
6. Tests for indented and flat AOS-8 configuration styles, secret redaction, missing values, abnormal inputs, and SQL snapshot behavior.

## Firmware and operation safeguards

- This is a typed CLI *projection*, **not firmware-certified parsing**. Aruba Instant AP-505/AP-535 images and their `show running-config` variations must be tested separately, including the known AP-505 Instant 8.10.0.21 lab baseline.
- No real Aruba config changes were enabled. Existing new WLAN/ARM actions remain proposal-only for real hardware; write gate, model/firmware certification, two-person approval and rollback are unchanged.
- Preserve **20/40 MHz only**; 2.4 GHz stays 20 MHz. Never enable 80/160 MHz or DFS channels excluded by JCF compatibility policy. Jurisdiction/regulatory domain still controls legal channels.
- Never equate **missing** `deny-intra-vlan-traffic` with disabled guest isolation: a global policy may apply. The compliance rule flags only explicit `no deny-intra-vlan-traffic`.
- Do not infer client effective VLAN from the static SSID `vlan` when `set-vlan` attribute-based rules are present.
- A parsed `opmode` token does not prove successful WPA3 or RADIUS client authentication; verify on a real client with a separate test plan.
- Secrets may occur in the original CLI response. Connector parses the response transiently and persists **only** allowlisted, bounded fields. Never log the raw response.
- The native v0.9.1 systemd deployment, real SQL Server/ASP.NET Identity accounts, simulated inventory and temporary HTTP lab proxy are unchanged. Before production move to HTTPS and supported SQL Server operating system.

## Hardware acceptance gates still required

- Run firmware-specific fixtures against real AP-505 and AP-535 with approved lab credentials; demonstrate no leaks and accurate SSID VLAN state.
- Validate per-Virtual Controller scope, multi-AP zone membership, and actual client DHCP/DNS/authentication outcomes.
- Certify physical write/readback/restore adapters before enabling any real SSID, ARM or radio change.
- Review client-network RBAC, authentication, authorization, transport security, failure behavior and source retention.
- Keep all AP writes disabled until operational acceptance.

## Documentation source

HPE Aruba Networking Instant CLI Bank, `wlan ssid-profile` and `show configuration`:
- https://arubanetworking.hpe.com/techdocs/CLI-Bank/Content/instant/wlan-ssid-prof.htm
- https://arubanetworking.hpe.com/techdocs/CLI-Bank/Content/instant/showconfigur.htm
