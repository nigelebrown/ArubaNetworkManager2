# Local pilot only. Use on a management workstation that can reach authorized devices.
# Entra application registration and SQL Server are required (see README).
$ErrorActionPreference='Stop'
$env:Jcf__DemoMode='false'
$env:Jcf__EnableDeviceWrites='false'
$project=Join-Path $PSScriptRoot '..\src\Jcf.NetworkManager\Jcf.NetworkManager.csproj'
Write-Host 'Ensure Entra, SQL Server, and certificate pinning are configured through environment variables.'
dotnet run --project $project
