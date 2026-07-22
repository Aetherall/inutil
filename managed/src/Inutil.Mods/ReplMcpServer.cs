// Inutil.Repl.ReplMcpServer — an SSE MCP transport for the in-game REPL, the agent-facing sibling of ReplServer.
// Publishes a Model Context Protocol server over HTTP+SSE so an AGENT (Claude Code, claude.ai, any MCP client)
// can evaluate C# against the live game with NO client-side install — it just adds a remote MCP server by URL:
//
//     claude mcp add --transport sse inutil-repl http://127.0.0.1:<port>/sse
//
// Same contract as ReplServer (docs/contribution/architecture/18-repl.md), different wire protocol:
//   • LOOPBACK ONLY (127.0.0.1) — this evaluates ARBITRARY C# in-process; never reachable off the machine. (Under
//     Wine a game-side 127.0.0.1 socket is reachable from the Linux host, so a Linux-side agent connects with no bridge.)
//   • MAIN-THREAD DISPATCH — each repl_eval is posted to the main (il2cpp-attached) thread via MainThread.Post,
//     so proxy touches run where il2cpp requires and the §7.12 deadlock guard applies; the background HTTP thread
//     blocks on the result, the main thread does not.
//   • ONE ReplSession per MCP (SSE) connection — state chains across a session's calls; a disconnect resets it.
//
// TRANSPORT: the MCP HTTP+SSE transport (spec rev 2024-11-05), hand-rolled over a raw TcpListener — NOT
// System.Net.HttpListener, which needs http.sys (unreliable under Wine). Two endpoints:
//   • GET  /sse                    -> opens the event stream; the first event is `endpoint` -> /messages?sessionId=<id>
//   • POST /messages?sessionId=<id> -> one JSON-RPC request; the JSON-RPC response is pushed back over that
//                                      session's open SSE stream (the POST itself returns 202 Accepted).
// JSON-RPC methods: initialize, notifications/initialized (no reply), tools/list, tools/call. One tool: repl_eval.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Inutil.Host;   // MainThread

namespace Inutil.Repl;

public sealed class ReplMcpServer : IDisposable
{
    const string ServerName = "inutil-repl";
    const string ServerVersion = "0.1.0";
    const string ProtocolVersion = "2024-11-05";

    readonly TcpListener _listener;
    readonly string _proxyNamespace;
    readonly IEnumerable<string>? _extraImports;
    readonly Action<string>? _log;
    readonly ConcurrentDictionary<string, Session> _sessions = new();
    volatile bool _stop;
    Thread? _acceptThread;
    int _sessionSeq;

    public int Port { get; private set; }

    ReplMcpServer(int port, string proxyNamespace, Action<string>? log, IEnumerable<string>? extraImports)
    {
        Port = port;
        _proxyNamespace = proxyNamespace;
        _extraImports = extraImports;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    // Start a loopback MCP/SSE server. `port == 0` binds an OS-assigned free port (read `Port` after). Throws if
    // the Roslyn scripting engine isn't deployed (no point serving eval to an agent nothing can evaluate).
    public static ReplMcpServer Start(int port, string proxyNamespace, Action<string>? log = null, IEnumerable<string>? extraImports = null)
    {
        if (!ReplBootstrap.PreloadEngine(log))
            throw new InvalidOperationException(
                "REPL scripting engine is not available here — the Roslyn scripting closure " +
                "(Microsoft.CodeAnalysis.CSharp.Scripting) is not deployed beside the plugin.");

        var srv = new ReplMcpServer(port, proxyNamespace, log, extraImports);
        srv._listener.Start();
        srv.Port = ((IPEndPoint)srv._listener.LocalEndpoint).Port;
        srv._acceptThread = new Thread(srv.AcceptLoop) { IsBackground = true, Name = "inutil-mcp-accept" };
        srv._acceptThread.Start();
        log?.Invoke($"[mcp] repl MCP server on http://127.0.0.1:{srv.Port}/sse  (add: claude mcp add --transport sse inutil-repl http://127.0.0.1:{srv.Port}/sse)");
        return srv;
    }

    void AcceptLoop()
    {
        while (!_stop)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { if (_stop) break; Thread.Sleep(10); continue; }
            var t = new Thread(() => HandleConnection(client)) { IsBackground = true, Name = "inutil-mcp-conn" };
            t.Start();
        }
    }

