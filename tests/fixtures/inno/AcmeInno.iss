[Setup]
AppName=Acme Inno
AppVersion=1.0.0
AppPublisher=Acme Test
DefaultDirName={autopf}\AcmeInno
DefaultGroupName=Acme Inno
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=AcmeInnoSetup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\AcmeInno.exe
WizardStyle=modern

[Files]
Source: "payload\AcmeInno.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Acme Inno"; Filename: "{app}\AcmeInno.exe"
Name: "{autodesktop}\Acme Inno"; Filename: "{app}\AcmeInno.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked
