using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceFixes
{
    // 2.3.0 / 2.3.1: "0. Mod Overlap". Where ValheimPerformanceOverhaul (VPO) or FiresGhettoNetworkMod (FGN) do the same job as a
    // feature here, this mod's version is in charge by default (2.3.1). Each feature has a Cede_<feature> switch: true hands
    // that job back to the other mod, and this mod's version then steps aside while the other mod's version is really on.
    // Where both running at once would do the work twice, the other mod's matching setting is switched off in memory for as long
    // as this mod's version is active (never written to that mod's .cfg file; a save of that file writes the user's own value)
    // and is put back on cede or when this mod's version stops. Each decision is logged under [Mod Overlap].
    internal enum Feature { SpawnBudget, AutoScale, SceneLod, Grass, Particles, LayerCull, TreeShadows, Gc, OrphanPrune, RemovalCap, IdlePass }

    internal static class Overlap
    {
        internal const string Section = "0. Mod Overlap";
        const string VpoGuid = "com.Skarif.ValheimPerformanceOverhaul_WATER", FgnGuid = "com.Fire.FiresGhettoNetworkMod";
        const string FgnMain = "FiresGhettoNetworkMod.FiresGhettoNetworkMod";
        const float RecheckSec = 5f;
        const int Count = 11;

        static readonly ConfigEntry<bool>[] cede = new ConfigEntry<bool>[Count];
        static readonly bool[] yielding = new bool[Count];
        static readonly bool[] known = new bool[Count];
        static readonly string[] reason = new string[Count];
        static readonly string[] lastState = new string[Count];
        static readonly Func<bool>[] ours = new Func<bool>[Count];
        static float next = -1f;
        static int version;

        // what the other mod does, per feature (shown in the F1 description)
        static readonly string[] Desc =
        {
            "object creation budget while you play. Not ceded: this mod budgets object creation and FiresGhettoNetworkMod's 'Enable Time-Slice Instantiation' is switched off in memory while it does. Ceded: FGN's time slice does it when that is on (arrival screen, player-build detection and all location / dungeon / zone work stay here either way)",
            "frame-rate driven scaling. Not ceded: Auto Scale is in charge and VPO's adaptive resolution is switched off in memory while Auto Scale runs (VPO then puts the render scale back to full). Ceded: VPO's adaptive resolution runs and Auto Scale only steps LOD down once VPO's render scale is at its minimum",
            "VPO Scene LOD far distance. Not ceded: this mod's FarDistance value is used. Ceded: VPO's own far distance is left alone (Auto Scale still scales it)",
            "grass distance / density. Not ceded: this mod's grass values are applied. Ceded: VPO's grass values are left alone when its vegetation optimizer patched the grass",
            "particle handling. Not ceded: this mod replaces VPO's per-particle-system tags with its own. Ceded: VPO's particle optimizer keeps its tags",
            "layer cull distances. Not ceded: this mod's distances apply, raising allowed. Ceded: never raise a distance VPO's render optimizer set lower",
            "tree shadow trim. Not ceded: always runs. Ceded: stands down when VPO's shadow budget already ends all shadows at or before TreeShadowDistance",
            "forced GCs (menu / idle / smart GC). Not ceded: run alongside VPO's GC control (they do different things: VPO blocks collections during combat, this one collects at quiet moments). Ceded: stand down when VPO's GC control is on",
            "dead-instance repair in ZNetScene.RemoveObjects. Not ceded: always runs. Ceded: on a dedicated server, left to FiresGhettoNetworkMod when its 'Enable Instance Orphan Prune' is on",
            "removal cap (LimitRemovalsPerFrame). Not ceded: this cap runs and FiresGhettoNetworkMod's 'Max Destroys Per Frame' is set to 0 (its vanilla setting) in memory while it does. Ceded: FGN's throttle handles removals while its limit is above 0",
            "idle object pass skip (22. Object Pass). Not ceded: this mod skips the object create/remove pass while nothing changed, and VPO's sector scene cache stands down by itself because it sees another mod's patch on that pass. Ceded: this mod's skip is removed again and VPO's cache takes over within about 5 s when VPO's 'SectorCache' is on",
        };

        internal static void Bind(ConfigFile c)
        {
            for (int i = 0; i < Count; i++)
            {
                cede[i] = c.Bind(Section, "Cede_" + ((Feature)i), false, "False (default) = this mod's version is in charge. true = hand the job to the other mod. Covers: " + Desc[i] + ".");
                cede[i].SettingChanged += (s, e) => next = -1f;
            }
            spread = c.Bind(Section, "SpreadRechecks", true, "While the world is visible, the 5-second re-check of the other mods' settings is done one feature per frame instead of all features in one frame (a few reflection reads each). Same checks, same order, same results. Off = all at once, as before.");
        }
        static ConfigEntry<bool> spread;
        static int step = -1, stepFrame = -1;

        // Each module says when its own version of a feature is actually running (used for the in-memory switch-offs).
        internal static void SetOurs(Feature f, Func<bool> active) { ours[(int)f] = active; next = -1f; }
        internal static bool Ceded(Feature f) { var c = cede[(int)f]; return c != null && c.Value; }

        // Main thread only. True = the feature should step aside right now (it is ceded AND the other mod's version is on).
        internal static bool Yield(Feature f) { Tick(); return yielding[(int)f]; }
        // regular driver (called from the modules' Update); cheap between rechecks
        internal static void Tick()
        {
            if (cede[0] == null) return;
            float t = Time.realtimeSinceStartup;
            if (step < 0 && t >= next) { next = t + RecheckSec; step = 0; passFrame0 = Time.frameCount; passMax = 0; passSpread = false; }
            if (step < 0 || inTick) return;
            inTick = true;
            try { TickStep(); } finally { inTick = false; }
        }
        static bool inTick;
        static void TickStep()
        {
            bool visible; try { visible = Player.m_localPlayer != null; } catch { visible = false; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (spread == null || !spread.Value || !visible) { while (step >= 0) Step(); sRcWhole++; } // menu / off: the whole pass at once
                else { int f = Time.frameCount; if (f == stepFrame) return; stepFrame = f; Step(); sRcSteps++; passSpread = true; }
            }
            catch (Exception e) { step = -1; Log("recheck failed: " + e.Message); }
            double ms = sw.Elapsed.TotalMilliseconds;
            if (visible) FrameCost.Add("overlap", ms, "mod overlap re-check");
            // 2.3.8: SpreadRechecks counters and one log line for the first spread pass
            if (ms > passMax) passMax = ms; if (ms > sRcMaxMs) sRcMaxMs = ms; sRcMs += ms;
            if (step < 0)
            {
                sRcPasses++; if (passSpread) sRcSpreadPasses++;
                if (passSpread && !rcLogged) { rcLogged = true; Log("SpreadRechecks: re-check pass spread over " + (Time.frameCount - passFrame0 + 1) + " frames, longest piece " + passMax.ToString("0.00") + " ms (counted in STATS)"); }
            }
        }
        static int sRcPasses, sRcSpreadPasses, sRcSteps, sRcWhole, passFrame0; static double sRcMs, sRcMaxMs, passMax; static bool passSpread, rcLogged;
        internal static string RecheckStats()
        {
            string s = "spreadRechecks " + (spread != null && spread.Value ? "on" : "off") + " passes=" + sRcPasses + " spreadPasses=" + sRcSpreadPasses + " pieces=" + sRcSteps + " wholeAtOnce=" + sRcWhole + " totalMs=" + sRcMs.ToString("0.0") + " longestPieceMs=" + sRcMaxMs.ToString("0.00");
            sRcPasses = sRcSpreadPasses = sRcSteps = sRcWhole = 0; sRcMs = sRcMaxMs = 0;
            return s;
        }
        // 2.3.7: one piece of the re-check: first the switch-offs (so ceded features probe the other mod's real value), then each feature
        static void Step()
        {
            int nh = holds.Count;
            if (step < nh) { var h = holds[step]; try { Drive(h); } catch (Exception e) { Log(h.f + ": switch-off check failed (" + e.GetType().Name + ": " + e.Message + ")"); } }
            else if (step - nh < Count) CheckOne(step - nh);
            step++; if (step >= nh + Count) step = -1;
        }
        internal static int Version => version;
        internal static string Summary()
        {
            var l = new List<string>();
            for (int i = 0; i < Count; i++) if (yielding[i]) l.Add(((Feature)i).ToString());
            string held = "";
            foreach (var h in holds) if (h.held) held += (held.Length == 0 ? "" : ",") + h.f;
            return (l.Count == 0 ? "none" : string.Join(",", l.ToArray())) + " othersOff=" + (held.Length == 0 ? "none" : held);
        }

        // set by RenderTrim so the tree-shadow rule can compare against the configured distance
        internal static float OurTreeShadowDistance = 70f;

        static bool OursOn(Feature f) { try { var a = ours[(int)f]; return a != null && a(); } catch { return false; } }

        static void CheckOne(int i)
        {
            {
                var f = (Feature)i; string why = null; bool other;
                try { other = Probe(f, out why); } catch (Exception e) { other = false; why = "check failed (" + e.GetType().Name + ")"; }
                bool c = cede[i].Value, y = other && c;
                var h = HoldFor(f);
                string state;
                if (y) state = f + " ceded: steps aside, " + why;
                else if (c) state = f + " ceded but runs: " + (why ?? "no other mod does this");
                else if (h != null && h.held) state = f + " runs (ours): " + h.what + " switched off in memory while it does (was " + Fmt(h.orig) + "; Cede_" + f + " = true gives it back)";
                else state = f + " runs (ours)" + (other ? ": " + why + "; both run (" + (h == null ? "no overlap that needs switching off" : (OursOn(f) ? "the other setting is already off" : "ours is not active right now")) + ")" : "");
                if (!known[i] || y != yielding[i] || state != lastState[i])
                {
                    // stay quiet about features no other mod touches
                    if (known[i] || y || other || (h != null && h.held)) Log(state);
                    if (y != yielding[i]) version++;
                    known[i] = true; yielding[i] = y; reason[i] = why; lastState[i] = state;
                }
            }
        }

        // ---------------------------------------------------------------- in-memory switch-offs
        // The other mod's ConfigEntry<T> value is replaced by writing its backing field directly: no SettingChanged event,
        // no save. A Harmony guard on ConfigFile.Save swaps the user's own value back in for the save and re-applies it after.
        sealed class Hold
        {
            public Feature f; public string what; public Func<ConfigEntryBase> find; public object off;
            public ConfigEntryBase e; public FieldInfo fi; public bool looked, held, swapped; public object orig, written;
        }
        static readonly List<Hold> holds = new List<Hold>
        {
            new Hold { f = Feature.SpawnBudget, what = "FiresGhettoNetworkMod 'Enable Time-Slice Instantiation'", off = false, find = () => Loaded(FgnGuid) ? StaticEntry(FgnMain, "ConfigEnableTimeSliceInstantiation") : null },
            new Hold { f = Feature.RemovalCap, what = "FiresGhettoNetworkMod 'Max Destroys Per Frame'", off = 0, find = () => Loaded(FgnGuid) ? (StaticEntry(FgnMain, "ConfigClientMaxDestroysPerFrame") ?? CfgEntry(FgnGuid, "Max Destroys Per Frame")) : null },
            new Hold { f = Feature.AutoScale, what = "VPO 'Adaptive Resolution'", off = false, find = () => Loaded(VpoGuid) ? StaticEntry(VpoNs + "Modules.LargeSceneConfig", "AdaptiveResolution") : null },
        };
        static Hold HoldFor(Feature f) { foreach (var h in holds) if (h.f == f) return h; return null; }

        static ConfigEntryBase StaticEntry(string type, string member)
        {
            var t = AccessTools.TypeByName(type); if (t == null) return null;
            var fi = AccessTools.Field(t, member); if (fi != null && fi.IsStatic) return fi.GetValue(null) as ConfigEntryBase;
            var pi = AccessTools.Property(t, member); if (pi != null && pi.GetGetMethod(true) != null && pi.GetGetMethod(true).IsStatic) return pi.GetValue(null, null) as ConfigEntryBase;
            return null;
        }
        static ConfigEntryBase CfgEntry(string guid, string key)
        {
            BepInEx.PluginInfo pi; if (!Chainloader.PluginInfos.TryGetValue(guid, out pi) || pi == null || pi.Instance == null) return null;
            var cfg = pi.Instance.Config; if (cfg == null) return null;
            foreach (var kv in cfg) if (kv.Key != null && string.Equals(kv.Key.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
        static FieldInfo Backing(ConfigEntryBase e)
        {
            var t = e.GetType();
            var fi = AccessTools.Field(t, "_typedValue");
            if (fi != null && fi.FieldType == e.SettingType) return fi;
            foreach (var x in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)) if (x.FieldType == e.SettingType) return x;
            return null;
        }
        static bool IsOn(object v)
        {
            if (v == null) return false; if (v is bool b) return b;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) > 0.0; } catch { return false; }
        }
        static string Fmt(object v) => v == null ? "null" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        static object Off(Hold h) { try { return Convert.ChangeType(h.off, h.e.SettingType, System.Globalization.CultureInfo.InvariantCulture); } catch { return h.off; } }

        static void Drive(Hold h)
        {
            if (!h.looked)
            {
                if (!Loaded(FgnGuid) && !Loaded(VpoGuid)) { h.looked = true; return; }
                h.e = h.find(); if (h.e == null) { if (ZNet.instance != null) h.looked = true; return; } // the other mod may still be setting up
                h.looked = true; h.fi = Backing(h.e);
                if (h.fi == null) { Log(h.f + ": cannot switch off " + h.what + " (its value field was not found); both run"); h.e = null; return; }
            }
            if (h.e == null) return;
            bool want = !Ceded(h.f) && OursOn(h.f);
            object cur = h.fi.GetValue(h.e);
            if (h.held && !Equals(cur, h.written))
            {
                // the user changed it (F1) or the file was reloaded while it was switched off
                h.orig = cur;
                if (want && IsOn(cur)) { var o = Off(h); h.fi.SetValue(h.e, o); h.written = o; Log(h.f + ": " + h.what + " was changed to " + Fmt(cur) + " while switched off; switched off again in memory (set Cede_" + h.f + " = true to let it run)"); }
                else { h.held = false; Log(h.f + ": " + h.what + " was changed to " + Fmt(cur) + "; left as it is"); }
                return;
            }
            if (want && !h.held)
            {
                if (!IsOn(cur)) return;
                EnsureSaveGuard();
                var o = Off(h); h.orig = cur; h.fi.SetValue(h.e, o); h.written = o; h.held = true;
                Log(h.f + ": switched off " + h.what + " in memory (was " + Fmt(cur) + ") while this mod's version runs; its .cfg file keeps " + Fmt(cur));
            }
            else if (!want && h.held)
            {
                h.held = false;
                if (Equals(cur, h.written)) h.fi.SetValue(h.e, h.orig);
                Log(h.f + ": " + h.what + " put back to " + Fmt(h.orig) + " (" + (Ceded(h.f) ? "Cede_" + h.f + " = true" : "this mod's version is not active") + ")");
            }
        }

        static bool saveGuard;
        static void EnsureSaveGuard()
        {
            if (saveGuard) return; saveGuard = true;
            try
            {
                var m = AccessTools.Method(typeof(ConfigFile), "Save", Type.EmptyTypes);
                new Harmony(PerformanceFixesPlugin.GUID + ".overlap").Patch(m, prefix: new HarmonyMethod(typeof(Overlap), nameof(SavePre)), postfix: new HarmonyMethod(typeof(Overlap), nameof(SavePost)));
            }
            catch (Exception e) { Log("save guard failed (" + e.Message + "); switch-offs are put back now so they can never be saved"); foreach (var h in holds) if (h.held && h.e != null) { h.held = false; h.fi.SetValue(h.e, h.orig); } for (int i = 0; i < holds.Count; i++) holds[i].e = null; }
        }
        static void SavePre(ConfigFile __instance)
        {
            foreach (var h in holds)
            {
                h.swapped = false;
                try { if (h.held && h.e != null && h.e.ConfigFile == __instance && Equals(h.fi.GetValue(h.e), h.written)) { h.fi.SetValue(h.e, h.orig); h.swapped = true; } } catch { }
            }
        }
        static void SavePost(ConfigFile __instance)
        {
            foreach (var h in holds)
            {
                if (!h.swapped) continue; h.swapped = false;
                try { if (h.held && Equals(h.fi.GetValue(h.e), h.orig)) h.fi.SetValue(h.e, h.written); } catch { }
            }
        }

        static void Log(string s) { try { PerformanceFixesPlugin.Log?.LogInfo("[Mod Overlap] " + s); } catch { } }

        // ---------------------------------------------------------------- probes
        static bool Loaded(string guid) { try { return Chainloader.PluginInfos.ContainsKey(guid); } catch { return false; } }

        static readonly Dictionary<string, MemberInfo> members = new Dictionary<string, MemberInfo>();
        // static field or property of a type found by name; ConfigEntry values are unwrapped. null when missing.
        static object Static(string type, string member)
        {
            string k = type + "|" + member; MemberInfo mi;
            if (!members.TryGetValue(k, out mi))
            {
                var t = AccessTools.TypeByName(type);
                mi = t == null ? null : (MemberInfo)AccessTools.Field(t, member) ?? AccessTools.Property(t, member);
                members[k] = mi;
            }
            object v = null;
            if (mi is FieldInfo fi) v = fi.GetValue(null);
            else if (mi is PropertyInfo pi) v = pi.GetValue(null, null);
            if (v is ConfigEntryBase ce) v = ce.BoxedValue;
            return v;
        }
        static bool Flag(string type, string member) => Static(type, member) is bool b && b;
        static float Num(string type, string member, float def) { var v = Static(type, member); try { return v == null ? def : Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return def; } }

        // does a Harmony owner containing `owner` patch this method?
        static bool PatchedBy(MethodBase m, string owner)
        {
            if (m == null) return false;
            HarmonyLib.Patches info; try { info = Harmony.GetPatchInfo(m); } catch { return false; }
            if (info == null) return false;
            foreach (var o in info.Owners) if (o != null && o.IndexOf(owner, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static MethodInfo fgnTimeSlice; static bool fgnLooked;
        static bool FgnTimeSlice()
        {
            if (!fgnLooked) { fgnLooked = true; var t = AccessTools.TypeByName("FiresGhettoNetworkMod.AutoTune.EffectiveConfig"); if (t != null) fgnTimeSlice = AccessTools.Method(t, "TimeSliceInstantiationEnabled", Type.EmptyTypes); }
            var h = HoldFor(Feature.SpawnBudget); if (h != null && h.held) return IsOn(h.orig);
            if (fgnTimeSlice != null) return fgnTimeSlice.Invoke(null, null) is bool b && b;
            return Flag("FiresGhettoNetworkMod.FiresGhettoNetworkMod", "ConfigEnableTimeSliceInstantiation");
        }

        const string VpoNs = "ValheimEngineOptimizer.";
        static bool Probe(Feature f, out string why)
        {
            why = null;
            switch (f)
            {
                case Feature.SpawnBudget:
                    if (!Loaded(FgnGuid)) return false;
                    if (!PatchedBy(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects"), FgnGuid)) { why = "FiresGhettoNetworkMod is loaded but does not patch ZNetScene.CreateObjects"; return false; }
                    if (!FgnTimeSlice()) { why = "FiresGhettoNetworkMod time-slice instantiation is off"; return false; }
                    why = "FiresGhettoNetworkMod time-slice instantiation is on (it budgets object creation while you play)"; return true;
                case Feature.AutoScale:
                    if (!Loaded(VpoGuid)) return false;
                {
                    var ha = HoldFor(Feature.AutoScale);
                    if (ha != null && ha.held) { why = "VPO adaptive resolution is set on in its config"; return IsOn(ha.orig); }
                }
                    if (!Flag(VpoNs + "Modules.LargeSceneConfig", "AdaptiveResolution")) { why = "VPO adaptive resolution is off"; return false; }
                    if (!Flag(VpoNs + "Modules.SceneGpuBudgetManager", "GpuTimingsAvailable")) { why = "VPO adaptive resolution is on but has no GPU timings (it is not scaling)"; return false; }
                    if (Flag("UpscaledFrameBuffer", "m_autoTargetResolution")) { why = "VPO adaptive resolution is on but the game's automatic render resolution is in charge"; return false; }
                    why = "VPO adaptive resolution is working (minimum render scale " + Num(VpoNs + "Modules.LargeSceneConfig", "MinimumRenderScale", 0.75f).ToString("0.00") + ")"; return true;
                case Feature.SceneLod:
                    if (!Loaded(VpoGuid)) return false;
                    if (Static("VPO_WATER.Modules.ENGINE.SceneLODOptimizer", "FarDistance") == null) { why = "VPO Scene LOD optimizer not found"; return false; }
                    why = "VPO Scene LOD optimizer is present (its far distance " + Num("VPO_WATER.Modules.ENGINE.SceneLODOptimizer", "FarDistance", 0f).ToString("0") + " m)"; return true;
                case Feature.Grass:
                    if (!Loaded(VpoGuid)) return false;
                    if (!Flag(VpoNs + "Modules.VegetationOptimizerPatches", "Enabled")) { why = "VPO vegetation optimizer is off"; return false; }
                    if (!PatchedBy(AccessTools.DeclaredMethod(typeof(ClutterSystem), "Awake"), VpoGuid)) { why = "VPO vegetation optimizer is on but does not patch the grass"; return false; }
                    why = "VPO vegetation optimizer patches the grass (distance x" + Num(VpoNs + "Modules.VegetationOptimizerPatches", "ClutterDistanceScale", 1f).ToString("0.00") + ", amount x" + Num(VpoNs + "Modules.VegetationOptimizerPatches", "ClutterAmountScale", 1f).ToString("0.00") + ")"; return true;
                case Feature.Particles:
                    if (!Loaded(VpoGuid)) return false;
                    if (!Flag(VpoNs + "Modules.ParticleOptimizerPatches", "Enabled")) { why = "VPO particle optimizer is off"; return false; }
                    if (!PatchedBy(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject"), VpoGuid)) { why = "VPO particle optimizer is on but its object hook is not in place"; return false; }
                    why = "VPO particle optimizer is on"; return true;
                case Feature.LayerCull:
                    if (!Loaded(VpoGuid)) return false;
                    if (!Flag(VpoNs + "Modules.RenderOptimizer", "Enabled")) { why = "VPO render optimizer is off"; return false; }
                    why = "VPO render optimizer sets layer cull distances (default " + Num(VpoNs + "Modules.RenderOptimizer", "DefaultLayerCullDistance", 0f).ToString("0") + " m)"; return true;
                case Feature.TreeShadows:
                {
                    if (!Loaded(VpoGuid)) return false;
                    if (!Flag(VpoNs + "Modules.LargeSceneConfig", "ShadowBudget")) { why = "VPO shadow budget is off"; return false; }
                    float sd = Num(VpoNs + "Modules.LargeSceneConfig", "ShadowDistance", float.MaxValue);
                    if (sd > OurTreeShadowDistance) { why = "VPO shadow budget ends shadows at " + sd.ToString("0") + " m, beyond TreeShadowDistance " + OurTreeShadowDistance.ToString("0") + " m, so the tree trim still helps"; return false; }
                    why = "VPO shadow budget already ends all shadows at " + sd.ToString("0") + " m (TreeShadowDistance " + OurTreeShadowDistance.ToString("0") + " m)"; return true;
                }
                case Feature.Gc:
                    if (!Loaded(VpoGuid)) return false;
                    if (!Flag(VpoNs + "Modules.GCPatches", "Enabled")) { why = "VPO GC control is off"; return false; }
                    why = "VPO GC control is on"; return true;
                case Feature.OrphanPrune:
                    if (!Loaded(FgnGuid)) return false;
                    // FGN prunes only inside its own dedicated-server object pass (ZNetScene.CreateDestroyObjects with players connected)
                    if (ZNet.instance == null || !ZNet.instance.IsDedicated()) { why = "FiresGhettoNetworkMod only prunes broken entries on a dedicated server"; return false; }
                    if (!PatchedBy(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDestroyObjects"), FgnGuid)) { why = "FiresGhettoNetworkMod server object pass is not in place"; return false; }
                    if (!Flag("FiresGhettoNetworkMod.FiresGhettoNetworkMod", "ConfigEnableInstanceOrphanPrune")) { why = "FiresGhettoNetworkMod instance orphan prune is off"; return false; }
                    why = "FiresGhettoNetworkMod instance orphan prune is on (server)"; return true;
                case Feature.RemovalCap:
                {
                    if (!Loaded(FgnGuid)) return false;
                    var hr = HoldFor(Feature.RemovalCap);
                    object v = hr != null && hr.held ? hr.orig : (hr != null && hr.e != null ? hr.fi.GetValue(hr.e) : null);
                    if (v == null) { var e = StaticEntry(FgnMain, "ConfigClientMaxDestroysPerFrame") ?? CfgEntry(FgnGuid, "Max Destroys Per Frame"); v = e?.BoxedValue; }
                    if (!IsOn(v)) { why = "FiresGhettoNetworkMod 'Max Destroys Per Frame' is 0 (it does not cap removals)"; return false; }
                    why = "FiresGhettoNetworkMod 'Max Destroys Per Frame' is " + Fmt(v); return true;
                }
                case Feature.IdlePass:
                    if (!Loaded(VpoGuid)) return false;
                    if (!Flag(VpoNs + "Modules.LargeSceneConfig", "SectorCache")) { why = "VPO sector scene cache is off"; return false; }
                    why = "VPO sector scene cache skips idle object passes"; return true;
            }
            return false;
        }

        // 2.3.6: FGN's live 'Max Destroys Per Frame' (0 while this mod's removal cap holds it, or when FGN is not loaded)
        static ConfigEntryBase fgnLimit; static bool fgnLimitLooked;
        internal static double FgnDestroyLimit()
        {
            if (!Loaded(FgnGuid)) return 0;
            if (fgnLimitLooked && fgnLimit == null) return 0;
            if (fgnLimit == null)
            {
                fgnLimit = StaticEntry(FgnMain, "ConfigClientMaxDestroysPerFrame") ?? CfgEntry(FgnGuid, "Max Destroys Per Frame");
                if (fgnLimit != null || ZNet.instance != null) fgnLimitLooked = true;
                if (fgnLimit == null) return fgnLimitLooked ? 0 : 1; // still setting up: assume it throttles
            }
            try { var v = fgnLimit.BoxedValue; return v == null ? 0 : Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return 1; }
        }

        // AutoScale helper: VPO's current render scale is still above its minimum (so VPO can still lower it)
        internal static bool VpoScaleAboveMin(out float cur, out float min)
        {
            cur = Num(VpoNs + "Modules.SceneGpuBudgetManager", "CurrentScale", 1f);
            min = Num(VpoNs + "Modules.LargeSceneConfig", "MinimumRenderScale", 0.75f);
            return cur > min + 0.01f;
        }
    }
}
