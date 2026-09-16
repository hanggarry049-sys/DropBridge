@echo off
setlocal

rem ============================================================
rem  DropBridge 打包脚本 —— 生成免安装单文件 exe
rem
rem  前置条件：安装 .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0）
rem  用法：双击本文件，产物落在 publish\DropBridge.exe
rem ============================================================

rem --- 1. 定位 dotnet：优先 PATH，其次常见安装位置 ---
set "DOTNET="
for /f "delims=" %%I in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%I"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "C:\Program Files\dotnet\dotnet.exe" set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not defined DOTNET if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET (
    echo [X] 未找到 dotnet，请先安装 .NET 8 SDK：
    echo     https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)
echo [i] dotnet: %DOTNET%

rem --- 2. 补齐 Windows 关键环境变量 ---
rem NuGet 还原时会用 Path.Combine(ProgramFiles, "NuGet") 计算机器级配置目录，
rem 某些受限 shell / 精简环境下 ProgramFiles / ProgramData 未设置（为 null 而非空串），
rem 会直接抛 "Value cannot be null. (Parameter 'path1')" 导致还原失败。
if not defined ProgramData      set "ProgramData=C:\ProgramData"
if not defined ALLUSERSPROFILE  set "ALLUSERSPROFILE=C:\ProgramData"
if not defined ProgramFiles     set "ProgramFiles=C:\Program Files"
if not defined CommonProgramFiles set "CommonProgramFiles=C:\Program Files\Common Files"
if not defined APPDATA          set "APPDATA=%USERPROFILE%\AppData\Roaming"
if not defined LOCALAPPDATA     set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"

rem --- 3. 清掉可能占用输出文件的残留进程 ---
taskkill /F /IM DropBridge.exe >nul 2>&1

rem --- 4. 发布 ---
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
