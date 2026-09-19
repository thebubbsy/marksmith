# Marksmith - All-in-One Standalone Publish Script (win-x64 & win-arm64)
# Stops running instances and publishes zero-dependency single-file executables.

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = Get-Location }

Write-Host "[1/3] Stopping existing Marksmith & compiler processes..." -ForegroundColor Cyan
Stop-Process -Name "Marksmith","dotnet","MSBuild","VBCSCompiler" -ErrorAction SilentlyContinue

$ProjectFile = Join-Path $ScriptDir "marksmith-v2\MarkSmith.Desktop\MarkSmith.Desktop.csproj"

Write-Host "[2/3] Publishing Standalone Marksmith Executable (win-x64)..." -ForegroundColor Green
dotnet publish "$ProjectFile" -c Release -r win-x64 --self-contained true /p:Platform=x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "win-x64 publish failed with exit code $LASTEXITCODE" }

Write-Host "[3/3] Publishing Standalone Marksmith Executable (win-arm64)..." -ForegroundColor Green
dotnet publish "$ProjectFile" -c Release -r win-arm64 --self-contained true /p:Platform=arm64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "win-arm64 publish failed with exit code $LASTEXITCODE" }

$X64Exe = Join-Path $ScriptDir "marksmith-v2\MarkSmith.Desktop\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Marksmith.exe"
$Arm64Exe = Join-Path $ScriptDir "marksmith-v2\MarkSmith.Desktop\bin\arm64\Release\net8.0-windows10.0.19041.0\win-arm64\publish\Marksmith.exe"

Write-Host "`n✅ All-in-One Standalone Publish Complete!" -ForegroundColor Cyan
Write-Host "  x64 Binary: $X64Exe" -ForegroundColor Yellow
Write-Host "  arm64 Binary: $Arm64Exe" -ForegroundColor Yellow
