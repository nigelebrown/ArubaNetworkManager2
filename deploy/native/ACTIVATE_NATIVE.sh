#!/usr/bin/env bash
# Real ASP.NET Identity + externally managed SQL Server + sample device inventory.
# Native systemd .NET 10 on CentOS Stream 9, Nginx HTTPS, NO Docker.
set -Eeuo pipefail
umask 077
fail(){ echo "[JCF Activation] STOPPED: $*" >&2; exit 1; }
info(){ echo "[JCF Activation] $*"; }
[[ $EUID -eq 0 ]] || fail 'Run with sudo/root.'
source /etc/os-release
[[ "${ID:-}" == centos && "${VERSION_ID%%.*}" == 9 ]] || fail 'CentOS Stream 9 required.'
ROOT=/opt/jcf-network-manager-v091
CFG=/etc/jcfnetwork-v091
STATE=/var/lib/jcfnetwork-v091
SERVICE=jcf-network-manager-v091
[[ -f "$ROOT/prepared/Jcf.NetworkManager.dll" ]] || fail 'Run PREPARE_NATIVE.sh first.'
[[ -f "$ROOT/builds/DBA_REVIEW_IDEMPOTENT.sql" ]] || fail 'DBA-reviewed migration SQL not generated.'
systemctl is-active --quiet jcf-network-manager.service || fail 'Existing v0.8.2 fallback is not running. Resolve before proceeding.'
[[ -z "$(ss -H -lnt '( sport = :5181 )')" ]] || fail '5181 already in use; no service will be replaced.'
[[ -z "$(ss -H -lnt '( sport = :443 )')" ]] || fail '443 already in use; review existing HTTPS service before proceeding.'
command -v firewall-cmd >/dev/null || fail 'firewalld is required; install/activate it through approved change control first.'
firewall-cmd --state >/dev/null || fail 'firewalld must already be running; avoid unexpectedly changing SSH firewall access.'

read -r -p 'JCF HTTPS hostname (must match internal CA certificate): ' HOST
[[ "$HOST" =~ ^[A-Za-z0-9][A-Za-z0-9.-]{2,250}$ && "$HOST" == *.* ]] || fail 'Use a valid FQDN.'
ip -o -4 addr show scope global | awk '{print "Server IP: " $2 " " $4}'
read -r -p 'Server interface IPv4 to bind HTTPS: ' SERVER_IP
python3 - "$SERVER_IP" <<'PY' || fail 'Invalid bind IP.'
import ipaddress,sys
x=ipaddress.ip_address(sys.argv[1]);assert isinstance(x,ipaddress.IPv4Address) and x.is_private
PY
ip -o -4 addr show scope global | awk '{split($4,a,"/");print a[1]}' | grep -Fxq "$SERVER_IP" || fail 'IP not assigned to host.'
IFACE=$(ip -o -4 addr show scope global | awk -v ip="$SERVER_IP" '{split($4,a,"/");if(a[1]==ip){print $2;exit}}')
read -r -p 'Approved client subnets, comma-separated IPv4 CIDRs (e.g. 192.168.10.0/24): ' NETWORKS
CIDRS=$(python3 - "$NETWORKS" <<'PY'
import ipaddress,sys
raw=[s.strip() for s in sys.argv[1].split(',')]
if not 1<=len(raw)<=30:raise SystemExit('Provide 1-30 source networks')
for item in raw:
    n=ipaddress.IPv4Network(item,strict=False)
    if not n.is_private or n.prefixlen<16:raise SystemExit('Use private source networks /16-/32 only')
    print(n)
PY
) || fail 'Invalid client allowlist; no broad access permitted.'

read -r -p 'Existing internal-CA TLS certificate full-chain PEM path: ' TLS_CERT
read -r -p 'Matching TLS private key PEM path: ' TLS_KEY
[[ -r "$TLS_CERT" && -r "$TLS_KEY" ]] || fail 'Trusted TLS certificate/key required; no self-signed HTTPS fallback.'
openssl x509 -in "$TLS_CERT" -checkend 86400 -noout >/dev/null || fail 'TLS certificate invalid/expiring.'
openssl x509 -in "$TLS_CERT" -checkhost "$HOST" -noout | grep -q 'does match' || fail 'Certificate does not cover hostname.'
CERT_HASH=$(openssl x509 -in "$TLS_CERT" -pubkey -noout | openssl sha256)
KEY_HASH=$(openssl pkey -in "$TLS_KEY" -pubout | openssl sha256)
[[ "$CERT_HASH" == "$KEY_HASH" ]] || fail 'TLS certificate and private key do not match.'

