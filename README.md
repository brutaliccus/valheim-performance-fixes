# Valheim Performance Fixes

One BepInEx plugin (`vpf.ValheimPerformanceFixes`) for Valheim: fewer frame hitches while exploring, faster joins and fewer save hitches. It merges what used to be five separate plugins. Their behavior is unchanged; they now share one config file and one entry in Configuration Manager.

This repository is **source only**. It does not include Valheim, Unity, BepInEx or third-party mod DLLs.

## Modules

| Module | Was | What it does |
|--------|-----|--------------|
| **RenderTrim** | RenderTrim | Client-side render and CPU trimming: small-prop shadows, distant clutter and particle culling, distant tree shadows, smart idle GC, adaptive and combat modes, UI warm-up, audio routing, optional mod throttles. The **Quick Setup** preset slider sets the technical values, and the Smart GC IdleSec floor stays at 20 s. A/B hotkey: **Ctrl+F12**. |
| **Spawn Budget** | SpawnBudget | Spreads heavy world loading across frames (spawn, zone, location, dungeon, heightmap and arrival budgets). |
| **Minimap Cache + Save Hitch Fix** | MinimapCache | Keeps the generated minimap on disk so joins skip regeneration. It also fast-serializes explored-map data and dedupes or defers *periodic* saves; quit and logout always save. |
| **DropNSpawn Cache** *(optional)* | DropNSpawnCache | Caching and pacing for the DropNSpawn mod. It only runs when DropNSpawn (`sighsorry.DropNSpawn`) is loaded. |
| **Riverheim Cache** *(optional)* | RiverheimCache | Disk cache for Riverheim worldgen. It only runs when Riverheim (`dev.gurebu.riverheim.stable`) is loaded. |

## Configuration Manager

Everything lives in `BepInEx/config/vpf.ValheimPerformanceFixes.cfg`, sorted by feature:

- **0. Quick Setup**: the preset slider, plus "keep my settings on this PC"
- **0. Mod Overlap**: which features step aside for ValheimPerformanceOverhaul / FiresGhettoNetworkMod
- **0. General … 12. Drop Fixes, Adaptive, Combat Mode, Mod Throttles**: RenderTrim (same section names as before)
- **13. Map Fog, 14. Distant Terrain, 15. Teleport Terrain Prefetch, 16. Fade Settle**: RenderTrim additions
- **17. Loading Zones, 18. Terrain Worker**: world loading (client and server)
- **20. Allocation Cuts, 21. Terrain Mesh Reuse, 22. Object Pass, 23. Combat Housekeeping**: small per-frame savings (client only)
- **Spawn Budget - …**: General, Locations, Dungeon, Zones, Heightmap, Arrival, Location Hysteresis, Adaptive, Combat Mode, Diagnostics, Safety, Terrain Collider Bake, Slim Location Models
- **Minimap Cache - …**: General, Diagnostics, 2. Save Hitch Fix
- **Optional - DropNSpawn Cache - …** / **Optional - Riverheim Cache - …**

### Upgrading from the separate plugins

1. Remove the old DLLs (RenderTrim, SpawnBudget, MinimapCache, DropNSpawnCache, RiverheimCache). If one is still installed, its module switches itself off and logs a warning, so nothing gets patched twice.
2. Start the game once. When the new .cfg does not exist yet, the old `.cfg` files in `BepInEx/config` are recognised by their content (section and key names, not file names) and copied in. Keys keep their names; only the sections get the module prefix shown above. The old files are only read, never changed. See [CONFIG-MAPPING.md](CONFIG-MAPPING.md).

## Versions

### 2.1.0

New features since 2.0.0. Every one has its own switch in F1, and player builds still load and unload exactly like the unmodded game.

**World loading**
- One shared budget spreads objects, locations, dungeons and zones over several frames, so exploring new areas no longer freezes the game for a moment.
- Dungeon rooms are built at a steady pace and checked afterwards; interiors you leave are let go one at a time.
- Zones around you are set up back to back while the loading screen is black.
- Far terrain and grass are rebuilt over several frames, and the map fog is uploaded in parts.

**Big locations learned per PC**
- Every world location is timed when it appears. Slow ones are remembered on your PC; their assets load early behind loading screens and a hidden copy is made ready as you come near, so they appear almost for free.
- Hidden copies use a memory budget based on your RAM and are let go first when memory runs low.
- Lighter models for often-loaded locations, without the parts the game creates and then throws away.

**Terrain**
- Ground colliders are cooked on a background thread.
- The game's terrain worker no longer pauses after every tile, and unchanged terrain keeps its meshes.
- The ground at a teleport destination starts building during the fade.

**Loading, teleport and respawn screens**
- Memory clean-ups run behind these screens instead of while you play.
- The inventory, fonts and sounds are warmed up behind the first loading screen, so the first inventory open is smooth.
- The black screen stays up a moment longer after spawning or teleporting until nearby objects are ready.
- A character save that comes as the sleep or teleport screen ends is hidden behind it.

**Smaller per-frame savings**
- Fewer temporary allocations, the object create/remove pass is skipped while nothing nearby changed, quieter combat housekeeping, finished fade-in effects stop updating every frame, and noisy log lines are filtered.

**Working with other mods**
- New Mod Overlap section: for features that ValheimPerformanceOverhaul or FiresGhettoNetworkMod also offer, tick a box to hand that job to the other mod.
- DropNSpawn's sync work can run behind the loading screen.

### 2.0.0

- First Thunderstore release as HitchFix.
- Combines five earlier plugins into one: RenderTrim, Spawn Budget, Minimap Cache + Save Hitch Fix, and the optional DropNSpawn Cache and Riverheim Cache.
- One config file with a Performance-to-Quality preset slider on the first tab, and plain-language descriptions for every setting.
- Optional modules only switch on when DropNSpawn or Riverheim is installed.
- Settings from the old separate plugins are copied in automatically on first start.

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
