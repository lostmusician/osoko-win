; ============================================================================
;  Osoko — Windows installer (Inno Setup 6)
;
;  Build the payload first:
;    dotnet publish src\Osoko\Osoko.csproj -c Release -r win-x64 --self-contained true ^
;      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
;      -p:EnableCompressionInSingleFile=true -o publish\win-x64
;
;  Then compile:
;    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\OsokoSetup.iss ^
;      /DMyAppVersion=1.0.0
;
;  Optional defines: MyAppVersion, PublishDir, OutputDir.
; ============================================================================

#define MyAppName "Osoko"
#define MyAppPublisher "Osoko"
#define MyAppExeName "Osoko.exe"
#define MyAppId "{{1F0F0F0C-6C55-4E2B-9F4E-2B0B7E3A4C11}"
#define MyAppUrl "https://github.com/lostmusician/osoko-win"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

; Numeric x.y.z[.w] used for the Windows file version resource. CI passes a
; sanitised value when the release tag contains a pre-release suffix.
#ifndef MyAppVersionInfo
  #define MyAppVersionInfo MyAppVersion
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "Output"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}
VersionInfoVersion={#MyAppVersionInfo}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} — floating screenshot shelf
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersionInfo}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=OsokoSetup-{#MyAppVersion}-x64
SetupIconFile=..\src\Osoko\Assets\osoko.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Per-user install by default (no UAC), with an optional all-users mode.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "startupicon"; Description: "Start Osoko when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Osoko — floating screenshot shelf"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Comment: "Osoko — floating screenshot shelf"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
; Optional auto-start. Osoko also manages this key itself (Settings → Launch at Windows startup).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Osoko"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupicon

[UninstallDelete]
; The single-file host may leave an extraction cache behind; settings are intentionally kept.
Type: filesandordirs; Name: "{localappdata}\Osoko\Scratch"
