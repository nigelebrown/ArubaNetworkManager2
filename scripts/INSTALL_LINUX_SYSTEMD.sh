#!/usr/bin/env bash
set -euo pipefail
# Deploy only reviewed .NET 10 publish output. Requires externally provisioned SQL, TLS reverse proxy and secret files.
[[ "$(id -u)" -eq 0 ]] || { echo 'Run as root on an approved application VM'; exit 2; }
[[ "${JCF_DEPLOY_APPROVED:-}" == 'YES' ]] || { echo 'Set JCF_DEPLOY_APPROVED=YES after CAB and DBA approval'; exit 2; }
PUBLISH_DIR="${1:?Usage: INSTALL_LINUX_SYSTEMD.sh /path/to/published-output}"
[[ -f "$PUBLISH_DIR/Jcf.NetworkManager.dll" ]] || { echo 'Missing published .NET application'; exit 2; }
command -v dotnet >/dev/null || { echo 'Install the approved .NET 10 ASP.NET runtime first'; exit 2; }
id -u jcfnetwork >/dev/null 2>&1 || useradd --system --shell /usr/sbin/nologin --home /var/lib/jcfnetwork jcfnetwork
install -d -m 0750 -o jcfnetwork -g jcfnetwork /opt/jcfnetwork /var/lib/jcfnetwork
install -d -m 0750 -o root -g jcfnetwork /etc/jcfnetwork
[[ -f /etc/jcfnetwork/production.env ]] || { echo 'Provision /etc/jcfnetwork/production.env with SQL connection, bootstrap path, URL binding; permissions 0640';exit 3; }
chmod 0640 /etc/jcfnetwork/production.env
cp -a "$PUBLISH_DIR"/. /opt/jcfnetwork/
chown -R root:jcfnetwork /opt/jcfnetwork
chmod -R go-w /opt/jcfnetwork
cat >/etc/systemd/system/jcfnetwork.service <<'UNIT'
[Unit]
Description=JCF Intelligent Network Manager
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
User=jcfnetwork
Group=jcfnetwork
WorkingDirectory=/opt/jcfnetwork
EnvironmentFile=/etc/jcfnetwork/production.env
Environment=ASPNETCORE_ENVIRONMENT=Production
ExecStart=/usr/bin/dotnet /opt/jcfnetwork/Jcf.NetworkManager.dll
Restart=on-failure
RestartSec=8
TimeoutStopSec=35
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/jcfnetwork
RestrictSUIDSGID=true
LockPersonality=true
MemoryDenyWriteExecute=true
[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable jcfnetwork.service
# Service intentionally not started: run reviewed SQL migration and configure TLS reverse proxy first.
echo 'Installed. Verify reviewed database migrations, SQL AG access and TLS reverse proxy, then systemctl start jcfnetwork.'
