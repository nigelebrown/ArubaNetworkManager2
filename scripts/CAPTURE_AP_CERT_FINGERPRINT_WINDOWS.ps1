# Reads the TLS certificate fingerprint ONLY; never sends authentication credentials.
# The callback does not establish trust: compare the output with an out-of-band certified value.
param(
    [Parameter(Mandatory=$true)][string]$TargetIp,
    [int]$Port = 4343
)
$ErrorActionPreference='Stop'
$tcp = New-Object System.Net.Sockets.TcpClient
$stream=$null
try {
    $tcp.Connect($TargetIp,$Port)
    # Untrusted certificate allowed for fingerprint CAPTURE ONLY; no API or password follows.
    $callback = [System.Net.Security.RemoteCertificateValidationCallback]{param($sender,$certificate,$chain,$errors) return $true}
    $stream = New-Object System.Net.Security.SslStream($tcp.GetStream(),$false,$callback)
    $stream.AuthenticateAsClient($TargetIp)
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($stream.RemoteCertificate)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fingerprint = [BitConverter]::ToString($sha.ComputeHash($cert.RawData)).Replace('-','')
    } finally {$sha.Dispose()}
    Write-Host "Server certificate SHA256 at ${TargetIp}:$Port : $fingerprint"
    Write-Host 'Verify fingerprint independently with the authorized network administrator before trusting it.'
} finally {
    if($stream){$stream.Dispose()}
    $tcp.Dispose()
}
