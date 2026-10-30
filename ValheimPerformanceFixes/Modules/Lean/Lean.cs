using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using PG = global::ValheimPerformanceFixes.PatchGuard;

namespace ValheimPerformanceFixes.Lean
{
    // 2.3.6: small per-frame savings. Client only. Each part has its own switch (default on) and stands down on its own when
    // another mod changes the same game method in a way that could clash (logged). Nothing here changes what the game
    // decides: the same objects load in the same order and the same meshes are shown.
    //  20. Allocation Cuts      - reuse grass instance arrays, collision callback objects, contact arrays, and set ZDO values
    //                             without boxing them for the "did it change" test
    //  21. Terrain Mesh Reuse   - a terrain tile whose heights did not change keeps its collision / render mesh
    //  22. Object Pass          - the 30-per-second object create/remove scan is skipped while nothing it looks at changed
    //  23. Combat Housekeeping  - no "look rotation viewing vector is zero" warnings from stopped projectiles, chest/network
    //                             chatter kept out of the log, mist emitters read mist volume positions once per frame
    //  (24. Support Lookups was removed in 2.3.7; its old settings are dropped from the .cfg on start)
    internal static class Lean
    {
        internal const string HarmonyId = "vpf.ValheimPerformanceFixes.lean";
        const string SA = "20. Allocation Cuts", SM = "21. Terrain Mesh Reuse", SO = "22. Object Pass", SC = "23. Combat Housekeeping";
        internal static Action<string> L;
        static Harmony h;
        internal static ConfigEntry<bool> grassOn, reuseOn, contactOn, zdoOn, meshOn, idleOn, projOn, quietOn, mistOn;

        internal static void Init(ConfigFile c, Action<string> log)
        {
            L = s => log("[Lean] " + s);
            { int gone = Removed.Forget(c); if (gone > 0) L("dropped " + gone + " old setting(s) of a removed feature (24. Support Lookups) from the .cfg"); }
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { L("headless/server process: lean savings are client-only, nothing patched"); return; }
            grassOn = c.Bind(SA, "GrassArrays", true, "The 64 KB instance array of each grass/clutter patch is handed back when the patch is destroyed and given (cleared) to the next new patch instead of a fresh one. Fewer garbage collections while you move. Off = vanilla.");
            reuseOn = c.Bind(SA, "CollisionReuse", true, "Unity reuses one collision callback object instead of making a new one for every contact callback (Physics.reuseCollisionCallbacks). Off = the game's own setting.");
            contactOn = c.Bind(SA, "ContactBuffer", true, "The character, impact-effect and floating-terrain collision handlers read their contact points into a reused array instead of a new array per callback. Same points, same order. Off = vanilla.");
            zdoOn = c.Bind(SA, "ZdoSetNoBox", true, "Setting a number, vector or rotation on a network object compares it with the stored value without boxing (vanilla allocates for every compare). Same result and same 'changed' answer. Off = vanilla.");
            meshOn = c.Bind(SM, "Enabled", true, "When a terrain tile is rebuilt but its heights, size and biomes are exactly the same as last time, the existing collision and render meshes are kept instead of being rebuilt (no collider re-cook). Any height change rebuilds as vanilla. Off = vanilla.");
            idleOn = c.Bind(SO, "SkipIdlePasses", true, "The object create/remove pass (30 times a second) is skipped while nothing it looks at changed: same zone, same view range, every nearby object already created, no object added, removed or changed type, nothing held back by the removal cap or location keep-alive. Runs at least every 0.5 s anyway. Player builds and everything else still load in the game's own order. Off = vanilla.");
            projOn = c.Bind(SC, "ProjectileLook", true, "A projectile whose velocity is exactly zero keeps the same rotation the game would give it without Unity logging a 'Look rotation viewing vector is zero' warning each physics step. Off = vanilla.");
            quietOn = c.Bind(SC, "QuietLogs", true, "The per-request chest open/stack/take-all lines and 'Failed to send data' network lines are counted instead of written to the log. Off = vanilla logging.");
            mistOn = c.Bind(SC, "MistOnce", true, "Mist emitters read the mist volumes' positions once per frame instead of once per particle test. Same tests, same order, same results. Off = vanilla.");
            h = new Harmony(HarmonyId);
            Hook("grass arrays", Alloc.PatchGrass);
            Hook("contact buffer", Alloc.PatchContacts);
            Hook("zdo set", Alloc.PatchZdo);
            Hook("terrain mesh reuse", MeshKeep.Patch);
            Hook("projectile look", Combat.PatchProjectile);
            Hook("quiet logs", Combat.PatchQuiet);
            Hook("mist once", Combat.PatchMist);
            Hook("object pass", IdlePass.Prepare);
            reuseOn.SettingChanged += (s, e) => Alloc.ApplyReuse();
            Alloc.ApplyReuse();
            var go = new GameObject("vpf_lean"); UnityEngine.Object.DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideAndDontSave; go.AddComponent<Runner>();
            L("lean savings set up");
        }
        static void Hook(string what, Action<Harmony> f) { try { f(h); } catch (Exception e) { L(what + " patch FAILED: " + e.Message); } }

        // another mod skips or rewrites this method: leave it alone
        internal static bool StandDown(MethodBase m, string what, params string[] allowed)
        {
            if (m == null) { L(what + ": target method not found (game update?) - off"); return true; }
            var r = PG.Rivals(m, true, true); var bad = new List<string>();
            foreach (var x in r) { bool ok = false; foreach (var a in allowed) if (x.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0) ok = true; if (!ok) bad.Add(x); }
            if (bad.Count == 0) return false;
            L(what + " stands down: " + PG.Name(m) + " is also changed by " + string.Join(", ", bad)); return true;
        }
        internal static bool IsCall(CodeInstruction ci, MethodInfo target) => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == target;

