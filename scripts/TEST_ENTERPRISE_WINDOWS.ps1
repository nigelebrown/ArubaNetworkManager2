$ErrorActionPreference='Stop'
$root=Resolve-Path (Join-Path $PSScriptRoot '..')
$results=Join-Path $root 'test-results'
New-Item $results -ItemType Directory -Force | Out-Null
& dotnet --info | Out-File -Encoding utf8 (Join-Path $results 'dotnet-info.txt')
& dotnet restore (Join-Path $root 'tests/Jcf.NetworkManager.Tests.csproj')
if($LASTEXITCODE -ne 0){throw 'NuGet restore failed'}
& dotnet build (Join-Path $root 'src/Jcf.NetworkManager/Jcf.NetworkManager.csproj') --configuration Release --no-restore
if($LASTEXITCODE -ne 0){throw 'Production project compilation failed'}
& dotnet test (Join-Path $root 'tests/Jcf.NetworkManager.Tests.csproj') --configuration Release --no-restore --logger "trx;LogFileName=enterprise-tests.trx" --results-directory $results
if($LASTEXITCODE -ne 0){throw 'Tests failed; no deployment permitted'}
Write-Host "Test evidence saved to $results"
