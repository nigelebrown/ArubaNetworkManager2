# JCF Network Manager 0.9.1 — Native Production Identity + Simulated Aruba Inventory

Deployment target: **CentOS Stream 9, ASP.NET Core .NET 10 under systemd (no Docker)**.
Identity: **real ASP.NET Core Identity** with secure cookies, role policies and SQL Server user store.
Inventory: **sample simulated Aruba devices** with `IsSimulated=true` and documentation-only management addresses.
Real-device polling and writes: **disabled until certified**.

Read [README_NATIVE_PRODUCTION.md](README_NATIVE_PRODUCTION.md) and follow its two-stage native install.

Use `deploy/native/PREPARE_NATIVE.sh` to build/test/publish and generate DBA-review SQL. After the DBA applies the script to a dedicated SQL database, use `deploy/native/ACTIVATE_NATIVE.sh` to configure the secure service and Nginx TLS.

The currently installed v0.8.2 service is not replaced during preparation or activation; it remains on localhost:5180 as fallback.


## One-command source pull and native preparation

The new repository contains the **complete v0.9.1 source** and a native CentOS Stream 9 installer. On the CentOS server:

```bash
curl -fsSLo /tmp/JCF_NATIVE_091.sh https://raw.githubusercontent.com/nigelebrown/ArubaNetworkManager2/main/INSTALL_NATIVE_091.sh
bash -n /tmp/JCF_NATIVE_091.sh
sudo bash /tmp/JCF_NATIVE_091.sh prepare
```

The `prepare` action compiles the source, executes offline tests and generates a DBA-reviewable SQL migration file. It **does not** overwrite the existing v0.8.2 application or expose insecure demo authentication.

After the DBA reviews and applies the migration and the team supplies an approved SQL Server connection, trusted internal CA-issued HTTPS certificate, DNS name and authorized client CIDRs:

```bash
sudo bash /tmp/JCF_NATIVE_091.sh activate
```

The new SQL-backed application runs at 127.0.0.1:5181 behind Nginx HTTPS on TCP 443; v0.8.2 remains separately at 127.0.0.1:5180. From an approved Windows workstation, use `https://<approved-jcf-hostname>/account/login` (not HTTP port 5180).

Never upload production passwords, TLS private keys, SQL credentials or internal device inventories to this **public** repository.
