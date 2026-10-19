@echo off
setlocal

rem ============================================================
rem  DropBridge - publish script (portable single-file exe)
rem
rem  Requires the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
rem  Usage: double-click this file. Output: publish\DropBridge.exe
rem ============================================================

rem --- 1. Locate a dotnet that actually ships an SDK ---
rem Caveat: C:\Program Files\dotnet sometimes contains only the *runtime*
rem (no "sdk" subfolder). Pointing the build at it fails with
rem "No .NET SDKs were found", so every candidate is validated with --list-sdks.
set "DOTNET="
call :probe "%ProgramFiles%\dotnet\dotnet.exe"
call :probe "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
for /f "delims=" %%I in ('where dotnet 2^>nul') do call :probe "%%I"
rem Some sandbox / portable setups (for example an AI toolchain) install
rem a full SDK here:
call :probe "%USERPROFILE%\.workbuddy\binaries\dotnet\dotnet.exe"

if not defined DOTNET (
    echo [X] No dotnet with an SDK was found. Please install the .NET 8 SDK:
    echo     https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)
echo [i] dotnet: %DOTNET%

rem --- 2. Fill in essential Windows environment variables ---
rem NuGet computes its machine-wide config directory via
rem Path.Combine(ProgramFiles, "NuGet"). In restricted shells those variables
rem can be unset (null rather than empty) and restore fails with
rem "Value cannot be null. (Parameter 'path1')".
if not defined ProgramData      set "ProgramData=C:\ProgramData"
if not defined ALLUSERSPROFILE  set "ALLUSERSPROFILE=C:\ProgramData"
if not defined ProgramFiles     set "ProgramFiles=C:\Program Files"
if not defined CommonProgramFiles set "CommonProgramFiles=C:\Program Files\Common Files"
if not defined APPDATA          set "APPDATA=%USERPROFILE%\AppData\Roaming"
if not defined LOCALAPPDATA     set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"

rem --- 3. Kill any leftover instance that still holds the output file ---
taskkill /F /IM DropBridge.exe >nul 2>&1

rem --- 4. Publish ---
"%DOTNET%" publish "%~dp0src\DropBridge\DropBridge.csproj" -c Release -r win-x64 --self-contained true --nologo ^
  -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false ^
  -o "%~dp0publish"

if errorlevel 1 (
    echo.
    echo [X] publish failed
    exit /b 1
)
echo.
echo [OK] output: %~dp0publish\DropBridge.exe
endlocal
exit /b 0

rem --- Candidate check: the path must exist AND --list-sdks must print something
:probe
if defined DOTNET exit /b
if not exist "%~1" exit /b
"%~1" --list-sdks 2>nul | findstr . >nul
if errorlevel 1 exit /b
set "DOTNET=%~1"
exit /b
