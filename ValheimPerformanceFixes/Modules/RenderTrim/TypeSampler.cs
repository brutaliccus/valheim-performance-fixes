// Valheim Performance Fixes - per-type update sampler (2.1.2-test). MIT License.
// Client only, off by default (diagnostics). Measures time and managed allocation of selected per-object update methods (VisEquipment,
// smoke, footsteps, sound effects, water, crafting stations, and known mod hooks on equipment visuals) so the stats line
// can show which object type costs how much: calls/min, ms/min, MB/min, plus how many VisEquipment are players, armor
// stands and creatures. Times are inclusive (a method called inside another counts in both, and other mods' patches on
// a method count toward it). Switch: "20. Memory Cleanup Smoothing" / TypeSampler (takes effect at the next start).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;

namespace RenderTrim
{
    internal static class TypeSampler
    {
        internal static bool On;
        static readonly Dictionary<MethodBase, int> index = new Dictionary<MethodBase, int>();
        static readonly List<string> names = new List<string>();
        static readonly List<string> owners = new List<string>();
        static long[] ticks = new long[0], bytes = new long[0]; static int[] calls = new int[0];
        static readonly Stopwatch since = Stopwatch.StartNew();
        static bool ownersShown;

        internal struct Mark { public long ts, b; public int gc; }

        internal const string DefaultHooks = "VisEquipment.CustomUpdate,VisEquipment.UpdateVisuals,VisEquipment.UpdateEquipmentVisuals,VisEquipment.UpdateBaseModel,VisEquipment.UpdateColors,Smoke.CustomUpdate,SmokeSpawner.CustomUpdate,FootStep.CustomUpdate,ZSFX.CustomUpdate,WaterVolume.UpdateMaterials,WaterVolume.UpdateFloaters,InstanceRenderer.CustomUpdate,WaterTrigger.CustomUpdate,CraftingStation.CustomUpdate,InventorySlots.InventorySlotsPlugin.UpdateCustomEquipmentVisualsFromZdo,Balrond.Equipment.EquipmentVisualService.RepairArmorBones,Balrond.Features.ShadowPersonPlayerShaderPatches.Repair,PlayerColors.BalrondPlayerCustomization.LateUpdate,PlayerColors.BalrondPlayerCustomization.RefreshEyesBinding,PlayerColors.BalrondPlayerCustomization.BindEyesRenderer";

        internal static void Init(Harmony h, string list, Action<string> L)
        {
            var pre = new HarmonyMethod(typeof(TypeSampler).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
            var post = new HarmonyMethod(typeof(TypeSampler).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
            var ok = new StringBuilder(); var missing = new StringBuilder();
            foreach (var raw in list.Split(','))
            {
                var s = raw.Trim(); int dot = s.LastIndexOf('.'); if (dot <= 0) continue;
                MethodInfo m = null;
                try
                {
                    var ty = AccessTools.TypeByName(s.Substring(0, dot)); string mn = s.Substring(dot + 1);
                    if (ty != null) foreach (var c in AccessTools.GetDeclaredMethods(ty)) if (c.Name == mn && !c.IsAbstract && !c.ContainsGenericParameters) { m = c; break; }
                }
                catch { }
                if (m == null || index.ContainsKey(m)) { missing.Append(missing.Length > 0 ? ", " : "").Append(s); continue; }
                try
                {
                    h.Patch(m, prefix: pre, postfix: post);
                    index[m] = names.Count; names.Add(s); owners.Add("");
                    ok.Append(ok.Length > 0 ? ", " : "").Append(s);
                }
                catch (Exception e) { missing.Append(missing.Length > 0 ? ", " : "").Append(s + " (" + e.Message + ")"); }
            }
            ticks = new long[names.Count]; bytes = new long[names.Count]; calls = new int[names.Count];
            On = names.Count > 0;
            L("[Memory] per-type sampler " + (On ? "on" : "off (nothing found)") + ". Measuring: " + ok + (missing.Length > 0 ? ". Not found: " + missing : "") + ". Times are inclusive; other mods' patches count toward the method.");
        }

        static void Pre(out Mark __state) { __state.b = GcSmooth.Sampler.Now(); __state.gc = GC.CollectionCount(0); __state.ts = Stopwatch.GetTimestamp(); }
        static void Post(Mark __state, MethodBase __originalMethod)
        {
            if (__state.ts == 0) return;
            long dt = Stopwatch.GetTimestamp() - __state.ts;
            if (__originalMethod == null || !index.TryGetValue(__originalMethod, out int i)) return;
            calls[i]++; ticks[i] += dt;
            if (GC.CollectionCount(0) != __state.gc) return;
            long d = GcSmooth.Sampler.Now() - __state.b; if (d > 0) bytes[i] += d;
        }

        static string Owners(MethodBase m, string selfId)
        {
            try
            {
                var info = Harmony.GetPatchInfo(m); if (info == null) return "";
                var own = new SortedSet<string>();
                foreach (var p in info.Prefixes) own.Add(p.owner); foreach (var p in info.Postfixes) own.Add(p.owner); foreach (var p in info.Transpilers) own.Add(p.owner); foreach (var p in info.Finalizers) own.Add(p.owner);
                own.Remove(selfId);
                return own.Count > 0 ? string.Join("/", own) : "";
            }
            catch { return ""; }
        }

        internal static string Stats(string selfId)
        {
            if (!On) return "per-type sampler off";
            double min = Math.Max(0.05, since.Elapsed.TotalMinutes); since.Reset(); since.Start();
            var sb = new StringBuilder();
            int pl = 0, st = 0, cr = 0;
            try { foreach (var u in VisEquipment.Instances) if (u is VisEquipment v && v != null) { if (v.m_isArmorStand) st++; else if (v.m_isPlayer) pl++; else cr++; } } catch { }
            sb.Append("VisEquipment players=").Append(pl).Append(" armorStands=").Append(st).Append(" creatures=").Append(cr).Append(" |");
            var order = new List<int>(); for (int i = 0; i < names.Count; i++) if (calls[i] > 0) order.Add(i);
            order.Sort((a, b) => ticks[b].CompareTo(ticks[a]));
            double tms = 1000.0 / Stopwatch.Frequency;
            foreach (int i in order)
                sb.Append(' ').Append(names[i]).Append(" calls=").Append((calls[i] / min).ToString("0")).Append("/min ").Append((ticks[i] * tms / min).ToString("0")).Append("ms/min ").Append((bytes[i] / 1048576.0 / min).ToString("0.0")).Append("MB/min;");
            if (order.Count == 0) sb.Append(" no calls measured");
            if (!ownersShown && order.Count > 0)
            {
                ownersShown = true;
                sb.Append("\n   per-type patch owners (other mods):");
                foreach (var kv in index) { var o = Owners(kv.Key, selfId); sb.Append(' ').Append(names[kv.Value]).Append('=').Append(o.Length > 0 ? o : "-").Append(';'); }
            }
            for (int i = 0; i < names.Count; i++) { ticks[i] = bytes[i] = 0; calls[i] = 0; }
            return sb.ToString();
        }
    }
}
