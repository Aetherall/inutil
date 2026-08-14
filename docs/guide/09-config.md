# 9. Config — typed settings that apply live

*One seam, one file per mod, edits apply without a reload. The v1 `config.file` capability, back on v2's
lifecycle seams.*

## The seam

Implement `IConfigure` on any mod type and read typed keys with code-side defaults:

```csharp
public class Companion : ITick, IConfigure
{
    KeyCode _toastKey = KeyCode.F7;
    string _greeting = "hello";
    bool _loud;

    public void Configure(ModConfig cfg)
    {
        _toastKey = ParseKey(cfg.GetString("toastKey", "F7", "key that pops the toast"), KeyCode.F7);
        _greeting = cfg.GetString("greeting", "hello", "the toast text");
        _loud     = cfg.GetBool("loud", false);
        // also: cfg.GetInt / cfg.GetFloat — flat, typed leaf values (invariant-culture numerals)
    }
}
```

`Configure` fires **after instantiation, before `OnLoad`** — your settings are applied by the time your
hooks wire — and **re-fires on every on-disk edit of the file**, on the main thread, with **no reload and
no recompile**: save the file, and the next frame runs with the new values. Your one obligation:
`Configure` must be *re-appliable* (read keys, assign fields — which is what you'd write anyway).

## The file

`BepInEx/config/<modname>.cfg` (MelonLoader: `UserData/inutil-config/`), materialized **self-documented**
on first load from exactly what your `Configure` read:

```ini
## Settings for the 'ModdingCompanion' mod — created by inutil.
## Edits apply LIVE: every save re-fires the mod's Configure on the next frame (no restart, no reload).

[ModdingCompanion]

## key that pops the toast
# Setting type: String
# Default value: F7
toastKey = F7
```

It's the BepInEx cfg dialect on purpose: every BepInEx user can read it, and mod managers' structured
config editors understand it. Deleted files are re-created with defaults; a deleted or missing key reads
as the code default; **an unparsable value falls back to the default with a loud warning and your text is
left in the file for you to fix** — never clobbered. Keys the current build doesn't read are preserved
across rewrites.

## Semantics worth knowing

- **Per-mod, not per-class:** all of a mod's `IConfigure` implementers share one bag and one file (the
  mod's assembly name). A key read in two classes is one key.
- **Hot-reload:** the file is the durable state. A new generation re-runs `Configure` from it; statics
  dying with the old ALC is fine. On unload, the watcher entry is retired with the rest of the mod —
  a dead generation's `Configure` never re-fires.
- **The dev loop this unlocks:** edit the cfg, `game await` the effect — a live A/B with no reboot
  (`--set` env still exists for boot-scoped experiments; config is for everything the mod should own).
- **Values are single-line raw strings.** No nesting, no quoting — flat typed leaves cover every real
  consumer; if you need structure, that's a data file your mod reads itself, not config.

## Checkpoint

- ✅ a mod whose keys live in a self-documented cfg the user (and the mod manager UI) can edit
- ✅ you understand the re-fire contract: `Configure` is re-appliable and runs before `OnLoad`
- ✅ you know the fallback rules: missing → default, unparsable → default + warning, unknown → preserved
