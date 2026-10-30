using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using PG = global::ValheimPerformanceFixes.PatchGuard;

namespace RenderTrim
{
    // 2.1.0: smoothness while travelling. Three spots where vanilla does a big chunk of work in one frame:
    //  - map fog: every explore tick that reveals a pixel re-uploads the WHOLE fog texture to the GPU
    //  - distant terrain: when you move 256 m, every far-terrain tile is rebuilt in the same frame
    //  - grass: after a teleport / terrain reset / leaving a dungeon, every grass patch around you is built in one frame
    // Each is spread out or shrunk, with its own toggle; each stands down if another mod changes the same method.
    internal static class Travel
    {
        static ConfigEntry<bool> fogOn, lodOn, grassOn; static ConfigEntry<int> lodTiles, grassPasses;
        static Action<string> L;

        internal static void Init(ConfigFile c, Harmony h, Action<string> log)
        {
            L = log;
            fogOn = c.Bind("13. Map Fog", "PartialUpload", true, "When walking reveals new map area, send only the small changed square of the fog texture to the graphics card instead of the whole texture (vanilla re-sends all of it several times a second while exploring). Falls back to the vanilla full upload by itself if anything is unusual.");
            lodOn = c.Bind("14. Distant Terrain", "SpreadRebuild", true, "When you travel far enough that the far-away terrain has to be rebuilt, rebuild a few tiles per frame instead of all of them in one frame (a hitch while running, riding or sailing). The far terrain finishes updating within a few frames.");
            lodTiles = c.Bind("14. Distant Terrain", "TilesPerFrame", 3, new ConfigDescription("How many far-terrain tiles to rebuild per frame. Higher = finishes sooner but a bigger single-frame cost. 9 or more = all at once (vanilla).", new AcceptableValueRange<int>(1, 9)));
            grassOn = c.Bind("3b. Grass", "SpreadFullRebuild", true, "After a teleport, a terrain change or coming out of a dungeon, the game rebuilds ALL grass around you in one frame. Build it nearest-first over a few frames instead.");
            grassPasses = c.Bind("3b. Grass", "FullRebuildPatchesPerFrame", 8, new ConfigDescription("Grass patches built per frame while that full rebuild is spread out. Higher = grass fills in sooner, bigger single-frame cost.", new AcceptableValueRange<int>(1, 64), new ConfigurationManagerAttributes { IsAdvanced = true }));
            Hook(h, "map fog partial upload", FogPatch);
            Hook(h, "distant terrain spread rebuild", LodPatch);
            Hook(h, "grass spread full rebuild", GrassPatch);
        }

        static void Hook(Harmony h, string what, Action<Harmony> f)
        {
            try { f(h); } catch (Exception e) { L("[Travel] " + what + " patch FAILED: " + e.Message); }
        }

        static bool StandDown(MethodBase m, string what, bool prefixes, bool transpilers)
        {
            var r = PG.Rivals(m, prefixes, transpilers);
            var o = PG.Others(m);
            if (o.Count > 0) L("[Travel] patch audit: " + PG.Name(m) + " is also patched by " + string.Join(", ", o));
            if (r.Count == 0) return false;
            L("[Travel] " + what + " stands down: " + PG.Name(m) + " is also changed by " + string.Join(", ", r));
            return true;
        }

        // ------------------------------------------------------------------ map fog (#4)
        static MethodBase exploreM; static bool fogOk, fogDead; static int rx0, ry0, rx1, ry1; static bool rectSet;
        static AccessTools.FieldRef<Minimap, float> pixelSize; static AccessTools.FieldRef<Minimap, int> texSize;
        static Texture2D staging; static int fogFull, fogPartial, sinceFull;
        const int MaxRect = 256;

        static void FogPatch(Harmony h)
        {
            exploreM = AccessTools.DeclaredMethod(typeof(Minimap), "Explore", new[] { typeof(Vector3), typeof(float) });
            if (exploreM == null) { L("[Travel] Minimap.Explore(Vector3,float) not found - map fog partial upload off"); return; }
            if (StandDown(exploreM, "map fog partial upload", false, true)) return;
            pixelSize = AccessTools.FieldRefAccess<Minimap, float>("m_pixelSize");
            texSize = AccessTools.FieldRefAccess<Minimap, int>("m_textureSize");
            h.Patch(exploreM, prefix: new HarmonyMethod(typeof(Travel), nameof(FogPre)) { priority = Priority.Last },
                transpiler: new HarmonyMethod(typeof(Travel), nameof(FogTranspiler)) { priority = Priority.Last });
            L("[Travel] map fog partial upload patch " + (fogOk ? "ok" : "NOT applied (vanilla code changed)") + " (enabled=" + fogOn.Value + ")");
        }

