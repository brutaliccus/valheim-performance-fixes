using System; using System.Collections.Generic; using System.Diagnostics; using System.Threading;
using BepInEx.Configuration; using HarmonyLib; using UnityEngine; using UnityEngine.LowLevel;
namespace SpawnBudget {
// 2.2.0 "Terrain Collider Bake": a zone's ground collider is cooked on a background thread instead of the main thread.
//  Giving a MeshCollider a new mesh makes Unity cook the physics shape right there, on the main thread; for a fresh zone that
//  is one of the biggest single costs of the frame it loads in. Physics.BakeMesh can do that cooking on any thread and Unity
//  reuses the result when the mesh is then handed to the collider, as long as the same cooking options are used.
//  Only two kinds of rebuild are moved: a zone the client is loading (client zone spawn) and a delayed ground rebuild run from
//  the heightmap late update. Both only when the world is visible, the heightmap is farther than Heightmap ProtectRadius from
//  the player and not in or next to a zone with player builds, nobody else patches the rebuild, and the world owner is not
//  generating terrain. Everything else cooks the vanilla way.
//  The collider is given its mesh before anything can need it:
//   - before every physics step (MonoUpdaters.FixedUpdate prefix), before any ground height query (ZoneSystem ground/solid
//     height, FindFloor, GetGroundData), before a navmesh tile is built, before a rebuild/clear/destroy of the same heightmap,
//     and before Heightmap.ForceGenerateAll (ships, carts, snapping);
//   - zone loads: at the end of the same frame's LateUpdate at the latest;
//   - delayed rebuilds: at the start of the next frame (before physics and Update) unless FinishSameFrame is on.
//  The worker only cooks; it never touches a Unity object. All assignment happens on the main thread.
public partial class Plugin {
  static ConfigEntry<bool> BkOn, BkSame;
  sealed class BakeJob { public Heightmap hm; public MeshCollider col; public Mesh mesh; public int meshId; public MeshColliderCookingOptions opt; public Vector3 pos; public float half;
    public bool late; public volatile bool done; public volatile string err; public long t0; }
  static readonly List<BakeJob> bakes = new List<BakeJob>();               // main thread only
  static readonly List<BakeJob> bakeQ = new List<BakeJob>();                // guarded by bakeQ
  static Thread bakeThread; static readonly AutoResetEvent bakeWake = new AutoResetEvent(false); static readonly object bakeDoneLock = new object();
  static AccessTools.FieldRef<Heightmap, MeshCollider> hmCol; // set in BakeInit (keeps the static init free of game types)
  static System.Reflection.MethodBase bkRebuildM; static bool bkOk, bkLoop, bkRivalChecked; static int bkZoneCtx, bkLateCtx, lateDoneFrame = -1;
  static int sBkBaked, sBkWaited, sBkFailed, sBkStale; static double sBkWaitMs, sBkWaitMax;
  struct VpfColliderBakeAssign { }

