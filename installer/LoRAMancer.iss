; Inno Setup Script for LoRAMancer Desktop
#define MyAppName "LoRAMancer"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "LoRAMancer Team"
#define MyAppURL "https://github.com/loramancer/loramancer"
#define MyAppExeName "LoRAMancer.App.exe"
#ifndef MySourceDir
#define MySourceDir "..\artifacts\staging\LoRAMancer"
#endif

[Setup]
AppId={{D37E88F9-6E53-4872-8C84-B09257C95B32}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\src\LoRAMancer.App\Resources\AppIcon\appicon.ico
OutputDir=..\artifacts
OutputBaseFilename=LoRAMancer-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Resources\AppIcon\appicon.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; IconFilename: "{app}\Resources\AppIcon\appicon.ico"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
