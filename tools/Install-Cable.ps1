$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'VB-CABLE\VBCABLE_Setup_x64.exe'
if (-not (Test-Path -LiteralPath $installer)) {
    Write-Host 'Download VB-CABLE from https://vb-audio.com/Cable/ and extract it into tools\VB-CABLE next to this script.'
    Write-Host 'Then run this script again, or run the official installer as administrator.'
    exit 1
}
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Vincent BUREL|BUREL VINCENT|VB-Audio') {
    throw "Installer signature verification failed: $($signature.Status)"
}
Write-Host 'Approve the Windows admin prompt, then click Install Driver. Restart Windows if requested.'
Start-Process -FilePath $installer -Verb RunAs -Wait
