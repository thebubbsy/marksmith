# Publishes an UPDATE of the Marksmith Connector to the Chrome Web Store via the official API.
# (First-time listing must be created once in the dashboard — see PUBLISH_KIT.md.)
#
# One-time setup (5 min, do once):
#   1. https://console.cloud.google.com → create/select a project (signed in as mbubbtech@gmail.com)
#   2. APIs & Services → Library → enable "Chrome Web Store API"
#   3. APIs & Services → Credentials → Create Credentials → OAuth client ID → Desktop app
#      → note the Client ID + Client Secret
#   4. OAuth consent screen → add mbubbtech@gmail.com as a test user
#   5. Run:  .\publish-update.ps1 -Setup   (opens browser, prints the refresh token; paste all
#      three values into store-credentials.json — see template below)
#
# Every release after that:  .\publish-update.ps1   (bumps nothing itself — bump manifest.json first)
#
# store-credentials.json format (NEVER commit this file):
#   { "clientId": "...", "clientSecret": "...", "refreshToken": "...", "itemId": "<from dashboard URL>" }

param([switch]$Setup)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$credPath = Join-Path $root 'store-credentials.json'
$extensionDir = Join-Path (Split-Path $root) 'extension'

if ($Setup) {
    $clientId = Read-Host 'Client ID'
    $clientSecret = Read-Host 'Client Secret'
    $authUrl = "https://accounts.google.com/o/oauth2/v2/auth?client_id=$clientId&redirect_uri=urn:ietf:wg:oauth:2.0:oob&response_type=code&scope=https://www.googleapis.com/auth/chromewebstore"
    Write-Host "Opening browser for consent... paste the code it gives you." -ForegroundColor Cyan
    Start-Process $authUrl
    $code = Read-Host 'Authorization code'
    $tok = Invoke-RestMethod -Method Post -Uri 'https://oauth2.googleapis.com/token' -Body @{
        client_id = $clientId; client_secret = $clientSecret; code = $code
        grant_type = 'authorization_code'; redirect_uri = 'urn:ietf:wg:oauth:2.0:oob'
    }
    $itemId = Read-Host 'Extension item ID (32 chars, from the dashboard item URL)'
    @{ clientId = $clientId; clientSecret = $clientSecret; refreshToken = $tok.refresh_token; itemId = $itemId } |
        ConvertTo-Json | Set-Content $credPath
    Write-Host "Saved $credPath — you're set. Run without -Setup to publish updates." -ForegroundColor Green
    return
}

if (-not (Test-Path $credPath)) { throw "Missing $credPath — run .\publish-update.ps1 -Setup first." }
$cred = Get-Content $credPath | ConvertFrom-Json

# Fresh access token from the refresh token
$access = (Invoke-RestMethod -Method Post -Uri 'https://oauth2.googleapis.com/token' -Body @{
    client_id = $cred.clientId; client_secret = $cred.clientSecret
    refresh_token = $cred.refreshToken; grant_type = 'refresh_token'
}).access_token

# Zip current extension source
$manifest = Get-Content (Join-Path $extensionDir 'manifest.json') | ConvertFrom-Json
$zip = Join-Path $root "mdpdfm-connector-$($manifest.version).zip"
if (Test-Path $zip) { throw "$zip already exists — bump the version in manifest.json first." }
$stage = Join-Path $env:TEMP "cws_pkg_$(Get-Random)"
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item "$extensionDir\manifest.json","$extensionDir\background.js","$extensionDir\options.html","$extensionDir\options.js" $stage
Copy-Item "$extensionDir\icons" "$stage\icons" -Recurse
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Write-Host "Packaged v$($manifest.version) -> $zip"

# Upload + publish
$headers = @{ Authorization = "Bearer $access"; 'x-goog-api-version' = '2' }
$up = Invoke-RestMethod -Method Put -Uri "https://www.googleapis.com/upload/chromewebstore/v1.1/items/$($cred.itemId)" -Headers $headers -InFile $zip -ContentType 'application/zip'
if ($up.uploadState -ne 'SUCCESS') { throw "Upload failed: $($up | ConvertTo-Json -Depth 5)" }
Write-Host "Upload OK" -ForegroundColor Green

$pub = Invoke-RestMethod -Method Post -Uri "https://www.googleapis.com/chromewebstore/v1.1/items/$($cred.itemId)/publish" -Headers $headers
Write-Host "Publish status: $($pub.status -join ', ')" -ForegroundColor Green
Write-Host "v$($manifest.version) submitted — it goes live when review passes."
