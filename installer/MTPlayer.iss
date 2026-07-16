#define MyAppName "MT播放器"
#define MyAppVersion "1.1.1"
#define MyAppPublisher "MT Player"
#define MyAppExeName "MTPlayer.exe"

[Setup]
AppId={{B88B7244-BC36-47DC-9E3E-EB131E15A33B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\mtplayer
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=DISCLAIMER.txt
OutputDir=..\artifacts\installer
OutputBaseFilename=MT播放器-Setup-1.1.1
SetupIconFile=..\src\WebHtv.Desktop\Assets\mtplayer.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x86compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: checkedonce

[Files]
Source: "..\artifacts\publish\win-x64\MTPlayer.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: Is64BitInstallMode
Source: "..\artifacts\publish\win-x86\MTPlayer.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: not Is64BitInstallMode

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
