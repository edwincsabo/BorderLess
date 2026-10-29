using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using BorderLess.Core;

internal static class PinPairingTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    public static async Task RunAsync(Action<bool, string> check)
    {
        async Task Until(Func<bool> test, string name, int milliseconds = 8000) { var limit = DateTime.UtcNow.AddMilliseconds(milliseconds); while (!test() && DateTime.UtcNow < limit) await Task.Delay(20); check(test(), name); }
        var clock = new Clock(); using var gate = new PinInvitationWindow(clock);
        var initial = gate.Create(); check(initial.Code.Length == 8 && initial.Code.All(char.IsAsciiDigit) && !initial.ToString().Contains(initial.Code), "PIN tiene ocho dígitos y no se revela en ToString");
        using (var attempt = gate.Begin(initial.Id))
        { try { using var other = gate.Begin(initial.Id); throw new Exception("Two concurrent attempts"); } catch (AuthenticationException) { check(true, "Solo un intento PAKE simultáneo por receptor"); } }
        for (var i = 0; i < 4; i++) { using var attempt = gate.Begin(initial.Id); if (i == 3) check(gate.Status.Active && gate.Status.AttemptsRemaining == 0, "Quinto intento conserva su oportunidad aunque se agote el presupuesto"); }
        try { using var other = gate.Begin(initial.Id); throw new Exception("Sixth attempt"); } catch (AuthenticationException) { check(true, "Sexto intento se rechaza globalmente"); }
        var next = gate.Create(); using (var attempt = gate.Begin(next.Id)) { gate.Consume(attempt); }
        check(!gate.Status.Active, "PIN correcto se consume una sola vez");
        var revoked = gate.Create(); using (var attempt = gate.Begin(revoked.Id)) { gate.Revoke(); check(attempt.Token.IsCancellationRequested, "Revocar cancela la operación en curso"); }
        _ = gate.Create(); clock.Now = clock.Now.AddMinutes(3); check(!gate.Status.Active, "El código vence a los dos minutos sin un temporizador de fondo");
        try { using var other = gate.Begin(Guid.Empty); throw new Exception("Expired attempt"); } catch (AuthenticationException) { check(true, "Código vencido no inicia PAKE"); }
        check(PinPake.Normalize("0123 4567") == "01234567" && PinPake.Normalize("0123-4567") == "01234567", "Entrada manual conserva ceros y admite el separador visual");
        foreach (var wrong in new[] { "1234567", "123456789", "1234abcd", "１２３４５６７８" }) { try { PinPake.Normalize(wrong); throw new Exception("PIN format accepted"); } catch (FormatException) { } }
        check(true, "PIN incompleto, letras o dígitos ambiguos se rechazan");
        await CheckContextBindingAsync(check);
        var hostId = Guid.NewGuid(); var clientId = Guid.NewGuid();
        using var hc = NodeIdentity.CreateCertificate(hostId); using var cc = NodeIdentity.CreateCertificate(clientId);
        await using var host = new NetworkHub(new(hostId, "Receptor PIN", hc)); await using var client = new NetworkHub(new(clientId, "Emisor PIN", cc));
        host.Start(0); client.Start(0);
        var logs = new List<Diagnostic>(); host.Activity += log => { lock (logs) logs.Add(log); };
        var invite = host.CreateShortCode();
        check(!host.Credentials.ContainsKey(clientId), "El vínculo todavía no existe antes de verificar el PIN");
        var pairing = client.PairWithShortCodeAsync("127.0.0.1", host.Port, invite.Code, hostId, invite.Id);
        var peer = await pairing.WaitAsync(TimeSpan.FromSeconds(12));
        await Until(() => host.Inputs.ContainsKey(clientId) && host.Clipboards.ContainsKey(clientId), "PIN correcto vincula y abre canales TLS sin segunda confirmación");
        check(peer.Id == hostId && host.Credentials[clientId].Key.SequenceEqual(client.Credentials[hostId].Key) && peer.Key.Length == 32, "Ambos PCs derivan la misma clave persistente de 256 bits");
        check(!host.ShortCodeStatus.Active, "PIN usado deja de anunciarse y no se reutiliza");
        check(host.IsPairingCodeText(invite.Code.Insert(4, " ")) && host.IsPairingCodeText("BL1.example"), "PIN mostrado se excluye del portapapeles automático durante su vigencia");
        var keyBefore = host.Credentials[clientId].Key.ToArray();
        try { await client.PairWithShortCodeAsync("127.0.0.1", host.Port, invite.Code); throw new Exception("Used PIN accepted"); } catch (AuthenticationException) { check(true, "PIN usado no permite otro vínculo ni renueva las credenciales"); }
        check(keyBefore.SequenceEqual(host.Credentials[clientId].Key), "Reutilizar código conserva las claves existentes");
        var bad = host.CreateShortCode(); var wrongCode = bad.Code == "00000000" ? "00000001" : "00000000";
        try { await client.PairWithShortCodeAsync("127.0.0.1", host.Port, wrongCode); throw new Exception("Wrong PIN accepted"); } catch (Exception ex) when (ex is AuthenticationException or IOException) { check(true, "PIN equivocado falla en confirmación criptográfica"); }
        await Until(() => !host.ShortCodeStatus.Busy, "Intento fallido libera el turno");
        check(host.ShortCodeStatus.AttemptsRemaining == 4 && keyBefore.SequenceEqual(host.Credentials[clientId].Key), "PIN incorrecto consume un intento sin reemplazar claves");
        var withdrawn = host.CreateShortCode(); host.RevokeShortCode();
        try { await client.PairWithShortCodeAsync("127.0.0.1", host.Port, withdrawn.Code); throw new Exception("Revocation ignored"); } catch (Exception ex) when (ex is AuthenticationException or IOException) { check(true, "Código revocado no permite vincular automáticamente"); }
        check(keyBefore.SequenceEqual(host.Credentials[clientId].Key), "Revocar conserva las claves de vínculos previos");
        var cancelled = host.CreateShortCode(); using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { await client.PairWithShortCodeAsync("127.0.0.1", host.Port, cancelled.Code, ct: cancel.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { check(true, "Cancelación previa no inicia una nueva vinculación"); }
        }
        var discoverable = host.CreateShortCode();
        await using (var responder = new LanPairDiscovery.Responder(host, 0))
        {
            var endpoints = new[] { new IPEndPoint(IPAddress.Loopback, responder.LocalPort) };
            var found = await LanPairDiscovery.SearchAsync(clientId, CancellationToken.None, endpoints);
            check(found.Count == 1 && found[0].Id == hostId && found[0].Offer == discoverable.Id && found[0].Host == "127.0.0.1" && found[0].Port == host.Port, "Detección local devuelve dirección y oferta sin transportar el PIN");
            host.RevokeShortCode(); var absent = await LanPairDiscovery.SearchAsync(clientId, CancellationToken.None, endpoints);
            check(absent.Count == 0, "Receptor sin código activo no responde a detección");
        }
        lock (logs) check(logs.All(l => !l.Message.Contains(invite.Code) && !l.Message.Contains(bad.Code)), "Actividad no registra los PIN");
        var old = client.Inputs[hostId]; old.Close("Prueba de reconexión tras PIN"); await client.ConnectAsync(hostId);
        await Until(() => client.Inputs.TryGetValue(hostId, out var s) && s != old && s.Connected, "Reconexión usa clave larga guardada y no vuelve a pedir PIN");
    }
    private static async Task CheckContextBindingAsync(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            using var server = await listener.AcceptTcpClientAsync(deadline.Token);
            var a = PinPake.ExchangeAsync(server.GetStream(), "12345678", SHA256.HashData(new byte[] { 1 }), true, deadline.Token);
            var b = PinPake.ExchangeAsync(client.GetStream(), "12345678", SHA256.HashData(new byte[] { 2 }), false, deadline.Token);
            try { await Task.WhenAll(a, b); throw new Exception("Mismatched channel context accepted"); } catch (AuthenticationException) { }
            check(a.IsFaulted && b.IsFaulted, "Mismo PIN con identidades/canal distintos no produce una clave compartida");
        }
        finally { listener.Stop(); }
        listener = new(IPAddress.Loopback, 0); listener.Start();
        try
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            using var server = await listener.AcceptTcpClientAsync(deadline.Token);
            var context = SHA256.HashData(new byte[] { 3 });
            var exchange = PinPake.ExchangeAsync(server.GetStream(), "12345678", context, true, deadline.Token);
            await Wire.ReadAsync(client.GetStream(), deadline.Token, 8192);
            await Wire.WriteJsonAsync(client.GetStream(), new { Id = "BorderLess-PIN/1/sender/" + Convert.ToHexString(context), Gx1 = new string('f', 769), Gx2 = "1", Proof1 = new[] { "1", "1" }, Proof2 = new[] { "1", "1" } }, deadline.Token);
            try { await exchange; throw new Exception("Oversized integer accepted"); } catch (InvalidDataException) { check(true, "Enteros PAKE sobredimensionados se rechazan antes de operaciones criptográficas"); }
        }
        finally { listener.Stop(); }
    }
}
