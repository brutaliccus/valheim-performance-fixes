using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Scripting;

namespace RenderTrim
{
    public class Plugin : global::ValheimPerformanceFixes.ModuleBehaviour
    {
        public const string GUID = "vpf.ValheimPerformanceFixes.RenderTrim";
        internal static Plugin I;

        // General
        ConfigEntry<bool> master; ConfigEntry<KeyboardShortcut> abKey; ConfigEntry<float> statsSec; ConfigEntry<bool> census; ConfigEntry<float> censusSec;
        // Shadows
        internal static ConfigEntry<bool> shadowsOn; internal static ConfigEntry<float> shadowMaxSize; internal static ConfigEntry<string> shadowSkipNames;
        // Tree shadows
        internal static ConfigEntry<bool> treeOn; internal static ConfigEntry<float> treeDist; internal static ConfigEntry<string> treePrefixes;
        // Layer cull
        ConfigEntry<bool> layerOn; ConfigEntry<string> layerSpec;
        // Small prop cull
        internal static ConfigEntry<bool> propOn; internal static ConfigEntry<float> propDist; internal static ConfigEntry<float> propPickableMaxSize; internal static ConfigEntry<string> propPrefixes;
        // Particles
        internal static ConfigEntry<bool> partOn; internal static ConfigEntry<float> partDist; internal static ConfigEntry<bool> partFalloff; internal static ConfigEntry<bool> partPause;
        // GC
        ConfigEntry<bool> gcMenuOn; ConfigEntry<float> gcMenuMinOpen; ConfigEntry<float> gcHeadroomMB; ConfigEntry<float> gcMinGapSec; ConfigEntry<bool> gcIdleOn; ConfigEntry<float> gcIdleSec;
        ConfigEntry<bool> gcSmartOn, gcSmartUi; ConfigEntry<float> gcSmartIdleSec, gcSmartGapSec, gcSmartHeadroomMB, gcSmartCombatRange, gcMoveEps; ConfigEntry<bool> warmOn, invPreOn, invPreCraft, invPreGc; ConfigEntry<float> invPreFallbackSec;
        // VPO unload deferral
        internal static ConfigEntry<bool> vpoDeferOn, deathLoad; static ConfigEntry<bool> invPreSplit, invPrePreGc; internal static ConfigEntry<float> vpoTeleportGrace;
        // Loot stacking
        internal static ConfigEntry<bool> stackOn; internal static ConfigEntry<int> stackThreshold; internal static ConfigEntry<bool> terrainSnapOn;
        // LOD
        ConfigEntry<float> lodMul;
        // 1.6.0 live ranges + combat mode
        internal static ConfigEntry<float> propHyst, grassDist, grassDensity, lodFar, cmbDetect, cmbExit, cmbEase, cmbProp, cmbTree, cmbPart, cmbKeep; internal static ConfigEntry<bool> grassOn, cmbOn; internal static ConfigEntry<int> grassPerFrame; ConfigEntry<bool> layerRaise;
        bool propDirty, layerDirty, grassDirty = true; float nextVpoLod; System.Reflection.FieldInfo vpoLodField; bool vpoLodLooked; float vpoLodOrig = -1f;
        bool grassApplied; float grassOrigDist = -1f, grassOrigScale = -1f; ClutterSystem grassFor;
        internal static ConfigEntry<bool> vpoInMenu, bgLoad; static int bgState = -1; static ConfigEntry<bool> adOn, adOverlay; static ConfigEntry<float> adLen, adAgg, adLow, adHigh, adStr, adShift;
  static float Ad(float normal, bool isProp) { if (!adOn.Value) return normal; float r = Adaptive.range; if (isProp) return Mathf.Max(cmbKeep.Value, r); return Mathf.Max(cmbKeep.Value, normal * r / Mathf.Max(1f, adLen.Value)); }
  void OnGUI() { if (!adOverlay.Value) return; GUI.Label(new Rect(10, 10, 700, 24), "RenderTrim " + (Active ? "ON" : "OFF(A/B)") + " | pressure " + Adaptive.pressure.ToString("0.00") + (Adaptive.fromSB ? " (SpawnBudget)" : " (local)") + " | prop " + Eff(Ad(propDist.Value, true), cmbProp.Value).ToString("0") + "m tree " + Eff(Ad(treeDist.Value, false), cmbTree.Value).ToString("0") + "m part " + Eff(Ad(partDist.Value, false), cmbPart.Value).ToString("0") + "m | " + (Combat.factor > 0f ? "COMBAT " + Combat.factor.ToString("0.00") : "normal")); }
  static float Eff(float normal, float combat) { if (!cmbOn.Value || Combat.factor <= 0f) return normal; float c = Mathf.Max(combat, cmbKeep.Value); return Mathf.Lerp(normal, Mathf.Min(normal, c), Combat.factor); }

        internal static bool Active => I != null && I.master.Value && !I.abOff;
        bool abOff; Camera camC;
        Harmony harmony;
        internal static Type vpoTagType;
        StreamWriter log;

        // state
        internal class ShadowRec { public Renderer r; public ShadowCastingMode mode; }
        internal static readonly List<ShadowRec> shadowRecs = new List<ShadowRec>();
        internal class PropRec { public Transform t; public Renderer[] rs; public bool[] disabledByUs; public bool culled; }
        internal static readonly List<PropRec> props = new List<PropRec>();
        internal class PartRec { public ParticleSystem ps; public Transform t; public bool origEmit; public float origRate; public bool culled; public bool paused; }
        internal static readonly List<PartRec> parts = new List<PartRec>();
        internal class TreeRec { public Transform t; public Renderer[] rs; public ShadowCastingMode[] modes; public bool trimmed; }
        internal static readonly List<TreeRec> trees = new List<TreeRec>();
        internal static int statTreeTrimmedCasters, statTreesTrimmed;
        bool treesWereOn;
        int propIdx, partIdx, treeIdx;
        float[] savedLayerCull; Camera layerCam; float nextLayer;
        float origLodBias = -1; // unused since 1.7.4
        internal const string Ver = "1.8.0"; // smartIdle HeadroomMB 64, MinGapSec 120; IdleSec floor 20; never move/combat
        float baseBias = -1f, appliedBias = -1f;
        static ConfigEntry<bool> asOn, warmSpread; static ConfigEntry<float> asFps, asMinLod, asMinFar, asDown, asUp, asHold, asRecHold, asMargin;
        internal static float asLod = 1f, asFar = 1f; float asEma = -1f, asNext, asLowSince = -1f, asHighSince = -1f; int asFrames; float asAccum;
        float nextStats, nextCensus = 900f;
        int gcCountSeen; long usedAfterGc; float lastGcForced = -999f; int forcedMenu, forcedIdle, naturalGcs;
        float menuOpenedAt = -1f; Vector3 lastPos; float stillSince;
        internal static int statShadowOff, statPropCulledNow, statPartCulledNow, statPartPausedNow, statTagsReplaced;

        void Awake()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || Application.isBatchMode) { try { DropFix.Init(Config, new Harmony(GUID), s => Logger.LogInfo(s)); } catch (Exception e) { Logger.LogWarning("[Drop Fixes] init failed: " + e.Message); } Logger.LogInfo("RenderTrim: headless/dedicated server - disabled (only [12. Drop Fixes] runs)"); enabled = false; return; }
            I = this;
            master = Config.Bind("0. General", "Enabled", true, "Master switch for every RenderTrim feature.");
            abKey = Config.Bind("0. General", "ABToggleKey", new KeyboardShortcut(KeyCode.F12, KeyCode.LeftControl), "Toggles ALL RenderTrim features off/on at runtime (A/B test). Each toggle is logged with a timestamp so FrameSpikeLogger 30 s summaries can be compared.");
            statsSec = Config.Bind("0. General", "StatsIntervalSec", 30f, "Seconds between RenderTrim stat lines in BepInEx\\RenderTrim.log.");
            census = Config.Bind("0. General", "PrefabCensusOn", false, "1.4.0 (new key, old PrefabCensus is ignored): periodically log top prefabs by renderers / shadow casters / particle systems. Off by default: each census is a 35-45 ms spike.");
            censusSec = Config.Bind("0. General", "PrefabCensusIntervalSec", 900f, new ConfigDescription("Seconds between prefab census (min 900).", new AcceptableValueRange<float>(900f, 86400f)));

            shadowsOn = Config.Bind("1. Shadows", "SmallShadowsOff", true, "Disable shadow casting on small, non-skinned MeshRenderers of world objects (not characters, not items).");
            shadowMaxSize = Config.Bind("1. Shadows", "SmallShadowMaxSize", 0.8f, "Largest bounds dimension (m) for a renderer to count as small.");
            shadowSkipNames = Config.Bind("1. Shadows", "SkipPrefabPrefixes", "", "Comma-separated prefab prefixes never touched.");

