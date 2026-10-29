using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using BorderLess.App;
using BorderLess.Core;

if (args.Contains("--native-keyboard")) { NativeKeyboardProbe.Run(); return; }
if (args.Contains("--native-mouse")) { NativeMouseProbe.Run(); return; }
if (args.Contains("--display-cadence")) { DisplayFrameProbe.Run(); return; }
if (args.Contains("--ui-lifecycle")) { UiLifecycleProbe.Run(); return; }
if (args.Contains("--clipboard-pipeline")) { ClipboardPipelineProbe.Run(); return; }
if (args.Contains("--cursor-artwork")) { CursorArtworkProbe.Run(); return; }
if (args.Contains("--ui-preview-pin")) { UiLifecycleProbe.Run(true); return; }
if (args.Contains("--ui-preview-pause")) { UiLifecycleProbe.Run(previewPause: true); return; }
if (args.Contains("--ui-preview-improvements")) { UiLifecycleProbe.Run(previewImprovements: true); return; }
if (args.Contains("--ui-preview-language")) { UiLifecycleProbe.Run(previewLanguage: true); return; }
if (args.ElementAtOrDefault(0) == "--ui-fixture") { await UiFixture.RunAsync(args.ElementAtOrDefault(1) ?? "artifacts/ui-0.1.4"); return; }

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
async Task Wait(Func<bool> condition, string name)
{
    var until = DateTime.UtcNow.AddSeconds(5); while (!condition() && DateTime.UtcNow < until) await Task.Delay(20); Check(condition(), name);
}
var viewGate = new WindowRefreshGate();
viewGate.MarkDirty(); Check(!viewGate.TrySchedule() && !viewGate.Consume(), "Ventana oculta no programa ni consume redibujos");
viewGate.SetVisible(true); Check(viewGate.TrySchedule() && !viewGate.TrySchedule() && viewGate.Consume() && !viewGate.Consume(), "Mostrar ventana agrupa cambios y pinta una sola vez");
viewGate.SetVisible(false); viewGate.MarkDirty(); viewGate.SetVisible(true); Check(viewGate.TrySchedule() && viewGate.Consume(), "Restaurar ventana recupera los cambios recibidos estando minimizada");
var p1 = Guid.NewGuid(); var p2 = Guid.NewGuid();
var placement = PlacementRules.Assign(new Dictionary<Guid, string> { [p1] = "left", [p2] = "right" }, p1, "right");
PlacementRules.Validate(placement); Check(placement[p1] == "right" && placement[p2] == "left", "Mover un equipo a un borde ocupado intercambia las posiciones sin duplicados");
Check(Marshal.SizeOf<Native.Input>() == (Environment.Is64BitProcess ? 40 : 28), "Layout de INPUT coincide con ABI Win32");
Check(Native.Key(0xa0, 0x2a, false).Data.Key.Key == 0xa0 && (Native.Key(0xa0, 0x2a, false).Data.Key.Flags & 8) == 0 && (Native.Key(65, 30, false).Data.Key.Flags & 8) != 0, "Shift se inyecta como modificador explícito y letras conservan scan code");
Check(PointerVisibility.NeedsFallback(true, true, true, 0) && !PointerVisibility.NeedsFallback(true, true, true, 1) && !PointerVisibility.NeedsFallback(false, true, true, 0) && !PointerVisibility.NeedsFallback(true, false, true, 0), "Puntero auxiliar solo aparece al recibir control y faltar cursor visible");
Check((long)Native.NormalizePixel(4, 0, 1440) * 1440 / 65536 == 4, "El estacionamiento Y=4 se convierte al píxel 4 real, no al 3");
foreach (var extent in new[] { 1080, 1440, 1920, 2160, 2560, 3840, 7680 })
{
    var origin = -extent / 2;
    Check(Enumerable.Range(0, extent).All(offset => (long)Native.NormalizePixel(origin + offset, origin, extent) * extent / 65536 == offset), $"Centro de píxel sin sesgo en todos los puntos de eje {extent} con origen negativo");
}
var idA = Guid.NewGuid(); var idB = Guid.NewGuid();
using var certA = NodeIdentity.CreateCertificate(idA); using var certB = NodeIdentity.CreateCertificate(idB);
await using var a = new NetworkHub(new(idA, "Desktop A", certA));
await using var b = new NetworkHub(new(idB, "Desktop B", certB));
var desktopA = new FakeDesktop(); var desktopB = new FakeDesktop();
var configA = new AppSettings(); var configB = new AppSettings();
var routerA = new InputRouter(a, configA, desktopA); var routerB = new InputRouter(b, configB, desktopB);
a.Start(0); b.Start(0);
a.UpdateDesktop(new DesktopSnapshot(new[] { new DisplayMonitor("A", 0, 0, 1920, 1080, true) }));
b.UpdateDesktop(new DesktopSnapshot(new[] { new DisplayMonitor("B", 0, 0, 1920, 1080, true) }));
await a.PairAsync(b.CreateInvitation() with { Host = "127.0.0.1" });
routerA.Request(a.Inputs[idB], "left", .5);
await Wait(() => routerA.ActivePeer == idB && routerB.ControlledBy == idA, "Solicitud y concesión de control validan época en ambos extremos");
var initialX = desktopB.Position.X;
Check(routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(1060, 21) }), "Mouse remoto se suprime en origen sin esperar red");
await Wait(() => desktopB.Position.X > initialX + 90, "Movimiento acumulado llega al adaptador de inyección");
Check(desktopB.SingleInjections > 0, "Movimiento puro usa inyección de una estructura sin lote ni array de producción");
routerA.CaptureKey(new Native.KeyHook { Key = 0xa2, Scan = 29 }, false);
routerA.CaptureMouse(0x201, new Native.MouseHook());
await Wait(() => desktopB.HeldKeys.ContainsKey(29) && desktopB.HeldButtons.ContainsKey(1), "Tecla y botón se mantienen en el receptor durante arrastre");
a.Inputs[idB].Close("Prueba de fallo durante arrastre");
await Wait(() => routerA.ActivePeer == null && routerB.ControlledBy == null && desktopB.HeldKeys.IsEmpty && desktopB.HeldButtons.IsEmpty, "Desconexión libera tecla y botón y devuelve control local");
await a.ConnectAsync(idB); await Wait(() => a.Inputs.ContainsKey(idB), "Sesión de entrada recuperada");
routerB.Pause(true);
await Wait(() => a.Clipboards.IsEmpty && b.Clipboards.IsEmpty, "Pausar cierra el canal de datos y cancela transferencias en curso");
routerA.Request(a.Inputs[idB]);
await Wait(() => routerA.Status.Contains("pausado"), "Equipo pausado rechaza control sin suprimir entrada local");
Check(routerA.ActivePeer == null, "Denegación no deja activo el modo remoto");
routerB.Pause(false);
await a.ConnectAsync(idB);
await Wait(() => a.Clipboards.ContainsKey(idB) && b.Clipboards.ContainsKey(idA), "Reanudar permite recuperar el canal de portapapeles");
// A modifier held before crossing must not remain logically pressed on origin.
routerA.CaptureKey(new Native.KeyHook { Key = 0xa2, Scan = 29 }, false);
routerA.Request(a.Inputs[idB], "left");
await Wait(() => routerA.ActivePeer == idB && desktopB.HeldKeys.ContainsKey(29), "Modificador mantenido al cambiar se transfiere al destino");
routerA.CaptureKey(new Native.KeyHook { Key = 0xa2, Scan = 29 }, true);
await Wait(() => desktopB.HeldKeys.IsEmpty, "Soltar modificador remoto no lo deja atascado");
routerA.EmergencyReturn();
await Wait(() => routerB.ControlledBy == null, "Retorno de emergencia libera la sesión receptora");
routerA.Request(a.Inputs[idB], "left"); await Wait(() => routerA.ActivePeer == idB, "Nueva sesión usa época nueva");
routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(900, 1) });
await Wait(() => routerA.ActivePeer == null && routerB.ControlledBy == null, "Cruzar borde de regreso retorna al equipo de origen");
routerA.Request(a.Inputs[idB], "bottom", .5, .25);
await Wait(() => routerA.ActivePeer == idB && desktopB.PointerTargets.Last().Y == 1076, "Equipo superior recibe entrada por su borde inferior");
Check(Math.Abs(desktopB.PointerTargets.Last().X - 479) <= 1, "Cruce vertical conserva posición horizontal proporcional");
routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(960, 34) });
await Wait(() => routerA.ActivePeer == null && routerB.ControlledBy == null, "Regreso por borde inferior devuelve control al origen");
routerA.Request(a.Inputs[idB], "top", .5, .75);
await Wait(() => routerA.ActivePeer == idB && desktopB.PointerTargets.Last().Y == 3, "Equipo inferior recibe entrada por borde superior");
routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(960, -16) });
await Wait(() => routerA.ActivePeer == null && routerB.ControlledBy == null, "Regreso por borde superior libera la sesión vertical");

