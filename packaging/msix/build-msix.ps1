# Builds an MSIX from the self-contained publish output.
# Prereqs: Windows 10/11 SDK (makeappx.exe, signtool.exe) — already present on this machine
# at C:\Program Files (x86)\Windows Kits\10\bin\<ver>\x64\.
#
# NOTE: This is the OPTIONAL Store path. You must first edit Package.appxmanifest with your
# Partner Center Identity (Name + Publisher). For a Store submission you upload the UNSIGNED
# .msix — the Store re-signs it. To sideload/test locally, sign it with a self-signed cert.

param(
  [string]$PublishDir = "$PSScriptRoot\..\..\MdToPdf\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish",
  [string]$OutMsix    = "$PSScriptRoot\..\..\dist_installer\Marksmith-1.0.0-x64.msix",
  [string]$CertPfx    = "",   # optional: path to a .pfx to sign for local sideloading
  [string]$CertPass   = ""
)

$ErrorActionPreference = 'Stop'

$makeappx = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe" |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $makeappx) { throw "makeappx.exe not found — install the Windows 10/11 SDK." }

# Stage: publish output + manifest + assets
$stage = Join-Path $env:TEMP "marksmith-msix-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item "$PublishDir\*" $stage -Recurse -Force
Copy-Item "$PSScriptRoot\Package.appxmanifest" (Join-Path $stage "AppxManifest.xml") -Force
Copy-Item "$PSScriptRoot\assets" (Join-Path $stage "assets") -Recurse -Force

New-Item -ItemType Directory -Force -Path (Split-Path $OutMsix) | Out-Null
& $makeappx pack /d $stage /p $OutMsix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }
Write-Host "Built $OutMsix"

if ($CertPfx) {
  $signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" |
              Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
  & $signtool sign /fd SHA256 /a /f $CertPfx /p $CertPass $OutMsix
  if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
  Write-Host "Signed $OutMsix"
}
