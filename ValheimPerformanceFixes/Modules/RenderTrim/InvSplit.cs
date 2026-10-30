using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RenderTrim
{
    // 2.3.8: an in-world inventory open costs the Show frame plus one 40-72 ms frame. The game reads "visible" at the top of
    // InventoryGui.Update before Show, so that second frame is the first one in which ALL of the per-frame panel updates run at
    // once (grid, container, stats, weights, recipe, repair) while the newly shown panels are laid out.
    // SplitOpenUpdate: on that first frame only the player's grid (UpdateInventory, plus gamepad / drag) runs; the container,
    // character stats, weights, recipe and repair updates are left to the next frame, where they run as usual. They are
    // updated again every frame anyway, so the only change is that they appear one frame (about 16 ms) later, during the
    // open animation. Other mods' prefixes / postfixes on those methods still run (Harmony), so their own cost is not moved.
    // LogOpenBreakdown: for the first 3 frames after each open, the time of InventoryGui.Update and of each of these calls
    // (including other mods' patches on them) is written to RenderTrim.log, so the remaining cost can be found.
    internal static class InvSplit
    {
        static ConfigEntry<bool> splitOn, breakdownOn;
        static Action<string> log;
        static readonly string[] deferable = { "UpdateContainer", "UpdateCharacterStats", "UpdateInventoryWeight", "UpdateContainerWeight", "UpdateRecipe", "UpdateRepair" };
        static readonly string[] timedOnly = { "UpdateGamepad", "UpdateInventory", "UpdateItemDrag", "UpdateSplitDialog" };
        static readonly HashSet<string> skipSet = new HashSet<string>(deferable);
        static readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        static int showFrame = -1, firstFrame = -1, logUntil = -1, curFrame = -1; static bool pending;
        static double updT0, updMs; static readonly Dictionary<string, double> parts = new Dictionary<string, double>();
        static string prevLine; static int prevFrame = -1;
        static int sOpens, sSplit, sSkipped; static double sWorstUpd;

        internal static void Init(ConfigFile cfg, Harmony h, Action<string> l)
        {
            log = l;
            splitOn = cfg.Bind("9. UI Warm-up", "SplitOpenUpdate", true, "In the first frame after you open the inventory in the world, only your own item grid is updated; the container, character stats, weights, recipe and repair panels are updated from the next frame on (they are updated every frame anyway, so they show up about one frame later, during the open animation). Spreads the 40-72 ms frame after each open over two frames. Off = vanilla.");
            breakdownOn = cfg.Bind("9. UI Warm-up", "LogOpenBreakdown", true, "For the first 3 frames after each inventory open, write the time of the inventory's per-frame update and of each of its parts (with other mods' patches on them) to RenderTrim.log.");
            try
            {
                var upd = AccessTools.DeclaredMethod(typeof(InventoryGui), "Update");
                if (upd != null) h.Patch(upd, prefix: new HarmonyMethod(typeof(InvSplit), nameof(UpdPre)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(InvSplit), nameof(UpdPost)) { priority = Priority.Last });
                int n = 0; string miss = "";
                foreach (var name in deferable) { if (PatchSub(h, name, true)) n++; else miss += name + " "; }
                foreach (var name in timedOnly) { if (PatchSub(h, name, false)) n++; else miss += name + " "; }
                l("inventory open split: patched " + n + " calls" + (miss.Length > 0 ? ", not found: " + miss : "") + " (split=" + splitOn.Value + " breakdown=" + breakdownOn.Value + ")");
            }
            catch (Exception e) { l("inventory open split patch FAILED: " + e.Message); }
        }
        static bool PatchSub(Harmony h, string name, bool canSkip)
        {
            try
            {
                var m = AccessTools.DeclaredMethod(typeof(InventoryGui), name);
                if (m == null) return false;
                h.Patch(m, prefix: new HarmonyMethod(typeof(InvSplit), canSkip ? nameof(SkipPre) : nameof(TimePre)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(InvSplit), nameof(SubPost)) { priority = Priority.Last });
                return true;
            }
            catch (Exception e) { log?.Invoke("inventory open split: " + name + " not patched: " + e.Message); return false; }
        }
        // called from the Show timer postfix for a real (not warm-up) open
        internal static void Opened()
        {
            if (!Plugin.Active) return;
            showFrame = Time.frameCount; pending = true; firstFrame = -1; sOpens++;
            logUntil = breakdownOn != null && breakdownOn.Value ? showFrame + 3 : -1;
            parts.Clear(); updMs = 0; curFrame = showFrame;
            // 2.3.9: Show runs inside InventoryGui.Update, after its prefix: start the +0 timer here (2.3.8 used a stale start, so '+0 frame Update=' was wrong)
            updT0 = sw.Elapsed.TotalMilliseconds; updFrame = showFrame;
        }
        static int updFrame = -1;
        static bool Logging => logUntil >= 0 && Time.frameCount <= logUntil;
        static void UpdPre(InventoryGui __instance)
        {
            int f = Time.frameCount;
            if (prevLine != null && f > prevFrame) { log?.Invoke(prevLine + " frameMs=" + (Time.unscaledDeltaTime * 1000f).ToString("0.0")); prevLine = null; }
            if (pending && f > showFrame) { pending = false; firstFrame = f; if (splitOn != null && splitOn.Value) sSplit++; }
            if (Logging) { if (curFrame != f) { parts.Clear(); curFrame = f; } updT0 = sw.Elapsed.TotalMilliseconds; updFrame = f; }
        }
        static void UpdPost()
        {
            if (!Logging) return;
            int f = Time.frameCount;
            if (updFrame != f) return; // no matching start this frame
            updMs = sw.Elapsed.TotalMilliseconds - updT0; if (updMs > sWorstUpd) sWorstUpd = updMs;
            var sb = new System.Text.StringBuilder();
            sb.Append("inventory open +").Append(f - showFrame).Append(f == showFrame ? " frame: Update(rest after the open)=" : " frame: Update=").Append(updMs.ToString("0.0")).Append("ms");
            foreach (var kv in parts) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.ToString("0.0"));
            if (f == firstFrame && splitOn != null && splitOn.Value) sb.Append(" (split: container/stats/weights/recipe/repair left to the next frame)");
            prevLine = sb.ToString(); prevFrame = f;
            if (f >= logUntil) logUntil = -1;
        }
        static bool SkipPre(MethodBase __originalMethod, out double __state)
        {
            __state = Logging ? sw.Elapsed.TotalMilliseconds : -1;
            if (firstFrame == Time.frameCount && splitOn != null && splitOn.Value && Plugin.Active && skipSet.Contains(__originalMethod.Name)) { sSkipped++; return false; }
            return true;
        }
        static void TimePre(out double __state) { __state = Logging ? sw.Elapsed.TotalMilliseconds : -1; }
        static void SubPost(MethodBase __originalMethod, double __state)
        {
            if (__state < 0 || !Logging) return;
            string k = __originalMethod.Name.StartsWith("Update") ? __originalMethod.Name.Substring(6) : __originalMethod.Name;
            parts.TryGetValue(k, out double v); parts[k] = v + (sw.Elapsed.TotalMilliseconds - __state);
        }
        internal static string Stats()
        {
            string s = "invOpen opens=" + sOpens + " split=" + sSplit + " (" + (splitOn != null && splitOn.Value ? "on" : "off") + ") callsMovedToNextFrame=" + sSkipped + " worstUpdateMs=" + sWorstUpd.ToString("0.0");
            sOpens = sSplit = sSkipped = 0; sWorstUpd = 0;
            return s;
        }
    }
}
