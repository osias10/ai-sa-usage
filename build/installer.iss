#define AppName "AI SA"
#ifndef AppVersion
  #error AppVersion must be supplied by build/Packaging.csproj
#endif
#define AppExe "AIUsageWidget.exe"
[Setup]
AppId={{EA2541B7-CE15-4DF9-A1EF-4273D81B8DB6}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=AI SA
DefaultDirName={localappdata}\Programs\AIUsageWidget
DefaultGroupName={#AppName}
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir=..\.artifacts\installer
OutputBaseFilename=AISA-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\AIUsage.App\Assets\AISA.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no
[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "바탕 화면 바로가기 만들기"; Flags: unchecked
[Files]
Source: "..\.artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
[InstallDelete]
Type: files; Name: "{userprograms}\AI Usage Widget\AI Usage Widget.lnk"
Type: dirifempty; Name: "{userprograms}\AI Usage Widget"
Type: files; Name: "{userdesktop}\AI Usage Widget.lnk"
[Run]
Filename: "{app}\{#AppExe}"; Description: "AI SA 실행"; Flags: nowait postinstall skipifsilent
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "AIUsageWidget"; Flags: uninsdeletevalue
; Preserve account state on upgrade/uninstall. Users can disconnect in the app before removal.
