#define ProductId "A3C6E7D8-2B10-4F64-91DD-5F21D9A06701"
#ifndef Payload
  #define Payload "..\staging\win-x64"
#endif
[Setup]
AppId={#ProductId}
AppName=Drawer
AppVersion=0.2
AppVerName={cm:AppTitle}
AppPublisher=XGaming
VersionInfoVersion=0.2.0.1
DefaultDirName={code:DefaultProgramDirectory}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
OutputDir=..\output
OutputBaseFilename=Drawer-0.2-win11-x64-test-r1-setup
SetupIconFile={#Payload}\Assets\Drawer.ico
UninstallDisplayIcon={app}\Drawer.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousLanguage=yes
ShowLanguageDialog=yes
SetupLogging=yes
UninstallLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
english.AppTitle=Drawer Test 0.2
chinesesimp.AppTitle=Drawer 测试版 0.2
english.LanguageFailed=The installation language could not be saved. See the installation log. Existing drawer data has not been replaced.
chinesesimp.LanguageFailed=未能保存安装语言，请查看安装日志。原有抽屉数据未被替换。
english.OptionsTitle=Drawer installation options
english.OptionsSub=Windows 11 x64 internal installation test
english.OptionsText=Existing user data is retained in LocalAppData\Drawer.
english.Desktop=Create a desktop shortcut
english.Startup=Start Drawer when I sign in to Windows
english.StartMenu=Create a Start menu shortcut
english.Launch=Launch Drawer
english.Running=Drawer is running, possibly in another Windows session. Exit it normally from the tray, then choose Retry. Files will not be replaced while it is running.
english.ProcessUnknown=Cannot verify running processes. Installation cannot safely continue.
english.MoveDir=An existing installation cannot be moved during upgrade. Uninstall it first (user data is retained), then install at the new location.
english.BackupFailed=User data could not be backed up. Installation has stopped without replacing the data.
english.UnknownStartup=The existing startup command cannot be safely identified. It will be retained without changes. Review Windows startup settings before using automatic startup.
english.StartupFailed=The startup choice could not be applied. User data has been retained. See the installation log.
english.ShortcutConflict=A shortcut with the same name belongs to another application. Remove it yourself or deselect this option.
english.Backup=User data backup:
english.Win10=This package targets Windows 11 x64. Windows 10 has only been checked statically and is unverified. Continue as a compatibility test?
english.UninstallOptions=Uninstall Drawer
english.UninstallDataText=User data is kept by default so that reinstalling restores your drawers. Optionally delete this Windows user's Drawer data and installer backups at the locations below. External original shortcuts, files and programs are not deleted.
english.DeleteUserData=Also delete my Drawer data and backups
english.ContinueUninstall=Continue uninstall
english.CancelUninstall=Cancel
english.DeleteDataConfirm=Permanently delete all drawers, contents, names, settings, managed copies and recovery backups for this Windows user? This cannot be undone. External original files and programs are not deleted.
english.DataDeleteFailed=Drawer was uninstalled, but some user data could not be deleted. Check permissions or files in use at these locations. No linked external folders were traversed:
chinesesimp.OptionsTitle=Drawer 安装选项
chinesesimp.OptionsSub=Windows 11 x64 小范围安装测试版
chinesesimp.OptionsText=已有数据继续保存在 LocalAppData\Drawer，卸载默认保留。
chinesesimp.Desktop=创建桌面快捷方式
chinesesimp.Startup=登录 Windows 后启动 Drawer
chinesesimp.StartMenu=创建开始菜单快捷方式
chinesesimp.Launch=安装完成后启动 Drawer
chinesesimp.Running=Drawer 正在运行，可能位于另一个 Windows 会话。请从托盘正常退出，再点击重试。运行期间不会覆盖文件。
chinesesimp.ProcessUnknown=无法核实正在运行的进程，安装暂不能安全继续。
chinesesimp.MoveDir=升级时不支持移动安装目录。请先卸载（保留用户数据），再安装到新位置。
chinesesimp.BackupFailed=用户数据无法完整备份，安装已停止，不会替换用户数据。
chinesesimp.UnknownStartup=无法安全识别已有 Drawer 启动命令，将保留原登记且不修改。请在使用自启前检查 Windows 启动设置。
chinesesimp.StartupFailed=未能应用开机启动选择。用户数据已保留，请查看安装日志。
chinesesimp.ShortcutConflict=同名快捷方式不属于本安装。请自行处理冲突，或取消创建该快捷方式。
chinesesimp.Backup=用户数据备份：
chinesesimp.Win10=本包首轮测试范围是 Windows 11 x64。Windows 10 仅做过静态检查，尚未验证。是否继续作为兼容性测试？
chinesesimp.UninstallOptions=卸载 Drawer
chinesesimp.UninstallDataText=默认保留数据，重装后可继续使用原抽屉。您也可以删除当前 Windows 用户在以下位置的 Drawer 数据及安装备份。不会删除外部原快捷方式、文件或程序。
chinesesimp.DeleteUserData=同时删除我的 Drawer 数据及备份
chinesesimp.ContinueUninstall=继续卸载
chinesesimp.CancelUninstall=取消
chinesesimp.DeleteDataConfirm=是否永久删除当前 Windows 用户的全部抽屉、内容、名称、设置、托管副本及恢复备份？此操作无法撤销。不会删除外部原文件或程序。
chinesesimp.DataDeleteFailed=Drawer 已卸载，但部分用户数据未能删除。请检查以下位置的权限或文件占用。没有进入链接指向的外部文件夹：

english.TargetPath=Selected installation folder: 
english.SystemError=Windows error: 
english.DirRoot=Choose an independent application subfolder, not a drive root, network location or Program Files itself.
english.DirUnsafe=The path is too long, has an invalid trailing character, or contains a directory link. Choose a direct local path.
english.DirData=The application folder cannot be the Drawer user data/backup folder or its parent.
english.DirWindows=Drawer cannot be installed inside the Windows system directory.
english.DirVolume=The selected drive is missing or unavailable.
english.DirFile=The selected path is an existing file, not a directory.
english.DirRead=The directory could not be inspected safely. Check its permissions.
english.DirNonEmpty=This directory contains files that cannot be verified as this installation. Choose an empty subfolder. No existing files were removed.
english.DirCreate=The installation subfolder could not be created.
english.DirWrite=The selected application folder is not writable.
english.DirCleanup=The write probe could not be removed. Check delete permission or file protection.
english.RequestPermission=Allow Drawer to request Windows administrator approval for this application folder? Only the original Windows user will receive Modify permission on this folder; installation and data remain per-user. Choose No to select another folder. Windows will show a separate UAC prompt.
english.PermissionCancelled=Administrator approval was cancelled. You can select another folder and continue.
english.PermissionFailed=Folder permission could not be prepared. No all-user installation was performed. Select a writable folder or check the Windows error below.
chinesesimp.TargetPath=实际安装路径：
chinesesimp.SystemError=Windows 错误：
chinesesimp.DirRoot=请选择独立的程序子文件夹，不能选择磁盘根目录、网络位置或 Program Files 本身。
chinesesimp.DirUnsafe=路径过长、末尾字符不合法或包含目录链接。请选择直接的本地目录。
chinesesimp.DirData=程序不能安装到 Drawer 用户数据、备份目录或其上层目录。
chinesesimp.DirWindows=程序不能安装到 Windows 系统目录中。
chinesesimp.DirVolume=所选盘符不存在或当前不可用。
chinesesimp.DirFile=所选位置已经是一个文件，请选择独立的程序文件夹。
chinesesimp.DirRead=无法安全读取目录内容，请检查该目录的访问权限。
chinesesimp.DirNonEmpty=该目录包含无法确认为本次安装所有的文件，请选择空白子文件夹。没有删除原文件。
chinesesimp.DirCreate=无法创建所选的程序子文件夹。
chinesesimp.DirWrite=所选程序文件夹没有写入权限。
chinesesimp.DirCleanup=无法删除写入探测文件，请检查删除权限或文件保护。
chinesesimp.RequestPermission=是否请求 Windows 管理员授权来使用此程序目录？只为原 Windows 用户授予这个目录的修改权限，安装范围和用户数据仍属于当前用户。选“否”可更换目录；选“是”后 Windows 将另行显示 UAC 授权框。
chinesesimp.PermissionCancelled=已取消管理员授权，可以更换目录后继续安装。
chinesesimp.PermissionFailed=未能准备目录权限，没有切换为所有用户安装。请更换可写目录，或查看下方 Windows 错误。
[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\helper\build\DrawerFolderPermission.exe"; Flags: dontcopy

[Run]
Filename: "{app}\Drawer.exe"; WorkingDir: "{app}"; Description: "{cm:Launch}"; Flags: postinstall skipifsilent nowait; BeforeInstall: ReleaseInstallationLock

[Code]
const
  Marker = '{#ProductId}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunName = 'Drawer (AA_drawer)';
var
  Options: TInputOptionWizardPage;
  OldDir, BackupDir: String;
  UnknownStartup, GuardHeld, DeleteUserData: Boolean;
  InstallLock, LegacyLock, DataLock: THandle;
  PayloadFiles, PreparedDirectories: TStringList;
  LastDirectoryReason, LastDirectoryTarget, CachedUserSid: String;
  LastDirectoryCode: Integer;
  InstallationFinished: Boolean;

function CreateMutexW(Attributes: Integer; Owner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributesW(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function GetDriveTypeW(RootPath: String): LongWord;
  external 'GetDriveTypeW@kernel32.dll stdcall';
function MoveFileExW(ExistingName, NewName: String; Flags: LongWord): Boolean;
  external 'MoveFileExW@kernel32.dll stdcall';

function CreateDirectoryW(Name: String; Security: Integer): Boolean;
  external 'CreateDirectoryW@kernel32.dll stdcall';
function CreateFileW(Name: String; Access, Share: LongWord; Security: Integer; Creation, Flags: LongWord; TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function GetCurrentProcessId(): LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

function DefaultProgramDirectory(Param: String): String;
begin
  if DirExists('D:\') then Result := 'D:\Program Files\Drawer'
  else Result := ExpandConstant('{commonpf}\Drawer');
end;

function InstallStamp(): String;
begin
  Result := GetDateTimeString('yyyymmdd-hhnnss-zzz', '-', ':');
end;

function InitializeSetup(): Boolean;
var Version: TWindowsVersion;
begin
  Log('Drawer installer revision=0.2-test-r1 file-version=0.2.0.1');
  Result := True;
  GetWindowsVersionEx(Version);
  if Version.Build < 22000 then begin
    if WizardSilent() then Result := False
    else Result := MsgBox(CustomMessage('Win10'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;
end;

function SamePath(A, B: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(ExpandFileName(A)),
    RemoveBackslashUnlessRoot(ExpandFileName(B))) = 0;
end;

function UnderDir(Path, Parent: String): Boolean;
begin
  Result := SamePath(Path, Parent) or
    (CompareText(Copy(AddBackslash(ExpandFileName(Path)), 1,
      Length(AddBackslash(ExpandFileName(Parent)))), AddBackslash(ExpandFileName(Parent))) = 0);
end;

function OwnedLink(Path, TargetDir: String): Boolean;
var Shell, Link: Variant;
begin
  Result := False;
  if not FileExists(Path) then exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    Result := SamePath(Link.TargetPath, AddBackslash(TargetDir) + 'Drawer.exe') and (Link.Description = Marker);
  except
    Log('Shortcut ownership could not be verified.');
  end;
end;

function StartupState(): Integer;
var Command, ExePath, Tail, Product: String; Closing: Integer; Shell, Folder, Item: Variant;
begin
  Result := 0;
  if not RegQueryStringValue(HKCU, RunKey, RunName, Command) then exit;
  Result := 2;
  Command := Trim(Command);
  if (Length(Command) < 3) or (Command[1] <> '"') then exit;
  Closing := Pos('"', Copy(Command, 2, Length(Command)));
  if Closing = 0 then exit;
  ExePath := Copy(Command, 2, Closing - 1);
  Tail := Trim(Copy(Command, Closing + 2, Length(Command)));
  if Tail <> '' then exit;
  if CompareText(ExtractFileName(ExePath), 'Drawer.exe') <> 0 then exit;
  if FileExists(ExePath) then begin
    try
      Shell := CreateOleObject('Shell.Application');
      Folder := Shell.NameSpace(ExtractFileDir(ExePath));
      Item := Folder.ParseName(ExtractFileName(ExePath));
      Product := Item.ExtendedProperty('System.Software.ProductName');
      if Product <> 'Drawer' then exit;
    except exit; end;
  end;
  Result := 1;
end;

function AnyDrawerRunning(): Boolean;
var Service, Processes: Variant;
begin
  Service := CreateOleObject('WbemScripting.SWbemLocator');
  Service := Service.ConnectServer('', 'root\CIMV2');
  Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''Drawer.exe''');
  Result := Processes.Count > 0;
end;

function CurrentUserDataMutex(): String;
var Network, Service, Account: Variant; Sid: String;
begin
  Network := CreateOleObject('WScript.Network');
  Service := CreateOleObject('WbemScripting.SWbemLocator');
  Service := Service.ConnectServer('', 'root\CIMV2');
  Account := Service.Get('Win32_UserAccount.Domain="' + Network.UserDomain +
    '",Name="' + Network.UserName + '"');
  Sid := Account.SID;
  if Copy(Sid, 1, 4) <> 'S-1-' then RaiseException(CustomMessage('ProcessUnknown'));
  Result := 'Global\Drawer.Data.' + Sid;
end;

procedure ReleaseInstallationLock;
begin
  if DataLock <> 0 then CloseHandle(DataLock);
  if LegacyLock <> 0 then CloseHandle(LegacyLock);
  if InstallLock <> 0 then CloseHandle(InstallLock);
  DataLock := 0; LegacyLock := 0; InstallLock := 0; GuardHeld := False;
end;

function RequireStopped(): Boolean;
var Silent: Boolean;
begin
  Result := False;
  if GuardHeld then begin Result := True; exit; end;
  if IsUninstaller then Silent := UninstallSilent() else Silent := WizardSilent();
  try
    while AnyDrawerRunning() or CheckForMutexes('Local\Drawer.TopPrototype') do begin
      if Silent or (MsgBox(CustomMessage('Running'), mbInformation, MB_RETRYCANCEL) <> IDRETRY) then exit;
    end;
  except
    MsgBox(CustomMessage('ProcessUnknown'), mbError, MB_OK); exit;
  end;
  InstallLock := CreateMutexW(0, False, 'Global\Drawer.Installation');
  if (InstallLock = 0) or (DLLGetLastError() = 183) then begin ReleaseInstallationLock; exit; end;
  LegacyLock := CreateMutexW(0, False, 'Local\Drawer.TopPrototype');
  if (LegacyLock = 0) or (DLLGetLastError() = 183) then begin ReleaseInstallationLock; exit; end;
  try
    DataLock := CreateMutexW(0, False, CurrentUserDataMutex());
    if (DataLock = 0) or (DLLGetLastError() = 183) then begin ReleaseInstallationLock; exit; end;
  except
    ReleaseInstallationLock;
    MsgBox(CustomMessage('ProcessUnknown'), mbError, MB_OK);
    exit;
  end;
  GuardHeld := True; Result := True;
end;

function SafeDirectory(Path: String): Boolean;
var Part, Parent: String; Attributes: LongWord;
begin
  Result := False;
  while Length(Path) > 3 do begin
    Part := ExtractFileName(Path);
    if (Part = '') or (Part[Length(Part)] = '.') or (Part[Length(Part)] = ' ') then exit;
    Attributes := GetFileAttributesW(Path);
    if (Attributes <> $FFFFFFFF) and ((Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then exit;
    Path := Parent;
  end;
  Result := True;
end;

function EmptyDirectory(Directory: String): Boolean;
var Rec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Directory) + '*', Rec) then begin
    try
      repeat
        if (Rec.Name <> '.') and (Rec.Name <> '..') then Result := False;
      until not FindNext(Rec);
    finally FindClose(Rec); end;
  end else if (DLLGetLastError() <> 2) and (DLLGetLastError() <> 18) then RaiseException('Directory enumeration failed');
end;

#include "PayloadManifest.iss"
#include "DirectoryChecks.iss"

function NextButtonClick(CurPageID: Integer): Boolean;
var Target: String;
begin
  Result := True;
  if (CurPageID = wpSelectDir) or (CurPageID = wpReady) then begin
    Target := RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue()));
    Result := CheckDirectoryWithPermission(Target, True);
    if not Result then begin MsgBox(DirectoryErrorText(),mbError,MB_OK); exit; end;
  end;
  if CurPageID = Options.ID then begin
    if Options.Values[0] and FileExists(ExpandConstant('{userdesktop}\drawer.lnk')) and
      not OwnedLink(ExpandConstant('{userdesktop}\drawer.lnk'),WizardDirValue()) then Result := False;
    if Options.Values[2] and FileExists(ExpandConstant('{userprograms}\Drawer.lnk')) and
      not OwnedLink(ExpandConstant('{userprograms}\Drawer.lnk'),WizardDirValue()) then Result := False;
    if not Result then MsgBox(CustomMessage('ShortcutConflict'),mbError,MB_OK);
  end;
end;
procedure CopyBackup(Source, Target: String);
var Rec: TFindRec; Input, Output: String;
begin
  if (GetFileAttributesW(Source) and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
    RaiseException(CustomMessage('BackupFailed'));
  if not ForceDirectories(Target) then RaiseException(CustomMessage('BackupFailed'));
  if FindFirst(AddBackslash(Source) + '*', Rec) then begin
    try
      repeat
        if (Rec.Name <> '.') and (Rec.Name <> '..') then begin
          if (Rec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then RaiseException(CustomMessage('BackupFailed'));
          Input := AddBackslash(Source) + Rec.Name;
          Output := AddBackslash(Target) + Rec.Name;
          if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then CopyBackup(Input, Output)
          else begin
            if not CopyFile(Input, Output, True) then RaiseException(CustomMessage('BackupFailed'));
            if GetSHA256OfFile(Input) <> GetSHA256OfFile(Output) then RaiseException(CustomMessage('BackupFailed'));
          end;
        end;
      until not FindNext(Rec);
    finally FindClose(Rec); end;
  end else if (DLLGetLastError() <> 2) and (DLLGetLastError() <> 18) then
    RaiseException(CustomMessage('BackupFailed'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // Silent launches also pass these checks, without relying on wizard navigation.
  if not CheckDirectoryWithPermission(RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue())),False) then begin Result := DirectoryErrorText(); exit; end;
  if not NextButtonClick(Options.ID) then begin Result := CustomMessage('ShortcutConflict'); exit; end;
  if not RequireStopped() then begin Result := CustomMessage('Running'); exit; end;
  if (BackupDir = '') and DirExists(ExpandConstant('{localappdata}\Drawer')) then begin
    BackupDir := ExpandConstant('{localappdata}\DrawerBackups\before-Drawer-0.2-test-r1-') + InstallStamp();
    while DirExists(BackupDir) do BackupDir := BackupDir + '-next';
    try
      CopyBackup(ExpandConstant('{localappdata}\Drawer'), BackupDir + '\data');
      if not SaveStringToFile(BackupDir + '\backup-complete.txt', 'Drawer 0.2 test-r1' + #13#10, False) then
        RaiseException(CustomMessage('BackupFailed'));
      Log(CustomMessage('Backup') + ' ' + BackupDir);
    except
      Result := CustomMessage('BackupFailed'); BackupDir := ''; ReleaseInstallationLock;
    end;
  end;
end;

procedure CreateOwnedLink(Path: String);
var Shell, Link: Variant;
begin
  if FileExists(Path) and not OwnedLink(Path, ExpandConstant('{app}')) then RaiseException(CustomMessage('ShortcutConflict'));
  Shell := CreateOleObject('WScript.Shell');
  Link := Shell.CreateShortcut(Path);
  Link.TargetPath := ExpandConstant('{app}\Drawer.exe');
  Link.Arguments := ''; Link.WorkingDirectory := ExpandConstant('{app}');
  Link.IconLocation := ExpandConstant('{app}\Drawer.exe,0'); Link.Description := Marker;
  Link.Save();
end;

procedure RemoveOwnedLink(Path: String);
begin
  if OwnedLink(Path, ExpandConstant('{app}')) then DeleteFile(Path);
end;

procedure SaveInstallationLanguage(LocalRoot, LanguageName, Revision: String);
var Target, Pending: String;
begin
  if (LanguageName <> 'english') and (LanguageName <> 'chinesesimp') then
    RaiseException(CustomMessage('LanguageFailed'));
  Target := AddBackslash(LocalRoot) + 'Drawer';
  if not SafeDirectory(Target) then RaiseException(CustomMessage('LanguageFailed'));
  if not ForceDirectories(Target) then RaiseException(CustomMessage('LanguageFailed'));
  Target := AddBackslash(Target) + 'installation-language.txt';
  Pending := Target + '.pending-' + Revision;
  if not SafeDirectory(Target) or not SafeDirectory(Pending) or FileExists(Pending) then
    RaiseException(CustomMessage('LanguageFailed'));
  try
    if not SaveStringToFile(Pending, LanguageName + #13#10 + Revision + #13#10, False) then
      RaiseException(CustomMessage('LanguageFailed'));
    if not MoveFileExW(Pending, Target, 1 or 8) then RaiseException(CustomMessage('LanguageFailed'));
  finally
    if FileExists(Pending) then DeleteFile(Pending);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer; Argument: String;
begin
  if CurStep = ssPostInstall then begin
    if not UnknownStartup then begin
      if Options.Values[1] then Argument := '--installer-startup-on' else Argument := '--installer-startup-off';
      if not Exec(ExpandConstant('{app}\Drawer.exe'), Argument, ExpandConstant('{app}'),
        SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then RaiseException(CustomMessage('StartupFailed'));
    end;
    if Options.Values[0] then CreateOwnedLink(ExpandConstant('{userdesktop}\drawer.lnk'))
    else RemoveOwnedLink(ExpandConstant('{userdesktop}\drawer.lnk'));
    if Options.Values[2] then CreateOwnedLink(ExpandConstant('{userprograms}\Drawer.lnk'))
    else RemoveOwnedLink(ExpandConstant('{userprograms}\Drawer.lnk'));
    // Persist only after copying files and applying installation choices.
    // The app consumes this revision once through its normal atomic settings commit.
    SaveInstallationLanguage(ExpandConstant('{localappdata}'), ExpandConstant('{language}'),
      '0.2-test-r1-' + InstallStamp());
    InstallationFinished := True;
  end;
end;

procedure InitializeWizard();
var State: Integer;
begin
  InitializePayloadFiles();
  PreparedDirectories := TStringList.Create;
  OldDir := GetPreviousData('InstallDir', '');
  Options := CreateInputOptionPage(wpSelectDir, CustomMessage('OptionsTitle'), CustomMessage('OptionsSub'),
    CustomMessage('OptionsText'), False, False);
  Options.Add(CustomMessage('Desktop')); Options.Add(CustomMessage('Startup')); Options.Add(CustomMessage('StartMenu'));
  State := StartupState(); UnknownStartup := State = 2;
  if OldDir = '' then begin Options.Values[0] := True; Options.Values[1] := True; end
  else begin
    Options.Values[0] := OwnedLink(ExpandConstant('{userdesktop}\drawer.lnk'), OldDir);
    Options.Values[1] := State = 1;
    Options.Values[2] := OwnedLink(ExpandConstant('{userprograms}\Drawer.lnk'), OldDir);
  end;
  if UnknownStartup then begin
    Options.Values[1] := False; Options.CheckListBox.ItemEnabled[1] := False;
    MsgBox(CustomMessage('UnknownStartup'), mbInformation, MB_OK);
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'InstallDir', ExpandConstant('{app}'));
end;

procedure DeinitializeSetup();
var I: Integer;
begin
  ReleaseInstallationLock;
  if PreparedDirectories <> nil then begin
    if not InstallationFinished then
      for I := 0 to PreparedDirectories.Count - 1 do begin
        RemovePermissionMarker(PreparedDirectories[I]);
        RemoveDir(PreparedDirectories[I]);
      end;
    PreparedDirectories.Free;
  end;
  if PayloadFiles <> nil then PayloadFiles.Free;
end;

function PurgeUserDataRoot(Path, LocalRoot: String): Boolean;
var Attributes, ErrorCode: LongWord;
begin
  Result := False;
  // Only the two exact app-owned roots are permitted. Never traverse a
  // redirected root/ancestor; DelTree also does not recurse into nested junctions.
  LocalRoot := RemoveBackslashUnlessRoot(ExpandFileName(LocalRoot));
  Path := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  if (Length(LocalRoot) <= 3) or (Copy(LocalRoot, 1, 2) = '\\') then exit;
  if not (SamePath(Path, AddBackslash(LocalRoot) + 'Drawer') or
    SamePath(Path, AddBackslash(LocalRoot) + 'DrawerBackups')) then exit;
  if not SafeDirectory(Path) then exit;
  if DirExists(Path) then Result := DelTree(Path, True, True, True)
  else begin
    Attributes := GetFileAttributesW(Path); ErrorCode := DLLGetLastError();
    Result := (Attributes = $FFFFFFFF) and ((ErrorCode = 2) or (ErrorCode = 3));
  end;
end;

function SelectUninstallDataOption(): Boolean;
var Form: TSetupForm; Description: TNewStaticText; Paths: TNewMemo;
  Choice: TNewCheckBox; ContinueButton, CancelButton: TNewButton;
begin
  Result := False;
  DeleteUserData := False;
  // Silent uninstall always preserves user data; no purge command-line option.
  if UninstallSilent() then begin Result := True; exit; end;
  Form := CreateCustomForm(520, 320, True, True);
  try
    Form.Caption := CustomMessage('UninstallOptions');
    Form.Position := poScreenCenter;
    Description := TNewStaticText.Create(Form);
    Description.Parent := Form; Description.AutoSize := False;
    Description.SetBounds(ScaleX(20), ScaleY(16), Form.ClientWidth - ScaleX(40), ScaleY(90));
    Description.WordWrap := True; Description.Caption := CustomMessage('UninstallDataText');
    Paths := TNewMemo.Create(Form); Paths.Parent := Form;
    Paths.SetBounds(ScaleX(20), ScaleY(112), Form.ClientWidth - ScaleX(40), ScaleY(90));
    Paths.ReadOnly := True; Paths.ScrollBars := ssBoth; Paths.WordWrap := False;
    Paths.Text := ExpandConstant('{localappdata}\Drawer') + #13#10 +
      ExpandConstant('{localappdata}\DrawerBackups');
    Choice := TNewCheckBox.Create(Form); Choice.Parent := Form;
    Choice.SetBounds(ScaleX(20), ScaleY(216), Form.ClientWidth - ScaleX(40), ScaleY(24));
    Choice.Caption := CustomMessage('DeleteUserData'); Choice.Checked := False;
    ContinueButton := TNewButton.Create(Form); ContinueButton.Parent := Form;
    ContinueButton.SetBounds(Form.ClientWidth - ScaleX(290), ScaleY(268), ScaleX(150), ScaleY(30));
    ContinueButton.Caption := CustomMessage('ContinueUninstall');
    ContinueButton.Default := True; ContinueButton.ModalResult := mrOk;
    CancelButton := TNewButton.Create(Form); CancelButton.Parent := Form;
    CancelButton.SetBounds(Form.ClientWidth - ScaleX(128), ScaleY(268), ScaleX(108), ScaleY(30));
    CancelButton.Caption := CustomMessage('CancelUninstall');
    CancelButton.Cancel := True; CancelButton.ModalResult := mrCancel;
    if Form.ShowModal() <> mrOk then exit;
    if Choice.Checked then begin
      if MsgBox(CustomMessage('DeleteDataConfirm'), mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2) <> IDYES then exit;
      DeleteUserData := True;
    end;
    Result := True;
  finally Form.Free(); end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := RequireStopped();
  if Result then Result := SelectUninstallDataOption();
  if not Result then ReleaseInstallationLock;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Code: Integer; DataRemoved, BackupsRemoved: Boolean;
begin
  if CurUninstallStep = usUninstall then begin
    RemovePermissionMarker(ExpandConstant('{app}'));
    if not Exec(ExpandConstant('{app}\Drawer.exe'), '--installer-uninstall-startup', ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then RaiseException(CustomMessage('StartupFailed'));
    RemoveOwnedLink(ExpandConstant('{userdesktop}\drawer.lnk'));
    RemoveOwnedLink(ExpandConstant('{userprograms}\Drawer.lnk'));
  end;
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then begin
    DataRemoved := False; BackupsRemoved := False;
    if GuardHeld then begin
      try
        DataRemoved := PurgeUserDataRoot(ExpandConstant('{localappdata}\Drawer'), ExpandConstant('{localappdata}'));
        BackupsRemoved := PurgeUserDataRoot(ExpandConstant('{localappdata}\DrawerBackups'), ExpandConstant('{localappdata}'));
      except Log('User data cleanup did not complete.'); end;
    end;
    Log('Optional user data cleanup: data=' + IntToStr(Ord(DataRemoved)) +
      ', backups=' + IntToStr(Ord(BackupsRemoved)));
    if not DataRemoved or not BackupsRemoved then
      MsgBox(CustomMessage('DataDeleteFailed') + #13#10 +
        ExpandConstant('{localappdata}\Drawer') + #13#10 +
        ExpandConstant('{localappdata}\DrawerBackups'), mbError, MB_OK);
  end;
end;

procedure DeinitializeUninstall();
begin ReleaseInstallationLock; end;
