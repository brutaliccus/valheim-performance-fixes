using System; using System.Collections.Generic; using BepInEx.Configuration; using HarmonyLib; using UnityEngine;
namespace SpawnBudget {
// 2.1.2 "Smarter location copies" (client; hidden copies of remembered world locations only, never anything players built).
//  - GrowCopiesWithMemory: the copy limits (MaxPrebuiltCopies / MaxReserveCopies) grow with the memory budget, up to
//    GrowMaxCopies. Every copy still has to fit the memory budget and the low-RAM check, so this only lets a large budget be used.
//  - DropFarCopies: a ready hidden copy is let go when you are more than DropCopyBeyondMeters from every known spot of that
//    location (the world's location list where this PC has it - host / single player - plus every spot this session placed one),
//    so its slot and memory go to one you are near. A copy younger than FarCopyGraceSec is kept. A location with no known spot is
//    never dropped for distance. Behind a loading screen, copies are not made for locations whose every known spot is that far.
//  - PreferSessionRepeats: a measured location that was placed SessionRepeatMin times or more this session ranks above the ones
//    that were placed fewer times (worst first inside each group), for copy slots and for which copy is let go first.
public partial class Plugin {
  static ConfigEntry<bool> GrowOn, FarOn, FarSkipPrebuild, RepeatOn; static ConfigEntry<int> GrowMax, RepeatMin; static ConfigEntry<float> FarM, FarGrace;
  void CopiesConfig() {
    string s = SBS + "Location Assets";
    GrowOn = Config.Bind(s, "GrowCopiesWithMemory", true, "Let MaxPrebuiltCopies and MaxReserveCopies grow with the memory budget (about one copy per average copy size that fits the budget), up to GrowMaxCopies. Every copy still has to fit the memory budget and the low-RAM check. Off = the two settings are hard limits.");
    GrowMax = Config.Bind(s, "GrowMaxCopies", 16, new ConfigDescription("Most copies GrowCopiesWithMemory may grow to (the remembered list holds at most MaxRemembered locations, one copy each).", new AcceptableValueRange<int>(8, 32)));
    FarOn = Config.Bind(s, "DropFarCopies", true, "Let a ready hidden location copy go when you are more than DropCopyBeyondMeters from every known spot of that location, so its slot and memory go to one you are near. Locations with no known spot are kept. Off = copies stay until used, the memory budget or low RAM.");
    FarM = Config.Bind(s, "DropCopyBeyondMeters", 500f, new ConfigDescription("Distance for DropFarCopies (meters, measured flat).", new AcceptableValueRange<float>(150f, 5000f)));
    FarGrace = Config.Bind(s, "FarCopyGraceSec", 60f, new ConfigDescription("A copy is never dropped for distance in its first this many seconds.", new AcceptableValueRange<float>(0f, 600f)));
    FarSkipPrebuild = Config.Bind(s, "SkipFarCopiesBehindScreens", true, "Needs DropFarCopies. Behind a loading screen, make no copy of a location whose every known spot is more than DropCopyBeyondMeters from where you arrive (it would only be dropped again).");
    RepeatOn = Config.Bind(s, "PreferSessionRepeats", true, "Measured locations placed SessionRepeatMin times or more this session rank first for copy slots and are let go last. Off = worst hitch first only.");
    RepeatMin = Config.Bind(s, "SessionRepeatMin", 2, new ConfigDescription("Placements this session that make a location count as repeating.", new AcceptableValueRange<int>(2, 10)));
  }
  // ---- placements this session ----
  static readonly Dictionary<int, List<Vector3>> seenAt = new Dictionary<int, List<Vector3>>();
  static readonly Dictionary<string, int> sessionPlaced = new Dictionary<string, int>();
  static ZoneSystem seenZs; static int sFarDrops, sFarSkips; static readonly HashSet<int> farSkipped = new HashSet<int>();
  static void SeenReset(ZoneSystem zs) { seenZs = zs; seenAt.Clear(); sessionPlaced.Clear(); worldAt.Clear(); worldAtZs = null; farSince.Clear(); farSkipped.Clear(); }
  static void NoteSeen(ZoneSystem.ZoneLocation location, Vector3 pos, ZoneSystem.SpawnMode mode) {
    if (location == null || mode == ZoneSystem.SpawnMode.Ghost) return;
    var zs = ZoneSystem.instance; if (zs != seenZs) SeenReset(zs);
    string n = location.m_prefabName; if (!string.IsNullOrEmpty(n)) { sessionPlaced.TryGetValue(n, out var c); sessionPlaced[n] = c + 1; }
    int h = location.Hash; if (h == 0) return;
    if (!seenAt.TryGetValue(h, out var l)) { l = new List<Vector3>(); seenAt[h] = l; }
    for (int i = 0; i < l.Count; i++) if (Flat(l[i], pos) < 20f) return;
    if (l.Count >= 64) l.RemoveAt(0); l.Add(pos);
  }
  static int SessionPlaced(string n) { return n != null && sessionPlaced.TryGetValue(n, out var c) ? c : 0; }
  static bool Repeats(string n) { return RepeatOn != null && RepeatOn.Value && SessionPlaced(n) >= (RepeatMin != null ? RepeatMin.Value : 2); }
  static float Flat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }
  // ---- known spots: the world's list (host / single player) plus this session's placements ----
  static readonly Dictionary<int, List<Vector3>> worldAt = new Dictionary<int, List<Vector3>>(); static ZoneSystem worldAtZs; static float worldAtT = -999f; static int worldAtCount = -1;
  static void WorldAtRefresh(ZoneSystem zs) {
    int cnt = zs.m_locationInstances != null ? zs.m_locationInstances.Count : 0;
    if (zs == worldAtZs && cnt == worldAtCount && Time.unscaledTime - worldAtT < 60f) return;
    worldAtZs = zs; worldAtCount = cnt; worldAtT = Time.unscaledTime; worldAt.Clear();
    if (cnt == 0) return; // a client of a dedicated server has no list: only this session's placements are known
    foreach (var li in zs.m_locationInstances.Values) {
      if (li.m_location == null) continue; int h = li.m_location.Hash; if (h == 0) continue;
      if (!worldAt.TryGetValue(h, out var l)) { l = new List<Vector3>(); worldAt[h] = l; }
      l.Add(li.m_position);
    }
  }
  // false = no known spot of this location
  static bool NearestKnown(int h, Vector3 p, out float d) {
    d = float.MaxValue; bool any = false; var zs = ZoneSystem.instance; if (zs == null || h == 0) return false;
    if (zs != seenZs) SeenReset(zs);
    try { WorldAtRefresh(zs); } catch { }
    if (worldAt.TryGetValue(h, out var a)) foreach (var v in a) { any = true; float x = Flat(v, p); if (x < d) d = x; }
    if (seenAt.TryGetValue(h, out var b)) foreach (var v in b) { any = true; float x = Flat(v, p); if (x < d) d = x; }
    return any;
  }
  static bool RefPos(out Vector3 p) {
    p = Vector3.zero; var zn = ZNet.instance; if (zn == null) return false;
    try { var pl = Player.m_localPlayer; p = pl != null && !Loading() ? pl.transform.position : zn.GetReferencePosition(); return true; } catch { return false; }
  }
  static float FarV() { return FarM != null ? FarM.Value : 500f; }
  // behind a loading screen: no copy for a location whose every known spot is far from where you arrive
  static bool FarSkip(int h, out string why) {
    why = null;
    if (FarOn == null || !FarOn.Value || FarSkipPrebuild == null || !FarSkipPrebuild.Value) return false;
    if (!RefPos(out var p) || !NearestKnown(h, p, out var d) || d <= FarV()) return false;
    why = "far: nearest known spot " + d.ToString("0") + " m (DropCopyBeyondMeters " + FarV().ToString("0") + ")"; return true;
  }
  static readonly Dictionary<int, float> farSince = new Dictionary<int, float>(); static float farNext; static readonly List<int> farTmp = new List<int>();
  static void FarTick() {
    if (FarOn == null || !FarOn.Value || Time.unscaledTime < farNext) return;
    farNext = Time.unscaledTime + 3f;
    if (preReady.Count == 0) { farSince.Clear(); return; }
    farTmp.Clear(); foreach (var k in farSince.Keys) if (!preReady.ContainsKey(k)) farTmp.Add(k); foreach (var k in farTmp) farSince.Remove(k);
    float now = Time.unscaledTime; foreach (var k in preReady.Keys) if (!farSince.ContainsKey(k)) farSince[k] = now;
    if (Loading() || !Settled() || !RefPos(out var p)) return;
    float lim = FarV(), grace = FarGrace != null ? FarGrace.Value : 60f;
    farTmp.Clear(); farTmp.AddRange(preReady.Keys);
    foreach (var h in farTmp) {
      if (preUrgent.Contains(h) || preOps.ContainsKey(h)) continue;
      if (farSince.TryGetValue(h, out var t0) && now - t0 < grace) continue;
      if (!NearestKnown(h, p, out var d) || d <= lim) continue;
      sFarDrops++; ResDrop(h, "you are " + d.ToString("0") + " m from its nearest known spot (DropCopyBeyondMeters " + lim.ToString("0") + "); the slot is free for a nearer one");
      return; // one per check
    }
  }
  // ---- copy limits that grow with the memory budget ----
  static int growCache = -1; static float growAt = -999f; static bool growLogged;
  static int GrowMaxV() { return GrowMax != null ? Mathf.Clamp(GrowMax.Value, 8, 32) : 16; }
  static int GrowCopies() {
    if (GrowOn == null || !GrowOn.Value) return 0;
    if (growCache >= 0 && Time.unscaledTime - growAt < 2f && Time.unscaledTime >= growAt) return growCache;
    growAt = Time.unscaledTime;
    if (growCache < 0) growCache = 0; // ResBudgetMB logs ResMaxV once, which asks here again
    float b = ResBudgetMB(); if (b <= 0f || (freeMBLast >= 0 && freeMBLast < FreeFloorMB())) return growCache = 0;
    float sum = 0f; int n = 0; foreach (var k in preReady.Keys) { sum += CopyMB(k, null); n++; }
    float avg = n > 0 ? Math.Max(24f, sum / n) : 48f;
    int g = Mathf.Clamp((int)(b / avg), 0, GrowMaxV());
    if (!growLogged && g > 0) { growLogged = true; Log("copy limits grow with memory: budget " + b.ToString("0") + " MB / about " + avg.ToString("0") + " MB per copy -> up to " + g + " copies (GrowMaxCopies " + GrowMaxV() + "; every copy still has to fit the budget)"); }
    return growCache = g;
  }
  static string Stats212() {
    return "copies cap prebuilt=" + PrebuildCap() + " reserve=" + ResMaxV() + (GrowOn != null && GrowOn.Value ? " (grow " + GrowCopies() + ")" : "") + " | far drops=" + sFarDrops + " skipsBehindScreens=" + sFarSkips + " (" + FarV().ToString("0") + " m)"
      + " | repeating this session=" + RepeatCount() + " | knownSpots session=" + seenAt.Count + " world=" + worldAt.Count;
  }
  static int RepeatCount() { int n = 0; int m = RepeatMin != null ? RepeatMin.Value : 2; foreach (var v in sessionPlaced.Values) if (v >= m) n++; return n; }
}
}
