# Signs a Marksmith Pro license key for a customer, using your private key.
# The key is:  base64url(payloadJson) + "." + base64url(RSA-SHA256 signature)
# and is verified offline by the app (LicenseValidator) with the embedded public key.
#
# Usage:
#   pwsh tools/licensing/sign-license.ps1 -Email "buyer@example.com"
#   pwsh tools/licensing/sign-license.ps1 -Email "buyer@example.com" -ExpiresUtc "2027-01-01"   # subscription
#   pwsh tools/licensing/sign-license.ps1 -Email "buyer@example.com" -Order "LS-1234" -Note "launch discount"
#
# A key with no expiry is a perpetual Pro license; with -ExpiresUtc it stops validating after that date.
#
# EVERY key gets a unique serial (jti). That serial is the ONLY way to revoke a key later — after a
# refund, a chargeback, or if a customer posts their key publicly. To revoke: add the serial to
# LicenseValidator.RevokedKeyIds and ship a release. You cannot retrofit a serial onto a key that
# was already sold, which is why one is stamped on every key from the very first sale.
#
# Each issued key is appended to issued-keys.csv (gitignored) so you have a record of who has what.
# Without that ledger you cannot answer "did this person buy?" or "which serial do I revoke?".

param(
  [Parameter(Mandatory=$true)][string]$Email,
  [string]$ExpiresUtc = "",
  [string]$PrivateKeyPath = "",
  [string]$Order = "",
  [string]$Note = "",
  [string]$LedgerPath = ""
)

$ErrorActionPreference = 'Stop'

if (-not $PrivateKeyPath) { $PrivateKeyPath = Join-Path $PSScriptRoot 'private-key.pem' }
if (-not (Test-Path $PrivateKeyPath)) { throw "private-key.pem not found. Run generate-keys.ps1 first." }
if (-not $LedgerPath) { $LedgerPath = Join-Path $PSScriptRoot 'issued-keys.csv' }

if ($Email -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
  throw "'$Email' doesn't look like an email address. The key embeds it and the customer sees it, so typos are expensive."
}

$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem((Get-Content $PrivateKeyPath -Raw))

$exp = 'null'
if ($ExpiresUtc) {
  $expDto = [DateTimeOffset]::Parse($ExpiresUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
  if ($expDto -le [DateTimeOffset]::UtcNow) { throw "-ExpiresUtc '$ExpiresUtc' is in the past; the key would be dead on arrival." }
  $exp = $expDto.ToUnixTimeSeconds()
}

# Serial: date-stamped so a ledger sorts chronologically, plus random bytes so two keys minted in
# the same second can never collide.
$rand = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(4)).ToLowerInvariant()
$keyId = 'MS-' + [DateTime]::UtcNow.ToString('yyyyMMdd') + '-' + $rand

# Compact JSON with the exact property names the app expects.
function JsonEsc([string]$v) { $v.Replace('\','\\').Replace('"','\"') }
$payload = '{"email":"' + (JsonEsc $Email) + '","edition":"pro","exp":' + $exp + ',"iss":"marksmith","jti":"' + $keyId + '"}'

function B64Url([byte[]]$b) { [Convert]::ToBase64String($b).TrimEnd('=').Replace('+','-').Replace('/','_') }
$pbytes = [Text.Encoding]::UTF8.GetBytes($payload)
$sig = $rsa.SignData($pbytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$key = (B64Url $pbytes) + '.' + (B64Url $sig)

# Verify what we just produced before handing it to a paying customer. Signing and verifying are
# different code paths; a key that doesn't round-trip is a support ticket and a refund.
$verifyRsa = [System.Security.Cryptography.RSA]::Create()
$verifyRsa.ImportFromPem($rsa.ExportSubjectPublicKeyInfoPem())
if (-not $verifyRsa.VerifyData($pbytes, $sig, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
  throw "The key failed to verify against its own public key. Do NOT send it."
}

if (-not (Test-Path $LedgerPath)) {
  Set-Content -Path $LedgerPath -Value 'issued_utc,key_id,email,expires_utc,order,note' -NoNewline:$false
}
function CsvEsc([string]$v) { '"' + ($v -replace '"','""') + '"' }
$row = @(
  (CsvEsc ([DateTime]::UtcNow.ToString('o'))),
  (CsvEsc $keyId),
  (CsvEsc $Email),
  (CsvEsc $(if ($ExpiresUtc) { $ExpiresUtc } else { 'perpetual' })),
  (CsvEsc $Order),
  (CsvEsc $Note)
) -join ','
Add-Content -Path $LedgerPath -Value $row

Write-Host "License key for $Email$(if ($ExpiresUtc) { " (expires $ExpiresUtc)" } else { " (perpetual)" }):"
Write-Host ""
Write-Host $key
Write-Host ""
Write-Host "Serial: $keyId   (add this to LicenseValidator.RevokedKeyIds to revoke)"
Write-Host "Logged to: $LedgerPath"
