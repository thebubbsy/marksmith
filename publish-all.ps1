# Marksmith - All-in-One Standalone Publish Script (x64 & x86)
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

Write-Host "[3/3] Publishing Standalone Marksmith Executable (win-x86)..." -ForegroundColor Green
dotnet publish "$ProjectFile" -c Release -r win-x86 --self-contained true /p:Platform=x86 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "win-x86 publish failed with exit code $LASTEXITCODE" }

$X64Exe = Join-Path $ScriptDir "marksmith-v2\MarkSmith.Desktop\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Marksmith.exe"
$X86Exe = Join-Path $ScriptDir "marksmith-v2\MarkSmith.Desktop\bin\x86\Release\net8.0-windows10.0.19041.0\win-x86\publish\Marksmith.exe"

Write-Host "`n✅ All-in-One Standalone Publish Complete!" -ForegroundColor Cyan
Write-Host "  x64 Binary: $X64Exe" -ForegroundColor Yellow
Write-Host "  x86 Binary: $X86Exe" -ForegroundColor Yellow