    // One HTTP request per accepted connection. A GET /sse connection stays open for the session's lifetime; a
    // POST /messages is request/response and closes. Everything is exception-isolated to the log — one bad
    // request never takes the server (or the game) down.
    void HandleConnection(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            MiniHttpRequest? req = MiniHttp.ReadRequest(stream);
            if (req is null) { client.Close(); return; }

            if (req.Method == "OPTIONS") { MiniHttp.WriteStatus(stream, 204, "No Content"); client.Close(); return; }   // CORS preflight
            if (req.Method == "GET" && req.Path == "/sse") { ServeSse(client, stream); return; }                // keeps `client` open
            if (req.Method == "POST" && req.Path == "/messages") { ServePost(stream, req); client.Close(); return; }

            MiniHttp.WriteStatus(stream, 404, "Not Found");
            client.Close();
        }
        catch (Exception ex) { _log?.Invoke($"[mcp] connection: {ex.GetType().Name} {ex.Message}"); try { client.Close(); } catch { } }
    }

    // GET /sse — open the event stream, hand the client its POST endpoint, then park. Each connection gets its own
    // ReplSession (state chains within it). A keep-alive comment every ~15s stops idle proxies from dropping it.
    void ServeSse(TcpClient client, NetworkStream stream)
    {
        string id = "s" + Interlocked.Increment(ref _sessionSeq).ToString();
        var session = new Session(new ReplSession(_proxyNamespace, _extraImports), stream);
        _sessions[id] = session;
        try
        {
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Connection: keep-alive\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "\r\n");
            lock (session.WriteLock) { stream.Write(head, 0, head.Length); stream.Flush(); }
            session.SendEvent("endpoint", $"/messages?sessionId={id}");   // the MCP handshake: where to POST

            while (!_stop && client.Connected)
            {
                Thread.Sleep(15000);
                lock (session.WriteLock)
                {
                    byte[] ping = Encoding.ASCII.GetBytes(": keep-alive\r\n\r\n");
                    stream.Write(ping, 0, ping.Length); stream.Flush();
                }
            }
        }
        catch { /* client hung up */ }
        finally { _sessions.TryRemove(id, out _); try { client.Close(); } catch { } }
    }

    // POST /messages?sessionId=X — one JSON-RPC message. Parse it, run it, push the JSON-RPC response over the
    // matching SSE stream, and return 202 on the POST itself (the MCP SSE contract). An unknown/closed session 404s.
    void ServePost(NetworkStream stream, MiniHttpRequest req)
    {
        string? sid = req.QueryValue("sessionId");
        if (sid is null || !_sessions.TryGetValue(sid, out Session? session))
        {
            MiniHttp.WriteStatus(stream, 404, "Not Found");
            return;
        }
        MiniHttp.WriteStatus(stream, 202, "Accepted");   // ack the POST immediately; the answer goes out over SSE

        JsonNode? response;
        try { response = Dispatch(session, req.Body); }
        catch (Exception ex) { _log?.Invoke($"[mcp] dispatch: {ex.GetType().Name} {ex.Message}"); return; }
        if (response is not null) session.SendEvent("message", response.ToJsonString());
    }

    // The JSON-RPC 2.0 dispatch. Returns the response node, or null for a notification (no id -> no reply).
    JsonNode? Dispatch(Session session, string body)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(body); }
        catch { return Error(null, -32700, "parse error"); }

        JsonNode? idNode = msg?["id"];
        string? method = (string?)msg?["method"];
        // JSON-RPC id is a string or a number; preserve whichever verbatim for the reply (net6 has no GetValueKind).
        object? id = null;
        if (idNode is JsonValue idv)
        {
            if (idv.TryGetValue<long>(out long il)) id = il;
            else if (idv.TryGetValue<string>(out string? isv)) id = isv;
        }

        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
                });

            case "notifications/initialized":
            case "notifications/cancelled":
                return null;   // notifications: no response

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = new JsonArray(ReplEvalToolSchema()) });

            case "tools/call":
            {
                string? name = (string?)msg?["params"]?["name"];
                if (name != "repl_eval") return Error(id, -32602, $"unknown tool '{name}'");
                string? code = (string?)msg?["params"]?["arguments"]?["code"];
                if (string.IsNullOrEmpty(code)) return ToolResult(id, "error: missing required argument 'code'", isError: true);

                ReplResult r = EvalOnMain(session.Repl, code!);
                return r.Ok
                    ? ToolResult(id, Format(r.Value), isError: false)
                    : ToolResult(id, r.Error ?? "error", isError: true);
            }

            default:
                return idNode is null ? null : Error(id, -32601, $"method not found: {method}");
        }
    }

    // Main-thread dispatch lives in ReplDispatch (ReplTransport.cs) — the one home all three transports share.
    static ReplResult EvalOnMain(ReplSession repl, string code) => ReplDispatch.EvalOnMain(repl, code);

    static JsonObject ReplEvalToolSchema() => new()
    {
        ["name"] = "repl_eval",
        ["description"] =
            "Evaluate a C# expression or statement in the running game via the inutil REPL. Runs on the game's " +
            "main thread against the live loaded world: you can name the game's Il2CppInterop proxies, call " +
            "Inutil.Hooks/Introspect, and register hooks that fire immediately. State chains across calls in this " +
            "session (a `var` declared in one call is visible in the next). Returns the value of the last " +
            "expression, or the compile/runtime error. Do not await a main-thread primitive (it fails loud).",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["code"] = new JsonObject { ["type"] = "string", ["description"] = "C# to evaluate (one submission)." },
            },
            ["required"] = new JsonArray("code"),
        },
    };

    static JsonObject Result(object? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = IdNode(id), ["result"] = result };
    static JsonObject Error(object? id, int code, string message) => new()
    { ["jsonrpc"] = "2.0", ["id"] = IdNode(id), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    static JsonObject ToolResult(object? id, string text, bool isError) => Result(id, new JsonObject
    { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError });

    static JsonNode? IdNode(object? id) => id switch { string s => JsonValue.Create(s), long l => JsonValue.Create(l), _ => null };
    static string Format(object? v) => v is null ? "null" : (v.ToString() ?? v.GetType().Name);

    // HTTP request parsing / status writing live in MiniHttp (ReplTransport.cs) — shared with ReplHttpServer.

    // One live MCP connection: its ReplSession + the SSE stream responses are pushed over (serialized by WriteLock).
    sealed class Session
    {
        public readonly ReplSession Repl;
        readonly NetworkStream _sse;
        public readonly object WriteLock = new();
        public Session(ReplSession repl, NetworkStream sse) { Repl = repl; _sse = sse; }

        public void SendEvent(string ev, string data)
        {
            // Each data line is prefixed `data: `; a multi-line payload sends one `data:` per line (SSE framing).
            var sb = new StringBuilder().Append("event: ").Append(ev).Append('\n');
            foreach (string line in data.Split('\n')) sb.Append("data: ").Append(line).Append('\n');
            sb.Append('\n');
            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            lock (WriteLock) { _sse.Write(bytes, 0, bytes.Length); _sse.Flush(); }
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
        foreach (var kv in _sessions) { try { /* streams close with their connections */ } catch { } }
        _sessions.Clear();
    }
}
