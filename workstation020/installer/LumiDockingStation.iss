#define MyAppName "Lumi Docking Station"
#define MyAppVersion "0.2.1"
#define MyAppPublisher "Distressed Elk Acres"
#define MyAppExeName "LumiDockingStation.exe"

[Setup]
AppId={{7D5967F2-3212-49AB-B1EF-96A87790C43A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Lumi Docking Station
DefaultGroupName=Lumi Docking Station
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=Lumi-Docking-Station-Setup-0.2.1
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\platform-tools\*"; DestDir: "{localappdata}\Android\Sdk\platform-tools"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Lumi Docking Station"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Lumi Docking Station"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Lumi Docking Station"; Flags: nowait postinstall skipifsilent
