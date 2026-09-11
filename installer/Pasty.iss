#ifndef MyAppVersion
  #error 必须通过 /DMyAppVersion=... 传入版本号
#endif
#ifndef SourceDir
  #error 必须通过 /DSourceDir=... 传入构建输出目录
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{A94A72B1-70A6-4BFA-B71B-7C89268D9875}
AppName=Pasty
AppVersion={#MyAppVersion}
AppPublisher=Gongsc
AppPublisherURL=https://github.com/Gongsc/Pasty
AppSupportURL=https://github.com/Gongsc/Pasty/issues
AppUpdatesURL=https://github.com/Gongsc/Pasty/releases
DefaultDirName={localappdata}\Programs\Pasty
DefaultGroupName=Pasty
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Pasty-v{#MyAppVersion}-win-x64-Setup
SetupIconFile={#SourceDir}\Assets\Pasty.ico
UninstallDisplayIcon={app}\Pasty.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Pasty_SingleInstance

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Pasty"; Filename: "{app}\Pasty.exe"
Name: "{autodesktop}\Pasty"; Filename: "{app}\Pasty.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Pasty"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\Pasty.exe"; Description: "{cm:LaunchProgram,Pasty}"; Flags: nowait postinstall skipifsilent