            treeOn = Config.Bind("1b. Tree Shadows", "TreeShadowTrim", true, "Turn shadow casting off on tree renderers (all LODs) beyond TreeShadowDistance; restored when they come back in range. Time-sliced (whole list ~once per second), no per-frame scans.");
            treeDist = Config.Bind("1b. Tree Shadows", "TreeShadowDistance", 70f, new ConfigDescription("Meters from camera beyond which trees stop casting shadows. Live (applies within ~1 s). Higher = nicer, more GPU shadow cost.", new AcceptableValueRange<float>(20f, 300f)));
            treePrefixes = Config.Bind("1b. Tree Shadows", "TreePrefixes", "Beech,Birch,Oak,Pine,Fir,Willow,SwampTree,Poplar,YggaShoot,Ashlands_tree,Ashlands_Tree", "Prefab name prefixes treated as trees (anything with a TreeBase component also counts). Stumps/logs are not TreeBase and not matched unless listed.");
            layerOn = Config.Bind("2. Layer Cull", "Enabled", true, "Apply per-layer camera cull distances (only ever lowers the distance; 0 in Unity means far clip).");
            layerSpec = Config.Bind("2. Layer Cull", "Distances", "Default_small=160,item=100,effect=100,piece_nonsolid=160", new ConfigDescription("layer=meters list (Default_small = bushes, rocks, small vegetation). Live: on change the camera's original distances are restored and the list re-applied. VPO sets Default_small=140.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));
            layerRaise = Config.Bind("2. Layer Cull", "AllowRaise", true, "1.6.0: set the listed layers to exactly the given distance, even if another mod (VPO) set them LOWER. Off = only ever lower (pre-1.6 behaviour).");

            propOn = Config.Bind("3. Small Prop Cull", "Enabled", true, "Disable renderers (only renderers; colliders/ZDOs untouched) of small decorative props beyond Distance.");
            propDist = Config.Bind("3. Small Prop Cull", "Distance", 100f, new ConfigDescription("Meters from camera: props (pickables, ferns, bushes) are always SHOWN inside this range. Live: every tracked prop is re-evaluated on change. Lower = less GPU work, more visible pop-in.", new AcceptableValueRange<float>(30f, 300f)));
            propHyst = Config.Bind("3. Small Prop Cull", "HideHysteresis", 15f, new ConfigDescription("1.6.0: a shown prop is only hidden again once it is Distance + this far away, so props at the edge do not flicker as you move.", new AcceptableValueRange<float>(0f, 60f)));
            propPickableMaxSize = Config.Bind("3. Small Prop Cull", "PickableMaxSize", 2f, new ConfigDescription("Pickables up to this size (m) are culled. Applies to objects created after the change.", new AcceptableValueRange<float>(0.5f, 10f), new ConfigurationManagerAttributes { IsAdvanced = true }));
            propPrefixes = Config.Bind("3. Small Prop Cull", "ExtraPrefixes", "ormbunke,BushGreen_bal,Pickable_", new ConfigDescription("Additional prefab name prefixes treated as decorative props. Applies to objects created after the change.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));

            grassOn = Config.Bind("3b. Grass", "Override", true, "1.6.0: take control of vanilla ClutterSystem grass (VPO hard-codes distance x0.85 and density x0.8 at startup). Off = put back what VPO/vanilla set (live).");
            grassDist = Config.Bind("3b. Grass", "Distance", 50f, new ConfigDescription("Grass/ground-clutter draw radius in meters (vanilla 40, VPO makes that 34). Live: grass is rebuilt on change. Each +10 m costs roughly +50% grass instances.", new AcceptableValueRange<float>(20f, 120f)));
            grassDensity = Config.Bind("3b. Grass", "DensityScale", 1f, new ConfigDescription("Multiplier on grass amount (vanilla m_amountScale). VPO already cut amounts to 80%, so 1.25 = vanilla density. Live.", new AcceptableValueRange<float>(0.25f, 2f)));
            grassPerFrame = Config.Bind("3b. Grass", "PatchesPerFrame", 3, new ConfigDescription("Vanilla builds ONE 8 m grass patch per frame, so when you move fast or after a terrain rebuild grass fills in visibly around you. Build up to this many per frame (nearest first).", new AcceptableValueRange<int>(1, 10), new ConfigurationManagerAttributes { IsAdvanced = true }));

            lodFar = Config.Bind("3c. VPO Scene LOD", "FarDistanceOverride", 160f, new ConfigDescription("1.6.0: VPO forces every LODGroup (trees, rocks, bushes) to its lowest LOD and freezes it beyond 'Scene LOD Far Distance' (110 m). This overrides that distance live. 0 = leave VPO's value.", new AcceptableValueRange<float>(0f, 400f)));

            cmbOn = Config.Bind("Combat Mode", "Enabled", true, "1.6.0: while in combat (alerted hostile within DetectRange, or you took/dealt damage recently) use the tighter ranges below to free GPU/CPU. Checked 4x per second. Values ease in/out over EaseSec; props are re-checked over 0.5 s so hiding is spread out.");
            cmbDetect = Config.Bind("Combat Mode", "DetectRange", 30f, new ConfigDescription("Alerted hostile creatures within this many meters count as combat.", new AcceptableValueRange<float>(10f, 80f)));
            cmbExit = Config.Bind("Combat Mode", "ExitDelaySec", 5f, new ConfigDescription("Seconds without any combat signal before returning to normal ranges.", new AcceptableValueRange<float>(1f, 30f)));
            cmbEase = Config.Bind("Combat Mode", "EaseSec", 3f, new ConfigDescription("Seconds to blend between normal and combat ranges (both directions).", new AcceptableValueRange<float>(0.5f, 10f)));
            cmbProp = Config.Bind("Combat Mode", "PropDistance", 60f, new ConfigDescription("Small prop show distance in combat.", new AcceptableValueRange<float>(30f, 300f)));
            cmbTree = Config.Bind("Combat Mode", "TreeShadowDistance", 45f, new ConfigDescription("Tree shadow distance in combat.", new AcceptableValueRange<float>(20f, 300f)));
            cmbPart = Config.Bind("Combat Mode", "ParticleCullDistance", 50f, new ConfigDescription("Particle emission cull distance in combat.", new AcceptableValueRange<float>(20f, 300f)));
            adOn = Config.Bind("Adaptive", "Enabled", true, "1.7.0: out of combat, shift prop distance between LenientRange (low load) and AggressiveRange (high load); tree-shadow and particle distances scale by the same ratio. Pressure 0..1 comes from SpawnBudget (pending objects/zones/locations, frame time, instance count) or, if SpawnBudget is absent, from frame time alone. While on, LenientRange replaces '3. Small Prop Cull/Distance'. Combat Mode tightens on top. NeverHideWithin is always respected.");
    adLen = Config.Bind("Adaptive", "LenientRange", 120f, new ConfigDescription("Prop show distance at low pressure (near vanilla).", new AcceptableValueRange<float>(30f, 300f)));
    adAgg = Config.Bind("Adaptive", "AggressiveRange", 60f, new ConfigDescription("Prop show distance at high pressure.", new AcceptableValueRange<float>(30f, 300f)));
    adLow = Config.Bind("Adaptive", "LowPressure", 0.25f, new ConfigDescription("Pressure at/below which LenientRange is used.", new AcceptableValueRange<float>(0f, 1f)));
    adHigh = Config.Bind("Adaptive", "HighPressure", 0.75f, new ConfigDescription("Pressure at/above which AggressiveRange is used.", new AcceptableValueRange<float>(0f, 1f)));
    adStr = Config.Bind("Adaptive", "Strength", 1f, new ConfigDescription("How far toward AggressiveRange full pressure goes (0 = always lenient).", new AcceptableValueRange<float>(0f, 1f)));
    adShift = Config.Bind("Adaptive", "ShiftSec", 6f, new ConfigDescription("Seconds to glide the range to a new target (gradual shift while traveling).", new AcceptableValueRange<float>(1f, 30f)));
    adOverlay = Config.Bind("Adaptive", "ShowOverlay", false, "Top-left text overlay: pressure, effective prop/tree/particle distances, mode.");
    cmbKeep = Config.Bind("Combat Mode", "NeverHideWithin", 30f, new ConfigDescription("Hard floor: combat mode never hides props / tree shadows / particles closer than this.", new AcceptableValueRange<float>(10f, 60f)));

            partOn = Config.Bind("4. Particles", "ReplaceVpoParticleTags", true, "Replace VPO's per-particle-system Update (VPOParticleOptimizerTag, ~1400 instances) with one time-sliced manager doing the same emission culling.");
            partDist = Config.Bind("4. Particles", "CullDistance", 80f, new ConfigDescription("Emission off beyond this distance (VPO uses 80). Live.", new AcceptableValueRange<float>(20f, 300f)));
            partFalloff = Config.Bind("4. Particles", "RateFalloff", true, "Lerp emission rate to 40% toward the cull distance (VPO behaviour).");
            partPause = Config.Bind("4. Particles", "PauseCulledLooping", true, "Also Pause() culled looping systems once they have no live particles (saves simulation); resumed on approach.");

            gcMenuOn = Config.Bind("5. GC", "CollectInMenus", true, "Run a GC only behind a genuinely blocking screen (pause/ESC menu or popup, game paused, loading screen) if the heap grew by HeadroomMB since the last GC. Since 1.2.0 never for inventory, containers/tombstones, map, chat or console (you can still be hit while those are open).");
            gcMenuMinOpen = Config.Bind("5. GC", "MenuMinOpenSec", 1.5f, "The blocking screen must have been up continuously this long before a collection (hysteresis; avoids GCs on a quick ESC tap).");
            gcHeadroomMB = Config.Bind("5. GC", "HeadroomMB", 96f, "Minimum growth since last GC before an opportunistic collection.");
            gcMinGapSec = Config.Bind("5. GC", "MinGapSec", 20f, "Minimum seconds between forced collections.");
            gcIdleOn = Config.Bind("5. GC", "CollectWhenIdle", false, "Also collect after standing still for IdleSec (off by default).");
            gcIdleSec = Config.Bind("5. GC", "IdleSec", 4f, "");

            gcSmartOn = Config.Bind("5b. Smart GC", "Enabled", true, "1.8.0: clean up memory only at genuinely quiet moments so it never freezes gameplay. Never while moving (horizontal speed above MoveEpsilon), never in combat, and only after IdleSec of real stillness (or with map/inventory open). Needs heap growth above HeadroomMB and is rate-limited by MinGapSec. Loading-screen / teleport GCs are separate and unchanged. Off = no smart clean-ups.");
            gcSmartIdleSec = Config.Bind("5b. Smart GC", "IdleSec", 20f, new ConfigDescription("1.8.0: seconds of genuine stillness (no position change and horizontal speed below MoveEpsilon) before a smart clean-up may run. Default 20; Quick Setup preset never lowers this floor. Never during move/combat.", new AcceptableValueRange<float>(5f, 120f)));
            gcSmartGapSec = Config.Bind("5b. Smart GC", "MinGapSec", 120f, new ConfigDescription("1.8.0: minimum seconds between smart clean-ups (and since any forced clean-up). Default 120. Higher = fewer freezes while exploring.", new AcceptableValueRange<float>(15f, 600f)));
            gcSmartHeadroomMB = Config.Bind("5b. Smart GC", "HeadroomMB", 64f, "1.8.0: only run a smart clean-up if the heap has grown by at least this many MB since the last GC. Default 64 so small drifts while exploring do not trigger freezes.");
            gcSmartCombatRange = Config.Bind("5b. Smart GC", "CombatRange", 40f, "No smart GC while an alerted monster is within this many metres, or for 10 s after taking damage.");
            gcSmartUi = Config.Bind("5b. Smart GC", "AllowInMapOrInventory", true, "Also allow a smart GC while the large map or inventory is open (still not in combat, still not while moving).");
            gcMoveEps = Config.Bind("5b. Smart GC", "MoveEpsilon", 0.15f, new ConfigDescription("1.7.9: horizontal speed (m/s) above this counts as moving â€” smart GC never runs while moving. Small enough that standing still / tiny drift still counts as idle.", new AcceptableValueRange<float>(0.05f, 2f)));
            warmOn = Config.Bind("9. UI Warm-up", "WarmupCanvases", true, "1.3.0: once per session, while the loading screen is still opaque, activate the large map root for one frame-internal pass (SetActive true -> Canvas.ForceUpdateCanvases -> SetActive false, nothing is ever rendered) and force a canvas update for the inventory, so the first real open does not pay the whole layout/mesh rebuild.");
            invPreOn = Config.Bind("9. UI Warm-up", "InventoryPreload", true, "1.4.0: once per session, AFTER the local Player exists (ideally while the loading screen is still black, otherwise ~1 s after spawn), build the player inventory grid invisibly by calling InventoryGui.UpdateInventory(player) (the same path a real open uses, including other mods' patches), then Canvas.ForceUpdateCanvases. Never calls Show/Hide: no sounds, cursor or state changes. The inventory root is made active with a CanvasGroup alpha 0 / no raycasts only if it was inactive, and restored exactly.");
            invPreCraft = Config.Bind("9. UI Warm-up", "InventoryPreloadCraftingPanel", false, "1.4.0: also run InventoryGui.UpdateCraftingPanel during the preload (builds the recipe list). Off by default: other mods patch it and it touches tab state.");
            invPreGc = Config.Bind("9. UI Warm-up", "InventoryPreloadGc", false, "IGNORED since 1.4.1: the preload never forces a GC.");
            invPreFallbackSec = Config.Bind("9. UI Warm-up", "InventoryPreloadFallbackSec", 1f, "1.4.0: if the loading screen is already gone when the Player appears, run the preload this many seconds after spawn (only if not in combat and inventory closed).");
            vpoDeferOn = Config.Bind("7. VPO Unload", "DeferVpoAssetUnload", true, "Hold back ValheimPerformanceOverhaul's periodic Resources.UnloadUnusedAssets (GCPatches.TriggerSafeAssetUnload every 300 s, VPOMemoryManager cleanup every 10 min; 370-410 ms freezes) while you are in the world, and run it instead on a loading screen, right after a teleport/respawn, or behind a blocking menu. Vanilla's own unloads are untouched.");
            vpoInMenu = Config.Bind("7. VPO Unload", "RunInPauseMenu", false, "1.7.0: also run deferred VPO cleanup behind the pause menu. Off by default: the 20261025 log showed a 675 ms freeze (VPOMemoryManager.CleanupCoroutine + forced GC) in the ESC menu, and multiplayer keeps running behind it. When off, deferred cleanup runs only on loading screens (teleport/portal/respawn).");
        bgLoad = Config.Bind("8. Streaming", "InWorldBackgroundLoadingLow", true, "1.7.0: in world, set Application.backgroundLoadingPriority=Low (max ~2 ms/frame of async asset integration instead of 10-50 ms); High on loading screens. Targets the EarlyUpdate 65-107 ms hitches while traveling.");
        deathLoad = Config.Bind("7. VPO Unload", "DeathScreenCountsAsLoading", false, "1.7.7: false = the death screen (dead player, including its fade to black) is NOT a loading screen, so deferred VPO cleanup and the forced menu GC do not fire there (22:11 log: 636 ms GC + cleanup while dead); they run on the real respawn/teleport loading screen instead. true = 1.7.6 behaviour.");
        vpoTeleportGrace = Config.Bind("7. VPO Unload", "TeleportGraceSec", 3f, "Seconds after a teleport/respawn finishes during which a pending/teleport unload may still run.");
            stackOn = Config.Bind("8. Loot Stacking", "EarlyAutoStack", true, "Use vanilla's own ItemDrop auto-stack (same item, same quality, within 4 m, only drops YOU own, never exceeds the max stack) when more than StackThreshold drops exist instead of vanilla's 200. Item counts are unchanged; it only merges piles into fewer physics objects (each loose drop costs a Rigidbody + ZSyncTransform).");
            stackThreshold = Config.Bind("8. Loot Stacking", "StackThreshold", 60, new ConfigDescription("Loose ItemDrop count above which auto-stacking runs (vanilla 200).", new AcceptableValueRange<int>(10, 200)));
            terrainSnapOn = Config.Bind("8. Loot Stacking", "VanillaItemTerrainSnap", true, "1.7.3: run vanilla 1.0.20 ItemDrop.TerrainCheck (a drop more than 0.5 m below the terrain is moved to terrain + 0.5 m and stopped) instead of FiresGhettoNetworking's Fix Ground Snap Through Floors raycast, which treats ore veins and cliffs as ground (mined ore stuck under the surface, thrown onto the vein top or up a cliff). A drop resting on a building piece or ship (layers piece/vehicle only) is left alone. Live. Off = FGN/vanilla behaviour unchanged. Independent of the A/B key.");

            propDist.SettingChanged += (a, b) => propDirty = true; propHyst.SettingChanged += (a, b) => propDirty = true;
            layerSpec.SettingChanged += (a, b) => layerDirty = true; layerRaise.SettingChanged += (a, b) => layerDirty = true; layerOn.SettingChanged += (a, b) => layerDirty = true;
            grassOn.SettingChanged += (a, b) => grassDirty = true; grassDist.SettingChanged += (a, b) => grassDirty = true; grassDensity.SettingChanged += (a, b) => grassDirty = true;
            lodFar.SettingChanged += (a, b) => nextVpoLod = 0f;
            lodMul = Config.Bind("6. LOD", "LodBiasMultiplier", 1f, "Multiplies QualitySettings.lodBias (1 = unchanged; 0.8 = LODs switch ~20% earlier).");
            asOn = Config.Bind("6b. Auto Scale", "Enabled", true, "1.7.4: when the smoothed FPS stays below TargetFps, step lodBias and the VPO Scene LOD far distance down (to at most the Min scales), and step back up once FPS recovers. Held while loading, behind black/blocking screens and with no player.");
            asFps = Config.Bind("6b. Auto Scale", "TargetFps", 45f, new ConfigDescription("Scale down while the smoothed FPS is below this.", new AcceptableValueRange<float>(20f, 144f)));
            asMinLod = Config.Bind("6b. Auto Scale", "MinLodBiasScale", 0.6f, new ConfigDescription("Lowest lodBias scale auto-scaling may reach.", new AcceptableValueRange<float>(0.3f, 1f)));
            asMinFar = Config.Bind("6b. Auto Scale", "MinFarDistanceScale", 0.7f, new ConfigDescription("Lowest VPO far-distance scale auto-scaling may reach.", new AcceptableValueRange<float>(0.3f, 1f)));
            asDown = Config.Bind("6b. Auto Scale", "StepDown", 0.05f, new ConfigDescription("Scale removed per step down.", new AcceptableValueRange<float>(0.01f, 0.25f)));
            asUp = Config.Bind("6b. Auto Scale", "StepUp", 0.05f, new ConfigDescription("Scale added per step up.", new AcceptableValueRange<float>(0.01f, 0.25f)));
            asHold = Config.Bind("6b. Auto Scale", "HoldSec", 2f, new ConfigDescription("FPS must stay low this long before each step down.", new AcceptableValueRange<float>(0.5f, 30f)));
            asRecHold = Config.Bind("6b. Auto Scale", "RecoverHoldSec", 5f, new ConfigDescription("FPS must stay above TargetFps x RecoverFpsMargin this long before each step up.", new AcceptableValueRange<float>(1f, 60f)));
            asMargin = Config.Bind("6b. Auto Scale", "RecoverFpsMargin", 1.15f, new ConfigDescription("Recovery threshold multiplier on TargetFps (hysteresis).", new AcceptableValueRange<float>(1f, 2f)));
            invPreSplit = Config.Bind("9. UI Warm-up", "InventoryPreloadSplit", true, "1.7.7 (needs SpreadOverFrames): split the inventory preload over two frames: Show/Hide (crafting setup, ~250 ms) and the grid/stats/canvas pass (~200 ms), instead of one ~460 ms frame.");
            invPrePreGc = Config.Bind("9. UI Warm-up", "InventoryPreloadPreGc", true, "1.7.7 (needs SpreadOverFrames): if the loading screen is still black, run one GC on its own frame right before the inventory preload, so a natural GC (heap 700+ MB) does not land inside the preload frame (20:32 log: 1683 ms frame = preload + GC). Never runs when the screen is visible.");
            warmSpread = Config.Bind("9. UI Warm-up", "SpreadOverFrames", true, "1.7.4: run the warm-up steps (map, inventory preload, final canvas pass) on separate frames instead of one long frame; the final canvas pass is skipped unless the loading screen is still black.");

            vpoTagType = AccessTools.TypeByName("ValheimEngineOptimizer.Modules.VPOParticleOptimizerTag");
            try { log = new StreamWriter(Path.Combine(Paths.BepInExRootPath, "RenderTrim.log"), false) { AutoFlush = true }; } catch { }
            L("RenderTrim " + Ver + " start; vpoTag=" + (vpoTagType != null) + " gcIncremental=" + SafeInc() + " gcMode=" + GarbageCollector.GCMode + " lodBias=" + QualitySettings.lodBias);
            harmony = new Harmony(GUID); ModThrottles.Bind(Config); Prewarm.Bind(Config); Preset.Bind(Config, L); LocalStore.Bind(Config, L); AnimFix.Init(Config, harmony); try { DropFix.Init(Config, harmony, L); } catch (Exception e) { L("[Drop Fixes] init failed: " + e.Message); }
            harmony.PatchAll(typeof(Patches)); try { harmony.PatchAll(typeof(GrassBoost)); } catch (Exception e) { L("grass patch FAILED: " + e.Message); } Combat.Init(harmony);
            AudioRoute.Init(this, harmony);
            try { harmony.Patch(AccessTools.DeclaredMethod(typeof(InventoryGui), "Show"), postfix: new HarmonyMethod(typeof(InvPreload), nameof(InvPreload.ShowPost)) { priority = Priority.Last }); } catch (Exception e) { L("inventory first-open safety patch FAILED: " + e.Message); }
            VpoUnload.Init(harmony);
            try { harmony.Patch(AccessTools.DeclaredMethod(typeof(ItemDrop), "SlowUpdate"), postfix: new HarmonyMethod(typeof(LootStack), nameof(LootStack.Post))); L("loot auto-stack patch ok"); }
            catch (Exception e) { L("loot auto-stack patch FAILED: " + e.Message); }
            try { harmony.Patch(AccessTools.DeclaredMethod(typeof(ItemDrop), "TerrainCheck"), prefix: new HarmonyMethod(typeof(ItemSnap), nameof(ItemSnap.Pre)) { priority = Priority.First, before = new[] { "com.Fire.FiresGhettoNetworkMod" } }); L("item terrain snap patch ok (enabled=" + terrainSnapOn.Value + ")"); }
            catch (Exception e) { L("item terrain snap patch FAILED: " + e.Message); }
        }

        internal float MenuMinOpen => gcMenuMinOpen.Value;
        internal float BlockingFor() => menuOpenedAt < 0 ? 0f : Time.unscaledTime - menuOpenedAt;

        static string SafeInc() { try { return GarbageCollector.isIncremental.ToString(); } catch { return "?"; } }

        internal void L(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + s;
            Logger.LogInfo(s);
            try { log?.WriteLine(line); } catch { }
        }

        void Update()
        {
            if (abKey.Value.IsDown())
            {
                abOff = !abOff;
                L("A/B TOGGLE -> RenderTrim " + (abOff ? "OFF" : "ON"));
                if (abOff) RestoreAll(); else ReapplyAll();
            }
            if (!described) { described = true; try { Preset.Describe(Config); Config.Save(); LocalStore.Start(); } catch (Exception e) { L("describe failed: " + e.Message); } }
            Preset.Tick();
            if (master.Value && !abOff) { try { Prewarm.Tick(this, HudBlack()); } catch (Exception e) { L("Prewarm tick failed: " + e.Message); } }
            GcTick();
            VpoUnload.Tick();
            try { int want = !bgLoad.Value ? -2 : (LoadingScreen() ? 1 : 0); if (want != bgState) { if (want == 1) Application.backgroundLoadingPriority = ThreadPriority.High; else if (want == 0) Application.backgroundLoadingPriority = ThreadPriority.Low; else if (bgState >= 0) Application.backgroundLoadingPriority = ThreadPriority.Normal; bgState = want; } } catch { }
            GrassTick(); AutoTick(); VpoLodTick(); Combat.Tick(this); Adaptive.Tick(); ModThrottles.Tick(harmony);
            if (!Active) { if (statPropCulledNow != 0) foreach (var p in props) if (p.culled) SetProp(p, false); return; }
            if (camC == null || !camC.isActiveAndEnabled) camC = Camera.main; Camera cam = camC;
            if (cam == null) return;
            Vector3 cp = cam.transform.position;
            LayerTick(cam);
            PropTick(cp);
            TreeTick(cp);
            PartTick(cp);
            LodTick();
            float t = Time.unscaledTime;
            if (t >= nextStats) { nextStats = t + statsSec.Value; Stats(); }
            if (census.Value && Player.m_localPlayer != null && t >= nextCensus && t > 60f) { nextCensus = t + censusSec.Value; Census(); }
        }

        // ---------- registration (called from CreateObject postfix) ----------
        internal static void OnCreated(GameObject go)
        {
            if (go == null || I == null || !I.master.Value) return;
            try
            {
                string name = go.name; int k = name.IndexOf('('); if (k > 0) name = name.Substring(0, k);
                if (go.GetComponentInChildren<Character>(true) != null || go.GetComponent<ItemDrop>() != null) { RegisterParticles(go); return; }
                if (StartsAny(name, shadowSkipNames.Value)) return;
                RegisterShadows(go);
                RegisterTree(go, name);
                RegisterProp(go, name);
                RegisterParticles(go);
            }
            catch (Exception e) { I.Logger.LogWarning("OnCreated " + go.name + ": " + e.Message); }
        }

        static bool StartsAny(string name, string list)
        {
            if (string.IsNullOrEmpty(list)) return false;
            foreach (var p in list.Split(',')) { var q = p.Trim(); if (q.Length > 0 && name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return true; }
            return false;
        }

        static void RegisterShadows(GameObject go)
        {
            if (!shadowsOn.Value) return;
            var rs = go.GetComponentsInChildren<MeshRenderer>(true);
            float max = shadowMaxSize.Value;
            foreach (var r in rs)
            {
                if (r == null || r.shadowCastingMode != ShadowCastingMode.On) continue;
                Vector3 s = r.bounds.size;
                if (Mathf.Max(s.x, Mathf.Max(s.y, s.z)) > max) continue;
                shadowRecs.Add(new ShadowRec { r = r, mode = r.shadowCastingMode });
                if (Active) { r.shadowCastingMode = ShadowCastingMode.Off; statShadowOff++; }
            }
        }

        static void RegisterTree(GameObject go, string name)
        {
            if (!treeOn.Value) return;
            if (go.GetComponent<TreeBase>() == null && !StartsAny(name, treePrefixes.Value)) return;
            var all = go.GetComponentsInChildren<Renderer>(true);
            int n = 0;
            foreach (var r in all) if (r != null && !(r is ParticleSystemRenderer) && r.shadowCastingMode != ShadowCastingMode.Off) n++;
            if (n == 0) return;
            var rs = new Renderer[n]; var modes = new ShadowCastingMode[n]; int i = 0;
            foreach (var r in all) if (r != null && !(r is ParticleSystemRenderer) && r.shadowCastingMode != ShadowCastingMode.Off) { rs[i] = r; modes[i] = r.shadowCastingMode; i++; }
            trees.Add(new TreeRec { t = go.transform, rs = rs, modes = modes });
        }

        static void SetTree(TreeRec t, bool trim)
        {
            if (t.trimmed == trim) return;
            for (int i = 0; i < t.rs.Length; i++) { var r = t.rs[i]; if (r == null) continue; r.shadowCastingMode = trim ? ShadowCastingMode.Off : t.modes[i]; }
            t.trimmed = trim;
            int d = trim ? 1 : -1; statTreesTrimmed += d; statTreeTrimmedCasters += d * t.rs.Length;
        }

        static void RestoreTrees() { foreach (var t in trees) if (t.trimmed) SetTree(t, false); }

        void TreeTick(Vector3 cp)
        {
            if (!treeOn.Value) { if (treesWereOn) { RestoreTrees(); treesWereOn = false; } return; }
            treesWereOn = true;
            if (trees.Count == 0) return;
            int n = Mathf.Clamp(Mathf.CeilToInt(trees.Count * Time.unscaledDeltaTime / 1f), 1, trees.Count);
            float td = Eff(Ad(treeDist.Value, false), cmbTree.Value); float d2 = td * td;
            for (int c = 0; c < n && trees.Count > 0; c++)
            {
                if (treeIdx >= trees.Count) treeIdx = 0;
                var t = trees[treeIdx];
                if (t.t == null) { if (t.trimmed) { statTreesTrimmed--; statTreeTrimmedCasters -= t.rs.Length; } int last = trees.Count - 1; trees[treeIdx] = trees[last]; trees.RemoveAt(last); continue; }
                SetTree(t, (t.t.position - cp).sqrMagnitude > d2);
                treeIdx++;
            }
        }

        static void RegisterProp(GameObject go, string name)
        {
            if (!propOn.Value) return;
            bool cand = false;
            if (go.GetComponent<Pickable>() != null || StartsAny(name, propPrefixes.Value))
            {
                if (go.GetComponent<Piece>() != null || go.GetComponent<Container>() != null) return;
                var all = go.GetComponentsInChildren<Renderer>(true);
                if (all.Length == 0) return;
                Bounds b = all[0].bounds; foreach (var r in all) b.Encapsulate(r.bounds);
                Vector3 s = b.size;
                cand = Mathf.Max(s.x, Mathf.Max(s.y, s.z)) <= propPickableMaxSize.Value || StartsAny(name, propPrefixes.Value) && !name.StartsWith("Pickable_");
                if (!cand) return;
                var list = all.Where(r => !(r is ParticleSystemRenderer)).ToArray();
                if (list.Length == 0) return;
                props.Add(new PropRec { t = go.transform, rs = list, disabledByUs = new bool[list.Length] });
            }
        }

        static void RegisterParticles(GameObject go)
        {
            if (!partOn.Value || vpoTagType == null) return;
            var tags = go.GetComponentsInChildren(vpoTagType, true);
            foreach (var tag in tags)
            {
                var ps = tag.GetComponent<ParticleSystem>();
                if (ps == null) continue;
                var em = ps.emission;
                parts.Add(new PartRec { ps = ps, t = ps.transform, origEmit = em.enabled, origRate = em.rateOverTimeMultiplier });
                if (Active) { UnityEngine.Object.Destroy(tag); statTagsReplaced++; }
            }
        }

        // ---------- ticks ----------
        void LayerTick(Camera cam)
        {
            if (layerDirty) { layerDirty = false; nextLayer = 0f; if (layerCam != null && savedLayerCull != null) try { layerCam.layerCullDistances = (float[])savedLayerCull.Clone(); } catch { } }
            if (!layerOn.Value) return;
            if (cam == layerCam && Time.unscaledTime < nextLayer) return;
            nextLayer = Time.unscaledTime + 2f;
            float[] d = cam.layerCullDistances;
            if (cam != layerCam) { savedLayerCull = (float[])d.Clone(); layerCam = cam; }
            bool changed = false;
            foreach (var e in layerSpec.Value.Split(','))
            {
                var kv = e.Split('='); if (kv.Length != 2) continue;
                int layer = LayerMask.NameToLayer(kv[0].Trim()); if (layer < 0) continue;
                if (!float.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m) || m <= 0) continue;
                if (layerRaise.Value ? d[layer] != m : (d[layer] == 0f || d[layer] > m)) { d[layer] = m; changed = true; }
            }
            if (changed) cam.layerCullDistances = d;
        }

        void PropTick(Vector3 cp)
        {
            if (!propOn.Value) { if (statPropCulledNow != 0) foreach (var q in props) if (q.culled) SetProp(q, false); return; }
            if (props.Count == 0) return;
            int n = Mathf.Clamp(Mathf.CeilToInt(props.Count * Time.unscaledDeltaTime / 0.5f), 1, props.Count);
            if (propDirty) { propDirty = false; n = props.Count; }
            float sd = Eff(Ad(propDist.Value, true), cmbProp.Value); float show2 = sd * sd; float hd = sd + propHyst.Value; float hide2 = hd * hd;
            for (int c = 0; c < n && props.Count > 0; c++)
            {
                if (propIdx >= props.Count) propIdx = 0;
                var p = props[propIdx];
                if (p.t == null) { if (p.culled) statPropCulledNow--; props.RemoveAt(propIdx); continue; }
                float sq = (p.t.position - cp).sqrMagnitude;
                bool far = p.culled ? sq > show2 : sq > hide2;
                if (far != p.culled) SetProp(p, far);
                propIdx++;
            }
        }

        static void SetProp(PropRec p, bool cull)
        {
            for (int i = 0; i < p.rs.Length; i++)
            {
                var r = p.rs[i]; if (r == null) continue;
                if (cull) { if (r.enabled) { r.enabled = false; p.disabledByUs[i] = true; } }
                else if (p.disabledByUs[i]) { r.enabled = true; p.disabledByUs[i] = false; }
            }
            p.culled = cull;
            statPropCulledNow += cull ? 1 : -1;
        }

        void GrassTick()
        {
            var cs = ClutterSystem.instance;
            if (cs == null) { grassApplied = false; grassFor = null; return; }
            if (cs != grassFor) { grassFor = cs; grassApplied = false; grassOrigDist = cs.m_distance; grassOrigScale = cs.m_amountScale; grassDirty = true; }
            bool want = Active && grassOn.Value;
            GrassBoost.perFrame = want ? grassPerFrame.Value : 1;
            if (!grassDirty && want == grassApplied) return;
            grassDirty = false;
            try
            {
                float nd = want ? grassDist.Value : grassOrigDist, ns = want ? grassDensity.Value : grassOrigScale;
                if (Mathf.Abs(cs.m_distance - nd) > 0.01f || Mathf.Abs(cs.m_amountScale - ns) > 0.001f)
                {
                    cs.m_distance = nd; cs.m_amountScale = ns; GrassBoost.ForceRebuild(cs);
                    L("grass -> distance " + nd.ToString("0") + " m, density x" + ns.ToString("0.00") + (want ? "" : " (restored)"));
                }
                grassApplied = want;
            }
            catch (Exception e) { L("grass apply failed: " + e.Message); grassApplied = want; }
        }

        void VpoLodTick()
        {
            if (Time.unscaledTime < nextVpoLod) return; nextVpoLod = Time.unscaledTime + 2f;
            if (!vpoLodLooked) { vpoLodLooked = true; var t = AccessTools.TypeByName("VPO_WATER.Modules.ENGINE.SceneLODOptimizer"); if (t != null) vpoLodField = AccessTools.Field(t, "FarDistance"); L("VPO SceneLOD field " + (vpoLodField != null ? "found" : "not found")); }
            if (vpoLodField == null) return;
            try
            {
                float cur = (float)vpoLodField.GetValue(null);
                if (vpoLodOrig < 0f) vpoLodOrig = cur;
                float bse = lodFar.Value > 0f ? lodFar.Value : vpoLodOrig; float want = Active ? (asFar < 0.999f ? Mathf.Round(bse * asFar / 10f) * 10f : bse) : vpoLodOrig;
                if (Mathf.Abs(cur - want) > 0.01f) { vpoLodField.SetValue(null, want); L("VPO Scene LOD far distance " + cur.ToString("0") + " -> " + want.ToString("0") + " m"); }
            }
            catch { }
        }

        void PartTick(Vector3 cp)
        {
            if (!partOn.Value || parts.Count == 0) return;
            int n = Mathf.Clamp(Mathf.CeilToInt(parts.Count * Time.unscaledDeltaTime / 0.5f), 1, parts.Count);
            float pdx = Eff(Ad(partDist.Value, false), cmbPart.Value); float d2max = pdx * pdx;
            for (int c = 0; c < n && parts.Count > 0; c++)
            {
                if (partIdx >= parts.Count) partIdx = 0;
                var p = parts[partIdx];
                if (p.ps == null) { if (p.culled) statPartCulledNow--; if (p.paused) statPartPausedNow--; parts.RemoveAt(partIdx); continue; }
                if (!p.t.gameObject.activeInHierarchy) { partIdx++; continue; }
                float d2 = (p.t.position - cp).sqrMagnitude;
                var em = p.ps.emission;
                if (d2 > d2max)
                {
                    if (em.enabled) { em.enabled = false; if (!p.culled) { p.culled = true; statPartCulledNow++; } }
                    if (partPause.Value && !p.paused && p.culled && p.ps.isPlaying && p.ps.main.loop && p.ps.particleCount == 0)
                    { p.ps.Pause(true); p.paused = true; statPartPausedNow++; }
                }
                else
                {
                    if (p.paused) { if (p.ps.isPaused) p.ps.Play(true); p.paused = false; statPartPausedNow--; }
                    if (p.culled) { if (p.origEmit) em.enabled = true; p.culled = false; statPartCulledNow--; }
                    if (partFalloff.Value && em.enabled && p.origRate > 0f)
                        em.rateOverTimeMultiplier = Mathf.Lerp(p.origRate, p.origRate * 0.4f, Mathf.Clamp01(d2 / d2max));
                }
                partIdx++;
            }
        }

        void LodTick()
        {
            float m = lodMul.Value * asLod; float cur = QualitySettings.lodBias;
            if (Mathf.Approximately(m, 1f)) { if (baseBias > 0f) { if (Mathf.Approximately(cur, appliedBias)) QualitySettings.lodBias = baseBias; baseBias = appliedBias = -1f; } return; }
            if (baseBias < 0f || !Mathf.Approximately(cur, appliedBias)) baseBias = cur; // first time, or the game/another mod changed it: recapture the base (never from our own multiplied value)
            float want = baseBias * m;
            if (!Mathf.Approximately(cur, want)) { QualitySettings.lodBias = want; L("lodBias " + baseBias.ToString("0.###") + " x " + m.ToString("0.##") + " -> " + want.ToString("0.###")); }
            appliedBias = want;
        }

        void AutoTick()
        {
            float dt = Time.unscaledDeltaTime; asFrames++; asAccum += dt;
            float t = Time.unscaledTime; if (t < asNext) return; asNext = t + 1f;
            float fps = asAccum > 0f ? asFrames / asAccum : 0f; asFrames = 0; asAccum = 0f;
            if (!asOn.Value || !Active) { if (asLod != 1f || asFar != 1f) { asLod = asFar = 1f; nextVpoLod = 0f; L("AutoScale reset to 1.0 (" + (!asOn.Value ? "disabled" : "inactive") + ")"); } asLowSince = asHighSince = -1f; return; }
            bool hold = Player.m_localPlayer == null || LoadingScreen() || HudBlack() || BlockingScreen();
            if (hold) { asLowSince = asHighSince = -1f; asEma = -1f; return; }
            asEma = asEma < 0f ? fps : asEma * 0.6f + fps * 0.4f;
            if (asEma < asFps.Value) { asHighSince = -1f; if (asLowSince < 0f) asLowSince = t;
                if (t - asLowSince >= asHold.Value && (asLod > asMinLod.Value + 0.001f || asFar > asMinFar.Value + 0.001f)) { asLowSince = t;
                    asLod = Mathf.Max(asMinLod.Value, asLod - asDown.Value); asFar = Mathf.Max(asMinFar.Value, asFar - asDown.Value); nextVpoLod = 0f;
                    L("AutoScale DOWN fps=" + asEma.ToString("0.0") + " < " + asFps.Value.ToString("0") + ": lodScale=" + asLod.ToString("0.00") + " farScale=" + asFar.ToString("0.00")); } }
            else if (asEma >= asFps.Value * asMargin.Value) { asLowSince = -1f; if (asHighSince < 0f) asHighSince = t;
                if (t - asHighSince >= asRecHold.Value && (asLod < 0.999f || asFar < 0.999f)) { asHighSince = t;
                    asLod = Mathf.Min(1f, asLod + asUp.Value); asFar = Mathf.Min(1f, asFar + asUp.Value); nextVpoLod = 0f;
                    L("AutoScale UP fps=" + asEma.ToString("0.0") + ": lodScale=" + asLod.ToString("0.00") + " farScale=" + asFar.ToString("0.00")); } }
            else { asLowSince = asHighSince = -1f; }
        }

        void GcTick()
        {
            int cnt = GC.CollectionCount(0);
            if (cnt != gcCountSeen) { if (gcCountSeen != 0) naturalGcs += cnt - gcCountSeen; gcCountSeen = cnt; usedAfterGc = GC.GetTotalMemory(false); }
            if (!I.master.Value || abOff) return;
            float t = Time.unscaledTime;
            bool menu = BlockingScreen();
            if (menu) { if (menuOpenedAt < 0) menuOpenedAt = t; } else menuOpenedAt = -1f;
            var pl = Player.m_localPlayer;
            if (pl != null) {
                var pos = pl.transform.position; if ((pos - lastPos).sqrMagnitude > 0.04f) { lastPos = pos; stillSince = t; }
                // 1.7.9: also require genuine stillness via horizontal velocity (never force while walking/sprinting/boat)
                try { var v = pl.GetVelocity(); float eps = gcMoveEps != null ? gcMoveEps.Value : 0.15f; if (v.x * v.x + v.z * v.z > eps * eps) stillSince = t; } catch { }
            }
            if (pl != null) { float hp = pl.GetHealth(); if (hp < lastHp - 0.01f) lastHitAt = t; lastHp = hp; }
            WarmTick();
            if (SmartGcTick(t, pl)) return;
            if (t - lastGcForced < gcMinGapSec.Value) return;
            long grown = GC.GetTotalMemory(false) - usedAfterGc;
            if (grown < (long)(gcHeadroomMB.Value * 1048576f)) return;
            string why = null;
            if (gcMenuOn.Value && menu && t - menuOpenedAt >= gcMenuMinOpen.Value) why = "menu:" + blockingWhy;
            else if (gcIdleOn.Value && pl != null && t - stillSince > gcIdleSec.Value && !pl.InAttack() && pl.IsOnGround()) { bool mv=false; try { var v=pl.GetVelocity(); float eps=gcMoveEps!=null?gcMoveEps.Value:0.15f; mv=v.x*v.x+v.z*v.z>eps*eps; } catch { mv=true; } if (!mv) why = "idle"; }
            if (why == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            GC.Collect();
            sw.Stop();
            lastGcForced = t; gcCountSeen = GC.CollectionCount(0); usedAfterGc = GC.GetTotalMemory(false);
            if (why.StartsWith("menu")) forcedMenu++; else forcedIdle++;
            L("GC forced (" + why + ") grown=" + (grown >> 20) + "MB took=" + sw.ElapsedMilliseconds + "ms heapAfter=" + (usedAfterGc >> 20) + "MB");
        }

        float lastHp, lastHitAt = -999f, uiOpenedAt = -1f, nextCombatScan; bool combatCached; int forcedSmart; bool warmed, warmDone, preGcDone, invShowDone; string warmWhat = "";
        bool InCombat(Player pl, float t)
        {
            if (pl.InAttack() || pl.IsBlocking() || t - lastHitAt < 10f) return true;
            if (t < nextCombatScan) return combatCached;
            nextCombatScan = t + 0.5f; combatCached = false;
            try
            {
                float r2 = gcSmartCombatRange.Value * gcSmartCombatRange.Value; var pp = pl.transform.position;
                foreach (var c in Character.GetAllCharacters())
                {
                    if (c == null || c.IsPlayer() || c.IsTamed() || c.IsDead()) continue;
                    if ((c.transform.position - pp).sqrMagnitude > r2) continue;
                    var ai = c.GetBaseAI(); if (ai != null && ai.IsAlerted()) { combatCached = true; break; }
                }
            }
            catch { combatCached = true; }
            return combatCached;
        }
        // returns true if it ran a GC this frame
        bool SmartGcTick(float t, Player pl)
        {
            bool ui = false;
            try { ui = (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large) || InventoryGui.IsVisible(); } catch { }
            if (ui) { if (uiOpenedAt < 0) uiOpenedAt = t; } else uiOpenedAt = -1f;
            if (!gcSmartOn.Value || pl == null || LoadingScreen()) return false;
            if (t - lastGcForced < gcSmartGapSec.Value) return false;
            long grown = GC.GetTotalMemory(false) - usedAfterGc;
            if (grown < (long)(gcSmartHeadroomMB.Value * 1048576f)) return false;
            string why = null;
            // 1.7.9: never force while moving (horiz speed) or in combat; idle requires genuine stillness
            bool moving = false;
            try { var v = pl.GetVelocity(); float eps = gcMoveEps != null ? gcMoveEps.Value : 0.15f; moving = v.x * v.x + v.z * v.z > eps * eps; } catch { moving = true; }
            if (moving || pl.InAttack() || pl.IsBlocking()) return false;
            bool idle = !moving && t - stillSince >= gcSmartIdleSec.Value && pl.IsOnGround() && !pl.IsAttached();
            if (idle) why = "smartIdle";
            else if (gcSmartUi.Value && ui && !moving && t - uiOpenedAt >= gcMenuMinOpen.Value) why = "smartUi";
            if (why == null || InCombat(pl, t)) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            GC.Collect();
            sw.Stop();
            lastGcForced = t; gcCountSeen = GC.CollectionCount(0); usedAfterGc = GC.GetTotalMemory(false); forcedSmart++;
            L("GC forced (" + why + ") grown=" + (grown >> 20) + "MB took=" + sw.ElapsedMilliseconds + "ms heapAfter=" + (usedAfterGc >> 20) + "MB");
            return true;
        }
        float playerSeenAt = -1f; bool invPreloaded, described;
        static bool HudBlack()
        {
            var h = Hud.instance;
            return h != null && h.m_loadingScreen != null && h.m_loadingScreen.gameObject.activeInHierarchy && h.m_loadingScreen.alpha >= 0.99f;
        }
        void WarmTick()
        {
            if (!warmOn.Value && !invPreOn.Value) return;
            if (warmDone) return;
            var pl = Player.m_localPlayer;
            if (pl == null || Game.instance == null) { playerSeenAt = -1f; return; }
            var mm = Minimap.instance; var inv = InventoryGui.instance;
            if (inv == null) return;
            float t = Time.unscaledTime;
            if (playerSeenAt < 0) playerSeenAt = t;
            bool black = HudBlack();
            if (!black)
            {
                // fallback: first quiet second after spawn
                if (t - playerSeenAt < invPreFallbackSec.Value) return;
                if (InventoryGui.IsVisible() || pl.IsDead() || pl.IsTeleporting() || InCombat(pl, t)) return;
            }
            bool spread = warmSpread.Value; string where = black ? "behind loading screen" : "fallback " + (t - playerSeenAt).ToString("0.0") + "s after spawn";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!warmed)
            {
                warmed = true; string w = "";
                if (warmOn.Value)
                {
                    try
                    {
                        if (mm != null && mm.m_largeRoot != null && !mm.m_largeRoot.activeSelf && mm.m_mode != Minimap.MapMode.Large)
                        {
                            mm.m_largeRoot.SetActive(true);
                            try { Canvas.ForceUpdateCanvases(); } finally { mm.m_largeRoot.SetActive(false); }
                            w = "map";
                        }
                    }
                    catch (Exception e) { w = "map FAILED " + e.Message; }
                }
                if (spread) { L("UI warm-up step 1/3 (" + where + "): " + (w == "" ? "map skipped" : w) + " " + sw.ElapsedMilliseconds + "ms"); return; }
                warmWhat += w + " ";
            }
            if (invPreOn.Value && !invPreloaded)
            {
                if (spread && invPreSplit.Value)
                {
                    if (!invShowDone) { invShowDone = true; L("UI warm-up step 2b (" + where + "): " + InvPreload.Run(inv, pl, invPreCraft.Value, 1)); return; }
                    invPreloaded = true; L("UI warm-up step 2c (" + where + "): " + InvPreload.Run(inv, pl, invPreCraft.Value, 2)); return;
                }
                invPreloaded = true; string w = InvPreload.Run(inv, pl, invPreCraft.Value);
                if (spread) { L("UI warm-up step 2/3 (" + where + "): " + w + " " + sw.ElapsedMilliseconds + "ms"); return; }
                warmWhat += w;
            }
            // 1.7.8: GC moved AFTER the preload (was before it and used up the black frames, pushing the preload into visible frames)
            if (spread && invPreOn.Value && invPrePreGc.Value && !preGcDone)
            {
                preGcDone = true;
                if (black) { long h0 = GC.GetTotalMemory(false) >> 20; GC.Collect(); L("UI warm-up step 2d (" + where + "): post-preload GC heap " + h0 + "->" + (GC.GetTotalMemory(false) >> 20) + "MB " + sw.ElapsedMilliseconds + "ms"); return; }
            }
            warmDone = true;
            if (!spread || black) { try { Canvas.ForceUpdateCanvases(); warmWhat += " canvases"; } catch (Exception e) { warmWhat += " canvases FAILED " + e.Message; } }
            else warmWhat += " (final canvas pass skipped: screen visible)";
            sw.Stop();
            L("UI warm-up " + (spread ? "step 3/3" : "after spawn") + " (" + where + "): " + warmWhat + " took=" + sw.ElapsedMilliseconds + "ms");
        }

        internal static string blockingWhy = "";
        // Genuinely blocking screens only. Inventory/containers/tombstones, map, chat and console are NOT blocking (fights continue behind them).
        internal static bool BlockingScreen()
        {
            try
            {
                if (Menu.IsVisible()) { blockingWhy = "pauseMenu"; return true; }
                if (Game.IsPaused()) { blockingWhy = "paused"; return true; }
                if (CleanupLoading()) { blockingWhy = "loading"; return true; }
            }
            catch { }
            return false;
        }
        // 1.7.7: loading-screen test for the deferred VPO cleanup and the forced GC: the death screen does not count unless DeathScreenCountsAsLoading.
        internal static bool CleanupLoading()
        {
            if (deathLoad == null || deathLoad.Value) return LoadingScreen();
            if (ZNet.instance == null || Game.instance == null) return false;
            var p = Player.m_localPlayer;
            if (p != null && p.IsDead()) return false;
            return LoadingScreen();
        }
        internal static bool LoadingScreen()
        {
            if (ZNet.instance == null || Game.instance == null) return false;
            var p = Player.m_localPlayer;
            if (p == null || p.IsTeleporting() || p.IsDead()) return true;
            var h = Hud.instance;
            return h != null && h.m_loadingScreen != null && h.m_loadingScreen.gameObject.activeInHierarchy && h.m_loadingScreen.alpha >= 0.99f;
        }

        void RestoreAll()
        {
            foreach (var s in shadowRecs) if (s.r != null) s.r.shadowCastingMode = s.mode;
            foreach (var p in props) if (p.culled) SetProp(p, false);
            RestoreTrees();
            foreach (var p in parts)
            {
                if (p.ps == null) continue;
                if (p.paused && p.ps.isPaused) p.ps.Play(true);
                var em = p.ps.emission; em.enabled = p.origEmit; if (p.origRate > 0) em.rateOverTimeMultiplier = p.origRate;
                p.paused = p.culled = false;
                if (vpoTagType != null && p.ps.GetComponent(vpoTagType) == null) p.ps.gameObject.AddComponent(vpoTagType);
            }
            statPartCulledNow = statPartPausedNow = 0;
            if (layerCam != null && savedLayerCull != null) layerCam.layerCullDistances = savedLayerCull;
            layerCam = null;
            if (baseBias > 0f) { if (Mathf.Approximately(QualitySettings.lodBias, appliedBias)) QualitySettings.lodBias = baseBias; baseBias = appliedBias = -1f; } asLod = asFar = 1f; nextVpoLod = 0f;
        }

        void ReapplyAll()
        {
            foreach (var s in shadowRecs) if (s.r != null && shadowsOn.Value) s.r.shadowCastingMode = ShadowCastingMode.Off;
            if (vpoTagType != null && partOn.Value)
                foreach (var p in parts) if (p.ps != null) { var tag = p.ps.GetComponent(vpoTagType); if (tag != null) UnityEngine.Object.Destroy(tag); }
        }

        internal static float adLowV => adLow.Value; internal static float adHighV => adHigh.Value; internal static float adStrV => adStr.Value; internal static float adLenV => adLen.Value; internal static float adAggV => adAgg.Value; internal static float adShiftV => adShift.Value;
  internal static void AdLog(string m) { try { I.Logger.LogInfo(m); } catch { } }
  void Stats()
        {
            shadowRecs.RemoveAll(s => s.r == null);
            L($"STATS active={Active} shadowsOffTracked={shadowRecs.Count} trees={trees.Count} treesShadowTrimmed={statTreesTrimmed} treeCastersTrimmed={statTreeTrimmedCasters} props={props.Count} propsCulled={statPropCulledNow} particleSys={parts.Count} culled={statPartCulledNow} paused={statPartPausedNow} vpoTagsReplaced={statTagsReplaced} | GC natural={naturalGcs} forcedMenu={forcedMenu} forcedIdle={forcedIdle} forcedSmart={forcedSmart} vpoUnloads deferred={VpoUnload.deferred} ranLater={VpoUnload.ranLater} allowed={VpoUnload.allowed} | itemSnap snaps={ItemSnap.snaps} supported={ItemSnap.supported} | lootStack runs={LootStack.runs} drops={LootStack.lastCount} heap={GC.GetTotalMemory(false) >> 20}MB lodBias={QualitySettings.lodBias}");
        }

        void Census()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rs = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var byPrefab = new Dictionary<string, int[]>(); // renderers, shadowcasters, visible, particle systems playing
            var byLayer = new Dictionary<int, int>();
            int sc = 0, en = 0;
            foreach (var r in rs)
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                en++;
                byLayer.TryGetValue(r.gameObject.layer, out int lc); byLayer[r.gameObject.layer] = lc + 1;
                var zv = r.GetComponentInParent<ZNetView>();
                string key = zv != null ? zv.gameObject.name.Replace("(Clone)", "") : "<" + r.transform.root.name.Replace("(Clone)", "") + ">";
                if (!byPrefab.TryGetValue(key, out var a)) byPrefab[key] = a = new int[4];
                a[0]++;
                if (r.shadowCastingMode != ShadowCastingMode.Off) { a[1]++; sc++; }
                if (r.isVisible) a[2]++;
                if (r is ParticleSystemRenderer) { var ps = r.GetComponent<ParticleSystem>(); if (ps != null && ps.isPlaying) a[3]++; }
            }
            sw.Stop();
            L($"CENSUS enabled renderers={en} shadowcasting={sc} treeShadowCastersTrimmed={statTreeTrimmedCasters} (trees {statTreesTrimmed}/{trees.Count} beyond {treeDist.Value:0}m, on={treeOn.Value && Active}) took={sw.ElapsedMilliseconds}ms; layers: " + string.Join(", ", byLayer.OrderByDescending(x => x.Value).Select(x => LayerMask.LayerToName(x.Key) + "=" + x.Value)));
            L("  top by renderers [rend/shadow/visible/psPlaying]: " + string.Join(" | ", byPrefab.OrderByDescending(x => x.Value[0]).Take(25).Select(x => $"{x.Key} {x.Value[0]}/{x.Value[1]}/{x.Value[2]}/{x.Value[3]}")));
            L("  top by shadowcasters: " + string.Join(" | ", byPrefab.OrderByDescending(x => x.Value[1]).Take(15).Select(x => $"{x.Key} {x.Value[1]}")));
            L("  top by particle systems: " + string.Join(" | ", byPrefab.OrderByDescending(x => x.Value[3]).Take(15).Select(x => $"{x.Key} {x.Value[3]}")));
        }
    }

