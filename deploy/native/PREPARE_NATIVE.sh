#!/usr/bin/env bash
# Builds native .NET release and produces DBA-reviewed SQL migration. Does NOT alter running v0.8.2.
set -Eeuo pipefail
umask 027
fail(){ printf '[JCF Native] FAILED: %s\n' "$*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || fail 'Run as root via sudo.'
source /etc/os-release
[[ "${ID:-}" == centos && "${VERSION_ID%%.*}" == 9 ]] || fail 'CentOS Stream 9 required.'
SOURCE="$(realpath "$(dirname "$0")/../..")"
[[ -f "$SOURCE/src/Jcf.NetworkManager/Jcf.NetworkManager.csproj" ]] || fail 'Application source missing.'
grep -Fq '<Version>0.9.1</Version>' "$SOURCE/src/Jcf.NetworkManager/Jcf.NetworkManager.csproj" || fail 'Expecting patched 0.9.1 application source.'
# Note: this must install the packages on the target CentOS host, not Docker.
dnf install -y dotnet-sdk-10.0 git rsync python3 curl ca-certificates openssl >/dev/null
command -v dotnet >/dev/null || fail '.NET 10 missing.'
if ! id jcfbuild91 >/dev/null 2>&1; then useradd --system --create-home --home-dir /var/lib/jcfbuild91 --shell /bin/bash jcfbuild91; fi
if ! id jcfnetwork91 >/dev/null 2>&1; then useradd --system --create-home --home-dir /var/lib/jcfnetwork91 --shell /usr/sbin/nologin jcfnetwork91; fi
ROOT=/opt/jcf-network-manager-v091
install -d -m 0750 -o jcfbuild91 -g jcfbuild91 "$ROOT/source" "$ROOT/builds"
install -d -m 0750 -o root -g jcfnetwork91 "$ROOT/releases"
rsync -a --delete --exclude='.git/' --exclude='**/bin/' --exclude='**/obj/' --exclude='**/*.env' --exclude='secrets/' --exclude='**/*.pfx' --exclude='**/*.key' --exclude='**/*.pem' "$SOURCE/" "$ROOT/source/"
chown -R jcfbuild91:jcfbuild91 "$ROOT/source" "$ROOT/builds"
echo '[JCF Native] Restoring, building and running .NET tests (this may require internet for NuGet).'
runuser -u jcfbuild91 -- bash -c 'set -euo pipefail; cd /opt/jcf-network-manager-v091/source; export DOTNET_CLI_TELEMETRY_OPTOUT=1; dotnet restore Jcf.NetworkManager.slnx -p:NuGetAuditLevel=moderate -p:NuGetAuditMode=all -warnaserror:NU1902,NU1903,NU1904; dotnet build Jcf.NetworkManager.slnx -c Release --no-restore; dotnet test tests/Jcf.NetworkManager.Tests.csproj -c Release --no-build --no-restore'
echo '[JCF Native] Publishing version 0.9.1.'
runuser -u jcfbuild91 -- bash -c 'set -euo pipefail; dotnet publish /opt/jcf-network-manager-v091/source/src/Jcf.NetworkManager/Jcf.NetworkManager.csproj -c Release --no-restore -o /opt/jcf-network-manager-v091/builds/publish'
[[ -f "$ROOT/builds/publish/Jcf.NetworkManager.dll" ]] || fail 'Publish output missing.'
echo '[JCF Native] Generating idempotent SQL migration script for DBA review; NO SQL connection is made.'
runuser -u jcfbuild91 -- bash -c 'set -euo pipefail; cd /opt/jcf-network-manager-v091/source; export DOTNET_CLI_TELEMETRY_OPTOUT=1 ConnectionStrings__NetworkManager="Server=localhost;Database=MigrationScriptOnly;Encrypt=True;TrustServerCertificate=False;Integrated Security=True"; dotnet tool restore; dotnet ef migrations script 0 --idempotent --project src/Jcf.NetworkManager/Jcf.NetworkManager.csproj --startup-project src/Jcf.NetworkManager/Jcf.NetworkManager.csproj --context NetworkDbContext --output /opt/jcf-network-manager-v091/builds/DBA_REVIEW_IDEMPOTENT.sql'
[[ -s "$ROOT/builds/DBA_REVIEW_IDEMPOTENT.sql" ]] || fail 'Migration SQL generation failed.'
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
DEST="$ROOT/releases/$STAMP"
install -d -m 0750 -o root -g jcfnetwork91 "$DEST"
rsync -a "$ROOT/builds/publish/" "$DEST/"
chown -R root:jcfnetwork91 "$DEST"
chmod -R go-w "$DEST"
ln -sfn "$DEST" "$ROOT/prepared.next"
mv -Tf "$ROOT/prepared.next" "$ROOT/prepared"
printf '\n[JCF Native] PREPARED. Existing v0.8.2 service unchanged.\nDBA migration script: %s/builds/DBA_REVIEW_IDEMPOTENT.sql\n' "$ROOT"
printf 'Have your DBA apply the reviewed script to a dedicated empty SQL Server database and configure a trusted SQL TLS certificate BEFORE running ACTIVATE_NATIVE.sh.\n'
