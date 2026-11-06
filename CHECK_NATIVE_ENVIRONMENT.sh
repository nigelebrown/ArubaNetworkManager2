#!/usr/bin/env bash
# Safe native CentOS 9 preflight: never installs SQL Server or exposes demo authentication.
set -Eeuo pipefail
umask 027
MODE="${1:-check}"
[[ "$MODE" == check || "$MODE" == install-os ]] || { echo 'Usage: sudo bash CHECK_NATIVE_ENVIRONMENT.sh [check|install-os] [SQL_HOST] [SQL_PORT]' >&2; exit 2; }
SQL_HOST="${2:-}"
SQL_PORT="${3:-1433}"
[[ "$SQL_PORT" =~ ^[0-9]+$ && "$SQL_PORT" -ge 1 && "$SQL_PORT" -le 65535 ]] || { echo 'SQL port must be 1..65535' >&2; exit 2; }
say(){ printf '[JCF preflight] %-27s %s\n' "$1" "$2"; }
has(){ command -v "$1" >/dev/null 2>&1; }
if [[ ! -r /etc/os-release ]]; then echo 'Cannot determine OS' >&2; exit 1; fi
# shellcheck disable=SC1091
source /etc/os-release
say 'Host' "$(hostname -f 2>/dev/null || hostname)"
say 'OS' "$PRETTY_NAME"
say 'Kernel' "$(uname -r)"
if [[ "$ID" != centos || "${VERSION_ID%%.*}" != 9 ]]; then
  say 'STOP' 'Expected CentOS Stream 9, do not auto-install packages.'
  exit 1
fi
if [[ "$MODE" == install-os ]]; then
  [[ $EUID -eq 0 ]] || { echo 'install-os requires sudo/root' >&2; exit 1; }
  say 'Action' 'Install OS dependencies only: git curl rsync nginx firewalld openssl python3 policycoreutils, .NET 10 SDK.'
  dnf install -y git curl ca-certificates rsync nginx firewalld openssl \
    python3 python3-pip policycoreutils-python-utils dotnet-sdk-10.0
  systemctl enable --now firewalld
  # Never enable nginx or change network policy until a validated v0.9.1 service exists.
fi
say 'LAN IPv4' "$(ip -o -4 addr show scope global | awk '{printf "%s=%s ",$2,$4}')"
say '.NET' "$(has dotnet && dotnet --version || echo 'MISSING')"
for bin in git curl python3 nginx openssl firewall-cmd; do
  if has "$bin"; then say "Command $bin" 'installed'; else say "Command $bin" 'MISSING'; fi
done
for svc in jcf-network-manager jcf-network-manager-v091 nginx firewalld mssql-server; do
  say "Service $svc" "$(systemctl is-active "$svc" 2>/dev/null || true)"
done
for port in 80 443 5180 5181 1433; do
  if has ss; then say "TCP $port" "$(ss -H -lnt "( sport = :$port )" | awk '{printf "%s:%s ",$4,$6}' | head -c 230)"; fi
done
if has curl; then
  for p in 5180 5181; do
    BODY="$(curl -fsS --connect-timeout 2 --max-time 4 "http://127.0.0.1:$p/healthz" 2>/dev/null || true)"
    if [[ -n "$BODY" ]]; then say "Health $p" "$BODY"; else say "Health $p" 'unavailable'; fi
  done
fi
if has firewall-cmd && firewall-cmd --state >/dev/null 2>&1; then
  ZONE="$(firewall-cmd --get-zone-of-interface=ens192 2>/dev/null || firewall-cmd --get-default-zone)"
  say 'firewalld zone' "$ZONE"
  say 'firewalld services' "$(firewall-cmd --zone="$ZONE" --list-services)"
  say 'firewalld ports' "$(firewall-cmd --zone="$ZONE" --list-ports)"
  say 'firewalld rich rules' "$(firewall-cmd --zone="$ZONE" --list-rich-rules | tr '\\n' ' ' | head -c 350)"
fi
if has getenforce; then say 'SELinux' "$(getenforce)"; fi
for path in \
  /opt/jcf-network-manager-v091/builds/publish/Jcf.NetworkManager.dll \
  /opt/jcf-network-manager-v091/builds/DBA_REVIEW_IDEMPOTENT.sql \
  /opt/jcf-network-manager-v091/prepared/Jcf.NetworkManager.dll \
  /etc/jcfnetwork-v091/config.json; do
  if [[ -s "$path" ]]; then say 'Artifact' "$path EXISTS"; else say 'Artifact' "$path MISSING"; fi
done
if rpm -q mssql-server >/dev/null 2>&1; then
  say 'Local SQL Server package' 'installed (support status MUST be reviewed for CentOS)'
else
  say 'Local SQL Server package' 'NOT installed; do not assume remote SQL exists'
fi
if [[ -n "$SQL_HOST" ]]; then
  say 'SQL target' "$SQL_HOST:$SQL_PORT"
  if timeout 4 bash -c 'exec 3<>/dev/tcp/"$1"/"$2"' _ "$SQL_HOST" "$SQL_PORT" 2>/dev/null; then
    say 'SQL TCP' 'REACHABLE (not proof of SQL login or TLS)'
  else
    say 'SQL TCP' 'NOT REACHABLE; check host, routing, firewall, listener'
  fi
else
  say 'SQL TCP' 'NOT CHECKED: no SQL hostname was supplied'
fi
say 'Safety' 'NO SQL database changes; NO port opening; NO Docker; NO app service replacement'
say 'Next' 'Capture these results; SQL host and authorized credentials required if SQL is not provisioned.'
