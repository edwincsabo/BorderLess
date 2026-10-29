using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BorderLess.Windows.Tests")]
namespace BorderLess.App;

internal interface IInputDesktop
{
    (int Left, int Top, int Width, int Height) Bounds { get; }
    Native.Point Position { get; }
    Native.Point ParkingPosition { get; }
    int LockState { get; }
    Native.Point ConstrainPosition(int x, int y);
    void SetPosition(int x, int y);
    bool IsKeyDown(int key);
    void Inject(params Native.Input[] inputs);
    void InjectOne(Native.Input input);
    string DescribeInjectionFailure(string pc, int code) => InputFailureInspector.Format(pc, code, null, null, null);
}
internal sealed class WindowsInputDesktop : IInputDesktop
{
    public (int Left, int Top, int Width, int Height) Bounds => Native.Bounds;
    public Native.Point Position { get { Native.GetPhysicalCursorPos(out var point); return point; } }
    public Native.Point ParkingPosition { get { var m = Native.MonitorAt(Position); return new Native.Point(m.Left + (m.Right - m.Left) / 2, m.Top + (m.Bottom - m.Top) / 2); } }
    public int LockState => ((Native.GetKeyState(0x14) & 1) != 0 ? 1 : 0) | ((Native.GetKeyState(0x90) & 1) != 0 ? 2 : 0) | ((Native.GetKeyState(0x91) & 1) != 0 ? 4 : 0);
    public Native.Point ConstrainPosition(int x, int y) => Native.Constrain(new Native.Point(x, y));
    public void SetPosition(int x, int y) { var p = ConstrainPosition(x, y); Native.InjectOne(Native.MoveTo(p.X, p.Y)); }
    public bool IsKeyDown(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;
    public void Inject(params Native.Input[] inputs) => Native.Inject(inputs);
    public void InjectOne(Native.Input input) => Native.InjectOne(input);
    public string DescribeInjectionFailure(string pc, int code) => InputFailureInspector.Describe(pc, code);
}
