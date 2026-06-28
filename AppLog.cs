// Lightweight file logger — writes app.log next to the exe.
// Rotates to app.log.bak when the file exceeds 512 KB so it never grows unbounded.
static class AppLog
{
    static readonly string LogPath    = Path.Combine(AppContext.BaseDirectory, "app.log");
    static readonly string BackupPath = Path.Combine(AppContext.BaseDirectory, "app.log.bak");
    static readonly object _lock = new();
    const long MaxBytes = 512 * 1024;

    public static void Write(string message)
    {
        try
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    File.Move(LogPath, BackupPath, overwrite: true);
                File.AppendAllText(LogPath, entry);
            }
        }
        catch { /* never let logging crash the app */ }
    }
}
