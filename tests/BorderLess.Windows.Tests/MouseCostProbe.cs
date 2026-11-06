using System.Diagnostics;
using BorderLess.App;
using BorderLess.Core;

internal static class MouseCostProbe
{
    public static void Run()
    {
        var desktop = new WindowsInputDesktop();
        desktop.RefreshGeometry("Benchmark");
        var origin = desktop.ParkingPosition;
        var epoch = Guid.NewGuid();
        for (int round = 0; round < 6; round++)
        {
            const int count = 20000;
            var ticks = new long[count];
            long checksum = 0;
            for (int i = 0; i < count; i++)
            {
                var start = Stopwatch.GetTimestamp();
                var bounds = desktop.Bounds;
                var p = desktop.ConstrainPosition(origin.X + i % 20, origin.Y);
                var move = Native.MoveTo(p.X, p.Y, bounds);
                ticks[i] = Stopwatch.GetTimestamp() - start;
                checksum += move.Data.Mouse.X;
            }
            Array.Sort(ticks);
            var batchStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                var b = desktop.Bounds;
                var p = desktop.ConstrainPosition(origin.X + i % 20, origin.Y);
                checksum += Native.MoveTo(p.X, p.Y, b).Data.Mouse.X;
            }
            var meanNs = Stopwatch.GetElapsedTime(batchStart).TotalNanoseconds / count;
            var queue = new InputOutbox();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1; i <= count; i++)
            {
                var input = new InputEvent(InputKind.Move, i, epoch, i, -i);
                queue.EnqueueInput(input);
                if (i % 100 == 0 && (!queue.TryTake(out var frame) || frame.ReadInput() != input))
                    throw new Exception("El movimiento acumulado cambió.");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round != 0) Console.WriteLine($"mean_ns={meanNs:F1} p50_us={ticks[count/2]*1e6/Stopwatch.Frequency:F3} p99_us={ticks[count*99/100]*1e6/Stopwatch.Frequency:F3} burst_bytes={allocated} checksum={checksum}");
        }
    }
}
