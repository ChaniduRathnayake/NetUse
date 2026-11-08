@echo off
setlocal
cd /d "%~dp0"
echo === NetUse build ===
echo Builds one self-contained file: NetUse.exe in this folder
echo.

if not exist NetUse.csproj (
  echo ERROR: NetUse.csproj not found. Run build.bat from the NetUse source folder.
  goto :end
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: the .NET 8 SDK is needed to build from source: https://dotnet.microsoft.com/download/dotnet/8.0
  echo Running NetUse.exe needs nothing installed. Only building it needs the SDK.
  goto :end
)

echo [1/2] Publishing...
if exist publish rmdir /s /q publish
dotnet publish NetUse.csproj -c Release -o publish
if errorlevel 1 ( echo. & echo ERROR: dotnet publish failed. & goto :end )
copy /y publish\NetUse.exe NetUse.exe >nul
if errorlevel 1 ( echo ERROR: could not copy NetUse.exe - close NetUse if it is running. & goto :end )

rem Optional code signing (removes the SmartScreen "unknown publisher" warning):
rem set NETUSE_SIGN_PFX=path\to\cert.pfx and NETUSE_SIGN_PASS=password before running.
echo.
echo [2/2] Code signing...
if defined NETUSE_SIGN_PFX (
  signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f "%NETUSE_SIGN_PFX%" /p "%NETUSE_SIGN_PASS%" NetUse.exe
  if errorlevel 1 ( echo ERROR: signing failed. & goto :end )
) else (
  echo Skipped ^(set NETUSE_SIGN_PFX to sign^)
)

echo.
echo SUCCESS: %CD%\NetUse.exe
echo Double-click NetUse.exe in this folder to run it.

:end
echo.
pause
