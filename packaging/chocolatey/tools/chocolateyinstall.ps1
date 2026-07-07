$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'marksmith'
  fileType       = 'exe'
  softwareName   = 'Marksmith*'
  url64bit       = 'https://github.com/thebubbsy/marksmith/releases/download/v1.2.3/Marksmith-Setup-x64.exe'
  # Replace with the real SHA256 of the released installer (the release workflow prints it).
  checksum64     = '6133df3398df9a7f4c184ee5686f7c5f8fe5ebbd900b1b2636d5172698b01d79'
  checksumType64 = 'sha256'
  # Inno Setup silent switches
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
