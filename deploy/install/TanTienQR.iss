#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
#ifndef StagingDirectory
  #error StagingDirectory must be supplied by build-installer.ps1
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied by build-installer.ps1
#endif
#ifndef IconPath
  #error IconPath must be supplied by build-installer.ps1
#endif

[Setup]
AppId={{D1B36EF6-0C58-44B8-AB12-DDEFB80E8871}
AppName=TanTienQR
AppVersion={#AppVersion}
AppPublisher=Tan Tien
DefaultDirName={localappdata}\Programs\TanTienQR
DefaultGroupName=TanTienQR
UninstallDisplayName=TanTienQR
UninstallDisplayIcon={app}\TanTienQR.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
OutputDir={#OutputDirectory}
OutputBaseFilename=TanTienQR-{#AppVersion}-win-x64-setup
SetupIconFile={#IconPath}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng TanTienQR trên màn hình Desktop"; GroupDescription: "Biểu tượng bổ sung:"; Flags: unchecked

[Files]
Source: "{#StagingDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\TanTienQR"; Filename: "{app}\TanTienLauncher.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\TanTienQR"; Filename: "{app}\TanTienLauncher.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TanTienLauncher.exe"; Description: "Khởi chạy TanTienQR"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent
