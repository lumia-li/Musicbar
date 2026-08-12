@echo off
setlocal

set "ROOT=%~dp0"
set "PROJECT=%ROOT%app\MusicBar.csproj"
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format 'M.d'"') do set "TODAY=%%i"
set "PUBLISH_DIR=%ROOT%dist\MusicBar%TODAY%"
set "ISS_FILE=%ROOT%installer\MusicBar.iss"

echo Publishing MusicBar...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "%PUBLISH_DIR%"
if errorlevel 1 goto :failed

set "ISCC=iscc"
where iscc >nul 2>nul
if errorlevel 1 (
    if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
    if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
    if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
    if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
    if exist "D:\Application\Inno Setup 6\ISCC.exe" set "ISCC=D:\Application\Inno Setup 6\ISCC.exe"
)

where "%ISCC%" >nul 2>nul
if errorlevel 1 if not exist "%ISCC%" (
    echo.
    echo Inno Setup was not found.
    echo Install it from https://jrsoftware.org/isinfo.php, then run this file again.
    echo Published files are ready at: "%PUBLISH_DIR%"
    goto :failed
)

echo Building installer...
"%ISCC%" "%ISS_FILE%"
if errorlevel 1 goto :failed

echo.
echo Done. Installer is in: "%ROOT%installer\Output"
goto :end

:failed
echo.
echo Build failed.

:end
echo.
pause
