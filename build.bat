@echo off
setlocal
rem Never run a same-named tool (dotnet.exe, signtool.exe, powershell.exe...) that happens to sit in this folder.
set NoDefaultCurrentDirectoryInExePath=1
cd /d "%~dp0"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
echo === NetUse build ===
echo Builds NetUse.exe and its native DLLs into this folder, plus the portable zip, the installer and SHA256SUMS
echo.

if not exist NetUse.csproj (
  echo ERROR: NetUse.csproj not found. Run build.bat from the NetUse source folder.
  goto :end
)

set SDKMAJOR=
for /f "tokens=1 delims=." %%m in ('dotnet --version 2^>nul') do set SDKMAJOR=%%m
if not defined SDKMAJOR goto :nosdk
if %SDKMAJOR% LSS 10 goto :nosdk

echo [1/4] Publishing...
if exist publish rmdir /s /q publish
dotnet publish NetUse.csproj -c Release -o publish
if errorlevel 1 ( echo. & echo ERROR: dotnet publish failed. & goto :end )

rem Optional code signing (removes the SmartScreen "unknown publisher" warning and lets people check who built it):
rem   set NETUSE_SIGN_THUMBPRINT=<SHA-1 thumbprint of a code-signing certificate in your certificate store or on a token>
rem The certificate's password or PIN never goes on a command line, where other programs and audit logs can see it.
rem The Microsoft DLLs already carry Microsoft's signature; NetUse.exe and e_sqlite3.dll get yours.
echo.
echo [2/4] Code signing...
if defined NETUSE_SIGN_THUMBPRINT (
  signtool sign /sha1 %NETUSE_SIGN_THUMBPRINT% /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 publish\NetUse.exe publish\e_sqlite3.dll
  if errorlevel 1 ( echo ERROR: signing failed. & goto :end )
) else (
  echo Skipped ^(set NETUSE_SIGN_THUMBPRINT to sign^)
)

rem NetUse.exe needs the native DLLs next to it (they are deliberately not self-extracted to %%TEMP%%)
copy /y publish\NetUse.exe NetUse.exe >nul
if errorlevel 1 ( echo ERROR: could not copy NetUse.exe - close NetUse if it is running. & goto :end )
copy /y publish\*.dll . >nul
if errorlevel 1 ( echo ERROR: could not copy the DLLs - close NetUse if it is running. & goto :end )

rem version = <Version> in NetUse.csproj
rem (%PS% unquoted here: inside for /f a line that starts and ends with quotes loses them; the Windows path has no spaces)
for /f "usebackq delims=" %%v in (`%PS% -NoProfile -Command "([xml](Get-Content NetUse.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set VER=%%v
if not defined VER ( echo ERROR: could not read the version from NetUse.csproj. & goto :end )
if not exist setup\Windows mkdir setup\Windows

echo.
echo [3/4] Portable package...
set PORTABLE=setup\Windows\NetUse-%VER%-portable-win-x64.zip
if exist installer rmdir /s /q installer
mkdir installer\NetUse
copy /y publish\NetUse.exe installer\NetUse\ >nul
copy /y publish\*.dll installer\NetUse\ >nul
copy /y setup\portable-readme.txt installer\NetUse\README.txt >nul
"%PS%" -NoProfile -Command "Compress-Archive -Path 'installer\NetUse' -DestinationPath '%PORTABLE%' -Force"
if errorlevel 1 ( echo ERROR: could not create %PORTABLE%. & goto :end )
rmdir /s /q installer
echo %PORTABLE%

echo.
echo [4/4] Installer...
rem Inno Setup compiler: on PATH, in ISCC_PATH, or a standard install folder
set ISCC=
where iscc >nul 2>nul && set ISCC=iscc
if not defined ISCC if defined ISCC_PATH set "ISCC=%ISCC_PATH%"
for %%d in ("%ProgramFiles(x86)%\Inno Setup 7" "%ProgramFiles%\Inno Setup 7" "%LOCALAPPDATA%\Programs\Inno Setup 7" "%ProgramFiles(x86)%\Inno Setup 6" "%ProgramFiles%\Inno Setup 6" "%LOCALAPPDATA%\Programs\Inno Setup 6") do (
  if not defined ISCC if exist "%%~d\ISCC.exe" set "ISCC=%%~d\ISCC.exe"
)
if not defined ISCC (
  echo Skipped: Inno Setup not found. Install it from https://jrsoftware.org/isinfo.php or set ISCC_PATH.
  goto :sums
)
"%ISCC%" /Q setup\NetUse.iss
if errorlevel 1 ( echo ERROR: Inno Setup failed. & goto :end )
if defined NETUSE_SIGN_THUMBPRINT (
  signtool sign /sha1 %NETUSE_SIGN_THUMBPRINT% /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "setup\Windows\NetUse-%VER%-setup-win-x64.exe"
  if errorlevel 1 ( echo ERROR: signing the installer failed. & goto :end )
)
echo setup\Windows\NetUse-%VER%-setup-win-x64.exe

:sums
rem SHA256SUMS (sha256sum format) for the release page, so downloads can be checked
"%PS%" -NoProfile -Command "Get-ChildItem 'setup\Windows' -File | Where-Object { $_.Name -like 'NetUse-%VER%-*' -and $_.Extension -in '.zip','.exe' } | Sort-Object Name | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } | Set-Content -Encoding ascii 'setup\Windows\NetUse-%VER%-SHA256SUMS.txt'"
echo setup\Windows\NetUse-%VER%-SHA256SUMS.txt

echo.
echo SUCCESS: %CD%\NetUse.exe
echo Double-click NetUse.exe in this folder to run it. Release files are in setup\Windows.
goto :end

:nosdk
echo ERROR: the .NET 10 SDK is needed to build from source: https://dotnet.microsoft.com/download/dotnet/10.0
echo Running NetUse needs nothing installed. Only building it needs the SDK.

:end
echo.
pause
