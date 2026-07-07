; Inno Setup script for Marksmith
; Builds a silent-capable per-machine installer from the self-contained publish output.
; Build:  iscc packaging\installer\marksmith.iss
;         (override the publish dir with:  iscc /DPublishDir="...\win-x64\publish" ...)
; The produced Marksmith-Setup-x64.exe is the single artifact consumed by winget,
; Chocolatey, and a Microsoft Store EXE/MSI submission.

#define AppName "Marksmith"
#define AppVersion "1.2.1"
#define AppPublisher "thebubbsy"
#define AppURL "https://github.com/thebubbsy/marksmith"
#define AppExe "Marksmith.exe"

#ifndef PublishDir
  #define PublishDir "..\..\MdToPdf\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"
#endif

[Setup]
; A stable AppId keeps upgrades/uninstall coherent across versions — do not change it.
AppId={{7E9B2C4A-3D5F-4A1E-9C8B-6F2D1A4B7E30}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\..\dist_installer
OutputBaseFilename=Marksmith-Setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
LicenseFile=..\..\LICENSE
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
