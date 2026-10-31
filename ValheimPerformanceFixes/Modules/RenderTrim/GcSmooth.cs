// Valheim Performance Fixes - memory cleanup smoothing (2.1.2-test). MIT License.
// Client only. Three parts, each with its own switch under "20. Memory Cleanup Smoothing":
//  1. Adaptive incremental GC: on frames that finished under the frame budget, Unity's incremental garbage collector gets a
//     bigger slice (up to MaxSliceMs) and, while a collection cycle is running or the heap has grown a lot, this mod spends
//     part of the spare time on an extra GarbageCollector.CollectIncremental step. Busy frames, combat and loading screens
//     put the slice straight back to the base value. Goal: finish collections in small pieces before the heap forces a long
//     stop-the-world finish.
//  2. Quiet-moment cleanup: small incremental steps while nothing is going on (standing still out of combat, pause menu,
//     paused game, inventory / map open out of combat). Never a full GC.Collect, so it does not compete with the smart UI /
//     idle collections in "5. GC"; it skips the second after one of those ran.
//  3. Garbage sampler: managed bytes allocated inside the main per-frame update paths (MonoUpdaters, ZNet, ZNetScene, ...)
//     so the periodic stats can name the top garbage producers.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;
using ValheimPerformanceFixes;

namespace RenderTrim
{
    internal sealed class GcSmoothRunner : MonoBehaviour
    {
        void Update() { GcSmooth.FrameStart(); }
        void LateUpdate() { GcSmooth.FrameEnd(); }
    }

    internal static class GcSmooth
    {
        const string S = "20. Memory Cleanup Smoothing";
        static ConfigEntry<bool> adOn, quietOn, sampOn;
        static ConfigEntry<float> baseMs, maxMs, budgetMs, extraMaxMs, startGrowMB, quietIdleSec, quietStepMs, quietGrowMB, blockingMs, busyWorkMs;
        static ConfigEntry<string> sampHooks; static ConfigEntry<int> sampTop;
        static Action<string> log, logFile;

        static bool incOk; static string incState = "?"; static string bootSlice = "?";
        static ulong unitySliceNs = 3000000UL; static bool sliceOurs; static ulong sliceSet;

        // ---- stats (reset with each stats line) ----
        static readonly Stopwatch since = Stopwatch.StartNew();
        static int sRaisedFrames, sSteps, sQuietSteps, sCyclesOurs, sCyclesQuiet, sCollections, sBlocking, sBusyFrames;
        static double sRaisedSum, sStepMs, sStepMax, sQuietMs, sWorstGc, sAllGcMs; static string sWorstWhen = "-";
        static int ownFinishedAt = -1; // GC.CollectionCount(0) right after a collection finished inside one of our steps
        static bool cycleOpen; // the last CollectIncremental said more work is left

