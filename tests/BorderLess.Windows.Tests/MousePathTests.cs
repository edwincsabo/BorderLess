using System.Diagnostics;
using BorderLess.App;
using BorderLess.Core;

internal static class MousePathTests
{
    public static void Run(Action<bool, string> check)
    {
        DisplayMonitor[] layout = [new("left", -1600, -200, 1600, 900, false), new("right", 0, 100, 1920, 1080, true)];
        check(WindowsInputDesktop.ContainsPixel(layout, -1600, -200) && WindowsInputDesktop.ContainsPixel(layout, -1, 699)
            && WindowsInputDesktop.ContainsPixel(layout, 0, 100), "Los bordes y orígenes negativos pertenecen al monitor correcto");
        check(!WindowsInputDesktop.ContainsPixel(layout, 0, 99) && !WindowsInputDesktop.ContainsPixel(layout, -1, 700)
            && !WindowsInputDesktop.ContainsPixel(layout, 1920, 500), "Huecos y bordes exclusivos se delegan a Windows");
        var desktop = new WindowsInputDesktop();
        var monitors = desktop.RefreshGeometry("Test").Monitors;
        foreach (var m in monitors)
        {
            foreach (var p in new[] { new Native.Point(m.X, m.Y), new Native.Point(m.X + m.Width - 1, m.Y + m.Height - 1), new Native.Point(m.X - 1, m.Y - 1) })
            {
                var expected = Native.Constrain(p);
                var fast = desktop.ConstrainPosition(p.X, p.Y);
                if (fast.X != expected.X || fast.Y != expected.Y) throw new Exception("Monitor mapping changed");
            }
        }
        desktop.InvalidateGeometry();
        check(desktop.Bounds == Native.Bounds && desktop.RefreshGeometry("Test").Monitors.Length > 0, "Invalidar vuelve a Windows y reconstruye la geometría de las pantallas activas");

        var epoch = Guid.NewGuid();
        var queue = new InputOutbox();
        var expectedEvents = new List<InputEvent>();
        long sequence = 0;
        foreach (var action in new[] { InputKind.ButtonDown, InputKind.KeyDown, InputKind.KeyUp, InputKind.Wheel, InputKind.ButtonUp })
        {
            InputEvent move = default;
            for (int n = 0; n < 128; n++)
            {
                move = new(InputKind.Move, ++sequence, epoch, sequence, -sequence);
                queue.EnqueueInput(move);
            }
            expectedEvents.Add(move);
            var barrier = new InputEvent(action, ++sequence, epoch, move.X, move.Y, 1, 120);
            queue.Enqueue(Frame.FromInput(barrier));
            expectedEvents.Add(barrier);
        }
        var enqueuedBy = Stopwatch.GetTimestamp();
        foreach (var expected in expectedEvents)
        {
            if (!queue.TryTake(out var actual) || actual.Created > enqueuedBy || actual.Created <= 0 ||
                !actual.Payload.SequenceEqual(Frame.FromInput(expected).Payload)) throw new Exception("Input order, age or bytes changed");
        }
        check(queue.Count == 0 && queue.Coalesced == 5 * 127, "Ráfagas con clic, tecla y rueda conservan bytes, edad y orden en la cola");
    }
}
