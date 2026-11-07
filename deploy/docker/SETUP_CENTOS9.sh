#!/usr/bin/env bash
# CHECK -> PREPARE -> ACTIVATE -> ROLLBACK. Does not remove native service or SQL.
set -Eeuo pipefail
umask 027
say(){ echo "[JCF Docker] $*"; }
die(){ say "STOPPED: $*" >&2; exit 1; }
HERE="$(cd "$(dirname "$0")" && pwd)"
HOST_IP="$(printenv JCF_HOST_IP || echo 172.17.71.20)"
SUBNETS="$(printenv JCF_ALLOWED_CIDRS || echo 172.17.71.0/24)"
SOURCE="$(printenv JCF_NATIVE_CONFIG || echo /etc/jcfnetwork-v091/config.json)"
CONF=/etc/jcfnetwork-docker-v091
STATE=/var/lib/jcf-network-docker-v091
ENVFILE="$HERE/.env"
COMPOSE="$HERE/compose.yaml"
require(){
 [[ "$EUID" == 0 ]]||die 'Run as root'
 source /etc/os-release
 [[ "$ID" == centos && "$(echo "$VERSION_ID"|cut -d. -f1)" == 9 ]]||die 'CentOS Stream 9 required'
 ip -4 -o addr show | grep -Fq "$HOST_IP/" || die "Real IP $HOST_IP not on server"
 [[ -f "$SOURCE" ]]||die "External SQL Identity config absent: $SOURCE"
 [[ -f "$COMPOSE" && -f "$HERE/Dockerfile" ]]||die 'Use a complete feature branch checkout'
}
compose(){ docker compose --env-file "$ENVFILE" -f "$COMPOSE" "$@"; }
prepare(){
 require
 dnf -y install python3 curl acl nginx firewalld dnf-plugins-core
 if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1;then
  dnf config-manager --add-repo https://download.docker.com/linux/centos/docker-ce.repo
  dnf -y install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
 fi
 systemctl enable --now docker
 install -d -m 0750 "$CONF" "$STATE"
 install -d -m 0700 "$STATE/recovery"
 chown root:1654 "$CONF"
 chown 1654:1654 "$STATE/recovery"
 keydir="$(python3 - "$SOURCE" "$CONF" <<'PY'
import sys,json,shutil,os
source,dest=sys.argv[1:3]
data=json.load(open(source))
assert data.get('ConnectionStrings',{}).get('NetworkManager'), 'Missing external SQL Server connection'
j=data.setdefault('Jcf',{})
dp=j.setdefault('DataProtection',{})
keys,cert=dp['KeysDirectory'],dp['CertificatePath']
assert os.path.isdir(keys) and os.path.isfile(cert),'Persistent DataProtection keys/certificate required'
shutil.copyfile(cert,dest+'/dp.pfx');os.chmod(dest+'/dp.pfx',0o640)
dp['CertificatePath']='/run/jcf-config/dp.pfx';dp['KeysDirectory']='/var/lib/jcf-keys'
if dp.get('CertificatePasswordFile'):
 shutil.copyfile(dp['CertificatePasswordFile'],dest+'/dp.pass');os.chmod(dest+'/dp.pass',0o640)
 dp['CertificatePasswordFile']='/run/jcf-config/dp.pass'
admin=j.get('BootstrapAdmin',{})
if admin.get('PasswordFile') and os.path.isfile(admin['PasswordFile']):
 shutil.copyfile(admin['PasswordFile'],dest+'/bootstrap.pass');os.chmod(dest+'/bootstrap.pass',0o640)
 admin['PasswordFile']='/run/jcf-config/bootstrap.pass'
j['TemporaryHttp']=True;j['ContainerMode']=True;j['EnableDeviceWrites']=False
j.setdefault('DeviceWrites',{})['Enabled']=False
j.setdefault('Recovery',{})['EncryptedBackupDirectory']='/var/lib/jcf-backups'
with open(dest+'/config.json','w') as f:json.dump(data,f,indent=2)
os.chmod(dest+'/config.json',0o640)
print(keys)
PY
)"
 chgrp 1654 "$CONF/"*
 setfacl -m u:1654:rx "$keydir"
 find "$keydir" -type d -exec setfacl -m u:1654:rwx {} +
 find "$keydir" -type f -exec setfacl -m u:1654:rw {} +
 cat > "$ENVFILE" <<EOF