        internal static void Init(ConfigFile cfg, Harmony h, Action<string> L, Action<string> LF)
        {
            log = L; logFile = LF;
            adOn = cfg.Bind(S, "AdaptiveIncremental", true, "On frames that finished under the frame budget, give Unity's incremental garbage collector a bigger slice of the frame (up to MaxSliceMs) and, while a collection is running or the heap has grown by StartCycleGrowMB, spend part of the spare time on an extra incremental step. Busy frames, combat and loading screens put the slice back to BaseSliceMs. Steps aside when \"5. GC\" SmallIncrementalSlice is on or another mod controls the GC (Mod Overlap).");
            baseMs = cfg.Bind(S, "BaseSliceMs", 3f, new ConfigDescription("Incremental GC slice per frame on busy frames / in combat (Unity's default in Valheim is 3 ms, set by gc-max-time-slice in boot.config).", new AcceptableValueRange<float>(0.5f, 5f)));
            maxMs = cfg.Bind(S, "MaxSliceMs", 5f, new ConfigDescription("Largest incremental GC slice per frame on calm frames with spare time.", new AcceptableValueRange<float>(1f, 10f)));
            budgetMs = cfg.Bind(S, "FrameBudgetMs", 0f, new ConfigDescription("Frame time that counts as 'on budget'. 0 = automatic: the frame cap / vsync period if one is set, otherwise 12 ms (about 83 fps). Spare time = budget - last frame - 1 ms.", new AcceptableValueRange<float>(0f, 50f)));
            extraMaxMs = cfg.Bind(S, "ExtraStepMaxMs", 2f, new ConfigDescription("Most time per frame for this mod's own extra incremental step (out of the spare time).", new AcceptableValueRange<float>(0f, 5f)));
            startGrowMB = cfg.Bind(S, "StartCycleGrowMB", 128f, new ConfigDescription("Heap growth since the last collection at which calm frames start an incremental collection early (instead of waiting for the heap to force one).", new AcceptableValueRange<float>(16f, 1024f)));
            busyWorkMs = cfg.Bind(S, "BusyWorldWorkMs", 2f, new ConfigDescription("A frame counts as busy when this mod's world work (spawns, zones, saves, ...) in it took longer than this.", new AcceptableValueRange<float>(0.5f, 20f)));
            quietOn = cfg.Bind(S, "QuietCleanup", true, "Small incremental GC steps at quiet moments: standing still out of combat for QuietIdleSec, pause menu / paused game, or inventory / map open out of combat. Never a full collection; skips the second after a \"5. GC\" collection.");
            quietIdleSec = cfg.Bind(S, "QuietIdleSec", 3f, new ConfigDescription("Seconds standing still (out of combat) before quiet cleanup steps start.", new AcceptableValueRange<float>(1f, 60f)));
            quietStepMs = cfg.Bind(S, "QuietStepMs", 1f, new ConfigDescription("Length of one quiet cleanup step per frame.", new AcceptableValueRange<float>(0.25f, 3f)));
            quietGrowMB = cfg.Bind(S, "QuietStartGrowMB", 32f, new ConfigDescription("Heap growth since the last collection at which quiet moments start an incremental collection.", new AcceptableValueRange<float>(8f, 512f)));
            blockingMs = cfg.Bind(S, "BlockingCollectionMs", 20f, new ConfigDescription("Stats only: a collection whose frame ran this much over the usual frame time counts as a blocking (full stop-the-world) collection.", new AcceptableValueRange<float>(5f, 200f)));
            sampOn = cfg.Bind(S, "GarbageSampler", true, "Test builds: measure managed memory allocated inside the main per-frame update paths and list the top garbage producers in the periodic stats. Low overhead (two counters per hooked call). Takes effect at the next start.");
            sampHooks = cfg.Bind(S, "SamplerHooks", "MonoUpdaters.FixedUpdate,MonoUpdaters.Update,MonoUpdaters.LateUpdate,ZNetScene.Update,ZNet.Update,ZoneSystem.Update,EnvMan.Update,Game.Update,Hud.Update,Hud.LateUpdate,InventoryGui.Update,Minimap.Update,GameCamera.LateUpdate,ClutterSystem.LateUpdate,AudioMan.Update,Chat.Update,MessageHud.Update,WearNTearUpdater.Update,Menu.Update", "Type.Method list (Valheim classes) the garbage sampler measures. Takes effect at the next start.");
            sampTop = cfg.Bind(S, "SamplerTopN", 6, new ConfigDescription("How many producers the stats line lists.", new AcceptableValueRange<int>(1, 20)));
            adOn.SettingChanged += (s, e) => { if (!adOn.Value) RestoreSlice(); };

            Probe();
            if (sampOn.Value) try { Sampler.Init(h, sampHooks.Value, log); } catch (Exception e) { log("[Memory] garbage sampler failed to start: " + e.Message); }
            try { Plugin.I.gameObject.AddComponent<GcSmoothRunner>(); } catch (Exception e) { log("[Memory] could not add the per-frame runner: " + e.Message); }
        }