desktopA.LockState = 1; desktopB.LockState = 0;
routerA.RefreshLocalKeyboardState(); routerB.RefreshLocalKeyboardState();
routerA.Request(a.Inputs[idB], "left");
await Wait(() => routerA.ActivePeer == idB && desktopB.LockState == 1, "Al entrar se sincroniza Bloq Mayús aunque estuviera distinto en destino");
var capsDownSuppressed = routerA.CaptureKey(new Native.KeyHook { Key = 0x14, Scan = 0x3a }, false);
if (!capsDownSuppressed) desktopA.Inject(Native.Key(0x14, 0x3a, false));
var capsUpSuppressed = routerA.CaptureKey(new Native.KeyHook { Key = 0x14, Scan = 0x3a }, true);
if (!capsUpSuppressed) desktopA.Inject(Native.Key(0x14, 0x3a, true));
await Wait(() => desktopB.LockState == 0, "Bloq Mayús remoto se desactiva exactamente una vez");
Check(!capsDownSuppressed && !capsUpSuppressed && desktopA.LockState == 0, "Bloq Mayús conserva el estado y LED del teclado de origen");
routerA.CaptureKey(new Native.KeyHook { Key = 0x10, Scan = 0x2a }, false);
var beforeText = desktopB.Rendered.Count;
routerA.CaptureKey(new Native.KeyHook { Key = 65, Scan = 30 }, false);
routerA.CaptureKey(new Native.KeyHook { Key = 65, Scan = 30 }, true);
routerA.CaptureKey(new Native.KeyHook { Key = 0x10, Scan = 0x2a }, true);
await Wait(() => desktopB.Rendered.Count > beforeText, "Shift izquierdo y letra llegan en orden al receptor");
Check(desktopB.Rendered.Last() == 'A', "Shift izquierdo produce mayúscula en el modelo de teclado");
await Wait(() => desktopB.HeldKeys.IsEmpty, "Soltar Shift y letra libera todo el estado inyectado");
routerA.CaptureKey(new Native.KeyHook { Key = 0xa1, Scan = 0x36 }, false);
beforeText = desktopB.Rendered.Count;
routerA.CaptureKey(new Native.KeyHook { Key = 65, Scan = 30 }, false);
routerA.CaptureKey(new Native.KeyHook { Key = 65, Scan = 30 }, true);
routerA.CaptureKey(new Native.KeyHook { Key = 0xa1, Scan = 0x36 }, true);
await Wait(() => desktopB.Rendered.Count > beforeText && desktopB.HeldKeys.IsEmpty, "Shift derecho se transmite y libera por separado");
Check(desktopB.Rendered.Last() == 'A', "Shift derecho produce mayúscula en el modelo de teclado");
routerA.CaptureKey(new Native.KeyHook { Key = 0xa2, Scan = 29 }, false);
routerA.CaptureKey(new Native.KeyHook { Key = 0xa4, Scan = 56 }, false);
Check(routerA.CaptureKey(new Native.KeyHook { Key = 0x7b, Scan = 88 }, false) && routerA.ActivePeer == null, "Ctrl+Alt+F12 funciona aunque Windows no vea modificadores suprimidos");
routerA.CaptureKey(new Native.KeyHook { Key = 0x7b, Scan = 88 }, true);
routerA.CaptureKey(new Native.KeyHook { Key = 0xa4, Scan = 56 }, true);
routerA.CaptureKey(new Native.KeyHook { Key = 0xa2, Scan = 29 }, true);
await Wait(() => routerB.ControlledBy == null && desktopB.HeldKeys.IsEmpty, "Atajo de emergencia libera también los modificadores remotos");