        sealed class Runner : MonoBehaviour
        {
            float nextCede;
            void Update()
            {
                try { Alloc.Tick(); } catch (Exception e) { L("grass array return failed, pooling off: " + e.Message); Alloc.grassBroken = true; }
                float t = Time.realtimeSinceStartup;
                if (t >= nextCede) { nextCede = t + 1f; try { IdlePass.Drive(); } catch (Exception e) { L("object pass check failed: " + e.Message); } }
            }
            void OnDestroy() { try { Alloc.RestoreReuse(); } catch { } }
        }

        internal static string Stats()
        {
            var s = "allocCuts grassArrays new=" + Alloc.sRentNew + " reused=" + Alloc.sRentReused + " handedBack=" + Alloc.sReturned + " pooled=" + Alloc.PoolCount()
                + " contactArraysSaved=" + Alloc.sContacts + " zdoSetUnboxed=" + Alloc.sZdoSame + "same/" + Alloc.sZdoSet + "changed collisionReuse=" + (reuseOn != null && reuseOn.Value ? "on" : "off")
                + " | terrainMesh collisionKept=" + MeshKeep.sColKept + " renderKept=" + MeshKeep.sRenKept + " rebuilt=" + MeshKeep.sColBuilt + "/" + MeshKeep.sRenBuilt
                + " | objectPass skipped=" + IdlePass.sSkipped + " ran=" + IdlePass.sRan + " (" + IdlePass.State() + ")"
                + " | combat zeroLook=" + Combat.sZeroLook + " quietLines=" + Combat.sQuiet + " mistSnapshots=" + Combat.sMistSnaps + " mistTests=" + Combat.sMistTests;
            Interlocked.Exchange(ref Alloc.sRentNew, 0); Interlocked.Exchange(ref Alloc.sRentReused, 0); Alloc.sReturned = 0; Alloc.sContacts = 0; Alloc.sZdoSame = Alloc.sZdoSet = 0;
            MeshKeep.sColKept = MeshKeep.sRenKept = MeshKeep.sColBuilt = MeshKeep.sRenBuilt = 0; IdlePass.sSkipped = IdlePass.sRan = 0;
            Combat.sZeroLook = Combat.sQuiet = Combat.sMistSnaps = 0; Combat.sMistTests = 0;
            return s;
        }
    }

    // ---------------------------------------------------------------------------------------------------- 20. Allocation Cuts
    internal static class Alloc
    {
        internal static int sRentNew, sRentReused, sReturned, sContacts, sZdoSame, sZdoSet;
        internal static volatile bool grassLive; internal static bool grassBroken;
        const int GrassLen = 1024, PoolCap = 96;
        static readonly object poolLock = new object();
        static readonly Stack<Matrix4x4[]> pool = new Stack<Matrix4x4[]>();
        static AccessTools.FieldRef<InstanceRenderer, Matrix4x4[]> irArr;
        struct Parked { public InstanceRenderer r; public Matrix4x4[] a; }
        static readonly Dictionary<int, Parked> parked = new Dictionary<int, Parked>();
        static readonly List<int> parkedGone = new List<int>();
        internal static int PoolCount() { lock (poolLock) return pool.Count; }

        internal static void PatchGrass(Harmony h)
        {
            var ctor = AccessTools.DeclaredConstructor(typeof(InstanceRenderer), Type.EmptyTypes);
            var en = AccessTools.DeclaredMethod(typeof(InstanceRenderer), "OnEnable"); var dis = AccessTools.DeclaredMethod(typeof(InstanceRenderer), "OnDisable");
            if (ctor == null || en == null || dis == null || AccessTools.DeclaredField(typeof(InstanceRenderer), "m_instances") == null) { Lean.L("grass arrays: InstanceRenderer members not found - off"); return; }
            if (Lean.StandDown(ctor, "grass arrays")) return;
            irArr = AccessTools.FieldRefAccess<InstanceRenderer, Matrix4x4[]>("m_instances");
            grassLive = Lean.grassOn.Value; Lean.grassOn.SettingChanged += (s, e) => grassLive = Lean.grassOn.Value;
            h.Patch(ctor, transpiler: new HarmonyMethod(typeof(Alloc), nameof(GrassCtorT)));
            h.Patch(dis, postfix: new HarmonyMethod(typeof(Alloc), nameof(GrassOff)));
            h.Patch(en, postfix: new HarmonyMethod(typeof(Alloc), nameof(GrassOn)));
        }
        static IEnumerable<CodeInstruction> GrassCtorT(IEnumerable<CodeInstruction> ins)
        {
            var rent = AccessTools.Method(typeof(Alloc), nameof(RentMatrices));
            bool ok; var r = PG.ReplaceCalls(ins, ci => ci.opcode == OpCodes.Newarr && ci.operand is Type t && t == typeof(Matrix4x4), rent, 1, "grass arrays", Lean.L, out ok);
            if (ok) Lean.L("grass arrays patched"); else grassBroken = true;
            return r;
        }
        // runs where the field initializer did "new Matrix4x4[n]" (any thread)
        public static Matrix4x4[] RentMatrices(int n)
        {
            if (n == GrassLen && grassLive && !grassBroken)
            {
                lock (poolLock) { if (pool.Count > 0) { Interlocked.Increment(ref sRentReused); return pool.Pop(); } }
                Interlocked.Increment(ref sRentNew);
            }
            return new Matrix4x4[n];
        }
        static void GrassOff(InstanceRenderer __instance)
        {
            if (!grassLive || grassBroken || irArr == null) return;
            try { var a = irArr(__instance); if (a != null && a.Length == GrassLen) parked[__instance.GetInstanceID()] = new Parked { r = __instance, a = a }; } catch { }
        }
        static void GrassOn(InstanceRenderer __instance) { if (parked.Count > 0) parked.Remove(__instance.GetInstanceID()); }
        static int tickFrame;
        // a renderer that was switched off and is now destroyed gives its array back (cleared, so it is the same as a new one)
        internal static void Tick()
        {
            if (parked.Count == 0 || ++tickFrame % 15 != 0) return;
            parkedGone.Clear();
            foreach (var kv in parked) if (kv.Value.r == null) parkedGone.Add(kv.Key);
            for (int i = 0; i < parkedGone.Count; i++)
            {
                var p = parked[parkedGone[i]]; parked.Remove(parkedGone[i]);
                if (!grassLive || grassBroken) continue;
                Array.Clear(p.a, 0, p.a.Length);
                lock (poolLock) { if (pool.Count < PoolCap) { pool.Push(p.a); sReturned++; } }
            }
            if (!grassLive && parked.Count > 0) parked.Clear();
        }