        static void Probe()
        {
            try
            {
                incOk = GarbageCollector.isIncremental;
                var mode = GarbageCollector.GCMode;
                unitySliceNs = Plugin.UnitySliceNs();
                try
                {
                    string p = Path.Combine(Application.dataPath, "boot.config"); // read only
                    if (File.Exists(p)) { bootSlice = "not set"; foreach (var line in File.ReadAllLines(p)) if (line.StartsWith("gc-max-time-slice")) bootSlice = line.Trim(); }
                    else bootSlice = "boot.config not found";
                }
                catch (Exception e) { bootSlice = "unreadable (" + e.Message + ")"; }
                incState = "isIncremental=" + incOk + " mode=" + mode + " unitySlice=" + (unitySliceNs / 1e6).ToString("0.00") + "ms boot.config " + bootSlice;
                if (incOk) log("[Memory] incremental GC is ON (" + incState + "). Adaptive slice " + (adOn.Value ? "on" : "off") + " base=" + baseMs.Value.ToString("0.0") + "ms max=" + maxMs.Value.ToString("0.0") + "ms, quiet cleanup " + (quietOn.Value ? "on" : "off") + ", garbage sampler " + (sampOn.Value ? "on" : "off"));
                else log("[Memory] incremental GC is OFF in this game build (" + incState + "). Adaptive slice and quiet steps do nothing; Unity only turns it on from the build setting (boot.config gc-max-time-slice / player setting), which this mod does not change. Garbage sampler still runs.");
            }
            catch (Exception e) { incOk = false; incState = "probe failed: " + e.Message; log("[Memory] incremental GC probe failed: " + e.Message); }
        }

        static bool Controlling()
        {
            if (!incOk || !Plugin.Active) return false;
            try { if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled) return false; } catch { return false; }
            if (Plugin.SmallSliceOn()) return false;
            try { if (Overlap.Yield(Feature.Gc)) return false; } catch { }
            return true;
        }

        static void SetSlice(ulong ns)
        {
            if (sliceOurs && ns == sliceSet) return;
            try { GarbageCollector.incrementalTimeSliceNanoseconds = ns; sliceOurs = true; sliceSet = ns; } catch { }
        }
        static void RestoreSlice()
        {
            if (!sliceOurs) return;
            sliceOurs = false;
            try { if (!Plugin.SmallSliceOn()) GarbageCollector.incrementalTimeSliceNanoseconds = unitySliceNs; } catch { }
        }

        static float Budget()
        {
            float b = budgetMs.Value;
            if (b > 0f) return b;
            try
            {
                if (QualitySettings.vSyncCount > 0) { double hz = Screen.currentResolution.refreshRateRatio.value; if (hz > 1) return (float)(1000.0 / hz * QualitySettings.vSyncCount); }
                int tf = Application.targetFrameRate; if (tf > 0) return 1000f / tf;
            }
            catch { }
            return 12f;
        }

        static double Step(double ms, bool quiet)
        {
            int before = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            bool more = true;
            try { more = GarbageCollector.CollectIncremental((ulong)(ms * 1e6)); } catch { more = false; }
            sw.Stop();
            double took = sw.Elapsed.TotalMilliseconds;
            int after = GC.CollectionCount(0);
            if (after != before) { ownFinishedAt = after; if (quiet) sCyclesQuiet++; else sCyclesOurs++; }
            cycleOpen = more && after == before;
            if (quiet) { sQuietSteps++; sQuietMs += took; } else { sSteps++; sStepMs += took; }
            if (took > sStepMax) sStepMax = took;
            FrameCost.Add("GC", took, quiet ? "quiet incremental step (this mod)" : "extra incremental step (this mod)");
            return took;
        }

        internal static void FrameStart() { if (Sampler.On) Sampler.FrameStart(); }

