using System.ComponentModel;
using BorderLess.App;
using BorderLess.Core;

internal static class InputFailureTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var high = InputFailureInspector.Format("TORRE", 0, 8192, 12288, "GCC");
        check(high.Contains("GCC") && high.Contains("administrador") && high.Contains("TORRE"), "Diagnóstico identifica app elevada y PC donde revisar permisos");
        check(!InputFailureInspector.Format("PC", 5, 12288, 12288, "App").Contains("tiene permisos superiores"), "Integridad equivalente no se presenta falsamente como bloqueo por elevación");
        check(!InputFailureInspector.Format("PC", 5, null, null, null).Contains("tiene permisos superiores"), "Permisos no verificables producen un mensaje general");
        var safe = InputFailureInspector.Format("PC\n", 5, 8192, 12288, new string('X', 1000) + "\n");
        check(safe.Length <= 480 && !safe.Any(char.IsControl), "Motivo enviado es breve y no contiene controles ni rutas completas");
        var native = InputFailureInspector.Describe("PC de ensayo", 5);
        check(native.Length <= 480 && native.Contains("PC de ensayo") && !native.Any(char.IsControl), "Consulta nativa de permisos es de solo lectura y devuelve un diagnóstico acotado");
        var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
        using var ac = NodeIdentity.CreateCertificate(aId); using var bc = NodeIdentity.CreateCertificate(bId);
        await using var a = new NetworkHub(new(aId, "ORIGEN", ac)); await using var b = new NetworkHub(new(bId, "TORRE", bc));
        var ad = new FakeDesktop(); var bd = new FailureDesktop();
        var ar = new InputRouter(a, new AppSettings { AutoEdges = false }, ad); var br = new InputRouter(b, new AppSettings { AutoEdges = false }, bd);
        a.Start(0); b.Start(0); await a.PairAsync(b.CreateInvitation() with { Host = "127.0.0.1" });
        async Task Until(Func<bool> condition, string text) { var end = DateTime.UtcNow.AddSeconds(6); while (!condition() && DateTime.UtcNow < end) await Task.Delay(20); check(condition(), text); }
        bd.FailNext = true; ar.Request(a.Inputs[bId]);
        await Until(() => ar.Status.Contains("GCC") && br.Status.Contains("GCC"), "Fallo al conceder control informa del motivo en ambos PCs");
        check(ar.ActivePeer == null && br.ControlledBy == null && !ar.Paused && !br.Paused, "Fallo de Windows libera control sin convertirlo en pausa");
        ar.Request(a.Inputs[bId]); await Until(() => ar.ActivePeer == bId && br.ControlledBy == aId, "Se puede solicitar control de nuevo sin volver a vincular");
        ar.CaptureKey(new Native.KeyHook { Key = 65, Scan = 30 }, false); ar.CaptureMouse(0x201, new Native.MouseHook());
        await Until(() => !bd.Inner.HeldKeys.IsEmpty && !bd.Inner.HeldButtons.IsEmpty, "Fallo intermedio se prueba con tecla y botón retenidos");
        bd.FailNext = true; ar.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(970, 4) });
        await Until(() => ar.ActivePeer == null && br.ControlledBy == null && ar.Status.Contains("GCC") && br.Status.Contains("GCC"), "Fallo durante movimiento comunica la misma explicación al controlador");
        check(bd.Inner.HeldKeys.IsEmpty && bd.Inner.HeldButtons.IsEmpty && bd.Inspections == 2, "Se libera entrada retenida y se consulta el diagnóstico solo al fallar");
    }
    private sealed class FailureDesktop : IInputDesktop
    {
        public FakeDesktop Inner { get; } = new();
        public bool FailNext;
        public int Inspections;
        public (int Left, int Top, int Width, int Height) Bounds => Inner.Bounds;
        public Native.Point Position => Inner.Position;
        public Native.Point ParkingPosition => Inner.ParkingPosition;
        public int LockState => Inner.LockState;
        public Native.Point ConstrainPosition(int x, int y) => Inner.ConstrainPosition(x, y);
        public void SetPosition(int x, int y) => Inner.SetPosition(x, y);
        public bool IsKeyDown(int key) => Inner.IsKeyDown(key);
        private void Fail() { if (FailNext) { FailNext = false; throw new Win32Exception(5); } }
        public void Inject(params Native.Input[] inputs) { Fail(); Inner.Inject(inputs); }
        public void InjectOne(Native.Input input) { Fail(); Inner.InjectOne(input); }
        public string DescribeInjectionFailure(string pc, int code) { Inspections++; return InputFailureInspector.Format(pc, code, 8192, 12288, "GCC"); }
    }
}
