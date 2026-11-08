@echo off
setlocal
cd /d "%~dp0"
echo === NetUse build ===
echo Builds NetUse.exe and its native DLLs into this folder, plus the portable zip and the installer
echo.

if not exist NetUse.csproj (
  echo ERROR: NetUse.csproj not found. Run build.bat from the NetUse source folder.
  goto :end
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: the .NET 8 SDK is needed to build from source: https://dotnet.microsoft.com/download/dotnet/8.0
  echo Running NetUse needs nothing installed. Only building it needs the SDK.
  goto :end
)

echo [1/4] Publishing...
if exist publish rmdir /s /q publish
dotnet publish NetUse.csproj -c Release -o publish
if errorlevel 1 ( echo. & echo ERROR: dotnet publish failed. & goto :end )

rem Optional code signing (removes the SmartScreen "unknown publisher" warning):
rem set NETUSE_SIGN_PFX=path\to\cert.pfx and NETUSE_SIGN_PASS=password before running.
echo.
echo [2/4] Code signing...
if defined NETUSE_SIGN_PFX (
  signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f "%NETUSE_SIGN_PFX%" /p "%NETUSE_SIGN_PASS%" publish\NetUse.exe
  if errorlevel 1 ( echo ERROR: signing failed. & goto :end )
) else (
  echo Skipped ^(set NETUSE_SIGN_PFX to sign^)
)

rem NetUse.exe needs the native DLLs next to it (they are deliberately not self-extracted to %%TEMP%%)
copy /y publish\NetUse.exe NetUse.exe >nul
if errorlevel 1 ( echo ERROR: could not copy NetUse.exe - close NetUse if it is running. & goto :end )
copy /y publish\*.dll . >nul
if errorlevel 1 ( echo ERROR: could not copy the DLLs - close NetUse if it is running. & goto :end )

rem version = <Version> in NetUse.csproj
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content NetUse.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set VER=%%v
if not exist setup\Windows mkdir setup\Windows

echo.
echo [3/4] Portable package...
set PORTABLE=setup\Windows\NetUse-%VER%-portable-win-x64.zip
if exist installer rmdir /s /q installer
mkdir installer\NetUse
copy /y publish\NetUse.exe installer\NetUse\ >nul
copy /y publish\*.dll installer\NetUse\ >nul
copy /y setup\portable-readme.txt installer\NetUse\README.txt >nul
powershell -NoProfile -Command "Compress-Archive -Path 'installer\NetUse' -DestinationPath '%PORTABLE%' -Force"
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
  goto :done
)
"%ISCC%" /Q setup\NetUse.iss
if errorlevel 1 ( echo ERROR: Inno Setup failed. & goto :end )
if defined NETUSE_SIGN_PFX (
  signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f "%NETUSE_SIGN_PFX%" /p "%NETUSE_SIGN_PASS%" "setup\Windows\NetUse-%VER%-setup-win-x64.exe"
)
echo setup\Windows\NetUse-%VER%-setup-win-x64.exe

:done
echo.
echo SUCCESS: %CD%\NetUse.exe
echo Double-click NetUse.exe in this folder to run it. Release files are in setup\Windows.

:end
echo.
pause
