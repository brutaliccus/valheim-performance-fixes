# Old config -> merged config mapping

Old files are recognised by their content (section and key names), not by file name, so no old plugin GUID is hardcoded. Key names are unchanged; only section names gain the module prefix shown in each table.

Key names are unchanged everywhere. Only the file and (for most modules) the section name change; RenderTrim sections are kept verbatim. On first run (merged .cfg missing) each old file present in BepInEx\config is copied in automatically; the old files are only read.

## Old RenderTrim config -> vpf.ValheimPerformanceFixes.cfg (Valheim Performance Fixes)

| Old section | New section | Keys |
|---|---|---|
| 0. General | 0. General | Enabled, ABToggleKey, StatsIntervalSec, PrefabCensusOn, PrefabCensusIntervalSec, PrefabCensus, PrefabCensusSec |
| 0. Quick Setup | 0. Quick Setup | Preset, AlsoSetSpawnBudget, LastAppliedPreset, KeepMySettingsOnThisPC, ForgetMySettings |
| 1. Shadows | 1. Shadows | SmallShadowsOff, SmallShadowMaxSize, SkipPrefabPrefixes |
| 10. Audio Routing | 10. Audio Routing | Enabled, RemapForeignMixers, AssignUnroutedToSfx |
| 11. Animation Fixes | 11. Animation Fixes | FixStuckAnimatorSpeed, GraceSec, LogResets, ResetDualMasteryLease |
| 12. Drop Fixes | 12. Drop Fixes | DisableMonsterMayhemDropCap, ExtraSlotsInsteadOfDisable, LogCapBypass |
| 1b. Tree Shadows | 1b. Tree Shadows | TreeShadowTrim, TreeShadowDistance, TreePrefixes |
| 2. Layer Cull | 2. Layer Cull | Enabled, Distances, AllowRaise |
| 3. Small Prop Cull | 3. Small Prop Cull | Enabled, Distance, HideHysteresis, PickableMaxSize, ExtraPrefixes |
| 3b. Grass | 3b. Grass | Override, Distance, DensityScale, PatchesPerFrame |
| 3c. VPO Scene LOD | 3c. VPO Scene LOD | FarDistanceOverride |
| 4. Particles | 4. Particles | ReplaceVpoParticleTags, CullDistance, RateFalloff, PauseCulledLooping |
| 5. GC | 5. GC | CollectInMenus, MenuMinOpenSec, HeadroomMB, MinGapSec, CollectWhenIdle, IdleSec (new in 2.1.1: LogEveryGc) |
| 5b. Smart GC | 5b. Smart GC | Enabled, IdleSec, MinGapSec, HeadroomMB, CombatRange, AllowInMapOrInventory, MoveEpsilon |
| 6. LOD | 6. LOD | LodBiasMultiplier |
| 6b. Auto Scale | 6b. Auto Scale | Enabled, TargetFps, MinLodBiasScale, MinFarDistanceScale, StepDown, StepUp, HoldSec, RecoverHoldSec, RecoverFpsMargin |
| 7. VPO Unload | 7. VPO Unload | DeferVpoAssetUnload, RunInPauseMenu, DeathScreenCountsAsLoading, TeleportGraceSec |
| 8. Loot Stacking | 8. Loot Stacking | EarlyAutoStack, StackThreshold, VanillaItemTerrainSnap |
| 8. Streaming | 8. Streaming | InWorldBackgroundLoadingLow |
| 9. UI Warm-up | 9. UI Warm-up | WarmupCanvases, InventoryPreload, InventoryPreloadCraftingPanel, InventoryPreloadGc, InventoryPreloadFallbackSec, InventoryPreloadSplit, InventoryPreloadPreGc, SpreadOverFrames |
| 9b. Loading Warm-up | 9b. Loading Warm-up | Enabled, BudgetMsPerFrame, EquippedWeaponEffects, PlayerEffects, ContainerGrid, CurrentBiome, WarmupAllShaders |
| 9c. Biome Prewarm | 9c. Biome Prewarm | Enabled, BudgetMsPerFrame, LookaheadDistance, MaxItemsPerFrame |
| Adaptive | Adaptive | Enabled, LenientRange, AggressiveRange, LowPressure, HighPressure, Strength, ShiftSec, ShowOverlay |
| Combat Mode | Combat Mode | Enabled, DetectRange, ExitDelaySec, EaseSec, PropDistance, TreeShadowDistance, ParticleCullDistance, NeverHideWithin |
| Mod Throttles | Mod Throttles | VPOLightCulling_Throttle, VPOLightCulling_EveryNFrames, ItemRarityVFX_FarSkip, ItemRarityVFX_Distance, AnimalFeeding_Throttle, AnimalFeeding_Distance, AnimalFeeding_EveryNFrames, ZenHoverItem_Throttle, ZenHoverItem_EveryNFrames, BAO_InspiredRunestone_Prewarm, FullRateInCombat |
| 15. Teleport Terrain Prefetch (new in 2.2.0) | 15. Teleport Terrain Prefetch | Enabled, MaxZones |
| 16. Fade Settle (new in 2.2.0) | 16. Fade Settle | Enabled |

## Old SpawnBudget config -> vpf.ValheimPerformanceFixes.cfg (Valheim Performance Fixes)

