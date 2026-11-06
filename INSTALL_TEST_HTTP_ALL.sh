#!/usr/bin/env bash
# JCF Intelligent Network Manager v0.9.1 - single-command TEST installation.
# Native CentOS Stream 9, SQL Server 2022 Developer (local, non-production),
# real ASP.NET Identity, simulated devices, restricted HTTP LAN access.
# No Docker. Does not change the existing JCF v0.8.2 service.
set -Eeuo pipefail
umask 077
say(){ printf '[JCF 0.9.1 test] %s\n' "$*"; }
die(){ say "STOPPED: $*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || die "Run as root: sudo bash $0"
source /etc/os-release
[[ "${ID:-}" == centos && "${VERSION_ID%%.*}" == 9 ]] || die "CentOS Stream 9 required"
[[ "$(uname -m)" == x86_64 ]] || die "x86_64 is required"
[[ "$(awk '/MemTotal:/{print $2}' /proc/meminfo)" -ge 4000000 ]] ||
  die "SQL Server requires adequate RAM; this test installer requires at least 4 GB"
[[ "$(df -Pk /var | awk 'END{print $4}')" -ge 8000000 ]] ||
  die "At least 8 GB of free storage on /var is required"
command -v ip >/dev/null || die "iproute utilities missing"
SERVER_IP="${JCF_SERVER_IP:-$(ip -o -4 addr show dev ens192 scope global 2>/dev/null | awk 'NR==1{split($4,a,"/");print a[1]}')}"
[[ -n "$SERVER_IP" ]] || SERVER_IP="$(ip -o -4 addr show scope global | awk 'NR==1{split($4,a,"/");print a[1]}')"
python3 - "$SERVER_IP" <<'PY' || die "Invalid server LAN IPv4"
import ipaddress,sys
x=ipaddress.ip_address(sys.argv[1]);assert x.is_private and x.version==4
PY
ip -o -4 addr show scope global | awk '{split($4,a,"/");print a[1]}' | grep -Fxq "$SERVER_IP" ||
  die "Server IP $SERVER_IP is not assigned to this host"
IFACE="$(ip -o -4 addr show scope global | awk -v ip="$SERVER_IP" '{split($4,a,"/");if(a[1]==ip){print $2;exit}}')"
IFACE_CIDR="$(ip -o -4 addr show dev "$IFACE" scope global | awk -v ip="$SERVER_IP" '{split($4,a,"/");if(a[1]==ip){print $4;exit}}')"
if [[ -z "${JCF_CLIENT_CIDR:-}" ]]; then
  CLIENT_CIDR="$(python3 - "$IFACE_CIDR" <<'PY'
import ipaddress,sys
i=ipaddress.ip_interface(sys.argv[1])
print(i.network)
PY
)"
else
  CLIENT_CIDR="$JCF_CLIENT_CIDR"
fi
CLIENT_CIDR="$(python3 - "$CLIENT_CIDR" <<'PY'
import ipaddress,sys
n=ipaddress.IPv4Network(sys.argv[1],strict=False)
assert n.is_private and n.prefixlen>=24, "Use a private /24-/32 for temporary HTTP"
print(n)
PY
)" || die "HTTP client CIDR must be a private /24 or narrower"
say "Target: http://$SERVER_IP (authorized clients: $CLIENT_CIDR)"
say "TEMPORARY TEST ONLY: HTTP exposes login credentials and sessions to observers on the LAN."
say "SQL Server Developer is free for development/testing, not licensed for production."
say "CentOS Stream 9 SQL Server hosting is not officially supported; migrate to supported OS before production."
if systemctl is-active --quiet jcf-network-manager-v091.service; then
  die "Existing v0.9.1 service is active; inspect before changing an active installation"
fi
if [[ -n "$(ss -H -lnt '( sport = :80 )')" ]]; then
  die "Port 80 already has a listener; inspect before installing web proxy"
fi
# No script downloads from untrusted locations. Package repositories are Microsoft/RHEL9.
say "Installing verified application and operating-system prerequisites"
dnf install -y git curl ca-certificates rsync python3 openssl nginx \
  firewalld policycoreutils-python-utils dotnet-sdk-10.0 >/dev/null
systemctl is-active --quiet firewalld || systemctl enable --now firewalld >/dev/null
cd /root
curl -fsSLo /tmp/JCF_NATIVE_091_LATEST.sh \
  https://raw.githubusercontent.com/nigelebrown/ArubaNetworkManager2/main/INSTALL_NATIVE_091.sh
