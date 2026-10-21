; Inno Setup script for FastShare. Built by package-app.bat (Website\"archive this build.bat"):
;   ISCC /DAppVersion=x.y.z /DSourceDir=<Website\FastShare> /DOutDir=<Website> installer.iss
; Shown as "Fast Share" (Start menu, Windows search, Apps / Control Panel); the exe stays FastShare.exe.
; Installs per-user into %LOCALAPPDATA%\Programs\Fast Share - no admin prompt, and FastShare's
; own updater can replace its exe there, which Program Files would forbid without elevation
; (and FastShare must never run elevated: it breaks drag-and-drop from Explorer).
; Settings live in %APPDATA%\FastShare and are kept on uninstall.

#define AppName "Fast Share"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppExe "FastShare.exe"
; Where the app is taken from and where the Setup.exe goes (package-app.bat passes both).
#ifndef SourceDir
  #define SourceDir "..\Website\FastShare"
#endif
#ifndef OutDir
  #define OutDir "..\Website"
#endif

[Setup]
AppId={{B4E7C2A1-6F3D-4C8E-9A15-3D7E2F6B8C94}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher=Patrick
AppPublisherURL=https://teamwolfgamers.online/fast-share
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=Fast-Share-V{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
; The Apps list shows the version in its own column, so the name is just "Fast Share".
UninstallDisplayName={#AppName}
SetupIconFile=FastShare.ico
; A running FastShare holds its exe: Setup offers to close it first, and does not start it
; again behind the user's back (the Launch box at the end does that).
CloseApplications=yes
RestartApplications=no

[Messages]
SetupWindowTitle=Setup - {#AppName} v{#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; The same files an update ships (Updater.PackagedFiles): the exe, the native DLLs and the guide.
; Not the update server or anything else that lives beside them.
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Shortcuts from 2.4.38, when the app was still called "FastShare": an upgrade would otherwise leave
; them next to the new "Fast Share" ones. Same AppId, so an upgrade stays in the old install folder.
Type: files; Name: "{autoprograms}\FastShare.lnk"
Type: files; Name: "{autoprograms}\FastShare Guide.lnk"
Type: files; Name: "{autodesktop}\FastShare.lnk"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{#AppName} Guide"; Filename: "{app}\GUIDE.md"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Left behind by FastShare's own updater (the previous exe, swapped out) and debug mode.
Type: files; Name: "{app}\*.fsold"
Type: files; Name: "{app}\fastshare-debug.log"
