#ifndef SourceRoot
  #define SourceRoot "..\out\release\agent-win-x64"
#endif
#ifndef OutputRoot
  #define OutputRoot "..\out\release\installer"
#endif
#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif
#ifndef OutputBaseName
  #define OutputBaseName "CodexControl-Setup-UNSIGNED"
#endif

[Setup]
AppId={{C22D5E9C-9CA2-4B7C-97F3-58E5C2369689}
AppName=Codex Control
AppVersion={#AppVersion}
AppPublisher=Codex Control
DefaultDirName={localappdata}\Programs\CodexControl
DefaultGroupName=Codex Control
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputRoot}
OutputBaseFilename={#OutputBaseName}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\CodexControlAgent.exe
VersionInfoVersion={#AppVersion}
#ifdef SIGN_INSTALLER
SignTool=codexsign
SignedUninstaller=yes
#endif

[Files]
Source: "{#SourceRoot}\*"; DestDir: "{app}"; Excludes: "data\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{app}\data"

[Icons]
Name: "{group}\Codex Control"; Filename: "{app}\CodexControlAgent.exe"
Name: "{group}\卸载 Codex Control"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\CodexControlAgent.exe"; Description: "启动 Codex Control"; Flags: nowait postinstall skipifsilent

[Code]
const
  DesktopRuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  AspNetRuntimeUrl = 'https://aka.ms/dotnet/8.0/aspnetcore-runtime-win-x64.exe';

var
  DeleteDataCheckBox: TNewCheckBox;

function HasRuntime(const FrameworkName: String): Boolean;
var
  SearchPath: String;
  FindRec: TFindRec;
begin
  Result := False;
  SearchPath := ExpandConstant('{commonpf64}\dotnet\shared\') + FrameworkName + '\8.*';
  if FindFirst(SearchPath, FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InstallRuntime(
  const FrameworkName: String;
  const DownloadUrl: String;
  const FileName: String;
  var ResultCode: Integer): Boolean;
var
  InstallerPath: String;
begin
  Result := HasRuntime(FrameworkName);
  if Result then
    Exit;

  DownloadTemporaryFile(DownloadUrl, FileName, '', nil);
  InstallerPath := ExpandConstant('{tmp}\') + FileName;
  Result := Exec(
    InstallerPath,
    '/install /quiet /norestart',
    '',
    SW_SHOW,
    ewWaitUntilTerminated,
    ResultCode);
  if Result then
    Result := (ResultCode = 0) or (ResultCode = 3010);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if not InstallRuntime(
      'Microsoft.WindowsDesktop.App',
      DesktopRuntimeUrl,
      'windowsdesktop-runtime-8-win-x64.exe',
      ResultCode) then
  begin
    Result := '无法安装 .NET 8 Desktop Runtime。退出码：' + IntToStr(ResultCode) +
      '。请从 Microsoft .NET 8 下载页安装后重试。';
    Exit;
  end;

  if not InstallRuntime(
      'Microsoft.AspNetCore.App',
      AspNetRuntimeUrl,
      'aspnetcore-runtime-8-win-x64.exe',
      ResultCode) then
  begin
    Result := '无法安装 .NET 8 ASP.NET Core Runtime。退出码：' + IntToStr(ResultCode) +
      '。请从 Microsoft .NET 8 下载页安装后重试。';
    Exit;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
end;

procedure InitializeUninstallProgressForm();
begin
  DeleteDataCheckBox := TNewCheckBox.Create(UninstallProgressForm);
  DeleteDataCheckBox.Parent := UninstallProgressForm;
  DeleteDataCheckBox.Left := UninstallProgressForm.StatusLabel.Left;
  DeleteDataCheckBox.Top := UninstallProgressForm.StatusLabel.Top +
    UninstallProgressForm.StatusLabel.Height + ScaleY(18);
  DeleteDataCheckBox.Width := UninstallProgressForm.ClientWidth - DeleteDataCheckBox.Left * 2;
  DeleteDataCheckBox.Caption := '删除 data（设置、设备身份、日志和 Runtime 缓存）';
  DeleteDataCheckBox.Checked := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and DeleteDataCheckBox.Checked then
    DelTree(ExpandConstant('{app}\data'), True, True, True);
end;
