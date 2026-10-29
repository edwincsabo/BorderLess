using System.Net;
using System.Security.Cryptography;
using BorderLess.Core;

var passed = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); passed++; }
async Task Eventually(Func<bool> condition, string description, int ms = 6000)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (!condition() && DateTime.UtcNow < until) await Task.Delay(25);
    Check(condition(), description);
}
var epoch = Guid.NewGuid();
var desktopA = new DesktopSnapshot(new[] { new DisplayMonitor("DISPLAY1", -1920, 0, 1920, 1080, false, 100), new DisplayMonitor("DISPLAY2", 0, 0, 2560, 1440, true, 125) }, "Torre");
desktopA.Validate();
Check(DesktopSnapshot.Same(desktopA, desktopA with { Monitors = desktopA.Monitors.ToArray() }), "Pantallas iguales no provocan publicaciones o redibujos repetidos");
try { new DesktopSnapshot(new[] { new DisplayMonitor("A", 0, 0, 0, 1080, true) }).Validate(); throw new Exception(); } catch (InvalidDataException) { Check(true, "Se rechazan dimensiones de pantalla no válidas"); }
try { new DesktopSnapshot(new[] { new DisplayMonitor("A", 0, 0, 100, 100, true), new DisplayMonitor("A", 0, 0, 100, 100, true) }).Validate(); throw new Exception(); } catch (InvalidDataException) { Check(true, "Se rechazan identificadores duplicados de pantalla"); }
var wakeQueue = new InputOutbox();
using (var wakeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
{
    var waiting = wakeQueue.WaitForDataAsync(wakeDeadline.Token).AsTask();
    Check(!waiting.IsCompleted, "Emisor sin datos espera sin sondeo activo");
    wakeQueue.Enqueue(Frame.FromInput(new(InputKind.KeyDown, 1, epoch, 0, 0, 65, 30)));
    Check(await waiting && wakeQueue.TryTake(out _), "Encolar una acción despierta al consumidor inmediatamente");
    for (var cycle = 0; cycle < 200; cycle++)
    {
        var next = wakeQueue.WaitForDataAsync(wakeDeadline.Token).AsTask();
        wakeQueue.Enqueue(Frame.FromInput(new(InputKind.KeyUp, cycle + 2, epoch, 0, 0, 65, 30)));
        await next;
        if (!wakeQueue.TryTake(out _)) throw new Exception("Notificación perdida");
    }
    Check(true, "Doscientos ciclos vacío/encolado no pierden despertares");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await wakeQueue.WaitForDataAsync(canceled.Token); throw new Exception("Cancelación ignorada"); }
    catch (OperationCanceledException) { Check(true, "Cerrar una espera vacía no deja al emisor bloqueado"); }
}
var input = new InputEvent(InputKind.KeyDown, 42, epoch, -123, 456, 65, 30);
var keyboardState = new KeyboardSnapshot(63, 7);
var packedKey = KeyboardPacket.Pack(0x1001d, keyboardState);
Check(KeyboardPacket.TryUnpack(packedKey, out var unpackedNative, out var unpackedState) && unpackedNative == 0x1001d && unpackedState == keyboardState, "Estado de modificadores y bloqueos se conserva sin alterar código físico");
Check(!KeyboardPacket.TryUnpack(unchecked((int)0x80000000), out _, out _) && !KeyboardPacket.TryUnpack(1 << 28, out _, out _), "Bits de teclado fuera de protocolo se rechazan");
Check(KeyboardPacket.Normalize(0x10, 0x36) == 0xa1 && KeyboardPacket.Normalize(0x10, 0x2a) == 0xa0 && KeyboardPacket.Normalize(0x11, 0x1001d) == 0xa3, "Modificadores genéricos se normalizan por lado");
Check(Frame.FromInput(input).ReadInput() == input, "Formato binario conserva secuencia, época, acumulados y tecla");
var outbox = new InputOutbox();
for (var i = 1; i <= 1000; i++) outbox.Enqueue(Frame.FromInput(new(InputKind.Move, i, epoch, i, i * 2)));
Check(outbox.Count == 1 && outbox.Coalesced == 999, "1000 movimientos se coalescen en un estado acumulativo");
outbox.Enqueue(Frame.FromInput(new(InputKind.ButtonDown, 1001, epoch, 1000, 2000, 1)));
outbox.Enqueue(Frame.FromInput(new(InputKind.Move, 1002, epoch, 1010, 2020)));
var sequence = new List<long>(); while (outbox.TryTake(out var frame)) sequence.Add(frame.ReadInput().Sequence);
Check(sequence.SequenceEqual(new long[] { 1000, 1001, 1002 }), "Mover, pulsar y mover conservan orden y posición de clic");
var bounded = new InputOutbox(); var rejected = false;
for (var i = 0; i < 2000; i++) if (!bounded.Enqueue(Frame.FromInput(new(InputKind.KeyDown, i, epoch, 0, 0, 65, 30)))) rejected = true;
Check(rejected && bounded.Count <= 512, "Sobrecarga de acciones se rechaza con memoria acotada");
var state = new InputState(); state.Begin(epoch);
Check(state.TryAdvance(new(InputKind.Move, 1, epoch, 15, -5), out var dx, out var dy) && dx == 15 && dy == -5, "Primer movimiento acumulado se aplica");
Check(state.TryAdvance(new(InputKind.Move, 5, epoch, 40, 12), out dx, out dy) && dx == 25 && dy == 17, "Coalescer estados intermedios no pierde deltas relativos");
Check(!state.TryAdvance(new(InputKind.Move, 4, epoch, 9, 9), out _, out _) && !state.TryAdvance(new(InputKind.Move, 6, Guid.NewGuid(), 1, 1), out _, out _), "Eventos atrasados y épocas ajenas se ignoran");
state.End(); Check(!state.TryAdvance(new(InputKind.KeyDown, 100, epoch, 1, 1), out _, out _), "Cerrar época impide acciones antiguas");
using var memory = new MemoryStream(); await Wire.WriteAsync(memory, Frame.FromInput(input), CancellationToken.None); memory.Position = 0;
Check((await Wire.ReadAsync(memory, CancellationToken.None)).ReadInput() == input, "Enmarcado de red se reconstruye sin cambios");
using (var recorded = new RecordingStream())
{
    await Wire.WriteAsync(recorded, Frame.FromInput(input), CancellationToken.None);
    Check(recorded.WriteCalls == 1 && recorded.Length == 54, "Cabecera y entrada utilizan una sola escritura de 54 bytes");
    await Wire.WriteAsync(recorded, Frame.FromInput(input with { Sequence = 43 }), CancellationToken.None);
    using var fragmented = new FragmentedStream(recorded.ToArray());
    var sharedHeader = new byte[5];
    var first = await Wire.ReadAsync(fragmented, CancellationToken.None, 16384, sharedHeader);
    var second = await Wire.ReadAsync(fragmented, CancellationToken.None, 16384, sharedHeader);
    Check(first.ReadInput() == input && second.ReadInput().Sequence == 43, "Lecturas fragmentadas y cabecera reutilizada conservan límites y contenido");
}
using (var blocked = new BlockedStream())
using (var expired = new CancellationTokenSource(60))
{
    try { await Wire.WriteAsync(blocked, Frame.FromInput(input), expired.Token); throw new Exception("Escritura no cancelada"); }
    catch (OperationCanceledException) { Check(true, "Una escritura detenida respeta su plazo con el buffer reutilizable"); }
}
using (var afterCancel = new RecordingStream())
{
    await Wire.WriteAsync(afterCancel, Frame.FromInput(input), CancellationToken.None); afterCancel.Position = 0;
    Check((await Wire.ReadAsync(afterCancel, CancellationToken.None)).ReadInput() == input, "Reutilizar buffers tras cancelación no corrompe el siguiente mensaje");
}
var malicious = new MemoryStream(new byte[] { 127, 255, 255, 255, 1 });
try { await Wire.ReadAsync(malicious, CancellationToken.None); throw new Exception("No se rechazó tamaño malicioso"); } catch (InvalidDataException) { Check(true, "Tamaño de trama malicioso se rechaza antes de reservar memoria"); }

