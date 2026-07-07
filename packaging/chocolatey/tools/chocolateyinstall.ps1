$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'marksmith'
  fileType       = 'exe'
  softwareName   = 'Marksmith*'
  url64bit       = 'https://github.com/thebubbsy/marksmith/releases/download/v1.2.5/Marksmith-Setup-x64.exe'
  # Replace with the real SHA256 of the released installer (the release workflow prints it).
  checksum64     = 'd8e96f72c67bedccbdfb49b3ab043e7cac4dcb3e8dc8a556017f47e205d73131'
  checksumType64 = 'sha256'
  # Inno Setup silent switches
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
