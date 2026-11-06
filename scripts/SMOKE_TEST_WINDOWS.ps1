$ErrorActionPreference = 'Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location $root
$report = [ordered]@{test='JCF Network Manager enterprise 0.6 local end-to-end smoke'; timestamp_utc=[DateTime]::UtcNow.ToString('o'); checks=@(); passed=$false; mode='DEMO_NO_DEVICE_WRITES'}
function Check($name,$ok,$detail) {
    $script:report.checks+=@{name=$name;pass=[bool]$ok;detail=[string]$detail}
    Write-Host ($(if($ok){'PASS'}else{'FAIL'})) $name ':' $detail
    if(!$ok){throw "Smoke test failed: $name"}
}
try {
    if(!(Get-Command dotnet -ErrorAction SilentlyContinue)){throw '.NET 10 SDK missing.'}
    dotnet test tests\Jcf.NetworkManager.Tests.csproj --configuration Release
    Check 'dotnet test' ($LASTEXITCODE -eq 0) "Exit code $LASTEXITCODE"
    $env:ASPNETCORE_ENVIRONMENT='Development'
    $env:Jcf__DemoMode='true'
    $env:Jcf__EnableDeviceWrites='false'
    $env:Jcf__LiveLabWrites__Enabled='false'
    $out=Join-Path $root 'docs\smoke_stdout.txt';$err=Join-Path $root 'docs\smoke_stderr.txt'
    $proc=Start-Process 'dotnet' -ArgumentList @('run','--no-launch-profile','--project','src\Jcf.NetworkManager\Jcf.NetworkManager.csproj','--no-restore') -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $hostUrl='http://127.0.0.1:5180';$ready=$false
    for($i=0;$i -lt 70;$i++) {
       Start-Sleep -Seconds 1
       if($proc.HasExited){break}
       try {$h=Invoke-WebRequest "$hostUrl/healthz" -UseBasicParsing -TimeoutSec 2;$ready=($h.StatusCode -eq 200);if($ready){break}}catch{}
    }
    Check 'Demo host starts' $ready '127.0.0.1:5180'
    $info=Invoke-RestMethod "$hostUrl/healthz" -TimeoutSec 10
    Check 'Health reports demo mode' ($info.demo -eq $true) 'demo=true'
    $devices=Invoke-RestMethod "$hostUrl/api/v1/devices" -TimeoutSec 10
    Check 'Demo device inventory' ($devices.Count -ge 2) "$($devices.Count) simulated devices"
    $policy=Invoke-RestMethod "$hostUrl/api/v1/radio-policy" -TimeoutSec 10
    Check 'Radio widths 20/40 only' (($policy.approvedWidthsMhz -join ',') -eq '20,40') ($policy.approvedWidthsMhz -join ',')
    foreach($route in @('/configuration','/changes','/groups','/capabilities','/alerts','/recommendations','/topology','/operations')) {
        $r=Invoke-WebRequest "$hostUrl$route" -UseBasicParsing -TimeoutSec 10
        Check "Route $route" ($r.StatusCode -eq 200) 'HTTP 200'
    }
    try {Invoke-WebRequest "$hostUrl/api/v1/execution-jobs" -Method POST -Body '{}' -ContentType 'application/json' -UseBasicParsing -TimeoutSec 10 | Out-Null;Check 'Unapproved write blocked' $false 'Unexpected success'}
    catch [System.Net.WebException] {
        $status=[int]$_.Exception.Response.StatusCode
        Check 'Unapproved write blocked' ($status -eq 501) "HTTP $status"
    }
    $report.passed=$true
} catch { $report.error=$_.Exception.Message;Write-Host 'FAILED:' $report.error }
finally {
    if($null -ne $proc -and !$proc.HasExited) {Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue}
    $file=Join-Path $root 'docs\ON_MACHINE_SMOKE_TEST_RESULTS.json'
    $report | ConvertTo-Json -Depth 7 | Out-File -FilePath $file -Encoding utf8
    Write-Host "Report saved to $file"
}
if(!$report.passed){exit 1}