| Old section | New section | Keys |
|---|---|---|
| Adaptive | Spawn Budget - Adaptive | Enabled, LenientBudgetMs, AggressiveBudgetMs, LowPressure, HighPressure, Strength, RiseSec, FallSec, TargetFrameMs, PendDistStaleSec |
| Arrival | Spawn Budget - Arrival | Enabled, ArrivalBudgetMs, ArrivalRampSec, ArrivalDistantMaxPerFrame |
| Combat Mode | Spawn Budget - Combat Mode | Enabled, CombatBudgetMs, DetectRange, ExitDelaySec, EaseSec, CombatEnterHoldSec, CombatExitDelaySec |
| Diagnostics | Spawn Budget - Diagnostics | MirrorToLogOutput, LogSlowObjects, SlowObjectMs, ReportIntervalSeconds, TailSeconds, ListObjectsOverMs (new in 2.1.1: LogHitches, HitchMs, MeasureRoomLoadFrames) |
| Dungeon | Spawn Budget - Dungeon | SpreadRooms, StaggerRoomLoads, MaxRoomLoadsInFlight, SoftEntry, SoftEntryWallMs, RoomBudgetMs (new in 2.1.1: StrictWorldBudget, MaxWaitFrames, SnapInOwnFrame, LearnRoomCost, GlobalLoadsInFlight, BudgetLoadRequests) |
| General | Spawn Budget - General | BacklogAwareDistantSkip, NearBacklogThreshold, SettleGraceSec, Enabled, BudgetMs, PriorityRadius, PriorityMaxPerFrame, DeferDistantWhenOverBudget, MaxObjectsPerFrame, DistantMaxPerFrame |
| Heightmap | Spawn Budget - Heightmap | Enabled, HeightmapBudgetMs, CombinedFrameBudgetMs, ProtectRadius, MaxDeferFrames, SliceAllRegeneratePaths |
| Location Hysteresis | Spawn Budget - Location Hysteresis | Enabled, KeepLoadedSec, KeepExtraZones, ReloadCooldownSec, AlwaysReloadWithinMeters (new in 2.1.1: IncludeDungeons) |
| Locations | Spawn Budget - Locations | HoldOneFrameAfterLoad, PrewarmFromQueue, SplitHeavyLocationActivation, HeavyLocationMs, HeavyLocationTransforms, HeavyLocations, SoftCapHeavyActivation, PreInstantiateHeavy, SoftCapBudgetMs, SoftCapProxyCreateMs, SerializeProxySpawns, DelayWhenFrameSpawnOverMs, MaxDeferFrames, LocationMaxPerFrame, LocationDeferIfFrameOverMs |
| Safety (new in 2.1.1) | Spawn Budget - Safety | FlushBeforeSave, CommitOnComplete, HostSpawnsAtOnce, LeavePlayerBuildsVanilla, PlaceLocationsBeforeBuilds, SpreadDungeonInteriorsNearBuilds |
| Zones | Spawn Budget - Zones | PostponeIfLastFrameOverMs, ThrottleClientZoneSpawns, MaxDeferTicks |
| Terrain Collider Bake (new in 2.2.0) | Spawn Budget - Terrain Collider Bake | Enabled, FinishSameFrame |
| Slim Location Models (new in 2.2.0) | Spawn Budget - Slim Location Models | Enabled, MinDeadObjects, MinDeadShare, WorkMsPerFrame, IdleUnloadSec |

## Old MinimapCache config -> vpf.ValheimPerformanceFixes.cfg (Valheim Performance Fixes)

| Old section | New section | Keys |
|---|---|---|
| 2. Save Hitch Fix | Minimap Cache - 2. Save Hitch Fix | Enabled, CacheCompressedMap, FastMapSerialize, VerifyAtJoin, PrewarmIntervalSec, DedupWindowSec, DeferSaveInCombatSec, LogSaves |
| Diagnostics | Minimap Cache - Diagnostics | LogKeyDetails |
| General | Minimap Cache - General | BypassClientWorldVersionCheck, CacheRoot, MigrateOldCache |

## Old DropNSpawnCache config -> vpf.ValheimPerformanceFixes.cfg (Valheim Performance Fixes)

| Old section | New section | Keys |
|---|---|---|
| Diagnostics | Optional - DropNSpawn Cache - Diagnostics | LogStats |
| Features | Optional - DropNSpawn Cache - Features | CacheSignatures, RevalidateSeconds, SkipRedundantPickableReconcile, GuardCloneDropTable |
| Pacing | Optional - DropNSpawn Cache - Pacing | PaceRuntimeWork, RefreshSliceMs, DeferUntilAfterSpawn, DeferSec (new in 2.1.1: PauseDuringWorldBuilds) |

## Old RiverheimCache config -> vpf.ValheimPerformanceFixes.cfg (Valheim Performance Fixes)

| Old section | New section | Keys |
|---|---|---|
| General | Optional - Riverheim Cache - General | Enabled |
| Safety | Optional - Riverheim Cache - Safety | VerifyFirstLoad, VerifyEveryLoad, SamplePoints |

Keys marked "new in 2.1.1" did not exist in the old standalone plugins; they are created with their defaults. 2.1.1 default changes (only for keys not yet in the file): 5. GC HeadroomMB 96 -> 128, 5b. Smart GC HeadroomMB 64 -> 96, DropNSpawn DeferUntilAfterSpawn false -> true.

Keys and sections marked "new in 2.2.0" are created with their defaults. The Quick Setup slider (0. Quick Setup) now also sets `15. Teleport Terrain Prefetch MaxZones` (4 / 6 / 7) and, with AlsoSetSpawnBudget, `Spawn Budget - Slim Location Models WorkMsPerFrame` (1.5 / 2 / 3).