JCF_CONFIG_DIRECTORY=$CONF
JCF_KEYS_DIRECTORY=$keydir
JCF_BACKUP_DIRECTORY=$STATE/recovery
EOF
 chmod 0600 "$ENVFILE"
 compose config --quiet
 compose up -d --build
 for attempt in $(seq 1 40);do
  if curl -fsS --max-time 4 http://127.0.0.1:5182/readyz >/dev/null;then
   say 'Prepared: SQL-backed container ready on host loopback 5182. Native service unchanged.'
   exit 0
  fi
  sleep 2
 done
 compose logs --tail=40 web >&2
 die 'Container failed readiness; no front-end cutover'
}
activate(){
 require
 [[ -f "$ENVFILE" ]]||die 'Run prepare first'
 curl -fsS --max-time 5 http://127.0.0.1:5182/readyz >/dev/null||die 'Container not SQL-ready'
 [[ ! -f "$STATE/active-proxy" ]]||die 'Already activated'
 local target='' match='' count=0
 for match in /etc/nginx/conf.d/*.conf;do
  if [[ -f "$match" ]] && grep -Eq 'proxy_pass[[:space:]]+http://127\.0\.0\.1:518[01];' "$match";then
   target="$match";count=$((count+1))
  fi
 done
 [[ "$count" -le 1 ]]||die 'Ambiguous existing native upstream; manual review required'
 if [[ "$count" == 1 ]];then
  cp -a "$target" "$STATE/original-proxy"
  sed -i -E 's@proxy_pass[[:space:]]+http://127\.0\.0\.1:518[01];@proxy_pass http://127.0.0.1:5182;@g' "$target"
 else
  target=/etc/nginx/conf.d/jcf-network-manager-docker.conf
  [[ ! -e "$target" ]]||die 'Existing unmanaged Docker nginx config'
  local allow='' net
  for net in $SUBNETS;do
   python3 - "$net" <<'PY'
import sys,ipaddress
ipaddress.ip_network(sys.argv[1],strict=False)
PY
   allow="$allow    allow $net;\n"
  done
  python3 - "$HERE/nginx-site.conf.template" "$HOST_IP" "$allow" > "$target" <<'PY'
import sys
print(open(sys.argv[1]).read().replace('JCF_HOST_IP',sys.argv[2]).replace('JCF_ALLOWED_NETS',sys.argv[3].replace('\\n','\n')))
PY
 fi
 if ! nginx -t;then
  if [[ -f "$STATE/original-proxy" ]];then cp -a "$STATE/original-proxy" "$target";else rm -f "$target";fi
  die 'Nginx failed validation; original restored'
 fi
 systemctl enable --now nginx
 systemctl reload nginx
 if systemctl is-active --quiet firewalld;then
  for net in $SUBNETS;do
   firewall-cmd --permanent --add-rich-rule="rule family='ipv4' source address='$net' port protocol='tcp' port='80' accept" >/dev/null
  done
  firewall-cmd --reload >/dev/null
 fi
 echo "$target" > "$STATE/active-proxy"
 chmod 0600 "$STATE/active-proxy"
 say "Activated http://$HOST_IP via Nginx -> Docker 127.0.0.1:5182"
 say 'Native systemd service and SQL not removed'
}
rollback(){
 [[ "$EUID" == 0 ]]||die 'Root required'
 [[ -f "$STATE/active-proxy" ]]||die 'No recorded activation'
 target="$(cat "$STATE/active-proxy")"
 if [[ -f "$STATE/original-proxy" ]];then
  cp -a "$STATE/original-proxy" "$target";rm -f "$STATE/original-proxy"
 else rm -f "$target";fi
 nginx -t && systemctl reload nginx
 rm -f "$STATE/active-proxy"
 say 'Previous front end restored; SQL/native/docker retained'
}
check(){
 require
 ss -lntp | grep -E ':(80|443|5181|5182) ' || true
 command -v docker >/dev/null && docker compose version || true
 systemctl is-active jcf-network-manager-v091 || true
 if [[ -f "$ENVFILE" ]];then compose ps;fi
 firewall-cmd --state || true
}
case "$(echo "$1")" in
 check)check;;prepare)prepare;;activate)activate;;rollback)rollback;;
 *)die 'Usage: sudo bash SETUP_CENTOS9.sh {check|prepare|activate|rollback}';;
esac