// Receiving cursor positions can lag SendInput. The next delta must be applied
// against the intended position, not a stale GetCursorPos result.
desktopB.DeferPointerUpdates = true;
desktopA.ParkingPosition = new Native.Point(5000, 4);
routerA.Request(a.Inputs[idB], "left"); await Wait(() => routerA.ActivePeer == idB, "Control con posición de estacionamiento fuera de monitor se prepara");
routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(1929, 4) });
await Wait(() => desktopB.PointerTargets.Last().X >= 12, "Estacionamiento usa la posición válida del monitor, no un ancla fuera de pantalla");
routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(1929, 4) });
await Wait(() => desktopB.PointerTargets.Last().X >= 22, "Dos movimientos se acumulan aunque el cursor del sistema siga atrasado");
routerA.EmergencyReturn(); await Wait(() => routerB.ControlledBy == null, "Se cierra sesión de prueba con cursor atrasado");
desktopB.DeferPointerUpdates = false; desktopA.ParkingPosition = new Native.Point(960, 4);

routerA.RequirePointerParkingAcknowledgement = true;
routerA.Request(a.Inputs[idB], "left"); await Wait(() => routerA.ActivePeer == idB, "El estacionamiento puede exigir confirmación del hook");
var parkedStart = desktopB.PointerTargets.Count;
routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(970, 4) });
await Task.Delay(60);
Check(desktopB.PointerTargets.Count == parkedStart, "No se envían deltas antes de confirmar la posición inicial");
routerA.AcknowledgePointerParking(new Native.Point(300, 300));
routerA.AcknowledgePointerParking(new Native.Point(960, 4));
routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(970, 4) });
await Wait(() => desktopB.PointerTargets.Count > parkedStart, "Confirmación del marcador propio habilita el movimiento");
routerA.EmergencyReturn(); await Wait(() => routerB.ControlledBy == null, "Se libera la sesión con confirmación de estacionamiento");
routerA.Request(a.Inputs[idB], "left"); await Wait(() => routerA.ActivePeer == idB, "Nueva sesión espera su propia confirmación");
await Task.Delay(550); routerA.Tick();
await Wait(() => routerA.ActivePeer == null && routerB.ControlledBy == null, "Falta de confirmación devuelve control local sin dejar mouse bloqueado");
routerA.RequirePointerParkingAcknowledgement = false;

