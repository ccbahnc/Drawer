function CurrentUserSid(): String;
begin
  if CachedUserSid = '' then CachedUserSid := Copy(CurrentUserDataMutex(), 20, 200);
  Result := CachedUserSid;
end;

function PermissionMarkerOwned(Target: String): Boolean;
var Text: AnsiString;
begin
  Result := LoadStringFromFile(AddBackslash(Target) + 'drawer-permission-id.txt', Text) and
    (Text = Marker + '|' + CurrentUserSid());
end;

function PayloadFolderAllowed(Relative: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 0 to PayloadFiles.Count - 1 do
    if CompareText(Copy(PayloadFiles[I], 1, Length(Relative) + 1), Relative + '\') = 0 then begin Result := True; exit; end;
end;

function RetryContentsOwned(Target, Relative: String): Boolean;
var Rec: TFindRec; Path, Rel, Key: String;
begin
  Result := True;
  Path := AddBackslash(Target) + Relative;
  if FindFirst(AddBackslash(Path) + '*', Rec) then begin
    try
      repeat
        if (Rec.Name <> '.') and (Rec.Name <> '..') then begin
          Rel := Relative; if Rel <> '' then Rel := AddBackslash(Rel); Rel := Rel + Rec.Name;
          if (Rec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then begin Result := False; exit; end;
          if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
            if not PayloadFolderAllowed(Rel) or not RetryContentsOwned(Target, Rel) then begin Result := False; exit; end;
          end else if not ((Relative = '') and (CompareText(Rec.Name, 'drawer-permission-id.txt') = 0)) then begin
            Key := Lowercase(Rel) + '|' + Lowercase(GetSHA256OfFile(AddBackslash(Target) + Rel));
            if PayloadFiles.IndexOf(Key) < 0 then begin Result := False; exit; end;
          end;
        end;
      until not FindNext(Rec);
    finally FindClose(Rec); end;
  end else if (DLLGetLastError() <> 2) and (DLLGetLastError() <> 18) then Result := False;
end;

function DirectoryFailure(Key, Target: String; Code: Integer): Boolean;
begin
  LastDirectoryReason := Key; LastDirectoryTarget := Target; LastDirectoryCode := Code;
  Log('directory-check: reason=' + Key + ' target=' + Target + ' win32=' + IntToStr(Code));
  Result := False;
end;

function DirectoryErrorText(): String;
begin
  Result := CustomMessage(LastDirectoryReason) + #13#10 + CustomMessage('TargetPath') + LastDirectoryTarget;
  if LastDirectoryCode <> 0 then Result := Result + #13#10 +
    CustomMessage('SystemError') + IntToStr(LastDirectoryCode) + ' - ' + SysErrorMessage(LastDirectoryCode);
end;

function ValidateDirectory(Target: String): Boolean;
var Attributes, Code: LongWord; SavedMarker: AnsiString; HasInstallMarker: Boolean;
begin
  LastDirectoryReason := ''; LastDirectoryCode := 0; LastDirectoryTarget := Target;
  if (Length(Target) <= 3) or (Copy(Target,1,2) = '\\') or SamePath(Target, ExpandConstant('{commonpf}')) or
    SamePath(Target, ExpandConstant('{commonpf32}')) or SamePath(Target,Copy(Target,1,3)+'Program Files') or
    SamePath(Target,Copy(Target,1,3)+'Program Files (x86)') then begin Result := DirectoryFailure('DirRoot',Target,0); exit; end;
  if (Length(Target) > 230) or not SafeDirectory(Target) then begin Result := DirectoryFailure('DirUnsafe',Target,0); exit; end;
  if UnderDir(Target,ExpandConstant('{localappdata}\Drawer')) or
    UnderDir(Target,ExpandConstant('{localappdata}\DrawerBackups')) or
    UnderDir(ExpandConstant('{localappdata}\Drawer'),Target) then begin Result := DirectoryFailure('DirData',Target,0); exit; end;
  if UnderDir(Target,ExpandConstant('{win}')) then begin Result := DirectoryFailure('DirWindows',Target,0); exit; end;
  if not DirExists(Copy(Target,1,3)) then begin Result := DirectoryFailure('DirVolume',Target,3); exit; end;
  if GetDriveTypeW(Copy(Target,1,3)) = 4 then begin Result := DirectoryFailure('DirRoot',Target,0); exit; end;
  if (OldDir <> '') and not SamePath(Target,OldDir) then begin Result := DirectoryFailure('MoveDir',Target,0); exit; end;
  Attributes := GetFileAttributesW(Target); Code := DLLGetLastError();
  if (Attributes <> $FFFFFFFF) and ((Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) then begin Result := DirectoryFailure('DirFile',Target,0); exit; end;
  if (Attributes = $FFFFFFFF) and (Code <> 2) and (Code <> 3) then begin Result := DirectoryFailure('DirRead',Target,Code); exit; end;
  try
    if DirExists(Target) and not EmptyDirectory(Target) then begin
      HasInstallMarker := LoadStringFromFile(AddBackslash(Target)+'drawer-install-id.txt',SavedMarker) and (Trim(SavedMarker)=Marker);
      if (OldDir <> '') and SamePath(Target,OldDir) and HasInstallMarker then begin Result := True; exit; end;
      if not (PermissionMarkerOwned(Target) or HasInstallMarker) then begin Result := DirectoryFailure('DirNonEmpty',Target,0); exit; end;
      if not RetryContentsOwned(Target,'') then begin Result := DirectoryFailure('DirNonEmpty',Target,0); exit; end;
      Log('directory-check: verified incomplete installation contents for retry');
    end;
  except
    Code := DLLGetLastError(); if Code = 0 then Code := 13;
    Log('directory-check inspection failed: ' + GetExceptionMessage());
    Result := DirectoryFailure('DirRead',Target,Code); exit;
  end;
  Result := True;
end;

function ProbeTargetDirectory(Target: String): Boolean;
var Missing, Created: TStringList; Parent, Probe: String; I: Integer; FileHandle: THandle; Code: LongWord;
begin
  Result := False; Missing := TStringList.Create; Created := TStringList.Create;
  try
    Parent := Target;
    while not DirExists(Parent) do begin
      Missing.Add(Parent); Parent := ExtractFileDir(Parent);
      if Length(Parent) < 3 then begin Result := DirectoryFailure('DirVolume',Target,3); exit; end;
    end;
    for I := Missing.Count - 1 downto 0 do begin
      if not CreateDirectoryW(Missing[I],0) then begin
        Code := DLLGetLastError(); Result := DirectoryFailure('DirCreate',Target,Code); exit;
      end;
      Created.Add(Missing[I]);
    end;
    Probe := AddBackslash(Target) + 'drawer-write-probe-' + IntToStr(GetCurrentProcessId()) + '-' + InstallStamp() + '.tmp';
    FileHandle := CreateFileW(Probe,$40000000,7,0,1,$80,0);
    if FileHandle = THandle(-1) then begin Code := DLLGetLastError(); Result := DirectoryFailure('DirWrite',Target,Code); exit; end;
    CloseHandle(FileHandle);
    if not DeleteFile(Probe) then begin Code := DLLGetLastError(); Result := DirectoryFailure('DirCleanup',Target,Code); exit; end;
    Result := True;
  finally
    // Remove only empty directories created by this probe, never preexisting folders.
    for I := Created.Count - 1 downto 0 do
      if DirExists(Created[I]) then RemoveDir(Created[I]);
    Created.Free; Missing.Free;
  end;
end;

function MayRequestElevation(Code: Integer): Boolean;
begin Result := (Code = 5) or (Code = 1314); end;

function CheckDirectoryWithPermission(Target: String; Interactive: Boolean): Boolean;
var Code: Integer; Helper, Params: String; NewlyCreated: Boolean;
begin
  Result := ValidateDirectory(Target);
  if Result then Result := ProbeTargetDirectory(Target);
  if Result then exit;
  if (LastDirectoryReason <> 'DirRead') and (LastDirectoryReason <> 'DirCreate') and
    (LastDirectoryReason <> 'DirWrite') and (LastDirectoryReason <> 'DirCleanup') then exit;
  if not Interactive or WizardSilent() or not MayRequestElevation(LastDirectoryCode) then exit;
  if MsgBox(DirectoryErrorText() + #13#10#13#10 + CustomMessage('RequestPermission'),mbConfirmation,MB_YESNO or MB_DEFBUTTON2) <> IDYES then exit;
  NewlyCreated := not DirExists(Target);
  try
    ExtractTemporaryFile('DrawerFolderPermission.exe'); Helper := ExpandConstant('{tmp}\DrawerFolderPermission.exe');
  except
    Code := DLLGetLastError(); if Code = 0 then Code := 13;
    Log('directory permission helper unavailable: ' + GetExceptionMessage());
    Result := DirectoryFailure('PermissionFailed',Target,Code); exit;
  end;
  Params := '--grant ' + AddQuotes(Target) + ' ' + IntToStr(GetCurrentProcessId()) + ' ' + AddQuotes(ExpandConstant('{localappdata}'));
  Log('directory-check: user requested UAC for target=' + Target);
  if not ShellExec('runas',Helper,Params,ExpandConstant('{tmp}'),SW_HIDE,ewWaitUntilTerminated,Code) or (Code <> 0) then begin
    if Code = 1223 then Result := DirectoryFailure('PermissionCancelled',Target,Code)
    else Result := DirectoryFailure('PermissionFailed',Target,Code);
    exit;
  end;
  // This marker is deliberately written by the unelevated original installer.
  if NewlyCreated and SafeDirectory(Target) then begin
    if not SaveStringToFile(AddBackslash(Target)+'drawer-permission-id.txt',Marker+'|'+CurrentUserSid(),False) then begin Result := DirectoryFailure('DirWrite',Target,5); exit; end;
    PreparedDirectories.Add(Target);
  end;
  Result := ValidateDirectory(Target);
  if Result then Result := ProbeTargetDirectory(Target);
end;

procedure RemovePermissionMarker(Target: String);
begin
  if SafeDirectory(Target) and PermissionMarkerOwned(Target) then DeleteFile(AddBackslash(Target)+'drawer-permission-id.txt');
end;
