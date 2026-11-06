# DEVELOPMENT/RELEASE ENGINEERING STEP. Generate and REVIEW a source-controlled migration,
# then run it only against an approved SQL Server database. Never auto-generate on startup.
$ErrorActionPreference='Stop'
$root=Resolve-Path (Join-Path $PSScriptRoot '..')
$project=Join-Path $root 'src/Jcf.NetworkManager/Jcf.NetworkManager.csproj'
if(-not $env:ConnectionStrings__NetworkManager){
  throw 'Set the ConnectionStrings__NetworkManager environment variable (TLS validated SQL Server connection).'
}
if(-not (Get-Command dotnet -ErrorAction SilentlyContinue)){throw '.NET 10 SDK is required.'}
$dotnetVersion=(& dotnet --version)
Write-Host "Using .NET SDK $dotnetVersion"
& dotnet restore $project
if($LASTEXITCODE -ne 0){throw 'dotnet restore failed'}
& dotnet build $project --no-restore -c Release
if($LASTEXITCODE -ne 0){throw 'dotnet build failed'}
$existing=Get-ChildItem (Join-Path $root 'src/Jcf.NetworkManager/Migrations') -Filter '*_InitialProduction.cs' -ErrorAction SilentlyContinue
if($existing){throw 'InitialProduction migration already exists. Review and use dotnet ef database update; do not recreate.'}
& dotnet tool restore
if($LASTEXITCODE -ne 0){throw 'Restore dotnet-ef 10.x from the approved internal NuGet feed.'}
& dotnet ef migrations add InitialProduction --project $project --startup-project $project --context NetworkDbContext
if($LASTEXITCODE -ne 0){throw 'Migration generation failed. Check dotnet-ef 10.x availability.'}
Write-Host 'Migration generated. REVIEW ALL generated SQL and check secrets/data types before applying.'
& dotnet ef migrations script 0 --idempotent --project $project --startup-project $project --output (Join-Path $root 'docs/INITIAL_DB_REVIEW.sql')
if($LASTEXITCODE -ne 0){throw 'Migration SQL generation failed'}
Write-Host 'Send docs/INITIAL_DB_REVIEW.sql to DBA for review. Commit migration source; no DB was modified by this script.'