routerA.RequirePointerParkingAcknowledgement = true;
routerA.Request(a.Inputs[idB], "left"); await Wait(() => routerA.ActivePeer == idB, "Se prepara sesión para reproducir sesgo de un píxel en el ancla");
routerA.AcknowledgePointerParking(new Native.Point(959, 3));
desktopA.SetPosition(959, 3);
var unbiasedStart = desktopB.PointerTargets.Last();
for (var i = 0; i < 100; i++) routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(969, 3) });
await Wait(() => desktopB.PointerTargets.Last().X == unbiasedStart.X + 1000, "Cien eventos horizontales conservan íntegro el desplazamiento X");
Check(desktopB.PointerTargets.Last().Y == unbiasedStart.Y, "Cien eventos horizontales no hacen subir el puntero con ancla redondeada");
for (var i = 0; i < 100; i++) routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(959, 4) });
await Wait(() => desktopB.PointerTargets.Last().Y == unbiasedStart.Y + 100, "Cien movimientos de un píxel hacia abajo avanzan cien píxeles");
var stillAt = desktopB.PointerTargets.Last(); var stillCount = desktopB.PointerTargets.Count;
for (var i = 0; i < 100; i++) routerA.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(959, 3) });
await Task.Delay(80);
Check(desktopB.PointerTargets.Last().Y == stillAt.Y && desktopB.PointerTargets.Count == stillCount, "Cien notificaciones sin movimiento no inyectan deriva ni saltos");
routerA.EmergencyReturn(); await Wait(() => routerB.ControlledBy == null, "La sesión de regresión del sesgo libera el control");
routerA.RequirePointerParkingAcknowledgement = false;

