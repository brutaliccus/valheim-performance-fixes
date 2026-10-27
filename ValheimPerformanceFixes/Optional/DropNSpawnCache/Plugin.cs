// DropNSpawnCache - performance companion for sighsorry.DropNSpawn (written for 1.4.8).
// 1) Caches DropNSpawn's per-frame "game data"/"snapshot" signatures (which otherwise walk every
//    ZNetScene prefab with ~10 GetComponent calls + name allocations whenever any object work runs).
// 2) Skips redundant per-instance live reconcile for unconditional Pickable-only overrides when the
//    fresh instance already carries exactly the values DropNSpawn applied to its prefab.
// 3) Null-guards ObjectDropManager.CloneDropTable (NRE on prefabs with a null DropTable / m_drops).
// Every patch is optional: if a target is missing, that feature is skipped and logged.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawnCache
{
  public class Plugin : global::ValheimPerformanceFixes.ModuleBehaviour
  {
    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> cSig, cPick, cNull, cStats;
    internal static ConfigEntry<float> cRevalidate, cSlice, cDeferSec; internal static ConfigEntry<bool> cPace, cDefer;
    Harmony h;

    void Awake()
    {
      Log = Logger;
      cSig = Config.Bind(global::ValheimPerformanceFixes.Sections.DNS + "Features", "CacheSignatures", true, "Cache DropNSpawn object-domain prefab signatures (invalidated when ZNetScene/ObjectDB prefab lists change, on DropNSpawn game-data refresh, and every RevalidateSeconds).");
      cRevalidate = Config.Bind(global::ValheimPerformanceFixes.Sections.DNS + "Features", "RevalidateSeconds", 30f, "Force a real signature recompute at most this often (safety net). 0 = never.");
      cPick = Config.Bind(global::ValheimPerformanceFixes.Sections.DNS + "Features", "SkipRedundantPickableReconcile", true, "Skip queuing live reconcile for freshly spawned Pickables whose prefab only has unconditional Pickable overrides and whose values already match the configured prefab.");
      cNull = Config.Bind(global::ValheimPerformanceFixes.Sections.DNS + "Features", "GuardCloneDropTable", true, "Return null / empty copy instead of throwing when DropNSpawn snapshots a null DropTable.");
      cStats = Config.Bind(global::ValheimPerformanceFixes.Sections.DNS + "Diagnostics", "LogStats", true, "Log a one-line counter summary every 60 s while counters change.");
      cPace = Config.Bind(global::ValheimPerformanceFixes.Sections.DNS + "Pacing", "PaceRuntimeWork", true, "1.0.1: DropNSpawn's per-frame runtime work (payload sync, snapshot build, reconcile) has a 1 ms target but a single step can take much longer. After a frame where it took longer than RefreshSliceMs, skip it for ceil(took/RefreshSliceMs) frames so slow steps do not stack into a stutter. Never paces while loading or with no local player.");
      cSlice = Config.Bind(global::ValheimPerformanceFixes.Sections.DNS + "Pacing", "RefreshSliceMs", 8f, new ConfigDescription("Per-frame cost above which the following frames are skipped.", new AcceptableValueRange<float>(2f, 50f)));
      cDefer = Config.Bind(global::ValheimPerformanceFixes.Sections.DNS + "Pacing", "DeferUntilAfterSpawn", false, "1.0.1: skip DropNSpawn runtime work entirely until DeferSec after the local player spawns (and after each teleport). Off by default: work queued during load then runs a few seconds later.");
      cDeferSec = Config.Bind(global::ValheimPerformanceFixes.Sections.DNS + "Pacing", "DeferSec", 3f, new ConfigDescription("Seconds after spawn/teleport for DeferUntilAfterSpawn.", new AcceptableValueRange<float>(0f, 30f)));
      h = new Harmony("vpf.ValheimPerformanceFixes.DropNSpawnCache");
      try { DNS.Init(h); } catch (Exception e) { Log.LogError("init failed, plugin inactive: " + e); }
    }

    float nextStats;
    void Update()
    {
      if (!cStats.Value || Time.realtimeSinceStartup < nextStats) return;
      nextStats = Time.realtimeSinceStartup + 60f;
      DNS.LogStats();
    }
  }

  static class DNS
  {
    static Type odm, plugin;
    static FieldInfo fPending, fLastApplied, fSyncReady, fProfile, fRuntime;
    static MethodInfo mTryKinds, mHasCond, mDeferred, mTrack;
    static PropertyInfo pActive, pCfgSig;
    static Func<GameObject, string> getPrefabName;
    static object reloadObject;

    // stats
    static long sigHits, sigMiss, pickSkip, pickQueue, nullGuards; static double sigMissMs;
    static long lastTotal = -1;

    public static void Init(Harmony h)
    {
      Assembly asm = null;
      foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "DropNSpawn") { asm = a; break; }
      if (asm == null) { Plugin.Log.LogWarning("DropNSpawn assembly not loaded; nothing to do."); return; }
      try { InitPacing(h, asm); } catch (Exception e) { Plugin.Log.LogWarning("pacing init failed: " + e.Message); }
      odm = asm.GetType("DropNSpawn.ObjectDropManager"); plugin = asm.GetType("DropNSpawn.DropNSpawnPlugin");
      if (odm == null) { Plugin.Log.LogWarning("DropNSpawn.ObjectDropManager not found; nothing to do."); return; }
      var ver = plugin?.GetCustomAttribute<BepInPlugin>()?.Version?.ToString() ?? "?";
      Plugin.Log.LogInfo("DropNSpawn " + ver + " detected" + (ver != "1.4.8" ? " (written for 1.4.8 - patches are guarded, check log)" : ""));
      var me = typeof(DNS); const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

      // --- 1. signature cache
      if (Plugin.cSig.Value)
      {
        int n = 0;
        foreach (var name in new[] { "ComputeGameDataSignatureCore", "ComputeSnapshotSignatureCore" })
        {
          var m = AccessTools.Method(odm, name, Type.EmptyTypes);
          if (m == null || m.ReturnType != typeof(int)) { Plugin.Log.LogWarning("signature cache: " + name + " not found, skipped"); continue; }
          h.Patch(m, prefix: new HarmonyMethod(me.GetMethod(name == "ComputeGameDataSignatureCore" ? nameof(PreGame) : nameof(PreSnap), S)) { priority = Priority.First },
                     postfix: new HarmonyMethod(me.GetMethod(name == "ComputeGameDataSignatureCore" ? nameof(PostGame) : nameof(PostSnap), S)));
          n++;
        }
        var coord = asm.GetType("DropNSpawn.PluginRuntimeWorkCoordinator");
        var q = coord == null ? null : AccessTools.Method(coord, "QueueGameDataRefresh");
        if (q != null) h.Patch(q, prefix: new HarmonyMethod(me.GetMethod(nameof(Invalidate), S)));
        foreach (var t in new[] { typeof(ZNetScene), typeof(ObjectDB) })
        { var aw = AccessTools.Method(t, "Awake"); if (aw != null) h.Patch(aw, prefix: new HarmonyMethod(me.GetMethod(nameof(Invalidate), S))); }
        var cp = AccessTools.Method(typeof(ObjectDB), "CopyOtherDB"); if (cp != null) h.Patch(cp, prefix: new HarmonyMethod(me.GetMethod(nameof(Invalidate), S)));
        Plugin.Log.LogInfo("signature cache: " + n + "/2 patched");
      }

      // --- 2. pickable reconcile skip
      if (Plugin.cPick.Value)
      {
        try
        {
          fPending = AccessTools.Field(odm, "_pendingSnapshotBuild");
          fLastApplied = AccessTools.Field(odm, "_lastAppliedGameDataSignature");
          fSyncReady = AccessTools.Field(odm, "_synchronizedPayloadReady");
          fProfile = AccessTools.Field(odm, "PrefabProfileCatalogState");
          fRuntime = AccessTools.Field(odm, "RuntimeState");
          mTryKinds = fProfile == null ? null : AccessTools.Method(fProfile.FieldType, "TryGetReconcileKinds");
          pActive = fRuntime == null ? null : AccessTools.Property(fRuntime.FieldType, "ActiveEntriesByPrefab");
          pCfgSig = fRuntime == null ? null : AccessTools.Property(fRuntime.FieldType, "ConfigurationSignature");
          var dce = asm.GetType("DropNSpawn.DropConditionEvaluator");
          mHasCond = dce == null ? null : AccessTools.Method(dce, "HasConditions");
          mDeferred = plugin == null ? null : AccessTools.Method(plugin, "IsGameDataRefreshDeferred");
          var rdT = plugin?.GetNestedType("ReloadDomain", BindingFlags.NonPublic | BindingFlags.Public);
          reloadObject = rdT == null ? null : Enum.ToObject(rdT, 1);
          mTrack = AccessTools.Method(odm, "TrackObjectInstance", new[] { typeof(GameObject) });
          var gpn = AccessTools.Method(odm, "GetPrefabName", new[] { typeof(GameObject) });
          if (gpn != null && gpn.ReturnType == typeof(string)) getPrefabName = (Func<GameObject, string>)Delegate.CreateDelegate(typeof(Func<GameObject, string>), gpn);
          var kindT = odm.GetNestedType("LiveObjectComponentKind", BindingFlags.NonPublic | BindingFlags.Public);
          var target = kindT == null ? null : AccessTools.Method(odm, "QueueObjectInstanceReconcile", new[] { typeof(GameObject), kindT });
          bool ok = target != null && fPending != null && fLastApplied != null && fLastApplied.FieldType == typeof(int?) && fSyncReady != null && mTryKinds != null
                    && pActive != null && pCfgSig != null && mHasCond != null && mDeferred != null && reloadObject != null && mTrack != null && getPrefabName != null
                    && kindT != null && Convert.ToInt32(Enum.Parse(kindT, "Pickable")) == 0x40;
          if (ok) { h.Patch(target, prefix: new HarmonyMethod(me.GetMethod(nameof(PreQueue), S))); Plugin.Log.LogInfo("pickable reconcile skip: patched"); }
          else Plugin.Log.LogWarning("pickable reconcile skip: a required member is missing, skipped");
        }
        catch (Exception e) { Plugin.Log.LogWarning("pickable reconcile skip: setup failed, skipped: " + e.Message); }
      }

      // --- 3. CloneDropTable null guard
      if (Plugin.cNull.Value)
      {
        var m = AccessTools.Method(odm, "CloneDropTable", new[] { typeof(DropTable) });
        var cap = AccessTools.Method(odm, "CaptureSnapshot", new[] { typeof(GameObject) });
        if (m != null && m.ReturnType == typeof(DropTable))
        {
          h.Patch(m, prefix: new HarmonyMethod(me.GetMethod(nameof(PreClone), S)));
          if (cap != null) h.Patch(cap, prefix: new HarmonyMethod(me.GetMethod(nameof(PreCapture), S)), postfix: new HarmonyMethod(me.GetMethod(nameof(PostCapture), S)));
          Plugin.Log.LogInfo("CloneDropTable null guard: patched");
        }
        else Plugin.Log.LogWarning("CloneDropTable null guard: target missing, skipped");
      }
    }

    // ================= signature cache =================
    struct Slot { public bool valid; public long key; public int value; public float at; }
    static Slot sGame, sSnap;
    static readonly Stopwatch sw = new Stopwatch();
    static long pendingKey; static bool computing;

    static void Invalidate() { sGame.valid = false; sSnap.valid = false; }

    static long Fingerprint()
    {
      var zs = ZNetScene.instance; var db = ObjectDB.instance;
      if ((object)zs == null || (object)db == null || !zs || !db) return 0;
      unchecked
      {
        long hsh = 1469598103934665603L;
        hsh = (hsh ^ zs.GetInstanceID()) * 1099511628211L;
        hsh = (hsh ^ db.GetInstanceID()) * 1099511628211L;
        hsh = Mix(hsh, zs.m_prefabs); hsh = Mix(hsh, zs.m_nonNetViewPrefabs); hsh = Mix(hsh, db.m_items);
        return hsh == 0 ? 1 : hsh;
      }
    }
    static long Mix(long hsh, List<GameObject> l)
    {
      unchecked
      {
        if (l == null) return hsh * 31;
        hsh = (hsh ^ l.Count) * 1099511628211L;
        for (int i = 0; i < l.Count; i++) { var g = l[i]; hsh = (hsh ^ ((object)g == null ? 0 : g.GetInstanceID())) * 1099511628211L; }
        return hsh;
      }
    }

    static bool Pre(ref Slot s, ref int __result)
    {
      long key = Fingerprint();
      if (key == 0) { s.valid = false; computing = false; return true; }   // game data not ready: original returns 0
      float rv = Plugin.cRevalidate.Value;
      if (s.valid && s.key == key && (rv <= 0f || Time.realtimeSinceStartup - s.at < rv)) { __result = s.value; sigHits++; return false; }
      pendingKey = key; computing = true; sw.Restart(); return true;
    }
    static void Post(ref Slot s, int __result)
    {
      if (!computing) return;
      computing = false; sw.Stop(); sigMiss++; sigMissMs += sw.Elapsed.TotalMilliseconds;
      if (__result == 0) { s.valid = false; return; }
      s.valid = true; s.key = pendingKey; s.value = __result; s.at = Time.realtimeSinceStartup;
    }
    static bool PreGame(ref int __result) => Pre(ref sGame, ref __result);
    static void PostGame(int __result) => Post(ref sGame, __result);
    static bool PreSnap(ref int __result) => Pre(ref sSnap, ref __result);
    static void PostSnap(int __result) => Post(ref sSnap, __result);

    // ================= pickable reconcile skip =================
    static string condCacheSig; static readonly Dictionary<string, bool> condFree = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    static readonly object[] kindArgs = new object[2];

    // return false => skip original (we still track the instance so later config reloads reach it)
    static bool PreQueue(object[] __args)
    {
      try
      {
        if (Convert.ToInt32(__args[1]) != 0x40) return true;               // only Pickable.Awake
        var go = __args[0] as GameObject; if (!go) return true;
        if (fPending.GetValue(null) != null) return true;                   // snapshot build running
        if (!((int?)fLastApplied.GetValue(null)).HasValue) return true;      // prefabs not configured yet this session
        if (!(bool)fSyncReady.GetValue(null)) return true;                   // synced payload not ready
        if ((bool)mDeferred.Invoke(null, new[] { reloadObject })) return true;
        string name = getPrefabName(go); if (string.IsNullOrEmpty(name)) return true;
        var profile = fProfile.GetValue(null);
        kindArgs[0] = name; kindArgs[1] = null;
        if (!(bool)mTryKinds.Invoke(profile, kindArgs) || Convert.ToInt32(kindArgs[1]) != 0x40) return true; // kinds must be exactly Pickable
        if (!ConditionFree(name)) return true;
        var zs = ZNetScene.instance; if (!zs) return true;
        var prefab = zs.GetPrefab(name); if (!prefab || prefab == go) return true;
        var a = go.GetComponent<Pickable>(); var b = prefab.GetComponent<Pickable>();
        if (!a || !b || !Same(a, b)) return true;
        mTrack.Invoke(null, new object[] { go });
        pickSkip++;
        return false;
      }
      catch (Exception e) { Plugin.Log.LogWarning("pickable skip error, falling back: " + e.Message); return true; }
      finally { }
    }

    static bool ConditionFree(string prefab)
    {
      var rt = fRuntime.GetValue(null);
      var sig = pCfgSig.GetValue(rt) as string ?? "";
      if (!string.Equals(sig, condCacheSig, StringComparison.Ordinal)) { condFree.Clear(); condCacheSig = sig; }
      if (condFree.TryGetValue(prefab, out bool r)) return r;
      r = false;
      if (pActive.GetValue(rt) is IDictionary d && d.Contains(prefab) && d[prefab] is IList list && list.Count > 0)
      {
        r = true;
        foreach (var e in list)
        {
          var cp = e?.GetType().GetProperty("Conditions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
          if (e == null || cp == null || (bool)mHasCond.Invoke(null, new[] { cp.GetValue(e) })) { r = false; break; }
        }
      }
      condFree[prefab] = r;
      return r;
    }

    static bool Same(Pickable a, Pickable b)
    {
      if (a.m_itemPrefab != b.m_itemPrefab || a.m_amount != b.m_amount || a.m_minAmountScaled != b.m_minAmountScaled
          || a.m_dontScale != b.m_dontScale || !string.Equals(a.m_overrideName, b.m_overrideName, StringComparison.Ordinal)) return false;
      return SameTable(a.m_extraDrops, b.m_extraDrops);
    }
    static bool SameTable(DropTable x, DropTable y)
    {
      if (x == null || y == null) return x == y;
      if (x.m_dropMin != y.m_dropMin || x.m_dropMax != y.m_dropMax || x.m_dropChance != y.m_dropChance || x.m_oneOfEach != y.m_oneOfEach) return false;
      var p = x.m_drops; var q = y.m_drops;
      if (p == null || q == null) return p == q;
      if (p.Count != q.Count) return false;
      for (int i = 0; i < p.Count; i++)
      {
        var u = p[i]; var v = q[i];
        if (u.m_item != v.m_item || u.m_stackMin != v.m_stackMin || u.m_stackMax != v.m_stackMax || u.m_weight != v.m_weight || u.m_dontScale != v.m_dontScale) return false;
      }
      return true;
    }

    // ================= CloneDropTable guard =================
    [ThreadStatic] static string capturing;
    static readonly HashSet<string> warned = new HashSet<string>();
    static void PreCapture(GameObject prefab) { capturing = prefab ? prefab.name : "?"; }
    static void PostCapture() { capturing = null; }
    static bool PreClone(DropTable source, ref DropTable __result)
    {
      if (source != null && source.m_drops != null) return true;
      nullGuards++;
      string who = capturing ?? "(runtime)";
      if (warned.Add(who)) Plugin.Log.LogInfo("CloneDropTable guard: " + (source == null ? "null DropTable" : "DropTable with null m_drops") + " on " + who + " (DropNSpawn would have thrown NRE)");
      if (source == null) { __result = null; return false; }
      __result = new DropTable { m_dropMin = source.m_dropMin, m_dropMax = source.m_dropMax, m_dropChance = source.m_dropChance, m_oneOfEach = source.m_oneOfEach, m_drops = new List<DropTable.DropData>() };
      return false;
    }

    // ---- 1.0.1 pacing of PluginRuntimeWorkCoordinator.ProcessUpdateFrame
    static int skipFrames; static long paceSkipped, slowSteps, deferredFrames; static double slowMs, maxMs; static readonly Stopwatch psw = new Stopwatch(); static float readySince = -1f;
    public static void InitPacing(Harmony h, Assembly asm)
    {
      var t = asm.GetType("DropNSpawn.PluginRuntimeWorkCoordinator"); var m = t != null ? AccessTools.Method(t, "ProcessUpdateFrame") : null;
      if (m == null) { Plugin.Log.LogWarning("pacing: PluginRuntimeWorkCoordinator.ProcessUpdateFrame not found; pacing inactive"); return; }
      h.Patch(m, prefix: new HarmonyMethod(typeof(DNS).GetMethod(nameof(PacePre), BindingFlags.Static | BindingFlags.NonPublic)), finalizer: new HarmonyMethod(typeof(DNS).GetMethod(nameof(PaceFin), BindingFlags.Static | BindingFlags.NonPublic)));
      Plugin.Log.LogInfo("pacing: patched ProcessUpdateFrame");
    }
    static bool Ready() { var p = Player.m_localPlayer; if (p == null || p.IsTeleporting()) return false; var hud = Hud.instance; if (hud != null) { try { var ls = Traverse.Create(hud).Field("m_loadingScreen"); var c = ls.GetValue() as Component; if (c != null && c.gameObject.activeInHierarchy && ls.Property("alpha").GetValue<float>() > 0.01f) return false; } catch { } } return true; }
    static bool PacePre()
    {
      bool ready = Ready(); float now = Time.realtimeSinceStartup;
      if (!ready) { readySince = -1f; skipFrames = 0; } else if (readySince < 0f) readySince = now;
      if (Plugin.cDefer.Value && ZNet.instance != null && !ZNet.instance.IsServer() && (!ready || now - readySince < Plugin.cDeferSec.Value)) { deferredFrames++; return false; }
      if (Plugin.cPace.Value && ready && skipFrames > 0) { skipFrames--; paceSkipped++; return false; }
      psw.Restart(); return true;
    }
    static Exception PaceFin(Exception __exception)
    {
      if (psw.IsRunning) { psw.Stop(); double ms = psw.Elapsed.TotalMilliseconds; if (ms > maxMs) maxMs = ms;
        if (Plugin.cPace.Value && ms > Plugin.cSlice.Value) { slowSteps++; slowMs += ms; if (Ready()) skipFrames = (int)Math.Ceiling(ms / Plugin.cSlice.Value); } }
      return __exception;
    }
    public static void LogStats()
    {
      if (slowSteps + paceSkipped + deferredFrames > 0) Plugin.Log.LogInfo("pacing: slow steps=" + slowSteps + " (avg " + (slowSteps > 0 ? (slowMs / slowSteps).ToString("F1") : "-") + " ms, max " + maxMs.ToString("F1") + " ms) frames skipped=" + paceSkipped + " deferred=" + deferredFrames);
      long tot = sigHits + sigMiss + pickSkip + nullGuards;
      if (tot == lastTotal) return; lastTotal = tot;
      Plugin.Log.LogInfo("stats: signature cache hits=" + sigHits + " misses=" + sigMiss + " (avg miss " + (sigMiss > 0 ? (sigMissMs / sigMiss).ToString("F2") : "-") + " ms) | pickable reconciles skipped=" + pickSkip + " | CloneDropTable guards=" + nullGuards);
    }
  }
}
