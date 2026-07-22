# refstubs — game-free compile references for the inutil engine

inutil is a game-**agnostic** IL2CPP hook engine, but its managed assemblies bind a few il2cpp BCL/Unity
proxy types that Il2CppInterop normally **generates from a booted game** (`Il2Cppmscorlib.dll`,
`UnityEngine.CoreModule.dll`). These ref-stubs let the engine compile with **no game** — so it is
CI-buildable from source — while still binding the user's **real** regenerated proxies at runtime.

## How it works

Each stub is a tiny assembly with the **same assembly name + type full names + member signatures** that
Il2CppInterop emits (verified against a real generated `interop/` — see "maintaining"), but with
`throw null` bodies (the reference-assembly idiom). The engine compiles against the stubs; the emitted IL
references each member **by name + signature**, so at runtime the loader resolves them against the user's
real `Il2Cppmscorlib` / `UnityEngine.CoreModule`. The stubs are **`Private=false`** — a compile reference
only, never deployed or shipped.

Build the engine game-free with `-p:RefStubs=true -p:RefStubsDir=<built stubs dir>`: the interop
`Reference` items in `Inutil.csproj` / `Inutil.BepInEx.csproj` then swap to the stubs, while the
Il2CppInterop **core** stays the loader's real one (game-free, from the pinned BepInEx zip). The
OpenTarkov engine repo's `pack.sh` uses this to build the shipped Engine tier with no booted overlay;
`-p:RefStubs` flows through ProjectReferences, so building `Inutil.Mods`/`Inutil.BepInEx` in stub mode
rebuilds `Inutil` consistently.

## What's stubbed (only what the engine references)

- **`Il2Cppmscorlib`** — `Il2CppSystem.{Object, ValueType, Type, Nullable<T>, Reflection.MethodInfo,
  Collections.Generic.Dictionary<K,V>}`. The v2 surface: `Object(IntPtr)` (Fields' erased-handle mint),
  `Type` + `.IsValueType` (the §7.7 generic-inflation path + `Json`'s deserialize seam), `typeof(Nullable<>)`
  (Hooks' patched-Nullable ABI sizing — the C6.2-tied token), `MethodInfo.{GetGenericArguments,
  MakeGenericMethod}` (inflation), and the `Dictionary<K,V>` spelling `Json.ToDict` deserializes into.
- **`UnityEngine.CoreModule`** — `UnityEngine.{Object, Component, Behaviour, MonoBehaviour, GameObject,
  HideFlags}` (the BepInEx host plugin's injected main-thread pump). The `HideFlags` enum carries the
  **exact Unity values** because the compiler inlines them into the plugin's IL. (The plugin's
  `interop/UnityEngine.dll` reference is normal-mode only — every bound type lives in CoreModule.)

These are standard .NET BCL / Unity API surface — **no game-derived data**.

## Maintaining the stubs

If the engine starts referencing a **new** member/type from these proxy assemblies, the game-free build
breaks with a `CS1061`/`CS0246` — add the member, matching the real signature. Read the real signatures
off a live/booted tree: reflection over the loaded proxies (the REPL one-liner
`typeof(Il2CppSystem.Reflection.MethodInfo).GetMethods()...`), or Mono.Cecil over a booted
`BepInEx/interop/`. Every signature in these stubs was extracted that way (live EFT proxies, 2026-07).
Runtime correctness is then verified by booting the stub-built engine against a real game (the OpenTarkov
maintainer's validation gate).
