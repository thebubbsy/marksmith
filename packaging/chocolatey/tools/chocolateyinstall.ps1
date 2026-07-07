$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'marksmith'
  fileType       = 'exe'
  softwareName   = 'Marksmith*'
  url64bit       = 'https://github.com/thebubbsy/marksmith/releases/download/v1.1.3/Marksmith-Setup-x64.exe'
  # Replace with the real SHA256 of the released installer (the release workflow prints it).
  checksum64     = '98e3d66ae96c0165d8f02c73e671547a0d95853ee01164a43ad01f7f96de6a69'
  checksumType64 = 'sha256'
  # Inno Setup silent switches
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
