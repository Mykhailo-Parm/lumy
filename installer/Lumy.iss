; Lumy setup: per-user install, no administrator rights needed.
; Built in CI with: ISCC.exe /DAppVersion=0.6.0 /DSourceDir=<publish folder> installer\Lumy.iss

#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\Lumy-win-x64"
#endif

[Setup]
AppId={{7C3E5A1B-4F2D-4B8E-9A61-2D5C8E0F4A17}
AppName=Lumy
AppVersion={#AppVersion}
AppVerName=Lumy {#AppVersion}
AppPublisher=Mykhailo Parm
AppPublisherURL=https://github.com/Mykhailo-Parm/lumy
DefaultDirName={localappdata}\Programs\Lumy
DefaultGroupName=Lumy
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=Lumy-Setup-{#AppVersion}
SetupIconFile=..\src\Lumy.App\Assets\Lumy.ico
UninstallDisplayIcon={app}\Lumy.exe
UninstallDisplayName=Lumy
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.DesktopIcon=Значок на рабочем столе
en.DesktopIcon=Desktop shortcut
ru.FolderMenu=Пункт «Открыть в Lumy» в меню папок
en.FolderMenu="Open in Lumy" on folders' right-click menu
ru.OpenInLumy=Открыть в Lumy
en.OpenInLumy=Open in Lumy
ru.LaunchLumy=Запустить Lumy
en.LaunchLumy=Start Lumy

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
Name: "foldermenu"; Description: "{cm:FolderMenu}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Lumy"; Filename: "{app}\Lumy.exe"
Name: "{userdesktop}\Lumy"; Filename: "{app}\Lumy.exe"; Tasks: desktopicon

[Registry]
; Right-click a folder, or the empty space inside one, in Explorer: "Открыть в Lumy".
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Lumy"; ValueType: string; ValueData: "{cm:OpenInLumy}"; Flags: uninsdeletekey; Tasks: foldermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Lumy"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\Lumy.exe"",0"; Tasks: foldermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Lumy\command"; ValueType: string; ValueData: """{app}\Lumy.exe"" ""%1"""; Tasks: foldermenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Lumy"; ValueType: string; ValueData: "{cm:OpenInLumy}"; Flags: uninsdeletekey; Tasks: foldermenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Lumy"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\Lumy.exe"",0"; Tasks: foldermenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Lumy\command"; ValueType: string; ValueData: """{app}\Lumy.exe"" ""%V"""; Tasks: foldermenu

[Run]
Filename: "{app}\Lumy.exe"; Description: "{cm:LaunchLumy}"; Flags: nowait postinstall skipifsilent

; The library, settings and thumbnails in %LOCALAPPDATA%\Lumy stay after uninstalling, so ratings survive a reinstall.
