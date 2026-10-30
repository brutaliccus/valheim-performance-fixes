using System; using System.Collections.Generic; using System.Diagnostics; using System.Text;
using BepInEx.Configuration; using BepInEx.Logging; using HarmonyLib; using UnityEngine; using UnityEngine.LowLevel;
namespace SpawnBudget {
// 2.3.7 "Arrival timing" (client, diagnostics only; changes nothing in the game).
//  For ArrivalSec after every loading screen ends (world arrival, teleport, respawn), each frame is split into Unity's top-level
//  phases (initialization, early update, fixed update / physics, pre-update, update, pre-late update, post-late update / rendering)
//  by a marker at the start of each phase, the number of garbage collections in the frame is counted, and other mods' log lines
//  are noted with the frame they were written in. A frame over ArrivalLogMs gets that detail on its HITCH line, or its own
//  "ARRIVAL frame" line. Outside the window the markers are taken out of the player loop again and the log listener is removed.
//  Also: the once-per-session startup checks (patch audit, light-copy check, removal cap check) are done behind the world loading
//  screen instead of about 10 s after arrival, and are only repeated in the world if other mods' patches changed since.
sealed class VpfArrivalMark { }
public partial class Plugin {
  static ConfigEntry<bool> AtOn, ArEarly; static ConfigEntry<float> ArSec, ArLogMs;
  void ArrivalConfig() {
    AtOn = Config.Bind(SBS + "Diagnostics", "ArrivalTiming", true, "For ArrivalSec after each loading screen ends, frames over ArrivalLogMs are logged with Unity's frame phases (physics, update, rendering...), garbage collections and other mods' log lines in that frame, so the log names what ran. Diagnostics only. Off = no extra timing.");
    ArSec = Config.Bind(SBS + "Diagnostics", "ArrivalSec", 15f, new ConfigDescription("How long ArrivalTiming runs after a loading screen ends.", new AcceptableValueRange<float>(3f, 120f)));
    ArLogMs = Config.Bind(SBS + "Diagnostics", "ArrivalLogMs", 33f, new ConfigDescription("ArrivalTiming logs frames longer than this.", new AcceptableValueRange<float>(16f, 500f)));
    ArEarly = Config.Bind(SBS + "General", "StartupChecksBehindLoadingScreen", true, "The once-per-session checks of other mods' patches (patch audit, light location copies, removal cap) run behind the world loading screen instead of about 10 s after arrival, and run again in the world only if other mods' patches changed since. Same checks, same results. Off = after SettleGraceSec in the world, as before.");
    AtOn.SettingChanged += (s, e) => { if (!AtOn.Value) ArClose("switched off"); };
  }

