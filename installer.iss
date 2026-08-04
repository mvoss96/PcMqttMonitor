#define MyAppName "PC MQTT Monitor"
#define MyAppVersion "1.3.2"
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
; Program Files: the app always runs elevated (requireAdministrator), so it can
; write config.json/logs next to its exe even there — and unlike LocalAppData,
; non-admin processes can't swap out an exe that will run with admin rights.
DefaultDirName={autopf}\PcMqttMonitor
; The exe is published win-x64 only. Without the 64-bit-mode directive Inno runs
; in 32-bit install mode and {autopf} resolves to "Program Files (x86)".
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Always install to the directory above, even on upgrades — without this, Inno
; reuses the previous install location from the registry (e.g. the old
; LocalAppData path) and the move to Program Files would silently not happen.
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
; Output
OutputDir=installer-output
OutputBaseFilename=PcMqttMonitorSetup-{#MyAppVersion}
SetupIconFile=app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Run the installer elevated (UAC prompt). The app itself runs elevated
; (requireAdministrator manifest), so an unelevated installer could neither
; close it (Restart Manager can't touch elevated processes) nor replace its
; locked exe, nor register the /RL HIGHEST scheduled task.
PrivilegesRequired=admin
; The running app is closed via taskkill in PrepareToInstall (see [Code]) —
; Restart Manager can't gracefully close a hidden tray app.
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Default-checked: autostart at logon is the expected setup for a monitoring app.
; The task itself is created in [Code] (CreateAutostartTask) — a plain [Run]
; schtasks entry silently fails on the /TR quoting for paths with spaces.
Name: "autostart"; \
  Description: "Start {#MyAppName} automatically when Windows starts"; \
  GroupDescription: "Additional options:"
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
; Offer to launch the app after install.
; runascurrentuser: postinstall entries default to the unelevated original user,
; whose plain CreateProcess cannot start a requireAdministrator exe (error 740).
; The installer is already elevated, so launch from its context instead.
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent runascurrentuser

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

// Terminate the running app so its exe can be replaced. The app is a tray app
// without a visible window, so a graceful close request would go nowhere; it has
// no unsaved state, so /F is safe. Installer runs elevated, so this works even
// though the app itself is elevated.
procedure KillRunningApp();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM "{#MyAppExeName}"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  Sleep(500); // give Windows a moment to release the file lock
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningApp();
  Result := '';
end;

// Registers the autostart scheduled task. Built in Pascal so the /TR argument
// gets the exact quoting the exe path needs: /TR "\"C:\...\PcMqttMonitor.exe\""
// — the same pattern the app's own ApplyAutoStart (MainWindow.cs) uses, which
// is proven to work. A [Run] entry can't express this reliably ("" escaping).
procedure CreateAutostartTask();
var
  Params: String;
  ResultCode: Integer;
begin
  Params := '/Create /F /TN "{#MyAppTaskName}"'
    + ' /TR "\"' + ExpandConstant('{app}') + '\{#MyAppExeName}\""'
    + ' /SC ONLOGON /RU "' + GetUserNameString() + '" /RL HIGHEST /DELAY 0000:10';
  Exec('schtasks.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('schtasks /Create exit code: ' + IntToStr(ResultCode));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('autostart') then
    CreateAutostartTask();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    KillRunningApp();
end;