  void BakeConfig() {
    BkOn = Config.Bind(SBS + "Terrain Collider Bake", "Enabled", true, "Client, world visible: cook a far-away zone's ground collider on a background thread instead of in the frame the zone loads. Never for ground within Heightmap ProtectRadius of you, never in or next to a zone with player builds, never while the world owner generates terrain. The collider is always in place before the next physics step and before any ground height check. Off = vanilla.");
    BkSame = Config.Bind(SBS + "Terrain Collider Bake", "FinishSameFrame", false, "On = a delayed ground rebuild's collider is put in place at the end of the same frame (waits for the worker if needed; smaller gain). Off = at the very start of the next frame, before physics and game logic run. Zone loads always finish in their own frame.");
  }
  static void BakeInit(Harmony h) {
    try {
      hmCol = AccessTools.FieldRefAccess<Heightmap, MeshCollider>("m_collider");
      var rb = AccessTools.DeclaredMethod(typeof(Heightmap), "RebuildCollisionMesh");
      if (rb == null) { Log("terrain collider bake: Heightmap.RebuildCollisionMesh not found, off"); return; }
      bkRebuildM = rb;
      var o = global::ValheimPerformanceFixes.PatchGuard.Others(rb);
      if (o.Count > 0) { Log("terrain collider bake stands down: Heightmap.RebuildCollisionMesh is also patched by " + string.Join(", ", o)); return; }
      h.Patch(rb, prefix: new HarmonyMethod(typeof(Plugin), nameof(BkRebuildPre)) { priority = Priority.Last }, postfix: new HarmonyMethod(typeof(Plugin), nameof(BkRebuildPost)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Plugin), nameof(BkRebuildFin)));
      var zs = AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone");
      h.Patch(zs, prefix: new HarmonyMethod(typeof(Plugin), nameof(BkZonePre)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(BkZoneFin)));
      h.Patch(AccessTools.DeclaredMethod(typeof(Heightmap), "CustomLateUpdate"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BkLatePre)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Plugin), nameof(BkLateFin)));
      var w = new HarmonyMethod(typeof(Plugin), nameof(BkWaitAllPre)) { priority = Priority.First };
      h.Patch(AccessTools.DeclaredMethod(typeof(Heightmap), "ForceGenerateAll"), prefix: w);
      h.Patch(AccessTools.DeclaredMethod(typeof(MonoUpdaters), "FixedUpdate"), prefix: w);
      var bt = AccessTools.DeclaredMethod(typeof(Pathfinding), "BuildTile"); if (bt != null) h.Patch(bt, prefix: w);
      var own = new HarmonyMethod(typeof(Plugin), nameof(BkWaitOwnPre)) { priority = Priority.First };
      h.Patch(AccessTools.DeclaredMethod(typeof(Heightmap), "Clear"), prefix: own);
      h.Patch(AccessTools.DeclaredMethod(typeof(Heightmap), "OnDestroy"), prefix: own);
      h.Patch(AccessTools.DeclaredMethod(typeof(MonoUpdaters), "LateUpdate"), postfix: new HarmonyMethod(typeof(Plugin), nameof(BkLateUpdatePost)) { priority = Priority.Last });
      int q = 0;
      foreach (var m in AccessTools.GetDeclaredMethods(typeof(ZoneSystem))) {
        if (m.IsStatic || m.IsAbstract) continue; if (m.Name != "GetGroundHeight" && m.Name != "GetSolidHeight" && m.Name != "FindFloor" && m.Name != "GetGroundData") continue;
        var ps = m.GetParameters(); if (ps.Length == 0 || ps[0].Name != "p") continue;
        var pre = ps[0].ParameterType == typeof(Vector3) ? nameof(BkQueryPre) : ps[0].ParameterType == typeof(Vector3).MakeByRefType() ? nameof(BkQueryRefPre) : null;
        if (pre == null) continue; h.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), pre) { priority = Priority.First }); q++;
      }
      bkLoop = InsertEarlyAssign();
      bakeThread = new Thread(BakeWorker) { IsBackground = true, Name = "vpf collider bake" }; bakeThread.Start();
      bkOk = true;
      Application.quitting += () => { try { FinishAllBakes(); } catch { } bkOk = false; }; // nothing left cooking while Unity shuts down
      Log("terrain collider bake patched (enabled=" + BkOn.Value + ", ground queries guarded=" + q + ", next-frame assign " + (bkLoop ? "hooked into EarlyUpdate" : "NOT available, finishing in the same frame") + ")");
    } catch (Exception e) { bkOk = false; Log("terrain collider bake patch FAILED, off: " + e.Message); }
  }
  // the next-frame assignment point: an extra step at the end of Unity's EarlyUpdate, before FixedUpdate and Update
  static bool InsertEarlyAssign() {
    try {
      var root = PlayerLoop.GetCurrentPlayerLoop(); if (root.subSystemList == null) return false;
      for (int i = 0; i < root.subSystemList.Length; i++) {
        if (root.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.EarlyUpdate)) continue;
        var eu = root.subSystemList[i]; var subs = new List<PlayerLoopSystem>(eu.subSystemList ?? new PlayerLoopSystem[0]);
        foreach (var s in subs) if (s.type == typeof(VpfColliderBakeAssign)) return true;
        subs.Add(new PlayerLoopSystem { type = typeof(VpfColliderBakeAssign), updateDelegate = BkEarly });
        eu.subSystemList = subs.ToArray(); root.subSystemList[i] = eu; PlayerLoop.SetPlayerLoop(root); return true;
      }
    } catch (Exception e) { Log("terrain collider bake: could not add the early-frame step: " + e.Message); }
    return false;
  }
  static void BakeWorker() {
    while (true) {
      BakeJob j = null;
      lock (bakeQ) { if (bakeQ.Count > 0) { j = bakeQ[0]; bakeQ.RemoveAt(0); } }
      if (j == null) { bakeWake.WaitOne(); continue; }
      try { Physics.BakeMesh(j.meshId, false, j.opt); } catch (Exception e) { j.err = e.GetType().Name + ": " + e.Message; }
      lock (bakeDoneLock) { j.done = true; Monitor.PulseAll(bakeDoneLock); }
    }
  }
  static bool BkRival() {
    if (bkRivalChecked) return !bkOk; bkRivalChecked = true;
    try { var o = global::ValheimPerformanceFixes.PatchGuard.Others(bkRebuildM); if (o.Count > 0) { bkOk = false; FinishAllBakes(); Log("terrain collider bake stands down: Heightmap.RebuildCollisionMesh is also patched by " + string.Join(", ", o)); } } catch { }
    return !bkOk;
  }
  static void BkZonePre(ZoneSystem.SpawnMode mode, out bool __state) { __state = mode == ZoneSystem.SpawnMode.Client; if (__state) bkZoneCtx++; }
  static Exception BkZoneFin(Exception __exception, bool __state) { if (__state && bkZoneCtx > 0) bkZoneCtx--; return __exception; }
  static void BkLatePre() { bkLateCtx++; } static Exception BkLateFin(Exception __exception) { if (bkLateCtx > 0) bkLateCtx--; return __exception; }

  // __state = the collider hidden for this call (null = vanilla)
  static void BkRebuildPre(Heightmap __instance, out MeshCollider __state, bool __runOriginal) {
    __state = null;
    if (!__runOriginal) return; // 2.3.6: the rebuild is skipped (terrain mesh reuse): the mesh is untouched, a pending bake lands as usual
    if (bakes.Count > 0) FinishBakesOf(__instance); // the worker may still be reading the mesh this call is about to change
    if (!bkOk || !BkOn.Value) return;
    try {
      if ((bkZoneCtx == 0 && bkLateCtx == 0) || inForce || genDepth > 0 || Time.inFixedTimeStep || lateDoneFrame == Time.frameCount) return;
      if (__instance.IsDistantLod || !InGame() || BkRival()) return;
      var c = hmCol(__instance); if (c == null) return;
      var pl = Player.m_localPlayer; if (pl == null) return;
      var pp = pl.transform.position; var hp = __instance.transform.position; float dx = pp.x - hp.x, dz = pp.z - hp.z;
      float r = HmProtect.Value; if (dx * dx + dz * dz <= r * r) return;
      if (PbNearPos(hp)) return;
      hmCol(__instance) = null; __state = c; // vanilla's "if (m_collider)" then skips the synchronous cook
    } catch { }
  }
  static void BkRebuildPost(Heightmap __instance, MeshCollider __state) {
    if (__state == null) return;
    hmCol(__instance) = __state;
    try {
      var mesh = collMesh(__instance); if (mesh == null) return;
      var j = new BakeJob { hm = __instance, col = __state, mesh = mesh, meshId = mesh.GetInstanceID(), opt = __state.cookingOptions, pos = __instance.transform.position,
        half = __instance.m_width * __instance.m_scale * 0.5f + 2f, late = bkLateCtx > 0 && bkZoneCtx == 0, t0 = Stopwatch.GetTimestamp() };
      bakes.Add(j); lock (bakeQ) bakeQ.Add(j); bakeWake.Set();
    } catch (Exception e) { try { __state.sharedMesh = collMesh(__instance); } catch { } sBkFailed++; Log("terrain collider bake: queueing failed, cooked the vanilla way: " + e.Message); }
  }
  static Exception BkRebuildFin(Heightmap __instance, MeshCollider __state, Exception __exception) {
    if (__exception != null && __state != null) { try { if (hmCol(__instance) == null) hmCol(__instance) = __state; } catch { } }
    return __exception;
  }
  static void Await(BakeJob j) {
    if (j.done) return;
    long t = Stopwatch.GetTimestamp();
    lock (bakeDoneLock) { while (!j.done) Monitor.Wait(bakeDoneLock, 50); }
    double ms = (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency; sBkWaited++; sBkWaitMs += ms; if (ms > sBkWaitMax) sBkWaitMax = ms;
  }
  static void Land(BakeJob j) {
    Await(j);
    try {
      if (j.err != null) { sBkFailed++; Log("terrain collider bake: background cook failed (" + j.err + "), the collider cooks the vanilla way"); }
      if (j.hm == null || j.col == null || j.mesh == null || collMesh(j.hm) != j.mesh) { sBkStale++; return; }
      j.col.sharedMesh = j.mesh; sBkBaked++;
    } catch (Exception e) { sBkFailed++; Log("terrain collider bake: assign failed: " + e.Message); }
  }
  static void FinishAllBakes() { if (bakes.Count == 0) return; var l = bakes.ToArray(); bakes.Clear(); foreach (var j in l) Land(j); }
  static void FinishBakesOf(Heightmap hm) {
    for (int i = bakes.Count - 1; i >= 0; i--) { var j = bakes[i]; if (!ReferenceEquals(j.hm, hm)) continue; bakes.RemoveAt(i); Land(j); }
  }
  static void FinishBakesAt(Vector3 p) {
    for (int i = bakes.Count - 1; i >= 0; i--) { var j = bakes[i]; if (Mathf.Abs(p.x - j.pos.x) > j.half || Mathf.Abs(p.z - j.pos.z) > j.half) continue; bakes.RemoveAt(i); Land(j); }
  }
  static void BkWaitAllPre() { if (bakes.Count > 0) FinishAllBakes(); }
  static void BkWaitOwnPre(Heightmap __instance) { if (bakes.Count > 0) FinishBakesOf(__instance); }
  static void BkQueryPre(Vector3 p) { if (bakes.Count > 0) FinishBakesAt(p); }
  static void BkQueryRefPre(ref Vector3 p) { if (bakes.Count > 0) FinishBakesAt(p); }
  // end of the frame's LateUpdate: zone loads land now; delayed rebuilds too when FinishSameFrame (or no early-frame step)
  static void BkLateUpdatePost() {
    lateDoneFrame = Time.frameCount;
    if (bakes.Count == 0) return;
    bool all = !bkLoop || BkSame.Value || !bkOk;
    for (int i = bakes.Count - 1; i >= 0; i--) { var j = bakes[i]; if (j.late && !all) continue; bakes.RemoveAt(i); Land(j); }
  }
  static void BkEarly() { try { if (bakes.Count > 0) FinishAllBakes(); } catch { } }
  static string BakeStats() {
    return "colliderBake " + (!bkOk ? "OFF/stood down" : BkOn.Value ? "on" : "off") + " baked=" + sBkBaked + " waited=" + sBkWaited + " waitMs=" + sBkWaitMs.ToString("0.0") + " maxWait=" + sBkWaitMax.ToString("0.0") + "ms stale=" + sBkStale + " failed=" + sBkFailed;
  }
  static void BakeStatsReset() { sBkBaked = sBkWaited = sBkFailed = sBkStale = 0; sBkWaitMs = sBkWaitMax = 0; }
}
}
