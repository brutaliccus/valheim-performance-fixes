using System; using System.Collections.Generic; using System.Diagnostics; using BepInEx.Configuration; using HarmonyLib; using UnityEngine;
namespace SpawnBudget {
// 2.1.2 "Creature drop queue" (client and single player; never on a dedicated server).
// A creature's loot is dropped by CharacterDrop.DropItems (from CharacterDrop.OnDeath, or Ragdoll.SpawnLoot when the body
// fades). In a big fight many of those land in one frame and every item is a full networked object. Here each DropItems call
// is kept whole (same list, same place, same spread, same cheat flag) and the calls are spread over frames: in combat only a
// few items per frame, out of combat more. Nothing is rolled again and nothing is split: the call runs later exactly as it
// would have, through a copy of DropItems taken with the other mods' IL changes on it (Drop That's per-item changes and
// stacking). DropNSpawn's DropItems prefix has already run on the original call before the call is held. Only the PC that
// owns the dying creature runs DropItems, so only that PC holds anything.
// Never lost: whatever is held is dropped at once on save, logout, shutdown, teleport, when its spot's zone is no longer
// loaded, after MaxDelaySec, or when the toggle is switched off. Stands down (game behavior) when another mod has a postfix
// on DropItems (it would run before the items exist) or the copy cannot be made.
public partial class Plugin {
  static ConfigEntry<bool> DqOn; static ConfigEntry<int> DqItemsCombat, DqItems; static ConfigEntry<float> DqMsCombat, DqMs, DqMaxDelay;
  void DropQueueConfig() {
    string s = SBS + "Drop Queue";
    DqOn = Config.Bind(s, "Enabled", true, "Spread creature loot over several frames instead of all in the frame the creatures die (each creature's drops stay together; a big fight's loot comes out over a moment). Items are never lost: held drops are dropped at once on save, logout, shutdown, teleport, zone unload and after MaxDelaySec. Works with Drop That and DropNSpawn. Off = the game's behavior.");
    DqItemsCombat = Config.Bind(s, "ItemsPerFrameInCombat", 2, new ConfigDescription("In combat: items dropped per frame (a creature whose drops are more than this still drops in one go, one creature per frame).", new AcceptableValueRange<int>(1, 50)));
    DqItems = Config.Bind(s, "ItemsPerFrame", 8, new ConfigDescription("Out of combat: items dropped per frame.", new AcceptableValueRange<int>(1, 100)));
    DqMsCombat = Config.Bind(s, "BudgetMsInCombat", 1.5f, new ConfigDescription("In combat: time per frame for held drops (ms). At least one creature's drops always go per frame.", new AcceptableValueRange<float>(0.25f, 10f)));
    DqMs = Config.Bind(s, "BudgetMs", 3f, new ConfigDescription("Out of combat: time per frame for held drops (ms).", new AcceptableValueRange<float>(0.25f, 10f)));
    DqMaxDelay = Config.Bind(s, "MaxDelaySec", 2f, new ConfigDescription("No drop is held longer than this (seconds); older ones are all dropped.", new AcceptableValueRange<float>(0.2f, 10f)));
  }
  sealed class DqEntry { public List<KeyValuePair<GameObject, int>> drops; public Vector3 pos; public float area, at; public bool cheated; public int items; }
  static readonly List<DqEntry> dq = new List<DqEntry>();
  delegate void DropItemsFn(List<KeyValuePair<GameObject, int>> drops, Vector3 centerPos, float dropArea, bool cheated);
  static DropItemsFn dqRun; static bool dqTried, dqReplaying; static string dqOff; static System.Reflection.MethodBase dqTarget;
  static int dqFrame = -1, dqFrameItems, sDqHeld, sDqRan, sDqDirect, sDqFlushed, sDqMaxLen; static float dqWorstWait; static double dqWorstMs;
  static void DropQueuePatch(Harmony h) {
    try {
      dqTarget = AccessTools.DeclaredMethod(typeof(CharacterDrop), "DropItems");
      if (dqTarget == null) { dqOff = "DropItems not found"; Log("drop queue: CharacterDrop.DropItems not found, off"); return; }
      h.Patch(dqTarget, prefix: new HarmonyMethod(typeof(Plugin), nameof(DqPre)) { priority = Priority.Last, after = new[] { "sighsorry.DropNSpawn", "asharppen.valheim.drop_that" } });
      var lo = AccessTools.DeclaredMethod(typeof(Game), "Logout"); if (lo != null) h.Patch(lo, prefix: new HarmonyMethod(typeof(Plugin), nameof(DqLogoutPre)) { priority = Priority.First });
      var tp = AccessTools.DeclaredMethod(typeof(Player), "TeleportTo"); if (tp != null) h.Patch(tp, prefix: new HarmonyMethod(typeof(Plugin), nameof(DqTeleportPre)) { priority = Priority.First });
      Log("drop queue patch ok (enabled=" + DqOn.Value + ", combat " + DqItemsCombat.Value + " items / " + DqMsCombat.Value + " ms, else " + DqItems.Value + " items / " + DqMs.Value + " ms, max hold " + DqMaxDelay.Value + " s)");
    } catch (Exception e) { dqOff = "patch failed"; Log("drop queue patch failed, game behavior: " + e.Message); }
  }
  static void DropItemsStub(List<KeyValuePair<GameObject, int>> drops, Vector3 centerPos, float dropArea, bool cheated) { throw new NotImplementedException("reverse patch stub"); }
  // made the first time it is needed in the world, after every mod has patched DropItems
  static bool DqReady() {
    if (dqRun != null) return true; if (dqTried) return false; dqTried = true;
    try {
      var info = Harmony.GetPatchInfo(dqTarget);
      if (info != null) foreach (var p in info.Postfixes) if (!p.owner.StartsWith("vpf.", StringComparison.Ordinal)) { dqOff = "postfix by " + p.owner; Log("drop queue off: CharacterDrop.DropItems has a postfix by " + p.owner + " (it would run before the held items exist)"); return false; }
      var stub = AccessTools.DeclaredMethod(typeof(Plugin), nameof(DropItemsStub));
      var mi = Harmony.ReversePatch(dqTarget, new HarmonyMethod(stub) { reversePatchType = HarmonyReversePatchType.Snapshot });
      if (mi == null) { dqOff = "copy failed"; Log("drop queue off: copy of DropItems could not be made"); return false; }
      dqRun = (DropItemsFn)Delegate.CreateDelegate(typeof(DropItemsFn), (System.Reflection.MethodInfo)mi);
      Log("drop queue ready (DropItems copy with IL changes by: " + (info == null ? "-" : string.Join(",", System.Linq.Enumerable.Select(info.Transpilers, x => x.owner))) + ")");
      return true;
    } catch (Exception e) { dqOff = "copy failed"; Log("drop queue off: copy of DropItems failed: " + e.Message); return false; }
  }
  static bool DqWorld() {
    try { var zn = ZNet.instance; return zn != null && !zn.IsDedicated() && ZNetScene.instance != null && ZoneSystem.instance != null && Player.m_localPlayer != null; } catch { return false; }
  }
  static int DqCount(List<KeyValuePair<GameObject, int>> d) { int n = 0; for (int i = 0; i < d.Count; i++) if (d[i].Value > 0) n += d[i].Value; return n; }
  static int DqCap() { return SpawnBudgetPressure.Combat ? DqItemsCombat.Value : DqItems.Value; }
  static bool DqPre(List<KeyValuePair<GameObject, int>> drops, Vector3 centerPos, float dropArea, bool cheated, bool __runOriginal) {
    if (!__runOriginal || dqReplaying) return true;
    try {
      if (DqOn == null || !DqOn.Value || dqOff != null || drops == null || drops.Count == 0 || flushing) return true;
      if (!DqWorld()) return true;
      int n = DqCount(drops); if (n <= 0) return true;
      int f = Time.frameCount; if (f != dqFrame) { dqFrame = f; dqFrameItems = 0; }
      if (dq.Count == 0 && (dqFrameItems == 0 || dqFrameItems + n <= DqCap())) { dqFrameItems += n; sDqDirect++; return true; }
      if (!DqReady()) return true;
      dq.Add(new DqEntry { drops = drops, pos = centerPos, area = dropArea, cheated = cheated, at = Time.unscaledTime, items = n });
      sDqHeld++; if (dq.Count > sDqMaxLen) sDqMaxLen = dq.Count;
      return false;
    } catch { return true; }
  }
  static void DqOne(DqEntry e) {
    dqReplaying = true;
    try { dqRun(e.drops, e.pos, e.area, e.cheated); } catch (Exception x) { Log("drop queue: held drop threw: " + x.Message); }
    finally { dqReplaying = false; }
    float w = Time.unscaledTime - e.at; if (w > dqWorstWait) dqWorstWait = w;
  }
  static void DropQueueTick() {
    if (dq.Count == 0) return;
    if (DqOn == null || !DqOn.Value || dqRun == null) { DqFlush("toggle off"); return; }
    if (ZNetScene.instance == null || ZoneSystem.instance == null) { DqFlush("world closing"); return; }
    int f = Time.frameCount; if (f != dqFrame) { dqFrame = f; dqFrameItems = 0; }
    float now = Time.unscaledTime, maxD = DqMaxDelay.Value; var zs = ZoneSystem.instance;
    // zone gone or held too long: drop now, wherever it is in the queue
    for (int i = dq.Count - 1; i >= 0; i--) { var e = dq[i]; bool loaded = true; try { loaded = zs.IsZoneLoaded(e.pos); } catch { }
      if (!loaded || now - e.at >= maxD) { dq.RemoveAt(i); DqOne(e); sDqRan++; dqFrameItems += e.items; } }
    if (dq.Count == 0) return;
    int cap = DqCap(); double ms = SpawnBudgetPressure.Combat ? DqMsCombat.Value : DqMs.Value;
    if (dqFrameItems >= cap) return; // this frame's items already went (creatures dropping directly)
    var sw = Stopwatch.StartNew(); int done = 0;
    while (dq.Count > 0) { var e = dq[0];
      if (done > 0 && (dqFrameItems + e.items > cap || sw.Elapsed.TotalMilliseconds >= ms)) break;
      dq.RemoveAt(0); DqOne(e); sDqRan++; done++; dqFrameItems += e.items; }
    double t = sw.Elapsed.TotalMilliseconds; if (t > dqWorstMs) dqWorstMs = t;
  }
  static void DqFlush(string why) {
    if (dq.Count == 0) return;
    int n = dq.Count, items = 0; var sw = Stopwatch.StartNew();
    var all = dq.ToArray(); dq.Clear();
    foreach (var e in all) { items += e.items; if (dqRun != null) DqOne(e); }
    sDqFlushed += n;
    Log("drop queue flush (" + why + "): " + n + " creature drop(s), " + items + " item(s), " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
  }
  static void DqLogoutPre() { try { DqFlush("logout"); } catch { } }
  static void DqTeleportPre() { try { DqFlush("teleport"); } catch { } }
  static string DropQueueStats() {
    return "drop queue " + (DqOn != null && DqOn.Value ? (dqOff ?? "on") : "off") + ": direct " + sDqDirect + ", held " + sDqHeld + ", ran " + sDqRan + ", flushed " + sDqFlushed + ", waiting " + dq.Count + ", longest " + sDqMaxLen + ", worst wait " + dqWorstWait.ToString("0.00") + " s, worst frame " + dqWorstMs.ToString("0.0") + " ms";
  }
}
}
