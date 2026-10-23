@echo off
rem ==========================================================================
rem  Fast Share's downloads. Website\"2 archive this build.bat" calls:
rem      Code\package-app.bat <version> "<project folder>"
rem  and gets, in Website\, made from Website\FastShare\ (the copy Website\1 build.bat made from Build\):
rem    Fast-Share-V<version>.rar         portable: FastShare.exe, the 5 native DLLs and GUIDE.md, flat
rem                                      (WinRAR -ep1). Name AND inside layout must never change: every
rem                                      FastShare out there updates itself by finding exactly this file
rem                                      on the website and unpacking it over its own folder.
rem    Fast-Share-V<version>-Setup.exe   installer (Inno Setup 6, Code\installer.iss), the same files
rem  Needs: WinRAR (C:\Program Files\WinRAR\Rar.exe) and Inno Setup 6.
rem ==========================================================================
setlocal
set "VERSION=%~1"
set "ROOT=%~2"
set "WEB=%ROOT%\Website"
set "CODE=%ROOT%\Code"
for /f "usebackq eol=# tokens=1,* delims==" %%a in ("%ROOT%\project.cfg") do set "%%a=%%b"
set "PROGRAM=%EXE:~0,-4%"
set "SRC=%WEB%\%PROGRAM%"
if "%VERSION%"=="" echo Run "2 archive this build.bat" in Website\, not this. & exit /b 1

set "RAR=%ProgramFiles%\WinRAR\Rar.exe"
if not exist "%RAR%" echo WinRAR was not found: %RAR% & exit /b 1
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" echo Inno Setup 6 was not found: %ISCC% & exit /b 1

set "FILES=FastShare.exe D3DCompiler_47_cor3.dll PenImc_cor3.dll PresentationNative_cor3.dll vcruntime140_cor3.dll wpfgfx_cor3.dll GUIDE.md"
for %%f in (%FILES%) do if not exist "%SRC%\%%f" echo %%f is missing from %SRC%. & exit /b 1

rem The update check compares the exe's own version with the website's: never ship it under another number.
set "INEXE="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$v = [version](Get-Item -LiteralPath '%SRC%\FastShare.exe').VersionInfo.FileVersion; '{0}.{1}.{2}' -f $v.Major, $v.Minor, $v.Build"`) do set "INEXE=%%v"
if not "%INEXE%"=="%VERSION%" echo %PROGRAM%\FastShare.exe is V%INEXE%, not V%VERSION%: build again. & exit /b 1

rem Portable .rar (rar "a" would add to an old archive rather than replace it).
set "ARCHIVE=%WEB%\%NAME%-V%VERSION%.rar"
if exist "%ARCHIVE%" del /q "%ARCHIVE%"
pushd "%SRC%"
"%RAR%" a -ep1 -idq "%ARCHIVE%" %FILES%
set "RC=%ERRORLEVEL%"
popd
if not "%RC%"=="0" echo WinRAR failed (%RC%). & exit /b 1
echo Made %NAME%-V%VERSION%.rar

rem Installer.
"%ISCC%" /Q "/DAppVersion=%VERSION%" "/DSourceDir=%SRC%" "/DOutDir=%WEB%" "%CODE%\installer.iss"
if errorlevel 1 echo Inno Setup failed. & exit /b 1
echo Made %NAME%-V%VERSION%-Setup.exe
exit /b 0
