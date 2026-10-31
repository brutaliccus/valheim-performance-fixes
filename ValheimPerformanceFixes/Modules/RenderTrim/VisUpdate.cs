// Valheim Performance Fixes - equipment visual update trims (2.1.2-test). MIT License.
// Client only. Every character, armor stand and player runs VisEquipment.UpdateEquipmentVisuals every frame, and other mods
// hang work on it that allocates garbage each time even when nothing changed. Each part has its own switch under
// "24. Equipment Visual Updates":
//  1. InventorySlotsSkip: InventorySlots rebuilds its custom-slot visual list (belt, lantern, ...) from the ZDO every frame.
//     The call is skipped while everything it reads is unchanged: the ZDO values it reads, the items the character actually
//     shows (after any transmog), the body model, its visual count and its own cache resets. A full refresh still runs every
//     InventorySlotsRefreshSec. Turns itself off when the InventorySlots method is not found.
//  2. ArmorBonesSkip: BalrondAbsoluteOverhaul's per-frame armor bone check (reads the body bones array every frame) is skipped
//     while the body mesh, root bone, shown chest / leg items and their instances are unchanged; it still runs every
//     ArmorBonesRecheckSec. Turns itself off when the method is not found.
//  3. DistanceThrottle (off by default): characters farther than DistanceMeters from the camera update their equipment
//     visuals only every DistanceEveryNthFrame frames. Your own character always updates every frame.
// Transmog (vpf.Transmog) works by changing the item hashes VisEquipment shows; those hashes are part of the change check,
// so a transmog change refreshes both skips on the same frame. Mods can also call VisUpdate.ForceRefresh(VisEquipment) or
// bump the public static VisUpdate.Epoch.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RenderTrim
{
    public static class VisUpdate
    {
        const string S = "24. Equipment Visual Updates";
        static ConfigEntry<bool> slotsOn, bonesOn, distOn;
        static ConfigEntry<float> slotsSec, bonesSec, distM;
        static ConfigEntry<int> distEvery;
        static Action<string> log;

        /// <summary>Bump to make both skips run their target on the next frame for every character.</summary>
        public static int Epoch;
        /// <summary>Makes both skips run their target on the next call for this VisEquipment.</summary>
        public static void ForceRefresh(VisEquipment vis) { if (vis == null) return; int id = vis.GetInstanceID(); slotState.Remove(id); boneState.Remove(id); }

        static AccessTools.FieldRef<VisEquipment, ZNetView> fNview;
        static AccessTools.FieldRef<VisEquipment, int> fChest, fLeg, fHelmet, fShoulder, fUtility, fLeft, fRight, fTrinket, fLeftBack, fRightBack, fModel, fShVar, fShQ, fLeftVar, fLeftBackVar;
        static AccessTools.FieldRef<VisEquipment, List<GameObject>> fChestInst, fLegInst;

        internal static bool slotsOk, bonesOk, distOk;
        static int sSlotsRun, sSlotsSkip, sBonesRun, sBonesSkip, sDistRun, sDistSkip;

        internal static void Init(ConfigFile cfg, Harmony h, Action<string> L)
        {
            log = L;
            slotsOn = cfg.Bind(S, "InventorySlotsSkip", true, "Skip InventorySlots' per-frame custom-slot visual rebuild (it allocates garbage every frame for every character) while nothing it reads has changed: its ZDO values, the items the character shows (transmog included), the body model and its visual list. A full refresh still runs every InventorySlotsRefreshSec. Off = it runs every frame.");
            slotsSec = cfg.Bind(S, "InventorySlotsRefreshSec", 2f, new ConfigDescription("Safety refresh: the InventorySlots update runs at least this often per character even when nothing changed.", new AcceptableValueRange<float>(0.25f, 10f)));
            bonesOn = cfg.Bind(S, "ArmorBonesSkip", true, "Skip BalrondAbsoluteOverhaul's per-frame armor bone check while the body mesh, root bone and shown chest / leg items are unchanged. It still runs every ArmorBonesRecheckSec. Off = it runs every frame.");
            bonesSec = cfg.Bind(S, "ArmorBonesRecheckSec", 1f, new ConfigDescription("Safety recheck: the armor bone check runs at least this often per character.", new AcceptableValueRange<float>(0.25f, 10f)));
            distOn = cfg.Bind(S, "DistanceThrottle", false, "Characters farther than DistanceMeters from the camera update their equipment visuals (and every mod hooked on that) only every DistanceEveryNthFrame frames. Your own character always updates every frame. Equipment changes of far characters show up a few frames later. Off by default.");
            distM = cfg.Bind(S, "DistanceMeters", 40f, new ConfigDescription("Distance from the camera beyond which DistanceThrottle applies.", new AcceptableValueRange<float>(10f, 200f)));
            distEvery = cfg.Bind(S, "DistanceEveryNthFrame", 4, new ConfigDescription("Far characters update their equipment visuals once every this many frames.", new AcceptableValueRange<int>(2, 30)));

            try
            {
                var t = typeof(VisEquipment);
                fNview = AccessTools.FieldRefAccess<VisEquipment, ZNetView>("m_nview");
                fChest = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentChestItemHash");
                fLeg = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentLegItemHash");
                fHelmet = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentHelmetItemHash");
                fShoulder = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentShoulderItemHash");
                fUtility = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentUtilityItemHash");
                fLeft = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentLeftItemHash");
                fRight = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentRightItemHash");
                fTrinket = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentTrinketItemHash");
                fLeftBack = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentLeftBackItemHash");
                fRightBack = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentRightBackItemHash");
                fModel = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentModelIndex");
                fShVar = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentShoulderItemVariant");
                fShQ = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentShoulderItemQuality");
                fLeftVar = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentLeftItemVariant");
                fLeftBackVar = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentLeftBackItemVariant");
                fChestInst = AccessTools.FieldRefAccess<VisEquipment, List<GameObject>>("m_chestItemInstances");
                fLegInst = AccessTools.FieldRefAccess<VisEquipment, List<GameObject>>("m_legItemInstances");
            }
            catch (Exception e) { L("[Equipment Visuals] VisEquipment fields not found, both skips off: " + e.Message); return; }

            HookSlots(h);
            HookBones(h);
            try
            {
                var cu = AccessTools.DeclaredMethod(typeof(VisEquipment), "CustomUpdate");
                if (cu != null) { h.Patch(cu, prefix: new HarmonyMethod(typeof(VisUpdate), nameof(DistPre)) { priority = Priority.High }); distOk = true; }
                else L("[Equipment Visuals] VisEquipment.CustomUpdate not found, DistanceThrottle off");
            }
            catch (Exception e) { L("[Equipment Visuals] distance throttle hook failed: " + e.Message); }
            L("[Equipment Visuals] InventorySlotsSkip " + (slotsOk ? (slotsOn.Value ? "on" : "off (switch)") : "not available") + ", ArmorBonesSkip " + (bonesOk ? (bonesOn.Value ? "on" : "off (switch)") : "not available") + ", DistanceThrottle " + (distOk ? (distOn.Value ? "on" : "off") : "not available"));
        }

        // ---- shared: what the character shows right now (after transmog) ----
        static int Shown(VisEquipment v)
        {
            unchecked
            {
                int k = 17;
                k = k * 31 + fChest(v); k = k * 31 + fLeg(v); k = k * 31 + fHelmet(v); k = k * 31 + fShoulder(v); k = k * 31 + fUtility(v);
                k = k * 31 + fLeft(v); k = k * 31 + fRight(v); k = k * 31 + fTrinket(v); k = k * 31 + fLeftBack(v); k = k * 31 + fRightBack(v);
                k = k * 31 + fModel(v); k = k * 31 + fShVar(v); k = k * 31 + fShQ(v); k = k * 31 + fLeftVar(v); k = k * 31 + fLeftBackVar(v);
                return k;
            }
        }
        static ZDO ZdoOf(VisEquipment v) { var nv = fNview(v); return nv != null && nv.IsValid() ? nv.GetZDO() : null; }
        static int Inst(List<GameObject> l)
        {
            if (l == null) return 0;
            unchecked { int k = l.Count + 1; for (int i = 0; i < l.Count; i++) { var g = l[i]; k = k * 31 + (ReferenceEquals(g, null) ? 0 : g.GetInstanceID()); } return k; }
        }

        // purge per-character state of objects that are gone (about once a minute)
        static float nextPurge;
        static readonly List<int> purgeTmp = new List<int>();
        static void MaybePurge(float t)
        {
            if (t < nextPurge) return;
            nextPurge = t + 60f;
            purgeTmp.Clear(); foreach (var kv in slotState) if (t - kv.Value.seen > 30f) purgeTmp.Add(kv.Key);
            foreach (var k in purgeTmp) slotState.Remove(k);
            purgeTmp.Clear(); foreach (var kv in boneState) if (t - kv.Value.seen > 30f) purgeTmp.Add(kv.Key);
            foreach (var k in purgeTmp) boneState.Remove(k);
        }

        // ---------------------------------------------------------------- 1. InventorySlots
        struct SlotState { public ZDO zdo; public uint rev; public int inputs, shown, visCount, epoch, ownEpoch; public float at, seen; }
        static readonly Dictionary<int, SlotState> slotState = new Dictionary<int, SlotState>();
        static Func<bool> isReady;
        static ICollection isVisuals;
        static PropertyInfo isSlotDefs; static FieldInfo isDefsHolder, isDefsVersion; static object isDefsObj;
        static int[] isKeys = new int[0]; static int isKeysVersion = int.MinValue; static float isKeysAt = -100f; static int isOwnEpoch;

        static void HookSlots(Harmony h)
        {
            try
            {
                var pt = AccessTools.TypeByName("InventorySlots.InventorySlotsPlugin");
                var m = pt == null ? null : AccessTools.DeclaredMethod(pt, "UpdateCustomEquipmentVisualsFromZdo", new[] { typeof(VisEquipment) });
                if (m == null) { log("[Equipment Visuals] InventorySlots update method not found (mod not installed or changed): InventorySlotsSkip off"); return; }
                var ready = AccessTools.DeclaredMethod(pt, "IsEquipmentMaterialManagerReady", Type.EmptyTypes);
                if (ready != null && ready.ReturnType == typeof(bool)) isReady = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), ready);
                try
                {
                    var ev = AccessTools.Field(pt, "EquipmentVisuals")?.GetValue(null);
                    if (ev != null) isVisuals = AccessTools.Field(ev.GetType(), "Visuals")?.GetValue(ev) as ICollection;
                }
                catch { }
                try
                {
                    isSlotDefs = AccessTools.Property(pt, "SlotDefinitions");
                    isDefsHolder = AccessTools.Field(pt, "InventoryDefinitions");
                    isDefsObj = isDefsHolder?.GetValue(null);
                    if (isDefsObj != null) isDefsVersion = AccessTools.Field(isDefsObj.GetType(), "SlotDefinitionVersion");
                }
                catch { }
                if (isReady == null || isVisuals == null || isSlotDefs == null)
                { log("[Equipment Visuals] InventorySlots internals not found (ready=" + (isReady != null) + " visuals=" + (isVisuals != null) + " slots=" + (isSlotDefs != null) + "): InventorySlotsSkip off"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(VisUpdate), nameof(SlotsPre)) { priority = Priority.High }, postfix: new HarmonyMethod(typeof(VisUpdate), nameof(SlotsPost)) { priority = Priority.Last });
                var inv = AccessTools.DeclaredMethod(pt, "InvalidateCustomEquipmentVisualUpdateCache", Type.EmptyTypes);
                if (inv != null) h.Patch(inv, postfix: new HarmonyMethod(typeof(VisUpdate), nameof(SlotsReset)));
                var clr = AccessTools.DeclaredMethod(pt, "ClearCustomEquipmentVisuals", Type.EmptyTypes);
                if (clr != null) h.Patch(clr, postfix: new HarmonyMethod(typeof(VisUpdate), nameof(SlotsReset)));
                slotsOk = true;
            }
            catch (Exception e) { slotsOk = false; log("[Equipment Visuals] InventorySlots hook failed, InventorySlotsSkip off: " + e.Message); }
        }
        static void SlotsReset() { isOwnEpoch++; isKeysAt = -100f; }

        // ZDO keys InventorySlots reads for each custom equipment slot (same strings it builds every frame)
        static void RefreshKeys(float t)
        {
            if (t - isKeysAt < 1f) return;
            isKeysAt = t;
            int ver = 0;
            try { if (isDefsVersion != null && isDefsObj != null) ver = (int)isDefsVersion.GetValue(isDefsObj); } catch { }
            if (ver == isKeysVersion && isKeys.Length > 0) return;
            var keys = new List<int>();
            try
            {
                if (isSlotDefs.GetValue(null) is IEnumerable defs)
                    foreach (var d in defs)
                    {
                        if (d == null) continue;
                        var dt = d.GetType();
                        if (dt.GetProperty("Kind")?.GetValue(d)?.ToString() != "CustomEquipment") continue;
                        var id = dt.GetProperty("Id")?.GetValue(d) as string; if (id == null) continue;
                        keys.Add(("InventorySlots_CustomEquipmentVisual_Item_" + id).GetStableHashCode());
                        keys.Add(("InventorySlots_CustomEquipmentVisual_Variant_" + id).GetStableHashCode());
                        keys.Add(("InventorySlots_CustomEquipmentVisual_Quality_" + id).GetStableHashCode());
                    }
            }
            catch { }
            if (ver != isKeysVersion) isOwnEpoch++;
            isKeys = keys.ToArray(); isKeysVersion = ver;
        }
        static int Inputs(ZDO z)
        {
            if (z == null) return 0;
            unchecked { int k = isKeys.Length + 7; for (int i = 0; i < isKeys.Length; i++) k = k * 31 + z.GetInt(isKeys[i]); return k; }
        }

        static bool SlotsPre(VisEquipment __0, out bool __state)
        {
            __state = true;
            if (!slotsOn.Value || !Plugin.Active || __0 == null) return true;
            float t = Time.unscaledTime;
            MaybePurge(t);
            RefreshKeys(t);
            int id = __0.GetInstanceID();
            if (!slotState.TryGetValue(id, out var s)) return true;
            if (t - s.at >= slotsSec.Value || s.epoch != Epoch || s.ownEpoch != isOwnEpoch) return true;
            var z = ZdoOf(__0);
            if (!ReferenceEquals(z, s.zdo)) return true;
            if (isVisuals.Count != s.visCount || Shown(__0) != s.shown) return true;
            if (z != null && z.DataRevision != s.rev)
            {
                int inp = Inputs(z);
                if (inp != s.inputs) return true;
                s.rev = z.DataRevision; // something else in the ZDO changed, not what InventorySlots reads
            }
            s.seen = t; slotState[id] = s;
            sSlotsSkip++; __state = false;
            return false;
        }
        static void SlotsPost(VisEquipment __0, bool __state)
        {
            if (!__state || __0 == null) return;
            sSlotsRun++;
            int id = __0.GetInstanceID();
            // only remember the state when the update really ran (it returns early until the material manager is ready)
            bool ran = false; try { ran = ObjectDB.instance != null && isReady(); } catch { }
            if (!ran || !slotsOn.Value) { slotState.Remove(id); return; }
            var z = ZdoOf(__0);
            float t = Time.unscaledTime;
            slotState[id] = new SlotState { zdo = z, rev = z != null ? z.DataRevision : 0u, inputs = Inputs(z), shown = Shown(__0), visCount = isVisuals.Count, epoch = Epoch, ownEpoch = isOwnEpoch, at = t, seen = t };
        }

        // ---------------------------------------------------------------- 2. BalrondAbsoluteOverhaul armor bones
        struct BoneState { public int body, mesh, root, chest, legs, shown, epoch; public float at, seen; }
        static readonly Dictionary<int, BoneState> boneState = new Dictionary<int, BoneState>();

        static void HookBones(Harmony h)
        {
            try
            {
                var st = AccessTools.TypeByName("Balrond.Equipment.EquipmentVisualService");
                var m = st == null ? null : AccessTools.DeclaredMethod(st, "RepairArmorBones", new[] { typeof(VisEquipment) });
                if (m == null) { log("[Equipment Visuals] BalrondAbsoluteOverhaul RepairArmorBones not found (mod not installed or changed): ArmorBonesSkip off"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(VisUpdate), nameof(BonesPre)) { priority = Priority.High }, postfix: new HarmonyMethod(typeof(VisUpdate), nameof(BonesPost)) { priority = Priority.Last });
                bonesOk = true;
            }
            catch (Exception e) { bonesOk = false; log("[Equipment Visuals] armor bones hook failed, ArmorBonesSkip off: " + e.Message); }
        }
        static BoneState BonesNow(VisEquipment v, float t)
        {
            var b = v.m_bodyModel;
            var r = new BoneState { shown = Shown(v), chest = Inst(fChestInst(v)), legs = Inst(fLegInst(v)), epoch = Epoch, at = t, seen = t };
            if (!ReferenceEquals(b, null) && b != null)
            {
                r.body = b.GetInstanceID();
                var mesh = b.sharedMesh; r.mesh = mesh != null ? mesh.GetInstanceID() : 0;
                var root = b.rootBone; r.root = root != null ? root.GetInstanceID() : 0;
            }
            return r;
        }
        static bool BonesPre(VisEquipment __0, out bool __state)
        {
            __state = true;
            if (!bonesOn.Value || !Plugin.Active || __0 == null) return true;
            float t = Time.unscaledTime;
            MaybePurge(t);
            int id = __0.GetInstanceID();
            if (!boneState.TryGetValue(id, out var s)) return true;
            if (t - s.at >= bonesSec.Value || s.epoch != Epoch) return true;
            var n = BonesNow(__0, t);
            if (n.body != s.body || n.mesh != s.mesh || n.root != s.root || n.chest != s.chest || n.legs != s.legs || n.shown != s.shown) return true;
            s.seen = t; boneState[id] = s;
            sBonesSkip++; __state = false;
            return false;
        }
        static void BonesPost(VisEquipment __0, bool __state)
        {
            if (!__state || __0 == null) return;
            sBonesRun++;
            if (!bonesOn.Value) return;
            boneState[__0.GetInstanceID()] = BonesNow(__0, Time.unscaledTime);
        }

        // ---------------------------------------------------------------- 3. distance throttle
        static int camFrame = -1; static Vector3 camPos; static bool camOk;
        static bool DistPre(VisEquipment __instance)
        {
            if (!distOn.Value || !Plugin.Active || __instance == null) return true;
            int f = Time.frameCount;
            if (f != camFrame)
            {
                camFrame = f; camOk = false;
                try { var gc = GameCamera.instance; if (gc != null) { camPos = gc.transform.position; camOk = true; } } catch { }
            }
            if (!camOk) return true;
            var lp = Player.m_localPlayer;
            if (lp != null && ReferenceEquals(lp.gameObject, __instance.gameObject)) return true;
            float d = distM.Value;
            if ((__instance.transform.position - camPos).sqrMagnitude <= d * d) return true;
            int every = Math.Max(2, distEvery.Value);
            if (((f + (__instance.GetInstanceID() & 0x7fff)) % every) == 0) { sDistRun++; return true; }
            sDistSkip++;
            return false;
        }

        internal static string Stats()
        {
            string s = "invSlots " + (slotsOk ? (slotsOn.Value ? "on" : "off") : "n/a") + " run=" + sSlotsRun + " skipped=" + sSlotsSkip
                + " | armorBones " + (bonesOk ? (bonesOn.Value ? "on" : "off") : "n/a") + " run=" + sBonesRun + " skipped=" + sBonesSkip
                + " | distance " + (distOk ? (distOn.Value ? "on" : "off") : "n/a") + " farRun=" + sDistRun + " farSkipped=" + sDistSkip
                + " | tracked " + slotState.Count + "/" + boneState.Count;
            sSlotsRun = sSlotsSkip = sBonesRun = sBonesSkip = sDistRun = sDistSkip = 0;
            return s;
        }
    }
}
