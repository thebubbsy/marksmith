# Marksmith - All-in-One Build Script (x64 & x86)
# Stops running instances and compiles debug/release binaries for both architectures.

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = Get-Location }

Write-Host "[1/3] Stopping existing Marksmith & compiler processes..." -ForegroundColor Cyan
Stop-Process -Name "Marksmith","dotnet","MSBuild","VBCSCompiler" -ErrorAction SilentlyContinue

$ProjectFile = Join-Path $ScriptDir "marksmith-v2\MarkSmith.Desktop\MarkSmith.Desktop.csproj"

Write-Host "[2/3] Building Marksmith Desktop (x64 Release)..." -ForegroundColor Green
dotnet build "$ProjectFile" -c Release /p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { throw "x64 build failed with exit code $LASTEXITCODE" }

Write-Host "[3/3] Building Marksmith Desktop (x86 Release)..." -ForegroundColor Green
dotnet build "$ProjectFile" -c Release /p:Platform=x86 --nologo
if ($LASTEXITCODE -ne 0) { throw "x86 build failed with exit code $LASTEXITCODE" }

Write-Host "`n✅ All-in-One Build Complete! (x64 & x86)" -ForegroundColor Cyan