    // Defers ValheimPerformanceOverhaul's Resources.UnloadUnusedAssets calls (GCPatches.TriggerSafeAssetUnload, VPOMemoryManager.PerformSafeMemoryCleanup)
    // out of live gameplay. Resources.UnloadUnusedAssets itself is NOT patched, so vanilla scene transitions are unaffected.
    static class VpoUnload
    {
        static System.Reflection.MethodInfo trigger, cleanup; static Type mmType;
        static bool pendingTrigger, pendingCleanup, bypass;
        internal static int deferred, ranLater, allowed;
        static bool wasLoading = true; static float leftLoadingAt = -999f;
        internal static void Init(Harmony h)
        {
            try
            {
                var gc = AccessTools.TypeByName("ValheimEngineOptimizer.Modules.GCPatches");
                mmType = AccessTools.TypeByName("ValheimEngineOptimizer.Modules.VPOMemoryManager");
                trigger = gc == null ? null : AccessTools.DeclaredMethod(gc, "TriggerSafeAssetUnload");
                cleanup = mmType == null ? null : AccessTools.DeclaredMethod(mmType, "PerformSafeMemoryCleanup");
                var pre = new HarmonyMethod(typeof(VpoUnload), nameof(TriggerPre)) { priority = Priority.First };
                if (trigger != null) h.Patch(trigger, prefix: pre);
                if (cleanup != null) h.Patch(cleanup, prefix: new HarmonyMethod(typeof(VpoUnload), nameof(CleanupPre)) { priority = Priority.First });
                Plugin.I.L("VPO unload deferral: GCPatches.TriggerSafeAssetUnload " + (trigger != null ? "patched" : "not found") + ", VPOMemoryManager.PerformSafeMemoryCleanup " + (cleanup != null ? "patched" : "not found"));
            }
            catch (Exception e) { Plugin.I.L("VPO unload deferral patch FAILED: " + e.Message); }
        }
        static bool Allowed(out string why)
        {
            why = null;
            if (Plugin.CleanupLoading()) { why = "loading"; return true; }
            if (Time.unscaledTime - leftLoadingAt <= Plugin.vpoTeleportGrace.Value) { why = "afterTeleport"; return true; }
            if (Plugin.vpoInMenu.Value && Combat.factor <= 0f && Plugin.BlockingScreen() && Plugin.I.BlockingFor() >= Plugin.I.MenuMinOpen) { why = "menu:" + Plugin.blockingWhy; return true; }
            return false;
        }
        static bool On => Plugin.Active && Plugin.vpoDeferOn.Value && !bypass;
        static bool TriggerPre()
        {
            if (!On) return true;
            if (Allowed(out var why)) { allowed++; pendingTrigger = false; Plugin.I.L("VPO asset unload allowed (" + why + ")"); return true; }
            if (!pendingTrigger) { pendingTrigger = true; deferred++; Plugin.I.L("VPO asset unload (GCPatches) DEFERRED - in world"); }
            return false;
        }
        static bool CleanupPre()
        {
            if (!On) return true;
            if (Allowed(out var why)) { allowed++; pendingCleanup = false; Plugin.I.L("VPO memory cleanup allowed (" + why + ")"); return true; }
            if (!pendingCleanup) { pendingCleanup = true; deferred++; Plugin.I.L("VPO memory cleanup (VPOMemoryManager) DEFERRED - in world"); }
            return false;
        }
        internal static void Tick()
        {
            bool loading = false; try { loading = Plugin.CleanupLoading(); } catch { }
            if (wasLoading && !loading) leftLoadingAt = Time.unscaledTime;
            wasLoading = loading;
            if (!pendingTrigger && !pendingCleanup) return;
            if (!Plugin.Active || !Plugin.vpoDeferOn.Value) { pendingTrigger = pendingCleanup = false; return; }
            // Run pending work only on a loading screen or behind a blocking menu (not in the post-teleport grace: it is already fading in).
            if (!(loading || (Plugin.vpoInMenu.Value && Combat.factor <= 0f && Plugin.BlockingScreen() && Plugin.I.BlockingFor() >= Plugin.I.MenuMinOpen))) return;
            bypass = true;
            try
            {
                if (pendingTrigger && trigger != null) { pendingTrigger = false; trigger.Invoke(null, null); ranLater++; Plugin.I.L("pending VPO asset unload run (" + (loading ? "loading" : "menu:" + Plugin.blockingWhy) + ")"); }
                if (pendingCleanup && cleanup != null)
                {
                    pendingCleanup = false;
                    var inst = mmType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                    if (inst != null) { cleanup.Invoke(inst, null); ranLater++; Plugin.I.L("pending VPO memory cleanup run (" + (loading ? "loading" : "menu:" + Plugin.blockingWhy) + ")"); }
                }
            }
            catch (Exception e) { Plugin.I.L("pending VPO unload failed: " + e.Message); }
            finally { bypass = false; }
        }
    }

