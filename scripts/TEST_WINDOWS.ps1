$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET SDK 10 first.' }
Write-Host 'Building and running all .NET tests' -ForegroundColor Cyan
dotnet test (Join-Path $root 'tests\Jcf.NetworkManager.Tests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Build/tests did not pass. Do not deploy.' }
Write-Host 'Test suite completed.' -ForegroundColor Green
