using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using PG = global::ValheimPerformanceFixes.PatchGuard;

namespace RenderTrim
{
    // 2.2.0: two small client-side extras.
    //  - Teleport Terrain Prefetch: when you step into a portal (or anything else teleports you far), ask the game's own background
    //    terrain builder to start on the ground around the destination right away, during the fade, instead of only once you arrive.
    //    It is the very same request the game makes itself when it loads those zones; it only comes a couple of seconds earlier.
    //  - Fade Settle: a MaterialFader that has finished all its fades keeps re-applying the same final values every frame. Skip that
    //    until something starts a new fade.
    internal static class Extras
    {
        static ConfigEntry<bool> tpOn, fadeOn; static ConfigEntry<int> tpZones;
        static Action<string> L;
        static int sPrefetched, sTeleports, sFadeSkips;

        internal static void Init(ConfigFile c, Harmony h, Action<string> log)
        {
            L = log;
            tpOn = c.Bind("15. Teleport Terrain Prefetch", "Enabled", true, "When you teleport (portal, or anything else that moves you far), start building the ground of the destination zones on the game's own background terrain thread during the fade, nearest first. Arrival is ready sooner. Off = vanilla (built only once you arrive).");
            tpZones = c.Bind("15. Teleport Terrain Prefetch", "MaxZones", 6, new ConfigDescription("How many destination zones to start ahead of time (the zone you land in and the closest neighbours). Kept small so the game's own ready-list (16 entries) is never crowded.", new AcceptableValueRange<int>(1, 7)));
            fadeOn = c.Bind("16. Fade Settle", "Enabled", true, "A material fade effect that has finished stops re-applying its final values every frame (it starts again as soon as a new fade is triggered). Off = vanilla.");
            Hook(h, "teleport terrain prefetch", TpPatch);
            Hook(h, "fade settle", FadePatch);
        }
        internal static string Stats() { var s = "teleports=" + sTeleports + " zonesPrefetched=" + sPrefetched + " fadeFramesSkipped=" + sFadeSkips; sTeleports = sPrefetched = sFadeSkips = 0; return s; }

        static void Hook(Harmony h, string what, Action<Harmony> f)
        {
            try { f(h); } catch (Exception e) { L("[Extras] " + what + " patch FAILED: " + e.Message); }
        }
        static bool StandDown(MethodBase m, string what, bool prefixes, bool transpilers)
        {
            var r = PG.Rivals(m, prefixes, transpilers);
            var o = PG.Others(m);
            if (o.Count > 0) L("[Extras] patch audit: " + PG.Name(m) + " is also patched by " + string.Join(", ", o));
            if (r.Count == 0) return false;
            L("[Extras] " + what + " stands down: " + PG.Name(m) + " is also changed by " + string.Join(", ", r));
            return true;
        }

