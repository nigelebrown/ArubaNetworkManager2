#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet test "$ROOT/tests/Jcf.NetworkManager.Tests.csproj" -c Release
