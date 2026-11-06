$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot '..\src\Jcf.NetworkManager\Jcf.NetworkManager.csproj'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Host 'Install .NET 10 SDK from Microsoft, then run this script again.' -ForegroundColor Yellow
  exit 1
}
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Jcf__DemoMode='true'
$env:Jcf__EnableDeviceWrites='false'
Write-Host 'JCF Network Manager demo: sample devices are simulated; devices you register are REAL and are contacted.' -ForegroundColor Green
Write-Host 'Open http://127.0.0.1:5180 in a browser after startup. Press Ctrl+C to stop.'
dotnet run --project $project
