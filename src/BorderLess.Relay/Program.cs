using BorderLess.Core;

var port = 45162;
if (args.Length > 0 && (!int.TryParse(args[0], out port) || port is < 1 or > 65535)) { Console.Error.WriteLine("Uso: BorderLess.Relay [puerto: 45162]"); return 1; }
await using var relay = new RelayServer();
try { relay.Start(port); }
catch (System.Net.Sockets.SocketException ex) { Console.Error.WriteLine("No se pudo abrir el puerto: " + ex.SocketErrorCode); return 1; }
Console.WriteLine($"BorderLess Relay | puerto {relay.Port} | túneles con TLS de extremo a extremo");
Console.WriteLine("No se registran claves de sala ni contenido. Ctrl+C para detener.");
var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; completion.TrySetResult(); };
await completion.Task;
return 0;
