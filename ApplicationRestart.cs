using System.Runtime.InteropServices;

static class ApplicationRestart
{
    const int RestartNoHang = 0x2;
    const int RestartNoPatch = 0x4;
    const int RestartNoReboot = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterApplicationRestart(string? commandLine, int flags);

    public static void RegisterForCrashes()
    {
        int result = RegisterApplicationRestart(
            null, RestartNoHang | RestartNoPatch | RestartNoReboot);
        if (result != 0)
            AppLog.Write($"[error] Could not register Windows crash restart: 0x{result:X8}");
    }
}
