using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

// Autostart via Task Scheduler. The app requires elevation (requireAdministrator
// manifest), so the HKCU\Run registry key does not work — Windows silently skips
// elevated apps at logon. A scheduled task with the highest run level is the
// correct mechanism; the installer registers it through `--register-autostart`.
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

    // Takes 1-2 s (schtasks.exe) — call off the UI thread. Returns false when
    // schtasks reports failure so the UI can revert the toggle instead of
    // showing an autostart state that was never registered.
    public static bool Apply(bool enable)
    {
        string? xmlPath = null;
        string args;
        if (enable)
        {
            var exePath = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
            // schtasks reads the task XML as UTF-16 (Encoding.Unicode writes the BOM).
            xmlPath = Path.Combine(Path.GetTempPath(), $"{TaskName}-task.xml");
            File.WriteAllText(xmlPath, TaskXml(exePath, WindowsIdentity.GetCurrent().Name), Encoding.Unicode);
            args = $"/Create /F /TN \"{TaskName}\" /XML \"{xmlPath}\"";
        }
        else
        {
            args = $"/Delete /F /TN \"{TaskName}\"";
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName        = "schtasks.exe",
                Arguments       = args,
                CreateNoWindow  = true,
                UseShellExecute = false,
            });
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        finally
        {
            if (xmlPath != null)
                try { File.Delete(xmlPath); } catch { }
        }
    }

    // Logon trigger for this user, 10 s delay, highest run level. Set explicitly
    // because the schtasks command-line defaults are wrong for a long-running
    // tray app: no start on battery, stop when unplugged, kill after 72 hours.
    static string TaskXml(string exePath, string user) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(user)}</UserId>
              <Delay>PT10S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exePath)}</Command>
            </Exec>
          </Actions>
        </Task>
        """;
}
