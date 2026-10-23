@echo off
rem ==========================================================================
rem  Fast Share's build steps. build.bat (in the project folder) calls:
rem      Code\build-app.bat <version> "<project folder>"
rem  and gets Build\ with FastShare.exe at its top (+ the 5 native WPF DLLs, GUIDE.md and
rem  updater-tool\ = Patrick's update server, which is not part of the downloads).
rem
rem    1. the checks (Code\Tests, ~25 s): a build that breaks something is never made
rem    2. dotnet publish FastShare.csproj -p:Version=<version> into an empty staging folder
rem       (Code\obj\publish-stage), then copied over Build\: publishing straight over Build\
rem       when no code had changed left only FastShare.exe there (seen 2026-10-06)
rem    3. refuses a build missing any file it needs to start; GUIDE.md beside the exe
rem    4. the update server (Code\UpdateServer) into Build\updater-tool\, unless it is open
rem
rem  FASTSHARE_UPDATE_WEBSITE (optional environment variable): this build checks that address
rem  for updates instead of UpdateWebsite in FastShare.csproj, e.g.
rem      set FASTSHARE_UPDATE_WEBSITE=http://1.2.3.4:8080/
rem  Needs: .NET 10 SDK. FastShare must not be running from Build\ (a running exe can't be replaced).
rem  Nothing is made for the website here (that's Website\1 build.bat and package-app.bat).
rem ==========================================================================
setlocal
set "VERSION=%~1"
set "ROOT=%~2"
set "CODE=%ROOT%\Code"
set "BUILD=%ROOT%\Build"
if "%VERSION%"=="" echo Run build.bat in the project folder, not this. & exit /b 1
where dotnet >nul 2>nul || (echo The .NET SDK is not installed: https://dotnet.microsoft.com/download & exit /b 1)

powershell -NoProfile -Command "if (Get-Process FastShare -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '%BUILD%\*' }) { exit 1 }"
if errorlevel 1 (
    echo FastShare is running from %BUILD%. Close it, then build again.
    exit /b 1
)

echo Running FastShare's checks...
dotnet run --project "%CODE%\Tests" -c Release > "%TEMP%\FastShareTests.log" 2>&1
if errorlevel 1 (
    type "%TEMP%\FastShareTests.log"
    echo.
    echo The checks FAILED, so nothing was built. See above.
    exit /b 1
)
for /f "delims=" %%l in ('findstr /c:"CHECKS PASSED" "%TEMP%\FastShareTests.log"') do echo %%l

set "STAGE=%CODE%\obj\publish-stage"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
set "SITE="
if defined FASTSHARE_UPDATE_WEBSITE set "SITE=-p:UpdateWebsite=%FASTSHARE_UPDATE_WEBSITE%"
if defined SITE echo This build checks %FASTSHARE_UPDATE_WEBSITE% for updates.
echo Building FastShare V%VERSION%...
dotnet publish "%CODE%\FastShare.csproj" -c Release -o "%STAGE%" "-p:Version=%VERSION%" %SITE%
if errorlevel 1 exit /b 1
if not exist "%BUILD%" mkdir "%BUILD%"
robocopy "%STAGE%" "%BUILD%" /NJH /NJS /NDL /NFL /NP >nul
if errorlevel 8 echo Copying the build into %BUILD% failed. & exit /b 1

rem Not needed by a self-contained desktop app, and publishing drops it in anyway.
if exist "%BUILD%\aspnetcorev2_inprocess.dll" del "%BUILD%\aspnetcorev2_inprocess.dll"

rem Never leave a build that is missing a file it needs to start.
for %%f in (FastShare.exe D3DCompiler_47_cor3.dll PenImc_cor3.dll PresentationNative_cor3.dll vcruntime140_cor3.dll wpfgfx_cor3.dll) do (
    if not exist "%BUILD%\%%f" (
        echo %%f is missing from %BUILD% after publishing.
        exit /b 1
    )
)

rem The guide ships in every update, the .rar and the installer.
copy /y "%CODE%\GUIDE.md" "%BUILD%\GUIDE.md" >nul

rem The update server shares the updater code, so rebuild it too - unless it is open.
powershell -NoProfile -Command "if (Get-Process FastShareUpdateServer -ErrorAction SilentlyContinue) { exit 1 }"
if errorlevel 1 (
    echo The update server is open, so it was not rebuilt. Close it and build again to update it.
) else (
    dotnet publish "%CODE%\UpdateServer" -c Release -o "%BUILD%\updater-tool"
    if errorlevel 1 exit /b 1
)
exit /b 0
