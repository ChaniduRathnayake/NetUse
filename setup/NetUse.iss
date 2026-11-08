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

; NetUse holds this mutex while running: Setup and the uninstaller ask the user to close it first
AppMutex=Global\NetUseSingleInstanceMutex
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
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Usage history lives in %ProgramData%\NetUse. Keep it unless the user asks to remove it.
  if (CurUninstallStep = usPostUninstall) and DirExists(ExpandConstant('{commonappdata}\NetUse')) then
    if SuppressibleMsgBox('Also delete your NetUse usage history and settings (' +
        ExpandConstant('{commonappdata}\NetUse') + ')?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DelTree(ExpandConstant('{commonappdata}\NetUse'), True, True, True);
end;
