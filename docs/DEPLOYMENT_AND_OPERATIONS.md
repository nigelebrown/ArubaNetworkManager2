# Deployment and operational runbook

## Build environment

Use approved .NET 10 SDK, restored NuGet packages from a trusted organization feed, SQL Server 2019/2022, and isolated management network access. Build and test in a controlled environment:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/TEST_ENTERPRISE_WINDOWS.ps1
```

The app source intentionally contains no server credentials or lab AP password. Production certificates must validate or have an approved pinned SHA-256 fingerprint per device secret ref. Do not bypass TLS verification.

## Initial SQL schema

Configure `ConnectionStrings__NetworkManager` with an approved encrypted SQL Server connection. Follow the reviewed migration workflow:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/CREATE_BASELINE_MIGRATION_WINDOWS.ps1
```

Inspect `docs/INITIAL_DB_REVIEW.sql` and generated migrations. Review permissions, indexes and schema naming. Commit approved migration sources. Take a SQL full backup before applying:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/APPLY_REVIEWED_MIGRATIONS_WINDOWS.ps1
```

**No database change occurs automatically on web-service startup.** The service refuses startup if migrations are unapproved/outdated.

## Local account bootstrap

Create an admin username and a strong, unique password in an access-controlled file readable only by the service account. Set `Jcf__BootstrapAdmin__Username` and `Jcf__BootstrapAdmin__PasswordFile` in the protected service environment. Remove/rotate the bootstrap password file after first login and verify the account. The system never ships a preset admin password. Create least-privilege operator/approver accounts via `/admin/users`. Entra/AD may be added through a separately tested OIDC provider later.

## Upgrading from 0.8 to 0.9

1. Back up the database. Have the DBA review and apply `docs/pilot-sql/UPGRADE_0_8_TO_0_9.sql` (adds four nullable/defaulted
   columns to `Devices` and the `DeviceCredentials` table; no data is changed). The application refuses to start until the
   migration is applied. Existing devices become real devices (`IsSimulated=0`).
2. Create a protected Data Protection key directory (service account read/write only, outside the web root, included in
   backups) and set `Jcf:DataProtection:KeysDirectory`. On Windows the keys are DPAPI-protected (machine) by default; on Linux
   set `Jcf:DataProtection:CertificatePath` (+ `CertificatePasswordFile`) to encrypt the key ring with a certificate.
3. If 0.8 used `Jcf:LiveLabWrites`, it still works; new installs use `Jcf:DeviceWrites` (see `docs/DEVICE_ONBOARDING.md` §5).
   `Jcf:EnableDeviceWrites=true` without a target allowlist now stops startup.
4. Optional: `Jcf:Polling:Enabled`, `Jcf:Polling:StartupDelaySeconds`, `Jcf:MaxConcurrentClusterPolls` (1–32), and
   `Jcf:SampleData:Enabled` (unset = follow DemoMode).

## Network device credentials

Preferred (0.9): operators enter credentials as encrypted **credential profiles** under Devices (see `docs/DEVICE_ONBOARDING.md`),
and pin each device certificate from the Devices page. Alternative (0.8 style, still supported): for device `SecretRef=SITE_AP_1`, set `JCF_DEVICE_SITE_AP_1_USER`, `JCF_DEVICE_SITE_AP_1_PASS` and optionally `JCF_DEVICE_SITE_AP_1_CERT_SHA256` in a protected secret source (never source control). Use a dedicated read-only account where supported for polling. For switch configuration lab certification, an audited, restricted privileged account is required.

The AOS-CX 10.09+ login flow documented by HPE carries username/password in the HTTPS login URL query; the connector does not log this URL and redacts transport exceptions. Review and disable full-URL logging in any outbound HTTP proxy or inspection gateway, and use management VLAN ACLs. Device credentials must never be exposed through API reporting.

For cookie-authenticated programmatic writes to `/api/v1`, first GET `/api/v1/security/csrf` as the signed-in operator, then supply the returned value in the `X-CSRF-TOKEN` header alongside the same session cookie. All proposals and approvals still require RBAC; CSRF is not an authorization substitute.

Set `JCF_CLIENT_HASH_KEY` (>=32 random characters) to enable HMAC-pseudonymized client association history. Never persist raw client MAC addresses in production logs/reports.

## Linux / systemd

Publish on the build host:

```bash
dotnet publish src/Jcf.NetworkManager/Jcf.NetworkManager.csproj -c Release -o ./publish
```

Copy publish artifacts to an approved Linux VM. Configure `/etc/jcfnetwork/production.env` with a TLS SQL connection, admin bootstrap path, `Jcf__DemoMode=false`, `Jcf__EnableDeviceWrites=false`, `Jcf__LiveLabWrites__Enabled=false`, `Jcf__MaxConcurrentClusterPolls=4` and `ASPNETCORE_URLS=http://127.0.0.1:5180`. Configure `AllowedHosts` for the approved HTTPS hostname (not `*`); the default application config only admits localhost. Make env file readable only by root and the service group. Deploy using reviewed `scripts/INSTALL_LINUX_SYSTEMD.sh ./publish`. Configure Nginx/HAProxy to terminate HTTPS, forward only from known proxy IP, and block public management network access; protect TLS keys. Validate `/healthz` and `/readyz`.

The installer does **not** automatically start the service, apply schema or enable live writes. Start only after approved database migration, TLS proxy and access controls:

```bash
systemctl start jcfnetwork
systemctl status jcfnetwork
journalctl -u jcfnetwork -f
```

## Daily operating workflow

Register a permitted management address and a nonsecret credential reference. Assign it to the real JCF location; inspect Instant VC members separately from administrative groups. Use read-only API probe, RF metrics, port observations, policy compliance and incidents. Run Analyze Now or schedule hourly checks; review recommendations. Configuration proposals must be approved by an independent account. Production live execution remains disabled pending full certification. Missing RF metrics remain unknown, not zero.

## Recovery

Back up application binaries, service configuration, SQL database and encrypted secrets. Protect/rotate credentials. A failed write or timeout is not automatically retried. Use the execution evidence and device current state to perform a controlled, documented recovery after authorization. Verify both API readback and user service outcomes. Test SQL/AG recovery at least quarterly before production acceptance.