    // 1.7.3: vanilla 1.0.20 ItemDrop.TerrainCheck (called from SlowUpdate only when m_nview valid + owner):
    //   float g = ZoneSystem.instance.GetGroundHeight(pos); if (pos.y - g < -0.5f) { pos.y = g + 0.5f; transform.position = pos; rb.linearVelocity = Vector3.zero; }
    // FGN transpiles GetGroundHeight -> raycast on Default/static_solid/... which lands on ore veins / cliffs. This prefix skips that body.
    static class ItemSnap
    {
        static int mask = -1;
        internal static int snaps, supported;
        internal static bool Pre(ItemDrop __instance)
        {
            if (Plugin.terrainSnapOn == null || !Plugin.terrainSnapOn.Value) return true;
            try
            {
                var zs = ZoneSystem.instance; if (zs == null) return true;
                Transform t = __instance.transform; Vector3 pos = t.position;
                float g = zs.GetGroundHeight(pos);
                if (pos.y - g < -0.5f)
                {
                    if (mask == -1) mask = LayerMask.GetMask("piece", "vehicle");
                    // floor/ship support: a piece/vehicle surface between 0.5 m above and 1 m below the item -> leave it there
                    if (mask != 0 && Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, 1.5f, mask, QueryTriggerInteraction.Ignore)) { supported++; return false; }
                    pos.y = g + 0.5f; t.position = pos;
                    Rigidbody rb = __instance.GetComponent<Rigidbody>();
                    if (rb) rb.linearVelocity = Vector3.zero;
                    snaps++;
                }
                return false;
            }
            catch { return true; }
        }
    }
    // Vanilla ItemDrop.SlowUpdate only auto-stacks above 200 drops. Run the same vanilla AutoStackItems earlier.
    static class LootStack
    {
        static readonly AccessTools.FieldRef<List<ItemDrop>> instances = AccessTools.StaticFieldRefAccess<List<ItemDrop>>(AccessTools.DeclaredField(typeof(ItemDrop), "s_instances"));
        static readonly Action<ItemDrop> autoStack = AccessTools.MethodDelegate<Action<ItemDrop>>(AccessTools.DeclaredMethod(typeof(ItemDrop), "AutoStackItems"));
        static readonly AccessTools.FieldRef<ItemDrop, ZNetView> nview = AccessTools.FieldRefAccess<ItemDrop, ZNetView>("m_nview");
        internal static int runs, lastCount;
        internal static void Post(ItemDrop __instance)
        {
            if (Plugin.I == null || !Plugin.stackOn.Value || !Plugin.Active) return;
            try
            {
                int n = instances().Count; lastCount = n;
                if (n <= Plugin.stackThreshold.Value || n > 200) return; // >200: vanilla already did it
                var nv = nview(__instance);
                if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
                autoStack(__instance); runs++; // vanilla guards: max stack, once per drop, same name+quality, owner-only
            }
            catch { }
        }
    }

    // 1.4.2 (fixed 1.4.1): full silent Show -> Update* -> Hide pass, invisible (CanvasGroup alpha 0 on the GUI root), no sounds, no GC.
    static class InvPreload
    {
        class CgRec { public CanvasGroup cg; public bool added; public float a; public bool rc, it; }
        static void Call(InventoryGui inv, string name, Type[] sig, object[] args, ref string r, System.Diagnostics.Stopwatch sw)
        {
            try
            {
                var m = AccessTools.Method(typeof(InventoryGui), name, sig);
                if (m == null) { r += name + "=n/a "; return; }
                var a = sw.ElapsedMilliseconds; m.Invoke(inv, args); r += name + "=" + (sw.ElapsedMilliseconds - a) + "ms ";
            }
            catch (Exception e) { r += name + " FAILED " + (e.InnerException ?? e).Message + " "; }
        }
        static GameObject guiGo, rootGo; static bool guiHad, rootHad, pendingCheck; static float guiA = 1f, rootA = 1f; static bool guiRc = true, guiIt = true, rootRc = true, rootIt = true;
        // re-asserts the exact pre-preload CanvasGroup state on the GUI object and the inventory root; removes a leftover added group
        static string Reassert(InventoryGui inv)
        {
            string fixedS = "";
            try
            {
                if (guiGo != null)
                {
                    var g = guiGo.GetComponent<CanvasGroup>();
                    if (!guiHad && g != null && g.alpha == 0f && !g.blocksRaycasts) { UnityEngine.Object.DestroyImmediate(g); fixedS += "guiCG removed "; }
                    else if (guiHad && g != null && (g.alpha != guiA || g.blocksRaycasts != guiRc || g.interactable != guiIt)) { g.alpha = guiA; g.blocksRaycasts = guiRc; g.interactable = guiIt; fixedS += "guiCG restored "; }
                }
                if (rootGo != null && inv != null)
                {
                    var c = rootGo.GetComponent<CanvasGroup>();
                    if (c != null) // on a real open the root must take input
                    {
                        bool rc = rootHad ? rootRc : true, it = rootHad ? rootIt : true; float a = rootHad ? rootA : 1f;
                        if (c.blocksRaycasts != rc || c.interactable != it || c.alpha != a) { c.blocksRaycasts = rc; c.interactable = it; c.alpha = a; fixedS += "rootCG restored "; }
                    }
                }
            }
            catch (Exception e) { fixedS += "reassert FAILED " + e.Message; }
            return fixedS;
        }
        // first real open after the preload: re-assert the correct state once
        internal static void ShowPost(InventoryGui __instance)
        {
            if (!pendingCheck) return; pendingCheck = false;
            var s = Reassert(__instance);
            Plugin.I?.L("inventory first-open safety check: " + (s.Length == 0 ? "state OK" : s));
        }
        // part: 0 = full pass, 1 = Show/Hide only (crafting setup), 2 = grid/stats/canvas pass without Show (1.7.7 split)
        internal static string Run(InventoryGui inv, Player pl, bool craftUnused, int part = 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string r = part == 1 ? "inventory(show/hide part) " : part == 2 ? "inventory(update part) " : "inventory(show/hide) ";
            var fOpen = AccessTools.Field(typeof(InventoryGui), "m_openInventoryEffects");
            var fClose = AccessTools.Field(typeof(InventoryGui), "m_closeInventoryEffects");
            var fSetGrp = AccessTools.Field(typeof(InventoryGui), "m_setActiveGroupEffects");
            var fHidden = AccessTools.Field(typeof(InventoryGui), "m_hiddenFrames");
            var fShown = AccessTools.Field(typeof(InventoryGui), "m_shownFrames");
            var fAnim = AccessTools.Field(typeof(InventoryGui), "m_animator");
            object oOpen = fOpen?.GetValue(inv), oClose = fClose?.GetValue(inv), oGrp = fSetGrp?.GetValue(inv);
            object oHidden = fHidden?.GetValue(inv), oShown = fShown?.GetValue(inv);
            var cgs = new List<CgRec>();
            var activated = new List<GameObject>();
            bool shown = false;
            try
            {
                // 1.4.2: invisibility ONLY via a canvas group on the InventoryGui object (parent of the root). The inventory root's own
                // CanvasGroup is never touched: InventorySlots' Hide patch snapshots root.blocksRaycasts and restores that snapshot later,
                // so in 1.4.1 it captured our "false" and re-applied it after our restore (dead inventory: no drag, no tooltips).
                var objs = new List<GameObject> { inv.gameObject };
                var rootT = AccessTools.Field(typeof(InventoryGui), "m_inventoryRoot")?.GetValue(inv) as Component;
                if (rootT != null) { var rc0 = rootT.GetComponent<CanvasGroup>(); rootHad = rc0 != null; if (rc0 != null) { rootA = rc0.alpha; rootRc = rc0.blocksRaycasts; rootIt = rc0.interactable; } rootGo = rootT.gameObject; }
                foreach (var go in objs)
                {
                    var cg = go.GetComponent<CanvasGroup>(); bool added = false;
                    if (cg == null) { cg = go.AddComponent<CanvasGroup>(); added = true; }
                    cgs.Add(new CgRec { cg = cg, added = added, a = cg.alpha, rc = cg.blocksRaycasts, it = cg.interactable }); guiGo = go; guiHad = !added; guiA = cg.alpha; guiRc = cg.blocksRaycasts; guiIt = cg.interactable;
                    cg.alpha = 0f; cg.blocksRaycasts = false; cg.interactable = false;
                }
                if (rootT != null && !rootT.gameObject.activeSelf) { rootT.gameObject.SetActive(true); activated.Add(rootT.gameObject); r += "(root activated) "; }
                // silence
                if (fOpen != null) fOpen.SetValue(inv, new EffectList());
                if (fClose != null) fClose.SetValue(inv, new EffectList());
                if (fSetGrp != null) fSetGrp.SetValue(inv, new EffectList());
                var a0 = sw.ElapsedMilliseconds;
                if (part != 2) { inv.Show(null); shown = true; r += "Show(+SetupCrafting)=" + (sw.ElapsedMilliseconds - a0) + "ms "; }
                if (part == 1) { try { var an1 = fAnim?.GetValue(inv) as Animator; if (an1 != null) { an1.Update(0f); an1.Update(10f); } } catch { } r += "OK"; return r + " (" + sw.ElapsedMilliseconds + "ms)"; }
                var P = new[] { typeof(Player) };
                Call(inv, "UpdateInventory", P, new object[] { pl }, ref r, sw);
                Call(inv, "UpdateCraftingPanel", new[] { typeof(bool) }, new object[] { false }, ref r, sw);
                Call(inv, "UpdateCharacterStats", P, new object[] { pl }, ref r, sw);
                Call(inv, "UpdateInventoryWeight", P, new object[] { pl }, ref r, sw);
                Call(inv, "UpdateContainerWeight", Type.EmptyTypes, new object[0], ref r, sw);
                Call(inv, "UpdateRecipe", new[] { typeof(Player), typeof(float) }, new object[] { pl, 0f }, ref r, sw);
                try { var an = fAnim?.GetValue(inv) as Animator; if (an != null) { an.Update(0f); an.Update(10f); r += "anim "; } } catch { }
                var b = sw.ElapsedMilliseconds; Canvas.ForceUpdateCanvases(); r += "canvas=" + (sw.ElapsedMilliseconds - b) + "ms ";
                r += "OK";
            }
            catch (Exception e) { r += "FAILED " + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            finally
            {
                try { if (shown) inv.Hide(); } catch (Exception e) { r += " hide FAILED " + e.Message; }
                try { var an = fAnim?.GetValue(inv) as Animator; if (an != null) { an.Update(0f); an.Update(10f); } } catch { }
                try
                {
                    if (fOpen != null) fOpen.SetValue(inv, oOpen);
                    if (fClose != null) fClose.SetValue(inv, oClose);
                    if (fSetGrp != null) fSetGrp.SetValue(inv, oGrp);
                    if (fHidden != null && oHidden != null) fHidden.SetValue(inv, Math.Max((int)oHidden, 9999)); // IsVisible() false immediately -> cursor stays locked
                    if (fShown != null && oShown != null) fShown.SetValue(inv, 0);
                    foreach (var go in activated) go.SetActive(false);
                    foreach (var c in cgs)
                    {
                        if (c.cg == null) continue;
                        c.cg.alpha = c.a; c.cg.blocksRaycasts = c.rc; c.cg.interactable = c.it;
                        if (c.added) UnityEngine.Object.DestroyImmediate(c.cg);
                    }
                    Reassert(null); pendingCheck = true;
                }
                catch (Exception e) { r += " restore FAILED " + e.Message; }
            }
            return r + " (" + sw.ElapsedMilliseconds + "ms)";
        }
    }

    static class Patches
    {
        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("com.Skarif.ValheimPerformanceOverhaul_WATER")]
        static void CreateObjectPost(GameObject __result) => Plugin.OnCreated(__result);
    }

    // 1.5.0: route mod AudioSources through Valheim's own master mixer so the in-game volume sliders apply.
    // Mod bundles (e.g. all Balrond mods) ship their own copy of "MasterMixer" (groups SFX, SFX_LARGE, GUI, Music...), which is a
    // separate AudioMixer instance that Settings never touches; some sources also have no group at all.
    internal static class AudioRoute
    {
        static ConfigEntry<bool> on, remapForeign, assignNull;
        static Dictionary<string, UnityEngine.Audio.AudioMixerGroup> groups;
        static UnityEngine.Audio.AudioMixer master;
        static UnityEngine.Audio.AudioMixerGroup sfx;
        static int foreignFixed, nullFixed, skipped, sweeps;
        static readonly HashSet<string> samples = new HashSet<string>();

        internal static void Init(Plugin p, Harmony h)
        {
            on = p.Config.Bind("10. Audio Routing", "Enabled", true, "1.5.0: make mod sound effects obey the in-game volume settings. Re-routes AudioSources that point at a mod-bundled copy of Valheim's mixer to the real group with the same name (SFX, SFX_LARGE, GUI, Music, Ambient...). Prefabs are fixed once (after ZNetScene loads and again on spawn); new ZSFX instances get a one-field check.");
            remapForeign = p.Config.Bind("10. Audio Routing", "RemapForeignMixers", true, "Re-route sources whose mixer group belongs to a different (mod-bundled) AudioMixer.");
            assignNull = p.Config.Bind("10. Audio Routing", "AssignUnroutedToSfx", true, "Give sources with NO mixer group the vanilla SFX group (sources under AudioMan/MusicMan are never touched).");
            if (!on.Value) { p.L("audio routing disabled by config"); return; }
            P(p, h, typeof(ZNetScene), "Awake", nameof(SweepZNS), false);
            P(p, h, typeof(Player), "OnSpawned", nameof(SweepSpawn), false);
            P(p, h, typeof(ZSFX), "Awake", nameof(ZsfxPre), true);
        }

        static void P(Plugin p, Harmony h, Type t, string m, string hook, bool prefix)
        {
            try
            {
                var orig = AccessTools.Method(t, m);
                if (orig == null) { p.L("audio routing: " + t.Name + "." + m + " not found"); return; }
                var hm = new HarmonyMethod(typeof(AudioRoute), hook) { priority = prefix ? Priority.First : Priority.Last };
                if (prefix) h.Patch(orig, prefix: hm); else h.Patch(orig, postfix: hm);
                p.L("audio routing patch ok: " + t.Name + "." + m);
            }
            catch (Exception e) { p.L("audio routing patch FAILED " + t.Name + "." + m + ": " + e.Message); }
        }

        static bool Ready()
        {
            if (master != null && sfx != null) return true;
            var am = AudioMan.instance;
            if (am == null || am.m_masterMixer == null) return false;
            master = am.m_masterMixer;
            groups = new Dictionary<string, UnityEngine.Audio.AudioMixerGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in master.FindMatchingGroups(string.Empty)) if (g != null && !groups.ContainsKey(g.name)) groups[g.name] = g;
            groups.TryGetValue("SFX", out sfx);
            if (sfx == null) sfx = am.m_ambientMixer; // fallback, should not happen
            Plugin.I?.L("audio routing: vanilla mixer '" + master.name + "' groups=" + string.Join(",", groups.Keys.ToArray()) + " sfx=" + (sfx != null ? sfx.name : "null"));
            return sfx != null;
        }

        static bool Exempt(AudioSource s)
        {
            return s.GetComponentInParent<AudioMan>() != null || s.GetComponentInParent<MusicMan>() != null;
        }

        // returns true if changed
        static bool Fix(AudioSource s)
        {
            var g = s.outputAudioMixerGroup;
            if (g != null)
            {
                if (!remapForeign.Value || g.audioMixer == master) return false;
                UnityEngine.Audio.AudioMixerGroup t;
                if (!groups.TryGetValue(g.name, out t)) t = sfx;
                if (Exempt(s)) { skipped++; return false; }
                s.outputAudioMixerGroup = t; foreignFixed++;
            }
            else
            {
                if (!assignNull.Value || Exempt(s)) { skipped++; return false; }
                s.outputAudioMixerGroup = sfx; nullFixed++;
            }
            if (samples.Count < 12) samples.Add(s.transform.root.name + (g != null ? "[" + g.name + "]" : "[null]"));
            return true;
        }

        static void Sweep(string why)
        {
            if (!on.Value || !Ready()) { Plugin.I?.L("audio routing sweep (" + why + ") skipped: AudioMan not ready"); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int f0 = foreignFixed, n0 = nullFixed, seen = 0;
            var roots = new HashSet<string>();
            foreach (var s in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (s == null) continue; seen++;
                try { if (Fix(s)) roots.Add(s.transform.root.name); } catch { }
            }
            sweeps++;
            Plugin.I?.L("audio routing sweep (" + why + "): sources=" + seen + " remappedForeign=" + (foreignFixed - f0) + " assignedNull=" + (nullFixed - n0) + " objects=" + roots.Count + " skipped(total)=" + skipped + " in " + sw.ElapsedMilliseconds + " ms; samples: " + string.Join(", ", samples.ToArray()));
        }

        static void SweepZNS() => Sweep("ZNetScene.Awake");
        static void SweepSpawn() { if (sweeps < 2) Sweep("Player.OnSpawned"); }

        static void ZsfxPre(ZSFX __instance)
        {
            if (!on.Value || master == null) return;
            try { var s = __instance.GetComponent<AudioSource>(); if (s != null && (s.outputAudioMixerGroup == null || s.outputAudioMixerGroup.audioMixer != master)) Fix(s); } catch { }
        }
    }}
