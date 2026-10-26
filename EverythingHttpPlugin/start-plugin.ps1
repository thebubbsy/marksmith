<#
.SYNOPSIS
  Launches the native OmniSight Everything HTTP Server Plugin & Dashboard
#>
[CmdletBinding()]
param(
    [int]$Port = 8080,
    [switch]$NoBrowser
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $scriptDir "publish\EverythingHttpPlugin.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "[Build] Executable not found, compiling..." -ForegroundColor Yellow
    dotnet publish (Join-Path $scriptDir "EverythingHttpPlugin.csproj") -c Release -o (Join-Path $scriptDir "publish") /p:PublishSingleFile=true /p:SelfContained=false
}

if (-not (Test-Path $exePath)) {
    Write-Error "Failed to build or find EverythingHttpPlugin.exe"
    exit 1
}

Write-Host "⚡ Starting OmniSight Native Plugin on http://127.0.0.1:$Port/ ..." -ForegroundColor Cyan

if (-not $NoBrowser) {
    Start-Job -ScriptBlock {
        param($p)
        Start-Sleep -Seconds 1
        Start-Process "http://127.0.0.1:$p/"
    } -ArgumentList $Port | Out-Null
}

& $exePath --port $Port
