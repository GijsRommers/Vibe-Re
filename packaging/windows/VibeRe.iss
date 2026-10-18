#ifndef AppVersion
  #error Pass AppVersion, AppNumericVersion, SourceDir and OutputDir to ISCC.
#endif

[Setup]
AppId={{4C0B4424-0E54-4A49-912E-C6311CED7731}
AppName=VibeRe
AppVersion={#AppVersion}
VersionInfoVersion={#AppNumericVersion}
AppPublisher=GijsRommers
AppPublisherURL=https://github.com/GijsRommers/Vibe-Re
DefaultDirName={localappdata}\Programs\VibeRe
DefaultGroupName=VibeRe
PrivilegesRequired=lowest
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=vibe-re-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\desktop\VibeRe.Desktop.exe
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VibeRe"; Filename: "{app}\desktop\VibeRe.Desktop.exe"; WorkingDir: "{app}"
Name: "{group}\VibeRe CLI"; Filename: "{cmd}"; Parameters: "/K """"{app}\cli\vibe-re.exe"" --help"""; WorkingDir: "{app}\cli"
Name: "{autodesktop}\VibeRe"; Filename: "{app}\desktop\VibeRe.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\desktop\VibeRe.Desktop.exe"; Description: "Launch VibeRe"; Flags: nowait postinstall skipifsilent
