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
UsePreviousAppDir=yes
DisableDirPage=no
AlwaysShowDirOnReadyPage=yes
DirExistsWarning=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
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

[Registry]
Root: HKCU; Subkey: "Software\LoRAMancer"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekeyifempty

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  IsUpgrade: Boolean;
  ExistingInstallDir: String;
  CustomizeCheckBox: TCheckBox;

function InitializeSetup(): Boolean;
var
  RegVal: String;
begin
  IsUpgrade := False;
  ExistingInstallDir := '';

  // 1. Check HKCU\Software\LoRAMancer\InstallPath (shared with PowerShell installer)
  if RegQueryStringValue(HKCU, 'Software\LoRAMancer', 'InstallPath', RegVal) then
  begin
    if DirExists(RegVal) and (FileExists(ExpandConstant(RegVal + '\{#MyAppExeName}')) or FileExists(ExpandConstant(RegVal + '\bin\{#MyAppExeName}'))) then
    begin
      ExistingInstallDir := RegVal;
      IsUpgrade := True;
    end;
  end;

  // 2. Check Inno Setup uninstall registry in HKCU
  if not IsUpgrade then
  begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1', 'InstallLocation', RegVal) then
    begin
      if DirExists(RegVal) and (FileExists(ExpandConstant(RegVal + '\{#MyAppExeName}')) or FileExists(ExpandConstant(RegVal + '\bin\{#MyAppExeName}'))) then
      begin
        ExistingInstallDir := RegVal;
        IsUpgrade := True;
      end;
    end;
  end;

  // 3. Check Inno Setup uninstall registry in HKLM
  if not IsUpgrade then
  begin
    if RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1', 'InstallLocation', RegVal) then
    begin
      if DirExists(RegVal) and (FileExists(ExpandConstant(RegVal + '\{#MyAppExeName}')) or FileExists(ExpandConstant(RegVal + '\bin\{#MyAppExeName}'))) then
      begin
        ExistingInstallDir := RegVal;
        IsUpgrade := True;
      end;
    end;
  end;

  Result := True;
end;

procedure InitializeWizard();
begin
  if IsUpgrade and (ExistingInstallDir <> '') then
  begin
    WizardForm.DirEdit.Text := ExistingInstallDir;
    WizardForm.WelcomeLabel1.Caption := 'Update LoRAMancer';
    WizardForm.WelcomeLabel2.Caption := 'An existing installation of LoRAMancer was detected at:' + #13#10#13#10 +
      '  ' + ExistingInstallDir + #13#10#13#10 +
      'Setup will update this installation in-place to version {#MyAppVersion}.' + #13#10#13#10 +
      'Click Next to proceed with the update immediately.';

    CustomizeCheckBox := TCheckBox.Create(WizardForm);
    CustomizeCheckBox.Parent := WizardForm.WelcomePage;
    CustomizeCheckBox.Caption := 'Change installation folder or advanced options';
    CustomizeCheckBox.Left := WizardForm.WelcomeLabel2.Left;
    CustomizeCheckBox.Top := WizardForm.WelcomePage.Height - 35;
    CustomizeCheckBox.Width := WizardForm.WelcomePage.Width - WizardForm.WelcomeLabel2.Left;
    CustomizeCheckBox.Checked := False;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if IsUpgrade and (CustomizeCheckBox <> nil) and (not CustomizeCheckBox.Checked) then
  begin
    // When updating, skip License, Directory, and Tasks pages unless user requested custom options
    if (PageID = wpLicense) or (PageID = wpSelectDir) or (PageID = wpSelectTasks) then
    begin
      Result := True;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if IsUpgrade then
  begin
    if CurPageID = wpReady then
    begin
      WizardForm.NextButton.Caption := '&Update';
    end;
  end;
end;
