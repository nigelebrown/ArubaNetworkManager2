$ErrorActionPreference='Stop'
if(-not $env:ConnectionStrings__NetworkManager){throw 'ConnectionStrings__NetworkManager not configured.'}
$root=Resolve-Path (Join-Path $PSScriptRoot '..')
$project=Join-Path $root 'src/Jcf.NetworkManager/Jcf.NetworkManager.csproj'
if(-not (Test-Path (Join-Path $root 'docs/INITIAL_DB_REVIEW.sql'))){throw 'Migration SQL has not been generated and approved.'}
Write-Host 'Only apply in a change window after SQL full backup, DBA approval and SQL permissions review.'
$confirm=Read-Host 'Type APPLY-REVIEWED-MIGRATION to continue'
if($confirm -cne 'APPLY-REVIEWED-MIGRATION'){throw 'Cancelled'}
& dotnet ef database update --project $project --startup-project $project --context NetworkDbContext
if($LASTEXITCODE -ne 0){throw 'Database migration failed. DBA recovery required.'}