        internal static void FrameEnd()
        {
            if (Sampler.On) Sampler.FrameEnd();
            bool ctl = Controlling();
            if (!ctl || !adOn.Value) RestoreSlice();
            if (!ctl) return;
            float t = Time.unscaledTime, dt = Time.unscaledDeltaTime * 1000f;
            var pl = Player.m_localPlayer;
            bool loading = false, black = false, menu = false;
            try { loading = Plugin.LoadingScreen(); black = Plugin.HudBlackNow(); menu = Plugin.BlockingScreen(); } catch { }
            if (loading || black) { RestoreSliceToBase(); return; } // the screen GC in "5. GC" handles loading screens
            bool combat = Combat.inCombat;
            if (!combat && pl != null) try { combat = Plugin.I.CombatNow(pl); } catch { }
            float sinceForced = t - Plugin.I.LastForcedGcAt;
            long grown = 0; try { grown = GC.GetTotalMemory(false) - Plugin.I.UsedAfterGc; } catch { }

            // 2. quiet moments
            if (quietOn.Value && sinceForced > 1f)
            {
                bool uiOpen = false; try { uiOpen = InventoryGui.IsVisible() || Minimap.IsOpen(); } catch { }
                bool idle = pl != null && !combat && t - Plugin.I.StillSince >= quietIdleSec.Value;
                bool quiet = menu || (!combat && (uiOpen || idle));
                if (quiet && (cycleOpen || grown >= (long)(quietGrowMB.Value * 1048576f)))
                {
                    if (adOn.Value) RestoreSliceToBase();
                    Step(quietStepMs.Value, true);
                    return;
                }
            }

            // 1. adaptive slice on world frames
            if (!adOn.Value) return;
            float budget = Budget();
            float spare = budget - dt - 1f;
            bool busy = spare <= 0f || combat || FrameCost.WorldBuildBusy || FrameWork() > busyWorkMs.Value || sinceForced < 1f;
            if (busy) { sBusyFrames++; RestoreSliceToBase(); return; }
            double extra = 0;
            if (extraMaxMs.Value > 0f && spare >= 0.5f && (cycleOpen || grown >= (long)(startGrowMB.Value * 1048576f)))
                extra = Math.Min(spare, extraMaxMs.Value);
            double baseV = BaseNs() / 1e6;
            double slice = Math.Max(baseV, Math.Min(Math.Max(maxMs.Value, baseV), baseV + Math.Max(0, spare - extra)));
            SetSlice((ulong)(slice * 1e6));
            if (slice > baseV + 0.05) { sRaisedFrames++; sRaisedSum += slice; }
            if (extra > 0) Step(extra, false);
        }

        static ulong BaseNs() => (ulong)(Mathf.Clamp(baseMs.Value, 0.5f, 5f) * 1e6f);
        static void RestoreSliceToBase() { if (adOn.Value) SetSlice(BaseNs()); }

        // this mod's world work measured in the current frame (FrameCost ledger, GC excluded)
        static double FrameWork() { try { return FrameCost.FrameMs(Time.frameCount, "GC"); } catch { return 0; } }

        // called from RenderTrim's GcTick whenever GC.CollectionCount(0) moved without a forced collection
        // returns true when the collection was finished inside one of this mod's incremental steps (already charged to the frame)
        internal static bool OnCollection(int count, float estMs)
        {
            bool ours = ownFinishedAt == count;
            cycleOpen = false;
            if (!ours)
            {
                sCollections++; sAllGcMs += estMs;
                if (blockingMs != null && estMs >= blockingMs.Value) sBlocking++;
                if (estMs > sWorstGc) { sWorstGc = estMs; sWorstWhen = DateTime.Now.ToString("HH:mm:ss"); }
            }
            return ours;
        }

