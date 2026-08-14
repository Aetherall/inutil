# REPL — a live C# prompt against the running game

*A developer tool, not part of the engine. Read [the system map](../02-system-map.md) first. The
mod-author usage side is [guide/6](../../guide/06-repl.md) — link, don't duplicate. It shares the Roslyn
compiler and `MainThread` with [mod-host (15)](./15-mod-host.md), and a submission drives the same
[hook engine (13)](./13-hook-engine.md) a `.cs` mod does.*

## Job

`Inutil.Repl` compiles and runs one typed C# **submission** — "a line a user typed" — against the *live
loaded world*, in-process, on the loader's main (il2cpp-attached) thread, and chains session state so the
next submission sees the `var`s the last one declared. A line can name the Il2CppInterop proxies (`Player`),
the typed SDK (`Hooks`), and register a hook that fires *immediately* — because it runs inside the running
runtime, not against a rebuilt copy of it. The effect is a compile-deploy loop collapsed to a keystroke.

It owns **no** new engine: it is a Roslyn scripting front-end pointed at everything the game already loaded,
plus a loopback socket to type into. Everything a submission touches — hooks, marshalling, proxies — is the
same machinery a compiled mod uses.

## In the tree

`managed/src/Inutil.Mods/Repl*.cs` — in `Inutil.Mods.dll`, **not** the SDK, because it drags in the Roslyn
scripting closure (the same reason [`CsModCompiler`](./15-mod-host.md) lives there).

| File | Holds |
|---|---|
| `Repl.cs` | `ReplSession` — the long-lived Roslyn interpreter (`CSharpScript`, `ScriptState` chaining, the §7.12 deadlock guard); `ReplResult` (the per-submission outcome struct); `ReplBootstrap` (preloads the Roslyn closure) |
| `ReplTransport.cs` | the internals every transport shares, factored to ONE home so the faces cannot drift: `ReplDispatch.EvalOnMain` (the main-thread dispatch contract) + `MiniHttp` (the raw-TcpListener HTTP/1.1 reader/writer — http.sys is unreliable under Wine) |
| `ReplServer.cs` | the loopback-only TCP transport (a human at telnet) — one `ReplSession` per connection, background accept/read threads dispatching each line to the main thread |
| `ReplMcpServer.cs` | the SSE MCP transport (an agent by URL — `claude mcp add --transport sse …`) — one `ReplSession` per SSE connection, one tool `repl_eval` |
| `ReplHttpServer.cs` | the plain-HTTP JSON transport (a shell script) — `POST /eval` → `{"ok":bool,"result":string}`, ONE lazy `ReplSession` for the server's lifetime (created on first eval so it sees the fully-loaded world; state chains across POSTs) |

All three `Start(port, proxyNamespace, log, extraImports)` faces pass `extraImports` through to `ReplSession`
(seed a host-specific namespace — e.g. a game SDK — into every submission's usings).

## Design

**`ReplSession` is a long-lived scripting interpreter.** `Eval(code)` runs the first submission with
`CSharpScript.RunAsync` and every later one with `_state.ContinueWithAsync` — so a `ScriptState<object>`
threads through, and submission N sees submission N-1's declarations. That chaining *is* the REPL semantic;
without it each line would be an independent eval.

**It references the running runtime, so a line needs no wiring.** The constructor builds its
`MetadataReference` set from two sources, deduped by simple assembly name: (1) the entire *running-runtime
BCL directory* (`RuntimeEnvironment.GetRuntimeDirectory()`), so a submission can use **any** BCL type, not
only what happens to be loaded — a native PE (`coreclr`/`clrjit`) carries no ECMA metadata and would defer to
a compile-time `CS0009`, so `IsManaged` pre-filters by reading the PE header (the same check `CsModCompiler`
uses); (2) every non-dynamic loaded assembly with a `Location` not already covered — the interop proxies,
`Il2CppInterop`, and `Inutil.dll` (`Hooks`). Every reference therefore shares the *running* runtime's BCL
identity, so a submission's `int` and its boxed `object` return come from the same CoreLib the scripting host
uses — no skew. (The interop-patch normalizes each proxy's `System.Private.CoreLib` ref to the same version;
that normalization is what lets a submission touch a proxy through the natural expression API at all —
[interop-patch (11)](./11-interop-patch.md).)

**The seeded imports** are the usings every line gets for free: `System`, `System.Linq`, `Inutil.Hooks`,
`Inutil.Sugar`, plus the game's proxy namespace (`ToyGame` under BepInEx, `Il2CppToyGame` under
MelonLoader) — so `Player`, `Hooks`, and `Introspect` resolve with no `using` typed by hand.

**`Eval` is synchronous by design, and that is correct.** A no-`await` submission runs to completion inline.
It **must**: the calling thread has to be the loader's main il2cpp-attached thread, because that is where a
line's proxy touches (`player.GetHealth()`) are legal. Errors are *captured*, not thrown —
`CompilationErrorException` becomes the joined diagnostics, any other exception becomes `Type: message` — and
the session state advances **only** on success (`_state = next` past the guard), so a failed line leaves the
prior good state intact and you just retype.

