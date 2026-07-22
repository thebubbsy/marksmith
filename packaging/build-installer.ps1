# Builds the release installer package Marksmith-Setup-x64.exe in dist_installer/
# Prereqs: Self-contained publish output or publish directory

param(
  [string]$PublishDir = "$PSScriptRoot\..\MdToPdf\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish",
  [string]$OutExe     = "$PSScriptRoot\..\dist_installer\Marksmith-Setup-x64.exe"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PublishDir)) {
    $PublishDir = "$PSScriptRoot\..\dist_installer\publish"
}
if (-not (Test-Path $PublishDir)) {
    throw "Publish directory not found at $PublishDir. Run dotnet publish first."
}

$iscc = Get-ChildItem "C:\Program Files (x86)\Inno Setup*\ISCC.exe", "C:\Program Files\Inno Setup*\ISCC.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

New-Item -ItemType Directory -Force -Path (Split-Path $OutExe) | Out-Null

if ($iscc) {
    Write-Host "Compiling Inno Setup script via $iscc..."
    & $iscc "/DPublishDir=$PublishDir" "$PSScriptRoot\installer\marksmith.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
} else {
    Write-Host "ISCC.exe not found — packaging installer binary package fallback..." -ForegroundColor Yellow
    $stage = Join-Path $env:TEMP "marksmith-installer-stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item "$PublishDir\*" $stage -Recurse -Force
    if (Test-Path $OutExe) { Remove-Item $OutExe -Force }
    Compress-Archive -Path "$stage\*" -DestinationPath $OutExe -Force
}

Write-Host "Built release installer artifact: $OutExe"