        internal static string Stats()
        {
            if (adOn == null) return "memory smoothing not started";
            double min = Math.Max(0.1, since.Elapsed.TotalMinutes); since.Reset(); since.Start();
            var sb = new StringBuilder();
            sb.Append("incGC ").Append(incOk ? "on" : "OFF").Append(" (").Append(bootSlice).Append(")");
            sb.Append(" adaptive=").Append(adOn.Value ? (Controlling() ? "on" : "yielding") : "off");
            sb.Append(" sliceRaisedFrames=").Append(sRaisedFrames).Append(" avgRaised=").Append(sRaisedFrames > 0 ? (sRaisedSum / sRaisedFrames).ToString("0.0") + "ms" : "-").Append(" busyFrames=").Append(sBusyFrames);
            sb.Append(" | extraIncremental steps=").Append(sSteps).Append(" used=").Append(sStepMs.ToString("0")).Append("ms (").Append((sStepMs / min).ToString("0")).Append("ms/min) cyclesFinished=").Append(sCyclesOurs);
            sb.Append(" | quiet steps=").Append(sQuietSteps).Append(" used=").Append(sQuietMs.ToString("0")).Append("ms cyclesFinished=").Append(sCyclesQuiet);
            sb.Append(" | worstStep=").Append(sStepMax.ToString("0.0")).Append("ms");
            sb.Append(" | otherCollections n=").Append(sCollections).Append(" (").Append((sCollections / min).ToString("0.0")).Append("/min) blocking>=").Append(blockingMs.Value.ToString("0")).Append("ms n=").Append(sBlocking).Append(" (").Append((sBlocking / min).ToString("0.0")).Append("/min) worst=").Append(sWorstGc.ToString("0")).Append("ms at ").Append(sWorstWhen);
            sb.Append("\n   garbage: ").Append(Sampler.On ? Sampler.Stats(sampTop.Value, min) : "sampler off");
            sRaisedFrames = sSteps = sQuietSteps = sCyclesOurs = sCyclesQuiet = sCollections = sBlocking = sBusyFrames = 0;
            sRaisedSum = sStepMs = sStepMax = sQuietMs = sWorstGc = sAllGcMs = 0; sWorstWhen = "-";
            return sb.ToString();
        }

        // ---- 3. garbage sampler ----
        internal static class Sampler
        {
            internal static bool On;
            static string method = "?";
            static bool perThread;
            static readonly Dictionary<MethodBase, int> index = new Dictionary<MethodBase, int>();
            static readonly List<string> names = new List<string>();
            static long[] bytes = new long[0]; static int[] calls = new int[0];
            static long frameStart; static int frameGc; static bool frameOk;
            static long frameTotal, frameMax, hookedTotal; static int frames, framesSkipped;

            internal struct Mark { public long b; public int gc; }

            internal static long Now() => perThread ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalMemory(false);

