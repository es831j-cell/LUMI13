#define MyAppName "Lumi Workstation"
#define MyAppVersion "0.6.4"
#define MyAppPublisher "Distressed Elk Acres"
#define MyAppExeName "LumiDockingStation.exe"

[Setup]
AppId={{7D5967F2-3212-49AB-B1EF-96A87790C43A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Lumi Docking Station
DefaultGroupName=Lumi Workstation
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=Lumi-Workstation-Setup-0.6.4
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\platform-tools\*"; DestDir: "{localappdata}\Android\Sdk\platform-tools"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Lumi Workstation"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Lumi Workstation"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Lumi Workstation"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ExistingAdb: String;
begin
  { Stop Lumi first so its phone watcher cannot immediately restart ADB. }
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM "{#MyAppExeName}" /F >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);

  { Ask the currently installed bundled ADB server to shut down cleanly. }
  ExistingAdb := ExpandConstant('{app}\platform-tools\adb.exe');
  if FileExists(ExistingAdb) then
    Exec(ExistingAdb, 'kill-server', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { If a stale adb.exe survived, release the executable lock before replacement. }
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM adb.exe /F >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
  Result := '';
end;
