using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace RenderTrim
{
    // 1.7.9: one "Performance <-> Quality" slider that sets the technical values of RenderTrim and SpawnBudget.
    // Applied ONLY when the slider itself is moved (debounced 0.6 s); never on startup, never every frame.
    // Individual settings stay editable; they are only overwritten the next time the slider is moved.
    internal static class Preset
    {
        internal static ConfigEntry<int> preset; internal static ConfigEntry<bool> driveSb; internal static ConfigEntry<string> lastApplied;
        const string S = "0. Quick Setup";
        static float pendingAt = -1f; static ConfigFile cfg; static Action<string> log;

        // section, key, Performance (0), Balanced (50 = 1.7.7 values exactly), Quality (100), isInt
        internal static readonly object[][] RT = {
            new object[]{"1b. Tree Shadows","TreeShadowDistance",45f,70f,120f},
            new object[]{"1. Shadows","SmallShadowMaxSize",1.2f,0.8f,0.4f},
            new object[]{"3. Small Prop Cull","Distance",70f,100f,150f},
            new object[]{"3b. Grass","Distance",35f,50f,80f},
            new object[]{"3b. Grass","DensityScale",0.8f,1f,1f},
            new object[]{"3c. VPO Scene LOD","FarDistanceOverride",120f,160f,220f},
            new object[]{"4. Particles","CullDistance",55f,80f,120f},
            new object[]{"Combat Mode","PropDistance",45f,60f,90f},
            new object[]{"Combat Mode","TreeShadowDistance",35f,45f,70f},
            new object[]{"Combat Mode","ParticleCullDistance",40f,50f,80f},
            new object[]{"Adaptive","LenientRange",100f,120f,160f},
            new object[]{"Adaptive","AggressiveRange",50f,60f,90f},
            new object[]{"Adaptive","Strength",1f,1f,0.6f},
            new object[]{"6. LOD","LodBiasMultiplier",0.85f,1f,1.25f},
            new object[]{"6b. Auto Scale","TargetFps",50f,45f,40f},
            new object[]{"6b. Auto Scale","MinLodBiasScale",0.5f,0.6f,0.8f},
            new object[]{"6b. Auto Scale","MinFarDistanceScale",0.6f,0.7f,0.85f},
        };
        // Layer Cull distances (Default_small, item, effect, piece_nonsolid)
        internal static readonly float[][] LAYERS = { new[]{110f,70f,70f,110f}, new[]{160f,100f,100f,160f}, new[]{220f,140f,140f,220f} };
        // SpawnBudget: higher budgets = things appear sooner (less pop-in) but more frame time spent creating them.
// 1.8.0: also drive location soft-cap / dungeon wall / distant backlog. Smart GC IdleSec/MinGapSec/HeadroomMB are NOT in the RT rows -
// smoothness floors stay at defaults (IdleSec 20, MinGapSec 120, HeadroomMB 64) at every slider position.
        internal static readonly object[][] SB = {
            new object[]{"General","BudgetMs",4f,6f,9f},
            new object[]{"Adaptive","LenientBudgetMs",7f,10f,14f},
            new object[]{"Adaptive","AggressiveBudgetMs",3f,4f,6f},
            new object[]{"Combat Mode","CombatBudgetMs",2f,3f,5f},
            new object[]{"Heightmap","HeightmapBudgetMs",3f,4f,6f},
            new object[]{"Heightmap","CombinedFrameBudgetMs",8f,10f,14f},
            new object[]{"Locations","LocationDeferIfFrameOverMs",12f,15f,20f},
            new object[]{"Locations","SoftCapBudgetMs",8f,10f,14f},
            new object[]{"Locations","HeavyLocationTransforms",80f,100f,150f},
            new object[]{"Dungeon","RoomBudgetMs",6f,8f,12f},
            new object[]{"Dungeon","SoftEntryWallMs",14f,18f,22f},
            new object[]{"General","DistantMaxPerFrame",4f,10f,20f},
            new object[]{"General","NearBacklogThreshold",20f,40f,80f},
        };

        internal static void Bind(ConfigFile c, Action<string> l)
        {
            cfg = c; log = l;
            preset = c.Bind(S, "Preset", 50, new ConfigDescription(
                "One slider for the whole look-vs-speed balance. 0 = Performance (things fade out closer, fewer far shadows and plants, objects appear more gradually), 50 = Balanced (the values you have been playing with), 100 = Quality (draw distances, shadows and grass further out, objects appear sooner; costs more FPS). " +
                "Moving it rewrites the distance/shadow/grass/budget settings below (and SpawnBudget's budgets). After that you can still change any single setting; it is only overwritten again the next time you move this slider. Smoothness fixes (spreading work over frames, warm-ups, smart GC) stay on at every position. Per-PC setting.",
                new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = 100 }));
            driveSb = c.Bind(S, "AlsoSetSpawnBudget", true, new ConfigDescription("Let the slider also set SpawnBudget's per-frame budgets (how fast nearby objects are created).", null, new ConfigurationManagerAttributes { Order = 99 }));
            lastApplied = c.Bind(S, "LastAppliedPreset", "none (values untouched since install)", new ConfigDescription("Info only: which slider value was last applied. If you edited single settings afterwards they are kept until you move the slider.", null, new ConfigurationManagerAttributes { Order = 98, ReadOnly = true }));
            preset.SettingChanged += (s, e) => pendingAt = Time.unscaledTime + 0.6f;
        }

        internal static void Tick()
        {
            if (pendingAt < 0 || Time.unscaledTime < pendingAt) return;
            pendingAt = -1f; Apply(preset.Value);
        }

        static float Lerp3(float p, float a, float b, float c)
        {
            if (p == 50) return b; if (p <= 0) return a; if (p >= 100) return c;
            return p < 50 ? a + (b - a) * (p / 50f) : b + (c - b) * ((p - 50f) / 50f);
        }
        static string Fmt(float v) => (Mathf.Round(v * 100f) / 100f).ToString(CultureInfo.InvariantCulture);

        static int SetAll(ConfigFile c, object[][] rows, int p, string sectionPrefix = "")
        {
            int n = 0;
            foreach (var r in rows)
            {
                var def = new ConfigDefinition(sectionPrefix + (string)r[0], (string)r[1]);
                if (!c.ContainsKey(def)) continue;
                var e = c[def]; float v = Lerp3(p, (float)r[2], (float)r[3], (float)r[4]);
                if (e.SettingType == typeof(int)) e.BoxedValue = Mathf.RoundToInt(v);
                else if (e.SettingType == typeof(float)) e.BoxedValue = (float)Math.Round(v, 2);
                else continue;
                n++;
            }
            return n;
        }

        internal static void Apply(int p)
        {
            try
            {
                int n = SetAll(cfg, RT, p);
                var ld = new ConfigDefinition("2. Layer Cull", "Distances");
                if (cfg.ContainsKey(ld))
                {
                    var names = new[] { "Default_small", "item", "effect", "piece_nonsolid" }; var parts = new List<string>();
                    for (int i = 0; i < 4; i++) parts.Add(names[i] + "=" + Fmt(Lerp3(p, LAYERS[0][i], LAYERS[1][i], LAYERS[2][i])));
                    cfg[ld].BoxedValue = string.Join(",", parts.ToArray()); n++;
                }
                string sbRes = "SpawnBudget off";
                if (driveSb.Value)
                {
                    if (global::ValheimPerformanceFixes.PerformanceFixesPlugin.SpawnBudgetLoaded)
                    { var sc = cfg; int m = SetAll(sc, SB, p, global::ValheimPerformanceFixes.Sections.SB); sc.Save(); sbRes = "SpawnBudget " + m + " values"; }
                    else sbRes = "SpawnBudget not loaded";
                }
// 1.8.0 floors: Smart GC must never go below IdleSec 20 / MinGapSec 120 / HeadroomMB 64 via the slider (they are not in RT rows; this is a safety net).
                try {
                    var idleDef = new ConfigDefinition("5b. Smart GC", "IdleSec");
                    if (cfg.ContainsKey(idleDef) && cfg[idleDef].SettingType == typeof(float) && (float)cfg[idleDef].BoxedValue < 20f) cfg[idleDef].BoxedValue = 20f;
                    var gapDef = new ConfigDefinition("5b. Smart GC", "MinGapSec");
                    if (cfg.ContainsKey(gapDef) && cfg[gapDef].SettingType == typeof(float) && (float)cfg[gapDef].BoxedValue < 120f) cfg[gapDef].BoxedValue = 120f;
                    var hrDef = new ConfigDefinition("5b. Smart GC", "HeadroomMB");
                    if (cfg.ContainsKey(hrDef) && cfg[hrDef].SettingType == typeof(float) && (float)cfg[hrDef].BoxedValue < 64f) cfg[hrDef].BoxedValue = 64f;
                } catch { }
                cfg.Save();
                lastApplied.Value = p + " (" + (p < 34 ? "Performance side" : p > 66 ? "Quality side" : "Balanced") + ") at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                log("Quick Setup: preset " + p + " applied: " + n + " RenderTrim values, " + sbRes);
            }
            catch (Exception e) { log("Quick Setup apply FAILED: " + e.Message); }
        }

        // ---- plain-language descriptions (replace the old technical texts; ranges/attributes are kept) ----
        static readonly Dictionary<string, string> D = new Dictionary<string, string> {
            {"0. General|Enabled","Master switch. Off = RenderTrim does nothing."},
            {"0. General|ABToggleKey","Key combo to switch all of RenderTrim off/on while playing, to compare. Each switch is written to RenderTrim.log."},
            {"0. General|StatsIntervalSec","How often (seconds) a status line is written to BepInEx\\RenderTrim.log."},
            {"0. General|PrefabCensusOn","Diagnostics: every so often list which objects cost the most to draw. Causes a small stutter each time; leave off."},
            {"0. General|PrefabCensusIntervalSec","Seconds between those diagnostic lists (minimum 900)."},
            {"1. Shadows|SmallShadowsOff","Small world objects (stones, cups, small props) stop casting shadows. Barely visible, saves GPU time."},
            {"1. Shadows|SmallShadowMaxSize","Objects smaller than this (meters) count as small. Bigger number = more objects lose their shadow."},
            {"1. Shadows|SkipPrefabPrefixes","Object names (start of name, comma separated) that always keep their shadow."},
            {"1b. Tree Shadows|TreeShadowTrim","Trees far away stop casting shadows; they get them back when you come closer."},
            {"1b. Tree Shadows|TreeShadowDistance","Trees further than this (meters) have no shadow. Higher = nicer, costs more FPS."},
            {"1b. Tree Shadows|TreePrefixes","Which object names count as trees for the setting above."},
            {"2. Layer Cull|Enabled","Hide certain kinds of things (bushes and small rocks, dropped items, effects, decorations) beyond set distances."},
            {"2. Layer Cull|Distances","Distance in meters per kind: Default_small = bushes/small rocks, item = dropped items, effect = effects, piece_nonsolid = decorations. Higher = less pop-in, more FPS cost."},
            {"2. Layer Cull|AllowRaise","Use exactly these distances even if another mod set them shorter."},
            {"3. Small Prop Cull|Enabled","Hide small decorative props (ferns, bushes, pickables) far away. Only the look is hidden; nothing is removed from the world."},
            {"3. Small Prop Cull|Distance","Props further than this (meters) are hidden. Higher = less pop-in, more FPS cost."},
            {"3. Small Prop Cull|HideHysteresis","Extra meters before a visible prop is hidden again, so props don't flicker at the edge."},
            {"3. Small Prop Cull|PickableMaxSize","Pickables (berries, mushrooms...) up to this size (meters) count as small props."},
            {"3. Small Prop Cull|ExtraPrefixes","Extra object names (start of name, comma separated) to treat as small props."},
            {"3b. Grass|Override","Let RenderTrim set grass distance and density."},
            {"3b. Grass|Distance","How far grass is drawn (meters). Higher = nicer, more FPS cost."},
            {"3b. Grass|DensityScale","Grass thickness. 1 = normal, lower = thinner and faster."},
            {"3b. Grass|PatchesPerFrame","How many grass patches are built per frame. Lower = smoother while running, grass fills in a bit later."},
            {"3c. VPO Scene LOD|FarDistanceOverride","How far (meters) the optimizer mod keeps objects in full detail. Higher = less pop-in, more FPS cost."},
            {"Combat Mode|Enabled","During fights, temporarily draw less far-away detail so the fight stays smooth. Close range is never touched."},
            {"Combat Mode|DetectRange","An alerted enemy within this many meters counts as a fight."},
            {"Combat Mode|ExitDelaySec","Seconds without fighting before normal distances come back."},
            {"Combat Mode|EaseSec","Seconds to fade between normal and combat distances (no sudden jumps)."},
            {"Combat Mode|PropDistance","Small-prop distance (meters) during fights."},
            {"Combat Mode|TreeShadowDistance","Tree-shadow distance (meters) during fights."},
            {"Combat Mode|ParticleCullDistance","Particle-effect distance (meters) during fights."},
            {"Combat Mode|NeverHideWithin","Nothing within this many meters of you is ever hidden, fight or not."},
            {"Adaptive|Enabled","When the game is busy (lots loading, low FPS), gently shorten the prop distance; lengthen it again when things calm down."},
            {"Adaptive|LenientRange","Prop distance (meters) when the game is relaxed."},
            {"Adaptive|AggressiveRange","Prop distance (meters) when the game is under heavy load."},
            {"Adaptive|LowPressure","Load level (0-1) at or below which the relaxed distance is used."},
            {"Adaptive|HighPressure","Load level (0-1) at or above which the heavy-load distance is used."},
            {"Adaptive|Strength","How far toward the heavy-load distance it may go (0 = never, 1 = all the way)."},
            {"Adaptive|ShiftSec","Seconds to fade between distances."},
            {"Adaptive|ShowOverlay","Show a small on-screen readout of the current load level."},
            {"4. Particles|ReplaceVpoParticleTags","Use RenderTrim's particle distance instead of the optimizer mod's."},
            {"4. Particles|CullDistance","Particle effects (fire, smoke, sparkles) further than this (meters) stop. Higher = nicer, more FPS cost."},
            {"4. Particles|RateFalloff","Far particle effects emit fewer particles before they stop completely."},
            {"4. Particles|PauseCulledLooping","Pause looping effects that are out of range instead of letting them run unseen."},
            {"5. GC|CollectInMenus","Clean up memory while a menu is open, so the game does it less often during play (each clean-up is a short freeze)."},
            {"5. GC|MenuMinOpenSec","Menu must be open this long (seconds) before a clean-up."},
            {"5. GC|HeadroomMB","Only clean up if at least this much memory (MB) has piled up."},
            {"5. GC|MinGapSec","Minimum seconds between clean-ups."},
            {"5. GC|CollectWhenIdle","Also clean up when you stand still (see Smart GC for the better version)."},
            {"5. GC|IdleSec","Seconds standing still for the setting above."},
            {"5b. Smart GC|Enabled","Clean up memory only when you have been truly still (not walking, not fighting). Needs heap growth above HeadroomMB and MinGapSec spacing. Never while moving. Loading-screen clean-ups are separate. Off = no smart clean-ups during play."},
            {"5b. Smart GC|IdleSec","Seconds of genuine stillness before a clean-up may run (default 20). Never during move/combat. The Quick Setup slider never lowers this below 20."},
            {"5b. Smart GC|MinGapSec","Minimum seconds between these clean-ups (default 120). Higher = fewer freezes while exploring. Quick Setup never goes below 120."},
            {"5b. Smart GC|HeadroomMB","Only clean up if at least this much memory (MB) has piled up since the last one (default 64). Quick Setup never goes below 64."},
            {"5b. Smart GC|CombatRange","Never clean up with an alerted enemy within this many meters."},
            {"5b. Smart GC|AllowInMapOrInventory","Also clean up while the map or inventory is open (still not while moving or fighting)."},
            {"5b. Smart GC|MoveEpsilon","Horizontal speed (m/s) above this counts as moving â€” clean-up never runs while you are moving."},
            {"6. LOD|LodBiasMultiplier","Detail level of objects at a distance. Above 1 = more detail further out (less pop-in, more FPS cost); below 1 = faster."},
            {"6b. Auto Scale|Enabled","If FPS drops below TargetFps, lower far detail a little until it recovers; raise it again afterwards."},
            {"6b. Auto Scale|TargetFps","FPS to aim for. Higher = detail is lowered sooner."},
            {"6b. Auto Scale|MinLodBiasScale","Lowest far-detail level Auto Scale may go to (1 = never lower)."},
            {"6b. Auto Scale|MinFarDistanceScale","Shortest view range Auto Scale may go to (1 = never shorter)."},
            {"6b. Auto Scale|StepDown","How much it lowers per step."},
            {"6b. Auto Scale|StepUp","How much it raises per step."},
            {"6b. Auto Scale|HoldSec","Seconds FPS must be low before lowering."},
            {"6b. Auto Scale|RecoverHoldSec","Seconds FPS must be good before raising."},
            {"6b. Auto Scale|RecoverFpsMargin","FPS must be this much above target (1.15 = 15%) before raising."},
            {"7. VPO Unload|DeferVpoAssetUnload","Delay the optimizer mod's memory unloading to loading screens instead of during play (it causes a freeze)."},
            {"7. VPO Unload|RunInPauseMenu","Also allow that unloading while the pause menu is open."},
            {"7. VPO Unload|DeathScreenCountsAsLoading","Treat the death screen as a loading screen for the setting above."},
            {"7. VPO Unload|TeleportGraceSec","Seconds after a teleport during which unloading still counts as 'loading'."},
            {"8. Streaming|InWorldBackgroundLoadingLow","Load assets in the background at low priority while playing, to avoid stutter."},
            {"8. Loot Stacking|EarlyAutoStack","Merge piles of dropped items sooner so big loot drops don't slow the game."},
            {"8. Loot Stacking|StackThreshold","Start merging when this many dropped items are nearby."},
            {"8. Loot Stacking|VanillaItemTerrainSnap","Keep the game's normal 'dropped items rest on the ground' behaviour."},
            {"9. UI Warm-up|WarmupCanvases","Build the map window once during loading, so the first map open is quick."},
            {"9. UI Warm-up|InventoryPreload","Open the inventory invisibly once during loading, so the first real open is quick."},
            {"9. UI Warm-up|InventoryPreloadCraftingPanel","Also prepare the crafting list (slower load, quicker first craft)."},
            {"9. UI Warm-up|InventoryPreloadGc","Clean up memory right after the inventory preload."},
            {"9. UI Warm-up|InventoryPreloadFallbackSec","If loading ends too early, do the preload this many seconds after you appear (when it is quiet)."},
            {"9. UI Warm-up|InventoryPreloadSplit","Split the inventory preload over two frames."},
            {"9. UI Warm-up|InventoryPreloadPreGc","Clean up memory once during loading (1.7.8: after the preload, while still black)."},
            {"9. UI Warm-up|SpreadOverFrames","Do the warm-up steps one per frame instead of all at once."},
            {"10. Audio Routing|Enabled","Make sounds from mods follow your in-game volume sliders."},
            {"10. Audio Routing|RemapForeignMixers","Fix mod sounds that use their own volume channel."},
            {"10. Audio Routing|AssignUnroutedToSfx","Mod sounds with no volume channel use the sound-effects slider."},
            {"11. Animation Fixes|FixStuckAnimatorSpeed","Fix your character getting stuck in slow/fast motion after an attack (a combat-mod bug)."},
            {"11. Animation Fixes|GraceSec","Seconds to wait after an attack before fixing."},
            {"11. Animation Fixes|LogResets","Write each fix to RenderTrim.log."},
            {"11. Animation Fixes|ResetDualMasteryLease","Also reset the dual-wield mod's own speed state."},
            {"12. Drop Fixes|DisableMonsterMayhemDropCap","Creatures drop all their loot types (the monster mod normally limits it to 3)."},
            {"12. Drop Fixes|ExtraSlotsInsteadOfDisable","0 = no limit. Above 0 = keep the limit but allow this many extra loot types."},
            {"12. Drop Fixes|LogCapBypass","Write each affected kill to the log."},
            {"Mod Throttles|VPOLightCulling_Throttle","Run the optimizer mod's light check less often (saves a little time every frame)."},
            {"Mod Throttles|VPOLightCulling_EveryNFrames","Run it every this many frames."},
            {"Mod Throttles|ItemRarityVFX_FarSkip","Skip rarity glow updates on far-away dropped items."},
            {"Mod Throttles|ItemRarityVFX_Distance","Meters for the setting above."},
            {"Mod Throttles|AnimalFeeding_Throttle","Update far-away animals' feeding less often."},
            {"Mod Throttles|AnimalFeeding_Distance","Meters for the setting above."},
            {"Mod Throttles|AnimalFeeding_EveryNFrames","Update them every this many frames."},
            {"Mod Throttles|ZenHoverItem_Throttle","Update the hover-item tooltip less often."},
            {"Mod Throttles|ZenHoverItem_EveryNFrames","Update it every this many frames."},
            {"Mod Throttles|BAO_InspiredRunestone_Prewarm","Prepare a mod's rune-stone effect during loading so the first read doesn't stutter."},
            {"Mod Throttles|FullRateInCombat","During fights all the throttles above are paused (everything runs every frame)."},
        };
        internal static void Describe(ConfigFile c)
        {
            var f = typeof(ConfigEntryBase).GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) return;
            foreach (var def in c.Keys)
            {
                if (!D.TryGetValue(def.Section + "|" + def.Key, out var txt)) continue;
                var e = c[def]; var old = e.Description;
                f.SetValue(e, new ConfigDescription(txt, old?.AcceptableValues, old?.Tags ?? new object[0]));
            }
        }
    }
}