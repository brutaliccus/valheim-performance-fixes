# Valheim Performance Fixes

One BepInEx plugin (`vpf.ValheimPerformanceFixes`) for Valheim: fewer frame hitches while exploring, faster joins and fewer save hitches. It merges what used to be five separate plugins. Their behavior is unchanged; they now share one config file and one entry in Configuration Manager.

This repository is **source only**. It does not include Valheim, Unity, BepInEx or third-party mod DLLs.

## Modules

| Module | Was | What it does |
|--------|-----|--------------|
| **RenderTrim** | RenderTrim 1.8.0 | Client-side render and CPU trimming: small-prop shadows, distant clutter and particle culling, distant tree shadows, smart idle GC, adaptive and combat modes, UI warm-up, audio routing, optional mod throttles. The **Quick Setup** preset slider sets the technical values, and the Smart GC IdleSec floor stays at 20 s. A/B hotkey: **Ctrl+F12**. |
| **Spawn Budget** | SpawnBudget 1.7.5 | Spreads heavy world loading across frames (spawn, zone, location, dungeon, heightmap and arrival budgets). |
| **Minimap Cache + Save Hitch Fix** | MinimapCache 1.1.2 | Keeps the generated minimap on disk so joins skip regeneration. It also fast-serializes explored-map data and dedupes or defers *periodic* saves; quit and logout always save. |
| **DropNSpawn Cache** *(optional)* | DropNSpawnCache 1.0.1 | Caching and pacing for the DropNSpawn mod. It only runs when DropNSpawn (`sighsorry.DropNSpawn`) is loaded. |
| **Riverheim Cache** *(optional)* | RiverheimCache 1.1.0 | Disk cache for Riverheim worldgen. It only runs when Riverheim (`dev.gurebu.riverheim.stable`) is loaded. |

## Configuration Manager

Everything lives in `BepInEx/config/vpf.ValheimPerformanceFixes.cfg`, sorted by feature:

- **0. Quick Setup**: the preset slider, plus "keep my settings on this PC"
- **0. General … 12. Drop Fixes, Adaptive, Combat Mode, Mod Throttles**: RenderTrim (same section names as before)
- **Spawn Budget - …**: General, Locations, Dungeon, Zones, Heightmap, Arrival, Location Hysteresis, Adaptive, Combat Mode, Diagnostics
- **Minimap Cache - …**: General, Diagnostics, 2. Save Hitch Fix
- **Optional - DropNSpawn Cache - …** / **Optional - Riverheim Cache - …**

### Upgrading from the separate plugins

1. Remove the old DLLs (RenderTrim, SpawnBudget, MinimapCache, DropNSpawnCache, RiverheimCache). If one is still installed, its module switches itself off and logs a warning, so nothing gets patched twice.
2. Start the game once. When the new .cfg does not exist yet, the old `.cfg` files in `BepInEx/config` are recognised by their content (section and key names, not file names) and copied in. Keys keep their names; only the sections get the module prefix shown above. The old files are only read, never changed. See [CONFIG-MAPPING.md](CONFIG-MAPPING.md).

## Layout

```
ValheimPerformanceFixes/
  Host.cs          BepInPlugin, module start-up, optional-module checks, section names
  Signatures.cs    section|key names of the old standalone configs (used to recognise them for migration)
  Common.cs        ModuleBehaviour base (shared Config/Logger) and first-run config migration
  Modules/         RenderTrim, SpawnBudget, MinimapCache (+ SaveFix)
  Optional/        DropNSpawnCache, RiverheimCache
```

## Build

You need the .NET SDK 6 or later, a Valheim install and BepInEx 5. Riverheim.dll is needed only to compile the optional Riverheim module; it is not loaded at runtime unless Riverheim is installed.

```powershell
$env:VALHEIM_MANAGED = "<Valheim>\valheim_Data\Managed"
$env:BEPINEX_CORE    = "<BepInEx>\core"
$env:BEPINEX_PLUGINS = "<BepInEx>\plugins"   # or pass -p:RiverheimDll=<path>\Riverheim.dll
dotnet build ValheimPerformanceFixes\ValheimPerformanceFixes.csproj -c Release
```

Copy `ValheimPerformanceFixes.dll` to `BepInEx/plugins/`.

## License

MIT. See [LICENSE](LICENSE).