bash -n /tmp/JCF_NATIVE_091_LATEST.sh
bash /tmp/JCF_NATIVE_091_LATEST.sh prepare
ROOT=/opt/jcf-network-manager-v091
SRC="$ROOT/source"
[[ -s "$ROOT/prepared/Jcf.NetworkManager.dll" ]] || die "v0.9.1 build is not prepared"
grep -Fq 'Jcf:TemporaryHttp' "$SRC/src/Jcf.NetworkManager/Program.cs" ||
  die "Application source is missing HTTP opt-in; refusing to expose Identity"
[[ -s "$ROOT/builds/DBA_REVIEW_IDEMPOTENT.sql" ]] || die "Migrations missing"
CFG=/etc/jcfnetwork-v091
SEC="$CFG/secrets"
STATE=/var/lib/jcfnetwork-v091
install -d -m 0750 -o root -g jcfnetwork91 "$CFG" "$SEC"
install -d -m 0700 -o jcfnetwork91 -g jcfnetwork91 "$STATE" "$STATE/keys"
SECRETS_READY=0
if rpm -q --quiet mssql-server; then
  [[ -s "$SEC/sql-sa-password" ]] ||
    die "SQL Server already exists but installer does not own its sa password. No database changes made."
  say "Existing installer-managed local SQL Server found"
else
  say "Installing local SQL Server 2022 Developer edition from Microsoft RHEL 9 repository"
  curl -fsSLo /etc/yum.repos.d/jcf-mssql-server-2022.repo \
    https://packages.microsoft.com/config/rhel/9/mssql-server-2022.repo
  dnf install -y mssql-server >/dev/null || die "Microsoft SQL Server package failed on CentOS Stream 9; inspect DNF output"
  python3 - "$SEC/sql-sa-password" <<'PY'
