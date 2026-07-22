// mcp-repl — publish the in-game inutil REPL as an SSE MCP server, so an AI agent can evaluate C# in the live
// game with NOTHING to install (it just adds a remote MCP server by URL). Loopback only.
//
// A COREMOD (load-once, permanent — not a hot-reload mod), so the endpoint is stable for the whole session.
// Deploy this folder into your loader's coremod dir:
//   BepInEx:     <game>/BepInEx/inutil-coremods/mcp-repl/McpReplCoremod.cs
//   MelonLoader: <game>/MelonLoader/inutil-coremods/mcp-repl/McpReplCoremod.cs
// Requires Inutil.Mods.dll + the Roslyn scripting closure deployed beside the plugin (same as the no-build .cs
// mod loop and the telnet REPL). The bound port is printed to the loader log at startup.
//
// Connect an agent (no install — a remote MCP server by URL):
//   claude mcp add --transport sse inutil-repl http://127.0.0.1:<port>/sse
using Inutil;              // ILoad — the load-once lifecycle
using Inutil.Repl;         // ReplMcpServer — the SSE MCP transport (ships in Inutil.Mods.dll)

public sealed class McpReplCoremod : ILoad
{
    // Your game's il2cpp PROXY NAMESPACE, so a REPL line names game types with no `using`. Set it to your game:
    // "<Game>" under BepInEx, "Il2Cpp<Game>" under MelonLoader. (This default targets the ToyGame fixture.)
    const string ProxyNamespace = "ToyGame";

    ReplMcpServer? _server;

    public void OnLoad()
    {
        // Loopback ONLY — this evaluates arbitrary C# in-process; never expose it off the machine. port 0 lets the
        // OS pick a free port (read back in the log). Start() logs the ready URL + the `claude mcp add` line.
        _server = ReplMcpServer.Start(0, ProxyNamespace, m => Inutil.Hooks.Hooks.OnWarning?.Invoke("[mcp-repl] " + m));
    }
}
