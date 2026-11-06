# JCF Network Manager v0.8 - SQL Server baseline generation and DBA-controlled pilot migration.
# By default: generate reviewed SQL only. NEVER silently updates a database.
param(
    [switch]$ApplyToPilot,
    [string]$ExpectedPilotDatabase = '',
    [switch]$IHaveReviewedSqlAndBackup
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src\Jcf.NetworkManager\Jcf.NetworkManager.csproj'
$migrations = Join-Path $root 'src\Jcf.NetworkManager\Migrations'
$sqlFolder = Join-Path $root 'docs\pilot-sql'
$sqlFile = Join-Path $sqlFolder 'INITIAL_SCHEMA_REVIEW.sql'
New-Item -ItemType Directory -Path $sqlFolder -Force | Out-Null
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {throw 'Install .NET SDK 10 first.'}
if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__NetworkManager)) {
    throw 'Configure ConnectionStrings__NetworkManager with Encrypt=True and a trusted certificate; never store this string in source.'
}
# These checks are intentionally conservative. The DBA must also independently review the actual SQL host.
if ($env:ConnectionStrings__NetworkManager -notmatch '(?i)(?:^|;)\s*Encrypt\s*=\s*(?:True|Yes|Mandatory|Strict)\s*(?:;|$)') {
    throw 'SQL encryption is required (Encrypt=True/Mandatory/Strict).'
}
if ($env:ConnectionStrings__NetworkManager -match '(?i)(?:^|;)\s*TrustServerCertificate\s*=\s*(?:True|Yes)\s*(?:;|$)') {
    throw 'TrustServerCertificate=True is forbidden for pilot certification. Install a trusted SQL Server certificate.'
}
Push-Location $root
try {
    & dotnet restore $project
    if ($LASTEXITCODE -ne 0) {throw 'Production project NuGet restore failed.'}
    & dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {throw 'Production project failed to compile.'}
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) {throw 'Cannot restore dotnet-ef from approved feed.'}
    if (-not (Test-Path $migrations) -or -not (Get-ChildItem $migrations -Filter '*.cs' -ErrorAction SilentlyContinue)) {
        Write-Host 'Generating EF Core 10 baseline migration into source (not applying it)...'
        & dotnet ef migrations add InitialProduction --project $project --startup-project $project --context NetworkDbContext
        if ($LASTEXITCODE -ne 0) {throw 'Migration generation failed.'}
    }
    & dotnet ef migrations script 0 --idempotent --project $project --startup-project $project --output $sqlFile
    if ($LASTEXITCODE -ne 0) {throw 'SQL script generation failed.'}
    Write-Host "Generated reviewable SQL: $sqlFile"
    Write-Host 'Review ALL resulting SQL, especially Identity, indexes, Unicode lengths, nullable fields and telemetry retention.'
    if ($ApplyToPilot) {
        if (-not $IHaveReviewedSqlAndBackup) {throw '-IHaveReviewedSqlAndBackup is mandatory before applying.'}
        if ($ExpectedPilotDatabase -notmatch '^JCFNetworkManagerPilot_[A-Za-z0-9_]{1,32}$') {
            throw 'Pilot database name must start with JCFNetworkManagerPilot_ and contain only permitted characters.'
        }
        $match = [regex]::Match($env:ConnectionStrings__NetworkManager, '(?i)(?:^|;)\s*(?:Database|Initial Catalog)\s*=\s*([^;]+)')
        if (-not $match.Success -or $match.Groups[1].Value.Trim().Trim('"') -cne $ExpectedPilotDatabase) {
            throw 'Connection string database does not exactly match the required disposable pilot name.'
        }
        $confirmation = Read-Host 'Type APPLY-TO-DISPOSABLE-PILOT-ONLY to apply the reviewed schema'
        if ($confirmation -cne 'APPLY-TO-DISPOSABLE-PILOT-ONLY') {throw 'Cancelled before database change.'}
        & dotnet ef database update --project $project --startup-project $project --context NetworkDbContext
        if ($LASTEXITCODE -ne 0) {throw 'SQL migration failed. DBA review is required.'}
        Write-Host 'Applied migration to the explicitly named pilot database only.'
    }
} finally {Pop-Location}
