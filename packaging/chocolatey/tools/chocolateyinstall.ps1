$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'marksmith'
  fileType       = 'exe'
  softwareName   = 'Marksmith*'
  url64bit       = 'https://github.com/thebubbsy/marksmith/releases/download/v3.0.0/Marksmith-Setup-x64.exe'
  # SHA256 of the released installer (matches the release's checksums.txt asset).
  checksum64     = '48554b5b70ca73eed9469151a40a6479bbb915350ad3f92e4bd01de7beb67979'
  checksumType64 = 'sha256'
  # Inno Setup silent switches
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
