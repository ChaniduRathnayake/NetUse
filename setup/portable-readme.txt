NetUse - portable
=================

Network monitor and per-app firewall for Windows 10/11 (64-bit).

Run
  Double-click NetUse.exe. Nothing to install, no .NET needed.
  Keep the DLL files in the same folder as NetUse.exe: it loads them from here.
  Windows asks for administrator rights: NetUse needs them for kernel
  network tracing and Windows Firewall rules.

  If SmartScreen says "Windows protected your PC", click
  More info -> Run anyway.

Speed widgets
  Settings -> Speed widgets (or the tray icon) turns on a taskbar readout
  next to the clock and/or a small floating window: upload, download,
  CPU, memory, temperature and CPU clock. Right-click a widget for options.

Where data is kept
  Usage history and settings: C:\ProgramData\NetUse

Where to keep this folder
  NetUse runs as administrator, so any program that can change the files in
  this folder could get administrator rights the next time NetUse starts.
  Programs running as you can change Downloads, the Desktop and USB sticks.
  NetUse works from there, but it warns you. For everyday use, install NetUse
  or turn on Start with Windows.

Start with Windows
  Settings -> Start with Windows. NetUse copies itself to
  C:\Program Files\NetUse first, because an elevated logon task must
  only point at a folder that standard users can't change. It copies
  exactly the files it is running from, never whatever is in this folder now.

Check the download
  NetUse-<version>-SHA256SUMS.txt on the release page lists each file's
  SHA-256. In PowerShell: Get-FileHash <zip file>   (compare the two)

Remove
  1. Settings -> turn off Start with Windows.
  2. Firewall page -> allow any apps you blocked (Windows Firewall keeps
     those rules otherwise).
  3. Close NetUse from the tray icon (Exit) and delete this folder.
  4. Optional: delete C:\ProgramData\NetUse.

More: https://github.com/ChaniduRathnayake/NetUse
