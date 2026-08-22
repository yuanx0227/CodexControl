[CmdletBinding()]
param(
    [string]$DnsName = 'localhost'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$certificateDirectory = Join-Path $PSScriptRoot 'certs'
New-Item -ItemType Directory -Force -Path $certificateDirectory | Out-Null
$certificate = Join-Path $certificateDirectory 'fullchain.pem'
$privateKey = Join-Path $certificateDirectory 'privkey.pem'

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=$DnsName",
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $subjectAlternativeName = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $subjectAlternativeName.AddDnsName($DnsName)
    $subjectAlternativeName.AddIpAddress([System.Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($subjectAlternativeName.Build())
    $created = $request.CreateSelfSigned(
        [System.DateTimeOffset]::UtcNow.AddMinutes(-5),
        [System.DateTimeOffset]::UtcNow.AddDays(30))
    try {
        [System.IO.File]::WriteAllText($certificate, $created.ExportCertificatePem())
        [System.IO.File]::WriteAllText($privateKey, $rsa.ExportPkcs8PrivateKeyPem())
    }
    finally {
        $created.Dispose()
    }
}
finally {
    $rsa.Dispose()
}

Write-Output "Generated development-only certificate in $certificateDirectory"
