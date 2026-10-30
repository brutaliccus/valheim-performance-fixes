using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using PG = global::ValheimPerformanceFixes.PatchGuard;

namespace ValheimPerformanceFixes.WorldLoad
{
    // 2.3.0: two world-loading fixes. Set up from the host plugin directly, so they also run on a dedicated server.
    //  - Loading Zones: while the black loading screen is fully up (nothing to see, nothing to stutter), the zones around you
    //    are set up back to back instead of one zone per 0.1 s. Only the zone ground/terrain setup is sped up - objects in
    //    those zones (player builds included) are still created by the game's own object loading, in its own order.
    //  - Terrain Worker: the game's background terrain thread naps 10 ms after every tile even when more tiles are waiting,
    //    and keeps only the 16 newest finished tiles (older finished ones are thrown away and have to be built again).
    //    Now it only naps when it has nothing to do, and keeps up to ReadyTiles finished tiles until they are picked up.
    internal static class WorldLoad
    {
        internal const string HarmonyId = "vpf.ValheimPerformanceFixes.WorldLoad";
        const string ZSec = "17. Loading Zones", TSec = "18. Terrain Worker";
        static ConfigEntry<bool> zoneOn, workerOn; static ConfigEntry<float> zoneMs; static ConfigEntry<int> zoneMax, readyTiles;
        static Action<string> L;
        internal static volatile bool WorkerActive; static volatile int readyCap = 16;
        internal static bool zoneOk, sleepOk, capOk;
        static int sZones, sZoneFrames, sBusySkips, sIdleNaps;

        internal static void Init(ConfigFile c, Action<string> log)
        {
            L = log;
            zoneOn = c.Bind(ZSec, "Enabled", true, "While the black loading screen is fully up (joining a world, portals, respawn), set up the zones around you back to back instead of one zone every 0.1 s. Only the zone ground is sped up; the objects in those zones (player builds included) still load through the game's own object loading in the game's own order. Off = vanilla.");
            zoneMs = c.Bind(ZSec, "BudgetMs", 30f, new ConfigDescription("Time per frame that may be spent setting up zones behind the loading screen (the first zone of a frame always runs).", new AcceptableValueRange<float>(5f, 200f)));
            zoneMax = c.Bind(ZSec, "MaxZonesPerFrame", 8, new ConfigDescription("Most zones set up in one frame behind the loading screen.", new AcceptableValueRange<int>(1, 64)));
            workerOn = c.Bind(TSec, "Enabled", true, "The game's background terrain thread only pauses when it has no tile waiting (vanilla pauses 10 ms after every tile), and keeps finished tiles until they are used instead of throwing away all but the newest 16. Helps loading, portals and fast travel, and a server generating new ground. Off = vanilla.");
            readyTiles = c.Bind(TSec, "ReadyTiles", 128, new ConfigDescription("How many finished terrain tiles may wait to be picked up (vanilla 16). About 85 KB each.", new AcceptableValueRange<int>(16, 1024)));
            Sync(); workerOn.SettingChanged += (s, e) => Sync(); readyTiles.SettingChanged += (s, e) => Sync();
            var h = new Harmony(HarmonyId);
            Hook(h, "loading zones", ZonePatch);
            Hook(h, "terrain worker", WorkerPatch);
        }
        static void Sync() { WorkerActive = workerOn.Value; readyCap = Math.Max(16, readyTiles.Value); }

        internal static string Stats()
        {
            var s = "loadZones=" + sZones + "/" + sZoneFrames + "fr terrainBusy=" + sBusySkips + " terrainIdle=" + sIdleNaps;
            sZones = sZoneFrames = 0; Interlocked.Exchange(ref sBusySkips, 0); Interlocked.Exchange(ref sIdleNaps, 0); return s;
        }

        static void Hook(Harmony h, string what, Action<Harmony> f)
        {
            try { f(h); } catch (Exception e) { L("[WorldLoad] " + what + " patch FAILED: " + e.Message); }
        }
        static bool StandDown(MethodBase m, string what)
        {
            var o = PG.Others(m);
            if (o.Count > 0) L("[WorldLoad] patch audit: " + PG.Name(m) + " is also patched by " + string.Join(", ", o));
            var r = PG.Rivals(m, true, true);
            if (r.Count == 0) return false;
            L("[WorldLoad] " + what + " stands down: " + PG.Name(m) + " is also changed by " + string.Join(", ", r));
            return true;
        }

