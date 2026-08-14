# 6. The REPL — poke the live game from a prompt

*Experiment against the running game with no compile-deploy loop. Previous: [4. Escape hatches](./04-escape-hatches.md) · Next: [7. Full leverage](./07-full-leverage.md).*

## The one-line model

> **Start a loopback TCP server from your mod, `telnet 127.0.0.1 <port>`, and type C# that runs
> *in-process against the live game* — call proxies, register hooks, chain state line to line.**

It's the fastest way to answer "what is this object?", "does this method do what I think?", or "will this
hook fire?" without an edit → save → reload cycle. It's the same Roslyn engine that compiles your `.cs`
mods, pointed at a socket.

## What it needs

The REPL carries Roslyn, so it lives in `Inutil.Mods.dll` + its scripting closure — the **same** deploy
you did in [chapter 1](./01-setup-first-mod.md) to get the no-build `.cs` loop. If those DLLs are beside
your plugin, the REPL is available; if only `Inutil.dll` was deployed, `ReplServer.Start` throws a clear
"scripting engine is not available here" rather than failing quietly.

## Starting it (opt-in)

The loader does **not** open a REPL port for you — that would be a standing arbitrary-code socket on every
launch. You start it deliberately, from a mod or coremod's `OnLoad`:

```csharp
using Inutil;
using Inutil.Repl;

public sealed class DevRepl : ILoad
{
    ReplServer? _server;
    public void OnLoad()
    {
        // port 0 = let the OS pick a free port (read _server.Port after). The namespace is your game's
        // proxy namespace — "ToyGame" under BepInEx, "Il2CppToyGame" under MelonLoader.
        _server = ReplServer.Start(0, "ToyGame", msg => Log(msg));
        Log($"REPL on 127.0.0.1:{_server.Port}");   // also printed as "[repl] listening on 127.0.0.1:<port>"
    }
}
```

A coremod (load-once, before the menu — see [authoring modes](../reference/authoring-modes.md)) is the
natural home for a dev REPL.

## Connecting

```
$ telnet 127.0.0.1 <port>
```

Now type C#. Each line is compiled against the live loaded world and run **on the game's main thread**, so
its proxy touches happen where il2cpp requires:

```csharp
> var p = UnityEngine.Object.FindObjectOfType<Player>();
> p.GetHealth()
100
> Inutil.Introspect.Dump(p)          // the escape-hatch faces are right there
...
> Hooks.Pre(typeof(Player), "GetHealth", System.Type.EmptyTypes, ctx => ctx.SetReturn(4342))
> p.GetHealth()
4342                                  // the hook you just registered is live
```

Two things make this ergonomic:

- **State chains.** A `var` you declare on one line is visible on the next — it's a session, not
  independent evals. A line that fails to compile or throws leaves the session state *unadvanced*, so you
  just retype it.
- **Usings are seeded.** Every line gets `System`, `System.Linq`, `Inutil.Hooks`, `Inutil.Sugar`, and your
  proxy namespace for free, plus references to the whole BCL and every loaded assembly — so you name
  `Player`, `Hooks`, and `Introspect` with no setup.

## The one rule: don't `await` a main-thread primitive

A REPL line runs synchronously on the main thread. If a line awaits something only the main thread's
per-frame drain can complete (e.g. `MainThread.NextFrame()`), blocking on it would hang the game forever.
The REPL detects exactly that and **fails loud** instead:

```
error: this REPL line did not complete synchronously — it is awaiting (e.g. a main-thread primitive like
MainThread.NextFrame()). ... Do async work from a hook or coroutine, not a REPL line.
```

So: synchronous experiments in the REPL; async/per-frame work belongs in a hook or an `ITick`.

## Letting an AI agent drive it (MCP)

The same REPL can be published as an **MCP server over SSE**, so an AI agent (Claude Code, claude.ai) can
evaluate C# in the live game with nothing to install — it just adds a remote MCP server by URL. The transport
is `Inutil.Repl.ReplMcpServer` (a sibling of `ReplServer`); the ready-to-deploy coremod is
[`mods/mcp-repl/`](../../mods/mcp-repl/README.md):

```csharp
ReplMcpServer.Start(0, "ToyGame", log);   // loopback only; logs the URL + the `claude mcp add` line
// → claude mcp add --transport sse inutil-repl http://127.0.0.1:<port>/sse
```

It exposes one tool, `repl_eval(code)`, running on the main thread with the same deadlock guard — so an agent
gets the full REPL (name proxies, register hooks, chain state) through a URL.

## Letting a shell script drive it (HTTP)

The third face is plain HTTP JSON — `Inutil.Repl.ReplHttpServer` — for the consumer the other two can't
serve: a shell script that wants request/response with no handshake:

```csharp
ReplHttpServer.Start(0, "ToyGame", log);   // loopback only; logs the /eval URL
```

```
$ printf '%s' 'player.GetHealth()' | curl -s --data-binary @- http://127.0.0.1:<port>/eval
{"ok":true,"result":"100"}
```

`POST /eval` with the C# as the body; the answer is `{"ok":true,"result":"<value>"}` or
`{"ok":false,"result":"<compile/runtime error>"}` — trivially consumed with `jq -r '.result'`. Unlike the
per-connection sessions above, this face keeps **one** session for the server's lifetime (created lazily on
the first eval), so state chains across independent `curl` calls exactly like one telnet session.

All three faces take an optional `extraImports` (e.g. `new[] { "MyGame.Sdk" }`) seeded into every
submission's usings alongside the proxy namespace.

## Security

The server binds **loopback only** (`127.0.0.1`) — deliberately, because it evaluates arbitrary C#
in-process. Never bridge it off the machine. It's a developer tool for the person at the keyboard.

## Lifetime & limits

- A session's Roslyn submission assemblies are **not collectible** — a REPL session is long-lived by
  design (like `csi`/`dotnet-script`). The **hooks** you register from it *are* removable (each
  `Hooks.Pre/Post` returns an `IDisposable`); only the submission assemblies persist.
- A collectible session (a `:reset` that reclaims memory) is *roadmap*, not built.

## Checkpoint

- ✅ you can start a loopback REPL from a mod/coremod and `telnet` in
- ✅ you can call proxies, dump objects, and register a live hook that fires — statefully, line to line
- ✅ you know the deadlock guard (no awaiting main-thread primitives) and the loopback-only rule

**Next → [7. Full leverage](./07-full-leverage.md)** — composing everything, and the honest map of where
inutil's edges are.
