# mcp-repl — the in-game REPL as an MCP server for agents

Publishes inutil's [in-game C# REPL](../../docs/guide/06-repl.md) as a **Model Context Protocol** server over
**HTTP+SSE**, so an AI agent (Claude Code, claude.ai, any MCP client) can evaluate C# against the *live game*
— call the game's proxies, register hooks, inspect state — with **nothing to install**. The agent just adds a
remote MCP server by URL.

The transport lives in the SDK (`Inutil.Repl.ReplMcpServer`, a sibling of the telnet `ReplServer` —
[architecture](../../docs/contribution/architecture/18-repl.md)); this mod is the thin coremod that starts it.

## What the agent gets

One tool, `repl_eval(code)` — evaluate a C# submission in the running game. It runs on the game's main
(il2cpp-attached) thread, so it can name the game's Il2CppInterop proxies, call `Inutil.Hooks`/`Introspect`,
and register hooks that fire immediately. **State chains** across calls within a connection (a `var` declared
in one call is visible in the next), exactly like the telnet REPL.

## Requirements

- inutil deployed with **`Inutil.Mods.dll` + the Roslyn scripting closure** beside the plugin (the same deploy
  the no-build `.cs` mod loop and the telnet REPL need — see [guide/1](../../docs/guide/01-setup-first-mod.md)).

## Install

1. Copy this folder into your loader's coremod directory:
   - **BepInEx** → `<game>/BepInEx/inutil-coremods/mcp-repl/`
   - **MelonLoader** → `<game>/MelonLoader/inutil-coremods/mcp-repl/`
2. Set `ProxyNamespace` in `McpReplCoremod.cs` to your game's proxy namespace (`<Game>` on BepInEx,
   `Il2Cpp<Game>` on MelonLoader). It only affects the `using`s a REPL line gets for free.
3. Launch the game. The loader log prints the ready line with the port (port `0` → an OS-assigned free one):

   ```
   [mcp-repl] repl MCP server on http://127.0.0.1:53817/sse  (add: claude mcp add --transport sse inutil-repl http://127.0.0.1:53817/sse)
   ```

## Connect an agent

Nothing to install — it's a remote MCP server:

```bash
claude mcp add --transport sse inutil-repl http://127.0.0.1:<port>/sse
```

(Under Wine the game-side `127.0.0.1` socket is reachable from the Linux host, the same way the telnet REPL is
— so a Linux-side agent connects with no bridge.) Then the agent can call `repl_eval`, e.g.:

```
repl_eval("var p = UnityEngine.Object.FindObjectOfType<Player>(); p.GetHealth()")
repl_eval("Inutil.Introspect.Dump(p)")
repl_eval("Hooks.Pre(typeof(Player), \"GetHealth\", System.Type.EmptyTypes, ctx => ctx.SetReturn(9999))")
```

## Security

The server binds **loopback only** (`127.0.0.1`) — it evaluates *arbitrary C# in-process*, so it must never be
reachable off the machine. It is a developer tool for the person at the keyboard. Only run it while you're
actively using it; a coremod keeps it up for the whole session.

## The one rule (inherited from the REPL)

A submission runs synchronously on the main thread — don't `await` a main-thread primitive
(`MainThread.NextFrame()` etc.) from a `repl_eval` call; it fails loud rather than deadlocking the game. Do
async / per-frame work from a hook or coroutine.
