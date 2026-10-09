<#
.SYNOPSIS
  Creates Kakitome's self-signed code-signing certificate ONCE (docs/10): subject CN=Kakitome (must equal the MSIX
  Publisher), 5-year validity. Writes Kakitome_signing.pfx and Kakitome_signing.cer to %USERPROFILE%\Kakitome-signing
  (outside the repository) and stores the random .pfx password in Windows Credential Manager. Refuses to run again
  when the .pfx exists: replacing the certificate breaks in-place upgrades.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\New-SigningCertificate.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SigningCredential.psm1') -Force
$dir = Get-SigningDirectory
$pfx = Join-Path $dir 'Kakitome_signing.pfx'
$cer = Join-Path $dir 'Kakitome_signing.cer'
if (Test-Path $pfx) { Write-Host "The signing certificate already exists: $pfx (not replaced)."; return }

New-Item -ItemType Directory -Force -Path $dir | Out-Null
$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=Kakitome' -FriendlyName 'Kakitome code signing' `
    -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
    -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(5)
try {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.Dispose()
    $password = [Convert]::ToBase64String($bytes)
    Set-SigningPassword $password
    $secure = ConvertTo-SecureString -String $password -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
}
finally {
    # The .pfx is the only copy that is needed; keep the personal store clean.
    Remove-Item -Path ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -DeleteKey -ErrorAction SilentlyContinue
}

[ordered]@{
    pfx = $pfx
    cer = $cer
    subject = $cert.Subject
    thumbprint = $cert.Thumbprint
    expires = $cert.NotAfter.ToString('yyyy-MM-dd')
    password = 'stored in Windows Credential Manager (generic credential "Kakitome:code-signing")'
} | ConvertTo-Json