routerB.Request(b.Inputs[idA], "right");
await Wait(() => routerB.ActivePeer == idA && routerA.ControlledBy == idB, "El mismo vínculo concede control en sentido inverso");
var reverseStart = desktopA.PointerTargets.Count;
routerB.CaptureMouse(0x200, new Native.MouseHook { Point = new Native.Point(950, 4) });
await Wait(() => desktopA.PointerTargets.Count > reverseStart, "Movimiento también llega desde el equipo que antes era receptor");
routerB.EmergencyReturn(); await Wait(() => routerA.ControlledBy == null, "Regreso del control inverso limpia su sesión");

// An authenticated test controller can omit a modifier transition. Its next
// letter snapshot must repair state, including releasing a missed key-up.
var idC = Guid.NewGuid(); using var certC = NodeIdentity.CreateCertificate(idC);
await using var c = new NetworkHub(new(idC, "Controlador de regresión", certC)); c.Start(0);
await c.PairAsync(b.CreateInvitation() with { Host = "127.0.0.1" });
var recoveryEpoch = Guid.NewGuid();
c.Inputs[idB].Send(Frame.Json(PacketKind.RequestControl, new ControlRequest(recoveryEpoch, "left", .5, new(0, 0))));
await Wait(() => routerB.ControlledBy == idC, "Controlador de regresión establece época autenticada");
beforeText = desktopB.Rendered.Count;
c.Inputs[idB].Send(Frame.FromInput(new(InputKind.KeyDown, 1, recoveryEpoch, 0, 0, 65, KeyboardPacket.Pack(30, new(1, 0)))));
await Wait(() => desktopB.Rendered.Count > beforeText, "Snapshot de letra recupera un Shift-down ausente");
Check(desktopB.Rendered.Last() == 'A', "Shift recuperado se aplica antes de la letra en el mismo lote");
c.Inputs[idB].Send(Frame.FromInput(new(InputKind.KeyUp, 2, recoveryEpoch, 0, 0, 65, KeyboardPacket.Pack(30, new(0, 0)))));
await Wait(() => desktopB.HeldKeys.IsEmpty, "Snapshot posterior recupera también un Shift-up ausente");
c.Inputs[idB].Send(Frame.Json(PacketKind.ReleaseControl, new ControlReply(recoveryEpoch)));
await Wait(() => routerB.ControlledBy == null, "La recuperación de teclado respeta el cierre de época");
// Protected storage must be reversible by the current Windows user and opaque on disk.
var sample = new byte[] { 1, 7, 9, 23, 56 };
var protectedValue = SecretProtection.Protect(sample);
Check(SecretProtection.Unprotect(protectedValue).SequenceEqual(sample) && protectedValue != Convert.ToBase64String(sample), "DPAPI protege y recupera credenciales del usuario actual");
var profileFolder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "profile-test-" + Guid.NewGuid().ToString("N"));
var store = new SettingsStore(profileFolder);
var identityBeforeAlias = store.Settings.Id;
using var firstIdentity = store.LoadIdentity().Certificate;
store.SavePeer(a.Credentials[idB]);
var protectedKeyBeforeAlias = store.Settings.Peers.Single().ProtectedKey;
store.SetAlias(store.Settings.Id, "Torre"); store.SetAlias(idB, "Portátil empresa");
Check(store.Settings.Id == identityBeforeAlias && store.Settings.Name != "Torre" && store.Settings.Peers.Single().ProtectedKey == protectedKeyBeforeAlias, "Los alias no cambian ID, nombre real ni credenciales");
store.SavePeer(a.Credentials[idB]);
Check(store.Settings.Peers.Single().Alias == "Portátil empresa", "Actualizar un vínculo conserva su alias");
var reloaded = new SettingsStore(profileFolder);
Check(reloaded.Settings.DisplayName == "Torre" && reloaded.Settings.Peers.Single().DisplayName == "Portátil empresa", "Los alias sobreviven al cierre y reapertura");
var testVm = new DeviceViewModel(reloaded.Settings.Peers.Single(), (_, _) => { }); var vmChanges = 0;
testVm.PropertyChanged += (_, _) => vmChanges++;
testVm.Update(a, null, null, false); vmChanges = 0; testVm.Update(a, null, null, false);
Check(vmChanges == 0, "Estado idéntico de un equipo no emite notificaciones de propiedades");
using var secondIdentity = reloaded.LoadIdentity().Certificate;
Check(firstIdentity.GetCertHashString() == secondIdentity.GetCertHashString() && reloaded.Settings.Peers.Single().Credentials().Key.SequenceEqual(a.Credentials[idB].Key) && !File.ReadAllText(Path.Combine(profileFolder, "settings.json")).Contains(Convert.ToBase64String(a.Credentials[idB].Key)), "Reabrir perfil conserva identidad y vínculo sin guardar la clave en claro");
await PausePolicyTests.RunAsync(Check);
await InputFailureTests.RunAsync(Check);
ProfileLocationTests.Run(Check);
Console.WriteLine($"\n{passed} comprobaciones de integración Windows completadas (sin inyección física).");

