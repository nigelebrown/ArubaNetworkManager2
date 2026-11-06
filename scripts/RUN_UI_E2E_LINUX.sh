#!/usr/bin/env bash
# Browser end-to-end test of the demo UI (real Blazor circuit in headless Chrome/Chromium).
# Requires: .NET 10 SDK, Python 3.10+, `pip install playwright`, and Chrome/Chromium
# (set CHROME_PATH if not /usr/bin/google-chrome). Never point this at a production instance.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$ROOT/docs/ui-e2e-output}"
dotnet build "$ROOT/src/Jcf.NetworkManager/Jcf.NetworkManager.csproj" -c Debug
# Content root must be the project folder: appsettings.json (management CIDR allowlist) and wwwroot live there.
# Launching the DLL from the repo root without it loads no appsettings and every device is (correctly) refused.
cd "$ROOT/src/Jcf.NetworkManager"
ASPNETCORE_ENVIRONMENT=Development Jcf__DemoMode=true \
  dotnet "bin/Debug/net10.0/Jcf.NetworkManager.dll" --contentRoot "$ROOT/src/Jcf.NetworkManager" > "$OUT.server.log" 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT
for i in $(seq 1 40); do curl -sf http://127.0.0.1:5180/healthz >/dev/null && break; sleep 1; done
curl -sf http://127.0.0.1:5180/healthz >/dev/null || { echo "Server did not start; see $OUT.server.log"; exit 1; }
python3 "$ROOT/scripts/ui-e2e/ui_e2e.py" "$OUT"
