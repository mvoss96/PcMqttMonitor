using System.Globalization;

// Localization holder: L.T is the active string table, L.Culture the culture
// used for UI number formatting (transport/MQTT stays invariant). Resolved
// once at startup from config; changing the language restarts the app.
static class L
{
    public static Lang T = Lang.En;
    public static CultureInfo Culture = CultureInfo.InvariantCulture;

    public static void Init(string? configured)
    {
        bool de = configured?.ToLowerInvariant() switch
        {
            "de" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de",   // "system"
        };
        T = de ? Lang.De : Lang.En;
        Culture = de ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture;
    }
}

// One property per UI string. English IS the default value; a language sets
// only what differs — anything not set falls back to English automatically.
// Log lines (app.log) and everything on MQTT deliberately stay English.
sealed class Lang
{
    // ── navigation / window ─────────────────────────────────────────────────
    public string NavDashboard { get; init; } = "Dashboard";
    public string NavOutputs   { get; init; } = "Outputs";
    public string NavSensors   { get; init; } = "Sensors";
    public string NavSettings  { get; init; } = "Settings";
    public string NavAbout     { get; init; } = "About";

    public string UnsavedChanges { get; init; } = "Unsaved changes";
    public string Saved          { get; init; } = "✓ Saved";
    public string Save           { get; init; } = "Save";
    public string TitleUpdate    { get; init; } = "v{0} available";   // title-bar suffix

    public string PausedBanner { get; init; } = "Publishing is paused";
    public string Resume       { get; init; } = "Resume";

    // ── tray ────────────────────────────────────────────────────────────────
    public string TrayPause        { get; init; } = "Pause Publishing";
    public string TrayResume       { get; init; } = "Resume Publishing";
    public string TrayExit         { get; init; } = "Exit";
    public string TrayPausedSuffix { get; init; } = "(Paused)";
    public string TrayUpdateItem   { get; init; } = "Update available: v{0}";
    public string BalloonUpdate    { get; init; } = "Version {0} is available — click to open the download page.";

    // ── status line (tray menu + dashboard placeholder) ─────────────────────
    public string StatusStarting       { get; init; } = "Starting…";
    public string StatusWaitingDrivers { get; init; } = "Waiting for system drivers ({0}s)…";
    public string StatusOpeningSensors { get; init; } = "Opening sensors…";
    public string StatusNoOutputs      { get; init; } = "No outputs configured — sensors only";
    public string StatusPublishingTo   { get; init; } = "Publishing to {0}";
    public string StatusPaused         { get; init; } = "Paused";
    public string StatusConnectingTo   { get; init; } = "Connecting to {0}…";
    public string StatusDisconnected   { get; init; } = "Disconnected — open Settings to configure MQTT broker";
    public string StatusConnected      { get; init; } = "Connected — {0}";
    public string StatusError          { get; init; } = "Error: {0}";

    // ── dashboard ───────────────────────────────────────────────────────────
    public string CardCpu     { get; init; } = "CPU";
    public string CardGpu     { get; init; } = "GPU";
    public string CardRam     { get; init; } = "RAM";
    public string CardDrives  { get; init; } = "Drives";
    public string CardNetwork { get; init; } = "Network";
    public string CardSystem  { get; init; } = "System";
    public string RowUptime   { get; init; } = "Uptime";
    public string RowHost     { get; init; } = "Host";
    public string RowOs       { get; init; } = "OS";
    public string RowBoard    { get; init; } = "Board";

    // ── outputs page ────────────────────────────────────────────────────────
    public string OutputsTitle    { get; init; } = "Outputs";
    public string FieldHost       { get; init; } = "Host";
    public string FieldPort       { get; init; } = "Port";
    public string FieldUsername   { get; init; } = "Username";
    public string FieldPassword   { get; init; } = "Password";
    public string FieldTopicRoot  { get; init; } = "Topic root";
    public string FieldUseTls     { get; init; } = "Use TLS (typically port 8883)";
    public string FieldDiscovery  { get; init; } = "Home Assistant MQTT Discovery";
    public string FieldListenPort { get; init; } = "Listen port";
    public string FieldBaudRate   { get; init; } = "Baud rate";
    public string OutDisabled     { get; init; } = "Disabled";
    public string OutNoHost       { get; init; } = "Not configured — set a host";
    public string OutNoComPort    { get; init; } = "Not configured — set a port (e.g. COM3)";
    public string OutNotConnected { get; init; } = "Not connected";
    public string OutSendingTo    { get; init; } = "Sending to {0}:{1}";
    public string OutListeningOn  { get; init; } = "Listening on {0}";
    public string OutSendingCom   { get; init; } = "Sending on {0} @ {1} baud";

