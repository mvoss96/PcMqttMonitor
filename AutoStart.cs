using System.Diagnostics;

// Autostart via Task Scheduler. The app requires elevation (requireAdministrator
// manifest), so the HKCU\Run registry key does not work — Windows silently skips
// elevated apps at logon. A scheduled task with /RL HIGHEST is the correct
// mechanism; the installer registers the same task (installer.iss).
//
// NOTE: MainWindow.cs still carries its own copy of this logic — it is replaced
// wholesale by the v2 UI, which must call this class instead.
static class AutoStart
{
    const string TaskName = "PcMqttMonitor";

    public static bool IsEnabled()
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName               = "schtasks.exe",
            Arguments              = $"/Query /TN \"{TaskName}\"",
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        });
        proc?.WaitForExit();
        return proc?.ExitCode == 0;
    }

    // Takes 1-2 s (schtasks.exe) — call off the UI thread.
    public static void Apply(bool enable)
    {
        string args;
        if (enable)
        {
            var exePath = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
            // /SC ONLOGON  — trigger: current user logs on
            // /RL HIGHEST  — run with highest available privileges (elevation)
            // /DELAY       — small delay so the desktop and network are ready
            // /F           — overwrite if the task already exists
            args = $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" " +
                   $"/SC ONLOGON /RU \"{Environment.UserName}\" /RL HIGHEST /DELAY 0000:10";
        }
        else
        {
            args = $"/Delete /F /TN \"{TaskName}\"";
        }

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName        = "schtasks.exe",
            Arguments       = args,
            CreateNoWindow  = true,
            UseShellExecute = false,
        });
        proc?.WaitForExit();
    }
}
