$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName   = 'marksmith'
  softwareName  = 'Marksmith*'
  fileType      = 'exe'
  silentArgs    = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
  validExitCodes = @(0)
}

# Locate the Inno Setup uninstaller entry registered by the installer.
[array]$key = Get-UninstallRegistryKey -SoftwareName $packageArgs['softwareName']

if ($key.Count -eq 1) {
  $key | ForEach-Object {
    $packageArgs['file'] = "$($_.UninstallString)"
    # Inno's UninstallString already points at unins000.exe; strip any quoting.
    $packageArgs['file'] = $packageArgs['file'].Trim('"')
    Uninstall-ChocolateyPackage @packageArgs
  }
} elseif ($key.Count -eq 0) {
  Write-Warning "$($packageArgs['packageName']) has already been uninstalled by other means."
} elseif ($key.Count -gt 1) {
  Write-Warning "$($key.Count) matches found for '$($packageArgs['softwareName'])'. Uninstall manually."
  $key | ForEach-Object { Write-Warning "- $($_.DisplayName)" }
}