    // ── sensors page ────────────────────────────────────────────────────────
    public string SensorsTitle     { get; init; } = "Sensors to publish";
    public string GroupOther       { get; init; } = "Other";
    public string SensorLoad       { get; init; } = "Load";
    public string SensorTemp       { get; init; } = "Temperature";
    public string SensorPkgPower   { get; init; } = "Package Power";
    public string SensorCoreVolt   { get; init; } = "Core Voltage";
    public string SensorBoardPower { get; init; } = "Board Power";
    public string SensorFanSpeed   { get; init; } = "Fan Speed";
    public string SensorMemLoad    { get; init; } = "VRAM Load";
    public string SensorMemUsed    { get; init; } = "VRAM Used";
    public string SensorMemTotal   { get; init; } = "VRAM Total";
    public string SensorUsed       { get; init; } = "Used";
    public string SensorTotal      { get; init; } = "Total";
    public string SensorUpload     { get; init; } = "Upload";
    public string SensorDownload   { get; init; } = "Download";
    public string SensorBoard      { get; init; } = "Motherboard";
    public string SensorDrives     { get; init; } = "Drives";
    public string SensorUptime     { get; init; } = "Uptime";

    // ── settings page ───────────────────────────────────────────────────────
    public string SettingsTitle    { get; init; } = "Settings";
    public string SetInterval      { get; init; } = "Publish interval";
    public string SetIntervalSub   { get; init; } = "Seconds between two measurements";
    public string SetTheme         { get; init; } = "Theme";
    public string SetRestartSub    { get; init; } = "Changing restarts the app";
    public string SetLanguage      { get; init; } = "Language";
    public string SetAutostart     { get; init; } = "Start with Windows";
    public string SetAutostartSub  { get; init; } = "Scheduled task with highest privileges";
    public string SetDebug         { get; init; } = "Debug logging";
    public string SetDebugSub      { get; init; } = "Verbose entries in app.log";
    public string SetUpdates       { get; init; } = "Notify about new versions";
    public string SetUpdatesSub    { get; init; } = "Checks the GitHub releases daily";
    public string ThemeSystem      { get; init; } = "System";
    public string ThemeLight       { get; init; } = "Light";
    public string ThemeDark        { get; init; } = "Dark";
    public string LangSystem       { get; init; } = "System";

    // ── about page ──────────────────────────────────────────────────────────
    public string AboutVersion     { get; init; } = "Version {0}";
    public string AboutChecking    { get; init; } = "Checking for updates…";
    public string AboutUpdateFound { get; init; } = "Update {0} available";
    public string AboutUpToDate    { get; init; } = "✓ Up to date";
    public string AboutCheckFailed { get; init; } = "Check failed: {0}";
    public string AboutDownload    { get; init; } = "Download on GitHub";
    public string AboutDeps        { get; init; } = "Uses LibreHardwareMonitor (MPL-2.0) and MQTTnet (MIT)";

    // ── crash dialogs ───────────────────────────────────────────────────────
    public string CrashTitle       { get; init; } = "PC MQTT Monitor — Error";
    public string CrashTitleFatal  { get; init; } = "PC MQTT Monitor — Critical Error";
    public string CrashDetails     { get; init; } = "Details saved to:";

    public static readonly Lang En = new();

