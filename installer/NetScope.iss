#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif

[Setup]
AppId={{F9061E27-6AFF-4D31-91E1-9079AFEDA09B}
AppName=NetScope
AppVersion={#AppVersion}
AppVerName=NetScope {#AppVersion}
AppPublisher=NetScope
AppPublisherURL=https://github.com/yourlocalaccountant/netscope
DefaultDirName={localappdata}\Programs\NetScope
DefaultGroupName=NetScope
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=NetScope-Setup-x64
UninstallDisplayIcon={app}\NetScope.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\NetScope.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\NetScope"; Filename: "{app}\NetScope.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\NetScope"; Filename: "{app}\NetScope.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\NetScope.exe"; Description: "Launch NetScope"; Flags: nowait postinstall skipifsilent
