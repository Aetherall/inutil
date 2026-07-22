# 1. Setup & your first mod

*From nothing to a live, hot-reloading `.cs` mod. Previous: [0. Orient](./00-orient.md) · Next: [2. Hooking](./02-hooking.md).*

By the end of this chapter your game's console prints `[mods] hello: compiled + loaded`, and editing a
`.cs` file re-loads it live. No `.csproj`, no build step for the mod itself.

> **Reference deploy.** Everything here is exactly what [`tools/wine/validate.sh`](../../tools/wine/validate.sh)
> does to stand inutil up in-game. If any path or command drifts, that script is the ground truth — it's
> run on every validation.

---

## What you need first

inutil sits on top of a loader; you install that the normal way, once:

1. **An IL2CPP Unity game.**
2. **A loader** — [BepInEx (IL2CPP)](https://docs.bepinex.dev/) *or* [MelonLoader](https://melonwiki.xyz/) —
   installed and **run at least once**, so Il2CppInterop generates the game's proxy assemblies. You can
   confirm this happened:

   | Loader | Proxies land in | Mods deploy to |
   |---|---|---|
   | BepInEx | `<game>/BepInEx/interop/` (`Assembly-CSharp.dll`, `UnityEngine.*.dll`, …) | `<game>/BepInEx/plugins/` |
   | MelonLoader | `<game>/MelonLoader/Il2CppAssemblies/` | `<game>/Mods/` |

   If that proxy folder is empty, run the game once under the loader before continuing.
3. **The .NET SDK** (to build inutil — see the honest note below; there's no packaged release yet).

---

## The pieces inutil deploys

Five artifacts go next to your loader. Four are required; one enables the no-build `.cs` loop this
chapter uses.

| File | What it is | Required? |
|---|---|---|
| `Inutil.BepInEx.dll` (or `Inutil.MelonLoader.dll`) | the **loader plugin** — the thin per-loader entrypoint the loader actually loads (a `BasePlugin` / `MelonMod` over the SDK) | ✅ always |
| `Inutil.dll` | the **SDK** — the shared, loader-agnostic runtime the plugin and your mods bind to (hook engine, marshaller, mod host) | ✅ always |
| `Inutil.Schema.dll` | the type-correspondence schema the SDK depends on | ✅ always |
| `inutil_core.dll` | the native hook engine (MinHook detours) the SDK `[DllImport]`s | ✅ always |
| `Inutil.Mods.dll` (+ its Roslyn DLLs) | the in-process C# compiler for the no-build `.cs` mod loop | ⬅ **needed for this chapter** |

The plugin and the SDK are **separate** assemblies: `Inutil.dll` is loader-agnostic and never loaded
directly by BepInEx/MelonLoader; the tiny `Inutil.BepInEx.dll` / `Inutil.MelonLoader.dll` shim is the
actual entrypoint, and everything else is shared verbatim across both loaders.

Without `Inutil.Mods.dll` the engine and pre-built-DLL mods still work — you just don't get the `.cs`
hot-reload loop. We use it here because it's the fastest path to a running mod.

---

## Build inutil from source

> **Honest state:** `v2-rewrite` has no packaged release. You build the DLLs from this repo and copy
> them. A distributable is roadmap. The commands below are the BepInEx variant; for MelonLoader build
> `managed/src/Inutil.MelonLoader` instead and deploy to `<game>/Mods/`. The authoritative
> build+deploy reference is always [`tools/wine/validate.sh`](../../tools/wine/validate.sh).

```bash
# 1. the loader plugin (Inutil.BepInEx.dll) — a project reference chain that also builds the SDK
#    (Inutil.dll, Inutil.Schema.dll) it binds. The loader is selected by which shim project you build.
dotnet build managed/src/Inutil.BepInEx -c Release

# 2. the no-build .cs mod host + its Roslyn closure (Inutil.Mods.dll)
dotnet build managed/src/Inutil.Mods -c Release

# 3. the interop-patch CLI (used in the next step)
dotnet build managed/src/Inutil.InteropPatch.Cli -c Release

# 4. the native hook engine (inutil_core.dll) — cross-compiled with the vendored mingw toolchain.
#    The reference invocation lives in validate.sh; see native/ for the CMake setup.
ninja -C native/build inutil_core
```

Outputs land under each project's `bin/Release/`. The native DLL is at `native/build/inutil_core.dll`.

---

## Deploy

Copy the five artifacts into the loader's plugin/mods directory (BepInEx shown):

```bash
DEPLOY="<game>/BepInEx/plugins"
cp managed/src/Inutil.BepInEx/bin/Release/Inutil.BepInEx.dll "$DEPLOY"/   # the loader plugin (entrypoint)
cp managed/src/Inutil/bin/Release/Inutil.dll                 "$DEPLOY"/   # the SDK
cp managed/src/Inutil/bin/Release/Inutil.Schema.dll          "$DEPLOY"/
cp managed/src/Inutil.Mods/bin/Release/*.dll                 "$DEPLOY"/   # Inutil.Mods.dll + Roslyn
cp native/build/inutil_core.dll                              "$DEPLOY"/
```

(MelonLoader: `DEPLOY="<game>/Mods"`.)

---

## Patch the proxies — once

This is the step that makes **natural typing** real: it rewrites the generated proxy DLLs in place so
their method signatures speak `Task<T>`/`int?`/`List<T>` instead of `Il2CppSystem.*`. Run it once after
install, and again after any game update (which regenerates the proxies).

```bash
# --game auto-detects the loader layout under the game dir and patches its proxy folder:
dotnet managed/src/Inutil.InteropPatch.Cli/bin/Release/net9.0/inutil-interoppatch.dll --game "<game>"
```

You'll see it report what it flipped:

```
>> --game <game>: detected BepInEx
   proxies:  <game>/BepInEx/interop
>> patching proxies in <game>/BepInEx/interop
== patched 12 DLL(s), 480 member(s) flipped; 30 unchanged, 3 non-.NET ==
```

It's **idempotent** — running it on an already-patched folder is a safe no-op. If you skip this step,
inutil notices at startup and warns you (`inutil: interop proxies look unpatched…`) rather than failing
silently — the [fail-loud promise](./00-orient.md#the-fail-loud-promise) in action.

*(There's a second CLI, `inutil-metadata-extract`, which recovers a game's real serialized field names into
a sidecar `inutil-interoppatch` can re-attach to the patched proxies — see
[architecture/16-metadata](../contribution/architecture/16-metadata.md); most mods never need it.)*

---

## Your first mod

Launch the game once with inutil deployed. In the loader's console you'll see the mod loop come up:

```
[mods] watching <game>/BepInEx/inutil-mods (no mods yet — drop a <name>/*.cs to go live)
```

That folder is the drop zone. Each **subfolder** is one mod; every `.cs` in it is compiled together.
Create `<game>/BepInEx/inutil-mods/hello/Hello.cs`:

```csharp
using Inutil;          // ILoad, ITick — the mod lifecycle interfaces
using System.IO;

// A mod is just a public class that implements one or more lifecycle interfaces. inutil discovers it,
// creates one instance, and drives it. No attributes, no registration call, no entry point.
public sealed class Hello : ILoad, ITick
{
    static readonly string Log = Path.Combine(Path.GetTempPath(), "inutil-hello.log");
    int _frames;

    public void OnLoad() => File.AppendAllText(Log, "hello from inutil — loaded\n");   // fires once, on load

    public void Tick()                                                                  // fires every frame
    {
        if (++_frames % 300 == 0)
            File.AppendAllText(Log, $"still ticking: {_frames} frames\n");
    }
}
```

Within about a second, the console prints:

```
[mods] hello: compiled (142ms) + loaded — 0 hook/lifecycle wired (gen 1)
```

> The `0 hook/lifecycle wired` counts **hooks** — `Hello` has none yet (hooks come in
> [chapter 2](./02-hooking.md)). Its `ILoad`/`ITick` are still registered and running; the proof is the
> file. Tail it:
>
> ```bash
> tail -f "$TMPDIR/inutil-hello.log"   # "hello from inutil — loaded", then "still ticking: …" every ~5s
> ```

**Why a file, not a print?** There's no dedicated cross-loader log call for mods yet, so a file is the
simplest channel that works identically under both loaders (it's exactly what the in-game test mod uses).
The `[mods] …` lines themselves come from the host and *do* show in the loader console.

---

## See it hot-reload

With the game still running, edit `Hello.cs` — change the message, save. Within ~1s (a poll tick + a
250ms debounce) the console prints a **new** load line:

```
[mods] hello: compiled (61ms) + loaded — 0 hook/lifecycle wired (gen 2)
```

`gen 2` — the old instance was torn down (its `OnUnload` fired if it had one), the ALC unloaded, and the
fresh compile loaded in its place. No game restart. That's the edit loop.

## See it fail loud

Introduce a deliberate error — delete a `;` and save. Instead of a broken mod or a crash:

```
[mods] hello: 1 compile error(s) (refs=…) — keeping previous generation:
[mods]   (12,5): error CS1002: ; expected
```

The **previous working generation stays live**. Fix the error, save, and it reloads clean. A mod that
can't compile never replaces one that can.

Because the old generation keeps running, a failed edit makes *no visible change in-game* — which can
read as "my edit did nothing" rather than "my edit didn't compile". The engine records every
compile/load outcome into `Inutil.Diagnostics` (a game-free store: `Since(seq)` / `ActiveFailures()`),
so a host can surface failures however it likes. In OpenTarkov the dev `moderrors` coremod polls it and
pops an on-screen toast with the mod name + first error the moment a hot-reload fails — so you see it
without watching the log. (`game status` also reports `DEGRADED` for the agent/CI path.) A generic
on-screen renderer for other inutil hosts is a follow-on; the store + the outcome events are the engine
contract.

---

## Checkpoint

You now have:

- ✅ inutil deployed and attached (`[mods] watching …` at boot)
- ✅ the proxies patched for natural typing (`== patched … member(s) flipped ==`)
- ✅ a live mod whose `OnLoad`/`Tick` run, hot-reloading on save
- ✅ a feel for the fail-loud edit loop (compile errors keep the last good build)

What you *haven't* done yet is the point of it all: **change what the game does.** That's a hook.

**Next → [2. Hooking](./02-hooking.md)** — write a `Hook<Game>`, name a method like the game's own, and
call `Proceed<T>()` to run the original with your changes.