read -r -p 'Initial platform administrator username: ' ADMIN
[[ "$ADMIN" =~ ^[a-zA-Z][a-zA-Z0-9._-]{3,50}$ ]] || fail 'Invalid admin name.'
read -r -s -p 'Initial administrator password (14+ chars, mixed types; not echoed): ' ADMINPASS; echo
[[ ${#ADMINPASS} -ge 14 && "$ADMINPASS" =~ [A-Z] && "$ADMINPASS" =~ [a-z] && "$ADMINPASS" =~ [0-9] && "$ADMINPASS" =~ [^a-zA-Z0-9] ]] || fail 'Admin password does not meet Identity policy.'
read -r -s -p 'SQL connection string (TLS verified; hidden input): ' SQL; echo
printf '%s' "$SQL" | python3 -c 'import sys,re; s=sys.stdin.read(); assert re.search(r"(?i)(?:^|;)\s*(?:Encrypt)\s*=\s*(?:True|Mandatory|Strict)\s*(?:;|$)",s); assert not re.search(r"(?i)(?:^|;)\s*TrustServerCertificate\s*=\s*(?:True|Yes)\s*(?:;|$)",s); assert re.search(r"(?i)(?:^|;)\s*(?:Database|Initial Catalog)\s*=\s*[^;\s][^;]*",s)' || fail 'SQL connection string must use trusted TLS and a dedicated database.'
read -r -p 'Has the DBA reviewed/applied DBA_REVIEW_IDEMPOTENT.sql to this SQL database and backed it up? Type DBA-APPROVED: ' APPROVAL
[[ "$APPROVAL" == DBA-APPROVED ]] || fail 'SQL schema must be DBA-approved before startup.'

# All inputs checked; now install Nginx and create separate instance. No changes to old service.
dnf install -y nginx openssl policycoreutils-python-utils python3 curl rsync >/dev/null
id jcfnetwork91 >/dev/null || fail 'Native service identity missing; rerun prepare.'
install -d -o root -g jcfnetwork91 -m 0750 "$CFG" "$CFG/secrets"
install -d -o jcfnetwork91 -g jcfnetwork91 -m 0700 "$STATE" "$STATE/keys"
# Encryption certificate is separate from external HTTPS identity; its private key stays on the host.
if [[ ! -f "$CFG/secrets/keyring.pfx" ]]; then
  openssl rand -hex 32 > "$CFG/secrets/keyring-password"
  TMP=$(mktemp -d /tmp/jcf-dp.XXXXXXXX)
  trap 'rm -rf "$TMP"' EXIT
  openssl req -new -newkey rsa:3072 -nodes -x509 -sha256 -days 730 \
    -subj '/CN=JCF-NetworkManager-DataProtection' \
    -keyout "$TMP/key.pem" -out "$TMP/cert.pem" >/dev/null 2>&1
  openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$TMP/cert.pem" \
    -out "$CFG/secrets/keyring.pfx" -passout "file:$CFG/secrets/keyring-password"
fi
printf '%s' "$ADMINPASS" > "$CFG/secrets/initial-admin-password"
unset ADMINPASS
chmod 0640 "$CFG/secrets/initial-admin-password" "$CFG/secrets/keyring-password" "$CFG/secrets/keyring.pfx"
chown root:jcfnetwork91 "$CFG/secrets/initial-admin-password" "$CFG/secrets/keyring-password" "$CFG/secrets/keyring.pfx"
# SQL secret arrives via stdin; it never enters the script's command line or the systemd unit.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
printf '%s\n' "$SQL" | python3 "$SCRIPT_DIR/write_config.py" "$CFG/config.json" "$HOST" "$ADMIN" "$STATE/keys" "$CFG/secrets/keyring.pfx"
unset SQL
chown root:jcfnetwork91 "$CFG/config.json"; chmod 0640 "$CFG/config.json"

cat > "/etc/systemd/system/$SERVICE.service" <<EOF
[Unit]
Description=JCF Network Manager v0.9.1 - production Identity, SQL, simulated inventory
Wants=network-online.target
After=network-online.target
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
systemctl enable --now "$SERVICE" >/dev/null
OK=0
for i in {1..30}; do
  if curl -fsS --max-time 3 -H 'X-Forwarded-Proto: https' http://127.0.0.1:5181/readyz >/dev/null 2>&1; then OK=1; break; fi
  sleep 2
done
if [[ $OK -ne 1 ]]; then
  info 'SQL-backed application did not pass readiness; leaving old v0.8.2 service unchanged.'
  journalctl -u "$SERVICE" -n 30 --no-pager | sed -E 's/(Password|Pwd)=[^; ]+/\1=[REDACTED]/Ig' || true
  fail 'Check SQL migrations, login, trusted SQL TLS certificate and service permissions.'
fi
info 'Identity/SQL-ready on localhost:5181; preparing HTTPS reverse proxy.'
# Only now retire bootstrap password (the user is stored in SQL with an Identity password hash).
rm -f "$CFG/secrets/initial-admin-password"
# The Nginx worker must read certificates from standard SELinux-labelled paths.
install -d -m 0755 /etc/pki/tls/certs
install -d -m 0700 /etc/pki/tls/private
install -m 0644 -o root -g root "$TLS_CERT" /etc/pki/tls/certs/jcf-network-manager-v091.crt
install -m 0600 -o root -g root "$TLS_KEY" /etc/pki/tls/private/jcf-network-manager-v091.key
restorecon /etc/pki/tls/certs/jcf-network-manager-v091.crt /etc/pki/tls/private/jcf-network-manager-v091.key >/dev/null 2>&1 || true
TLS_CERT=/etc/pki/tls/certs/jcf-network-manager-v091.crt
TLS_KEY=/etc/pki/tls/private/jcf-network-manager-v091.key
PROXY_CONF=/etc/nginx/conf.d/jcf-network-manager-v091.conf
[[ ! -e "$PROXY_CONF" ]] || fail 'Existing v0.9.1 Nginx config found; review manually.'
{
cat <<EOF
# JCF v0.9.1: restricted client access + REAL ASP.NET Identity inside application.
server {
    listen ${SERVER_IP}:443 ssl;
    server_name ${HOST};
    ssl_certificate ${TLS_CERT};
    ssl_certificate_key ${TLS_KEY};
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_session_timeout 10m;
    add_header Strict-Transport-Security "max-age=31536000" always;
    client_max_body_size 2m;
EOF
while IFS= read -r net; do echo "    allow $net;"; done <<< "$CIDRS"
cat <<'EOF'
    deny all;
    location / {
        proxy_pass http://127.0.0.1:5181;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 120s;
        proxy_buffering off;
    }
}
EOF
} > "$PROXY_CONF"
chmod 0644 "$PROXY_CONF"
if ! nginx -t; then rm -f "$PROXY_CONF"; fail 'Nginx config validation failed; old application untouched.'; fi
setsebool -P httpd_can_network_connect 1
systemctl enable --now nginx >/dev/null
systemctl reload nginx
ZONE=$(firewall-cmd --get-zone-of-interface="$IFACE" 2>/dev/null || true)
[[ -n "$ZONE" && "$ZONE" != no\ zone ]] || ZONE=$(firewall-cmd --get-default-zone)
while IFS= read -r net; do
  RULE="rule family=ipv4 source address=\"$net\" port port=\"443\" protocol=\"tcp\" accept"
  firewall-cmd --permanent --zone="$ZONE" --add-rich-rule="$RULE" >/dev/null
  firewall-cmd --zone="$ZONE" --add-rich-rule="$RULE" >/dev/null
done <<< "$CIDRS"
# No global firewalld reload: preserves existing remote administration sessions.
info "SUCCESS: Production ASP.NET Identity and persistent SQL with simulated inventory."
info "Open https://$HOST (trusted JCF CA required); server firewall restricts source networks."
info 'The old 0.8.2 app remains intact on 127.0.0.1:5180 as rollback.'
info 'Hardware writes disabled; polling disabled. This is NOT a demo-persona login.'
