@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo ==========================================================
echo   AudioTwin - build
echo ==========================================================
echo.

rem ---- pick the right architecture -------------------------------------
set "RID=win-x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "RID=win-arm64"
if /i "%PROCESSOR_ARCHITEW6432%"=="ARM64" set "RID=win-arm64"
echo Target architecture: %RID%

rem ---- is the .NET 8 SDK available? ------------------------------------
where dotnet >nul 2>&1
if errorlevel 1 goto nosdk

set "HAS8="
for /f "tokens=1" %%v in ('dotnet --list-sdks 2^>nul') do (
    echo %%v | findstr /b /c:"8." >nul && set "HAS8=1"
    echo %%v | findstr /b /c:"9." >nul && set "HAS8=1"
    echo %%v | findstr /b /c:"10." >nul && set "HAS8=1"
)
if not defined HAS8 goto nosdk

rem ---- build ------------------------------------------------------------
set "OUTDIR=%~dp0publish"
echo.
echo Building a standalone executable (this takes a minute the first time)...
echo.

dotnet publish "src\AudioTwin\AudioTwin.csproj" ^
    -c Release ^
    -r %RID% ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o "%OUTDIR%"

if errorlevel 1 goto failed
if not exist "%OUTDIR%\AudioTwin.exe" goto failed

echo.
echo ==========================================================
echo   Done.
echo   %OUTDIR%\AudioTwin.exe
echo.
echo   This file is fully standalone - copy it anywhere.
echo   No .NET installation is needed on the target machine.
echo ==========================================================
echo.

choice /c YN /n /m "Run it now? [Y/N] "
if errorlevel 2 goto end
start "" "%OUTDIR%\AudioTwin.exe"
goto end

rem ---- no SDK -----------------------------------------------------------
:nosdk
echo.
echo The .NET 8 SDK was not found on this machine.
echo.
where winget >nul 2>&1
if errorlevel 1 goto manualsdk

choice /c YN /n /m "Install it now with winget? [Y/N] "
if errorlevel 2 goto manualsdk
echo.
winget install --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
echo.
echo Installation finished. Close this window, open a NEW command prompt,
echo and run build.cmd again so the new PATH takes effect.
goto end

:manualsdk
echo Download and install the .NET 8 SDK from:
echo     https://dotnet.microsoft.com/download/dotnet/8.0
echo Then run build.cmd again.
goto end

:failed
echo.
echo ==========================================================
echo   BUILD FAILED - see the messages above.
echo ==========================================================
echo   Most common cause: no internet access to restore the
echo   NAudio package from nuget.org.
echo.

:end
echo.
pause
endlocal