        // ------------------------------------------------------------------ teleport terrain prefetch
        static AccessTools.FieldRef<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>> toBuild;
        static void TpPatch(Harmony h)
        {
            var m = AccessTools.DeclaredMethod(typeof(Player), "TeleportTo", new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
            if (m == null) { L("[Extras] Player.TeleportTo not found - teleport terrain prefetch off"); return; }
            if (AccessTools.DeclaredMethod(typeof(HeightmapBuilder), "IsTerrainReady") == null) { L("[Extras] HeightmapBuilder.IsTerrainReady not found - teleport terrain prefetch off"); return; }
            try { toBuild = AccessTools.FieldRefAccess<HeightmapBuilder, List<HeightmapBuilder.HMBuildData>>("m_toBuild"); } catch { toBuild = null; }
            PG.Others(m); // audit only: a postfix that just reads the result cannot clash with anyone
            h.Patch(m, postfix: new HarmonyMethod(typeof(Extras), nameof(TpPost)) { priority = Priority.Last });
            L("[Extras] teleport terrain prefetch patched (enabled=" + tpOn.Value + ")");
        }
        static void TpPost(Player __instance, bool __result, Vector3 pos)
        {
            if (!__result || !tpOn.Value || __instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                var zs = ZoneSystem.instance; var wg = WorldGenerator.instance; if (zs == null || wg == null || zs.m_zonePrefab == null) return;
                var hb = HeightmapBuilder.instance; if (hb == null) return;
                var hmp = zs.m_zonePrefab.GetComponentInChildren<Heightmap>(); if (hmp == null) return;
                var center = ZoneSystem.GetZone(pos);
                if (zs.IsZoneLoaded(center)) return; // a short hop inside loaded ground: nothing to do
                int queued = 0; try { if (toBuild != null) { var q = toBuild(hb); if (q != null) queued = q.Count; } } catch { }
                int n = Math.Min(tpZones.Value, 7 - queued); if (n <= 0) return;
                sTeleports++;
                // the landing zone, then its neighbours closest to the landing point first
                var list = new List<Vector2s>(9);
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) list.Add(new Vector2s(center.x + dx, center.y + dy));
                list.Sort((a, b) => (ZoneSystem.GetZonePos(a) - pos).sqrMagnitude.CompareTo((ZoneSystem.GetZonePos(b) - pos).sqrMagnitude));
                foreach (var z in list)
                {
                    if (n <= 0) break;
                    if (zs.IsZoneLoaded(z)) continue;
                    // same call, same arguments as ZoneSystem.SpawnZone: queues the build if it is not ready or queued yet
                    if (!hb.IsTerrainReady(ZoneSystem.GetZonePos(z), hmp.m_width, hmp.m_scale, hmp.IsDistantLod, wg)) sPrefetched++;
                    n--;
                }
            }
            catch (Exception e) { L("[Extras] teleport terrain prefetch error (left to vanilla): " + e.Message); }
        }

        // ------------------------------------------------------------------ fade settle
        static AccessTools.FieldRef<MaterialFader, bool> mfStarted;
        static AccessTools.FieldRef<MaterialFader.FadeProperty, bool> fpFinished;
        static bool fadeDead;
        static void FadePatch(Harmony h)
        {
            var m = AccessTools.DeclaredMethod(typeof(MaterialFader), "Update");
            if (m == null) { L("[Extras] MaterialFader.Update not found - fade settle off"); return; }
            if (StandDown(m, "fade settle", true, true)) return;
            mfStarted = AccessTools.FieldRefAccess<MaterialFader, bool>("m_started");
            fpFinished = AccessTools.FieldRefAccess<MaterialFader.FadeProperty, bool>("m_finished");
            h.Patch(m, prefix: new HarmonyMethod(typeof(Extras), nameof(FadePre)) { priority = Priority.Last });
            L("[Extras] fade settle patched (enabled=" + fadeOn.Value + ")");
        }
        // keyed per fader: "was everything finished when the previous Update started". The frame a fade finishes still runs in full
        // (it applies the final values); only the frames after that are skipped.
        static readonly Dictionary<int, bool> settled = new Dictionary<int, bool>();
        static bool FadePre(MaterialFader __instance)
        {
            if (fadeDead || !fadeOn.Value) return true;
            try
            {
                if (!mfStarted(__instance)) return true;
                var props = __instance.m_fadeProperties; if (props == null || props.Count == 0) return true;
                bool all = true; for (int i = 0; i < props.Count; i++) { var p = props[i]; if (p == null || !fpFinished(p)) { all = false; break; } }
                int id = __instance.GetInstanceID();
                if (!all) { settled.Remove(id); return true; }
                if (!settled.ContainsKey(id)) { settled[id] = true; if (settled.Count > 4096) settled.Clear(); return true; } // the finishing frame already ran; run one more to be sure the final values are applied
                sFadeSkips++; return false;
            }
            catch (Exception e) { fadeDead = true; L("[Extras] fade settle turned itself off: " + e.Message); return true; }
        }
    }
}
