#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
command -v dotnet >/dev/null || { echo 'Install .NET SDK 10 before running'; exit 1; }
export ASPNETCORE_ENVIRONMENT=Development
export Jcf__DemoMode=true
export Jcf__EnableDeviceWrites=false
echo 'Demo on http://127.0.0.1:5180 - sample devices are simulated; devices you register are REAL and are contacted'
exec dotnet run --project "$ROOT/src/Jcf.NetworkManager/Jcf.NetworkManager.csproj"