var hostId = Guid.NewGuid(); var clientId = Guid.NewGuid();
using var hostCert = NodeIdentity.CreateCertificate(hostId); using var clientCert = NodeIdentity.CreateCertificate(clientId);
await using var host = new NetworkHub(new(hostId, "Receptor de prueba", hostCert));
await using var client = new NetworkHub(new(clientId, "Controlador de prueba", clientCert));
var logs = new List<Diagnostic>(); host.Activity += d => { lock (logs) logs.Add(d); };
host.Start(0); client.Start(0);
host.UpdateDesktop(desktopA);
client.UpdateDesktop(new DesktopSnapshot(new[] { new DisplayMonitor("Laptop", 0, 0, 1920, 1080, true, 150) }, "Portátil empresa"));
var invite = host.CreateInvitation() with { Host = "127.0.0.1" };
Check(Invitation.Parse(invite.Encode()) == invite, "Código temporal valida identidad y dirección");
try { Invitation.Parse((invite with { Expires = DateTimeOffset.UtcNow.AddMinutes(-1) }).Encode()); throw new Exception(); } catch (FormatException) { Check(true, "Código vencido rechazado"); }
var peer = await client.PairAsync(invite);
await Eventually(() => host.Inputs.ContainsKey(clientId) && host.Clipboards.ContainsKey(clientId), "Vinculación TLS mutua abre canales separados de entrada y portapapeles");
Check(host.Inputs[clientId].InputRevision == 2 && client.Inputs[hostId].InputRevision == 2, "Los dos extremos negocian sincronización de teclado revisión 2");
Check(host.Inputs[clientId].LayoutRevision == 1 && client.RemoteDesktops[hostId].Monitors.Length == 2 && host.RemoteDesktops[clientId].Label == "Portátil empresa", "Disposición y nombre visible se intercambian al conectar por TLS");
host.UpdateDesktop(desktopA with { Label = "Torre nueva" });
await Eventually(() => client.RemoteDesktops[hostId].Label == "Torre nueva", "Cambios de pantallas o etiqueta llegan sin volver a vincular");
Check(host.Credentials[clientId].Key.SequenceEqual(client.Credentials[hostId].Key), "Clave derivada coincide sin enviarse por el protocolo");
var receivedInput = new TaskCompletionSource<InputEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
host.Message += (_, f) => { if (f.Kind == PacketKind.Input) receivedInput.TrySetResult(f.ReadInput()); };
client.Inputs[hostId].Send(Frame.FromInput(input));
Check(await receivedInput.Task.WaitAsync(TimeSpan.FromSeconds(5)) == input, "Entrada viaja por TLS hasta receptor sin inyectar teclas en Windows");
var gotClip = new TaskCompletionSource<ClipboardPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
host.Message += (_, f) => { if (f.Kind == PacketKind.Clipboard) gotClip.TrySetResult(f.Read<ClipboardPayload>()); };
var payload = new ClipboardPayload(clientId, Guid.NewGuid(), "text", "Prueba de protocolo; no se modifica el portapapeles de Windows.");
client.Clipboards[hostId].Send(Frame.Json(PacketKind.Clipboard, payload));
Check(await gotClip.Task.WaitAsync(TimeSpan.FromSeconds(5)) == payload, "Portapapeles se transmite por su canal independiente");
var largeReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var inputDuringBulk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
host.Message += (_, f) =>
{
    if (f.Kind == PacketKind.Clipboard && f.Payload.Length > 3_000_000) largeReceived.TrySetResult();
    if (f.Kind == PacketKind.Input && f.ReadInput().Sequence == 43) inputDuringBulk.TrySetResult();
};
client.Clipboards[hostId].Send(Frame.Json(PacketKind.Clipboard, payload with { Change = Guid.NewGuid(), Content = new string('X', 4_000_000) }));
await Task.Delay(80);
client.Inputs[hostId].Send(Frame.FromInput(input with { Sequence = 43 }));
await inputDuringBulk.Task.WaitAsync(TimeSpan.FromSeconds(3));
Check(!largeReceived.Task.IsCompleted, "Entrada llega mientras el canal separado continúa una transferencia grande simulada");
await largeReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
await Eventually(() => client.Inputs[hostId].Rtt > 0, "Heartbeat mide RTT real");
var old = client.Inputs[hostId]; old.Close("Prueba de reconexión");
await client.ConnectAsync(hostId);
await Eventually(() => client.Inputs.TryGetValue(hostId, out var newSession) && newSession != old && host.Inputs.ContainsKey(clientId), "Par conocido reconecta con certificado y clave persistibles");

