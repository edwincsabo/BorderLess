using System.Windows.Media;

namespace BorderLess.App;

// Run at the display's WPF composition cadence, only while a fallback pointer
// actually needs drawing. It never schedules work in the input hook.
internal sealed class DisplayFramePump(Action draw) : IDisposable
{
    private bool running;
    private TimeSpan lastFrame = TimeSpan.MinValue;
    public void Start()
    {
        if (running) return;
        running = true; lastFrame = TimeSpan.MinValue;
        CompositionTarget.Rendering += Render;
    }
    public void Stop()
    {
        if (!running) return;
        running = false; CompositionTarget.Rendering -= Render;
    }
    private void Render(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == lastFrame) return;
            lastFrame = frame.RenderingTime;
        }
        draw();
    }
    public void Dispose() => Stop();
}
