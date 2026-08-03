# Generates the RSA keypair used to sign Marksmith license keys.
#   - Writes private-key.pem  (KEEP SECRET — gitignored; never commit or share it)
#   - Writes public-key.pem   (safe to share; this is what the app embeds)
#
# After running, paste the PUBLIC key into LicenseValidator.PublicKeyPem
# (MarkSmith.Core/Services/LicenseValidator.cs), replacing the placeholder key.
#
# Usage:  pwsh tools/licensing/generate-keys.ps1

$dir = $PSScriptRoot
$rsa = [System.Security.Cryptography.RSA]::Create(2048)
Set-Content -Path (Join-Path $dir 'private-key.pem') -Value $rsa.ExportRSAPrivateKeyPem() -NoNewline
$pub = $rsa.ExportSubjectPublicKeyInfoPem()
Set-Content -Path (Join-Path $dir 'public-key.pem') -Value $pub -NoNewline

Write-Host "Wrote private-key.pem (SECRET) and public-key.pem"
Write-Host ""
Write-Host "Paste this PUBLIC key into MarkSmith.Core/Services/LicenseValidator.cs (PublicKeyPem):"
Write-Host ""
Write-Host $pub
