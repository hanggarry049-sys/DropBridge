@echo off
setlocal

rem --- 定位 dotnet：优先 PATH，其次常见安装位置 ---
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

rem --- 补齐 Windows 关键环境变量 ---
rem NuGet 还原时会用 Path.Combine(ProgramFiles, "NuGet") 计算机器级配置目录，
rem 某些受限 shell 下 ProgramFiles / ProgramData 未设置（为 null 而非空串），
rem 会直接抛 "Value cannot be null. (Parameter 'path1')" 导致还原失败。
if not defined ProgramData      set "ProgramData=C:\ProgramData"
if not defined ALLUSERSPROFILE  set "ALLUSERSPROFILE=C:\ProgramData"
if not defined ProgramFiles     set "ProgramFiles=C:\Program Files"
if not defined CommonProgramFiles set "CommonProgramFiles=C:\Program Files\Common Files"
if not defined APPDATA          set "APPDATA=%USERPROFILE%\AppData\Roaming"
if not defined LOCALAPPDATA     set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"

"%DOTNET%" build "%~dp0src\DropBridge\DropBridge.csproj" -c Release %*

endlocal