sealed class FakeDesktop : IInputDesktop
{
    private int px = 100, py = 500;
    public (int Left, int Top, int Width, int Height) Bounds => (0, 0, 1920, 1080);
    public Native.Point Position => new(Volatile.Read(ref px), Volatile.Read(ref py));
    public Native.Point ParkingPosition { get; set; } = new(960, 4);
    public int LockState { get; set; }
    public bool DeferPointerUpdates { get; set; }
    public ConcurrentQueue<char> Rendered { get; } = new();
    public ConcurrentQueue<Native.Point> PointerTargets { get; } = new();
    public int SingleInjections;
    public Native.Point ConstrainPosition(int x, int y) => new(Math.Clamp(x, 0, 1919), Math.Clamp(y, 0, 1079));
    public ConcurrentDictionary<int, int> HeldKeys { get; } = new();
    public ConcurrentDictionary<int, int> HeldButtons { get; } = new();
    public void SetPosition(int x, int y) { Volatile.Write(ref px, x); Volatile.Write(ref py, y); }
    public bool IsKeyDown(int key) => false;
    public void InjectOne(Native.Input input) { Interlocked.Increment(ref SingleInjections); Inject(input); }
    public void Inject(params Native.Input[] inputs)
    {
        foreach (var input in inputs)
        {
            if (input.Type == 1)
            {
                var key = input.Data.Key;
                var identity = key.Scan | ((key.Flags & 1) != 0 ? 0x10000 : 0);
                if ((key.Flags & 2) == 0)
                {
                    if (HeldKeys.TryAdd(identity, 1)) LockState ^= KeyboardPacket.ToggleBit(key.Key);
                    if (key.Scan == 30) Rendered.Enqueue(((LockState & 1) != 0) ^ (HeldKeys.ContainsKey(42) || HeldKeys.ContainsKey(54)) ? 'A' : 'a');
                }
                else HeldKeys.TryRemove(identity, out _);
            }
            else
            {
                var m = input.Data.Mouse;
                if ((m.Flags & 0x8000) != 0)
                {
                    var target = new Native.Point((int)((long)m.X * 1920 / 65536), (int)((long)m.Y * 1080 / 65536));
                    PointerTargets.Enqueue(target);
                    if (!DeferPointerUpdates) SetPosition(target.X, target.Y);
                }
                if ((m.Flags & 2) != 0) HeldButtons[1] = 1;
                if ((m.Flags & 4) != 0) HeldButtons.TryRemove(1, out _);
            }
        }
    }
}
