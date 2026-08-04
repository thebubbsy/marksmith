# Local install of the marksmith-office plugin (no network): copies the built host payload +
# plugin.json into the app's user plugin directory. Run after Remove in Settings, or to seed
# a fresh machine:  powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = "Stop"

$hostBin = Join-Path $PSScriptRoot "office-host\bin\Debug\net8.0-windows10.0.19041.0"
if (-not (Test-Path (Join-Path $hostBin "marksmith-office-host.exe"))) {
    Write-Host "host not built - run: dotnet build office-host\marksmith-office-host.csproj -c Debug"
    exit 1
}

$dest = Join-Path $env:LOCALAPPDATA "MarkSmith\Plugins\marksmith-office"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $hostBin "*") $dest -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "plugin.json") $dest -Force

Write-Host "marksmith-office installed to $dest"