var intruderId = Guid.NewGuid(); using var intruderCert = NodeIdentity.CreateCertificate(intruderId);
await using var intruder = new NetworkHub(new(intruderId, "Cliente no autorizado", intruderCert));
try { await intruder.PairAsync(invite with { Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }); throw new Exception("Se aceptó una clave incorrecta"); }
catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { Check(true, "Clave de invitación incorrecta no obtiene sesión"); }
Check(!host.Inputs.ContainsKey(intruderId), "Cliente no autorizado no recibe capacidad de entrada");
try { await intruder.PairAsync(invite with { Fingerprint = new string('A', 64) }); throw new Exception("Se aceptó certificado incorrecto"); }
catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { Check(true, "Certificado remoto que no coincide con código se rechaza"); }

await using var relay = new RelayServer(); relay.Start(0, IPAddress.Loopback);
host.EnableRelay("127.0.0.1", relay.Port); await Eventually(() => host.RelayWaiting >= 2, "Relay confirma recepción de rutas antes de vincular");
var relayInvite = host.CreateInvitation(true);
var thirdId = Guid.NewGuid(); using var thirdCert = NodeIdentity.CreateCertificate(thirdId);
await using var third = new NetworkHub(new(thirdId, "Equipo por relay", thirdCert));
third.Start(0); await third.PairAsync(relayInvite);
await Eventually(() => host.Inputs.ContainsKey(thirdId) && host.Clipboards.ContainsKey(thirdId), "Relay enruta TLS extremo a extremo con dos canales");
Check(host.Inputs.ContainsKey(clientId) && host.Inputs.ContainsKey(thirdId), "Varios equipos permanecen vinculados simultáneamente");
Check(host.Inputs[thirdId].LayoutRevision == 0, "Un extremo sin capacidad de mapa permanece compatible y no recibe mensajes de pantalla");
host.Forget(clientId);
await Eventually(() => !host.Inputs.ContainsKey(clientId), "Desvincular revoca conexión e identidad");
Check(!host.Credentials.ContainsKey(clientId), "Credencial revocada se elimina del receptor");
lock (logs) Check(logs.All(d => !d.Message.Contains(invite.Secret) && !d.Message.Contains("Prueba de protocolo")), "Registros omiten secretos y contenido");
await FileTransferTests.RunAsync(Check);
await PinPairingTests.RunAsync(Check);
Console.WriteLine($"\n{passed} comprobaciones completadas.");

sealed class RecordingStream : MemoryStream
{
    public int WriteCalls;
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { WriteCalls++; return base.WriteAsync(buffer, ct); }
}
sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], ct);
}
sealed class BlockedStream : MemoryStream
{
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => await Task.Delay(Timeout.Infinite, ct);
}