namespace RenderTrim
{
    internal sealed class ConfigurationManagerAttributes { public bool? IsAdvanced; public int? Order; public bool? ReadOnly; }

    internal static class Combat
    {
        internal static float factor; internal static bool inCombat; static float lastSignal = -999f, nextCheck; internal static float lastHit = -999f;
        internal static void Init(Harmony h)
        {
            try { h.Patch(AccessTools.Method(typeof(Character), "ApplyDamage"), postfix: new HarmonyMethod(typeof(Combat), nameof(DmgPost))); Plugin.I.L("combat damage hook ok"); }
            catch (Exception e) { Plugin.I.L("combat damage hook FAILED (alert detection still works): " + e.Message); }
        }
        static void DmgPost(Character __instance, HitData hit)
        {
            try { var lp = Player.m_localPlayer; if (lp == null) return; if (__instance == lp || (hit != null && hit.GetAttacker() == lp)) lastHit = Time.unscaledTime; } catch { }
        }
        internal static void Tick(Plugin p)
        {
            float t = Time.unscaledTime;
            if (!Plugin.cmbOn.Value || !Plugin.Active) { inCombat = false; factor = Mathf.MoveTowards(factor, 0f, Time.unscaledDeltaTime / Plugin.cmbEase.Value); return; }
            if (t >= nextCheck)
            {
                nextCheck = t + 0.25f;
                bool sig = t - lastHit < 1f;
                var lp = Player.m_localPlayer;
                if (!sig && lp != null)
                {
                    Vector3 pp = lp.transform.position; float r2 = Plugin.cmbDetect.Value * Plugin.cmbDetect.Value;
                    foreach (var c in Character.GetAllCharacters())
                    {
                        if (c == null || c.IsPlayer() || c.IsTamed() || c.IsDead()) continue;
                        if ((c.transform.position - pp).sqrMagnitude > r2) continue;
                        var ai = c.GetBaseAI(); if (ai != null && ai.IsAlerted() && BaseAI.IsEnemy(c, lp)) { sig = true; break; }
                    }
                }
                if (sig) lastSignal = t;
                bool now = lp != null && t - lastSignal < Plugin.cmbExit.Value;
                if (now != inCombat) { inCombat = now; p.L("combat mode " + (now ? "ENTER" : "EXIT (easing back over " + Plugin.cmbEase.Value + " s)")); }
            }
            factor = Mathf.MoveTowards(factor, inCombat ? 1f : 0f, Time.unscaledDeltaTime / Plugin.cmbEase.Value);
        }
    }