    public static readonly Lang De = new()
    {
        // "Outputs" stays as the (common) loanword — every German attempt
        // ("Ausgänge", "Ausgaben") reads odd for data sinks.
        NavSensors   = "Sensoren",
        NavSettings  = "Einstellungen",
        NavAbout     = "Über",

        UnsavedChanges = "Ungespeicherte Änderungen",
        Saved          = "✓ Gespeichert",
        Save           = "Speichern",
        TitleUpdate    = "v{0} verfügbar",

        PausedBanner = "Übertragung pausiert",
        Resume       = "Fortsetzen",

        TrayPause        = "Pausieren",
        TrayResume       = "Fortsetzen",
        TrayExit         = "Beenden",
        TrayPausedSuffix = "(Pausiert)",
        TrayUpdateItem   = "Update verfügbar: v{0}",
        BalloonUpdate    = "Version {0} ist verfügbar — Klick öffnet die Download-Seite.",

        StatusStarting       = "Starte…",
        StatusWaitingDrivers = "Warte auf Systemtreiber ({0} s)…",
        StatusOpeningSensors = "Öffne Sensoren…",
        StatusNoOutputs      = "Keine Ausgänge konfiguriert — nur Sensoranzeige",
        StatusPublishingTo   = "Sendet an {0}",
        StatusPaused         = "Pausiert",
        StatusConnectingTo   = "Verbinde mit {0}…",
        StatusDisconnected   = "Getrennt — MQTT-Broker in den Ausgängen konfigurieren",
        StatusConnected      = "Verbunden — {0}",
        StatusError          = "Fehler: {0}",

        CardDrives  = "Laufwerke",
        CardNetwork = "Netzwerk",
        RowUptime   = "Laufzeit",
        RowBoard    = "Board",

        FieldUsername   = "Benutzername",
        FieldPassword   = "Passwort",
        FieldTopicRoot  = "Root-Topic",
        FieldUseTls     = "TLS verwenden (üblich: Port 8883)",
        FieldListenPort = "Port",
        FieldBaudRate   = "Baudrate",
        OutDisabled     = "Deaktiviert",
        OutNoHost       = "Nicht konfiguriert — Host angeben",
        OutNoComPort    = "Nicht konfiguriert — Port angeben (z. B. COM3)",
        OutNotConnected = "Nicht verbunden",
        OutSendingTo    = "Sendet an {0}:{1}",
        OutListeningOn  = "Lauscht auf Port {0}",
        OutSendingCom   = "Sendet auf {0} @ {1} Baud",

        SensorsTitle     = "Sensoren",
        GroupOther       = "Sonstiges",
        SensorLoad       = "Auslastung",
        SensorTemp       = "Temperatur",
        SensorPkgPower   = "Package-Leistung",
        SensorCoreVolt   = "Kernspannung",
        SensorBoardPower = "Board-Leistung",
        SensorFanSpeed   = "Lüfterdrehzahl",
        SensorMemLoad    = "VRAM-Auslastung",
        SensorMemUsed    = "VRAM belegt",
        SensorMemTotal   = "VRAM gesamt",
        SensorUsed       = "Belegt",
        SensorTotal      = "Gesamt",
        SensorBoard      = "Mainboard",
        SensorDrives     = "Laufwerke",
        SensorUptime     = "Laufzeit",

        SettingsTitle    = "Einstellungen",
        SetInterval      = "Sendeintervall",
        SetIntervalSub   = "Sekunden zwischen zwei Messungen",
        SetTheme         = "Design",
        SetRestartSub    = "Änderung startet die App neu",
        SetLanguage      = "Sprache",
        SetAutostart     = "Mit Windows starten",
        SetAutostartSub  = "Geplante Aufgabe mit höchsten Rechten",
        SetDebug         = "Debug-Protokoll",
        SetDebugSub      = "Ausführliche Einträge in app.log",
        SetUpdates       = "Über neue Versionen benachrichtigen",
        SetUpdatesSub    = "Prüft täglich die GitHub-Releases",
        ThemeLight       = "Hell",
        ThemeDark        = "Dunkel",

        AboutVersion     = "Version {0}",
        AboutChecking    = "Suche nach Updates…",
        AboutUpdateFound = "Update {0} verfügbar",
        AboutUpToDate    = "✓ Auf dem neuesten Stand",
        AboutCheckFailed = "Prüfung fehlgeschlagen: {0}",
        AboutDownload    = "Auf GitHub herunterladen",
        AboutDeps        = "Nutzt LibreHardwareMonitor (MPL-2.0) und MQTTnet (MIT)",

        CrashTitle      = "PC MQTT Monitor — Fehler",
        CrashTitleFatal = "PC MQTT Monitor — Kritischer Fehler",
        CrashDetails    = "Details gespeichert in:",
    };
}
