#!/usr/bin/env bash
# Safely upgrade an ALREADY ACTIVATED native v0.9.1 host from current GitHub main.
# Reuses installed SQL Server, Identity users, certificates, keys, and Nginx.
# Does NOT re-run the all-in-one installer or recreate SQL credentials.
set -Eeuo pipefail
umask 027
say(){ printf '[JCF AP Update] %s\n' "$*"; }
die(){ say "STOPPED: $*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || die 'Use sudo/root'
source /etc/os-release
[[ "${ID:-}" == centos && "${VERSION_ID%%.*}" == 9 ]] || die 'CentOS Stream 9 required'
ROOT=/opt/jcf-network-manager-v091
PREPARED="$ROOT/prepared"
SERVICE=jcf-network-manager-v091
[[ -f /etc/jcfnetwork-v091/config.json ]] || die 'v0.9.1 SQL identity config not found; initial installation incomplete'
systemctl is-active --quiet "$SERVICE" || die 'Native v0.9.1 service not active; do not replace it blindly'
OLD="$(readlink -f "$PREPARED" || true)"
[[ -f "$OLD/Jcf.NetworkManager.dll" ]] || die 'Previous working release not found'
curl -fsS --max-time 5 http://127.0.0.1:5181/readyz >/dev/null ||
  die 'Existing SQL-backed v0.9.1 is not ready; investigate before upgrade'
ROOT_INSTALL=/tmp/JCF_NATIVE_091_UPDATE_INSTALL.sh
curl -fsSLo "$ROOT_INSTALL" \
 https://raw.githubusercontent.com/nigelebrown/ArubaNetworkManager2/main/INSTALL_NATIVE_091.sh
bash -n "$ROOT_INSTALL" || die 'Downloaded installer failed syntax check'
RESTORE=1
rollback(){
  if [[ "$RESTORE" == 1 ]]; then
    say 'Restoring previous native release and restarting service.'
    ln -sfn "$OLD" "$ROOT/prepared.rollback"
    mv -Tf "$ROOT/prepared.rollback" "$PREPARED"
    systemctl restart "$SERVICE" || true
    say 'Old release path restored; inspect the service before reuse.'
  fi
}
trap rollback ERR
say 'Pulling latest source, rebuilding and running all offline tests; existing service is unchanged until successful build.'
bash "$ROOT_INSTALL" prepare
NEW="$(readlink -f "$PREPARED")"
if [[ "$OLD" == "$NEW" ]]; then
  RESTORE=0
  say 'Same native release already present; no restart needed.'
  exit 0
fi
say 'Build passed. Restarting v0.9.1 service with existing SQL accounts and sample inventory.'
systemctl restart "$SERVICE"
READY=0
for n in {1..25}; do
  if curl -fsS --max-time 4 http://127.0.0.1:5181/readyz >/dev/null 2>&1; then READY=1;break;fi
  sleep 2
done
[[ "$READY" == 1 ]] || die 'New application failed SQL readiness. Triggering rollback.'
curl -fsS --max-time 5 http://127.0.0.1:5181/healthz | \
 python3 -c 'import json,sys;x=json.load(sys.stdin);assert x["version"]=="0.9.1" and x["demo"] is False and x["status"]=="up"' ||
 die 'New application failed health/version check. Triggering rollback.'
RESTORE=0
trap - ERR
say "SUCCESS: updated v0.9.1 source, from $OLD to $NEW"
say 'AP management console: http://172.17.71.20/ap-management (if restricted HTTP proxy has been activated).'
say 'No SQL users, data, key rings, devices, Nginx, firewall or Docker configuration changed.'
