#!/usr/bin/env bash
# JCF Intelligent Network Manager 0.9.1, native .NET 10 / SQL Identity, simulated devices.
# No Docker, no simulated logins, no live equipment polling/writes.
set -Eeuo pipefail
umask 027
die(){ printf '[JCF Native 0.9.1] STOPPED: %s\n' "$*" >&2; exit 1; }
say(){ printf '[JCF Native 0.9.1] %s\n' "$*"; }
[[ ${EUID} -eq 0 ]] || die 'Run as root or with sudo.'
[[ -r /etc/os-release ]] || die 'Cannot identify operating system.'
source /etc/os-release
[[ "${ID:-}" == centos && "${VERSION_ID%%.*}" == 9 ]] || die 'CentOS Stream 9 required.'
MODE="${1:-prepare}"
[[ "$MODE" == prepare || "$MODE" == activate ]] || die 'Usage: sudo bash INSTALL_NATIVE_091.sh prepare|activate'
BASE=/opt/jcf-native-v091-source
REPO=https://github.com/nigelebrown/ArubaNetworkManager2.git
if [[ "$MODE" == prepare ]]; then
  command -v git >/dev/null || dnf install -y git ca-certificates
  install -d -m 0750 -o root -g root "$BASE" "$BASE/releases"
  TEMP="$(mktemp -d "$BASE/.stage.XXXXXXXX")"
  trap '[[ -d "$TEMP" ]] && rm -rf "$TEMP"' EXIT
  say 'Cloning complete v0.9.1 source from ArubaNetworkManager2'
  git clone --quiet --depth 1 --branch main "$REPO" "$TEMP"
  [[ -r "$TEMP/src/Jcf.NetworkManager/Jcf.NetworkManager.csproj" ]] ||
    die 'Source is incomplete.'
  grep -Fq '<Version>0.9.1</Version>' "$TEMP/src/Jcf.NetworkManager/Jcf.NetworkManager.csproj" ||
    die 'GitHub application version does not match v0.9.1; refusing to replace working service.'
  for path in deploy/native/PREPARE_NATIVE.sh deploy/native/ACTIVATE_NATIVE.sh \
     src/Jcf.NetworkManager/Migrations/20261106123050_DeviceIntegration.cs \
     src/Jcf.NetworkManager/Services/LocalIdentityBootstrap.cs; do
    [[ -s "$TEMP/$path" ]] || die "Incomplete source: missing $path"
  done
  bash -n "$TEMP/deploy/native/PREPARE_NATIVE.sh" "$TEMP/deploy/native/ACTIVATE_NATIVE.sh" ||
    die 'Native installer syntax invalid.'
  SHA="$(git -C "$TEMP" rev-parse --short=12 HEAD)"
  DEST="$BASE/releases/$SHA"
  if [[ -d "$DEST" ]]; then
    rm -rf "$TEMP"
    say "Reusing previously reviewed source revision $SHA"
  else
    mv "$TEMP" "$DEST"
  fi
  trap - EXIT
  ln -sfn "$DEST" "$BASE/current.next"
  mv -Tf "$BASE/current.next" "$BASE/current"
  say "Source SHA $SHA verified. Compiling application and preparing DBA migration review."
  bash "$DEST/deploy/native/PREPARE_NATIVE.sh"
  say 'PREPARE complete. Existing v0.8.2 service remains untouched.'
  say 'DBA must review/apply: /opt/jcf-network-manager-v091/builds/DBA_REVIEW_IDEMPOTENT.sql'
  say 'Next after DBA approval: sudo bash /tmp/JCF_NATIVE_091.sh activate'
else
  [[ -s "$BASE/current/deploy/native/ACTIVATE_NATIVE.sh" ]] ||
    die 'Run prepare first. No source tree is available.'
  say 'Starting supervised SQL Identity + HTTPS activation (requires approved SQL and JCF CA certificates).'
  bash "$BASE/current/deploy/native/ACTIVATE_NATIVE.sh"
fi
