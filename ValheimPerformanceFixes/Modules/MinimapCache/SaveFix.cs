using System; using System.Collections; using System.Collections.Generic; using System.Diagnostics; using System.IO; using System.IO.Compression; using System.Reflection; using System.Threading;
using BepInEx.Configuration; using HarmonyLib; using UnityEngine;

namespace MinimapCache {
// 1.1.2 SaveHitchFix. Character saves (Game.SavePlayerProfile) hitch the main thread for ~0.3-1 s. Biggest single cost is
// Minimap.GetMapData: it writes the two 2048x2048 explored BitArrays one bool at a time (~8.4 M ZPackage.Write(bool) calls)
// and then gzips the 8.4 MB payload, on every save, even if nothing changed. In the 2026-10-25 logs the server RPC save and the
// client's own 30 min timer save also landed in the same second (two full saves back to back, 21:59:04).
//  - CacheCompressedMap: exact snapshot compare of both bit arrays + the pins tail; unchanged -> reuse the last compressed bytes.
//  - FastMapSerialize: builds the identical payload from BitArray.CopyTo(int[]) words; verified byte-for-byte against vanilla's
//    own GetMapData once per session at join (behind the loading screen); any mismatch disables it for the session.
//  - PrewarmIntervalSec: snapshot on the main thread (~1 ms), expand+gzip on a worker thread, so a later save is a cache hit.
//  - DedupWindowSec: a periodic save (server RPC or the local 30 min timer) within N s of a completed save is skipped.
//  - DeferSaveInCombatSec: a periodic save during combat/boss music is postponed until 2 s of calm, at most N s (max 60).
// NEVER skipped: quit, logout, shutdown, admin save, sleep, respawn (anything not periodic). Shutdown also flushes a
// pending deferred save.
//  - 2.3.8 SaveAtScreenEnd: a save that comes while a loading / teleport / sleep screen is ending (the wake-up save after
//    sleeping in the 2.3.7 logs: 185 ms on the first visible frame) either runs at once with the black screen held a few
//    frames longer (HoldScreen, while the screen is still mostly black), or is moved to DelaySaveAfterScreenSec after the
//    screen is gone (Delay, and for HoldScreen when the screen is already mostly faded). Never for shutdown / logout. The disk write itself stays on the main thread (Steam cloud FileWriter / SaveSystem are not thread-safe);
// vanilla .old backups and autobackups are untouched.
static class SaveFix {
  static ConfigEntry<bool> On, Cache, Fast, Verify, Log; static ConfigEntry<float> Prewarm, Dedup, Defer, ScreenDelay; static ConfigEntry<string> ScreenSave;
  static BepInEx.Logging.ManualLogSource L;
  static FieldInfo fExplored = AccessTools.Field(typeof(Minimap), "m_explored"), fOthers = AccessTools.Field(typeof(Minimap), "m_exploredOthers"), fPins = AccessTools.Field(typeof(Minimap), "m_pins");
  static MethodInfo mGetMapData = AccessTools.DeclaredMethod(typeof(Minimap), "GetMapData");
  static FieldInfo fSaveTimer = AccessTools.Field(typeof(Game), "m_saveTimer"), fLastCombat = AccessTools.Field(typeof(Player), "m_lastCombatTimer"), fMusCombat = AccessTools.Field(typeof(MusicMan), "m_combatTimer");
  static MethodInfo mCurMusic = AccessTools.Method(typeof(MusicMan), "GetCurrentMusic");
  internal static void Init(ConfigFile c, Harmony h, BepInEx.Logging.ManualLogSource log) {
    L = log;
    On = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "Enabled", true, "Master switch for the character-save hitch fixes below (client only; inactive on a dedicated server). false = vanilla save path.");
    Cache = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "CacheCompressedMap", true, "Reuse the last compressed map data when the explored map, shared map and pins are exactly unchanged (exact compare, no hash guessing).");
    Fast = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "FastMapSerialize", true, "Build the map payload from the bit arrays' 32-bit words instead of 8.4 M single bool writes. Only used after it matched vanilla byte-for-byte at join (see VerifyAtJoin).");
    Verify = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "VerifyAtJoin", true, "Once per session, behind the loading screen, run vanilla GetMapData and compare with the fast payload; mismatch = fast path off for the session (logged). Also seeds the cache. If false, FastMapSerialize is never used.");
    Prewarm = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "PrewarmIntervalSec", 120f, new ConfigDescription("Every N s, if the map changed, snapshot it (~1-2 ms on the main thread) and compress it on a worker thread so the next save is a cache hit. 0 = off.", new AcceptableValueRange<float>(0f, 1800f)));
    Dedup = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "DedupWindowSec", 20f, new ConfigDescription("Skip a PERIODIC save (server save RPC or the local 30 min timer) that comes within N s of the last completed save. 0 = off. Quit/logout/admin/sleep saves are never skipped.", new AcceptableValueRange<float>(0f, 120f)));
    Defer = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "DeferSaveInCombatSec", 30f, new ConfigDescription("Postpone a PERIODIC save while in combat (targeted, attacking, combat or boss music) until 2 s of calm, at most N s. 0 = off. Quit/logout always save immediately and also flush a pending deferred save.", new AcceptableValueRange<float>(0f, 60f)));
    ScreenSave = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "SaveAtScreenEnd", "HoldScreen", new ConfigDescription("A character save that comes while a loading / teleport / sleep screen is ending (or up to 1 s after it ended) would land on the first frames you see (the wake-up save after sleeping took 185 ms in earlier logs). HoldScreen = run it at once and keep the black screen up a few frames longer so the save is not seen (if the screen is already mostly faded it is delayed instead). Delay = run it DelaySaveAfterScreenSec after the screen is gone. Off = vanilla. Shutdown / logout saves are never moved.", new AcceptableValueList<string>("HoldScreen", "Delay", "Off")));
    ScreenDelay = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "DelaySaveAfterScreenSec", 2f, new ConfigDescription("How long after the screen is gone a save moved by SaveAtScreenEnd runs.", new AcceptableValueRange<float>(0.5f, 10f)));
    Log = c.Bind(global::ValheimPerformanceFixes.Sections.MC + "2. Save Hitch Fix", "LogSaves", true, "Log one line per save: trigger, total ms, map path (cache hit / fast / vanilla) and ms.");
    try {
      if (mGetMapData == null || fExplored == null || fOthers == null || fPins == null) { L.LogWarning("SaveHitchFix: Minimap fields not found; map fixes inactive"); }
      else {
        h.Patch(mGetMapData, prefix: new HarmonyMethod(typeof(SaveFix), nameof(MapPre)), postfix: new HarmonyMethod(typeof(SaveFix), nameof(MapPost)));
        h.Patch(AccessTools.DeclaredMethod(typeof(Minimap), "LoadMapData"), postfix: new HarmonyMethod(typeof(SaveFix), nameof(LoadPost)));
      }
      h.Patch(AccessTools.DeclaredMethod(typeof(Game), "SavePlayerProfile"), prefix: new HarmonyMethod(typeof(SaveFix), nameof(SavePre)), finalizer: new HarmonyMethod(typeof(SaveFix), nameof(SaveFin)));
      h.Patch(AccessTools.DeclaredMethod(typeof(Game), "UpdateSaving"), prefix: new HarmonyMethod(typeof(SaveFix), nameof(TimerPre)), finalizer: new HarmonyMethod(typeof(SaveFix), nameof(TimerFin)));
      h.Patch(AccessTools.DeclaredMethod(typeof(Game), "Shutdown"), prefix: new HarmonyMethod(typeof(SaveFix), nameof(ShutPre)));
      // 2.3.9: the wake-up save is called from Game.SleepStop; mark it so it is always treated as a save at the end of a screen
      bool wakeOk = false;
      try { var ms = AccessTools.DeclaredMethod(typeof(Game), "SleepStop"); if (ms != null) { h.Patch(ms, prefix: new HarmonyMethod(typeof(SaveFix), nameof(WakePre)), finalizer: new HarmonyMethod(typeof(SaveFix), nameof(WakeFin))); wakeOk = true; } } catch (Exception e) { L.LogWarning("SaveHitchFix: wake-up hook not patched: " + e.Message); }
      try {
        bool saveOk = false; var pi = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(Game), "SavePlayerProfile"));
        if (pi != null) foreach (var p in pi.Prefixes) if (p.PatchMethod != null && p.PatchMethod.DeclaringType == typeof(SaveFix)) saveOk = true;
        L.LogInfo("SaveHitchFix hooks: Game.SavePlayerProfile=" + (saveOk ? "ok" : "MISSING") + " Game.SleepStop(wake-up save)=" + (wakeOk ? "ok" : "MISSING") + " SaveAtScreenEnd=" + ScreenMode());
      } catch { }
      L.LogInfo("SaveHitchFix ready: enabled=" + On.Value + " cache=" + Cache.Value + " fast=" + Fast.Value + " verify=" + Verify.Value + " prewarm=" + Prewarm.Value + "s dedup=" + Dedup.Value + "s defer=" + Defer.Value + "s");
    } catch (Exception e) { L.LogError("SaveHitchFix patch failed (vanilla saving unaffected): " + e.Message); }
  }
  static bool Active => On.Value && ZNet.instance != null && !ZNet.instance.IsDedicated();

  // ---------- map data ----------
  static int[] snapA, snapB; static byte[] snapTail; static int snapSize, snapLen; static byte[] cachedResult; // cached result belongs to the snap
  static volatile bool fastOk; static bool verified; static bool bypassMap; internal static string lastMapWhy = "";
  static readonly object lk = new object(); static volatile bool workerBusy;
  static byte[] Tail(Minimap m) {
    var z = new ZPackage(); var pins = (List<Minimap.PinData>)fPins.GetValue(m); int n = 0;
    foreach (var p in pins) if (p.m_save) n++;
    z.Write(n);
    foreach (var p in pins) if (p.m_save) { z.Write(p.m_name); z.Write(p.m_pos); z.Write((int)p.m_type); z.Write(p.m_checked); z.Write(p.m_ownerID); z.Write(((object)p.m_author).ToString()); }
    z.Write(ZNet.instance.IsReferencePositionPublic());
    return z.GetArray();
  }
  static int[] Words(BitArray b) { var w = new int[(b.Length + 31) / 32]; b.CopyTo(w, 0); int r = b.Length & 31; if (r != 0) w[w.Length - 1] &= (int)((1u << r) - 1); return w; }
  static bool Same(int[] a, int[] b) { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
  static bool Same(byte[] a, byte[] b) { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
  static byte[] Payload(int size, int len, int[] a, int[] b, byte[] tail) {
    var o = new byte[4 + len * 2 + tail.Length]; o[0] = (byte)size; o[1] = (byte)(size >> 8); o[2] = (byte)(size >> 16); o[3] = (byte)(size >> 24);
    int k = 4; Expand(a, len, o, ref k); Expand(b, len, o, ref k); Buffer.BlockCopy(tail, 0, o, k, tail.Length); return o;
  }
  static void Expand(int[] w, int len, byte[] o, ref int k) { for (int i = 0; i < len; i++) o[k++] = (byte)((w[i >> 5] >> (i & 31)) & 1); }
  static byte[] Compress(byte[] d) { using (var ms = new MemoryStream()) { using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest)) gz.Write(d, 0, d.Length); return ms.ToArray(); } }
  static byte[] Wrap(byte[] comp) { using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms)) { w.Write(8); w.Write(comp.Length); w.Write(comp); w.Flush(); return ms.ToArray(); } }
  struct MapState { public int[] a, b; public byte[] tail; public int size, len; public Stopwatch sw; }
  static bool MapPre(Minimap __instance, ref byte[] __result, out MapState __state) {
    __state = default;
    if (bypassMap || !Active) return true;
    try {
      var sw = Stopwatch.StartNew();
      var ex = (BitArray)fExplored.GetValue(__instance); var ot = (BitArray)fOthers.GetValue(__instance);
      if (ex == null || ot == null || ot.Length < ex.Length) return true;
      // vanilla writes m_exploredOthers[0..m_explored.Length); copy only that range
      var a = Words(ex); var b = Words(ot); if (b.Length > a.Length) Array.Resize(ref b, a.Length);
      if (ot.Length > ex.Length && (ex.Length & 31) != 0) { int r = ex.Length & 31; b[b.Length - 1] &= (int)((1u << r) - 1); }
      var tail = Tail(__instance);
      __state = new MapState { a = a, b = b, tail = tail, size = __instance.m_textureSize, len = ex.Length, sw = sw };
      lock (lk) {
        if (Cache.Value && cachedResult != null && snapSize == __instance.m_textureSize && snapLen == ex.Length && Same(snapA, a) && Same(snapB, b) && Same(snapTail, tail)) {
          __result = (byte[])cachedResult.Clone(); lastMapWhy = "cache hit " + sw.ElapsedMilliseconds + "ms"; __state.sw = null; return false;
        }
      }
      if (Fast.Value && fastOk) {
        var pay = Payload(__state.size, __state.len, a, b, tail); var res = Wrap(Compress(pay));
        ZLog.Log("Minimap: compressed mapData " + pay.Length + " => " + res.Length + " bytes (MinimapCache fast path)");
        Store(__state, res); __result = (byte[])res.Clone(); lastMapWhy = "fast " + sw.ElapsedMilliseconds + "ms"; __state.sw = null; return false;
      }
    } catch (Exception e) { L.LogWarning("SaveHitchFix map prefix failed, using vanilla: " + e.Message); __state = default; }
    return true;
  }
  static void MapPost(byte[] __result, MapState __state) {
    if (__state.sw == null || __state.a == null || __result == null) return; // ran our path, or bypass
    try { Store(__state, (byte[])__result.Clone()); lastMapWhy = "vanilla " + __state.sw.ElapsedMilliseconds + "ms"; } catch { }
  }
  static void Store(MapState s, byte[] res) { lock (lk) { snapA = s.a; snapB = s.b; snapTail = s.tail; snapSize = s.size; snapLen = s.len; cachedResult = res; } }
  static void LoadPost(Minimap __instance) {
    if (verified || !Active || !Verify.Value) return; verified = true;
    try {
      var sw = Stopwatch.StartNew();
      var ex = (BitArray)fExplored.GetValue(__instance); var ot = (BitArray)fOthers.GetValue(__instance);
      var a = Words(ex); var b = Words(ot); if (b.Length > a.Length) Array.Resize(ref b, a.Length);
      if (ot.Length > ex.Length && (ex.Length & 31) != 0) { int r = ex.Length & 31; b[b.Length - 1] &= (int)((1u << r) - 1); }
      var tail = Tail(__instance);
      byte[] van; bypassMap = true; try { van = (byte[])mGetMapData.Invoke(__instance, null); } finally { bypassMap = false; }
      long tv = sw.ElapsedMilliseconds;
      var z = new ZPackage(van); int ver = z.ReadInt(); var inner = z.ReadCompressedPackage().GetArray();
      var mine = Payload(__instance.m_textureSize, ex.Length, a, b, tail);
      fastOk = ver == 8 && Same(inner, mine);
      Store(new MapState { a = a, b = b, tail = tail, size = __instance.m_textureSize, len = ex.Length }, (byte[])van.Clone());
      L.LogMessage("SaveHitchFix: join self-check " + (fastOk ? "PASSED" : "FAILED (version " + ver + ", vanilla " + inner.Length + " vs fast " + mine.Length + " bytes) - fast path OFF this session, cache still on")
        + "; vanilla GetMapData took " + tv + " ms, check total " + sw.ElapsedMilliseconds + " ms (behind loading screen)");
    } catch (Exception e) { fastOk = false; L.LogWarning("SaveHitchFix: join self-check error, fast path OFF: " + e.Message); }
  }
  static float nextPrewarm;
  static void PrewarmTick() {
    if (Prewarm.Value <= 0f || !Cache.Value || !fastOk || workerBusy || Time.realtimeSinceStartup < nextPrewarm) return;
    nextPrewarm = Time.realtimeSinceStartup + Prewarm.Value;
    var m = Minimap.instance; if (m == null || Player.m_localPlayer == null) return;
    try {
      var ex = (BitArray)fExplored.GetValue(m); var ot = (BitArray)fOthers.GetValue(m); if (ex == null || ot == null || ot.Length < ex.Length) return;
      var a = Words(ex); var b = Words(ot); if (b.Length > a.Length) Array.Resize(ref b, a.Length);
      if (ot.Length > ex.Length && (ex.Length & 31) != 0) { int r = ex.Length & 31; b[b.Length - 1] &= (int)((1u << r) - 1); }
      var s = new MapState { a = a, b = b, tail = Tail(m), size = m.m_textureSize, len = ex.Length };
      lock (lk) if (cachedResult != null && snapSize == s.size && snapLen == s.len && Same(snapA, a) && Same(snapB, b) && Same(snapTail, s.tail)) return;
      workerBusy = true;
      new Thread(() => { try { var res = Wrap(Compress(Payload(s.size, s.len, s.a, s.b, s.tail))); Store(s, res); } catch { } finally { workerBusy = false; } }) { IsBackground = true, Name = "MinimapCache prewarm", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
    } catch (Exception e) { L.LogWarning("SaveHitchFix prewarm skipped: " + e.Message); }
  }

  // ---------- save scheduling ----------
  [ThreadStatic] static bool inTimer; static bool bypassSave; static float lastSaveEnd = -999f, pendingSince = -1f, calmSince = -1f; static string pendingWhy;
  static void TimerPre() { inTimer = true; }
  static Exception TimerFin(Exception __exception) { inTimer = false; return __exception; }
  static void ShutPre() { if (pendingSince >= 0f || screenPendingSince >= 0f) L.LogMessage("SaveHitchFix: shutdown/logout - the shutdown save also covers the deferred " + (pendingSince >= 0f ? pendingWhy : "after-screen") + " save"); pendingSince = -1f; screenPendingSince = -1f; }
  // ---------- 2.3.8: saves at the end of a loading / teleport / sleep screen ----------
  static float lastBlackAt = -999f, screenPendingSince = -1f; static bool screenPendingLogout; static int holdUntilFrame = -1; static string runWhy;
  static int sScreenHeld, sScreenDelayed; static long sScreenHeldMs;
  // 2.3.9: wake-up save marker and cumulative counters
  static bool inWake; static int sWakeSaves, sHeldAll, sDelayedAll; static string lastDecide;
  static void WakePre() { inWake = true; }
  static Exception WakeFin(Exception __exception) { inWake = false; return __exception; }
  internal static string Stats239() { return "saves wakeUp=" + sWakeSaves + " heldBehindScreen=" + sHeldAll + " movedAfterScreen=" + sDelayedAll + " (since start)"; }
  static float ScreenAlpha() { try { var h = Hud.instance; if (h == null || h.m_loadingScreen == null || !h.m_loadingScreen.gameObject.activeInHierarchy) return 0f; return h.m_loadingScreen.alpha; } catch { return 0f; } }
  static void ScreenWatch() { if (ScreenAlpha() > 0.01f) lastBlackAt = Time.realtimeSinceStartup; }
  // read by RenderTrim's Hud.UpdateBlackScreen prefix: keep the black screen as it is this frame
  internal static bool HoldBlack() { return holdUntilFrame >= Time.frameCount && Hud.instance != null; }
  static string ScreenMode() { return ScreenSave == null ? "Off" : ScreenSave.Value; }
  internal static string Stats238() { string s = "saveAtScreenEnd " + ScreenMode() + " heldBehindScreen=" + sScreenHeld + (sScreenHeld > 0 ? " avg=" + (sScreenHeldMs / sScreenHeld) + "ms" : "") + " delayed=" + sScreenDelayed; sScreenHeld = sScreenDelayed = 0; sScreenHeldMs = 0; return s; }
  static bool SavePre(Game __instance, bool setLogoutPoint, bool isFromRpc, out Stopwatch __state) {
    __state = null;
    try {
      if (!Active || bypassSave || __instance.IsShuttingDown() || Player.m_localPlayer == null) { __state = Stopwatch.StartNew(); lastMapWhy = ""; return true; }
      bool periodic = isFromRpc || inTimer; string trig = isFromRpc ? "server rpc" : inTimer ? "timer" : "other";
      runWhy = null;
      if (!periodic && ScreenMode() != "Off") {
        var pl = Player.m_localPlayer; float now = Time.realtimeSinceStartup; float a = ScreenAlpha(); ScreenWatch();
        bool ending = !pl.IsSleeping() && !pl.IsTeleporting() && !pl.IsDead() && (a > 0.01f || now - lastBlackAt <= 1f);
        if (inWake && !pl.IsDead()) { ending = true; sWakeSaves++; } // 2.3.9: the wake-up save, whatever the screen state reads
        lastDecide = "alpha=" + a.ToString("0.00") + " sinceBlack=" + (lastBlackAt < 0f ? "never" : (now - lastBlackAt).ToString("0.0") + "s") + (inWake ? " wake-up" : "") + (ending ? " -> screen end" : " -> normal");
        if (ending) {
          if (ScreenMode() == "HoldScreen" && a >= 0.5f) { holdUntilFrame = Time.frameCount + 3; sScreenHeld++; sHeldAll++; runWhy = "at screen end" + (inWake ? " (wake-up)" : "") + ", black screen held"; }
          else {
            if (screenPendingSince < 0f) { screenPendingSince = now; sScreenDelayed++; sDelayedAll++; L.LogMessage("SaveHitchFix: save at the end of a loading / teleport / sleep screen moved to " + ScreenDelay.Value.ToString("0.0") + " s after the screen is gone"); }
            screenPendingLogout |= setLogoutPoint;
            if (Log.Value) L.LogMessage("SaveHitchFix: save request (" + lastDecide + ") moved after the screen");
            return false;
          }
        }
      }
      if (periodic) {
        float now = Time.realtimeSinceStartup;
        if (Dedup.Value > 0f && now - lastSaveEnd < Dedup.Value) {
          if (inTimer) fSaveTimer?.SetValue(__instance, 0f);
          L.LogMessage("SaveHitchFix: skipped " + trig + " save, a save completed " + (now - lastSaveEnd).ToString("0.0") + " s ago (DedupWindowSec=" + Dedup.Value + ")");
          return false;
        }
        if (Defer.Value > 0f && InCombat(out var why)) {
          if (inTimer) fSaveTimer?.SetValue(__instance, 0f);
          if (pendingSince < 0f) { pendingSince = now; pendingWhy = trig; calmSince = -1f; L.LogMessage("SaveHitchFix: deferring " + trig + " save (" + why + "), max " + Defer.Value + " s"); }
          return false;
        }
      }
      __state = Stopwatch.StartNew(); lastMapWhy = "";
    } catch (Exception e) { L.LogWarning("SaveHitchFix save prefix error, saving normally: " + e.Message); __state = Stopwatch.StartNew(); }
    return true;
  }
  static Exception SaveFin(Exception __exception, bool setLogoutPoint, bool isFromRpc, Stopwatch __state) {
    if (__state != null) {
      lastSaveEnd = Time.realtimeSinceStartup; pendingSince = -1f;
      if (runWhy != null && runWhy.StartsWith("at screen end")) { sScreenHeldMs += __state.ElapsedMilliseconds; holdUntilFrame = Time.frameCount + 3; }
      if (Log.Value) L.LogMessage("SaveHitchFix: save (" + (runWhy ?? (bypassSave ? "deferred " + pendingWhy : isFromRpc ? "server rpc" : inTimer ? "timer" : "other")) + ") took " + __state.ElapsedMilliseconds + " ms; map " + (lastMapWhy.Length > 0 ? lastMapWhy : "n/a") + (__exception != null ? "; EXCEPTION " + __exception.Message : "") + (lastDecide != null ? "; " + lastDecide : ""));
    }
    runWhy = null; lastDecide = null;
    return __exception;
  }
  static bool InCombat(out string why) {
    why = null; var p = Player.m_localPlayer; if (p == null) return false;
    try {
      if (p.IsTargeted()) { why = "targeted"; return true; }
      if (fLastCombat != null && (float)fLastCombat.GetValue(p) < 3f) { why = "attacking"; return true; }
      var mm = MusicMan.instance;
      if (mm != null) {
        var cur = mCurMusic?.Invoke(mm, null) as string;
        if (cur != null && cur.StartsWith("boss", StringComparison.OrdinalIgnoreCase)) { why = "boss music " + cur; return true; }
        if (fMusCombat != null && (float)fMusCombat.GetValue(mm) > 0f) { why = "combat music"; return true; }
      }
    } catch { }
    return false;
  }
  internal static void Tick() {
    if (!Active) return;
    PrewarmTick();
    ScreenWatch();
    if (screenPendingSince >= 0f) ScreenTick();
    if (pendingSince < 0f) return;
    var g = Game.instance; float now = Time.realtimeSinceStartup;
    if (g == null || g.IsShuttingDown()) { pendingSince = -1f; return; }
    if (Player.m_localPlayer == null) return; // respawning: wait (cap still applies once back)
    bool calm = !InCombat(out _); if (!calm) calmSince = -1f; else if (calmSince < 0f) calmSince = now;
    bool cap = now - pendingSince >= Defer.Value; bool quiet = calm && now - calmSince >= 2f;
    if (!cap && !quiet) return;
    L.LogMessage("SaveHitchFix: running deferred " + pendingWhy + " save after " + (now - pendingSince).ToString("0.0") + " s (" + (cap ? "cap reached" : "combat over") + ")");
    bypassSave = true; try { g.SavePlayerProfile(true); } catch (Exception e) { L.LogWarning("deferred save failed: " + e.Message); } finally { bypassSave = false; pendingSince = -1f; }
  }
  static void ScreenTick() {
    var g = Game.instance; float now = Time.realtimeSinceStartup;
    if (g == null || g.IsShuttingDown()) { screenPendingSince = -1f; return; }
    if (Player.m_localPlayer == null) return;
    bool due = now - lastBlackAt >= ScreenDelay.Value, cap = now - screenPendingSince >= 30f;
    if (!due && !cap) return;
    bool lp = screenPendingLogout; screenPendingSince = -1f; screenPendingLogout = false;
    runWhy = "moved from the screen end, " + (cap ? "30 s cap" : ScreenDelay.Value.ToString("0.0") + " s after it");
    bypassSave = true; try { g.SavePlayerProfile(lp); } catch (Exception e) { L.LogWarning("moved save failed: " + e.Message); } finally { bypassSave = false; runWhy = null; }
  }
}
}
