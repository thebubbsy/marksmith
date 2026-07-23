# Signs a Marksmith Pro license key for a customer, using your private key.
# The key is:  base64url(payloadJson) + "." + base64url(RSA-SHA256 signature)
# and is verified offline by the app (LicenseValidator) with the embedded public key.
#
# Usage:
#   pwsh tools/licensing/sign-license.ps1 -Email "buyer@example.com"
#   pwsh tools/licensing/sign-license.ps1 -Email "buyer@example.com" -ExpiresUtc "2027-01-01"   # subscription
#
# A key with no expiry is a perpetual Pro license; with -ExpiresUtc it stops validating after that date.

param(
  [Parameter(Mandatory=$true)][string]$Email,
  [string]$ExpiresUtc = "",
  [string]$PrivateKeyPath = ""
)

if (-not $PrivateKeyPath) { $PrivateKeyPath = Join-Path $PSScriptRoot 'private-key.pem' }
if (-not (Test-Path $PrivateKeyPath)) { throw "private-key.pem not found. Run generate-keys.ps1 first." }

$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem((Get-Content $PrivateKeyPath -Raw))

$exp = 'null'
if ($ExpiresUtc) { $exp = [DateTimeOffset]::Parse($ExpiresUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUnixTimeSeconds() }

# Compact JSON with the exact property names the app expects.
$emailEsc = $Email.Replace('\','\\').Replace('"','\"')
$payload = '{"email":"' + $emailEsc + '","edition":"pro","exp":' + $exp + ',"iss":"marksmith"}'

function B64Url([byte[]]$b) { [Convert]::ToBase64String($b).TrimEnd('=').Replace('+','-').Replace('/','_') }
$pbytes = [Text.Encoding]::UTF8.GetBytes($payload)
$sig = $rsa.SignData($pbytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$key = (B64Url $pbytes) + '.' + (B64Url $sig)

Write-Host "License key for $Email$(if ($ExpiresUtc) { " (expires $ExpiresUtc)" } else { " (perpetual)" }):"
Write-Host ""
Write-Host $key
