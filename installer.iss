#define MyAppName "PC MQTT Monitor"
#define MyAppVersion "1.1.2"
#define MyAppPublisher "mvoss"
#define MyAppExeName "PcMqttMonitor.exe"
#define MyAppTaskName "PcMqttMonitor"

[Setup]
; Unique AppId — do not change this after first release or upgrades will break.
AppId={{6F3A2B1C-9E4D-4F8A-B3C7-2D5E8F1A0B9C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Upgrade: automatically uninstall previous version before installing new one.
; The GUID match above ensures this works across versions.
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
; Output
OutputDir=installer-output
OutputBaseFilename=PcMqttMonitorSetup-{#MyAppVersion}
SetupIconFile=app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Admin required only for the scheduled task registration (/RL HIGHEST).
; The install itself goes to LocalAppData so no UAC prompt is needed for the copy.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Upgrade: remove old version cleanly before installing new files.
; CloseApplications will prompt the user to close the running app first.
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; \
  Description: "Start {#MyAppName} automatically when Windows starts"; \
  GroupDescription: "Additional options:"; \
  Flags: unchecked
Name: "desktopicon"; \
  Description: "Create a desktop shortcut"; \
  GroupDescription: "Additional options:"; \
  Flags: unchecked

[Files]
; The published self-contained exe — build with:
;   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Register autostart scheduled task if the user ticked the checkbox.
; /RL HIGHEST runs elevated at logon (required — app needs admin for hardware sensors).
Filename: "schtasks.exe"; \
  Parameters: "/Create /F /TN ""{#MyAppTaskName}"" /TR """"""{app}\{#MyAppExeName}"""""" /SC ONLOGON /RU ""{username}"" /RL HIGHEST /DELAY 0000:10"; \
  Flags: runhidden; \
  Tasks: autostart

; Offer to launch the app after install.
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the scheduled task on uninstall.
Filename: "schtasks.exe"; \
  Parameters: "/Delete /F /TN ""{#MyAppTaskName}"""; \
  Flags: runhidden; \
  RunOnceId: "RemoveTask"

[Code]
// On upgrade: if a scheduled task already exists, preserve autostart across the upgrade.
// The new exe path is the same (same install dir), so the existing task stays valid.
// No action needed — schtasks /Create /F will overwrite if the user re-checks the box.
