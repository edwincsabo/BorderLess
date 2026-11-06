using System.Diagnostics;

namespace BorderLess.App;

internal struct EdgeHold
{
    private string? side;
    private long since;
    public void Reset() => side = null;
    public bool Ready(string next, long now, bool fast)
    {
        if (next.Length == 0) { Reset(); return false; }
        if (side != next) { side = next; since = now; return false; }
        return Stopwatch.GetElapsedTime(since, now).TotalMilliseconds >= (fast ? 80 : 160);
    }
}
