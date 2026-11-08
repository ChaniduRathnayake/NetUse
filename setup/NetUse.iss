; NetUse installer (Inno Setup 6.1+ / 7)
; Build:  ISCC setup\NetUse.iss      (after `dotnet publish -c Release -o publish`, or just run build.bat)
; Output: setup\Windows\NetUse-<version>-setup-win-x64.exe

#define AppName "NetUse"
#define AppExe  "NetUse.exe"
#define PubDir  AddBackslash(SourcePath) + "..\publish"
#define SrcExe  PubDir + "\NetUse.exe"

#if !FileExists(SrcExe)
  #error "publish\NetUse.exe not found. Run `dotnet publish -c Release -o publish` (or build.bat) first."
#endif

; version comes from the exe itself (<Version> in NetUse.csproj), so it never drifts
#define VerMajor
#define VerMinor
#define VerRev
#define VerBuild
#expr GetVersionComponents(SrcExe, VerMajor, VerMinor, VerRev, VerBuild)
#define AppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)

[Setup]
; AppId identifies NetUse for upgrades and uninstall: never change it
AppId={{3A64336F-0826-4973-96D1-E9E5D9120357}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=NetUse
AppPublisherURL=https://github.com/ChaniduRathnayake/NetUse
AppSupportURL=https://github.com/ChaniduRathnayake/NetUse/issues
AppUpdatesURL=https://github.com/ChaniduRathnayake/NetUse/releases
AppCopyright=Copyright (C) 2026 NetUse
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup

; %ProgramFiles%\NetUse: the same admin-only folder the "Start with Windows" task requires
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; No AppMutex: any program, even one without admin rights, can create a mutex with that name and so block every install,
; update and uninstall. Setup finds a running NetUse through the files it uses (Restart Manager) and the uninstaller
; looks for the real NetUse.exe process (see InitializeUninstall).
CloseApplications=yes

SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
OutputDir=Windows
OutputBaseFilename={#AppName}-{#AppVersion}-setup-win-x64
; the exe is already a compressed single file, so heavy LZMA settings gain little
Compression=lzma2/normal
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SrcExe}"; DestDir: "{app}"; Flags: ignoreversion
; native libraries load from the program folder (admin-only); they are never self-extracted to %TEMP%
Source: "{#PubDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; remove the "Start with Windows" logon task if it was turned on
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN NetUse /F"; Flags: runhidden; RunOnceId: "DelStartupTask"

[Code]
const
  FILE_ATTRIBUTE_REPARSE_POINT = $400;
  INVALID_FILE_ATTRIBUTES = $FFFFFFFF;

function GetFileAttributes(lpFileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

// True while a NetUse.exe started from Dir is running (checked on the real process, not on a mutex name).
function NetUseRunningFrom(const Dir: String): Boolean;
var
  Locator, Service, Procs, P: Variant;
  I, N: Integer;
  ExePath: String;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Service.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''NetUse.exe''');
    N := Procs.Count;
    for I := 0 to N - 1 do
    begin
      P := Procs.ItemIndex(I);
      try
        ExePath := P.ExecutablePath;
      except
        ExePath := '';
      end;
      if (ExePath <> '') and (CompareText(RemoveBackslashUnlessRoot(ExtractFileDir(ExePath)), RemoveBackslashUnlessRoot(Dir)) = 0) then
      begin
        Result := True;
        Exit;
      end;
    end;
  except
    Result := False;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  while NetUseRunningFrom(ExpandConstant('{app}')) do
    if SuppressibleMsgBox('NetUse is running. Right-click the NetUse icon in the taskbar notification area, choose Exit, then click OK.',
        mbInformation, MB_OKCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;

// %ProgramData%\NetUse without trusting the environment: a program running without admin rights can redefine
// SystemDrive for your account, which would otherwise point the (elevated) uninstaller at a folder it controls.
function DataDir(): String;
var
  Drive, S: String;
begin
  Drive := ExtractFileDrive(ExpandConstant('{sys}')); // from GetSystemDirectory, not from an environment variable
  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList', 'ProgramData', S)
     and (CompareText(Copy(S, 1, 13), '%SystemDrive%') = 0) then
    S := Drive + Copy(S, 14, Length(S))
  else
    S := Drive + '\ProgramData';
  if Pos('%', S) > 0 then
    S := Drive + '\ProgramData';
  Result := AddBackslash(S) + 'NetUse';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
  Attr: Cardinal;
begin
  // Usage history lives in %ProgramData%\NetUse. Keep it unless the user asks to remove it.
  if CurUninstallStep <> usPostUninstall then
    Exit;
  Dir := DataDir();
  Attr := GetFileAttributes(Dir);
  // missing, or a link someone put in its place: never follow it
  if (Attr = INVALID_FILE_ATTRIBUTES) or ((Attr and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then
    Exit;
  if SuppressibleMsgBox('Also delete your NetUse usage history and settings (' + Dir + ')?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    DelTree(Dir, True, True, True);
end;
