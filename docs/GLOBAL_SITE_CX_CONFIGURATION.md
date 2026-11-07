# Global configuration groups, site inheritance, and CX switch planning

**Feature branch:** `feature/ap-wlan-readback-compliance`. **Status:** Branch / review only; no `main` merge or real-device writes.

## User workflow

1. In **Global → Site Configuration**, an authorized PlatformAdmin creates a **global configuration group** with JCF-standard network and WLAN values. These records persist in the existing SQL `PolicyTemplate` table, `Scope=Global`.
2. A NetworkOperator/engineer creates a site/location (or selects an existing registered site), chooses a global group, and creates a **site configuration group**. The site template (`Scope=Site`, `LocationId`) points to the global template ID and stores only site overrides. A companion `DeviceGroup.Kind=SiteConfig` holds group membership.
3. The operator assigns an existing AP-505/AP-535 or CX-6100/CX-6200 to the site. The service updates `Device.LocationId`, `DevicePlacement` effective-dated history, `GroupMembership` and an append-only `AuditEvent`.
4. For CX switches, the operator declares **24 or 48 physical access ports**. The value is stored in `DeviceCapability` as `operator-declared:24` or `operator-declared:48` with `State=Unknown`. It is explicitly not certified device inventory or proof of PoE capabilities.
5. The effective desired policy is computed fresh: **global settings + site-specific overrides**, replacing only the overridden leaf values. WLAN/VLAN arrays are replaced as units; they are not concatenated. Updating a global template raises its version and automatically changes the effective desired policy for all linked sites.
6. The operator opens **CX Switch Configuration**, reviews the effective site policy, and submits switch-specific requests. They are validated, SQL-persisted and require **two independent approvers** through the existing change workflow.

## Global policy schema — non-secret allowlist

`switch`: `dataVlan`, `voiceVlan`, `managementVlan`, `nativeVlan`, `allowedVlans`, `lldpMode`, `poeDefault`, `poePriority`.

`wireless`: `width24GhzMhz` (fixed at 20), `width5GhzMhz` (20 or 40), `allowed5GhzChannels` (approved non-DFS pool), `dfsEnabled` (must be false), `clientMatch`, `bandSteering`, `airtimeFairness`, `minTxPowerDbm`, `maxTxPowerDbm`, `wlanProfiles` (validated SSID/VLAN/security settings, vault **references** only).

Credentials, PSKs, passwords, RADIUS secrets, arbitrary CLI commands, unknown properties, duplicate keys and invalid 20/40 MHz channel combinations are rejected. The template is **desired state**, not evidence of real configuration.

## CX planning actions (proposal-only)

| Action | Fields | Gate |
|---|---|---|
| `PlanCxTrunk` | Physical port, explicit allowed VLANs, native VLAN | Acknowledged impact; native VLAN included; forbid implicit all-VLANs |
| `PlanCxPortAdmin` | Physical port, up/down, description | Acknowledged impact for down |
| `PlanCxLag` | LAG ID, LACP active/passive, 2–8 member ports, native and allowed VLANs | Platform-specific LAG cap, same member/slot in this release, impact acknowledgment |
| `PlanCxLldp` | Physical port, LLDP TX/RX mode | Allowlisted modes only |
| `PlanCxPoePolicy` | Physical port, priority, proposed power cap | Requires exact SKU PoE capability review before physical deployment |

Proposals cannot target an unrecorded 24/48 chassis. Interface numbers beyond the declared copper access port bank are accepted only when the exact port was observed by a recent CX REST inventory read (uplink/SFP slots vary by SKU). This is a **planning gate**, not hardware certification.

The existing VLAN creation, access/trunk VLAN membership and PoE on/off actions retain their previous lab-gated adapter; **new actions have no live-write adapter**. Both the workflow service and gateway independently block real execution.

## Production gaps before enabling hardware writes

- Firmware- and SKU-specific tests with real CX 6100/6200, including 24/48-port and PoE/non-PoE variants.
- Physical uplink/management-path detection and preflight proving the application is not cutting its own management connection.
- LACP peer-state validation, native/allowed VLAN list readback, and exact restoration logic for each AOS-CX version.
- PoE class and per-port total device budget discovery.
- LLDP neighbor inventory readback, port admin description readback and Cx REST API-specific feature tests.
- Change impact graph (switch->uplink->AP/voice) and independently approved maintenance windows.
- Site-baseline drift reconciliation against certified switch/AP readback before any rollout.
- Full production infrastructure: supported OS, proper SQL Server HA/backup, HTTPS/SSO/RBAC hardening.

## SQL schema impact

No EF migration is required: existing `PolicyTemplate`, `DeviceGroup`, `GroupMembership`, `Location`, `Device`, `DevicePlacement`, `DeviceCapability`, `AuditEvent` and `ConfigProposal` entities are used. Global/site edits save prior versions in `PolicyTemplate` `GlobalHistory` and `SiteHistory` records. Site inheritance is evaluated on-demand.

## Validation

`dotnet test tests/Jcf.NetworkManager.Tests.csproj -c Release`; GitHub Actions must pass before review/merge.

Reference: [Aruba CX Switch Software and AOS-CX REST API Guide](https://arubanetworking.hpe.com/techdocs/AOS-CX/).
