# JCF Network Manager 0.9.1 — Native Production Identity + Simulated Aruba Inventory

Deployment target: **CentOS Stream 9, ASP.NET Core .NET 10 under systemd (no Docker)**.
Identity: **real ASP.NET Core Identity** with secure cookies, role policies and SQL Server user store.
Inventory: **sample simulated Aruba devices** with `IsSimulated=true` and documentation-only management addresses.
Real-device polling and writes: **disabled until certified**.

Read [README_NATIVE_PRODUCTION.md](README_NATIVE_PRODUCTION.md) and follow its two-stage native install.

Use `deploy/native/PREPARE_NATIVE.sh` to build/test/publish and generate DBA-review SQL. After the DBA applies the script to a dedicated SQL database, use `deploy/native/ACTIVATE_NATIVE.sh` to configure the secure service and Nginx TLS.

The currently installed v0.8.2 service is not replaced during preparation or activation; it remains on localhost:5180 as fallback.
