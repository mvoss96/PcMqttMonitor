#define MyAppName "PC MQTT Monitor"
#define MyAppVersion "2.12.0"
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
; Pick the language from Windows; ask only when no translation matches.
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german";  MessagesFile: "compiler:Languages\German.isl"

[Tasks]
; Default-checked: autostart at logon is the expected setup for a monitoring app.
; The task itself is created in [Code] (CreateAutostartTask). On upgrades the
; previous selection is kept, so existing tasks get re-registered.
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
; Fresh install: offer to launch the app. Upgrade: relaunch it automatically.
; runascurrentuser: postinstall entries default to the unelevated original user,
; whose plain CreateProcess cannot start a requireAdministrator exe (error 740).
; The installer is already elevated, so launch from its context instead.
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent runascurrentuser; \
  Check: not IsUpgrade
Filename: "{app}\{#MyAppExeName}"; \
  Flags: nowait skipifsilent runascurrentuser; \
  Check: IsUpgrade

[UninstallRun]
; Remove the scheduled task on uninstall.
Filename: "schtasks.exe"; \
  Parameters: "/Delete /F /TN ""{#MyAppTaskName}"""; \
  Flags: runhidden; \
  RunOnceId: "RemoveTask"

[Code]
var
  Upgrade: Boolean;

// An upgrade when the exe already exists in the fixed install directory.
function InitializeSetup(): Boolean;
begin
  Upgrade := FileExists(ExpandConstant('{autopf}\PcMqttMonitor\{#MyAppExeName}'));
  Result := True;
end;

function IsUpgrade(): Boolean;
begin
  Result := Upgrade;
end;

// On upgrades the previous task selection is reused (Inno's UsePreviousTasks),
// so the selection and confirmation pages are skipped: UAC, progress, Finish.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := Upgrade and ((PageID = wpSelectDir) or (PageID = wpSelectProgramGroup)
    or (PageID = wpSelectTasks) or (PageID = wpReady));
end;

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

// Registers the autostart scheduled task by running the installed app with
// --register-autostart, so the task definition lives only in AutoStart.cs.
// The installer is elevated, so the requireAdministrator exe starts directly.
procedure CreateAutostartTask();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--register-autostart', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('--register-autostart exit code: ' + IntToStr(ResultCode));
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