  // ---- startup checks behind the loading screen ----
  static string auditSig; static bool auditEarly, auditRechecked;
  static System.Reflection.MethodBase[] AuditTargets() {
    var l = new List<System.Reflection.MethodBase> { AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) }), AccessTools.DeclaredMethod(typeof(ZNetScene), "RemoveObjects"), AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation"),
      AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone"), AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnProxyLocation"), AccessTools.DeclaredMethod(typeof(Heightmap), "CustomLateUpdate"), AccessTools.DeclaredMethod(typeof(DungeonGenerator), "OnRoomLoaded"), AccessTools.DeclaredMethod(typeof(DungeonGenerator), "LoadRoomPrefabsAsync"),
      AccessTools.DeclaredMethod(typeof(ZoneSystem), "PrepareSave") };
    l.AddRange(flushHooks); return l.ToArray();
  }
  // cheap: who patches the checked methods, and how many patches each has
  static string PatchSig() {
    var sb = new StringBuilder();
    foreach (var m in AuditTargets()) {
      if (m == null) { sb.Append("-;"); continue; }
      var pi = Harmony.GetPatchInfo(m);
      if (pi == null) { sb.Append("0;"); continue; }
      sb.Append(pi.Prefixes.Count).Append(',').Append(pi.Postfixes.Count).Append(',').Append(pi.Transpilers.Count).Append(',').Append(pi.Finalizers.Count).Append(';');
      foreach (var o in pi.Owners) sb.Append(o).Append(' ');
      sb.Append('|');
    }
    return sb.ToString();
  }
  static void EarlyChecksTick() {
    if (audited || ArEarly == null || !ArEarly.Value) return;
    if (ZNet.instance == null || ZNetScene.instance == null || ZoneSystem.instance == null) return;
    if (!Loading()) return; // only behind the loading screen; otherwise the old timing applies
    var sw = Stopwatch.StartNew();
    Audit(); auditEarly = true;
    try { auditSig = PatchSig(); } catch { auditSig = null; }
    try { if (!slAudited && slOk) SlAudit(); } catch { }
    try { if (!Application.isBatchMode && RmCapOn != null && RmCapOn.Value && rmCapOk && !rmCapChecked) { rmCapChecked = true; RmCapDecide(); } } catch (Exception e) { Log("removal cap check failed: " + e.Message); }
    Log("startup checks done behind the loading screen in " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms (patch audit, light location copies, removal cap); checked again in the world only if other mods' patches change");
  }
  // once settled in the world: only if another mod patched or unpatched one of the checked methods since the loading screen
  static void EarlyRecheck() {
    if (!auditEarly || auditRechecked || !Settled()) return;
    auditRechecked = true;
    var sw = Stopwatch.StartNew(); string now = null; try { now = PatchSig(); } catch { }
    if (now != null && now == auditSig) { global::ValheimPerformanceFixes.FrameCost.Add("audit", sw.Elapsed.TotalMilliseconds, "patch check (unchanged)"); return; }
    Log("other mods' patches changed since the loading screen: patch audit again");
    Audit(); try { SlAudit(); } catch { } rmCapChecked = false; // the removal cap decides again on its next pass
    global::ValheimPerformanceFixes.FrameCost.Add("audit", sw.Elapsed.TotalMilliseconds, "patch audit (patches changed)");
  }

  // ---- the arrival window ----
  static readonly Type[] arTypes = { typeof(UnityEngine.PlayerLoop.Initialization), typeof(UnityEngine.PlayerLoop.EarlyUpdate), typeof(UnityEngine.PlayerLoop.FixedUpdate), typeof(UnityEngine.PlayerLoop.PreUpdate), typeof(UnityEngine.PlayerLoop.Update), typeof(UnityEngine.PlayerLoop.PreLateUpdate), typeof(UnityEngine.PlayerLoop.PostLateUpdate) };
  static readonly string[] arNames = { "init", "early", "physics", "preUpdate", "update", "lateUpdate", "render" };
  static PlayerLoopSystem.UpdateFunction[] arFns;
  static readonly long[] arTs = new long[7]; static readonly int[] arTsFrame = new int[7];
  static readonly double[] arPhase = new double[7]; static int arPhaseFrame = -1, arPhaseGc, arGc0;
  static volatile int arCurFrame; static volatile bool arOpen;
  static bool arLoop; static float arStart, arUntil; static int arLines, arFrames, arSlow, arLogged = -1; static double arWorst; static string arWorstAt = "-";
  static int sArWindows, sArSlow; static double sArWorst;
  static ArListener arListener;
  struct ArLine { public int frame; public string text; }
  static readonly ArLine[] arRing = new ArLine[64]; static int arRingN; static readonly object arLock = new object();
  sealed class ArListener : ILogListener {
    public void LogEvent(object sender, LogEventArgs e) {
      if (!arOpen || e == null) return;
      try {
        var src = e.Source != null ? e.Source.SourceName : "?"; if (BLog != null && ReferenceEquals(e.Source, BLog)) return;
        string d = e.Data != null ? e.Data.ToString() : ""; if (d.Length > 90) d = d.Substring(0, 90) + "...";
        d = d.Replace('\n', ' ').Replace('\r', ' ');
        lock (arLock) { arRing[arRingN % arRing.Length] = new ArLine { frame = arCurFrame, text = "[" + src + "] " + d }; arRingN++; }
      } catch { }
    }
    public void Dispose() { }
  }
  static void ArMark(int i) {
    if (!arOpen) return;
    long t = Stopwatch.GetTimestamp();
    if (i == 0) {
      // a new frame starts: the previous one is complete
      int pf = arTsFrame[0]; bool ok = true;
      for (int k = 0; k < 7; k++) { if (arTsFrame[k] != pf) { ok = false; break; } long nx = k < 6 ? arTs[k + 1] : t; arPhase[k] = (nx - arTs[k]) * 1000.0 / Stopwatch.Frequency; }
      arPhaseFrame = ok ? pf : -1;
      int g = GC.CollectionCount(0); arPhaseGc = g - arGc0; arGc0 = g;
      arCurFrame = arCurFrame + 1;
    } else arCurFrame = Time.frameCount;
    arTs[i] = t; arTsFrame[i] = arCurFrame;
  }
  static bool ArLoopSet(bool on) {
    try {
      var root = PlayerLoop.GetCurrentPlayerLoop(); if (root.subSystemList == null) return false;
      if (on && arFns == null) { arFns = new PlayerLoopSystem.UpdateFunction[7]; for (int k = 0; k < 7; k++) { int kk = k; arFns[k] = () => ArMark(kk); } }
      bool changed = false;
      for (int i = 0; i < root.subSystemList.Length; i++) {
        int k = Array.IndexOf(arTypes, root.subSystemList[i].type); if (k < 0) continue;
        var ph = root.subSystemList[i]; var subs = new List<PlayerLoopSystem>(ph.subSystemList ?? new PlayerLoopSystem[0]);
        int had = subs.FindIndex(s => s.type == typeof(VpfArrivalMark));
        if (on && had < 0) { subs.Insert(0, new PlayerLoopSystem { type = typeof(VpfArrivalMark), updateDelegate = arFns[k] }); changed = true; }
        else if (!on && had >= 0) { subs.RemoveAll(s => s.type == typeof(VpfArrivalMark)); changed = true; }
        else continue;
        ph.subSystemList = subs.ToArray(); root.subSystemList[i] = ph;
      }
      if (changed) PlayerLoop.SetPlayerLoop(root);
      return true;
    } catch (Exception e) { Log("arrival timing: player loop markers " + (on ? "could not be added" : "could not be removed") + ": " + e.Message); return false; }
  }
  // called when a loading screen ends
  static void ArOpen() {
    if (AtOn == null || !AtOn.Value) return;
    try {
      if (arOpen) ArClose("next arrival");
      arCurFrame = Time.frameCount; arGc0 = GC.CollectionCount(0); arPhaseFrame = -1; for (int k = 0; k < 7; k++) arTsFrame[k] = -1;
      lock (arLock) arRingN = 0;
      arLoop = ArLoopSet(true);
      if (arListener == null) { arListener = new ArListener(); BepInEx.Logging.Logger.Listeners.Add(arListener); }
      arStart = Time.unscaledTime; arUntil = arStart + ArSec.Value; arLines = 0; arFrames = 0; arSlow = 0; arWorst = 0; arWorstAt = "-"; sArWindows++;
      arOpen = true;
      Log("arrival timing on for " + ArSec.Value.ToString("0") + " s: frames over " + ArLogMs.Value.ToString("0") + " ms are logged with frame phases" + (arLoop ? "" : " (not available)") + ", garbage collections and other mods' log lines");
    } catch (Exception e) { Log("arrival timing failed to start: " + e.Message); ArClose(null); }
  }
  static void ArClose(string why) {
    if (!arOpen && arListener == null && !arLoop) return;
    bool was = arOpen; arOpen = false;
    try { if (arListener != null) { BepInEx.Logging.Logger.Listeners.Remove(arListener); arListener = null; } } catch { }
    if (arLoop) { ArLoopSet(false); arLoop = false; }
    if (was && why != null) Log("arrival timing done (" + why + "): " + arFrames + " frames, " + arSlow + " over " + (ArLogMs != null ? ArLogMs.Value : 33f).ToString("0") + " ms, worst " + arWorst.ToString("0") + " ms at " + arWorstAt);
  }
  // detail for frame f (the previous frame, as HitchTick describes it)
  static string ArDetail(int f) {
    var sb = new StringBuilder(" | arrival +").Append((Time.unscaledTime - arStart).ToString("0.0")).Append(" s");
    if (arPhaseFrame == f) {
      sb.Append(" | phases");
      for (int k = 0; k < 7; k++) if (arPhase[k] >= 0.5) sb.Append(' ').Append(arNames[k]).Append(' ').Append(arPhase[k].ToString("0.0"));
      sb.Append(" ms");
      if (arPhaseGc > 0) sb.Append(" | GC x").Append(arPhaseGc);
    }
    int n = 0; var lines = new StringBuilder();
    lock (arLock) {
      int from = Math.Max(0, arRingN - arRing.Length);
      for (int i = from; i < arRingN; i++) { var l = arRing[i % arRing.Length]; if (l.frame != f) continue; if (n < 4) lines.Append(n == 0 ? "" : "; ").Append(l.text); n++; }
    }
    if (n > 0) sb.Append(" | other logs that frame (").Append(n).Append("): ").Append(lines.ToString());
    return sb.ToString();
  }
  // from LateUpdate, after HitchTick
  static void ArFrameTick() {
    if (!arOpen) return;
    if (ZNet.instance == null) { ArClose("logout"); return; }
    if (Time.unscaledTime > arUntil) { ArClose(ArSec.Value.ToString("0") + " s after arrival"); return; }
    int f = Time.frameCount; double dt = Time.unscaledDeltaTime * 1000.0; arFrames++;
    if (dt <= ArLogMs.Value) return;
    arSlow++; sArSlow++;
    if (dt > arWorst) { arWorst = dt; arWorstAt = "+" + (Time.unscaledTime - arStart).ToString("0.0") + " s (frame " + (f - 1) + ")"; }
    if (dt > sArWorst) sArWorst = dt;
    if (arLogged == f - 1 || arLines >= 60) return; // already on a HITCH line / enough lines
    arLines++;
    string d = global::ValheimPerformanceFixes.FrameCost.Describe(f - 1, 0.5, out double sum, out string first);
    Log("ARRIVAL frame " + (f - 1) + ": " + dt.ToString("0") + " ms" + (d.Length > 0 ? " | " + d : "") + ArDetail(f - 1));
  }
  // timing of this module's own Update steps while the window is open (shows up in the HITCH / ARRIVAL lines)
  static long ArT0() { return arOpen ? Stopwatch.GetTimestamp() : 0L; }
  static void ArCost(long t0, string owner, string what) {
    if (t0 == 0L) return;
    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    if (ms >= 0.2) global::ValheimPerformanceFixes.FrameCost.Add(owner, ms, what);
  }
  static string ArStats() { return "arrivalTiming " + (AtOn != null && AtOn.Value ? "on" : "off") + " windows=" + sArWindows + " framesOver=" + sArSlow + " worst=" + sArWorst.ToString("0") + "ms | startupChecks " + (ArEarly != null && ArEarly.Value ? (auditEarly ? "behind loading screen" : "pending") : "off (after settle)") + (auditRechecked ? " rechecked" : ""); }
}
}