    [HarmonyPatch]
    internal static class GrassBoost
    {
        internal static int perFrame = 1;
        static readonly AccessTools.FieldRef<ClutterSystem, bool> forceRebuild = AccessTools.FieldRefAccess<ClutterSystem, bool>("m_forceRebuild");
        static readonly System.Reflection.MethodInfo update = AccessTools.Method(typeof(ClutterSystem), "UpdateGrass");
        static readonly System.Reflection.MethodInfo ready = AccessTools.Method(typeof(ClutterSystem), "IsHeightmapReady");
        static bool broken;
        internal static void ForceRebuild(ClutterSystem cs) { try { forceRebuild(cs) = true; } catch { } }

        [HarmonyPostfix, HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
        static void Post(ClutterSystem __instance)
        {
            if (perFrame <= 1 || broken || update == null || ready == null) return;
            try
            {
                if (forceRebuild(__instance) || !RenderGroupSystem.IsGroupActive(RenderGroup.Overworld)) return;
                var cam = Camera.main; if (cam == null) return;
                if (!(bool)ready.Invoke(__instance, null)) return;
                Vector3 c = (!GameCamera.InFreeFly() && Player.m_localPlayer != null) ? Player.m_localPlayer.transform.position : cam.transform.position;
                var args = new object[] { 0f, false, c };
                for (int i = 1; i < perFrame; i++) update.Invoke(__instance, args);
            }
            catch (Exception e) { broken = true; Plugin.I?.L("grass PatchesPerFrame disabled: " + e.Message); }
        }
    }
}

namespace RenderTrim {
internal static class Adaptive {
  internal static float pressure, range = 120f; internal static bool fromSB; static float lastT, dtEma = 0.016f, nextLog; static System.Reflection.FieldInfo fP; static bool looked;
  internal static void Tick() {
    try {
      float t = Time.unscaledTime; float dt = Mathf.Clamp(t - lastT, 0f, 0.5f); lastT = t;
      if (!looked) { looked = true; var ty = HarmonyLib.AccessTools.TypeByName("SpawnBudgetPressure"); if (ty != null) fP = ty.GetField("Value"); }
      if (fP != null) { pressure = (float)fP.GetValue(null); fromSB = true; }
      else { dtEma += (Mathf.Min(Time.unscaledDeltaTime, 0.25f) - dtEma) * Mathf.Clamp01(dt); float raw = Mathf.Clamp01((dtEma - 0.0167f) / 0.0167f); pressure += (raw - pressure) * Mathf.Clamp01(dt / (raw > pressure ? 4f : 10f)); fromSB = false; }
      float lo = Plugin.adLowV, hi = Mathf.Max(Plugin.adHighV, lo + 0.01f); float k = Mathf.Clamp01((pressure - lo) / (hi - lo)) * Plugin.adStrV;
      float target = Mathf.Lerp(Plugin.adLenV, Mathf.Min(Plugin.adLenV, Plugin.adAggV), k);
      range = Mathf.MoveTowards(range, target, Mathf.Abs(Plugin.adLenV - Plugin.adAggV) * dt / Mathf.Max(0.5f, Plugin.adShiftV) + 0.0001f);
      if (t >= nextLog) { nextLog = t + 30f; Plugin.AdLog("ADAPTIVE pressure=" + pressure.ToString("0.00") + (fromSB ? "(SB)" : "(local)") + " target=" + target.ToString("0") + "m range=" + range.ToString("0") + "m mode=" + (Combat.factor > 0f ? "COMBAT" : "normal")); }
    } catch { }
  }
}
}

namespace RenderTrim {
// 1.7.1: per-mod CPU throttles, measured with FrameSpikeLogger 2.3.3 (20261025-1309). Each is a live toggle; visual-only ones default on.
static class ModThrottles {
  const string S = "Mod Throttles";
  static BepInEx.Configuration.ConfigEntry<bool> vpoOn, rarOn, feedOn, hovOn, preOn, combatFull; static BepInEx.Configuration.ConfigEntry<int> vpoN, hovN, feedN; static BepInEx.Configuration.ConfigEntry<float> rarDist, feedDist;
  static bool patched; static float tryAt; static int vpoFrames, hovFrames; static readonly System.Collections.Generic.Dictionary<int, int> feedCtr = new System.Collections.Generic.Dictionary<int, int>();
  internal static int skipped;
  public static void Bind(BepInEx.Configuration.ConfigFile c) {
    vpoOn = c.Bind(S, "VPOLightCulling_Throttle", true, "ValheimEngineOptimizer VPOLightCullingManager.LateUpdate (top mod cost, 0.094 ms/f avg, 0.5 ms max): run every N frames. It only refreshes CullingGroup bounding spheres of moving lights (visual only; lights may cull up to N-1 frames late).");
    vpoN = c.Bind(S, "VPOLightCulling_EveryNFrames", 3, new BepInEx.Configuration.ConfigDescription("N for VPOLightCulling_Throttle.", new BepInEx.Configuration.AcceptableValueRange<int>(1, 10)));
    rarOn = c.Bind(S, "ItemRarityVFX_FarSkip", true, "BalrondItemRarity RarityWorldVFX.LateUpdate (25 instances, 0.012 ms/f, 11.3 ms max): skip for dropped items farther than RarityVFX_Distance (visual glow only).");
    rarDist = c.Bind(S, "ItemRarityVFX_Distance", 40f, "Meters.");
    feedOn = c.Bind(S, "AnimalFeeding_Throttle", false, "BalrondMonsterMayhem AnimalFeedingBehaviour.LateUpdate (31 instances): beyond AnimalFeeding_Distance run only every N frames. OFF by default: it is creature behaviour (gameplay), even though the measured cost is small.");
    feedDist = c.Bind(S, "AnimalFeeding_Distance", 50f, "Meters.");
    feedN = c.Bind(S, "AnimalFeeding_EveryNFrames", 4, new BepInEx.Configuration.ConfigDescription("N.", new BepInEx.Configuration.AcceptableValueRange<int>(1, 20)));
    hovOn = c.Bind(S, "ZenHoverItem_Throttle", false, "ZenHoverItem HoverItemUI.Update (0.027 ms/f): every N frames. OFF by default (tooltip UI; saving is tiny).");
    hovN = c.Bind(S, "ZenHoverItem_EveryNFrames", 2, new BepInEx.Configuration.ConfigDescription("N.", new BepInEx.Configuration.AcceptableValueRange<int>(1, 6)));
    preOn = c.Bind(S, "BAO_InspiredRunestone_Prewarm", true, "Spike fix: BalrondAbsoluteOverhaul registers its 'Inspired' status effect lazily on the first rune-stone read (Instantiate + ObjectDB.UpdateRegisters inside Player.Update; 127/143 ms spikes at 13:04:54/13:05:03 were both rune-stone reads). Calls BAO's own EnsureStatusEffect(ObjectDB) once after spawn, which is exactly what its first rune-stone read would do.");
    combatFull = c.Bind(S, "FullRateInCombat", true, "While Combat Mode is active, all throttles above run every frame.");
  }
  public static void Tick(Harmony h) {
    if (patched) return; if (Player.m_localPlayer == null || ObjectDB.instance == null || Time.realtimeSinceStartup < tryAt) return; patched = true;
    P(h, "VPOLightCullingManager", "LateUpdate", nameof(Vpo)); P(h, "RarityWorldVFX", "LateUpdate", nameof(Far)); P(h, "AnimalFeedingBehaviour", "LateUpdate", nameof(Feed)); P(h, "HoverItemUI", "Update", nameof(Hov));
    if (preOn.Value) try { var ty = AccessTools.TypeByName("Balrond.Features.Exploration.InspiredRunestoneService"); var m = ty != null ? AccessTools.Method(ty, "EnsureStatusEffect") : null; if (m != null) { var sw = System.Diagnostics.Stopwatch.StartNew(); m.Invoke(null, new object[] { ObjectDB.instance }); Plugin.I.L("[Mod Throttles] BAO Inspired status effect pre-registered in " + sw.Elapsed.TotalMilliseconds.ToString("F1") + " ms"); } } catch (System.Exception e) { Plugin.I.L("[Mod Throttles] BAO prewarm failed: " + e.Message); }
  }
  static void P(Harmony h, string type, string meth, string pre) {
    try { var ty = AccessTools.TypeByName(type); var m = ty != null ? AccessTools.DeclaredMethod(ty, meth) : null; if (m == null) { Plugin.I.L("[Mod Throttles] " + type + "." + meth + " not found (mod absent?)"); return; }
      h.Patch(m, prefix: new HarmonyMethod(typeof(ModThrottles), pre) { priority = Priority.First }); Plugin.I.L("[Mod Throttles] hooked " + type + "." + meth); } catch (System.Exception e) { Plugin.I.L("[Mod Throttles] " + type + " hook failed: " + e.Message); }
  }
  static bool Full() => combatFull.Value && Combat.factor > 0f;
  static bool Vpo() { if (!vpoOn.Value || Full()) return true; if (Time.frameCount == vpoFrames) return true; if (Time.frameCount % vpoN.Value == 0) { vpoFrames = Time.frameCount; return true; } skipped++; return false; }
  static bool Hov() { if (!hovOn.Value || Full()) return true; if (Time.frameCount % hovN.Value == 0) return true; skipped++; return false; }
  static bool Far(MonoBehaviour __instance) { if (!rarOn.Value || Full()) return true; var p = Player.m_localPlayer; if (p == null || __instance == null) return true; float d = rarDist.Value; if ((__instance.transform.position - p.transform.position).sqrMagnitude <= d * d) return true; skipped++; return false; }
  static bool Feed(MonoBehaviour __instance) { if (!feedOn.Value || Full()) return true; var p = Player.m_localPlayer; if (p == null || __instance == null) return true; float d = feedDist.Value; if ((__instance.transform.position - p.transform.position).sqrMagnitude <= d * d) return true;
    int id = __instance.GetInstanceID(); feedCtr.TryGetValue(id, out int c); c++; if (c >= feedN.Value) { feedCtr[id] = 0; return true; } feedCtr[id] = c; skipped++; return false; }
}
}