import secrets,sys,os
p=sys.argv[1]
fd=os.open(p,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
with os.fdopen(fd,"w") as f:f.write("A9!a"+secrets.token_hex(22))
PY
  say "Configuring SQL Server non-interactively"
  MSSQL_PID=Developer ACCEPT_EULA=Y MSSQL_SA_PASSWORD="$(cat "$SEC/sql-sa-password")" \
    /opt/mssql/bin/mssql-conf -n setup >/dev/null ||
    die "SQL Server setup failed. Inspect journalctl -u mssql-server"
fi
# SQL Server must accept only local application connections, never a LAN listener.
say "Restricting SQL listener to localhost"
sudo_sql=/opt/mssql/bin/mssql-conf
"$sudo_sql" set network.ipaddress 127.0.0.1 >/dev/null ||
  die "SQL Server localhost listener configuration failed"
systemctl restart mssql-server.service
for n in {1..30}; do
  if ss -H -lnt '( sport = :1433 )' | grep -q '127.0.0.1:1433'; then break; fi
  sleep 2
done
SQL_LISTEN="$(ss -H -lnt '( sport = :1433 )')"
[[ -n "$SQL_LISTEN" ]] || die "SQL Server never opened its local listener"
if printf '%s\n' "$SQL_LISTEN" | awk '{print $4}' | grep -Evq '^127\.0\.0\.1:1433$'; then
  die "SQL listener is exposed on a non-loopback address; refusing activation"
fi
# Nothing on TCP/1433 is added to firewalld.
say "Installing SQL command-line tools (local only)"
curl -fsSLo /etc/yum.repos.d/jcf-ms-prod.repo \
  https://packages.microsoft.com/config/rhel/9/prod.repo
ACCEPT_EULA=Y dnf install -y mssql-tools18 unixODBC-devel >/dev/null ||
  die "mssql-tools18 installation failed"
SQLCMD=/opt/mssql-tools18/bin/sqlcmd
[[ -x "$SQLCMD" ]] || die "sqlcmd binary missing"
sql_sa(){
  SQLCMDPASSWORD="$(cat "$SEC/sql-sa-password")" "$SQLCMD" \
    -S 127.0.0.1,1433 -U sa -C -b -l 8 -t 60 "$@"
}
sql_sa -Q 'SELECT 1;' >/dev/null || die "Local SQL Server sa login failed"
say "Real local SQL Server is responding; creating least-privilege app login"
if [[ ! -s "$SEC/sql-app-password" ]]; then
  python3 - "$SEC/sql-app-password" <<'PY'
import os,secrets,sys
fd=os.open(sys.argv[1],os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
with os.fdopen(fd,"w") as f:f.write("A9!a"+secrets.token_hex(22))
PY
fi
APP_PW="$(cat "$SEC/sql-app-password")"
SQL_BOOT="$SEC/sql-bootstrap.sql"
cat > "$SQL_BOOT" <<SQL
IF DB_ID(N'JcfNetworkManager091') IS NULL
  EXEC(N'CREATE DATABASE [JcfNetworkManager091]');
GO
IF SUSER_ID(N'jcfapp091') IS NULL
  CREATE LOGIN [jcfapp091] WITH PASSWORD=N'$APP_PW', CHECK_POLICY=ON, CHECK_EXPIRATION=OFF;
GO
USE [JcfNetworkManager091];
GO
IF USER_ID(N'jcfapp091') IS NULL
  CREATE USER [jcfapp091] FOR LOGIN [jcfapp091];
GO
IF IS_ROLEMEMBER(N'db_datareader',N'jcfapp091') <> 1
  ALTER ROLE [db_datareader] ADD MEMBER [jcfapp091];
IF IS_ROLEMEMBER(N'db_datawriter',N'jcfapp091') <> 1
  ALTER ROLE [db_datawriter] ADD MEMBER [jcfapp091];
GO
SQL
chmod 0600 "$SQL_BOOT"
sql_sa -i "$SQL_BOOT" >/dev/null || die "SQL database/login bootstrap failed"
rm -f "$SQL_BOOT"
APP_CONN="Server=127.0.0.1,1433;Database=JcfNetworkManager091;User Id=jcfapp091;Password=$APP_PW;Encrypt=True;TrustServerCertificate=True"
SA_CONN="Server=127.0.0.1,1433;Database=JcfNetworkManager091;User Id=sa;Password=$(cat "$SEC/sql-sa-password");Encrypt=True;TrustServerCertificate=True"
unset APP_PW
say "Applying source-controlled EF Core migrations on the local development database"
# This is a dedicated local TEST DB. No remote database is created or altered.
(
  cd "$SRC"
  export ConnectionStrings__NetworkManager="$SA_CONN"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  dotnet tool restore >/dev/null
  dotnet ef database update --project src/Jcf.NetworkManager/Jcf.NetworkManager.csproj \
    --startup-project src/Jcf.NetworkManager/Jcf.NetworkManager.csproj \
    --context NetworkDbContext --no-build >/dev/null
) || die "EF Core migrations failed; check SQL Server logs"
unset SA_CONN
say "Creating protected Data Protection certificate, persistent keys and bootstrap account"
if [[ ! -s "$SEC/keyring.pfx" ]]; then
  openssl rand -hex 32 > "$SEC/keyring-password"
  TMP="$(mktemp -d /tmp/jcf-native-cert.XXXXXXXX)"
  openssl req -new -newkey rsa:3072 -nodes -x509 -sha256 -days 730 \
    -subj '/CN=JCF-NetworkManager-DataProtection' \
    -keyout "$TMP/key.pem" -out "$TMP/cert.pem" >/dev/null 2>&1
  openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$TMP/cert.pem" \
    -out "$SEC/keyring.pfx" -passout "file:$SEC/keyring-password"
  rm -rf "$TMP"
fi
if [[ ! -s "$SEC/admin-login.txt" ]]; then
  python3 - "$SEC/admin-login.txt" "$SEC/initial-admin-password" <<'PY'
import os,secrets,sys
pw="Aa9!"+secrets.token_hex(20)
for path,value in ((sys.argv[1],"Username: jcfadmin\nPassword: "+pw+"\n"),(sys.argv[2],pw+"\n")):
    fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
    with os.fdopen(fd,"w") as f:f.write(value)
PY
fi
for p in keyring.pfx keyring-password initial-admin-password; do
  chown root:jcfnetwork91 "$SEC/$p"
  chmod 0640 "$SEC/$p"
done
chmod 0600 "$SEC/sql-sa-password" "$SEC/sql-app-password" "$SEC/admin-login.txt"
printf '%s\n' "$APP_CONN" | python3 "$SRC/deploy/native/write_config.py" \
  "$CFG/config.json" "$SERVER_IP" jcfadmin "$STATE/keys" "$SEC/keyring.pfx"
unset APP_CONN
python3 - "$CFG/config.json" <<'PY'
import json,sys
p=sys.argv[1]
with open(p) as f:c=json.load(f)
c["Jcf"]["TemporaryHttp"]=True
c["AllowedHosts"]=c["AllowedHosts"]+";localhost;127.0.0.1"
with open(p,"w") as f:json.dump(c,f,indent=2);f.write("\n")
PY
chown root:jcfnetwork91 "$CFG/config.json"
chmod 0640 "$CFG/config.json"
say "Configuring native systemd service (HTTP stays on 127.0.0.1:5181)"
cat > /etc/systemd/system/jcf-network-manager-v091.service <<EOF
[Unit]
Description=JCF Network Manager v0.9.1 - SQL Identity + simulated devices (test only)
After=network-online.target mssql-server.service
Wants=network-online.target
Requires=mssql-server.service
[Service]
Type=simple
User=jcfnetwork91
Group=jcfnetwork91
WorkingDirectory=$ROOT/prepared
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5181
Environment=JCF_CONFIG_FILE=$CFG/config.json
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=/usr/bin/dotnet $ROOT/prepared/Jcf.NetworkManager.dll
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=strict
ProtectHome=true
ProtectControlGroups=true
ProtectKernelTunables=true
CapabilityBoundingSet=
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
ReadWritePaths=$STATE
UMask=0077
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now jcf-network-manager-v091.service >/dev/null
READY=0
for n in {1..40}; do
  if curl -fsS --max-time 2 http://127.0.0.1:5181/readyz >/dev/null 2>&1; then
    READY=1; break
  fi
  sleep 2
done
if [[ "$READY" != 1 ]]; then
  journalctl -u jcf-network-manager-v091 -n 25 --no-pager | \
    sed -E 's/(Password|Pwd)=[^; ]+/\1=[REDACTED]/Ig' >&2 || true
  die "New SQL/Identity service failed readiness; old v0.8.2 remains untouched"
fi
curl -fsS http://127.0.0.1:5181/healthz | python3 -c '
import json,sys
x=json.load(sys.stdin)
assert x["version"]=="0.9.1" and x["demo"] is False and x["status"]=="up"
' || die "Real SQL-backed v0.9.1 health check failed"
rm -f "$SEC/initial-admin-password"
say "Real SQL-backed Identity is ready; opening restricted HTTP through Nginx"
PROXY=/etc/nginx/conf.d/jcf-network-manager-v091-test.conf
cat > "$PROXY" <<EOF
# TEMPORARY TEST ONLY. HTTP conveys passwords and session cookies in cleartext.
server {
    listen $SERVER_IP:80;
    server_name $SERVER_IP;
    allow $CLIENT_CIDR;
    deny all;
    client_max_body_size 2m;
    add_header X-Content-Type-Options nosniff always;
    location / {
        proxy_pass http://127.0.0.1:5181;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 120s;
    }
}
EOF
nginx -t || die "Nginx validation failed (service was not exposed)"
setsebool -P httpd_can_network_connect on
systemctl enable --now nginx.service >/dev/null
systemctl reload nginx.service
ZONE="$(firewall-cmd --get-zone-of-interface="$IFACE" 2>/dev/null || true)"
[[ -n "$ZONE" && "$ZONE" != "no zone" ]] || ZONE="$(firewall-cmd --get-default-zone)"
RULE="rule family=ipv4 source address=\"$CLIENT_CIDR\" port port=\"80\" protocol=\"tcp\" accept"
firewall-cmd --permanent --zone="$ZONE" --add-rich-rule="$RULE" >/dev/null
firewall-cmd --zone="$ZONE" --add-rich-rule="$RULE" >/dev/null
curl -fsS --max-time 5 -H "Host: $SERVER_IP" "http://$SERVER_IP/healthz" \
  | python3 -c 'import json,sys;x=json.load(sys.stdin);assert x["version"]=="0.9.1" and x["demo"]==False' \
  || die "Local Nginx forwarding check failed. Check journalctl -u nginx"
say "SUCCESS: http://$SERVER_IP/account/login"
say "Real ASP.NET Identity + local persistent SQL Server Developer + simulated AP/switch inventory"
say "Admin username: jcfadmin; protected login: sudo cat $SEC/admin-login.txt"
say "Old v0.8.2 remains on 127.0.0.1:5180. Docker not required."
say "Only $CLIENT_CIDR is allowed through Nginx and firewalld. No remote SQL port is opened."
say "IMPORTANT: move to HTTPS and a supported SQL Server OS before production use."
