#ifndef ScoutVersion
  #define ScoutVersion "0.1.0-beta.3"
#endif
#ifndef ScoutFileVersion
  #define ScoutFileVersion "0.1.0.3"
#endif

[Setup]
AppId={{52EB8A04-8C08-4BEE-AE6D-11BC4DD9D699}
AppName=Scout
AppVersion={#ScoutVersion}
AppVerName=Scout {#ScoutVersion}
AppPublisher=Scout
AppPublisherURL=https://adilzubair.github.io/scout-site/
AppSupportURL=https://github.com/adilzubair/scout-site/issues
DefaultDirName={localappdata}\Programs\Scout
DefaultGroupName=Scout
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
AppMutex=Local\Scout.Launcher
CloseApplications=yes
UninstallDisplayIcon={app}\Scout.exe
SetupIconFile=..\src\Scout.App\scout.ico
OutputDir=..\artifacts
OutputBaseFilename=Scout-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoDescription=Scout installer
VersionInfoVersion={#ScoutFileVersion}

[Files]
Source: "..\artifacts\Scout\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Scout"; Filename: "{app}\Scout.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall Scout"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\Scout.exe"; Description: "Launch Scout"; Flags: postinstall nowait
