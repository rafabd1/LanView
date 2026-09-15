#ifndef BundleDir
  #error Supply BundleDir with the clean, extracted LanView package.
#endif
#ifndef ProductVersion
  #error Supply ProductVersion from bundle.json.
#endif
#ifndef SetupOutputDir
  #error Supply SetupOutputDir for the installer output.
#endif

[Setup]
AppId={{C50A29AF-8E13-437E-9C85-616B8764B710}
AppName=LanView
AppVersion={#ProductVersion}
AppVerName=LanView {#ProductVersion}
DefaultDirName={%USERPROFILE}\.local\share\LanView\App
DefaultGroupName=LanView
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#SetupOutputDir}
OutputBaseFilename=LanView-{#ProductVersion}-Setup-win-x64
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=LanView
UninstallDisplayIcon={app}\LanView.exe
UninstallFilesDir={app}\uninstall
CloseApplications=no
RestartApplications=no
SetupLogging=yes
SetupMutex=LanView.Setup.C50A29AF8E13437E9C85616B8764B710

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#BundleDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "compiler:license.txt"; DestDir: "{app}\licenses\inno-setup"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LanView"; Filename: "{app}\LanView.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\LanView"; Filename: "{app}\LanView.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\LanView.exe"; Description: "{cm:LaunchProgram,LanView}"; Flags: nowait postinstall skipifsilent unchecked

; Uninstall removes only files recorded by Setup. Profiles, pairing state and
; received-file caches live outside {app}; no UninstallDelete entries are used.
