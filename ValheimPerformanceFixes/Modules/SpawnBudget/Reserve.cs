using System; using System.Collections.Generic; using System.Diagnostics; using System.IO; using System.Runtime.InteropServices; using System.Text;
using BepInEx; using BepInEx.Configuration; using HarmonyLib; using UnityEngine;
namespace SpawnBudget {
// 2.3.8 "Big locations, reserve copies and a memory budget" (client; world locations only, never anything players built).
//  - BigLocationMs: every normal placement of a world location is timed. One that takes BigLocationMs or longer (or whose asset
//    load froze the game that long) is put on the remembered list (HitchFix.slowlocations.txt), so behind every later loading
//    screen, in this and every later session, its asset is loaded early and a hidden copy is made ready for it. A listed one that
//    is placed the normal way under BigLocationMs DropAfterFastPlacements times in a row is taken off again and remembered as
//    "not big" (a "!name" line). A location placed from a hidden copy is not timed, so it stays on the list.
//  - RefillCopyAfterUse: when a hidden copy is used, a new one of that location is started on the next calm frames, so it is
//    ready again for the next one (instead of being gone until the next loading screen).
//  - MaxReserveCopies: most hidden copies (ready or being made) at a time, of every kind.
//  - Memory budget: hidden copies may use at most MemoryBudgetMode=Auto: about AutoBudgetPercent of this PC's RAM, less on small
//    PCs (8 GB or less: none, 16 GB: about 1.5 GB, 32 GB or more: the full percentage), or Manual: ManualBudgetMB. The size of a
//    copy is an estimate (its objects plus the textures and meshes it keeps loaded). Every 5 s the free RAM is checked; under
//    1.5 GB (or 10% of RAM, the larger) the least valuable copy (then the least valuable kept asset) is let go, one per check.
//  - SizeScanBehindLoadingScreen: behind a loading screen, a short scan (a few ms per frame) gives every location a rough size:
//    loaded ones (mod locations) by their pieces, textures and meshes; vanilla ones not loaded yet by the size of their own game
//    file (when that file holds only a few assets). Cached in HitchFix.locationsizes.txt until the game or a mod changes. With
//    SizeGuessCandidates > 0, the biggest ones not known yet are tried as unsaved candidates for this session; a candidate that is
//    placed under BigLocationMs is dropped at once. The guess only ranks; the timed placements decide.
// 2.3.9:
//  - Low RAM: the check uses Windows "available" memory (free + standby, as before), the floor is 10% of RAM but at most
//    LowRamFloorMaxMB (2.5 GB), nothing is let go when what this mod holds could not cover even half of the shortfall, the
//    ProtectTopCopies worst locations are never refused or let go for low RAM, and assets / copies asked for behind the current
//    loading screen are kept until 30 s after it ends.
//  - Refill: a used copy of a location whose worst placement is BigLocationMs or more is made again only behind the next loading
//    screen; smaller ones on calm frames in play (Unity builds them a few objects per frame). No copies at all for listed
//    locations whose measured worst is under MinCopyWorstMs.
//  - The file-size guess no longer ranks anything (RankBySizeGuess, off) and SizeGuessCandidates defaults to 0; the scan stays for
//    the copy memory estimate and the log.
//  - AsyncLoadPriorityLowInWorld: Unity's background loading priority is kept at Low while you play (vanilla's own value; a mod
//    that raised it is undone in the world and its value is put back behind loading screens).
//  - EarlyLoadNearGameRequest: when the game builds a zone, listed big locations not placed yet in the zones around it get their
//    asset load started right away (the game itself only asks for it a moment before placing).
sealed class VpfSizeRow { public char src; public int tr, rn; public long vx; public float texMB, meshMB, bundleMB;
  public float Guess() { return src == 'a' ? texMB + meshMB + tr * 0.004f : bundleMB; } }
[StructLayout(LayoutKind.Sequential)] sealed class VpfMemStat {
  public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
  public VpfMemStat() { dwLength = (uint)Marshal.SizeOf(typeof(VpfMemStat)); } }
public partial class Plugin {
  static ConfigEntry<float> BigMs; static ConfigEntry<int> FastDrop, ResMax, AutoPct, ManualMB, GuessSeeds; static ConfigEntry<bool> RefillOn, ScanOn; static ConfigEntry<string> BudgetMode;
  // 2.3.9
  static ConfigEntry<int> FloorMaxMB, ProtectTop; static ConfigEntry<bool> RamSkipUseless, ProtectScreen, RefillBigBehind, RankGuess, LoadPrioLow, EarlyLoad; static ConfigEntry<float> MinCopyMs;
  void ReserveConfig() {
    string s = SBS + "Location Assets";
    BigMs = Config.Bind(s, "BigLocationMs", 40f, new ConfigDescription("A world location whose normal placement takes this many ms or more (or whose asset load froze the game this long) is put on the remembered list (HitchFix.slowlocations.txt) for this PC, so from then on its asset is loaded and a hidden copy made behind loading screens. 0 = every location-load hitch is remembered, nothing is taken off.", new AcceptableValueRange<float>(0f, 500f)));
    FastDrop = Config.Bind(s, "DropAfterFastPlacements", 5, new ConfigDescription("A remembered location placed the normal way under BigLocationMs this many times in a row is taken off the list (and remembered as not big). One whose asset load itself froze the game over BigLocationMs stays. 0 = never take one off.", new AcceptableValueRange<int>(0, 50)));
    RefillCopyAfterUse = Config.Bind(s, "RefillCopyAfterUse", true, "When a hidden copy of a remembered big location is used, start a new one on the next calm frames so the next one is ready too (within MaxReserveCopies and the memory budget). Off = a used copy is gone until the next loading screen.");
    RefillOn = RefillCopyAfterUse;
    ResMax = Config.Bind(s, "MaxReserveCopies", 10, new ConfigDescription("Most hidden location copies (ready or being made) at a time, of every kind (behind loading screens, at the loaded-area edge, refills). MaxPrebuiltCopies still limits the ones made behind loading screens.", new AcceptableValueRange<int>(1, 16)));
    BudgetMode = Config.Bind(s, "MemoryBudgetMode", "Auto", new ConfigDescription("How much memory hidden location copies may use. Auto = about AutoBudgetPercent of this PC's RAM, less on small PCs (8 GB or less: none, 16 GB: about 1.5 GB, 32 GB or more: the full percentage). Manual = ManualBudgetMB. Copies are also let go when free RAM gets low.", new AcceptableValueList<string>("Auto", "Manual")));
    AutoPct = Config.Bind(s, "AutoBudgetPercent", 15, new ConfigDescription("Auto memory budget in percent of RAM (for 32 GB or more; scaled down on smaller PCs).", new AcceptableValueRange<int>(0, 50)));
    ManualMB = Config.Bind(s, "ManualBudgetMB", 2048, new ConfigDescription("Memory budget in MB for hidden location copies when MemoryBudgetMode = Manual. 0 = no hidden copies.", new AcceptableValueRange<int>(0, 65536)));
    ScanOn = Config.Bind(s, "SizeScanBehindLoadingScreen", true, "Behind the first loading screen, give every location a rough size (a few ms per frame; cached in HitchFix.locationsizes.txt until the game or a mod changes). Used to estimate copy memory and to rank first guesses. Off = no scan.");
    GuessSeeds = Config.Bind(s, "SizeGuessCandidates", 0, new ConfigDescription("How many of the biggest locations by size scan, not known yet, are tried as big for this session (not saved). One placed under BigLocationMs is dropped at once, one over it is kept and saved. 0 = guesses only rank, nothing is tried.", new AcceptableValueRange<int>(0, 10)));
    FloorMaxMB = Config.Bind(s, "LowRamFloorMaxMB", 2560, new ConfigDescription("Hidden copies and kept assets are let go (one per 5 s) when Windows' available memory (free + standby) is under 10% of RAM, but never a higher floor than this many MB.", new AcceptableValueRange<int>(512, 16384)));
    RamSkipUseless = Config.Bind(s, "LowRamSkipWhenUseless", true, "When available memory is under the floor but the hidden copies and kept assets of this mod together are less than half of what is missing, nothing is let go (it would not help, and they would only be loaded again). Off = always let one go.");
    ProtectTop = Config.Bind(s, "ProtectTopCopies", 3, new ConfigDescription("The copies and kept assets of this many worst remembered locations are never let go or refused for low RAM. 0 = none protected.", new AcceptableValueRange<int>(0, 10)));
    ProtectScreen = Config.Bind(s, "ProtectScreenRequests", true, "Assets and copies asked for behind the current loading screen are never let go for low RAM while it is up and for 30 s after it ends.");
    RefillBigBehind = Config.Bind(s, "RefillBigOnlyBehindScreens", true, "A used copy of a location whose worst placement is BigLocationMs or more is made again only behind the next loading screen (making it in play costs frames too). Smaller ones are refilled on calm frames. Off = all on calm frames.");
    MinCopyMs = Config.Bind(s, "MinCopyWorstMs", 10f, new ConfigDescription("A remembered location whose measured worst placement is under this many ms gets no hidden copy (its asset is still kept loaded). 0 = no limit.", new AcceptableValueRange<float>(0f, 200f)));
    RankGuess = Config.Bind(s, "RankBySizeGuess", false, "Rank unmeasured remembered locations by the file-size guess too. Off = only measured hitches and remembered load stalls rank; the size scan is kept for the memory estimate and the log.");
    LoadPrioLow = Config.Bind(s, "AsyncLoadPriorityLowInWorld", true, "Keep Unity's background loading priority at Low while you play (vanilla's own in-world value; less main-thread time per frame goes to finishing loads). Another mod's higher value is undone in the world and put back behind loading screens.");
    EarlyLoad = Config.Bind(s, "EarlyLoadNearGameRequest", true, "When the game builds a zone, remembered big locations not placed yet in that zone and the 8 around it get their asset load started at once (within the RAM floor; the top ProtectTopCopies always), so it is ready when the game places them.");
  }
  static ConfigEntry<bool> RefillCopyAfterUse;
  static float BigV() { return BigMs != null ? BigMs.Value : 0f; }
  static int ResMaxV() { return ResMax != null ? Mathf.Clamp(ResMax.Value, 1, 16) : 16; }
  static string LocNameOf(int h) { if (h == 0) return null; try { var zl = GetLoc(h); return zl != null ? zl.m_prefabName : null; } catch { return null; } }
  // ---- 2.3.8: big list upkeep ----
  static readonly Dictionary<string, int> locFast = new Dictionary<string, int>(); // timed placements under BigLocationMs in a row
  static readonly HashSet<string> locNotBig = new HashSet<string>(), locGuess = new HashSet<string>();
  static int sBigAdded, sBigDropped, sBigFast, sBigKeptStall, sBigUnder, sGuessSeeded, sGuessKept, sGuessDropped;
  static bool LocListedReal(int h) { string n = LocNameOf(h); return n != null && locSlowNames.Contains(n) && !locGuess.Contains(n); }
  static float GuessRank(string n) { if (RankGuess == null || !RankGuess.Value) return 0f; return n != null && locSize.TryGetValue(n, out var r) ? Math.Min(0.99f, r.Guess() / 100000f) : 0f; }
  static void LocBigAdd(string n, int h, float m, string why) {
    locNotBig.Remove(n); locFast.Remove(n);
    locSlowNames.Add(n); locSlowMs[n] = m; if (h != 0) locByName[n] = h;
    while (locSlowNames.Count > (LaMax != null ? LaMax.Value : 16)) LocSlowDropOne();
    locSlowDirty = true; locSaveAt = Time.unscaledTime + 3f; sBigAdded++;
    Log("big location remembered: " + n + " (" + why + ", BigLocationMs=" + BigV().ToString("0") + "; " + locSlowNames.Count + " on the list" + (LaKeep != null && LaKeep.Value ? ", saved for every later session" : "") + ")");
  }
  static void LocBigRemove(string n, string why) {
    int h; locByName.TryGetValue(n, out h);
    locSlowNames.Remove(n); locSlowMs.Remove(n); locStallMs.Remove(n); locFast.Remove(n); bool g = locGuess.Remove(n);
    locNotBig.Add(n); locSlowDirty = true; locSaveAt = Time.unscaledTime + 3f;
    if (h != 0) { for (int i = locHeldRefs.Count - 1; i >= 0; i--) if (locHeldRefs[i].Key == h) { try { locHeldRefs[i].Value.Release(); } catch { } locHeldRefs.RemoveAt(i); } resQueue.Remove(h); }
    Log("location taken off the big list: " + n + " (" + why + ")" + (g ? " (it was a size guess)" : "") + "; remembered as not big");
  }
  // a timed normal placement of a listed location; true = it was taken off the list
  static bool BigFastCheck(string n, float m) {
    float big = BigV(); if (big <= 0f) return false;
    if (m >= big) {
      if (locFast.Remove(n)) locSlowDirty = true;
      if (locGuess.Remove(n)) { sGuessKept++; locSlowDirty = true; locSaveAt = Time.unscaledTime + 3f; Log("size guess confirmed: " + n + " placed in " + m.ToString("0") + " ms (kept and saved)"); }
      return false;
    }
    if (locGuess.Contains(n)) { sGuessDropped++; LocBigRemove(n, "size guess, but placed in " + m.ToString("0") + " ms"); return true; }
    if (LocStallMs(n) >= big) { sBigKeptStall++; return false; } // its asset load is the slow part (the asset is loaded early): kept
    int c = (locFast.TryGetValue(n, out var c0) ? c0 : 0) + 1; locFast[n] = c; sBigFast++; locSlowDirty = true; locSaveAt = Time.unscaledTime + 3f;
    int lim = FastDrop != null ? FastDrop.Value : 0;
    if (lim > 0 && c >= lim) { sBigDropped++; LocBigRemove(n, c + " normal placements in a row under BigLocationMs, last " + m.ToString("0.0") + " ms"); return true; }
    return false;
  }
  // ---- 2.3.8: refill a used copy ----
  static readonly List<int> resQueue = new List<int>(); static readonly HashSet<int> refilled = new HashSet<int>(), refillBigLogged = new HashSet<int>();
  static int sRefillQueued, sRefillStarted, sRefillUsed, sRefillGaveUp, resNext;
  static void ResAfterUse(int h) {
    if (refilled.Remove(h)) sRefillUsed++;
    if (RefillOn == null || !RefillOn.Value || h == 0 || !LocListedReal(h)) return;
    if (MinCopySmall(h)) return; // 2.3.9: too small for a copy
    if (!resQueue.Contains(h)) { resQueue.Add(h); sRefillQueued++; }
  }
  // 2.3.9: worst placement known and >= BigLocationMs: refilled behind the next loading screen only
  static bool RefillIsBig(int h) { float b = BigV(); if (b <= 0f) return false; string n = LocNameOf(h); return Math.Max(LocWorstMs(n), LocStallMs(n)) >= b; }
  static bool MinCopySmall(int h) {
    if (MinCopyMs == null || MinCopyMs.Value <= 0f) return false;
    string n = LocNameOf(h); if (n == null) return false;
    float w = Math.Max(LocWorstMs(n), LocStallMs(n)); return w > 0f && w < MinCopyMs.Value;
  }
  static int sRefillDeferredBig, sMinCopySkips, sMemSkipUseless, sMemProtected, sEarlyLoads; static float memSkipLogAt;
  // ---- 2.3.9: protection from low-RAM release ----
  static readonly Dictionary<int, float> reqAt = new Dictionary<int, float>(); // when this mod last asked for the asset / copy
  static float locScreenStartT = -1f, locScreenEndT = -999f;
  static void NoteReq(int h) { if (h == 0) return; if (reqAt.Count > 512) reqAt.Clear(); reqAt[h] = Time.unscaledTime; }
  static bool ScreenReq(int h) {
    if (ProtectScreen == null || !ProtectScreen.Value || locScreenStartT < 0f) return false;
    if (!reqAt.TryGetValue(h, out var t) || t < locScreenStartT) return false;
    return locScreenOn || Time.unscaledTime - locScreenEndT < 30f;
  }
  static readonly HashSet<int> topSet = new HashSet<int>(); static float topAt = -10f;
  static bool TopProtected(int h) {
    int k = ProtectTop != null ? ProtectTop.Value : 0; if (k <= 0 || h == 0) return false;
    if (Time.unscaledTime - topAt > 1f) {
      topAt = Time.unscaledTime; topSet.Clear();
      var l = new List<string>(); foreach (var n in locSlowNames) if (!locGuess.Contains(n) && Math.Max(LocWorstMs(n), LocStallMs(n)) > 0f) l.Add(n);
      l.Sort((x, y) => Math.Max(LocWorstMs(y), LocStallMs(y)).CompareTo(Math.Max(LocWorstMs(x), LocStallMs(x))));
      var zs = ZoneSystem.instance;
      for (int i = 0; i < l.Count && topSet.Count < k; i++) { int hh = zs != null ? LocHashByName(zs, l[i]) : 0; if (hh != 0) topSet.Add(hh); }
    }
    return topSet.Contains(h);
  }
  static bool RamProtected(int h) { return TopProtected(h) || ScreenReq(h); }
  static float HeldMB() { float s = 0f; foreach (var r in locHeldRefs) if (!preReady.ContainsKey(r.Key) && !preOps.ContainsKey(r.Key)) s += CopyMB(r.Key, null); return s; }
  static void ResRefillTick() {
    if (resQueue.Count == 0 || Time.frameCount < resNext) return;
    resNext = Time.frameCount + 10;
    if (!Settled() && !Loading()) return;
    int h = resQueue[0];
    if (preReady.ContainsKey(h) || preOps.ContainsKey(h) || !LocListedReal(h)) { resQueue.RemoveAt(0); return; }
    if (MinCopySmall(h)) { resQueue.RemoveAt(0); sMinCopySkips++; return; }
    if (!Loading() && RefillBigBehind != null && RefillBigBehind.Value && RefillIsBig(h)) {
      // 2.3.9: a big one waits for the next loading screen (the others in the queue go first)
      if (resQueue.Count > 1) { resQueue.RemoveAt(0); resQueue.Add(h); }
      if (!refillBigLogged.Contains(h)) { refillBigLogged.Add(h); sRefillDeferredBig++; Log("location copy refill put off to the next loading screen: " + LocName(h) + " (" + MsText(LocNameOf(h)) + ", BigLocationMs " + BigV().ToString("0") + ")"); }
      bool any = false; foreach (var q in resQueue) if (!RefillIsBig(q)) { any = true; break; }
      if (!any) return;
      h = resQueue[0]; if (RefillIsBig(h)) return;
    }
    if (TryStartPreInst(h, true)) { resQueue.RemoveAt(0); refilled.Add(h); refillBigLogged.Remove(h); sRefillStarted++; Log("location copy refilled after use: " + LocName(h) + " (" + MsText(LocNameOf(h)) + "; " + (preReady.Count + preOps.Count) + "/" + ResMaxV() + " copies, est " + ResUsedMB(0).ToString("0") + "/" + ResBudgetMB().ToString("0") + " MB)"); return; }
    if (preWhy != null && (preWhy.StartsWith("waiting") || preWhy == "another copy is being built")) return; // try again on a later frame
    resQueue.RemoveAt(0); sRefillGaveUp++; Log("location copy not refilled: " + LocName(h) + " (" + preWhy + ")");
  }
  // ---- 2.3.8: memory budget ----
  static long ramMB = -1; static bool ramLogged; static long freeMBLast = -1; static float memNext; static bool memApiBad;
  static int sBudgetRefused, sResDropped, sMemDrops;
  static readonly Dictionary<string, float> copyMBCache = new Dictionary<string, float>();
  [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalMemoryStatusEx([In, Out] VpfMemStat b);
  static bool ManualMode() { return BudgetMode != null && string.Equals(BudgetMode.Value, "Manual", StringComparison.OrdinalIgnoreCase); }
  static float AutoBudget(long t, int pct) {
    if (t <= 0) return 1536f * pct / 15f; // RAM unknown: as for 16 GB
    float full = t * pct / 100f, mid = 1536f * pct / 15f;
    if (t <= 8192) return 0f;
    if (t <= 16384) return (t - 8192) / 8192f * mid;
    if (t < 32768) return mid + (t - 16384) / 16384f * (32768f * pct / 100f - mid);
    return full;
  }
  // 2.3.9: 10% of RAM, at most LowRamFloorMaxMB (2.3.8: at least 1.5 GB, so 6.4 GB on 64 GB)
  static long FreeFloorMB() { long cap = FloorMaxMB != null ? FloorMaxMB.Value : 2560L; return ramMB > 0 ? Math.Min(ramMB / 10, cap) : Math.Min(1536L, cap); }
  static float ResBudgetMB() {
    if (ramMB < 0) { try { ramMB = SystemInfo.systemMemorySize; } catch { ramMB = 0; } }
    float b = ManualMode() ? (ManualMB != null ? ManualMB.Value : 0) : AutoBudget(ramMB, AutoPct != null ? AutoPct.Value : 15);
    if (!ramLogged) {
      ramLogged = true; long f = FreeMB(); freeMBLast = f;
      Log("memory: RAM " + (ramMB > 0 ? ramMB + " MB" : "unknown") + (f >= 0 ? ", available now (incl. standby) " + f + " MB" : "") + " -> hidden location copies may use " + b.ToString("0") + " MB (" + (ManualMode() ? "Manual" : "Auto " + (AutoPct != null ? AutoPct.Value : 15) + "%") + "), at most " + ResMaxV() + " copies; copies are let go when available RAM is under " + FreeFloorMB() + " MB (10% of RAM, at most LowRamFloorMaxMB)" + (memApiBad ? " (free RAM not readable here, not checked)" : ""));
    }
    return b;
  }
  static long FreeMB() {
    if (memApiBad) return -1;
    try {
      if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) { memApiBad = true; return -1; }
      var m = new VpfMemStat(); if (!GlobalMemoryStatusEx(m)) return -1;
      return (long)(m.ullAvailPhys / (1024UL * 1024UL));
    } catch { memApiBad = true; return -1; }
  }
  // estimated memory of one hidden copy of this location: its objects plus the textures / meshes it keeps loaded (an upper bound:
  // textures shared with other things are counted too). Unknown: 24 MB.
  static float CopyMB(int h, GameObject asset) {
    string n = LocNameOf(h) ?? ("#" + h);
    if (copyMBCache.TryGetValue(n, out var v)) return v;
    locSize.TryGetValue(n, out var r);
    if (asset != null && (r == null || r.src != 'a')) { try { r = MeasureAsset(asset); locSize[n] = r; } catch { } }
    if (r == null) return 24f;
    v = r.src == 'a' ? Math.Max(4f, r.tr * 0.004f + r.texMB + r.meshMB) : Math.Max(4f, r.bundleMB * 2f);
    copyMBCache[n] = v; return v;
  }
  static float ResUsedMB(int except) {
    float s = 0f;
    foreach (var k in preReady.Keys) if (k != except) s += CopyMB(k, null);
    foreach (var k in preOps.Keys) if (k != except && !preReady.ContainsKey(k)) s += CopyMB(k, null);
    return s;
  }
  static float ResRank(int h) { string n = LocNameOf(h); return n != null && locSlowNames.Contains(n) ? RankKey(n) : -1f; }
  static int ResLowest(int except, bool forRam = false) {
    int v = 0; float vr = float.MaxValue;
    foreach (var kv in preReady) { if (kv.Key == except || preUrgent.Contains(kv.Key)) continue; if (forRam && RamProtected(kv.Key)) continue; float r = ResRank(kv.Key); if (r < vr) { vr = r; v = kv.Key; } }
    return v;
  }
  static void ResDrop(int h, string why) {
    if (!preReady.TryGetValue(h, out var go)) return;
    preReady.Remove(h); prebuilt.Remove(h); refilled.Remove(h);
    try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
    sResDropped++; Log("hidden location copy let go: " + LocName(h) + " (" + why + ")");
  }
  static bool ResBudgetOk(int h, GameObject asset, out string why) {
    why = null; float b = ResBudgetMB();
    if (b <= 0f) { why = "memory budget is 0 MB (" + (ManualMode() ? "ManualBudgetMB" : "RAM " + ramMB + " MB") + ")"; sBudgetRefused++; return false; }
    if (freeMBLast >= 0 && freeMBLast < FreeFloorMB()) { if (TopProtected(h)) sMemProtected++; else { why = "available RAM low (" + freeMBLast + " MB)"; sBudgetRefused++; return false; } } // 2.3.9: never for the top ProtectTopCopies
    float need = CopyMB(h, asset), used = ResUsedMB(h); float mine = ResRank(h);
    for (int guard = 0; used + need > b && guard < 16; guard++) {
      int v = ResLowest(h); if (v == 0 || ResRank(v) >= mine) break;
      ResDrop(v, "memory budget: room for " + LocName(h)); used = ResUsedMB(h);
    }
    if (used + need <= b) return true;
    why = "memory budget (" + used.ToString("0") + " MB used + " + need.ToString("0") + " MB over " + b.ToString("0") + " MB)"; sBudgetRefused++; return false;
  }
  static void MemWatchTick() {
    if (Time.unscaledTime < memNext) return;
    memNext = Time.unscaledTime + 5f;
    if (preReady.Count == 0 && locHeldRefs.Count == 0) return;
    long f = FreeMB(); freeMBLast = f; if (f < 0) return;
    long floor = FreeFloorMB(); if (f >= floor) return;
    // 2.3.9: letting go is pointless when everything this mod holds could not cover even half of what is missing
    long shortMB = floor - f;
    if (RamSkipUseless != null && RamSkipUseless.Value) {
      float ours = ResUsedMB(0) + HeldMB();
      if (ours * 2f < shortMB) {
        sMemSkipUseless++;
        if (Time.unscaledTime >= memSkipLogAt) { memSkipLogAt = Time.unscaledTime + 60f; Log("available RAM (incl. standby) " + f + " MB is under " + floor + " MB, but hidden copies and kept assets hold only about " + ours.ToString("0") + " MB of the " + shortMB + " MB missing: nothing let go (it would not help)"); }
        return;
      }
    }
    int v = ResLowest(0, true);
    if (v != 0) { sMemDrops++; ResDrop(v, "available RAM (incl. standby) " + f + " MB is under " + floor + " MB"); return; }
    int k = -1; float kr = float.MaxValue; bool prot = false;
    for (int i = 0; i < locHeldRefs.Count; i++) { int hh = locHeldRefs[i].Key; if (preOps.ContainsKey(hh)) continue; if (RamProtected(hh)) { prot = true; continue; } float r = ResRank(hh); if (r < kr) { kr = r; k = i; } }
    if (k < 0) { if (prot) sMemProtected++; return; }
    int h2 = locHeldRefs[k].Key; try { locHeldRefs[k].Value.Release(); } catch { } locHeldRefs.RemoveAt(k); sMemDrops++;
    Log("kept location asset let go: " + LocName(h2) + " (available RAM (incl. standby) " + f + " MB is under " + floor + " MB; it is loaded again behind a later loading screen)");
  }
  // ---- 2.3.8: size scan behind the loading screen ----
  static readonly Dictionary<string, VpfSizeRow> locSize = new Dictionary<string, VpfSizeRow>();
  static int scanState, scanPos, scanAssetPos, scanFrames, sScanA, sScanB, sScanShared, sScanNone; static double scanMs; static string scanFrom = "not run";
  static List<ZoneSystem.ZoneLocation> scanList; static int[] bundleCount; static Array scanAls, scanBls; static System.Collections.IDictionary scanIdx; static System.Reflection.FieldInfo fBli, fPath;
  static bool guessDone;
  static string SizePath() { try { return Path.Combine(Application.persistentDataPath, "HitchFix.locationsizes.txt"); } catch { return null; } }
  static string SizeKey() {
    var l = new List<string>();
    try { foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos) { try { l.Add(kv.Key + "@" + kv.Value.Metadata.Version); } catch { l.Add(kv.Key); } } } catch { }
    l.Sort(StringComparer.Ordinal);
    string s = global::Version.GetVersionString(false) + "|" + string.Join(",", l.ToArray());
    ulong x = 14695981039346656037UL; foreach (char c in s) { x ^= c; x *= 1099511628211UL; }
    return x.ToString("x16");
  }
  static bool SizeLoad(string key) {
    var p = SizePath(); if (p == null || !File.Exists(p)) return false;
    var inv = System.Globalization.CultureInfo.InvariantCulture; var fl = System.Globalization.NumberStyles.Float;
    var lines = File.ReadAllLines(p); bool ok = false; var tmp = new Dictionary<string, VpfSizeRow>();
    foreach (var line in lines) {
      if (line.StartsWith("key\t")) { ok = line.Substring(4).Trim() == key; if (!ok) return false; continue; }
      if (line.Length == 0 || line[0] == '#') continue;
      var c = line.Split('\t'); if (c.Length < 8 || c[1].Length != 1) continue;
      var r = new VpfSizeRow { src = c[1][0] };
      int.TryParse(c[2], out r.tr); int.TryParse(c[3], out r.rn); long.TryParse(c[4], out r.vx);
      float.TryParse(c[5], fl, inv, out r.texMB); float.TryParse(c[6], fl, inv, out r.meshMB); float.TryParse(c[7], fl, inv, out r.bundleMB);
      tmp[c[0]] = r;
    }
    if (!ok) return false;
    foreach (var kv in tmp) locSize[kv.Key] = kv.Value;
    return true;
  }
  static void SizeSave(string key) {
    try {
      var p = SizePath(); if (p == null) return; var inv = System.Globalization.CultureInfo.InvariantCulture;
      var lines = new List<string> { "# rough location sizes for ranking (written by the performance mod; made again when the game or a mod changes; safe to delete)", "# name<TAB>a = measured loaded asset, b = size of its own game file<TAB>pieces<TAB>renderers<TAB>vertices<TAB>texture MB<TAB>mesh MB<TAB>file MB per asset", "key\t" + key };
      foreach (var kv in locSize) { var r = kv.Value; lines.Add(kv.Key + "\t" + r.src + "\t" + r.tr + "\t" + r.rn + "\t" + r.vx + "\t" + r.texMB.ToString("0.00", inv) + "\t" + r.meshMB.ToString("0.00", inv) + "\t" + r.bundleMB.ToString("0.00", inv)); }
      File.WriteAllLines(p, lines.ToArray());
    } catch (Exception e) { Log("location size scan: could not save: " + e.Message); }
  }
  static void ScanReflInit() {
    scanAls = scanBls = null; scanIdx = null; fBli = fPath = null; bundleCount = null;
    try {
      var t = typeof(SoftReferenceableAssets.SoftReference<GameObject>).Assembly.GetType("SoftReferenceableAssets.AssetBundleLoader");
      var pi = t != null ? AccessTools.Property(t, "Instance") : null; var inst = pi != null ? pi.GetValue(null, null) : null;
      if (inst == null) { Log("location size scan: the game's asset file table is not available; only loaded locations are measured"); return; }
      var bls = AccessTools.Field(t, "m_bundleLoaders").GetValue(inst) as Array; var als = AccessTools.Field(t, "m_assetLoaders").GetValue(inst) as Array;
      var idx = AccessTools.Field(t, "m_assetIDToLoaderIndex").GetValue(inst) as System.Collections.IDictionary;
      if (bls == null || als == null || idx == null) return;
      var fb = AccessTools.Field(als.GetType().GetElementType(), "m_bundleLoaderIndex"); var fp = AccessTools.Field(bls.GetType().GetElementType(), "m_bundlePath");
      if (fb == null || fp == null) return;
      scanBls = bls; scanAls = als; scanIdx = idx; fBli = fb; fPath = fp; bundleCount = new int[bls.Length];
    } catch (Exception e) { scanAls = null; Log("location size scan: asset file table not readable (" + e.Message + "); only loaded locations are measured"); }
  }
  static long TexBytes(Texture t) {
    long px = (long)t.width * t.height; float bpp = 4f;
    var t2 = t as Texture2D;
    if (t2 != null) switch (t2.format) {
      case TextureFormat.DXT1: case TextureFormat.BC4: case TextureFormat.DXT1Crunched: bpp = 0.5f; break;
      case TextureFormat.DXT5: case TextureFormat.BC5: case TextureFormat.BC7: case TextureFormat.DXT5Crunched: case TextureFormat.BC6H: bpp = 1f; break;
      case TextureFormat.RGBAHalf: bpp = 8f; break; case TextureFormat.RGBAFloat: bpp = 16f; break;
    }
    return (long)(px * bpp * 1.33f);
  }
  static VpfSizeRow MeasureAsset(GameObject a) {
    var r = new VpfSizeRow { src = 'a' };
    r.tr = a.GetComponentsInChildren<Transform>(true).Length;
    var rends = a.GetComponentsInChildren<Renderer>(true); r.rn = rends.Length;
    var texSeen = new HashSet<int>(); var matSeen = new HashSet<int>(); var meshSeen = new HashSet<int>(); long tb = 0, mb = 0;
    foreach (var rd in rends) {
      if (rd == null) continue;
      var mats = rd.sharedMaterials; if (mats != null) foreach (var m in mats) {
        if (m == null || !matSeen.Add(m.GetInstanceID())) continue;
        var ids = m.GetTexturePropertyNameIDs(); if (ids == null) continue;
        foreach (var id in ids) { var tx = m.GetTexture(id); if (tx != null && texSeen.Add(tx.GetInstanceID())) tb += TexBytes(tx); }
      }
      var sk = rd as SkinnedMeshRenderer; if (sk != null && sk.sharedMesh != null && meshSeen.Add(sk.sharedMesh.GetInstanceID())) { r.vx += sk.sharedMesh.vertexCount; mb += sk.sharedMesh.vertexCount * 48L; }
    }
    foreach (var mf in a.GetComponentsInChildren<MeshFilter>(true)) { var me = mf != null ? mf.sharedMesh : null; if (me != null && meshSeen.Add(me.GetInstanceID())) { r.vx += me.vertexCount; mb += me.vertexCount * 48L; } }
    r.texMB = tb / 1048576f; r.meshMB = mb / 1048576f; return r;
  }
  static void ScanOne(ZoneSystem.ZoneLocation zl) {
    string n = zl.m_prefabName; var sr = zl.m_prefab; bool loaded = false;
    try { loaded = sr.IsValid && sr.IsLoaded; } catch { }
    if (loaded) { GameObject a = null; try { a = sr.Asset; } catch { } if (a != null) { locSize[n] = MeasureAsset(a); sScanA++; return; } }
    if (scanIdx != null) {
      object key = sr.m_assetID;
      if (scanIdx.Contains(key)) {
        int ai = (int)scanIdx[key]; int b = (int)fBli.GetValue(scanAls.GetValue(ai)); int cnt = b >= 0 && b < bundleCount.Length ? bundleCount[b] : 0;
        if (cnt > 0 && cnt <= 8) { var p = fPath.GetValue(scanBls.GetValue(b)) as string; if (!string.IsNullOrEmpty(p) && File.Exists(p)) { locSize[n] = new VpfSizeRow { src = 'b', bundleMB = new FileInfo(p).Length / 1048576f / cnt }; sScanB++; return; } }
        sScanShared++; return; // in a big shared file: its own size cannot be told from the file
      }
    }
    sScanNone++;
  }
  static void SizeScanTick() {
    if (scanState == 2 || ScanOn == null || !ScanOn.Value || !Loading()) return;
    var zs = ZoneSystem.instance; if (zs == null || zs.m_locations == null || zs.m_locations.Count == 0) return;
    var sw = Stopwatch.StartNew();
    try {
      if (scanState == 0) {
        string key = SizeKey();
        if (SizeLoad(key)) { scanState = 2; scanFrom = "cached (" + locSize.Count + ")"; Log("location sizes: " + locSize.Count + " read from HitchFix.locationsizes.txt (game and mods unchanged, no scan)"); GuessSeed(zs); return; }
        scanList = new List<ZoneSystem.ZoneLocation>(); var seen = new HashSet<string>();
        foreach (var l in zs.m_locations) if (l != null && !string.IsNullOrEmpty(l.m_prefabName) && seen.Add(l.m_prefabName)) scanList.Add(l);
        locSize.Clear(); copyMBCache.Clear(); scanPos = scanAssetPos = 0; scanState = 1; ScanReflInit(); scanFrom = key;
      }
      while (scanAls != null && scanAssetPos < scanAls.Length && sw.Elapsed.TotalMilliseconds < 3.0) {
        int end = Math.Min(scanAls.Length, scanAssetPos + 500);
        for (; scanAssetPos < end; scanAssetPos++) { int b = (int)fBli.GetValue(scanAls.GetValue(scanAssetPos)); if (b >= 0 && b < bundleCount.Length) bundleCount[b]++; }
      }
      if (scanAls != null && scanAssetPos < scanAls.Length) return;
      while (scanPos < scanList.Count && sw.Elapsed.TotalMilliseconds < 3.0) { try { ScanOne(scanList[scanPos]); } catch { sScanNone++; } scanPos++; }
      if (scanPos < scanList.Count) return;
      string k = scanFrom; scanState = 2; SizeSave(k);
      var top = new List<KeyValuePair<string, VpfSizeRow>>(locSize); top.Sort((x, y) => y.Value.Guess().CompareTo(x.Value.Guess()));
      var sb = new StringBuilder(); for (int i = 0; i < top.Count && i < 6; i++) sb.Append(i == 0 ? "" : ", ").Append(top[i].Key).Append(' ').Append(top[i].Value.Guess().ToString("0")).Append(" MB").Append(top[i].Value.src == 'a' ? "" : " (file)");
      scanFrom = "scanned (" + scanList.Count + " locations, " + locSize.Count + " with a size)"; // 2.3.9: the count was the sized ones only
      Log("location sizes scanned behind the loading screen: " + scanList.Count + " locations, " + sScanA + " measured loaded, " + sScanB + " by their own game file, " + sScanShared + " in shared files (no guess), " + sScanNone + " without one; " + (scanMs + sw.Elapsed.TotalMilliseconds).ToString("0") + " ms over " + (scanFrames + 1) + " frames; biggest: " + sb);
      scanList = null; scanAls = scanBls = null; scanIdx = null; bundleCount = null;
      GuessSeed(zs);
    } catch (Exception e) { scanState = 2; scanFrom = "failed"; Log("location size scan failed: " + e.Message); }
    finally { scanMs += sw.Elapsed.TotalMilliseconds; scanFrames++; }
  }
  static void GuessSeed(ZoneSystem zs) {
    if (guessDone) return; guessDone = true;
    int k = GuessSeeds != null ? GuessSeeds.Value : 0; if (k <= 0 || BigV() <= 0f) return;
    var c = new List<KeyValuePair<string, VpfSizeRow>>();
    foreach (var kv in locSize) if (kv.Value.Guess() >= 8f && !locSlowNames.Contains(kv.Key) && !locNotBig.Contains(kv.Key)) c.Add(kv);
    c.Sort((x, y) => y.Value.Guess().CompareTo(x.Value.Guess()));
    var names = new List<string>(); int max = LaMax != null ? LaMax.Value : 16;
    for (int i = 0; i < c.Count && names.Count < k && locSlowNames.Count < max; i++) {
      if (LocHashByName(zs, c[i].Key) == 0) continue;
      locSlowNames.Add(c[i].Key); locGuess.Add(c[i].Key); sGuessSeeded++; names.Add(c[i].Key + " " + c[i].Value.Guess().ToString("0") + " MB");
    }
    if (names.Count > 0) Log("first guesses from the size scan (tried as big for this session only, not saved until a placement takes BigLocationMs or longer): " + string.Join(", ", names.ToArray()));
  }
  static void ReserveTick() {
    var zn = ZNet.instance; if (zn == null || zn.IsDedicated()) return;
    ResBudgetMB(); // logs RAM and budget once
    SizeScanTick(); ResRefillTick(); MemWatchTick();
    try { LoadPrioTick(); } catch { }
  }
  // ---- 2.3.9: background loading priority ----
  static bool prioChanged, prioLogged; static UnityEngine.ThreadPriority prioOrig = UnityEngine.ThreadPriority.Low; static int sPrioSet;
  static void LoadPrioTick() {
    bool world = LoadPrioLow != null && LoadPrioLow.Value && InGame() && !Loading();
    var cur = Application.backgroundLoadingPriority;
    if (world) {
      if (cur == UnityEngine.ThreadPriority.Low) return;
      if (!prioChanged) { prioOrig = cur; prioChanged = true; }
      Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.Low; sPrioSet++;
      if (!prioLogged) { prioLogged = true; Log("background loading priority set to Low in the world (it was " + cur + "; put back behind loading screens)"); }
    } else if (prioChanged) {
      prioChanged = false;
      if (cur == UnityEngine.ThreadPriority.Low && prioOrig != UnityEngine.ThreadPriority.Low) Application.backgroundLoadingPriority = prioOrig;
    }
  }
  // ---- 2.3.9: start the asset load of listed big locations around a zone the game is building ----
  static readonly HashSet<Vector2s> earlyDone = new HashSet<Vector2s>(); static ZoneSystem earlyZs;
  static void EarlyLoadAround(Vector2s z) {
    if (EarlyLoad == null || !EarlyLoad.Value || LaPre == null || !LaPre.Value || locSlowNames.Count == 0) return;
    var zn = ZNet.instance; if (zn == null || zn.IsDedicated()) return;
    var zs = ZoneSystem.instance; if (zs == null || zs.m_locationInstances == null) return;
    if (zs != earlyZs) { earlyZs = zs; earlyDone.Clear(); }
    if (earlyDone.Count > 8192) earlyDone.Clear();
    if (!earlyDone.Add(z)) return;
    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) {
      var k = new Vector2s(z.x + dx, z.y + dy);
      if (!zs.m_locationInstances.TryGetValue(k, out var li) || li.m_placed || li.m_location == null) continue;
      string n = li.m_location.m_prefabName; if (n == null || !locSlowNames.Contains(n) || locGuess.Contains(n)) continue;
      int h = LocHashByName(zs, n); if (h == 0 || LocHeld(h)) continue;
      var sr = li.m_location.m_prefab; if (!sr.IsValid) continue;
      bool top = TopProtected(h);
      if (!top && freeMBLast >= 0 && freeMBLast < FreeFloorMB()) continue; // low RAM: only the worst ones
      if (!top && LaMax != null && locHeldRefs.Count >= LaMax.Value) continue;
      bool was = false; try { was = sr.IsLoaded; } catch { }
      sr.LoadAsync(); locHeldRefs.Add(new KeyValuePair<int, SoftReferenceableAssets.SoftReference<GameObject>>(h, sr)); NoteReq(h); sEarlyLoads++;
      if (LogSlow.Value) Log("location asset load started early: " + n + " (" + MsText(n) + ") in zone " + k.x + "," + k.y + ", the game is building zone " + z.x + "," + z.y + (was ? "; already loaded, now kept" : ""));
    }
  }
  static string Stats239() {
    return "lowRam floor=" + FreeFloorMB() + "MB skippedUseless=" + sMemSkipUseless + " protected=" + sMemProtected + " | refill bigToNextScreen=" + sRefillDeferredBig + " minCopySkips=" + sMinCopySkips
      + " | earlyLoads=" + sEarlyLoads + " | loadPriority=" + Application.backgroundLoadingPriority + " setLow=" + sPrioSet + (prioChanged ? " (was " + prioOrig + ")" : "")
      + " | " + DgCheckStats();
  }
  static void ReserveReset() { resQueue.Clear(); refilled.Clear(); }
  static string Stats238() {
    float b = ResBudgetMB();
    return "copies ready=" + preReady.Count + " building=" + preOps.Count + "/" + ResMaxV() + " est " + ResUsedMB(0).ToString("0") + "/" + b.ToString("0") + " MB (RAM " + ramMB + " MB " + (ManualMode() ? "Manual" : "Auto") + (freeMBLast >= 0 ? ", free " + freeMBLast + " MB" : "") + ")"
      + " refill queued=" + sRefillQueued + " started=" + sRefillStarted + " used=" + sRefillUsed + " gaveUp=" + sRefillGaveUp + " budgetRefused=" + sBudgetRefused + " letGo=" + sResDropped + " lowRam=" + sMemDrops
      + " | big list=" + locSlowNames.Count + " added=" + sBigAdded + " under=" + sBigUnder + " fast=" + sBigFast + " dropped=" + sBigDropped + " keptStall=" + sBigKeptStall + " notBig=" + locNotBig.Count
      + " | sizes " + scanFrom + (RankGuess != null && RankGuess.Value ? " (ranks)" : " (memory estimate only)") + " loaded=" + sScanA + " file=" + sScanB + " shared=" + sScanShared + " none=" + sScanNone + " " + scanMs.ToString("0") + " ms/" + scanFrames + "f guesses=" + sGuessSeeded + " kept=" + sGuessKept + " dropped=" + sGuessDropped;
  }
}
}