        // ---- collision callback reuse
        static bool reuseHeld, reuseOrig;
        internal static void ApplyReuse()
        {
            if (Lean.reuseOn == null) return;
            if (Lean.reuseOn.Value) { if (!reuseHeld) { reuseOrig = Physics.reuseCollisionCallbacks; reuseHeld = true; } Physics.reuseCollisionCallbacks = true; }
            else RestoreReuse();
        }
        internal static void RestoreReuse() { if (!reuseHeld) return; reuseHeld = false; Physics.reuseCollisionCallbacks = reuseOrig; }

        // ---- contact arrays
        static readonly ContactPoint[] noContacts = new ContactPoint[0];
        static readonly ContactPoint[][] byCount = new ContactPoint[65][];
        static volatile bool contactLive;
        internal static void PatchContacts(Harmony h)
        {
            contactLive = Lean.contactOn.Value; Lean.contactOn.SettingChanged += (s, e) => contactLive = Lean.contactOn.Value;
            Contact(h, AccessTools.DeclaredMethod(typeof(Character), "OnCollisionStay"), 1);
            Contact(h, AccessTools.DeclaredMethod(typeof(ImpactEffect), "OnCollisionEnter"), 2);
            Contact(h, AccessTools.DeclaredMethod(typeof(FloatingTerrain), "OnCollisionStay"), 1);
        }
        static readonly Dictionary<string, int> contactExpect = new Dictionary<string, int>();
        static void Contact(Harmony h, MethodInfo m, int n)
        {
            if (Lean.StandDown(m, "contact buffer")) return;
            contactExpect[PG.Name(m)] = n;
            h.Patch(m, transpiler: new HarmonyMethod(typeof(Alloc), nameof(ContactT)));
        }
        static IEnumerable<CodeInstruction> ContactT(IEnumerable<CodeInstruction> ins, MethodBase original)
        {
            var get = AccessTools.PropertyGetter(typeof(Collision), "contacts"); var fwd = AccessTools.Method(typeof(Alloc), nameof(Contacts));
            int n; contactExpect.TryGetValue(PG.Name(original), out n);
            bool ok; var r = PG.ReplaceCalls(ins, ci => Lean.IsCall(ci, get), fwd, n, "contact buffer " + PG.Name(original), Lean.L, out ok);
            return r;
        }
        // Same points as Collision.contacts. The handlers only read the array during the callback.
        public static ContactPoint[] Contacts(Collision c)
        {
            if (!contactLive || (object)c == null) return c.contacts;
            int n = c.contactCount;
            if (n == 0) { sContacts++; return noContacts; }
            if (n > 64) return c.contacts;
            var a = byCount[n] ?? (byCount[n] = new ContactPoint[n]);
            if (c.GetContacts(a) != n) return c.contacts;
            sContacts++; return a;
        }

