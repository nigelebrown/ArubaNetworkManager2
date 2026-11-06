# JCF Intelligent Network Manager 0.9.1 — Native CentOS 9 + real SQL Identity + simulated inventory

## Definition

This is **not** the insecure demo-persona system. It runs the actual ASP.NET Core application in `Production`, authenticates real users with ASP.NET Core Identity stored in the external SQL Server database, and populates SQL with **sample, simulated** Aruba AP-505, AP-535 and CX 6100 equipment. The samples are tagged `IsSimulated=true` and use RFC 5737 non-routable 192.0.2.x addresses. Approved operations on these sample devices go to the in-process simulator, not Aruba REST. SQL users, approval records, device/sample inventory, events and audit records persist in SQL.

**Safety defaults:** `DemoMode=false`; `SampleData.Enabled=true`; `Polling.Enabled=false`; `DeviceWrites.Enabled=false`; `EnableDeviceWrites=false`; `LiveLabWrites.Enabled=false`. No real-device scan, poll or configuration write is performed by the deployment. Do not enable live-device actions before firmware/model-specific acceptance and change approval.

The shipped code uses the exact application source from the user's uploaded v0.9.0, with two narrowly scoped modifications: (1) optional root-managed JSON secret configuration and (2) fail-closed Data Protection encryption requirements in Production. Release version 0.9.1 reflects these changes.

## Requirements

- CentOS Stream 9 x86_64 with root/sudo, outbound NuGet access for .NET build, and access to a **separate** SQL Server with validated TLS certificate.
- DBA-created dedicated database, permissions appropriate to identity/application operation, backup and approved schema changes.
- JCF internal DNS hostname resolving to the server LAN IP and a certificate issued by a JCF-trusted CA matching the hostname.
- Running firewalld and authorized source IP/CIDR list; 443 unoccupied.
- The existing healthy v0.8.2 systemd service on 127.0.0.1:5180 is preserved as rollback until acceptance.

## Phase A — Build/test + DBA migration review (does not touch old service)

Copy the supplied ZIP onto your CentOS server, for example with SCP from Windows:

```powershell
scp .\JCF_Network_Manager_v0_9_1_NATIVE_SQL_IDENTITY.zip root@SERVER_IP:/root/
```

Run on the CentOS server:

```bash
sudo dnf install -y unzip
sudo mkdir -p /root/jcf-native-091
sudo unzip -o /root/JCF_Network_Manager_v0_9_1_NATIVE_SQL_IDENTITY.zip -d /root/jcf-native-091
sudo bash /root/jcf-native-091/ArubaNetworkManager/deploy/native/PREPARE_NATIVE.sh
```

The prepare phase restores packages, compiles the **real .NET 10 application**, runs its .NET test project, publishes the release and generates:

`/opt/jcf-network-manager-v091/builds/DBA_REVIEW_IDEMPOTENT.sql`

**DBA step:** Review and apply this SQL script to an approved dedicated SQL Server database. No automatic migration occurs at application startup. Back up the database first. Require a valid SQL Server TLS certificate and `Encrypt=True;TrustServerCertificate=False` in the connection string.

## Phase B — Configure SQL Identity and HTTPS after DBA approval

```bash
sudo bash /root/jcf-native-091/ArubaNetworkManager/deploy/native/ACTIVATE_NATIVE.sh
```

The interactive script asks for HTTPS FQDN, host IPv4, allowed JCF client CIDRs, trusted CA-issued PEM certificate and key, initial admin username and strong password, and SQL connection string (hidden input). No credentials belong in GitHub. The script stores the SQL connection only in a root-controlled JSON file readable by the dedicated application identity. Initial admin password is removed after SQL-backed startup succeeds.

The script starts a NEW systemd service, `jcf-network-manager-v091`, on **127.0.0.1:5181**, verifies SQL readiness, then configures Nginx TLS 443 and source-IP firewall rules. The old v0.8.2 service stays running on 5180. It does not install Docker.

## Verification and rollback

```bash
systemctl status jcf-network-manager-v091 --no-pager
curl -fsS http://127.0.0.1:5181/healthz
curl -fsS -H 'X-Forwarded-Proto: https' http://127.0.0.1:5181/readyz
nginx -t
# From an allowed workstation with JCF CA certificate trusted:
# https://YOUR_APPROVED_FQDN/account/login
```

`/healthz` must return version **0.9.1** and `"demo":false`; `/readyz` must return `{"status":"ready"}`. Login must show real ASP.NET Identity login, not a demo-persona selector. The inventory must list sample AP-505/AP-535/CX 6100 entries marked simulated. Verify normal nonadmin access restrictions, lockouts, CSRF and authorization before exposing broadly.

If the new system fails, the older instance remains at `127.0.0.1:5180`; disable the new Nginx site and the `jcf-network-manager-v091` service **without deleting SQL data or the Data Protection keys**. Full SQL/keyring backup and tested restore are essential, especially for credential vault data. The generated Data Protection encryption certificate should be rotated by an approved process and archived with recovery controls.

## Production acceptance limitations

This package provides production-oriented hosting, security controls, SQL persistence, and actual Identity users, **but is not automatically certified for operational deployment**. Execute live .NET build/tests, SQL migration, login/logout/role tests, browser HTTPS test, independent security assessment, firewall change review, backups/restore drill, HA/DR review and load tests on site. It does not integrate Entra/AD SSO; local ASP.NET Identity is the implemented login method.
