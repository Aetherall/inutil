// Inutil.Repl.ReplServer — a localhost TCP transport for the in-game REPL (§7.12). `telnet 127.0.0.1 <port>`, type
// C#, see results against the LIVE game.
//
//   • The accept loop + each connection's read loop run on BACKGROUND threads.
//   • Each submission is DISPATCHED to the loader's main (il2cpp-attached) thread via MainThread.Post — a REPL
//     line's proxy touches (player.Health) MUST run there, and that is where ReplSession.Eval's deadlock guard
//     lives. The BACKGROUND transport thread blocks on the posted result; the MAIN thread does not (it runs the
//     Eval during its normal Drain). So a line that awaits a main-thread primitive fails loud in Eval, one place.
//
// Bound to loopback ONLY (127.0.0.1) — this evaluates arbitrary C# in-process, so it must never be reachable off
// the machine. One ReplSession per connection: state chains within a session, and a disconnect resets it cleanly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Inutil.Repl;

public sealed class ReplServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _proxyNamespace;
    private readonly IEnumerable<string>? _extraImports;
    private readonly Action<string>? _log;
    private volatile bool _stop;
    private Thread? _acceptThread;

    // The actual bound port (resolved when port 0 asks the OS for a free one).
    public int Port { get; private set; }

    private ReplServer(int port, string proxyNamespace, Action<string>? log, IEnumerable<string>? extraImports)
    {
        Port = port;
        _proxyNamespace = proxyNamespace;
        _extraImports = extraImports;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    // Start a loopback REPL server. `port == 0` binds an OS-assigned free port (read `Port` after). Throws if the
    // scripting engine isn't deployed (no point listening for lines nothing can evaluate).
    public static ReplServer Start(int port, string proxyNamespace, Action<string>? log = null, IEnumerable<string>? extraImports = null)
    {
        if (!ReplBootstrap.PreloadEngine(log))
            throw new InvalidOperationException(
                "REPL scripting engine is not available here — the Roslyn scripting closure " +
                "(Microsoft.CodeAnalysis.CSharp.Scripting) is not deployed beside the plugin.");

        var srv = new ReplServer(port, proxyNamespace, log, extraImports);
        srv._listener.Start();
        srv.Port = ((IPEndPoint)srv._listener.LocalEndpoint).Port;
        srv._acceptThread = new Thread(srv.AcceptLoop) { IsBackground = true, Name = "inutil-repl-accept" };
        srv._acceptThread.Start();
        log?.Invoke($"[repl] listening on 127.0.0.1:{srv.Port}");
        return srv;
    }

    private void AcceptLoop()
    {
        while (!_stop)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { if (_stop) break; Thread.Sleep(10); continue; }
            var t = new Thread(() => ServeClient(client)) { IsBackground = true, Name = "inutil-repl-conn" };
            t.Start();
        }
    }

    private void ServeClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                var session = new ReplSession(_proxyNamespace, _extraImports);
                writer.WriteLine("inutil repl — type C# (one line per submission); :quit to exit");
                writer.Write("> "); writer.Flush();

                string? line;
                while (!_stop && (line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) { writer.Write("> "); writer.Flush(); continue; }
                    if (line is ":quit" or ":exit") break;

                    ReplResult r = Evaluate(session, line);
                    writer.WriteLine(r.Ok ? Format(r.Value) : "! " + r.Error);
                    writer.Write("> "); writer.Flush();
                }
            }
        }
        catch (Exception ex) { _log?.Invoke($"[repl] connection: {ex.GetType().Name} {ex.Message}"); }
    }

    // Main-thread dispatch lives in ReplDispatch (ReplTransport.cs) — the one home all three transports share.
    private static ReplResult Evaluate(ReplSession session, string line) => ReplDispatch.EvalOnMain(session, line);

    private static string Format(object? v) => v is null ? "null" : (v.ToString() ?? v.GetType().Name);

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
    }
}