        // ---- ZDO values without boxing
        static Dictionary<ZDOID, BinarySearchDictionary<int, float>> sF; static Dictionary<ZDOID, BinarySearchDictionary<int, Vector3>> sV;
        static Dictionary<ZDOID, BinarySearchDictionary<int, Quaternion>> sQ; static Dictionary<ZDOID, BinarySearchDictionary<int, int>> sI; static Dictionary<ZDOID, BinarySearchDictionary<int, long>> sL;
        static bool zdoLive;
        static T StaticField<T>(string name) where T : class { var f = AccessTools.DeclaredField(typeof(ZDOExtraData), name); return f == null || !f.IsStatic ? null : f.GetValue(null) as T; }
        internal static void PatchZdo(Harmony h)
        {
            sF = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, float>>>("s_floats"); sV = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, Vector3>>>("s_vec3");
            sQ = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, Quaternion>>>("s_quats"); sI = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, int>>>("s_ints"); sL = StaticField<Dictionary<ZDOID, BinarySearchDictionary<int, long>>>("s_longs");
            if (sF == null || sV == null || sQ == null || sI == null || sL == null) { Lean.L("zdo set: ZDOExtraData stores not found - off"); return; }
            zdoLive = Lean.zdoOn.Value; Lean.zdoOn.SettingChanged += (s, e) => zdoLive = Lean.zdoOn.Value;
            var t = typeof(ZDOExtraData); var z = typeof(ZDOID); var i = typeof(int); int n = 0;
            n += One(h, AccessTools.DeclaredMethod(t, "Set", new[] { z, i, typeof(float) }), nameof(SetF));
            n += One(h, AccessTools.DeclaredMethod(t, "Set", new[] { z, i, typeof(Vector3) }), nameof(SetV));
            n += One(h, AccessTools.DeclaredMethod(t, "Update", new[] { z, i, typeof(Vector3) }), nameof(UpdV));
            n += One(h, AccessTools.DeclaredMethod(t, "Set", new[] { z, i, typeof(Quaternion) }), nameof(SetQ));
            n += One(h, AccessTools.DeclaredMethod(t, "Set", new[] { z, i, typeof(int) }), nameof(SetI));
            n += One(h, AccessTools.DeclaredMethod(t, "Set", new[] { z, i, typeof(long) }), nameof(SetL));
            Lean.L("zdo set: " + n + "/6 setters patched");
        }
        static int One(Harmony h, MethodInfo m, string pre)
        {
            if (m == null || m.ReturnType != typeof(bool)) { Lean.L("zdo set: a ZDOExtraData setter was not found"); return 0; }
            if (Lean.StandDown(m, "zdo set")) return 0;
            h.Patch(m, prefix: new HarmonyMethod(typeof(Alloc), pre) { priority = Priority.Last }); return 1;
        }
        // Same as BinarySearchDictionary.SetValue for an existing key: equal -> false, else store and true. A missing ZDO or key goes to the game.
        static bool SetF(ZDOID zid, int hash, float value, ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal) return false; if (!zdoLive) return true;
            if (!sF.TryGetValue(zid, out var d) || d == null || !d.TryGetValue(hash, out var old)) return true;
            if (old.Equals(value)) { sZdoSame++; __result = false; return false; }
            d[hash] = value; sZdoSet++; __result = true; return false;
        }
        static bool SetV(ZDOID zid, int hash, Vector3 value, ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal) return false; if (!zdoLive) return true;
            if (!sV.TryGetValue(zid, out var d) || d == null || !d.TryGetValue(hash, out var old)) return true;
            if (old.Equals(value)) { sZdoSame++; __result = false; return false; }
            d[hash] = value; sZdoSet++; __result = true; return false;
        }
        static bool UpdV(ZDOID zid, int hash, Vector3 value, ref bool __result, bool __runOriginal) => SetV(zid, hash, value, ref __result, __runOriginal);
        static bool SetQ(ZDOID zid, int hash, Quaternion value, ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal) return false; if (!zdoLive) return true;
            if (!sQ.TryGetValue(zid, out var d) || d == null || !d.TryGetValue(hash, out var old)) return true;
            if (old.Equals(value)) { sZdoSame++; __result = false; return false; }
            d[hash] = value; sZdoSet++; __result = true; return false;
        }
        static bool SetI(ZDOID zid, int hash, int value, ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal) return false; if (!zdoLive) return true;
            if (!sI.TryGetValue(zid, out var d) || d == null || !d.TryGetValue(hash, out var old)) return true;
            if (old == value) { sZdoSame++; __result = false; return false; }
            d[hash] = value; sZdoSet++; __result = true; return false;
        }
        static bool SetL(ZDOID zid, int hash, long value, ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal) return false; if (!zdoLive) return true;
            if (!sL.TryGetValue(zid, out var d) || d == null || !d.TryGetValue(hash, out var old)) return true;
            if (old == value) { sZdoSame++; __result = false; return false; }
            d[hash] = value; sZdoSet++; __result = true; return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------- 21. Terrain Mesh Reuse
    internal static class MeshKeep
    {
        internal static int sColKept, sRenKept, sColBuilt, sRenBuilt;
        static bool ok;
        static AccessTools.FieldRef<Heightmap, List<float>> fHeights; static AccessTools.FieldRef<Heightmap, MeshCollider> fCol; static AccessTools.FieldRef<Heightmap, MeshFilter> fFilter;
        static AccessTools.FieldRef<Heightmap, Mesh> fColMesh, fRenMesh; static AccessTools.FieldRef<Heightmap, bool> fDistant; static AccessTools.FieldRef<Heightmap, BiomeSector[]> fCorners;
        sealed class Snap
        {
            public float[] h = new float[0]; public int n, width; public float scale; public Vector3 pos; public bool col, ren;
            public BiomeSector c0, c1, c2, c3; public Color32 k0, k1, k2, k3; public WorldGenerator wg;
        }
        static readonly Dictionary<int, Snap> snaps = new Dictionary<int, Snap>();

        internal static void Patch(Harmony h)
        {
            var t = typeof(Heightmap);
            var rc = AccessTools.DeclaredMethod(t, "RebuildCollisionMesh"); var rr = AccessTools.DeclaredMethod(t, "RebuildRenderMesh");
            var clr = AccessTools.DeclaredMethod(t, "Clear"); var od = AccessTools.DeclaredMethod(t, "OnDestroy");
            if (rc == null || rr == null || clr == null || od == null) { Lean.L("terrain mesh reuse: Heightmap methods not found - off"); return; }
            foreach (var f in new[] { "m_heights", "m_collider", "m_meshFilter", "m_collisionMesh", "m_renderMesh", "m_isDistantLod", "m_cornerBiomes" }) if (AccessTools.DeclaredField(t, f) == null) { Lean.L("terrain mesh reuse: Heightmap." + f + " not found - off"); return; }
            // any other mod on either rebuild (any kind of patch) may depend on it running: stand down
            foreach (var m in new[] { rc, rr }) { var o = PG.Others(m); if (o.Count > 0) { Lean.L("terrain mesh reuse stands down: " + PG.Name(m) + " is also patched by " + string.Join(", ", o)); return; } }
            fHeights = AccessTools.FieldRefAccess<Heightmap, List<float>>("m_heights"); fCol = AccessTools.FieldRefAccess<Heightmap, MeshCollider>("m_collider");
            fFilter = AccessTools.FieldRefAccess<Heightmap, MeshFilter>("m_meshFilter"); fColMesh = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_collisionMesh");
            fRenMesh = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_renderMesh"); fDistant = AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");
            fCorners = AccessTools.FieldRefAccess<Heightmap, BiomeSector[]>("m_cornerBiomes");
            // below this mod's terrain collider bake prefix (Priority.Last), so that one sees the skip
            h.Patch(rc, prefix: new HarmonyMethod(typeof(MeshKeep), nameof(ColPre)) { priority = Priority.Low }, postfix: new HarmonyMethod(typeof(MeshKeep), nameof(ColPost)));
            h.Patch(rr, prefix: new HarmonyMethod(typeof(MeshKeep), nameof(RenPre)) { priority = Priority.Low }, postfix: new HarmonyMethod(typeof(MeshKeep), nameof(RenPost)));
            h.Patch(clr, postfix: new HarmonyMethod(typeof(MeshKeep), nameof(Forget)));
            h.Patch(od, prefix: new HarmonyMethod(typeof(MeshKeep), nameof(Drop)));
            ok = true; Lean.L("terrain mesh reuse patched");
        }
        static void Forget(Heightmap __instance) { if (snaps.TryGetValue(__instance.GetInstanceID(), out var s)) s.col = s.ren = false; }
        static void Drop(Heightmap __instance) { snaps.Remove(__instance.GetInstanceID()); }
        static Snap Get(Heightmap hm)
        {
            int id = hm.GetInstanceID();
            if (!snaps.TryGetValue(id, out var s)) { if (snaps.Count > 8192) snaps.Clear(); s = new Snap(); snaps[id] = s; }
            return s;
        }

        // __state: true = the rebuild runs (record the result afterwards)
        static bool ColPre(Heightmap __instance, bool __runOriginal, out bool __state)
        {
            __state = false;
            if (!__runOriginal) return false;
            if (!ok || !Lean.meshOn.Value) { Forget(__instance); return true; }
            var s = Get(__instance);
            if (s.col && Same(__instance, s)) { sColKept++; return false; }
            s.col = false; s.ren = false; __state = true; return true;
        }
        static bool Same(Heightmap hm, Snap s)
        {
            var mesh = fColMesh(hm); if (mesh == null) return false;
            var col = fCol(hm); if (col != null && col.sharedMesh != mesh) return false;
            if (hm.m_width != s.width || hm.m_scale != s.scale || hm.transform.position != s.pos) return false;
            var hs = fHeights(hm); int n = (hm.m_width + 1) * (hm.m_width + 1);
            if (hs == null || hs.Count < n || s.n != n || mesh.vertexCount != n) return false;
            var a = s.h; for (int i = 0; i < n; i++) if (hs[i] != a[i]) return false;
            return true;
        }
        static void ColPost(Heightmap __instance, bool __state)
        {
            if (!__state) return;
            var s = Get(__instance); var hs = fHeights(__instance); int n = (__instance.m_width + 1) * (__instance.m_width + 1);
            if (hs == null || hs.Count < n || fColMesh(__instance) == null) return;
            if (s.h.Length != n) s.h = new float[n];
            for (int i = 0; i < n; i++) s.h[i] = hs[i];
            s.n = n; s.width = __instance.m_width; s.scale = __instance.m_scale; s.pos = __instance.transform.position; s.col = true; s.ren = false; sColBuilt++;
        }
        static bool RenPre(Heightmap __instance, bool __runOriginal, out bool __state)
        {
            __state = false;
            if (!__runOriginal) return false;
            if (!ok || !Lean.meshOn.Value) { Forget(__instance); return true; }
            var s = Get(__instance);
            if (s.ren && s.col && RenSame(__instance, s)) { sRenKept++; return false; }
            s.ren = false; __state = true; return true;
        }
        static bool RenSame(Heightmap hm, Snap s)
        {
            var rm = fRenMesh(hm); var mf = fFilter(hm); if (rm == null || mf == null || mf.sharedMesh != rm || rm.vertexCount != s.n) return false;
            var c = fCorners(hm); if (c == null || c.Length < 4 || !ReferenceEquals(c[0], s.c0) || !ReferenceEquals(c[1], s.c1) || !ReferenceEquals(c[2], s.c2) || !ReferenceEquals(c[3], s.c3)) return false;
            // distant tiles colour every vertex from the world generator: always rebuilt
            if (fDistant(hm) || !ReferenceEquals(WorldGenerator.instance, s.wg)) return false;
            if (!Eq(Heightmap.GetBiomeColor(c[0]), s.k0) || !Eq(Heightmap.GetBiomeColor(c[1]), s.k1) || !Eq(Heightmap.GetBiomeColor(c[2]), s.k2) || !Eq(Heightmap.GetBiomeColor(c[3]), s.k3)) return false;
            return hm.m_width == s.width && hm.m_scale == s.scale;
        }
        static void RenPost(Heightmap __instance, bool __state)
        {
            if (!__state) return;
            var s = Get(__instance); var c = fCorners(__instance);
            if (!s.col || c == null || c.Length < 4 || fRenMesh(__instance) == null) return;
            sRenBuilt++;
            if (fDistant(__instance) || c[0] == null || c[1] == null || c[2] == null || c[3] == null) return;
            s.c0 = c[0]; s.c1 = c[1]; s.c2 = c[2]; s.c3 = c[3]; s.wg = WorldGenerator.instance;
            s.k0 = Heightmap.GetBiomeColor(c[0]); s.k1 = Heightmap.GetBiomeColor(c[1]); s.k2 = Heightmap.GetBiomeColor(c[2]); s.k3 = Heightmap.GetBiomeColor(c[3]); s.ren = true;
        }
        static bool Eq(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    }

    // ---------------------------------------------------------------------------------------------------- 22. Object Pass
    internal static class IdlePass
    {
        internal static int sSkipped, sRan;
        const float MaxIdle = 0.5f;
        const string Id = Lean.HarmonyId + ".idle", VpoGuid = "com.Skarif.ValheimPerformanceOverhaul_WATER", FgnGuid = "com.Fire.FiresGhettoNetworkMod";
        static Harmony h; static bool usable, patched; static string state = "not set up";
        static MethodInfo mPass;
        static AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> fInst; static AccessTools.FieldRef<ZNetScene, List<ZDO>> fNear, fFar;
        static AccessTools.FieldRef<ZDOMan, List<ZDO>[]> fBySector; static AccessTools.FieldRef<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>> fPortals;
        internal static int rev;
        static int lastRev = -1, lastCount; static long lastSig; static ZNetScene lastScene; static short lastX, lastY; static SimulationDistance lastDist; static bool settled; static float lastFull;
        internal static string State() => state;

        internal static void Prepare(Harmony main)
        {
            mPass = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDestroyObjects");
            var zs = typeof(ZNetScene); var zm = typeof(ZDOMan);
            if (mPass == null || AccessTools.DeclaredField(zs, "m_instances") == null || AccessTools.DeclaredField(zs, "m_tempCurrentObjects") == null || AccessTools.DeclaredField(zs, "m_tempCurrentDistantObjects") == null
                || AccessTools.DeclaredField(zm, "m_objectsBySector") == null || AccessTools.DeclaredField(zm, "m_portalObjects") == null) { Lean.L("object pass: game members not found - off"); state = "off (game changed)"; return; }
            fInst = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
            fNear = AccessTools.FieldRefAccess<ZNetScene, List<ZDO>>("m_tempCurrentObjects"); fFar = AccessTools.FieldRefAccess<ZNetScene, List<ZDO>>("m_tempCurrentDistantObjects");
            fBySector = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector"); fPortals = AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>>("m_portalObjects");
            // change signals: kept on even while the skip is off (cheap counters)
            var bump = new HarmonyMethod(typeof(IdlePass), nameof(Bump));
            foreach (var m in new MethodBase[] { AccessTools.DeclaredMethod(zs, "AddInstance"), AccessTools.DeclaredMethod(zs, "Destroy", new[] { typeof(GameObject) }), AccessTools.DeclaredMethod(typeof(ZNetView), "ResetZDO"),
                AccessTools.DeclaredMethod(typeof(ZDO), "SetDistant"), AccessTools.DeclaredMethod(typeof(ZDO), "SetType"), AccessTools.DeclaredMethod(typeof(ZDO), "SetPrefab") })
            { if (m == null) { Lean.L("object pass: a change hook was not found - off"); state = "off (game changed)"; return; } main.Patch(m, postfix: bump); }
            var des = AccessTools.DeclaredMethod(typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) });
            if (des == null) { Lean.L("object pass: ZDO.Deserialize not found - off"); state = "off (game changed)"; return; }
            main.Patch(des, prefix: new HarmonyMethod(typeof(IdlePass), nameof(DesPre)), postfix: new HarmonyMethod(typeof(IdlePass), nameof(DesPost)));
            h = new Harmony(Id); usable = true; state = "waiting";
            Lean.idleOn.SettingChanged += (s, e) => { try { Drive(); } catch { } };
        }
        static void Bump() { rev++; }
        struct Kind { public bool d; public ZDO.ObjectType t; public int p; }
        static void DesPre(ZDO __instance, out Kind __state) { __state = new Kind { d = __instance.Distant, t = __instance.Type, p = __instance.GetPrefab() }; }
        static void DesPost(ZDO __instance, Kind __state) { if (__instance.Distant != __state.d || __instance.Type != __state.t || __instance.GetPrefab() != __state.p) rev++; }

        // called about once a second: patch while wanted, unpatch when off or ceded (so VPO's own pass cache can take over)
        internal static void Drive()
        {
            if (!usable) return;
            bool ceded = Overlap.Yield(Feature.IdlePass);
            string why = !Lean.idleOn.Value ? "off" : ceded ? "ceded to VPO" : null;
            if (why == null)
            {
                var r = PG.Rivals(mPass, true, true); var bad = new List<string>();
                foreach (var x in r) if (x.IndexOf(VpoGuid, StringComparison.OrdinalIgnoreCase) < 0 && x.IndexOf(FgnGuid, StringComparison.OrdinalIgnoreCase) < 0) bad.Add(x);
                foreach (var nm in new[] { "CreateObjects", "RemoveObjects" })
                    foreach (var x in PG.Rivals(AccessTools.DeclaredMethod(typeof(ZNetScene), nm), true, true)) if (x.IndexOf(FgnGuid, StringComparison.OrdinalIgnoreCase) < 0 && !bad.Contains(x)) bad.Add(x);
                if (bad.Count > 0) why = "stands down (" + string.Join(", ", bad) + ")";
            }
            if (why == null && !patched)
            {
                h.Patch(mPass, prefix: new HarmonyMethod(typeof(IdlePass), nameof(Pre)) { priority = Priority.Last }, postfix: new HarmonyMethod(typeof(IdlePass), nameof(Post)) { priority = Priority.Last });
                patched = true; settled = false; Lean.L("object pass: idle-pass skip active"); state = "active";
            }
            else if (why != null && patched)
            {
                h.Unpatch(mPass, HarmonyPatchType.All, Id); patched = false; settled = false; Lean.L("object pass: idle-pass skip " + why); state = why;
            }
            else if (why != null) state = why;
        }

        static bool Pre(ZNetScene __instance, bool __runOriginal, out bool __state)
        {
            __state = false;
            if (!__runOriginal) return false;
            __state = true;
            if (!settled || !Lean.idleOn.Value || (object)__instance != (object)lastScene || rev != lastRev) return true;
            if (Time.realtimeSinceStartup - lastFull >= MaxIdle) return true;
            var zn = ZNet.instance; var zm = ZDOMan.instance; if (zn == null || zm == null || ZoneSystem.instance == null || Player.m_localPlayer == null) return true;
            var zone = ZoneSystem.GetZone(zn.GetReferencePosition()); if (zone.x != lastX || zone.y != lastY) return true;
            var dist = zn.GetSyncedSimulationDistance(); if (!dist.Equals(lastDist)) return true;
            var inst = fInst(__instance); if (inst == null || inst.Count != lastCount) return true;
            if (Holding() || Sig(zm, zone, dist) != lastSig) return true;
            __state = false; sSkipped++; return false;
        }
        static void Post(ZNetScene __instance, bool __state)
        {
            if (!__state) return;
            sRan++; settled = false;
            try
            {
                var zn = ZNet.instance; var zm = ZDOMan.instance; if (zn == null || zm == null || ZoneSystem.instance == null || Player.m_localPlayer == null) return;
                lastScene = __instance; lastFull = Time.realtimeSinceStartup; lastRev = rev;
                var zone = ZoneSystem.GetZone(zn.GetReferencePosition()); lastX = zone.x; lastY = zone.y; lastDist = zn.GetSyncedSimulationDistance();
                var inst = fInst(__instance); if (inst == null) return; lastCount = inst.Count;
                if (!ZoneSystem.instance.IsActiveAreaLoaded() || !AllCreated(fNear(__instance)) || !AllCreated(fFar(__instance)) || Holding()) return;
                lastSig = Sig(zm, zone, lastDist); settled = true;
            }
            catch (Exception e) { settled = false; Lean.L("object pass check threw (" + e.Message + "); the pass keeps running every time"); usable = false; if (patched) { h.Unpatch(mPass, HarmonyPatchType.All, Id); patched = false; } state = "off (error)"; }
        }
        static bool AllCreated(List<ZDO> l) { if (l == null) return false; for (int i = 0; i < l.Count; i++) { var z = l[i]; if (z == null || !z.Created) return false; } return true; }
        // something still waits for a later pass: removals held back by the removal cap / location keep-alive, or FGN's destroy throttle
        static bool Holding()
        {
            if (PerformanceFixesPlugin.SpawnBudgetLoaded && SbHolding()) return true;
            return Overlap.FgnDestroyLimit() > 0;
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool SbHolding() => global::SpawnBudget.Plugin.HoldingBack();
        // fingerprint of every sector the pass can look at: list sizes and their newest entry, and portal entries
        static long Sig(ZDOMan zm, Vector2s zone, SimulationDistance dist)
        {
            var by = fBySector(zm); var portals = fPortals(zm); if (by == null) return 0;
            int r = Math.Max(0, dist.TotalSimulationDistance); long sig = 17;
            for (int y = zone.y - r; y <= zone.y + r; y++)
                for (int x = zone.x - r; x <= zone.x + r; x++)
                {
                    var si = ZoneSystem.SectorToIndex(x, y); uint k = si.Sector;
                    List<ZDO> l = k < by.Length ? by[k] : null;
                    int n = l == null ? -1 : l.Count;
                    sig = sig * 1000003L + n;
                    if (n > 0) sig = sig * 31L + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l[n - 1]);
                    if (portals != null && portals.Count > 0 && portals.TryGetValue(si, out var pl) && pl != null) { sig = sig * 7L + pl.Count; if (pl.Count > 0) sig = sig * 31L + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pl[pl.Count - 1]); }
                }
            return sig;
        }
    }

    // ---------------------------------------------------------------------------------------------------- 23. Combat Housekeeping
    internal static class Combat
    {
        internal static int sZeroLook, sQuiet, sMistSnaps; internal static long sMistTests;
        static volatile bool projLive, quietLive, mistLive;
        internal static void PatchProjectile(Harmony h)
        {
            projLive = Lean.projOn.Value; Lean.projOn.SettingChanged += (s, e) => projLive = Lean.projOn.Value;
            var m = AccessTools.DeclaredMethod(typeof(Projectile), "FixedUpdate");
            if (Lean.StandDown(m, "projectile look")) return;
            h.Patch(m, transpiler: new HarmonyMethod(typeof(Combat), nameof(ProjT)));
        }
        static IEnumerable<CodeInstruction> ProjT(IEnumerable<CodeInstruction> ins)
        {
            var look = AccessTools.Method(typeof(Quaternion), "LookRotation", new[] { typeof(Vector3) });
            bool ok; return PG.ReplaceCalls(ins, ci => Lean.IsCall(ci, look), AccessTools.Method(typeof(Combat), nameof(Look)), 1, "projectile look", Lean.L, out ok);
        }
        // Unity answers identity for a zero direction, after logging a warning
        public static Quaternion Look(Vector3 v)
        {
            if (projLive && v.x == 0f && v.y == 0f && v.z == 0f) { sZeroLook++; return Quaternion.identity; }
            return Quaternion.LookRotation(v);
        }

        static readonly Dictionary<string, int> quietExpect = new Dictionary<string, int>();
        internal static void PatchQuiet(Harmony h)
        {
            quietLive = Lean.quietOn.Value; Lean.quietOn.SettingChanged += (s, e) => quietLive = Lean.quietOn.Value;
            Quiet(h, AccessTools.DeclaredMethod(typeof(Container), "RPC_RequestOpen"), 4);
            Quiet(h, AccessTools.DeclaredMethod(typeof(Container), "RPC_RequestStack"), 4);
            Quiet(h, AccessTools.DeclaredMethod(typeof(Container), "RPC_RequestTakeAll"), 4);
            Quiet(h, AccessTools.DeclaredMethod(typeof(ZSteamSocket), "SendQueuedPackages"), 1);
        }
        static void Quiet(Harmony h, MethodInfo m, int n)
        {
            if (Lean.StandDown(m, "quiet logs")) return;
            quietExpect[PG.Name(m)] = n; h.Patch(m, transpiler: new HarmonyMethod(typeof(Combat), nameof(QuietT)));
        }
        static IEnumerable<CodeInstruction> QuietT(IEnumerable<CodeInstruction> ins, MethodBase original)
        {
            var log = AccessTools.Method(typeof(ZLog), "Log", new[] { typeof(object) });
            int n; quietExpect.TryGetValue(PG.Name(original), out n);
            bool ok; return PG.ReplaceCalls(ins, ci => Lean.IsCall(ci, log), AccessTools.Method(typeof(Combat), nameof(QuietLog)), n, "quiet logs " + PG.Name(original), Lean.L, out ok);
        }
        public static void QuietLog(object o) { if (quietLive) { Interlocked.Increment(ref sQuiet); return; } ZLog.Log(o); }

        // ---- mist: one position read per mist volume per frame
        static AccessTools.FieldRef<List<Mister>> fMisters;
        static Mister[] mRef = new Mister[0]; static Vector3[] mPos = new Vector3[0]; static float[] mRad = new float[0], mHgt = new float[0];
        static int mCount, mFrame = -1, mVer, mSnapVer = -1; static bool mBad;
        internal static void PatchMist(Harmony h)
        {
            mistLive = Lean.mistOn.Value; Lean.mistOn.SettingChanged += (s, e) => mistLive = Lean.mistOn.Value;
            if (AccessTools.DeclaredField(typeof(Mister), "m_instances") == null) { Lean.L("mist once: Mister list not found - off"); return; }
            fMisters = AccessTools.StaticFieldRefAccess<List<Mister>>(AccessTools.DeclaredField(typeof(Mister), "m_instances"));
            var en = AccessTools.DeclaredMethod(typeof(Mister), "OnEnable"); var dis = AccessTools.DeclaredMethod(typeof(Mister), "OnDisable");
            var emit = AccessTools.DeclaredMethod(typeof(ParticleMist), "Emit"); var me = AccessTools.DeclaredMethod(typeof(ParticleMist), "MisterEmit");
            if (en == null || dis == null || emit == null || me == null) { Lean.L("mist once: methods not found - off"); return; }
            if (Lean.StandDown(emit, "mist once") || Lean.StandDown(me, "mist once")) return;
            var ver = new HarmonyMethod(typeof(Combat), nameof(MistChanged));
            h.Patch(en, postfix: ver); h.Patch(dis, postfix: ver);
            h.Patch(emit, transpiler: new HarmonyMethod(typeof(Combat), nameof(EmitT)));
            h.Patch(me, transpiler: new HarmonyMethod(typeof(Combat), nameof(MisterEmitT)));
        }
        static void MistChanged() { mVer++; }
        static IEnumerable<CodeInstruction> EmitT(IEnumerable<CodeInstruction> ins)
        {
            var inside = AccessTools.Method(typeof(Mister), "InsideMister", new[] { typeof(Vector3), typeof(float) });
            bool ok; return PG.ReplaceCalls(ins, ci => Lean.IsCall(ci, inside), AccessTools.Method(typeof(Combat), nameof(MistInside)), 2, "mist once ParticleMist.Emit", Lean.L, out ok);
        }
        static IEnumerable<CodeInstruction> MisterEmitT(IEnumerable<CodeInstruction> ins)
        {
            var comp = AccessTools.Method(typeof(Mister), "IsCompletelyInsideOtherMister", new[] { typeof(float) });
            var other = AccessTools.Method(typeof(Mister), "IsInsideOtherMister", new[] { typeof(Vector3), typeof(Mister) });
            bool ok1, ok2;
            var a = PG.ReplaceCalls(ins, ci => Lean.IsCall(ci, comp), AccessTools.Method(typeof(Combat), nameof(MistCompletelyInside)), 1, "mist once MisterEmit (volume)", Lean.L, out ok1);
            if (!ok1) return a;
            var b = PG.ReplaceCalls(a, ci => Lean.IsCall(ci, other), AccessTools.Method(typeof(Combat), nameof(MistInsideOther)), 1, "mist once MisterEmit (particles)", Lean.L, out ok2);
            if (ok2) Lean.L("mist once patched");
            return b;
        }
        // false = use the game's own code this time
        static bool Snap()
        {
            if (!mistLive || mBad || fMisters == null) return false;
            int f = Time.frameCount;
            if (f == mFrame && mSnapVer == mVer) return true;
            var l = fMisters(); if (l == null) return false;
            int n = l.Count;
            if (mRef.Length < n) { int c = Math.Max(n, mRef.Length * 2); mRef = new Mister[c]; mPos = new Vector3[c]; mRad = new float[c]; mHgt = new float[c]; }
            for (int i = 0; i < n; i++)
            {
                var m = l[i]; if (m == null) { mFrame = -1; return false; }
                mRef[i] = m; mPos[i] = m.transform.position; mRad[i] = m.m_radius; mHgt[i] = m.m_height;
            }
            for (int i = n; i < mCount; i++) mRef[i] = null;
            mCount = n; mFrame = f; mSnapVer = mVer; sMistSnaps++; return true;
        }
        public static bool MistInside(Vector3 p, float radius)
        {
            if (!Snap()) return Mister.InsideMister(p, radius);
            sMistTests++;
            for (int i = 0; i < mCount; i++) if (Vector3.Distance(mPos[i], p) < mRad[i] + radius && p.y - radius < mPos[i].y + mHgt[i]) return true;
            return false;
        }
        public static bool MistInsideOther(Vector3 p, Mister ignore)
        {
            if (!Snap()) return Mister.IsInsideOtherMister(p, ignore);
            sMistTests++;
            for (int i = 0; i < mCount; i++) if (!(mRef[i] == ignore) && Vector3.Distance(p, mPos[i]) < mRad[i] && p.y < mPos[i].y + mHgt[i]) return true;
            return false;
        }
        public static bool MistCompletelyInside(Mister self, float thickness)
        {
            if ((object)self == null || !Snap()) return self.IsCompletelyInsideOtherMister(thickness);
            sMistTests++;
            Vector3 pos = self.transform.position; float r = self.m_radius, ht = self.m_height;
            for (int i = 0; i < mCount; i++) if (!(mRef[i] == self) && Vector3.Distance(pos, mPos[i]) + r + thickness < mRad[i] && pos.y + ht < mPos[i].y + mHgt[i]) return true;
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------- removed settings
    internal static class Removed
    {
        // 2.3.7: entries of features that no longer exist, read back from an older .cfg (BepInEx keeps them as orphans and
        // writes them out again on every save). Dropped once on start; nothing else in the file changes.
        static readonly string[][] Gone = { new[] { "24. Support Lookups", null }, new[] { Overlap.Section, "Cede_SupportLookup" } };
        internal static int Forget(ConfigFile c)
        {
            int n = 0;
            try
            {
                var pi = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");
                var d = pi?.GetValue(c, null) as Dictionary<ConfigDefinition, string>;
                if (d == null) return 0;
                var drop = new List<ConfigDefinition>();
                foreach (var k in d.Keys) foreach (var g in Gone) if (k.Section == g[0] && (g[1] == null || k.Key == g[1])) { drop.Add(k); break; }
                foreach (var k in drop) { d.Remove(k); n++; }
                if (n > 0) c.Save();
            }
            catch { }
            return n;
        }
    }
}