**The §7.12 deadlock guard — the one addition v1 lacked.** A synchronous `Eval` has exactly one hazard: a
line that *awaits* a primitive only the main thread's per-frame `Drain` can complete (`MainThread.NextFrame()`
/ `Post` / `Until`). On the main thread, blocking on that task hangs the game forever — the awaited primitive
completes only from `Drain`, which the blocked main thread can never reach. The guard is a two-part check —
`!task.IsCompleted && MainThread.OnMainThread` — that fails **loud** with a precise message ("did not complete
synchronously … do async work from a hook or coroutine") instead of hanging. That is option (a) of §7.12:
detect and throw at the awaiter boundary.

**`ReplServer` is a loopback-only transport.** `Start(port, proxyNamespace, log)` binds a
`TcpListener(IPAddress.Loopback, port)` (port `0` → an OS-assigned free port, read back from `Port`) and
throws if the scripting engine isn't deployed — no point listening for lines nothing can evaluate. The accept
loop runs on a background thread; each connection gets its own background read thread and its own
`ReplSession` (state chains within a connection; a disconnect resets it cleanly). Each line is **dispatched to
the main thread** via `MainThread.Post`, and the *background transport thread* blocks on the posted result —
the main thread runs the `Eval` during its normal `Drain` and returns. So the guard applies exactly once, in
`Eval`, on the thread that matters; the transport never needs its own copy. `Evaluate` refuses before the pump
is live (`MainThread.IsPumping`) rather than posting into a queue nothing drains.

**`ReplBootstrap` preloads the Roslyn closure.** `PreloadEngine` loads the scripting assemblies
(`System.Collections.Immutable` and `System.Reflection.Metadata` first, the `CodeAnalysis` assemblies on top)
into the plugin's *own* load context by path, **before** any Roslyn type is touched — because BepInEx
default-probes `plugins/` and binds the right copies, but MelonLoader routes every resolve through its own
resolver, which can bind the *wrong* `System.Collections.Immutable` and throw. The class references no Roslyn
type, so calling it can't trigger the very resolve it is getting ahead of. It returns whether scripting is
**available** here (the closure is deployed, or already loaded) — so a consumer *skips* cleanly rather than
throwing a `FileNotFoundException` when only `Inutil.dll` + the host were deployed.

## Invariants

- **Loopback only — `127.0.0.1`, never off the machine.** The socket evaluates arbitrary C# in-process;
  binding it anywhere reachable would be a standing remote-code-execution port. `ReplServer` binds
  `IPAddress.Loopback` and nothing exposes a way to widen it. It is a tool for the person at the keyboard.
- **The guard is the *one* place the await-deadlock shape is handled.** Because the transport always dispatches
  `Eval` to the main thread, `!task.IsCompleted && OnMainThread` inside `Eval` is the single site that sees —
  and fails loud on — a line awaiting a main-thread primitive. There is no second copy in the transport (P4:
  the deadlock fails loud, deliberately, not silently).
- **A bad submission does not advance `ScriptState`.** State is assigned only past the guard, on the success
  path; a compile error or a runtime throw is captured and leaves `_state` at the last good value.
- **Hooks a session registers are removable; submission assemblies are not.** Each `Hooks.Pre/Post` returns an
  `IDisposable` a REPL line can hold and dispose (verified in-game). Roslyn's submission assemblies, by
  contrast, persist for the session's life — a session is not collectible.

## Limits, defers & TODOs

*(From the `Repl.cs` header and [reference/limits.md](../../reference/limits.md) P1 — the feature is DONE; these are the honest
edges.)*

- **Sessions are not collectible — long-lived by design** (as `csi` / `dotnet-script`). Submission assemblies
  accumulate; a `:reset` that compiles into a collectible ALC and reclaims the memory is *future hardening*,
  not built.
- **Opt-in — the loader does not auto-start it.** A standing arbitrary-code socket on every launch would be
  wrong, so nothing opens a port for you; a mod/coremod calls `ReplServer.Start` deliberately (see
  [guide/6](../../guide/06-repl.md)).
- **No collectible-per-line isolation, no multi-line/paste protocol** — one trimmed line per submission over
  the socket, `:quit`/`:exit` to disconnect. The REPL is a probe, not an editor.

## Tests

- **Offline** — `managed/src/Inutil.Mods.Tests` (`Program.cs`, the "REPL engine + transport (§7.12)" block):
  the whole path is pure-managed, so Roslyn eval (`40 + 2` → `42`), state chaining (`bonus`), compile-error
  capture, the **deadlock guard** (an unfinished submission on the main thread fails loud, not hangs — a
  never-completing `TaskCompletionSource` as the offline-reliable stand-in), and the **socket round-trip** via
  main-thread dispatch all run with no game boot.
- **In-game** — `repl.eval.live-hook` in `managed/test/Battery/Cases/ReplCases.cs`, under **both** loaders:
  the one thing offline can't cover — a submission compiled at runtime that chains `bonus` across lines and
  registers a hook through the natural expression API (`Hooks.Post((Player p) => p.GetHealth(), …)`, touching
  the proxy directly) on the real `Player.GetHealth()`, which *fires* and is *removable*. Skips cleanly where
  `Inutil.Mods.dll` or the Roslyn closure isn't deployed. See [testing (20)](./20-testing.md).

## Why it's shaped this way

The REPL is **ported from v1's `Repl.cs`** — Roslyn `CSharp.Scripting`, `Eval(string)`, state chaining across
submissions, validated under both loaders — with one addition the v2 redesign required: the **deadlock
guard**. v1's synchronous `Eval` predated `MainThread`'s per-frame primitives; once a line could `await`
something only `Drain` completes, a synchronous main-thread `Eval` could hang the game outright. The fix is
not to make `Eval` async (that would move proxy touches off the main thread, where il2cpp forbids them) — it
is to keep `Eval` synchronous and *refuse* the one shape that can't complete that way. The `Repl.cs` header
carries the full threading rationale; read it before touching the guard, because the shape it prevents is a
silent, total hang, not a caught exception.
