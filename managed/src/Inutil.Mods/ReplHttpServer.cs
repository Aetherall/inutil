// Inutil.Repl.ReplHttpServer — the plain-HTTP JSON transport for the in-game REPL: the SCRIPT-facing sibling of
// ReplServer (a human at telnet) and ReplMcpServer (an agent over SSE MCP). One endpoint:
//
//   POST /eval   body = C# (one submission)  ->  200 {"ok":true,"result":"<value.ToString()>"}
//                                            |   200 {"ok":false,"result":"<compile/runtime error>"}
//
// The contract a SHELL SCRIPT can consume — request/response, no session handshake:
//   printf '%s' "$code" | curl -s --data-binary @- http://127.0.0.1:<port>/eval | jq -r '.result'
// which the SSE MCP protocol is not. Same rules as the siblings: LOOPBACK ONLY (evaluates arbitrary C# in-process
// — never expose it off the machine), MAIN-THREAD dispatch via ReplDispatch (the §7.12 deadlock guard applies),
// raw TcpListener not HttpListener (http.sys is unreliable under Wine, where the game runs).
//
// ONE ReplSession for the server's lifetime — state chains across calls like one telnet session (a `var` declared
// in one POST is visible in the next; a failed submission leaves state unadvanced, so just re-POST). Created
// LAZILY on the first eval, NOT at Start(): ReplSession snapshots the loaded world at construction, and this
// server's host is a coremod whose Start() runs at chainload — before the SDK and the mods' assemblies load. The
// first POST arrives when the world is up, so the lazy session sees everything. Submissions are serialized
// (_evalLock). Roslyn submission assemblies are not collectible (docs/guide/06-repl.md), so a long-lived server
// grows with use — bounded to one session here.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Inutil.Repl;

public sealed class ReplHttpServer : IDisposable
{
    readonly TcpListener _listener;
    readonly string _proxyNamespace;
    readonly IEnumerable<string>? _extraImports;
    readonly Action<string>? _log;
    readonly object _evalLock = new();
    ReplSession? _session;   // lazy — see the header (construction snapshots the loaded world)
    volatile bool _stop;
    Thread? _acceptThread;

    // The actual bound port (resolved when port 0 asks the OS for a free one).
    public int Port { get; private set; }

    ReplHttpServer(int port, string proxyNamespace, Action<string>? log, IEnumerable<string>? extraImports)
    {
        Port = port;
        _proxyNamespace = proxyNamespace;
        _extraImports = extraImports;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    // Start a loopback HTTP eval server. `port == 0` binds an OS-assigned free port (read `Port` after). Throws if
    // the Roslyn scripting engine isn't deployed (no point serving an endpoint nothing can evaluate).
    public static ReplHttpServer Start(int port, string proxyNamespace, Action<string>? log = null, IEnumerable<string>? extraImports = null)
    {
        if (!ReplBootstrap.PreloadEngine(log))
            throw new InvalidOperationException(
                "REPL scripting engine is not available here — the Roslyn scripting closure " +
                "(Microsoft.CodeAnalysis.CSharp.Scripting) is not deployed beside the plugin.");

        var srv = new ReplHttpServer(port, proxyNamespace, log, extraImports);
        srv._listener.Start();
        srv.Port = ((IPEndPoint)srv._listener.LocalEndpoint).Port;
        srv._acceptThread = new Thread(srv.AcceptLoop) { IsBackground = true, Name = "inutil-http-accept" };
        srv._acceptThread.Start();
        log?.Invoke($"[repl-http] eval endpoint on http://127.0.0.1:{srv.Port}/eval  (POST C# -> {{\"ok\":bool,\"result\":string}})");
        return srv;
    }

    void AcceptLoop()
    {
        while (!_stop)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { if (_stop) break; Thread.Sleep(10); continue; }
            var t = new Thread(() => HandleConnection(client)) { IsBackground = true, Name = "inutil-http-conn" };
            t.Start();
        }
    }

    // One request per connection, request/response, then close. Exception-isolated to the log — one bad request
    // never takes the server (or the game) down.
    void HandleConnection(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            MiniHttpRequest? req = MiniHttp.ReadRequest(stream);
            if (req is null) { client.Close(); return; }

            if (req.Method == "POST" && req.Path == "/eval")
            {
                ReplResult r;
                if (string.IsNullOrWhiteSpace(req.Body))
                    r = ReplResult.Bad("empty submission — POST the C# to evaluate as the request body");
                else
                    lock (_evalLock)
                    {
                        _session ??= new ReplSession(_proxyNamespace, _extraImports);
                        r = ReplDispatch.EvalOnMain(_session, req.Body);
                    }

                var json = new JsonObject
                {
                    ["ok"] = r.Ok,
                    ["result"] = r.Ok ? (r.Value?.ToString() ?? "null") : (r.Error ?? "error"),
                };
                WriteJson(stream, json.ToJsonString());
            }
            else
                MiniHttp.WriteStatus(stream, 404, "Not Found");

            client.Close();
        }
        catch (Exception ex) { _log?.Invoke($"[repl-http] connection: {ex.GetType().Name} {ex.Message}"); try { client.Close(); } catch { } }
    }

    static void WriteJson(NetworkStream stream, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n\r\n");
        stream.Write(head, 0, head.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
    }
}
