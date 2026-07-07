$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'marksmith'
  fileType       = 'exe'
  softwareName   = 'Marksmith*'
  url64bit       = 'https://github.com/thebubbsy/marksmith/releases/download/v1.2.5/Marksmith-Setup-x64.exe'
  # Replace with the real SHA256 of the released installer (the release workflow prints it).
  checksum64     = '8774914a9787cb076cce45eb57d704c9f09c9e8e18a3d14c72cdd75605d4712f'
  checksumType64 = 'sha256'
  # Inno Setup silent switches
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
