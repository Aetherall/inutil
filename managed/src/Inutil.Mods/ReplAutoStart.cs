// Env-driven REPL transport auto-start — the host calls this once after the mod loop is up, so a consumer
// enables the REPL faces with plain environment instead of shipping a per-game publisher mod:
//   INUTIL_REPL_PORT     start the HTTP eval face (ReplHttpServer) on this loopback port
//   INUTIL_MCP_PORT      start the SSE MCP face (ReplMcpServer) on this loopback port
//   INUTIL_REPL_NS       the game's proxy namespace added to the eval usings (e.g. "Game"); empty = none
//   INUTIL_REPL_IMPORTS  comma-separated extra usings for eval sessions
// Unset ports = nothing starts (zero cost). Servers are rooted here for the process lifetime.
namespace Inutil.Repl;

public static class ReplAutoStart
{
    static ReplHttpServer? _http;   // rooted: the transports live as long as the process
    static ReplMcpServer? _mcp;

    public static void FromEnvironment(System.Action<string>? log = null)
    {
        string ns = System.Environment.GetEnvironmentVariable("INUTIL_REPL_NS") ?? "";
        string[]? imports = System.Environment.GetEnvironmentVariable("INUTIL_REPL_IMPORTS")
            ?.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        if (int.TryParse(System.Environment.GetEnvironmentVariable("INUTIL_REPL_PORT"), out int httpPort))
            try { _http = ReplHttpServer.Start(httpPort, ns, log, imports); }
            catch (System.Exception ex) { log?.Invoke($"inutil: repl http face not started ({ex.GetType().Name}: {ex.Message})"); }

        if (int.TryParse(System.Environment.GetEnvironmentVariable("INUTIL_MCP_PORT"), out int mcpPort))
            try { _mcp = ReplMcpServer.Start(mcpPort, ns, log, imports); }
            catch (System.Exception ex) { log?.Invoke($"inutil: repl mcp face not started ({ex.GetType().Name}: {ex.Message})"); }
    }
}
