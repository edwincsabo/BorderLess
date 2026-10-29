namespace BorderLess.App;

// Notifications can arrive on network threads. Hidden windows retain a dirty
// bit; they do not enqueue status redraws. Safety/control maintenance is separate.
internal sealed class WindowRefreshGate
{
    private int visible, dirty = 1, scheduled;
    public void SetVisible(bool value) { Volatile.Write(ref visible, value ? 1 : 0); if (value) MarkDirty(); }
    public bool Visible => Volatile.Read(ref visible) == 1;
    public void MarkDirty() => Interlocked.Exchange(ref dirty, 1);
    public bool TrySchedule() => Visible && Interlocked.CompareExchange(ref scheduled, 1, 0) == 0;
    public bool Consume() { Interlocked.Exchange(ref scheduled, 0); return Visible && Interlocked.Exchange(ref dirty, 0) != 0; }
}
