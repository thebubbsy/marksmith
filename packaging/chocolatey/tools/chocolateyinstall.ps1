$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'marksmith'
  fileType       = 'exe'
  softwareName   = 'Marksmith*'
  url64bit       = 'https://github.com/thebubbsy/marksmith/releases/download/v1.0.0/Marksmith-Setup-x64.exe'
  # Replace with the real SHA256 of the released installer (the release workflow prints it).
  checksum64     = '0000000000000000000000000000000000000000000000000000000000000000'
  checksumType64 = 'sha256'
  # Inno Setup silent switches
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
