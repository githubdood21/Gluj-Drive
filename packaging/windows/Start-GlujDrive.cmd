@echo off
setlocal
cd /d "%~dp0"
title Gluj Drive

if not exist "GlujDrive.Server.exe" (
    echo Gluj Drive could not find GlujDrive.Server.exe.
    echo Keep this launcher in the same directory as the published application.
    pause
    exit /b 1
)

rem A self-contained installation includes hostfxr.dll. The smaller portable
rem package relies on the matching ASP.NET Core runtime installed on Windows.
if not exist "hostfxr.dll" (
    where dotnet.exe >nul 2>&1
    if errorlevel 1 goto missing_runtime

    dotnet.exe --list-runtimes | findstr.exe /B /C:"Microsoft.AspNetCore.App 10." >nul
    if errorlevel 1 goto missing_runtime
)

if /I not "%~1"=="--no-browser" (
    start "" /B powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0Open-GlujDriveWhenReady.ps1"
)

echo.
echo Gluj Drive is starting at http://localhost:5199
echo Other devices can use http://THIS-PC-IP:5199
echo.
echo Keep this window open. Press Ctrl+C or close it to stop Gluj Drive.
echo.

"%~dp0GlujDrive.Server.exe"
set "GLUJDRIVE_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%GLUJDRIVE_EXIT_CODE%"=="0" (
    echo Gluj Drive stopped with exit code %GLUJDRIVE_EXIT_CODE%.
    echo Review the messages above, then press any key to close this window.
    pause >nul
) else (
    echo Gluj Drive stopped.
)

endlocal & exit /b %GLUJDRIVE_EXIT_CODE%

:missing_runtime
echo.
echo The portable edition requires the ASP.NET Core Runtime 10 for Windows x64.
echo Download it from:
echo https://dotnet.microsoft.com/download/dotnet/10.0
echo.
echo The installed edition includes its own runtime and does not require this download.
pause
exit /b 2
