using System.IO.MemoryMappedFiles;
using System.Diagnostics;

namespace BX.DesktopInteraction;

// Session-local coordination. The mutex is held only for synchronous state/input work,
// never across an await. Keeping the mapping open preserves a stop across clients.
internal sealed class SharedControl : IDisposable
{
    private readonly Mutex mutex;
    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly long token = Random.Shared.NextInt64(1, long.MaxValue);
    private long lease;
    private bool disposed;
    internal SharedControl(string name = @"Local\BXDesktopInteraction.Control.v1")
    {
        mutex = new Mutex(false, name + ".Lock");
        using (Enter())
        {
            map = MemoryMappedFile.CreateOrOpen(name, 64);
            view = map.CreateViewAccessor();
        }
        Claim();
    }
    private IDisposable Enter()
    {
        try { if (!mutex.WaitOne(5000)) throw new TimeoutException("Desktop ownership lock timed out."); }
        catch (AbandonedMutexException) { /* The mutex is acquired; validate state below. */ }
        return new Guard(mutex);
    }
    private sealed class Guard(Mutex mutex) : IDisposable { public void Dispose() => mutex.ReleaseMutex(); }
    private bool Owns => view.ReadInt64(0) == token && view.ReadInt64(16) == lease;
    internal void Claim()
    {
        using var guard = Enter();
        // A request is allowed to take the baton, but never clears an emergency stop.
        if (view.ReadInt64(0) != token)
        {
            ReleaseTrackedButtons();
            view.Write(16, view.ReadInt64(16) + 1);
            view.Write(0, token);
            view.Write(8, Environment.ProcessId);
        }
        lease = view.ReadInt64(16);
    }
    internal object Status()
    {
        using var guard = Enter();
        bool stopped = view.ReadInt32(24) != 0;
        bool ready = HotkeyReady();
        return new { instancePid = Environment.ProcessId, activePid = view.ReadInt32(8), active = Owns,
            paused = !Owns, generation = view.ReadInt64(16), stopped, hotkeyReady = ready,
            stopReason = stopped ? "emergency_stop" : !ready ? "hotkey_unavailable" : null };
    }
    internal bool Active { get { using var guard = Enter(); return Owns; } }
    internal bool Stopped { get { using var guard = Enter(); return view.ReadInt32(24) != 0; } }
    private bool HotkeyReady()
    {
        int pid = view.ReadInt32(28);
        if (pid == 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }
    internal void HotkeyRegistered()
    {
        using var guard = Enter();
        view.Write(28, Environment.ProcessId);
        view.Write(32, token);
    }
    internal void HotkeyReleased()
    {
        using var guard = Enter();
        if (view.ReadInt64(32) == token) { view.Write(28, 0); view.Write(32, 0L); }
    }
    internal void Stop()
    {
        using var guard = Enter();
        view.Write(24, 1);
        ReleaseTrackedButtons();
    }
    internal void Execute(Action action)
    {
        using var guard = Enter();
        if (!Owns) throw new OperationCanceledException("Interaction paused: another BX Desktop Interaction instance took control. A new interaction request takes the baton back.");
        if (view.ReadInt32(24) != 0) throw new InvalidOperationException("Emergency stop is latched for all BX Desktop Interaction instances. Close all instances before restarting; taking the baton does not reset the stop.");
        if (!HotkeyReady()) throw new InvalidOperationException("Emergency hotkey Ctrl+Alt+F12 is unavailable. It may be held by another application or an older incompatible BX Desktop Interaction instance. This is not a latched emergency stop.");
        action();
    }
    internal void SetButton(int mask, bool down) => view.Write(40, down ? view.ReadInt32(40) | mask : view.ReadInt32(40) & ~mask);
    internal void ReleaseButton(int mask, Action action)
    {
        using var guard = Enter();
        // A superseded operation's finally block must never release the new owner's button.
        if (!Owns || (view.ReadInt32(40) & mask) == 0) return;
        action(); SetButton(mask, false);
    }
    private void ReleaseTrackedButtons()
    {
        int held = view.ReadInt32(40);
        foreach (var (mask, flag, data) in new[] { (1, 4u, 0u), (2, 16u, 0u), (4, 64u, 0u), (8, 256u, 1u), (16, 256u, 2u) })
            if ((held & mask) != 0) Native.Send(flag, data: data);
        view.Write(40, 0);
    }
    internal void ReleaseOwnedButtons()
    {
        if (disposed) return;
        using var guard = Enter();
        if (Owns) ReleaseTrackedButtons();
    }
    public void Dispose()
    {
        if (disposed) return;
        using (Enter())
        {
            if (Owns) { ReleaseTrackedButtons(); view.Write(0, 0L); view.Write(8, 0); }
            if (view.ReadInt64(32) == token) { view.Write(28, 0); view.Write(32, 0L); }
        }
        disposed = true;
        view.Dispose(); map.Dispose(); mutex.Dispose();
    }
}
