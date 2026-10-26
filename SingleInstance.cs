using System.Security.Cryptography;
using System.Text;

// One running instance per installation: a named mutex keyed by the exe path,
// so an installed release and a dev build can run side by side. A second
// start signals the running instance (named event) to show its window.
sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly EventWaitHandle _show;
    RegisteredWaitHandle? _wait;
    bool _owned;

    SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
        _owned = true;
    }

    // Returns null when another instance owns the lock (it was asked to show
    // its window). waitForPrevious: wait up to 15 s for a restarting
    // instance to exit instead of giving up at once.
    public static SingleInstance? Acquire(bool waitForPrevious)
    {
        string id = "PcMqttMonitor-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Environment.ProcessPath!.ToLowerInvariant())))[..16];
        var mutex = new Mutex(false, id);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, id + "-show");

        bool owned;
        try { owned = mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { owned = true; }   // previous instance crashed

        if (owned)
            return new SingleInstance(mutex, show);

        show.Set();
        mutex.Dispose();
        show.Dispose();
        return null;
    }

    // Runs action (on a thread-pool thread) whenever a second start asks.
    public void OnShowRequested(Action action) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => action(), null, Timeout.Infinite, false);

    // Must run on the thread that called Acquire (mutexes are thread-owned).
    public void Release()
    {
        if (!_owned) return;
        _owned = false;
        _mutex.ReleaseMutex();
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        Release();
        _mutex.Dispose();
        _show.Dispose();
    }
}