        static IEnumerable<CodeInstruction> FogTranspiler(IEnumerable<CodeInstruction> ins)
        {
            var apply = AccessTools.Method(typeof(Texture2D), "Apply", Type.EmptyTypes);
            var list = PG.ReplaceCalls(ins, ci => (ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && Equals(ci.operand, apply),
                AccessTools.Method(typeof(Travel), nameof(FogApply)), 1, "Minimap.Explore fog Apply()", L, out bool ok);
            fogOk = ok; return list;
        }

        // Same rectangle vanilla scans (center pixel +- radius in pixels), clamped to the texture.
        static void FogPre(Minimap __instance, Vector3 p, float radius)
        {
            rectSet = false;
            if (!fogOn.Value || fogDead || !Plugin.Active) return;
            try
            {
                float ps = pixelSize(__instance); int ts = texSize(__instance); if (ps <= 0f || ts <= 0) return;
                int num = (int)Mathf.Ceil(radius / ps); int half = ts / 2;
                int px = Utils.RoundToInt(p.x / ps + half), py = Utils.RoundToInt(p.z / ps + half);
                rx0 = Math.Max(0, px - num); ry0 = Math.Max(0, py - num); rx1 = Math.Min(ts - 1, px + num); ry1 = Math.Min(ts - 1, py + num);
                rectSet = rx1 >= rx0 && ry1 >= ry0;
            }
            catch { rectSet = false; }
        }

        static void FogApply(Texture2D tex)
        {
            if (!rectSet || tex == null) { Full(tex); return; }
            rectSet = false;
            try
            {
                int w = rx1 - rx0 + 1, hh = ry1 - ry0 + 1;
                if (w > MaxRect || hh > MaxRect || tex.mipmapCount > 1 || !tex.isReadable || (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0) { Full(tex); return; }
                if (staging == null || staging.graphicsFormat != tex.graphicsFormat)
                {
                    if (staging != null) UnityEngine.Object.Destroy(staging);
                    staging = new Texture2D(MaxRect, MaxRect, tex.graphicsFormat, UnityEngine.Experimental.Rendering.TextureCreationFlags.None) { name = "_vpf fog staging", wrapMode = TextureWrapMode.Clamp, filterMode = tex.filterMode };
                }
                // every 30th upload is a full one, in case something else edited the fog texture without uploading it
                if (++sinceFull >= 30) { sinceFull = 0; Full(tex); return; }
                staging.SetPixels(0, 0, w, hh, tex.GetPixels(rx0, ry0, w, hh));
                staging.Apply(false, false);
                Graphics.CopyTexture(staging, 0, 0, 0, 0, w, hh, tex, 0, 0, rx0, ry0);
                fogPartial++;
            }
            catch (Exception e) { fogDead = true; L("[Travel] map fog partial upload turned itself off (" + e.GetType().Name + ": " + e.Message + ") - vanilla full uploads from now on"); Full(tex); }
        }

        static void Full(Texture2D tex) { if (tex != null) { tex.Apply(); fogFull++; } }


        // ------------------------------------------------------------------ distant terrain (#7)
        static FieldInfo fHms, fState, fHmState; static MethodInfo mRebuild; static object doneVal; static int doneInt;
        static readonly HashSet<TerrainLod> spreading = new HashSet<TerrainLod>(); static int lodSpread; static bool lodDead;

        static void LodPatch(Harmony h)
        {
            var t = typeof(TerrainLod);
            var rebuildAll = AccessTools.DeclaredMethod(t, "RebuildAllHeightmaps");
            var tileType = AccessTools.Inner(t, "HeightmapWithOffset");
            var ready = tileType != null ? AccessTools.DeclaredMethod(t, "IsTerrainReady", new[] { tileType }) : null;
            fHms = AccessTools.Field(t, "m_heightmaps"); fHmState = AccessTools.Field(t, "m_heightmapState");
            fState = tileType != null ? AccessTools.Field(tileType, "m_state") : null;
            mRebuild = tileType != null ? AccessTools.DeclaredMethod(t, "RebuildHeightmap", new[] { tileType }) : null;
            if (rebuildAll == null || ready == null || fHms == null || fHmState == null || fState == null || mRebuild == null) { L("[Travel] TerrainLod members not found - distant terrain spread off"); return; }
            doneVal = Enum.Parse(fState.FieldType, "Done"); doneInt = Convert.ToInt32(doneVal);
            if (StandDown(rebuildAll, "distant terrain spread", true, true) || StandDown(ready, "distant terrain spread", true, true)) return;
            h.Patch(rebuildAll, prefix: new HarmonyMethod(typeof(Travel), nameof(LodRebuildPre)) { priority = Priority.Last });
            h.Patch(ready, prefix: new HarmonyMethod(typeof(Travel), nameof(LodReadyPre)) { priority = Priority.Last });
            L("[Travel] distant terrain spread patch ok (enabled=" + lodOn.Value + ", tiles/frame=" + lodTiles.Value + ")");
        }

        static bool LodRebuildPre(TerrainLod __instance)
        {
            if (!lodOn.Value || lodDead || !Plugin.Active || lodTiles.Value >= 9) { spreading.Remove(__instance); return true; }
            try
            {
                if (!(fHms.GetValue(__instance) is System.Collections.IList tiles)) return true;
                int n = 0, left = 0; var args = new object[1];
                for (int i = 0; i < tiles.Count; i++)
                {
                    var tile = tiles[i]; if (Convert.ToInt32(fState.GetValue(tile)) == doneInt) continue;
                    if (n < lodTiles.Value) { args[0] = tile; mRebuild.Invoke(__instance, args); n++; } else left++;
                }
                if (left == 0) { fHmState.SetValue(__instance, doneVal); spreading.Remove(__instance); return false; }
                if (spreading.Add(__instance)) lodSpread++;
                return false;   // stays "needs rebuild": next frame vanilla checks readiness again and calls us for the next tiles
            }
            catch (Exception e) { lodDead = true; spreading.Clear(); L("[Travel] distant terrain spread turned itself off: " + e.Message); return true; }
        }

        // While spreading, tiles already rebuilt at the new position count as ready (vanilla would re-query the builder for them).
        static bool LodReadyPre(TerrainLod __instance, object __0, ref bool __result)
        {
            if (spreading.Count == 0 || !spreading.Contains(__instance) || __0 == null) return true;
            try { if (Convert.ToInt32(fState.GetValue(__0)) == doneInt) { __result = true; return false; } } catch { }
            return true;
        }

        // ------------------------------------------------------------------ grass full rebuild (#8)
        static Action<ClutterSystem, bool, Vector3> genPatches; static AccessTools.FieldRef<ClutterSystem, bool> forceRebuild, menuHack;
        static bool inSpread, grassDead; static int made, grassSpread;

        static void GrassPatch(Harmony h)
        {
            var gp = AccessTools.DeclaredMethod(typeof(ClutterSystem), "GeneratePatches", new[] { typeof(bool), typeof(Vector3) });
            var gv = AccessTools.DeclaredMethod(typeof(ClutterSystem), "GenerateVegPatch");
            if (gp == null || gv == null) { L("[Travel] ClutterSystem.GeneratePatches/GenerateVegPatch not found - grass spread off"); return; }
            if (StandDown(gp, "grass spread full rebuild", true, true)) return;
            genPatches = AccessTools.MethodDelegate<Action<ClutterSystem, bool, Vector3>>(gp);
            forceRebuild = AccessTools.FieldRefAccess<ClutterSystem, bool>("m_forceRebuild");
            menuHack = AccessTools.FieldRefAccess<ClutterSystem, bool>("m_menuHack");
            h.Patch(gp, prefix: new HarmonyMethod(typeof(Travel), nameof(GrassPre)) { priority = Priority.Last });
            h.Patch(gv, postfix: new HarmonyMethod(typeof(Travel), nameof(VegPost)) { priority = Priority.Last });
            L("[Travel] grass spread full rebuild patch ok (enabled=" + grassOn.Value + ", patches/frame=" + grassPasses.Value + ")");
        }

        static void VegPost(object __result) { if (inSpread && __result != null) made++; }

        static bool GrassPre(ClutterSystem __instance, bool rebuildAll, Vector3 center)
        {
            if (!rebuildAll || inSpread || grassDead || !grassOn.Value || !Plugin.Active || Player.m_localPlayer == null) return true;
            try
            {
                if (menuHack(__instance)) return true;
                // each normal pass builds the nearest missing/reset patch (and keeps the existing ones alive)
                inSpread = true; int n = Math.Max(1, grassPasses.Value), i = 0; bool more = true;
                for (; i < n; i++) { made = 0; genPatches(__instance, false, center); if (made == 0) { more = false; break; } }
                if (more) { forceRebuild(__instance) = true; grassSpread++; }   // not finished: continue next frame (after the heightmap check, like vanilla)
                return false;
            }
            catch (Exception e) { grassDead = true; L("[Travel] grass spread turned itself off: " + e.Message); return true; }
            finally { inSpread = false; }
        }

        internal static string Stats() => "fog partial=" + fogPartial + " full=" + fogFull + (fogDead ? " (OFF)" : "") + " | farTerrainSpreads=" + lodSpread + " | grassSpreadFrames=" + grassSpread;
    }
}