            internal static void Init(Harmony h, string list, Action<string> L)
            {
                // GetAllocatedBytesForCurrentThread is exact where the runtime supports it; Unity's Mono (Boehm GC) may return 0.
                try
                {
                    long a = GC.GetAllocatedBytesForCurrentThread(); var junk = new byte[64 * 1024]; long b2 = GC.GetAllocatedBytesForCurrentThread();
                    perThread = b2 - a >= 64 * 1024 && junk.Length > 0;
                }
                catch { perThread = false; }
                method = perThread ? "allocated bytes (exact)" : "heap-size deltas (rough: counts heap growth, misses reuse of freed blocks)";
                var pre = new HarmonyMethod(typeof(Sampler).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
                var post = new HarmonyMethod(typeof(Sampler).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
                var ok = new StringBuilder(); var missing = new StringBuilder();
                foreach (var raw in list.Split(','))
                {
                    var s = raw.Trim(); int dot = s.LastIndexOf('.'); if (dot <= 0) continue;
                    MethodInfo m = null;
                    try { var ty = AccessTools.TypeByName(s.Substring(0, dot)); if (ty != null) m = AccessTools.DeclaredMethod(ty, s.Substring(dot + 1), Type.EmptyTypes); } catch { }
                    if (m == null || index.ContainsKey(m)) { missing.Append(missing.Length > 0 ? ", " : "").Append(s); continue; }
                    index[m] = names.Count; names.Add(s);
                    try
                    {
                        string others = "";
                        try { var info = Harmony.GetPatchInfo(m); if (info != null) { var own = new HashSet<string>(); foreach (var p in info.Prefixes) own.Add(p.owner); foreach (var p in info.Postfixes) own.Add(p.owner); foreach (var p in info.Transpilers) own.Add(p.owner); own.Remove(h.Id); if (own.Count > 0) others = " (also patched by " + string.Join("/", own) + ")"; } } catch { }
                        h.Patch(m, prefix: pre, postfix: post);
                        ok.Append(ok.Length > 0 ? ", " : "").Append(s).Append(others);
                    }
                    catch (Exception e) { missing.Append(missing.Length > 0 ? ", " : "").Append(s + " (" + e.Message + ")"); }
                }
                bytes = new long[names.Count]; calls = new int[names.Count];
                On = true;
                L("[Memory] garbage sampler on, method: " + method + ". Measuring: " + ok + (missing.Length > 0 ? ". Not found / skipped: " + missing : "") + ". Note: patches by other mods on these methods count toward that method.");
            }

            static void Pre(out Mark __state) { __state.b = Now(); __state.gc = GC.CollectionCount(0); }
            static void Post(Mark __state, MethodBase __originalMethod)
            {
                if (GC.CollectionCount(0) != __state.gc) return; // a collection inside the call makes the heap delta meaningless
                long d = Now() - __state.b; if (d <= 0) return;
                if (__originalMethod != null && index.TryGetValue(__originalMethod, out int i)) { bytes[i] += d; calls[i]++; hookedTotal += d; }
            }

            internal static void FrameStart()
            {
                // whole-frame window from this runner's Update to the next one
                long n = Now(); int gc = GC.CollectionCount(0);
                if (frameOk)
                {
                    if (gc == frameGc) { long d = n - frameStart; if (d > 0) { frameTotal += d; if (d > frameMax) frameMax = d; } frames++; }
                    else framesSkipped++;
                }
                frameStart = n; frameGc = gc; frameOk = true;
            }
            internal static void FrameEnd() { }

            internal static string Stats(int top, double min)
            {
                var sb = new StringBuilder();
                sb.Append("method=").Append(perThread ? "exact" : "heapDelta");
                double perF = frames > 0 ? frameTotal / (double)frames : 0;
                sb.Append(" frame avg=").Append((perF / 1024).ToString("0")).Append("KB max=").Append((frameMax / 1024).ToString("0")).Append("KB total=").Append((frameTotal / 1048576.0 / min).ToString("0")).Append("MB/min (").Append(frames).Append(" frames, ").Append(framesSkipped).Append(" with a GC skipped)");
                var order = new List<int>(); for (int i = 0; i < names.Count; i++) if (bytes[i] > 0) order.Add(i);
                order.Sort((a, b) => bytes[b].CompareTo(bytes[a]));
                sb.Append(" | top:");
                int k = 0;
                foreach (int i in order)
                {
                    if (k++ >= top) break;
                    sb.Append(' ').Append(names[i]).Append(' ').Append((bytes[i] / 1048576.0 / min).ToString("0.0")).Append("MB/min");
                    if (frameTotal > 0) sb.Append(" (").Append((100.0 * bytes[i] / frameTotal).ToString("0")).Append("%)");
                    sb.Append(',');
                }
                if (k == 0) sb.Append(" none measured,");
                long other = frameTotal - hookedTotal;
                sb.Append(" notHooked ").Append((Math.Max(0, other) / 1048576.0 / min).ToString("0.0")).Append("MB/min");
                for (int i = 0; i < bytes.Length; i++) { bytes[i] = 0; calls[i] = 0; }
                frameTotal = frameMax = hookedTotal = 0; frames = framesSkipped = 0;
                return sb.ToString();
            }
        }
    }
}