        // ------------------------------------------------------------------ loading zones
        static Func<ZoneSystem, Vector3, bool> createLocal;
        static readonly Stopwatch sw = new Stopwatch();
        static void ZonePatch(Harmony h)
        {
            var upd = AccessTools.DeclaredMethod(typeof(ZoneSystem), "Update");
            var cl = AccessTools.DeclaredMethod(typeof(ZoneSystem), "CreateLocalZones", new[] { typeof(Vector3) });
            if (upd == null || cl == null || cl.ReturnType != typeof(bool)) { L("[WorldLoad] ZoneSystem.Update/CreateLocalZones not found - loading zones off"); return; }
            // a mod that skips or rewrites either one has its own idea of zone pacing: leave it alone
            if (StandDown(upd, "loading zones") || StandDown(cl, "loading zones")) return;
            var poke = AccessTools.DeclaredMethod(typeof(ZoneSystem), "PokeLocalZone"); if (poke != null && StandDown(poke, "loading zones")) return;
            createLocal = AccessTools.MethodDelegate<Func<ZoneSystem, Vector3, bool>>(cl);
            h.Patch(upd, postfix: new HarmonyMethod(typeof(WorldLoad), nameof(ZonePost)) { priority = Priority.Last });
            zoneOk = true;
            L("[WorldLoad] loading zones patched (enabled=" + zoneOn.Value + ")");
        }

        // Same conditions vanilla Update uses before it sets up zones, plus: the loading screen is fully up.
        static void ZonePost(ZoneSystem __instance)
        {
            if (!zoneOn.Value || createLocal == null || __instance == null) return;
            try
            {
                if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected) return;
                var zn = ZNet.instance; if (zn == null) return;
                if (zn.IsServer() && !__instance.LocationsGenerated) return;
                if (!BlackScreen()) return;
                Vector3 p = zn.GetReferencePosition();
                sw.Restart(); int n = 0, max = zoneMax.Value; double ms = zoneMs.Value;
                while (n < max && createLocal(__instance, p)) { n++; if (sw.Elapsed.TotalMilliseconds >= ms) break; }
                if (n > 0) { sZones += n; sZoneFrames++; }
            }
            catch (Exception e) { zoneOn.Value = false; L("[WorldLoad] loading zones turned off after an error: " + e.Message); }
        }
        static bool BlackScreen()
        {
            if (Application.isBatchMode) return false;
            var hd = Hud.instance; if (hd == null || hd.m_loadingScreen == null) return false;
            return hd.m_loadingScreen.gameObject.activeInHierarchy && hd.m_loadingScreen.alpha >= 0.99f;
        }

        // ------------------------------------------------------------------ terrain worker
        static FieldInfo fInst, fToBuild;
        static void WorkerPatch(Harmony h)
        {
            var bt = AccessTools.DeclaredMethod(typeof(HeightmapBuilder), "BuildThread");
            fInst = AccessTools.Field(typeof(HeightmapBuilder), "m_instance");
            fToBuild = AccessTools.Field(typeof(HeightmapBuilder), "m_toBuild");
            if (bt == null || fInst == null || fToBuild == null) { L("[WorldLoad] HeightmapBuilder.BuildThread / fields not found - terrain worker off"); return; }
            if (StandDown(bt, "terrain worker")) return;
            // The game may already have started the thread; a patched method is picked up on its next start
            // (every world load creates a new builder), so it is in place from the next world on at the latest.
            h.Patch(bt, transpiler: new HarmonyMethod(typeof(WorldLoad), nameof(BuildTranspiler)) { priority = Priority.Last });
            L("[WorldLoad] terrain worker: idle-only pause " + (sleepOk ? "ok" : "NOT applied (vanilla code changed)") + ", ready list " + (capOk ? "ok" : "NOT applied (vanilla code changed)") + " (enabled=" + workerOn.Value + ", ReadyTiles=" + readyTiles.Value + ")");
        }

        internal static IEnumerable<CodeInstruction> BuildTranspiler(IEnumerable<CodeInstruction> ins)
        {
            var sleep = AccessTools.Method(typeof(Thread), "Sleep", new[] { typeof(int) });
            var a = PG.ReplaceCalls(ins, ci => ci.opcode == OpCodes.Call && Equals(ci.operand, sleep),
                AccessTools.Method(typeof(WorldLoad), nameof(IdleSleep)), 1, "HeightmapBuilder.BuildThread Thread.Sleep", L, out sleepOk);
            var b = PG.ReplaceCalls(a, ci => IsLdc(ci, 16),
                AccessTools.Method(typeof(WorldLoad), nameof(ReadyCap)), 1, "HeightmapBuilder.BuildThread ready-list limit 16", L, out capOk);
            return b;
        }
        static bool IsLdc(CodeInstruction ci, int v)
        {
            if (ci.opcode == OpCodes.Ldc_I4_S || ci.opcode == OpCodes.Ldc_I4) { try { return Convert.ToInt32(ci.operand) == v; } catch { return false; } }
            return false;
        }

        // Runs on the game's terrain thread. Racy count read is fine: worst case one extra nap or one extra pass.
        static void IdleSleep(int ms)
        {
            if (WorkerActive)
            {
                try
                {
                    var hb = fInst.GetValue(null);
                    if (hb != null && fToBuild.GetValue(hb) is System.Collections.ICollection q && q.Count > 0)
                    { Interlocked.Increment(ref sBusySkips); Thread.Sleep(0); return; }
                }
                catch { }
                Interlocked.Increment(ref sIdleNaps);
            }
            Thread.Sleep(ms);
        }
        static int ReadyCap() => WorkerActive ? readyCap : 16;
    }
}
