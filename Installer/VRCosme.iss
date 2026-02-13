; VRCosme Inno Setup Script
; 自己完結型の単体 exe をインストーラーで配布する

#define MyAppName "VRCosme"
#define MyAppVersion "0.3.0-beta"
#define MyAppPublisher "VRCosme"
#define MyAppExeName "VRCosme.exe"

[Setup]
AppId={{B3F7A2D1-9C4E-4A8B-B5D6-7E1F3C2A9D04}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir=..\Installer\Output
OutputBaseFilename=VRCosme_Setup
SetupIconFile=..\Resources\VRCosme_Icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=yes
UsePreviousLanguage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=LICENSE.txt
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"; LicenseFile: "LICENSE.en-US.txt"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"; LicenseFile: "LICENSE.txt"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"; LicenseFile: "LICENSE.ko-KR.txt"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"; LicenseFile: "LICENSE.zh-CN.txt"
Name: "chinesetraditional"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"; LicenseFile: "LICENSE.zh-TW.txt"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 自己完結型の単体 exe とデバッグシンボルを含める (.lib は除外)
Source: "..\bin\Release\net10.0-windows\win-x64\publish\VRCosme.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net10.0-windows\win-x64\publish\VRCosme.pdb"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; アンインストール時にアプリフォルダを完全に削除
Type: filesandordirs; Name: "{app}"

[Code]
procedure InitializeWizard;
begin
  WizardForm.LicenseMemo.ReadOnly := True;
end;

function GetSelectedLanguageCode: string;
begin
  if ActiveLanguage = 'english' then
    Result := 'en-US'
  else if ActiveLanguage = 'korean' then
    Result := 'ko-KR'
  else if ActiveLanguage = 'chinesesimplified' then
    Result := 'zh-CN'
  else if ActiveLanguage = 'chinesetraditional' then
    Result := 'zh-TW'
  else
    Result := 'ja-JP';
end;

procedure WriteInitialLanguageSetting;
var
  DataDir: string;
  SettingsPath: string;
  SettingsJson: string;
begin
  DataDir := ExpandConstant('{localappdata}\VRCosme');
  SettingsPath := DataDir + '\settings.json';
  if FileExists(SettingsPath) then
    Exit;

  if (not DirExists(DataDir)) and (not ForceDirectories(DataDir)) then
    Exit;

  SettingsJson := Format('{"Language":"%s"}', [GetSelectedLanguageCode]);
  SaveStringToFile(SettingsPath, SettingsJson, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteInitialLanguageSetting;
end;
