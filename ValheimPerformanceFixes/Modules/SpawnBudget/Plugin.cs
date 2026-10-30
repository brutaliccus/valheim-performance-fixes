using System; using System.Collections.Generic; using System.Diagnostics; using System.IO; using System.Text;
using BepInEx; using BepInEx.Configuration; using HarmonyLib; using UnityEngine;
namespace SpawnBudget {
// SpawnBudget 1.2.0 (client-only).
//  1.1.0 patched ZNetScene.CreateObjectsSorted/CreateDistantObjects, but FiresGhettoNetworkMod ("Enable Time-Slice Instantiation")
//  prefixes ZNetScene.CreateObjects and skips the original in-game, so those two methods were only ever called during the
//  loading screen (where SpawnBudget deliberately bails). Result: the budget never engaged and nothing was logged.
//  1.2.0 prefixes ZNetScene.CreateObjects itself, FIRST (before FGN), does a vanilla-ordered near+distant creation under one
//  shared per-frame time budget, then hands later prefixes empty lists and skips the original (so nothing runs twice).
//  Heightmap.CustomLateUpdate slicing unchanged. Periodic STATS line every 60 s (always written, proves it is alive).
//  1.3.0: gate on what is on screen: vanilla speed while the loading screen is fully opaque (no player / dead / teleporting),
//  budget as soon as the world is visible (post-teleport/respawn tail tracked separately in STATS). Every Heightmap.Regenerate
//  of an already-built heightmap beyond ProtectRadius (TerrainComp Poke(0), OnEnable re-enable, ...) is converted into the sliced
//  delayed path; first-time generation (no collider yet) is never deferred. Whole ZNetScene.CreateObject timed from a
//  Priority.First prefix to a Priority.Last postfix (includes every mod's patches), per-prefab totals with owning mod/bundle.
//  1.4.0: was never silent - it only wrote BepInEx/SpawnBudget.log; key lines now mirrored to LogOutput.log. Skips entirely on a
//  headless/batch-mode process. Serializes LocationProxy spawns (vanilla delay hook), throttles client zone spawns when the frame
//  budget is gone, and spreads client dungeon room placement (DungeonGenerator.Spawn) across frames under RoomBudgetMs.
//  1.5.2: heavy client locations (learned > HeavyLocationMs, or listed) are instantiated with the root inactive in frame N and
//  activated (Awake/OnEnable + SnapToGround) in frame N+1 via a transpiler on ZoneSystem.SpawnLocation's SetActive(true).
//  1.5.1: zone throttle, location hold/prewarm and room-load stagger only act once the local player has spawned and the
//         loading screen has been gone for a grace period (Settled()); vanilla behaviour during load. Hold-one-frame is bounded.
//  1.5.0: dungeon spreading now coexists with DropNSpawn (its only DungeonGenerator.Spawn patch pushes a location-spawn context;
//  we push/pop the same context around every PlaceRoom batch). Room prefab async loads are staggered (a few unique assets in
//  flight at once) so main-thread asset integration is spread. Location spawns are held one frame after their asset finished
//  loading (integration frame != instantiate frame), location assets are pre-requested from the pending ZDO queue, SLOW lines
//  name the location. Zone throttle now uses previous-frame signals (it could never see same-frame work before).
//  1.7.5: SoftCapBudgetMs default 10, HeavyLocationTransforms 80, MaxRoomLoadsInFlight default 1;
//         SoftCap LocationProxy path when proxy CreateObject > SoftCapProxyCreateMs (~12);
//         overlap guard: SoftCap activation, InstantiateAsync completion, zone spawn, dungeon SoftEntry = one heavy job/frame;
//         PreInstantiate starts during arrival/teleport screen; per-location SoftCap/SnapToGround ms logs + heavyFrame owner.
//  1.7.4: heavy-location soft-cap (activate + SnapToGround split across frames under SoftCapBudgetMs; LocationDeferIfFrameOverMs default 15);
//         PreInstantiateHeavy: Unity InstantiateAsync during approach for heavy LocationProxy/ZoneSystem locations (MWL/overworld POIs),
//         so the spawn frame reuses a ready inactive clone instead of paying SoftReferenceableAssets.Instantiate (atomic, 100ms+);
//         dungeon SoftEntry (stagger+spread with SoftEntryWallMs hard wall ~18 ms, MaxRoomLoadsInFlight forced to 1, engages as soon as world is visible);
//         backlog-aware distant skip (skip distant while NearBacklogThreshold nearby objects still waiting); HeavyLocationTransforms default 100.
//  1.7.3: arrival budget (teleport/respawn black screen with a live player: finite nearest-first budget instead of vanilla 100+/frame, then a ramp),
//         distant objects nearest-first with DistantMaxPerFrame, LocationMaxPerFrame + LocationDeferIfFrameOverMs, pendDist counts only
//         un-created non-stale entries, edge-kept logging deduped per ZDO (with id/position, same-position duplicate warning),
//         combat hysteresis (CombatEnterHoldSec / CombatExitDelaySec).
//  1.7.2: location edge hysteresis (see "1.7.2 location edge hysteresis" below), duplicate spawn guard per LocationProxy, startup
//  log lines report the real version (Plugin.Ver).
//  2.1.1: save safety ("Spawn Budget - Safety"): FlushBeforeSave finishes every build this module is still spreading over
//  frames before ZNet.SaveWorld / ZNet.Save / ZNet.Shutdown / Game.Shutdown; CommitOnComplete keeps a zone that is still
//  being generated out of the generated-zones and placed-locations lists of a save (ZoneSystem.PrepareSave clones only);
//  HostSpawnsAtOnce makes "the PC that owns the world" an explicit, switchable rule for every split path, and nothing is
//  ever deferred while the world owner is generating a zone (Full/Ghost SpawnZone), whatever the switches say.
public partial class Plugin : global::ValheimPerformanceFixes.ModuleBehaviour {
  public const string Ver = "2.1.1";
  static ConfigEntry<bool> Enabled, SkipDistant, LogSlow, HmEnabled; static ConfigEntry<float> BudgetMs, SlowMs, HmBudgetMs, FrameBudgetMs, HmProtect, ReportSec; static ConfigEntry<int> HmMaxDefer, MaxPerFrame; static ConfigEntry<bool> HmAllPaths; static ConfigEntry<float> TailSec, ListObjMs;
  static readonly Stopwatch frameSw = new Stopwatch(); static int frame = -1; static double spawnMs, hmMs; static int hmRuns;
  static Func<ZNetScene, ZDO, GameObject> createObj; static bool objOk = true, engagedLogged;
  static ConfigEntry<float> PrioRadius, CmbBudget, CmbDetect, CmbExit, CmbEase; static ConfigEntry<int> PrioMax; static ConfigEntry<bool> CmbOn;
  static ConfigEntry<bool> AdOn; static ConfigEntry<float> AdLen, AdAgg, AdLow, AdHigh, AdStr, AdRise, AdFall, AdFrameMs; static int lastPendNear, lastPendDist; static float dtEma = 0.016f, nextAdLog;
  static void UpdatePressure(float dt) {
    if (dt <= 0f) return; float fd = Mathf.Min(Time.unscaledDeltaTime, 0.25f); dtEma += (fd - dtEma) * Mathf.Clamp01(dt / 1f);
    float tgt = AdFrameMs.Value / 1000f; float frame = Mathf.Clamp01((dtEma - tgt) / tgt);
    float pend = Mathf.Max(Mathf.Clamp01(lastPendNear / 150f), 0.7f * Mathf.Clamp01(lastPendDist / 600f));
    float queue = Mathf.Clamp01(zoneDefer / 5f + locDeferRun / 10f);
    int ni = 0; try { ni = ZNetScene.instance != null ? ZNetScene.instance.NrOfInstances() : 0; } catch { }
    float inst = Mathf.Clamp01((ni - 2000f) / 6000f);
    float raw = Mathf.Clamp01(0.5f * pend + 0.15f * queue + 0.2f * frame + 0.15f * inst);
    float p = SpawnBudgetPressure.Value; float tau = raw > p ? AdRise.Value : AdFall.Value;
    SpawnBudgetPressure.Value = p + (raw - p) * Mathf.Clamp01(dt / tau); SpawnBudgetPressure.Raw = raw; SpawnBudgetPressure.Instances = ni; SpawnBudgetPressure.PendingNear = lastPendNear; SpawnBudgetPressure.PendingDistant = lastPendDist;
  }
  static float AdT() { float lo = AdLow.Value, hi = Mathf.Max(AdHigh.Value, lo + 0.01f); return Mathf.Clamp01((SpawnBudgetPressure.Value - lo) / (hi - lo)) * AdStr.Value; }
  static ConfigEntry<bool> ArOn; static ConfigEntry<float> ArBudget, ArRamp, PendStale, LocOverMs, CmbEnterHold, CmbExit2; static ConfigEntry<int> ArDistMax, DistMax, LocMaxPF;
  static float cmbAiSince = -1f, cmbEnterAt, arrivalEndAt = -999f; static string cmbWhy = "";
  static int sArrFrames, sArrObjs, sArrHit, sArrEntered, sLocDupTracked, sLocSamePos, sLocCapDelayed, sLocSlowFrameDelayed, lastPendDistStale; static double sArrMax; static bool arrivalWas;
  static readonly Dictionary<ZDO, float> distSeen = new Dictionary<ZDO, float>(), distSeen2 = new Dictionary<ZDO, float>(); static float nextPendCount;
  static readonly List<ZDO> tmpD = new List<ZDO>();
  static void CountPendDist(List<ZDO> dist) {
    float t = Time.unscaledTime; if (t < nextPendCount) return; nextPendCount = t + 0.5f;
    if (dist == null) { lastPendDist = 0; return; }
    float stale = PendStale.Value; int n = 0, st = 0; distSeen2.Clear();
    for (int i = 0; i < dist.Count; i++) { var z = dist[i]; if (z == null || z.Created) continue;
      if (!distSeen.TryGetValue(z, out float f0)) f0 = t; distSeen2[z] = f0;
      if (stale > 0f && t - f0 > stale) st++; else n++; }
    distSeen.Clear(); foreach (var kv in distSeen2) distSeen[kv.Key] = kv.Value; distSeen2.Clear();
    lastPendDist = n; lastPendDistStale = st;
  }
  static bool ArrivalOk() {
    try { if (ZNet.instance == null || OwnerAtOnce()) return false; var p = Player.m_localPlayer; return p != null && !p.IsDead() && ZoneSystem.instance != null; } catch { return false; }
  }
  static float cmbF, cmbLastSig = -999f, cmbNext, cmbLastHit = -999f, cmbLastT; static bool cmbIn; static int sCombatFrames, sPrio;
  static void DmgPost(Character __instance, HitData hit) { try { var lp = Player.m_localPlayer; if (lp != null && (__instance == lp || (hit != null && hit.GetAttacker() == lp))) cmbLastHit = Time.unscaledTime; } catch { } }
  static double EffBudget() {
    float t = Time.unscaledTime; float dt = Mathf.Clamp(t - cmbLastT, 0f, 0.5f); cmbLastT = t; UpdatePressure(dt);
    double baseMs = AdOn.Value ? Mathf.Lerp(AdLen.Value, AdAgg.Value, AdT()) : BudgetMs.Value; SpawnBudgetPressure.BudgetMs = (float)baseMs;
    if (t >= nextAdLog) { nextAdLog = t + 30f; Log("ADAPTIVE pressure=" + SpawnBudgetPressure.Value.ToString("0.00") + " raw=" + SpawnBudgetPressure.Raw.ToString("0.00") + " pendNear=" + lastPendNear + " pendDist=" + lastPendDist + "(+" + lastPendDistStale + " stale)" + " inst=" + SpawnBudgetPressure.Instances + " frame=" + (dtEma * 1000f).ToString("0.0") + "ms budget=" + baseMs.ToString("0.0") + "ms mode=" + (cmbIn ? "COMBAT" : "normal")); }
    if (!CmbOn.Value) { cmbIn = false; cmbF = 0f; return baseMs; }
    if (t >= cmbNext) { cmbNext = t + 0.25f; bool sig = t - cmbLastHit < 1f; var lp = Player.m_localPlayer;
      if (!sig && lp != null) { Vector3 pp = lp.transform.position; float r2 = CmbDetect.Value * CmbDetect.Value;
        foreach (var c in Character.GetAllCharacters()) { if (c == null || c.IsPlayer() || c.IsTamed() || c.IsDead()) continue; if ((c.transform.position - pp).sqrMagnitude > r2) continue; var ai = c.GetBaseAI(); if (ai != null && ai.IsAlerted() && BaseAI.IsEnemy(c, lp)) { sig = true; break; } } }
      bool hitSig = t - cmbLastHit < 1f, aiSig = sig && !hitSig;
      if (aiSig) { if (cmbAiSince < 0f) cmbAiSince = t; } else if (!hitSig) cmbAiSince = -1f;
      bool now;
      if (cmbIn) { if (sig) cmbLastSig = t; now = lp != null && t - cmbLastSig < CmbExit2.Value; }
      else { now = lp != null && (hitSig || (aiSig && t - cmbAiSince >= CmbEnterHold.Value)); if (now) { cmbLastSig = t; cmbWhy = hitSig ? "damage" : "alerted hostile for " + (t - cmbAiSince).ToString("0.0") + "s"; } }
      if (now != cmbIn) { cmbIn = now; if (now) { cmbEnterAt = t; Log("combat mode ENTER (" + cmbWhy + ")"); } else Log("combat mode EXIT after " + (t - cmbEnterAt).ToString("0") + "s (no signal for " + CmbExit2.Value.ToString("0") + "s)"); } }
    cmbF = Mathf.MoveTowards(cmbF, cmbIn ? 1f : 0f, dt / CmbEase.Value); if (cmbF > 0f) sCombatFrames++;
    SpawnBudgetPressure.Combat = cmbIn; return Mathf.Lerp((float)baseMs, Mathf.Min((float)baseMs, CmbBudget.Value), cmbF);
  }
  static readonly List<ZDO> tmp = new List<ZDO>(); static readonly List<ZDO> empty = new List<ZDO>();
  static readonly Dictionary<Heightmap, int> deferred = new Dictionary<Heightmap, int>();
  static int sBudgetHitFrames, sNearDeferred, sDistantSkips, sHmDeferrals, sHmForced, sHmRuns, sObjs, sActiveFrames, sOverOne; static double sHmMax, sHmTotal, sMaxSpawn, sSpawnTotal; static float nextReport;
  static StreamWriter log; static readonly Stopwatch objSw = new Stopwatch(); static readonly Comparison<ZDO> cmp = Cmp;
  static ConfigEntry<bool> Mirror, DgSpread, LocSerialize, ZoneThrottle; static ConfigEntry<float> DgBudgetMs, LocBudgetMs; static ConfigEntry<int> LocMaxDefer, ZoneMaxDefer, DgMaxLoads; static ConfigEntry<bool> DgStagger, LocHoldFrame, LocPrewarm; static ConfigEntry<float> ZoneHeavyMs, SettleGrace; static ConfigEntry<bool> LocSplit, LocSoftCap, LocPreInst, DgSoftEntry, DistBacklogAware; static ConfigEntry<float> LocHeavyMs, LocSoftCapMs, LocProxySoftMs, DgWallMs; static ConfigEntry<int> LocHeavyPieces, NearBacklog; static readonly Dictionary<int, int> locPieces = new Dictionary<int, int>(); static ConfigEntry<string> LocHeavyList; static readonly List<GameObject> locPendingSnap = new List<GameObject>(); static readonly Dictionary<int, string> locPendingNames = new Dictionary<int, string>(); static readonly HashSet<int> proxyHeavyHashes = new HashSet<int>();
  static BepInEx.Logging.ManualLogSource BLog; static Plugin inst;
  const string SBS = global::ValheimPerformanceFixes.Sections.SB;
  static ConfigEntry<bool> WbOn, DgPerInstance, BlKeep, RmGuardOnly, RmCapOn, HyLight; static ConfigEntry<float> WbMs, BlMeters; static ConfigEntry<int> BlFrames, RmCapN;
  static ConfigEntry<bool> SfFlush, SfCommit, SfOwner, SfBuilds;
  // 2.1.1 phase 3
  static ConfigEntry<bool> SfExtFirst, SfDgInterior, DgStrict, DgSnapOwn, DgLearn, DgGlobalFlight, DgBudgetIssue, DgMeasureLand, HyDg, HitchOn; static ConfigEntry<int> DgMaxWait; static ConfigEntry<float> HitchMs;
  // ---- 2.1.0 shared per-frame world-building budget ----
  static int wbFrame = -1, wbJobs, sWbDeferred, sWbJobs; static double wbUsed, sWbMaxFrame;
  static void WbRoll() { int f = Time.frameCount; if (wbFrame != f) { if (wbUsed > sWbMaxFrame) sWbMaxFrame = wbUsed; wbFrame = f; wbUsed = 0; wbJobs = 0; } }
  static bool WbActive() { return WbOn != null && WbOn.Value && Settled(); }
  // true = a world-building job may start now (first job of the frame always may)
  static bool WbCanStart() { if (!WbActive()) return true; WbRoll(); if (wbJobs == 0 || wbUsed < WbMs.Value) return true; sWbDeferred++; return false; }
  static double WbLeft() { if (!WbActive()) return double.MaxValue; WbRoll(); return Math.Max(0.0, WbMs.Value - wbUsed); }
  static void WbCharge(double ms) { if (WbOn == null || !WbOn.Value) return; WbRoll(); wbUsed += ms; wbJobs++; sWbJobs++; }
  void Awake() {
    inst = this; BLog = Logger;
    if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { Logger.LogInfo("SpawnBudget " + Ver + ": headless/server process detected, not patching anything."); return; }
    Mirror = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "MirrorToLogOutput", true, "Also write patch status, ENGAGED and the STATS headline to BepInEx LogOutput.log (full detail stays in BepInEx/SpawnBudget.log).");
    DgSpread = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "SpreadRooms", false, "EXPERIMENTAL, off by default. Client only, world visible: place a dungeon's rooms over several frames (RoomBudgetMs per frame, >=1 room/frame) instead of all in one frame. Rooms are seeded per position, so the result is identical; the zone stays 'loading' until done (as vanilla during asset load). Disabled automatically if another mod patches DungeonGenerator.Spawn.");
    DgStagger = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "StaggerRoomLoads", true, "Client, world visible: issue a dungeon's room prefab async loads a few unique assets at a time instead of all at once, so Unity integrates them on the main thread over several frames. Room list/positions come from the ZDO (unchanged); only load timing changes.");
    DgUnSpread = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "SpreadDungeonUnloads", true, "When several dungeon interiors leave the area at once, let them go one at a time (DungeonUnloadGapFrames apart, at most 2 s later than vanilla) so their destroy cost does not land in one frame.");
    DgUnGap = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "DungeonUnloadGapFrames", 5, new ConfigDescription("Frames between two dungeon interior unloads when SpreadDungeonUnloads is on.", new AcceptableValueRange<int>(1, 60)));
    DgVerify = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "VerifyRoomsAfterStagger", true, "After a dungeon's room loads were staggered, check that every planned room was built and is switched on; missing rooms are built at once (vanilla PlaceRoom), switched-off ones switched on, and both are logged. A dungeon still waiting for rooms after 20 s is finished.");
    DgStagModded = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "StaggerModdedDungeons", true, "Also stagger room loads of dungeons that are not in VanillaDungeonNames (mod dungeons). Off = mod dungeons load their rooms the vanilla way.");
    DgVanillaNames = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "VanillaDungeonNames", "DG_ForestCrypt,DG_SunkenCrypt,DG_Cave,DG_DvergrTown,DG_DvergrBoss,DG_Hildir,DG_MeadowsFarm,DG_MeadowsVillage,DG_PlainsFortress,DG_AshlandsCastle,DG_CharredFortress,DG_PutridHole", "Comma list of dungeon generator name prefixes treated as vanilla by StaggerModdedDungeons (a name rule: edit it if a vanilla dungeon is named differently).");
    DgStagExclude = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "StaggerExcludeList", "", "Comma list; a dungeon whose generator or location name contains one of these (any case) loads its rooms the vanilla way. Empty = none.");
    DgMaxLoads = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "MaxRoomLoadsInFlight", 1, new ConfigDescription("Unique room prefabs loading at the same time while staggering. Default 1 so SoftEntry crypt loads do not stack EarlyUpdate spikes.", new AcceptableValueRange<int>(1, 16)));
    LocHoldFrame = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "HoldOneFrameAfterLoad", true, "When a location's asset has just finished loading, delay its instantiate to the next frame so asset integration and instantiate do not land in the same frame.");
    LocPrewarm = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "PrewarmFromQueue", true, "Request the location asset load (vanilla PokeCanSpawnLocation via ShouldDelayProxyLocationSpawning) as soon as a LocationProxy ZDO is in the pending creation queue, before the proxy is instantiated.");
    LocSplit = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SplitHeavyLocationActivation", true, "Client, settled in-game only: a heavy location is instantiated inactive in one frame and activated (Awake/OnEnable, renderers, SnapToGround) in the next frame, so its cost is spread over 2 frames. The next location is held until the activation is done.");
    LocHeavyMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "HeavyLocationMs", 8f, "Generic rule for ALL locations (vanilla and modded): a location whose last unsplit spawn took longer than this is treated as heavy (learned per session).");
    LocHeavyPieces = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "HeavyLocationTransforms", 80, "A location prefab with more than this many child transforms is treated as heavy from its first spawn (0 = split every location). Lower = more POIs split across frames; default 80 covers more swamp/MWL buildings.");
    LocHeavyList = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "HeavyLocations", "", "Optional extra names (normally empty; the ms/transform rules already cover every location).");
    LocSoftCap = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SoftCapHeavyActivation", true, "After a heavy location is instantiated inactive, activate it under SoftCapBudgetMs and defer SnapToGround to the next frame so activation does not hitch. Off = old 2-frame split only.");
    LocPreInst = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "PreInstantiateHeavy", true, "For heavy overworld LocationProxy locations, start Unity InstantiateAsync while approaching AND during arrival/teleport screens so SoftReferenceableAssets.Instantiate is not paid on the spawn frame. Spawn waits (up to MaxDeferFrames) for the clone.");
    LocSoftCapMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SoftCapBudgetMs", 10f, new ConfigDescription("Per-frame ms budget for deferred heavy-location activation (Awake/OnEnable). SnapToGround runs on the following frame. Default 10.", new AcceptableValueRange<float>(4f, 40f)));
    LocProxySoftMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SoftCapProxyCreateMs", 12f, new ConfigDescription("If creating a LocationProxy (or its location spawn) took longer than this many ms, SoftCap that location on the next activation path so Awake/SnapToGround do not stack on the same hitch.", new AcceptableValueRange<float>(4f, 40f)));
    DgSoftEntry = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "SoftEntry", true, "When entering a crypt/dungeon, stagger room asset loads AND place rooms across frames with SoftEntryWallMs as a hard per-frame wall (~18 ms) so opening a crypt does not freeze for hundreds of ms. Starts as soon as the world is visible (loads before you can reach the next rooms). Independent of SpreadRooms; leave SpreadRooms off unless you want spread without SoftEntry.");
    DgWallMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "SoftEntryWallMs", 18f, new ConfigDescription("Hard per-frame wall (ms) for SoftEntry room loads and room placement. Aim: no dungeon wall above ~20 ms.", new AcceptableValueRange<float>(6f, 40f)));
    DistBacklogAware = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "BacklogAwareDistantSkip", true, "Also skip distant object creation (LocationProxy etc.) while many nearby objects are still waiting (NearBacklogThreshold), so nearby scenery fills in first.");
    NearBacklog = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "NearBacklogThreshold", 40, new ConfigDescription("With BacklogAwareDistantSkip, skip distant creation while at least this many nearby objects are still waiting to appear.", new AcceptableValueRange<int>(5, 300)));
    HyOn = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "Enabled", true, "Client, settled in-game only: stop the unload/reload loop of locations at the rim of the loaded area when you move back and forth over a zone line (fights, kiting). A location that just left the area stays loaded for KeepLoadedSec if it is within KeepExtraZones of the rim, and one that was unloaded anyway is not re-created for ReloadCooldownSec unless it is within AlwaysReloadWithinMeters. Nearby objects are never affected.");
    HyKeepSec = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "KeepLoadedSec", 30f, new ConfigDescription("Seconds a location that left the loaded area is kept loaded before it is unloaded (0 = unload immediately, vanilla).", new AcceptableValueRange<float>(0f, 300f)));
    HyExtra = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "KeepExtraZones", 1, new ConfigDescription("Only keep a location loaded while it is at most this many zones (64 m) beyond the vanilla loaded area.", new AcceptableValueRange<int>(0, 3)));
    HyCooldown = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "ReloadCooldownSec", 10f, new ConfigDescription("After a location was unloaded, do not re-create it for this many seconds unless you are close to it (0 = off).", new AcceptableValueRange<float>(0f, 120f)));
    HyClose = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "AlwaysReloadWithinMeters", 128f, new ConfigDescription("A location within this distance (XZ) of you is always re-created immediately, cooldown or not.", new AcceptableValueRange<float>(0f, 500f)));
    SettleGrace = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "SettleGraceSec", 10f, new ConfigDescription("Zone throttle / location hold+prewarm / room-load stagger stay vanilla until the player has spawned and the loading screen has been gone this long.", new AcceptableValueRange<float>(0f, 120f)));
    ZoneHeavyMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Zones", "PostponeIfLastFrameOverMs", 40f, "Postpone a client zone spawn if the previous frame took longer than this (or spent the object budget, or spawned a location).");
    DgBudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "RoomBudgetMs", 8f, new ConfigDescription("Per-frame ms for dungeon room placement when SoftEntry/SpreadRooms is on. SoftEntry also clamps to SoftEntryWallMs.", new AcceptableValueRange<float>(1f, 50f)));
    LocSerialize = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SerializeProxySpawns", true, "Client only, world visible: at most one LocationProxy location spawn per frame, and none while this frame's object budget is spent or a dungeon is being spread (uses vanilla's own ShouldDelayProxyLocationSpawning retry).");
    LocBudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "DelayWhenFrameSpawnOverMs", 6f, "Delay a location spawn if object creation this frame already exceeded this.");
    LocMaxDefer = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "MaxDeferFrames", 30, "After this many consecutive delayed frames one location is let through regardless.");
    ZoneThrottle = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Zones", "ThrottleClientZoneSpawns", true, "Client only, world visible: postpone ZoneSystem.SpawnZone (client mode) to a later tick if a zone or location already spawned this frame or the object budget is spent (vanilla retries every 0.1 s).");
    ZoneMaxDefer = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Zones", "MaxDeferTicks", 10, "After this many consecutive postponed attempts a zone spawns regardless.");
    Enabled = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "Enabled", true, "Limit time spent creating objects per frame while in-game (never during the loading screen, never on a server).");
    BudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "BudgetMs", 6f, new ConfigDescription("Per-frame milliseconds for object creation (near + distant combined). At least one near object is always created per frame.", new AcceptableValueRange<float>(1f, 50f)));
    PrioRadius = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "PriorityRadius", 40f, new ConfigDescription("Objects within this many meters of you are never deferred by the time budget or count cap (up to PriorityMaxPerFrame per frame); spreading only happens farther out. Live. 0 = off.", new AcceptableValueRange<float>(0f, 100f)));
    PrioMax = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "PriorityMaxPerFrame", 40, new ConfigDescription("Safety cap for priority-radius objects created in one frame.", new AcceptableValueRange<int>(5, 200), new ConfigurationManagerAttributes { IsAdvanced = true }));
    AdOn = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "Enabled", true, "Out of combat, scale the per-frame object budget with load pressure (pending near/distant objects, postponed zones/locations, frame time, active instance count -> 0..1, smoothed). Low pressure = LenientBudgetMs (near vanilla), high = AggressiveBudgetMs. Replaces General/BudgetMs while on. PriorityRadius objects are never deferred either way.");
    AdLen = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "LenientBudgetMs", 10f, new ConfigDescription("Object creation ms/frame at low pressure.", new AcceptableValueRange<float>(2f, 50f)));
    AdAgg = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "AggressiveBudgetMs", 4f, new ConfigDescription("Object creation ms/frame at high pressure.", new AcceptableValueRange<float>(1f, 50f)));
    AdLow = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "LowPressure", 0.25f, new ConfigDescription("Pressure at/below which the lenient value is used.", new AcceptableValueRange<float>(0f, 1f)));
    AdHigh = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "HighPressure", 0.75f, new ConfigDescription("Pressure at/above which the aggressive value is used.", new AcceptableValueRange<float>(0f, 1f)));
    AdStr = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "Strength", 1f, new ConfigDescription("How far toward the aggressive value full pressure goes (0 = always lenient, 1 = all the way).", new AcceptableValueRange<float>(0f, 1f)));
    AdRise = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "RiseSec", 4f, new ConfigDescription("Smoothing time (EMA) when pressure rises.", new AcceptableValueRange<float>(0.5f, 30f)));
    AdFall = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "FallSec", 10f, new ConfigDescription("Smoothing time (EMA) when pressure falls (slower = no flip-flopping while traveling).", new AcceptableValueRange<float>(0.5f, 60f)));
    AdFrameMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "TargetFrameMs", 16.7f, new ConfigDescription("Frame time treated as 'no headroom left' starts above this (16.7 = 60 fps).", new AcceptableValueRange<float>(5f, 50f), new ConfigurationManagerAttributes { IsAdvanced = true }));
    CmbOn = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "Enabled", true, "While in combat (alerted hostile within DetectRange, or you took/dealt damage) use CombatBudgetMs for object creation beyond PriorityRadius, so spawning far stuff does not steal frame time mid-fight. Checked 4x/s; eases back over EaseSec after ExitDelaySec.");
    CmbBudget = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "CombatBudgetMs", 3f, new ConfigDescription("Per-frame object creation ms while in combat (normal: General/BudgetMs).", new AcceptableValueRange<float>(1f, 50f)));
    CmbDetect = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "DetectRange", 30f, new ConfigDescription("Alerted hostiles within this many meters count as combat.", new AcceptableValueRange<float>(10f, 80f)));
    CmbExit = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "ExitDelaySec", 5f, new ConfigDescription("Ignored (kept so existing configs stay valid); see CombatExitDelaySec.", new AcceptableValueRange<float>(1f, 30f)));
    CmbEase = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "EaseSec", 3f, new ConfigDescription("Seconds to blend between normal and combat budget.", new AcceptableValueRange<float>(0.5f, 10f)));
    SkipDistant = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "DeferDistantWhenOverBudget", true, "Skip distant-object creation once the frame budget is spent.");
    MaxPerFrame = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "MaxObjectsPerFrame", 10, "Count cap per frame (vanilla 10; near cap is max(pending/100, this) like vanilla). The time budget usually stops earlier.");
    LogSlow = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "LogSlowObjects", true, "Log single objects whose creation exceeds SlowObjectMs to BepInEx/SpawnBudget.log (+ a top list every 60 s).");
    SlowMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "SlowObjectMs", 3f, "Unused (kept so existing configs stay valid); see ListObjectsOverMs.");
    ReportSec = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "ReportIntervalSeconds", 60f, "Interval of the STATS summary line.");
    HmEnabled = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "Enabled", true, "Time-slice delayed terrain (Heightmap) rebuilds so they don't stack with each other or with object spawns.");
    HmBudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "HeightmapBudgetMs", 4f, "After the first rebuild of a frame, further rebuilds only run while heightmap time this frame is below this.");
    FrameBudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "CombinedFrameBudgetMs", 10f, "Defer a (non-protected) rebuild if object creation + heightmap rebuilds already used this much of the frame.");
    HmProtect = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "ProtectRadius", 96f, "Heightmaps whose center is within this many metres (XZ) of the player are never deferred.");
    HmMaxDefer = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "MaxDeferFrames", 20, "A heightmap is never deferred more than this many frames in a row.");
    HmAllPaths = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "SliceAllRegeneratePaths", true, "Also route immediate rebuilds (TerrainComp load / Poke(0), re-enable) of already-built heightmaps beyond ProtectRadius through the sliced path. Heightmaps that have never been built (no collider) are never deferred.");
    TailSec = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "TailSeconds", 15f, "Seconds after a loading screen / teleport / respawn ends that count as the 'tail' in STATS.");
    ArOn = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Arrival", "Enabled", true, "Client only: while a teleport/respawn screen is up and the local player exists and is alive, create objects nearest-first within ArrivalBudgetMs per frame instead of vanilla's 100+ objects per frame (fewer 100-200 ms frames). The first world load (no player yet) stays vanilla. Can make the black screen last slightly longer because the game waits for nearby objects.");
    ArBudget = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Arrival", "ArrivalBudgetMs", 25f, new ConfigDescription("Object creation ms/frame during the arrival screen.", new AcceptableValueRange<float>(5f, 200f)));
    ArRamp = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Arrival", "ArrivalRampSec", 4f, new ConfigDescription("After the screen clears, blend from ArrivalBudgetMs down to the normal budget over this many seconds (0 = switch at once).", new AcceptableValueRange<float>(0f, 30f)));
    ArDistMax = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Arrival", "ArrivalDistantMaxPerFrame", 40, new ConfigDescription("Distant objects (location proxies etc.) created per frame during the arrival screen.", new AcceptableValueRange<int>(1, 500)));
    DistMax = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "DistantMaxPerFrame", 10, new ConfigDescription("In-game cap on distant objects (LocationProxy etc.) created per frame; they are now created nearest-first. The time budget still applies.", new AcceptableValueRange<int>(1, 200)));
    PendStale = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "PendDistStaleSec", 30f, new ConfigDescription("A distant object that stayed un-created this long (invalid prefab, suppressed reload, zone not ready) stops counting toward pendDist pressure (0 = count all un-created).", new AcceptableValueRange<float>(0f, 600f)));
    LocMaxPF = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "LocationMaxPerFrame", 1, new ConfigDescription("At most this many LocationProxy location spawns per frame (also during the arrival screen once the player exists).", new AcceptableValueRange<int>(1, 10)));
    LocOverMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "LocationDeferIfFrameOverMs", 15f, new ConfigDescription("In-game, delay a location spawn while the previous frame took longer than this (still bounded by MaxDeferFrames; 0 = off). Default 15 so heavy swamp locations do not stack on a slow frame.", new AcceptableValueRange<float>(0f, 200f)));
    CmbEnterHold = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "CombatEnterHoldSec", 3f, new ConfigDescription("An alerted-hostile signal must last this long before combat mode starts (taking or dealing damage enters at once).", new AcceptableValueRange<float>(0f, 15f)));
    CmbExit2 = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "CombatExitDelaySec", 12f, new ConfigDescription("Seconds without any combat signal before combat mode ends (replaces ExitDelaySec).", new AcceptableValueRange<float>(1f, 60f)));
    ListObjMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "ListObjectsOverMs", 10f, "STATS lists every prefab with a single creation over this many ms (whole CreateObject incl. all mods' patches).");
    // 2.1.0 additions
    WbOn = Config.Bind(SBS + "World Budget", "Enabled", true, "Share ONE time budget per frame between everything that builds the world while you play: locations, dungeon rooms, new zones and terrain pieces. Before, each had its own budget, so two of them could still land in the same frame and add up to a hitch. The first job in a frame always runs (nothing waits forever) and nothing is held back while a loading screen is up.");
    WbMs = Config.Bind(SBS + "World Budget", "BudgetMs", 10f, new ConfigDescription("How many milliseconds per frame all world building together may use (the first job of a frame always runs). Higher = the world fills in faster, lower = smoother frames.", new AcceptableValueRange<float>(3f, 40f)));
    DgPerInstance = Config.Bind(SBS + "Dungeon", "OneDungeonPerFrame", true, "Fix: when two dungeons load at the same time (e.g. two crypts next to each other), only one of them may build rooms in a given frame. Without this both could, because they shared the same name in the one-job-per-frame check.");
    BlKeep = Config.Bind(SBS + "General", "KeepSortedBacklog", true, "Remember the nearest-first order of the objects that are still waiting to appear, and reuse it while you have barely moved, instead of sorting the whole waiting list again every frame. Saves time when a lot is still loading in.");
    BlMeters = Config.Bind(SBS + "General", "ResortAfterMeters", 4f, new ConfigDescription("Sort the waiting list again once you have moved this many metres since the last sort.", new AcceptableValueRange<float>(0.5f, 32f), new ConfigurationManagerAttributes { IsAdvanced = true }));
    BlFrames = Config.Bind(SBS + "General", "ResortAfterFrames", 30, new ConfigDescription("...and at the latest after this many frames.", new AcceptableValueRange<int>(1, 600), new ConfigurationManagerAttributes { IsAdvanced = true }));
    RmGuardOnly = Config.Bind(SBS + "Cleanup", "RepairOnlyOnError", true, "Look for broken leftover objects only when the game's clean-up actually trips over one (then remove them and carry on), instead of checking every loaded object every frame. Off = the old check every frame.");
    RmCapOn = Config.Bind(SBS + "Cleanup", "LimitRemovalsPerFrame", true, "While you play (not on a loading screen), unload at most RemovalsPerFrame out-of-range objects per frame and do the rest on the next frames. Stops the freeze when a big area unloads at once, e.g. when you run or sail away from a large base. Turns itself off if another mod changes the same clean-up code.");
    RmCapN = Config.Bind(SBS + "Cleanup", "RemovalsPerFrame", 250, new ConfigDescription("Most objects unloaded per frame when LimitRemovalsPerFrame is on.", new AcceptableValueRange<int>(25, 5000)));
    SfFlush = Config.Bind(SBS + "Safety", "FlushBeforeSave", true, "Before every world save (manual save, autosave, logout, quit, shutdown) finish right away whatever this mod is still building over several frames: locations waiting to be switched on, dungeons whose rooms are still being placed, and dungeon room loads still waiting to be requested. So a save or a shutdown never catches a building half done.");
    SfCommit = Config.Bind(SBS + "Safety", "CommitOnComplete", true, "A world save never records an area as generated (or its location as placed) while that area is still being built. If the game stops halfway, the area counts as new and is generated again the next time someone goes there. Turns itself off if another mod replaces the same save code.");
    SfOwner = Config.Bind(SBS + "Safety", "HostSpawnsAtOnce", true, "On the PC that owns the world (dedicated server, the host, or single player) build locations, dungeons and zones all at once, exactly like the unmodded game. Only players who joined someone else's world get the spread-out building. Off = EXPERIMENTAL: the host also spreads out what it shows on screen. Generating new world data is always done in one go, whatever this says. A dedicated server never loads this part of the mod at all.");
    SfBuilds = Config.Bind(SBS + "Safety", "LeavePlayerBuildsVanilla", true, "Anything a player built (every object that has a creator) loads and unloads exactly like the unmodded game, in the game's own order and timing: while such an object is waiting to load, this mod hands object loading back to the game; it never holds back, caps or reorders unloading of player builds; and the ground (terrain and its rebuilds) and the locations in and right next to an area with player builds are built without spreading or postponing. Exploration areas without player builds still get the smooth spread-out loading. See also PlaceLocationsBeforeBuilds and SpreadDungeonInteriorsNearBuilds. Off = player builds are spread out like everything else.");
    SfExtFirst = Config.Bind(SBS + "Safety", "PlaceLocationsBeforeBuilds", true, "Needs LeavePlayerBuildsVanilla. In the frame the game starts loading player builds in an area (the same or a neighbouring 64 m zone), first create what belongs under and around them: the location exteriors there (for example a dungeon entrance and its stonework), the location walls and stonework that are separate objects, and the terrain edits (the ground shape). So these never appear after the player pieces that stand on or against them. The player pieces themselves keep the game's order and timing. A location whose model is not loaded yet still appears as soon as the game has loaded it. Off = these objects come in the game's normal order, mixed with the player pieces.");
    SfDgInterior = Config.Bind(SBS + "Safety", "SpreadDungeonInteriorsNearBuilds", true, "Dungeon interiors (the rooms of a crypt, cave or similar, built far below the world) keep the smooth spread-out loading even when the dungeon entrance is next to player builds. Only the interior rooms are spread; the entrance outside is handled by PlaceLocationsBeforeBuilds. Off = a dungeon next to player builds builds its interior all at once, like the unmodded game.");
    DgStrict = Config.Bind(SBS + "Dungeon", "StrictWorldBudget", true, "A spread-out dungeon never places a room in a frame whose shared world-building budget (WorldBudgetMs) is already used up, and a room that is known to be slow waits for a frame that nothing else has used yet. It still goes after MaxWaitFrames so it can never get stuck. Off = old behaviour (after 3 waiting frames a room is placed anyway).");
    DgMaxWait = Config.Bind(SBS + "Dungeon", "MaxWaitFrames", 30, new ConfigDescription("Most frames a dungeon step (room, final ground snap, room load requests) waits for budget before it goes anyway.", new AcceptableValueRange<int>(3, 300)));
    DgSnapOwn = Config.Bind(SBS + "Dungeon", "SnapInOwnFrame", true, "After the last room of a spread-out dungeon, the final ground snap and the release of the room assets run in a frame of their own, are timed, and count against the shared world-building budget. Off = they run right after the last room, in the same frame.");
    DgLearn = Config.Bind(SBS + "Dungeon", "LearnRoomCost", true, "Remember how long each kind of dungeon room took to place. A room that is known to be slower than what is left of this frame's budget is placed in the next frame instead (as the first room there). Off = rooms are placed until the frame budget is used, whatever they cost.");
    DgGlobalFlight = Config.Bind(SBS + "Dungeon", "GlobalLoadsInFlight", true, "MaxRoomLoadsInFlight counts the room loads of all dungeons together, not per dungeon, so two dungeons loading at once cannot double the load spikes. Off = per dungeon (old behaviour).");
    DgBudgetIssue = Config.Bind(SBS + "Dungeon", "BudgetLoadRequests", true, "The time spent requesting dungeon room loads counts against the shared world-building budget, waits when that budget is used up (at most MaxWaitFrames), and stays within RoomBudgetMs per frame. Off = only SoftEntryWallMs limits it (old behaviour).");
    DgMeasureLand = Config.Bind(SBS + "Diagnostics", "MeasureRoomLoadFrames", true, "Measure how long the frame took in which a dungeon room asset finished loading, and name it in hitch lines and STATS.");
    HyDg = Config.Bind(SBS + "Location Hysteresis", "IncludeDungeons", true, "Also keep a dungeon interior (DG_ objects) loaded for KeepLoadedSec after it leaves the loaded area, and apply ReloadCooldownSec to it, so walking back and forth near a crypt does not rebuild all its rooms each time. Off = dungeon interiors unload and rebuild like the unmodded game.");
    HitchOn = Config.Bind(SBS + "Diagnostics", "LogHitches", true, "Write a line to SpawnBudget.log for every frame longer than HitchMs while the world is visible, naming what world work ran in it (dungeon, location, zone, object, heightmap, DropNSpawn, GC, save) and how long each took.");
    HitchMs = Config.Bind(SBS + "Diagnostics", "HitchMs", 33f, new ConfigDescription("Frame time (ms) above which LogHitches writes a line.", new AcceptableValueRange<float>(16f, 500f)));
    HyLight = Config.Bind(SBS + "Location Hysteresis", "LightScan", true, "Work out which locations are still in range from the far-object list only, instead of re-marking every loaded object each frame. Same result, less work. Off = old full marking.");
    // 2.3.2: slow location assets
    LaKeep = Config.Bind(SBS + "Location Assets", "RememberSlowAssets", true, "Remember the location assets (big buildings such as swamp crypts or fortresses) that froze the game for a moment when they finished loading, and keep that list in a small file on this PC (HitchFix.slowlocations.txt in the game's settings folder), so it is known from the first loading screen of the next session. Off = remembered for this session only.");
    LaPre = Config.Bind(SBS + "Location Assets", "PreloadBehindLoadingScreens", true, "Behind the first loading screen of a session and behind every teleport / respawn screen, start loading every remembered slow location asset and keep it loaded until you log out, so it is not loaded (and does not freeze the game) when you walk up to it. Costs some memory (a few MB per asset). Each loading screen writes one line saying what it requested, or why nothing was requested.");
    LaMax = Config.Bind(SBS + "Location Assets", "MaxRememberedAssets", 16, new ConfigDescription("How many slow location assets are remembered and kept loaded at most (the oldest is dropped first).", new AcceptableValueRange<int>(4, 64)));
    LaCalm = Config.Bind(SBS + "Location Assets", "CalmSecAfterAssetHitch", 0.5f, new ConfigDescription("After a location asset finished loading with a hitch, a hidden location copy (pre-instantiate) may only start or be let into the world after this many seconds of smooth frames (none longer than LocationDeferIfFrameOverMs), so two heavy steps never land back to back. 0 = off.", new AcceptableValueRange<float>(0f, 5f)));
    string[] oldLog = null; try { var op = Path.Combine(Paths.BepInExRootPath, "SpawnBudget.log"); if (File.Exists(op) && new FileInfo(op).Length < 64L * 1024 * 1024) oldLog = File.ReadAllLines(op); } catch { } // 2.3.7: last session's load stalls
    try { log = new StreamWriter(new FileStream(Path.Combine(Paths.BepInExRootPath, "SpawnBudget.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true }; } catch { }
    LaPrebuild = Config.Bind(SBS + "Location Assets", "PrebuildRememberedBehindLoadingScreens", true, "Needs PreloadBehindLoadingScreens and PreInstantiateHeavy. Once a remembered slow location asset has loaded behind a loading / teleport screen, also make one hidden, switched-off copy of that location there (the same hidden copy pre-instantiate makes while you approach). When that location then appears in the world the copy is used and nothing has to be built while you play. Only world locations (crypts, towers, ruins ...), never anything players built.");
    LaPrebuildMax = Config.Bind(SBS + "Location Assets", "MaxPrebuiltCopies", 8, new ConfigDescription("Most hidden copies PrebuildRememberedBehindLoadingScreens keeps ready at a time (each one uses some memory until it is used or you quit). Default 8; copies are built worst hitch first and one slot is always kept for the worst one.", new AcceptableValueRange<int>(1, 16)));
    LaEdge = Config.Bind(SBS + "Location Assets", "PrebuildRememberedAtLoadedAreaEdge", true, "While you play, also start the hidden copy of a remembered slow location as soon as its spot comes into the loaded area at the edge and its asset is loaded (not only for locations measured as heavy), so it is ready before the location is placed.");
    LaWaitCopy = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "WaitForHiddenCopySec", 3f, new ConfigDescription("When a hidden copy of a location is already being built, placing that location waits for the copy beyond MaxDeferFrames, up to this many seconds, so the build cost is not paid twice. When the time is up the copy is dropped and the location is placed the normal way. 0 = old behaviour (wait MaxDeferFrames only).", new AcceptableValueRange<float>(0f, 10f)));
    LaMeasure = Config.Bind(SBS + "Location Assets", "MeasureBeforeCopy", true, "A remembered slow location whose cost is not known yet (0 ms in HitchFix.slowlocations.txt) gets no hidden copy until it has been placed the normal way once and timed (that one placement can hitch). Measured ones get the copy slots first, worst first, so the hidden copies go where they help most. Off = unmeasured ones get copies too, ranked after the measured ones, and stay unmeasured.");
    LaSpare = Config.Bind(SBS + "Location Assets", "SpareSlotsForUnmeasured", true, "Copy slots (MaxPrebuiltCopies) that the measured slow locations leave free behind a loading screen go to remembered locations whose cost is not measured yet, those with the biggest remembered load stall first (see KnownLoadStalls). They never take a slot a measured one needs, and are dropped first when one does. A location placed from such a copy is not timed at placement. Off = no copy until measured.");
    LaStalls = Config.Bind(SBS + "Location Assets", "KnownLoadStalls", "MWL_SwampComplex1=355", "Location load stalls seen in earlier logs, as name=ms separated by commas. Used only to rank unmeasured locations for spare copy slots; stalls found in the previous session's SpawnBudget.log are added by themselves. Empty = none.");
    ReserveConfig(); // 2.3.8
    DefaultsBump();
    LocSlowLoad();
    try { LocStallImport(oldLog); } catch (Exception e) { Log("load stall import failed: " + e.Message); } oldLog = null;
    BakeConfig(); SlimConfig(); ArrivalConfig();
    var h = new Harmony("vpf.ValheimPerformanceFixes.SpawnBudget");
    try { h.Patch(AccessTools.Method(typeof(Character), "ApplyDamage"), postfix: new HarmonyMethod(typeof(Plugin), nameof(DmgPost))); } catch (Exception e) { Log("combat damage hook FAILED: " + e.Message); }
    try {
      createObj = AccessTools.MethodDelegate<Func<ZNetScene, ZDO, GameObject>>(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) }));
      var target = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) });
      h.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), nameof(CreateObjectsPre)) { priority = Priority.First, before = new[] { "com.Fire.FiresGhettoNetworkMod" } },
                      postfix: new HarmonyMethod(typeof(Plugin), nameof(CreateObjectsPost)) { priority = Priority.Last });
      Log("SpawnBudget " + Ver + " object budget patched on ZNetScene.CreateObjects (first prefix), " + BudgetMs.Value + " ms/frame");
    } catch (Exception e) { Log("object patch failed, disabling that part: " + e); objOk = false; }
    // 2.3.1 Mod Overlap: tell the overlap rules when this module's own object budget / removal cap is really running
    global::ValheimPerformanceFixes.Overlap.SetOurs(global::ValheimPerformanceFixes.Feature.SpawnBudget, () => objOk && Enabled != null && Enabled.Value);
    global::ValheimPerformanceFixes.Overlap.SetOurs(global::ValheimPerformanceFixes.Feature.RemovalCap, () => rmCapOk && RmCapOn != null && RmCapOn.Value);
    try {
      var co = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });
      h.Patch(co, prefix: new HarmonyMethod(typeof(Plugin), nameof(CoPre)) { priority = Priority.First, before = Others },
                  postfix: new HarmonyMethod(typeof(Plugin), nameof(CoPost)) { priority = Priority.Last, after = Others });
      Log("whole-CreateObject timer patched (First prefix / Last postfix)");
    } catch (Exception e) { Log("CreateObject timer patch failed: " + e); }
    try {
      h.Patch(AccessTools.DeclaredMethod(typeof(Heightmap), "Regenerate"), prefix: new HarmonyMethod(typeof(Plugin), nameof(RegenPre)) { priority = Priority.First });
      h.Patch(AccessTools.DeclaredMethod(typeof(Heightmap), "ForceGenerateAll"), prefix: new HarmonyMethod(typeof(Plugin), nameof(ForcePre)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(ForceFin)));
      Log("heightmap all-path slicing patch ok (enabled=" + HmAllPaths.Value + ")");
    } catch (Exception e) { Log("heightmap all-path patch failed: " + e); }
    try {
      h.Patch(AccessTools.DeclaredMethod(typeof(Heightmap), "CustomLateUpdate"), prefix: new HarmonyMethod(typeof(Plugin), nameof(HmPre)), postfix: new HarmonyMethod(typeof(Plugin), nameof(HmPost)));
      Log("heightmap slicing patch ok (enabled=" + HmEnabled.Value + ")");
    } catch (Exception e) { Log("heightmap patch failed: " + e); }
    try { BakeInit(h); } catch (Exception e) { Log("terrain collider bake setup failed: " + e.Message); }
    try {
      h.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), "ShouldDelayProxyLocationSpawning"), postfix: new HarmonyMethod(typeof(Plugin), nameof(DelayPost)));
      h.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnProxyLocation"), prefix: new HarmonyMethod(typeof(Plugin), nameof(LocPre)), postfix: new HarmonyMethod(typeof(Plugin), nameof(LocPost)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(LocFin)));
      try { h.Patch(AccessTools.DeclaredMethod(typeof(ZNetScene), "RemoveObjects"), prefix: new HarmonyMethod(typeof(Plugin), nameof(RemoveObjectsPre)) { priority = Priority.First, before = new[] { "com.Fire.FiresGhettoNetworkMod" } }, finalizer: new HarmonyMethod(typeof(Plugin), nameof(RemoveObjectsFin))); Log("ZNetScene.RemoveObjects guard + removal cap + location edge hysteresis patched"); } catch (Exception e) { Log("RemoveObjects guard patch failed: " + e.Message); }
    try { var slm = AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation"); var rv = global::ValheimPerformanceFixes.PatchGuard.Rivals(slm, false, true);
      if (rv.Count > 0) { locSplitOk = false; Log("location split-activation patch stands down: ZoneSystem.SpawnLocation is also transpiled by " + string.Join(", ", rv)); }
      else { h.Patch(slm, transpiler: new HarmonyMethod(typeof(Plugin), nameof(LocTranspiler)) { priority = Priority.Last }); locSplitHarmony = h; Log("location split-activation patch " + (locSplitOk ? "ok" : "NOT applied (pattern not found)") + " (enabled=" + LocSplit.Value + ")"); } } catch (Exception e) { locSplitOk = false; Log("location split-activation patch failed: " + e); }
    // 2.2.0: which location SpawnLocation is copying (for pre-built clones and light copies), and never copy a prefab Unity is still copying
    try { var slm2 = AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation"); h.Patch(slm2, prefix: new HarmonyMethod(typeof(Plugin), nameof(SlSpawnPre)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Plugin), nameof(SlSpawnFin)));
      slOk = locSplitOk; Log("slim location models " + (slOk ? "ready" : "NOT available (needs the split-activation patch)") + " (enabled=" + SlOn.Value + ")"); } catch (Exception e) { slOk = false; Log("slim location models patch failed: " + e.Message); }
      try { lpInst = AccessTools.FieldRefAccess<LocationProxy, GameObject>("m_instance"); lpNeeds = AccessTools.FieldRefAccess<LocationProxy, bool>("m_locationNeedsSpawn");
        h.Patch(AccessTools.DeclaredMethod(typeof(LocationProxy), "SpawnLocation"), prefix: new HarmonyMethod(typeof(Plugin), nameof(ProxySpawnPre)) { priority = Priority.First });
        try { h.Patch(AccessTools.DeclaredMethod(typeof(LocationProxy), "SpawnLocation"), prefix: new HarmonyMethod(typeof(Plugin), nameof(ProxySpawnCtx)) { priority = Priority.First + 1 }, finalizer: new HarmonyMethod(typeof(Plugin), nameof(ProxySpawnFin))); Log("player-build guard patched on LocationProxy.SpawnLocation"); }
        catch (Exception e2) { Log("player-build guard for locations FAILED (locations are spread everywhere): " + e2.Message); } Log("LocationProxy duplicate-spawn guard patched"); }
      catch (Exception e) { lpInst = null; Log("LocationProxy duplicate-spawn guard patch failed: " + e.Message); }
      Log("location serialization patch ok (enabled=" + LocSerialize.Value + ")");
    } catch (Exception e) { Log("location patch failed: " + e); }
    try {
      h.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone"), prefix: new HarmonyMethod(typeof(Plugin), nameof(ZonePre)) { priority = Priority.Last }, postfix: new HarmonyMethod(typeof(Plugin), nameof(ZonePost)));
      Log("zone throttle patch ok (enabled=" + ZoneThrottle.Value + ")");
    } catch (Exception e) { Log("zone patch failed: " + e); }
    try {
      var sp = AccessTools.DeclaredMethod(typeof(DungeonGenerator), "Spawn");
      var info = Harmony.GetPatchInfo(sp);
      var foreign = new List<string>(); if (info != null) foreach (var o in info.Owners) if (o != "sighsorry.DropNSpawn") foreign.Add(o);
      if (info != null && info.Owners.Contains("sighsorry.DropNSpawn")) {
        var sm = AccessTools.TypeByName("DropNSpawn.SpawnerManager");
        dnsBegin = sm == null ? null : AccessTools.Method(sm, "TryBeginDerivedLocationSpawnContext", new[] { typeof(DungeonGenerator) });
        dnsEnd = sm == null ? null : AccessTools.Method(sm, "EndLocationSpawnContext");
        if (dnsBegin == null || dnsEnd == null) foreign.Add("sighsorry.DropNSpawn(context API not found)");
        else Log("DungeonGenerator.Spawn is patched by DropNSpawn: its location-spawn context will be pushed around spread PlaceRoom batches");
      }
      if (foreign.Count > 0) { dgOk = false; Log("DungeonGenerator.Spawn is patched by " + string.Join(",", foreign) + " - dungeon spreading disabled"); }
      else {
        placeRoom = AccessTools.DeclaredMethod(typeof(DungeonGenerator), "PlaceRoom", new[] { typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode) });
        release = AccessTools.DeclaredMethod(typeof(DungeonGenerator), "ReleaseHeldReferences");
        var rpd = AccessTools.Inner(typeof(DungeonGenerator), "RoomPlacementData");
        fRd = AccessTools.Field(rpd, "m_roomData"); fPos = AccessTools.Field(rpd, "m_position"); fRot = AccessTools.Field(rpd, "m_rotation");
        if (placeRoom == null || release == null || fRd == null || fPos == null || fRot == null) throw new Exception("dungeon members not found");
        placeRoomD = AccessTools.MethodDelegate<Func<DungeonGenerator, DungeonDB.RoomData, Vector3, Quaternion, RoomConnection, ZoneSystem.SpawnMode, Room>>(placeRoom);
        releaseD = AccessTools.MethodDelegate<Action<DungeonGenerator>>(release);
        if (dnsBegin != null && dnsEnd != null) {
          try { dnsBeginD = AccessTools.MethodDelegate<Func<DungeonGenerator, bool>>(dnsBegin); dnsEndD = AccessTools.MethodDelegate<Action>(dnsEnd); }
          catch (Exception e) { var b0 = dnsBegin; var e0 = dnsEnd; dnsBeginD = g => Convert.ToBoolean(b0.Invoke(null, new object[] { g })); dnsEndD = () => e0.Invoke(null, null); Log("DropNSpawn context delegates not available (" + e.Message + "), using reflection"); }
        }
        h.Patch(AccessTools.DeclaredMethod(typeof(DungeonGenerator), "OnRoomLoaded"), prefix: new HarmonyMethod(typeof(Plugin), nameof(RoomLoadedPre)) { priority = Priority.Last });
        try { h.Patch(AccessTools.DeclaredMethod(typeof(DungeonGenerator), "OnRoomLoaded"), prefix: new HarmonyMethod(typeof(Plugin), nameof(DgTimePre)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Plugin), nameof(DgTimeFin))); } catch (Exception e) { Log("dungeon build timer patch failed: " + e.Message); }
        try {
          h.Patch(AccessTools.DeclaredMethod(typeof(DungeonGenerator), "LoadRoomPrefabsAsync"), prefix: new HarmonyMethod(typeof(Plugin), nameof(LoadRoomsPre)) { priority = Priority.Last });
          var lp = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(DungeonGenerator), "LoadRoomPrefabsAsync"));
          Log("dungeon room-load stagger patch ok (enabled=" + DgStagger.Value + ", inFlight=" + DgMaxLoads.Value + ", owners=" + (lp == null ? "-" : string.Join(",", lp.Owners)) + ")");
        } catch (Exception e) { Log("dungeon room-load stagger patch failed: " + e.Message); }
        Log("dungeon spread patch ok (enabled=" + DgSpread.Value + ", " + DgBudgetMs.Value + " ms/frame)");
      }
    } catch (Exception e) { dgOk = false; Log("dungeon patch failed, vanilla dungeon spawning: " + e); }
    // 2.1.1 save safety. The flush prefixes only finish this module's own queued work and never skip or change the
    // hooked method, so they stay even when another mod patches the same method (listed by the patch audit).
    HookFlush(h, typeof(ZNet), "SaveWorld", nameof(FlushSaveWorld)); HookFlush(h, typeof(ZNet), "Save", nameof(FlushSave));
    HookFlush(h, typeof(ZNet), "Shutdown", nameof(FlushNetShutdown)); HookFlush(h, typeof(Game), "Shutdown", nameof(FlushGameShutdown));
    Log("save safety: flush-before-save patched on " + flushHooks.Count + "/4 save and shutdown methods (enabled=" + SfFlush.Value + ")");
    // 2.1.1: time saves for the hitch log (never changes them)
    foreach (var sm0 in new[] { AccessTools.DeclaredMethod(typeof(ZNet), "SaveWorld"), AccessTools.DeclaredMethod(typeof(ZNet), "Save"), AccessTools.DeclaredMethod(typeof(Game), "SavePlayerProfile") })
      if (sm0 != null) try { h.Patch(sm0, prefix: new HarmonyMethod(typeof(Plugin), nameof(SaveTimePre)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Plugin), nameof(SaveTimeFin))); } catch (Exception e) { Log("save timer patch on " + sm0.Name + " failed: " + e.Message); }
    try { h.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone"), prefix: new HarmonyMethod(typeof(Plugin), nameof(GenPre)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Plugin), nameof(GenFin)));
      Log("save safety: zone generation tracking patched on ZoneSystem.SpawnZone"); }
    catch (Exception e) { commitOk = false; Log("save safety: zone generation tracking patch failed, commit gate off: " + e.Message); }
    try { var ps = AccessTools.DeclaredMethod(typeof(ZoneSystem), "PrepareSave"); var rv = global::ValheimPerformanceFixes.PatchGuard.Rivals(ps);
      if (rv.Count > 0) { commitOk = false; Log("save safety: commit gate stands down: ZoneSystem.PrepareSave is also changed by " + string.Join(", ", rv)); }
      else if (commitOk) { h.Patch(ps, postfix: new HarmonyMethod(typeof(Plugin), nameof(CommitPost)) { priority = Priority.Last }); Log("save safety: commit gate patched on ZoneSystem.PrepareSave (enabled=" + SfCommit.Value + ")"); } }
    catch (Exception e) { commitOk = false; Log("save safety: commit gate patch failed: " + e.Message); }
    Log("save safety: host spawns at once=" + SfOwner.Value);
    nextReport = 60f;
  }
  // ---- locations ----
  static int locFrame = -1, locDeferRun, locCount; static int sLocSpawns, sLocDelayed, sLocForced; static double sLocMax; static readonly Stopwatch locSw = new Stopwatch();
  static readonly Dictionary<int, int> locLoadedSeen = new Dictionary<int, int>(); static int sLocHeld, sLocPrewarm; static bool inPrewarm;
  static void DelayPost(int hash, ref bool __result) {
    if (inPrewarm || genDepth > 0) return;
    if (pbProxyHere && !__result) return; // 2.1.1: location next to player builds -> vanilla timing
    if (__result) { locLoadedSeen.Remove(hash); return; }
    if (!LocSerialize.Value) return;
    try {
      if (!Settled()) {
        locLoadedSeen.Clear();
        // 1.7.3: during arrival (player alive, screen up / settle grace) only the per-frame count cap applies
        if (ArOn.Value && ArrivalOk() && locFrame == Time.frameCount && locCount >= Math.Max(1, LocMaxPF.Value) && locDeferRun < Math.Max(1, Math.Min(LocMaxDefer.Value, 120))) { locDeferRun++; sLocCapDelayed++; __result = true; return; }
        locDeferRun = 0; return;
      }
      NewFrame();
      if (LocHoldFrame.Value) {
        if (locLoadedSeen.Count > 512) locLoadedSeen.Clear();
        int f0 = Time.frameCount;
        if (!locLoadedSeen.TryGetValue(hash, out int seen)) { locLoadedSeen[hash] = f0; sLocHeld++; __result = true; return; }
        if (seen == f0) { __result = true; return; }
        if (f0 - seen > 120) locLoadedSeen.Remove(hash);
      }
      // Hold heavy LocationProxy spawns until InstantiateAsync pre-clone is ready (closes atomic Instantiate gap).
      if (LocPreInst != null && LocPreInst.Value && PreInstPending(hash)) {
        float tw = Time.unscaledTime;
        if (!preWaitAt.TryGetValue(hash, out float w0) || tw - w0 > PreWaitCap() + 5f) preWaitAt[hash] = w0 = tw;
        if (locDeferRun < Math.Max(1, Math.Min(LocMaxDefer.Value, 120))) { locDeferRun++; sLocDelayed++; __result = true; return; }
        // 2.3.4: a copy is already being built: keep waiting for it (up to WaitForHiddenCopySec) and let it in at once, so the
        // location is not built a second time the normal way while the copy finishes anyway
        if (preOps.ContainsKey(hash) && PreWaitCap() > 0f) {
          if (tw - w0 < PreWaitCap()) { if (preUrgent.Add(hash)) sPreWaitLong++; sLocDelayed++; __result = true; return; }
          PreDiscard(hash, "placement waited " + (tw - w0).ToString("0.0") + " s");
        }
      }
      bool capHit = locFrame == Time.frameCount && locCount >= Math.Max(1, LocMaxPF.Value);
      bool slowFrame = LocOverMs.Value > 0f && Time.unscaledDeltaTime * 1000f > LocOverMs.Value;
      if (capHit) sLocCapDelayed++; else if (slowFrame) sLocSlowFrameDelayed++;
      bool busy = locPendingAct.Count > 0 || locPendingSnap.Count > 0 || capHit || slowFrame || dgActive > 0 || spawnMs > LocBudgetMs.Value || zoneFrame == Time.frameCount || HeavyBusyOther("location") || !WbCanStart();
      if (!busy) { locDeferRun = 0; return; }
      if (locDeferRun >= Math.Max(1, Math.Min(LocMaxDefer.Value, 120))) { locDeferRun = 0; sLocForced++; locFrame = Time.frameCount; locCount = 0; return; }
      locDeferRun++; sLocDelayed++; __result = true;
    } catch { }
  }
  static string sLocMaxName = "-";
  // ---- 1.5.2 split activation ----
  static bool locSplitOk, locDeferNext; static GameObject locAssetOff; static int sLocSplit;
  static readonly Dictionary<int, double> locLastMs = new Dictionary<int, double>();
  static readonly List<GameObject> locPendingAct = new List<GameObject>(); static int locPendingFrame;
  static HashSet<string> heavyNames; static string heavyListSeen;
  static IEnumerable<CodeInstruction> LocTranspiler(IEnumerable<CodeInstruction> ins) {
    // 2.1.0 hardening: edit copies only; exactly one Instantiate -> SetActive pair must exist, otherwise the original is returned.
    // 2.1.1 fix: copy with new CodeInstruction(c) so labels and exception blocks survive (Clone() dropped them: "Label #1 is not marked", the patch never applied)
    var orig = new List<CodeInstruction>(ins); var list = new List<CodeInstruction>(orig.Count); foreach (var c in orig) list.Add(new CodeInstruction(c));
    int pairs = 0; int at = -1, atJ = -1; var setActive = AccessTools.Method(typeof(GameObject), "SetActive", new[] { typeof(bool) });
    var locInst = AccessTools.Method(typeof(Plugin), nameof(LocInstantiate), new[] { typeof(SoftReferenceableAssets.SoftReference<GameObject>), typeof(Vector3), typeof(Quaternion) });
    for (int i = 0; i < list.Count; i++) {
      var mi = list[i].operand as System.Reflection.MethodInfo;
      if ((list[i].opcode == System.Reflection.Emit.OpCodes.Call || list[i].opcode == System.Reflection.Emit.OpCodes.Callvirt) && mi != null && mi.Name == "Instantiate" && mi.DeclaringType != null && mi.DeclaringType.FullName != null && mi.DeclaringType.FullName.StartsWith("SoftReferenceableAssets")) {
        var pars = mi.GetParameters();
        if (pars.Length != 3 || pars[1].ParameterType != typeof(Vector3)) continue;
        for (int j = i + 1; j < Math.Min(list.Count, i + 8); j++) {
          if ((list[j].opcode == System.Reflection.Emit.OpCodes.Callvirt || list[j].opcode == System.Reflection.Emit.OpCodes.Call) && Equals(list[j].operand, setActive)) {
            pairs++; if (at < 0) { at = i; atJ = j; } break;
          }
        }
      }
    }
    if (pairs != 1) { locSplitOk = false; Log("location split-activation: expected 1 Instantiate/SetActive pair in ZoneSystem.SpawnLocation, found " + pairs + " - left unchanged"); return orig; }
    list[atJ] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(LocActivate))).MoveLabelsFrom(list[atJ]).MoveBlocksFrom(list[atJ]);
    list[at] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, locInst).MoveLabelsFrom(list[at]).MoveBlocksFrom(list[at]);
    var bad = global::ValheimPerformanceFixes.PatchGuard.CheckLabels(list);
    if (bad != null) { locSplitOk = false; Log("location split-activation: result failed the label check (" + bad + ") - left unchanged"); return orig; }
    locSplitOk = true; return list;
  }
  // 1.5.3 FIX: 1.5.2 deactivated the location ASSET in the SpawnProxyLocation prefix, i.e. BEFORE vanilla SpawnLocation collected
  // Utils.GetEnabledComponentsInChildren<ZNetView/RandomSpawn/RandomObject>(asset). GetComponentsInChildren skips inactive objects,
  // so all three lists were empty: the location's ZNetView children were not switched off before Instantiate and RandomSpawn/
  // RandomObject did not run. On the deferred activation those children Awoke as fresh networked objects (new client-owned ZDOs:
  // duplicate doors/chests/etc. on top of the real ones -> "door needs 2 interactions"), and when the proxy unloaded them with
  // the location hierarchy, ZNetScene.m_instances kept dead entries -> NullReferenceException in ZNetScene.RemoveObjects.
  // Now the asset is deactivated only immediately before the Instantiate call (after vanilla collected/disabled its children) and
  // reactivated immediately after it.
  static bool locAssetDisabled;
  static void LocBeforeInstantiate() {
    try { if (locDeferNext && locAssetOff != null && locAssetOff.activeSelf) { locAssetOff.SetActive(false); locAssetDisabled = true; } } catch { }
  }
  // 1.7.4: replace SoftReferenceableAssets.Utils.Instantiate in SpawnLocation â€” reuse a pre-built inactive clone when ready.
  static int locCurHash; static int sLocPreInst, sLocPreInstUsed, sLocPreInstSync;
  static readonly Dictionary<int, AsyncInstantiateOperation<GameObject>> preOps = new Dictionary<int, AsyncInstantiateOperation<GameObject>>();
  static readonly Dictionary<int, GameObject> preReady = new Dictionary<int, GameObject>();
  static readonly Vector3 prePark = new Vector3(0f, -9000f, 0f);
  static GameObject LocInstantiate(SoftReferenceableAssets.SoftReference<GameObject> original, Vector3 position, Quaternion rotation) {
    try {
      int hash = curZl != null ? curZl.Hash : locCurHash;
      preWaitAt.Remove(hash);
      SlNoteUse(hash, original);
      if (TryTakePreInst(hash, position, rotation, out var pre) && pre != null) {
        // 2.2.0 FIX: the pre-built clone now takes the same asset reference vanilla's copy would (Load + TransferReference), and gets
        // the prefab's current on/off state, biome and interior placement (vanilla randomizes those on the prefab right before copying).
        GameObject asset = null; try { asset = original.Asset; } catch { }
        bool okMirror = false; try { okMirror = asset != null && MirrorClone(asset, pre); } catch { }
        if (okMirror) {
          if (original.Load() == SoftReferenceableAssets.LoadResult.Succeeded) { SoftReferenceableAssets.GameObjectExtentions.TransferReference(pre, original); sLocPreInstUsed++; try { ResAfterUse(hash); } catch { } return pre; }
          try { original.Release(); } catch { }
        }
        try { UnityEngine.Object.Destroy(pre); } catch { }
      }
    } catch { }
    LocBeforeInstantiate();
    try { var sl = SlimTake(curZl != null ? curZl.Hash : locCurHash, original, position, rotation); if (sl != null) return sl; } catch (Exception e) { Log("slim location copy error: " + e.Message); }
    sLocPreInstSync++;
    // 2.3.5: the normal placement is timed, so a remembered location with an unknown cost gets a measured one (and its copy rank)
    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    var go = SoftReferenceableAssets.Utils.Instantiate(original, position, rotation);
    try { LocMeasurePlaced(curZl != null ? curZl.Hash : locCurHash, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); } catch { }
    return go;
  }
  static int sLocMeasured;
  static void LocMeasurePlaced(int h, double ms) {
    if (h == 0) return;
    string n = null; try { var zl = GetLoc(h); if (zl != null) n = zl.m_prefabName; } catch { }
    if (string.IsNullOrEmpty(n)) return;
    float m = Math.Max(1f, (float)ms); // at least 1 ms: it counts as measured from now on
    // 2.3.8: a location not on the list yet that takes BigLocationMs or longer is put on it; a listed one that is fast again and again comes off
    if (!locSlowNames.Contains(n)) { float big = BigV(); if (big > 0f && m >= big) LocBigAdd(n, h, m, "placed the normal way in " + m.ToString("0") + " ms"); return; }
    if (BigFastCheck(n, m)) return;
    float was = LocWorstMs(n);
    if (was > 0f && m <= was + 0.5f) return;
    locSlowMs[n] = m; locSlowDirty = true; locSaveAt = Time.unscaledTime + 3f; sLocMeasured++;
    // 2.3.7: decided again on this screen with the new cost (the screen's summary then shows the measured value, not "not measured yet")
    if (was <= 0f) { prebuildTried.Remove(h); prebuildLimitCounted.Remove(h); if (locScreenOn) prebuildWhy[n] = "measured on this screen (" + m.ToString("0") + " ms)"; else prebuildWhy.Remove(n); }
    Log("slow location asset measured at placement: " + n + " " + (was > 0f ? was.ToString("0") : "?") + " -> " + m.ToString("0") + " ms (placed the normal way; saved, used to rank hidden copies)");
  }
  // 2.3.5: may this remembered location get a hidden copy (MeasureBeforeCopy: only once its cost is known)
  static bool LocMeasuredOk(string n) { return LaMeasure == null || !LaMeasure.Value || LocWorstMs(n) > 0f; }
  static bool LocMeasuredOk(int h) { string n = null; try { var zl = GetLoc(h); if (zl != null) n = zl.m_prefabName; } catch { } return n == null || LocMeasuredOk(n); }
  static GameObject LocAsset(int hash) {
    try { var zl = GetLoc(hash); return zl == null ? null : zl.m_prefab.Asset; } catch { return null; }
  }
  // 2.1.0: ZoneSystem.GetLocation(int) through a cached delegate (was MethodInfo.Invoke + Traverse on every call)
  static Func<ZoneSystem, int, ZoneSystem.ZoneLocation> getLocD; static bool getLocLooked;
  static ZoneSystem.ZoneLocation GetLoc(int hash) {
    var zs = ZoneSystem.instance; if (zs == null) return null;
    if (!getLocLooked) {
      getLocLooked = true;
      try { getLoc = AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) }); if (getLoc != null) getLocD = AccessTools.MethodDelegate<Func<ZoneSystem, int, ZoneSystem.ZoneLocation>>(getLoc); }
      catch (Exception e) { var m = getLoc; if (m != null) getLocD = (z, h) => m.Invoke(z, new object[] { h }) as ZoneSystem.ZoneLocation; Log("GetLocation delegate not available (" + e.Message + "), using reflection"); }
    }
    return getLocD == null ? null : getLocD(zs, hash);
  }
  static bool LocIsHeavyAsset(int hash, GameObject asset) {
    if (IsHeavy(hash) || proxyHeavyHashes.Contains(hash)) return true;
    if (asset == null) return false;
    if (!locPieces.TryGetValue(hash, out int pc)) { pc = asset.GetComponentsInChildren<Transform>(true).Length; locPieces[hash] = pc; }
    return pc > LocHeavyPieces.Value;
  }
  static void PrepLocationClone(GameObject go) {
    if (go == null) return;
    try {
      var nvs = go.GetComponentsInChildren<ZNetView>(true);
      for (int i = 0; i < nvs.Length; i++) { var c = nvs[i]; if (c != null && c.gameObject != go && c.gameObject.activeSelf) c.gameObject.SetActive(false); }
    } catch { }
    // 2.2.0: random pieces are no longer all switched off here; they get the prefab's state when the clone is taken (MirrorClone)
    if (go.activeSelf) go.SetActive(false);
  }
  static void PollPreInst() {
    if (preDiscardOps.Count > 0) PollDiscards();
    if (preOps.Count == 0) return;
    tmpPreKeys.Clear();
    foreach (var kv in preOps) {
      var op = kv.Value;
      if (op == null) { tmpPreKeys.Add(kv.Key); continue; }
      if (!op.isDone) { try { PreLetIn(kv.Key, op); } catch { } continue; }
      // 1.7.5: InstantiateAsync completion (PrepLocationClone) is a heavy job â€” one per frame
      if (!WbCanStart() || !TryClaimHeavy("preinst")) continue;
      var swPi = Stopwatch.StartNew();
      try {
        var arr = op.Result; GameObject go = (arr != null && arr.Length > 0) ? arr[0] : null;
        if (go != null) { PrepLocationClone(go); preReady[kv.Key] = go; if (LogSlow.Value) Log("location pre-instantiate ready: " + LocName(kv.Key) + " prep " + swPi.Elapsed.TotalMilliseconds.ToString("0.0") + " ms"); }
        else if (LogSlow.Value) Log("location pre-instantiate empty: " + LocName(kv.Key));
      } catch (Exception e) { Log("location pre-instantiate finish failed: " + LocName(kv.Key) + " " + e.Message); }
      NoteHeavyCandidate("PreInst." + LocName(kv.Key), "SpawnBudget", swPi.Elapsed.TotalMilliseconds); WbCharge(swPi.Elapsed.TotalMilliseconds);
      tmpPreKeys.Add(kv.Key);
    }
    for (int i = 0; i < tmpPreKeys.Count; i++) { preOps.Remove(tmpPreKeys[i]); preStartAt.Remove(tmpPreKeys[i]); preLetIn.Remove(tmpPreKeys[i]); preUrgent.Remove(tmpPreKeys[i]); }
  }
  // ---- 2.3.4: placement waiting for a hidden copy ----
  static readonly Dictionary<int, float> preWaitAt = new Dictionary<int, float>(), preDropAt = new Dictionary<int, float>();
  static readonly HashSet<int> preUrgent = new HashSet<int>();
  static readonly List<AsyncInstantiateOperation<GameObject>> preDiscardOps = new List<AsyncInstantiateOperation<GameObject>>();
  static int sPreWaitLong, sPreDiscarded, sPreDiscardDestroyed;
  static float PreWaitCap() { return LaWaitCopy != null ? Mathf.Clamp(LaWaitCopy.Value, 0f, 10f) : 0f; }
  // the copy took too long: cancel it, let Unity finish what it already made, and throw that away when it is done
  static void PreDiscard(int hash, string why) {
    if (!preOps.TryGetValue(hash, out var op)) return;
    preOps.Remove(hash); preStartAt.Remove(hash); preLetIn.Remove(hash); preUrgent.Remove(hash); preWaitAt.Remove(hash); prebuilt.Remove(hash);
    preDropAt[hash] = Time.unscaledTime;
    if (op != null) { try { op.Cancel(); } catch { } try { op.allowSceneActivation = true; } catch { } preDiscardOps.Add(op); }
    sPreDiscarded++; Log("location hidden copy dropped: " + LocName(hash) + " (" + why + "); the location is placed the normal way");
  }
  static void PollDiscards() {
    for (int i = preDiscardOps.Count - 1; i >= 0; i--) {
      var op = preDiscardOps[i]; bool done = true; try { done = op == null || op.isDone; } catch { }
      if (!done) continue;
      try { var arr = op != null ? op.Result : null; if (arr != null) foreach (var g in arr) if (g != null) { UnityEngine.Object.Destroy(g); sPreDiscardDestroyed++; } } catch { }
      preDiscardOps.RemoveAt(i);
    }
  }
  static bool PreDroppedRecently(int hash) { return preDropAt.TryGetValue(hash, out var t) && Time.unscaledTime - t < 30f; }
  static readonly List<int> tmpPreKeys = new List<int>();
  static bool TryStartPreInst(int hash, bool remembered = false) {
    if (LocPreInst == null || !LocPreInst.Value || hash == 0) { preWhy = "PreInstantiateHeavy is off"; return false; }
    if (preReady.ContainsKey(hash) || preOps.ContainsKey(hash)) return true;
    if (PreDroppedRecently(hash)) { preWhy = "a copy of it was dropped less than 30 s ago"; return false; }
    if (remembered && MinCopySmall(hash)) { preWhy = "worst placement under MinCopyWorstMs (" + (MinCopyMs != null ? MinCopyMs.Value.ToString("0") : "?") + " ms)"; sMinCopySkips++; return false; } // 2.3.9
    if (preOps.Count >= 1) { preWhy = "another copy is being built"; return false; } // one InstantiateAsync in flight
    if (preReady.Count >= PreReadyCap()) { preWhy = "too many copies ready (" + preReady.Count + ")"; return false; }
    if (preReady.Count + preOps.Count >= ResMaxV()) { preWhy = "MaxReserveCopies reached (" + ResMaxV() + ")"; return false; } // 2.3.8
    if (ResBudgetMB() <= 0f) { preWhy = "memory budget is 0 MB"; sBudgetRefused++; return false; } // 2.3.8
    var asset = LocAsset(hash); if (asset == null) { preWhy = "asset not loaded"; return false; }
    if (!remembered && !LocIsHeavyAsset(hash, asset)) { preWhy = "not heavy"; return false; }
    if (SlReady(hash)) { preWhy = "a light copy is ready"; return false; } // 2.2.0: a light copy is ready; no need for a full one
    // 2.3.1: starting the copy costs main-thread time too (Unity collects the whole hierarchy up front): while you play it waits for
    // a frame that is not already slow or busy, takes the frame's one heavy-job slot, and its time is charged to the budgets
    bool live = Settled();
    if (live) {
      bool slowFrame = LocOverMs.Value > 0f && Time.unscaledDeltaTime * 1000f > LocOverMs.Value;
      if (!slowFrame && CalmWait()) { sPreCalmWait++; preWhy = "waiting for calm frames"; return false; }
      if (slowFrame || spawnMs > LocBudgetMs.Value || dgActive > 0 || locPendingAct.Count > 0 || locPendingSnap.Count > 0 || !WbCanStart() || !TryClaimHeavy("preinst-start")) { sPreStartWait++; preWhy = "waiting for a free frame"; return false; }
    }
    { string bw; if (!ResBudgetOk(hash, asset, out bw)) { preWhy = bw; return false; } } // 2.3.8: memory budget
    var sw = Stopwatch.StartNew(); double acc0 = global::ValheimPerformanceFixes.FrameCost.Total;
    try {
      // 2.2.0: build under the hidden switched-off holder instead of switching the prefab itself off while Unity copies it
      var op = UnityEngine.Object.InstantiateAsync(asset, Holder().transform, prePark, Quaternion.identity);
      if (op == null) { preWhy = "Unity returned no operation"; return false; }
      // 2.3.1: keep the finished copy out of the scene until a free frame (PollPreInst lets it in)
      bool held = false; try { op.allowSceneActivation = false; held = true; } catch { }
      preOps[hash] = op; preStartAt[hash] = Time.unscaledTime; NoteReq(hash); if (!held) preLetIn.Add(hash); sLocPreInst++;
      sw.Stop(); double ms = sw.Elapsed.TotalMilliseconds;
      global::ValheimPerformanceFixes.FrameCost.AddOwn("location", ms, acc0, "pre-instantiate start " + LocName(hash));
      if (live) WbCharge(ms);
      if (LogSlow.Value) Log("location pre-instantiate started: " + LocName(hash) + " (" + ms.ToString("0.0") + " ms" + (live ? "" : ", loading screen") + ")");
      return true;
    } catch (Exception e) { preWhy = "start failed: " + e.Message; Log("location pre-instantiate start failed: " + LocName(hash) + " " + e.Message); return false; }
  }
  static readonly Dictionary<int, float> preStartAt = new Dictionary<int, float>(); static readonly HashSet<int> preLetIn = new HashSet<int>();
  static int sPreStartWait, sPreLetInWait, sPreLetInForced;
  // 2.3.1: let a held copy into the scene on a free frame (or at once on a loading screen; after 10 s regardless)
  static void PreLetIn(int hash, AsyncInstantiateOperation<GameObject> op) {
    if (preLetIn.Contains(hash)) return;
    float age = preStartAt.TryGetValue(hash, out var t0) ? Time.unscaledTime - t0 : 999f;
    if (op.progress < 0.9f && age < 2f) return; // still copying
    // 2.3.4: placement is waiting for this copy: let it in at once (it costs the same now as later, and the location waits)
    bool now = Loading() || !Settled() || preUrgent.Contains(hash);
    if (!now) {
      bool slowFrame = LocOverMs.Value > 0f && Time.unscaledDeltaTime * 1000f > LocOverMs.Value;
      if (!slowFrame && CalmWait() && age < 10f) { sPreCalmWait++; return; }
      bool free = !slowFrame && spawnMs <= LocBudgetMs.Value && dgActive == 0 && locPendingAct.Count == 0 && !HeavyBusyOther("preinst-letin") && WbCanStart();
      if (!free && age < 10f) { sPreLetInWait++; return; }
      if (!free) sPreLetInForced++;
      TryClaimHeavy("preinst-letin");
    }
    try { op.allowSceneActivation = true; } catch { }
    preLetIn.Add(hash); locLandFrame = Time.frameCount; locLandHash = hash; locLandName = "pre-instantiate finishing " + LocName(hash);
  }
  static bool TryTakePreInst(int hash, Vector3 position, Quaternion rotation, out GameObject go) {
    go = null;
    if (hash == 0 || !preReady.TryGetValue(hash, out go) || go == null) { go = null; preReady.Remove(hash); return false; }
    preReady.Remove(hash); preWaitAt.Remove(hash); if (prebuilt.Remove(hash)) sPrebuiltUsed++;
    try { if (go.activeSelf) go.SetActive(false); go.transform.SetParent(null, false); go.transform.SetPositionAndRotation(position, rotation); }
    catch { try { UnityEngine.Object.Destroy(go); } catch { } go = null; return false; }
    return true;
  }
  static bool PreInstPending(int hash) {
    if (LocPreInst == null || !LocPreInst.Value || hash == 0) return false;
    PollPreInst();
    if (preReady.ContainsKey(hash) || SlReady(hash)) return false;
    if (PreDroppedRecently(hash) && !preOps.ContainsKey(hash)) return false; // 2.3.4: gave up on a copy: place it the normal way
    bool listed = false; try { listed = LocListedReal(hash) && LocMeasuredOk(hash) && !MinCopySmall(hash); } catch { } // 2.3.8: a remembered big location gets a copy as it comes near, too
    TryStartPreInst(hash, listed);
    return !preReady.ContainsKey(hash) && (preOps.ContainsKey(hash) || LocIsHeavyAsset(hash, LocAsset(hash)));
  }
  static void LocAssetBack() { if (locAssetDisabled) { locAssetDisabled = false; try { if (locAssetOff != null) locAssetOff.SetActive(true); } catch { } } }
  static void LocActivate(GameObject go, bool v) {
    LocAssetBack();
    if (go == null) return;
    if (v && locDeferNext && !go.activeSelf) { locPendingAct.Add(go); try { locPendingNames[go.GetInstanceID()] = LocName(locCurHash); } catch { } locPendingFrame = Time.frameCount; sLocSplit++; return; }
    go.SetActive(v);
  }
  // 1.7.5: one heavy job per frame among SoftCap/snap, InstantiateAsync completion, zone spawn, dungeon SoftEntry.
  static int heavyJobFrame = -1; static string heavyJobWhy = "-";
  static int prevHfFrame = -1; static string prevHfName = "-", prevHfOwner = "-"; static double prevHfMs;
  static int curHfFrame = -1; static string curHfName = "-", curHfOwner = "-"; static double curHfMs;
  static bool TryClaimHeavy(string why) {
    int f = Time.frameCount;
    if (heavyJobFrame == f && heavyJobWhy != why) return false;
    heavyJobFrame = f; heavyJobWhy = why; return true;
  }
  static bool HeavyBusyOther(string why) { return heavyJobFrame == Time.frameCount && heavyJobWhy != why; }
  static void NoteHeavyCandidate(string name, string owner, double ms) {
    int f = Time.frameCount;
    if (curHfFrame != f) {
      if (curHfFrame >= 0) { prevHfFrame = curHfFrame; prevHfName = curHfName; prevHfOwner = curHfOwner; prevHfMs = curHfMs; }
      curHfFrame = f; curHfName = name; curHfOwner = owner; curHfMs = ms;
    } else if (ms > curHfMs) { curHfName = name; curHfOwner = owner; curHfMs = ms; }
  }
  // 1.7.5 soft-cap: frame N activate under SoftCapBudgetMs; frame N+1 SnapToGround. Overlap-guarded.
  static void RunPendingLocActivation() {
    // Finish deferred snaps from a prior soft-cap activation first (own frame).
    if (locPendingSnap.Count > 0 && Time.frameCount != locPendingFrame) {
      if (!WbCanStart() || !TryClaimHeavy("softcap-snap")) return;
      var sw0 = Stopwatch.StartNew();
      string snapNames = "";
      try {
        for (int i = 0; i < locPendingSnap.Count; i++) {
          var g = locPendingSnap[i]; if (g == null) continue;
          int id = g.GetInstanceID(); string nm = locPendingNames.TryGetValue(id, out var nn) ? nn : g.name;
          if (snapNames.Length > 0) snapNames += ","; snapNames += nm;
        }
      } catch { }
      try { SnapToGround.SnappAll(); } catch { }
      for (int i = 0; i < locPendingSnap.Count; i++) { var g = locPendingSnap[i]; if (g != null) locPendingNames.Remove(g.GetInstanceID()); }
      locPendingSnap.Clear();
      locFrame = Time.frameCount; locCount = Math.Max(1, LocMaxPF.Value); sw0.Stop();
      if (LogSlow.Value) Log("location SoftCap SnapToGround " + (snapNames.Length > 0 ? snapNames : "?") + " " + sw0.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
      NoteHeavyCandidate("SoftCap.SnapToGround", "SpawnBudget", sw0.Elapsed.TotalMilliseconds); WbCharge(sw0.Elapsed.TotalMilliseconds); global::ValheimPerformanceFixes.FrameCost.Add("location", sw0.Elapsed.TotalMilliseconds, "ground snap " + snapNames);
      if (LocSoftCap != null && LocSoftCap.Value) return; // do not also activate more on the snap frame
    }
    if (locPendingAct.Count == 0 || Time.frameCount == locPendingFrame) return;
    if (!WbCanStart() || !TryClaimHeavy("softcap-act")) return;
    var sw = Stopwatch.StartNew(); int n = 0; string actNames = "";
    double bud = (LocSoftCap != null && LocSoftCap.Value) ? LocSoftCapMs.Value : 9999.0; bud = Math.Min(bud, Math.Max(0.5, WbLeft()));
    try {
      for (int i = 0; i < locPendingAct.Count; i++) {
        var go = locPendingAct[i];
        if (go == null || go.activeSelf) continue;
        if (n > 0 && sw.Elapsed.TotalMilliseconds >= bud) break;
        try {
          string nm = go.name; int id = go.GetInstanceID();
          if (locPendingNames.TryGetValue(id, out var stored)) nm = stored;
          go.SetActive(true); n++; locPendingSnap.Add(go); locPendingNames[id] = nm;
          if (actNames.Length > 0) actNames += ","; actNames += nm;
        } catch (Exception e) { Log("deferred location activation threw: " + e.Message); }
        if (sw.Elapsed.TotalMilliseconds >= bud) break;
      }
    } finally { locPendingAct.RemoveAll(g => g == null || g.activeSelf); }
    if (LocSoftCap != null && LocSoftCap.Value && locPendingSnap.Count > 0) {
      locPendingFrame = Time.frameCount; // snap next frame
      locFrame = Time.frameCount; locCount = Math.Max(1, LocMaxPF.Value); sw.Stop();
      if (LogSlow.Value) Log("location SoftCap activation " + (actNames.Length > 0 ? actNames : (n + " obj")) + " " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms (snap deferred)");
      NoteHeavyCandidate("SoftCap.Activation", "SpawnBudget", sw.Elapsed.TotalMilliseconds); WbCharge(sw.Elapsed.TotalMilliseconds); global::ValheimPerformanceFixes.FrameCost.Add("location", sw.Elapsed.TotalMilliseconds, "activation " + actNames);
      return;
    }
    try { SnapToGround.SnappAll(); } catch { }
    locPendingSnap.Clear();
    locFrame = Time.frameCount; locCount = Math.Max(1, LocMaxPF.Value); sw.Stop();
    if (LogSlow.Value) Log("location deferred activation " + (actNames.Length > 0 ? actNames : (n + " obj")) + " " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
    NoteHeavyCandidate("LocActivation", "SpawnBudget", sw.Elapsed.TotalMilliseconds); WbCharge(sw.Elapsed.TotalMilliseconds); global::ValheimPerformanceFixes.FrameCost.Add("location", sw.Elapsed.TotalMilliseconds, "activation " + actNames);
  }

  static bool IsHeavy(int hash) {
    if (locLastMs.TryGetValue(hash, out var ms) && ms > LocHeavyMs.Value) return true;
    var lst = LocHeavyList.Value ?? "";
    if (heavyNames == null || heavyListSeen != lst) { heavyListSeen = lst; heavyNames = new HashSet<string>(); foreach (var x in lst.Split(',')) { var t = x.Trim(); if (t.Length > 0) heavyNames.Add(t); } }
    return heavyNames.Count > 0 && heavyNames.Contains(LocName(hash));
  }
  static void LocPre(int hash) {
    locCurHash = hash; locDeferNext = false; locAssetOff = null;
    try {
      if (pbProxyHere) sPbLocKept++;
      if (locSplitOk && LocSplit.Value && genDepth == 0 && !pbProxyHere && locPendingAct.Count == 0 && locPendingSnap.Count == 0 && Settled()) {
        bool heavy = IsHeavy(hash) || proxyHeavyHashes.Contains(hash);
        GameObject asset = LocAsset(hash);
        if (asset != null && !heavy) { if (!locPieces.TryGetValue(hash, out int pc)) { pc = asset.GetComponentsInChildren<Transform>(true).Length; locPieces[hash] = pc; } heavy = pc > LocHeavyPieces.Value; }
        // 1.7.5: SoftCap LocationProxy path when prior proxy/create was slow (> SoftCapProxyCreateMs)
        if (!heavy && proxyHeavyHashes.Contains(hash)) heavy = true;
        if (heavy && asset != null && asset.activeSelf) { locAssetOff = asset; locDeferNext = true; } // deactivated later, see LocBeforeInstantiate
      }
    } catch { }
    locSw.Restart();
  }
  static void LocRestore() { LocAssetBack(); var a = locAssetOff; locAssetOff = null; locDeferNext = false; if (a != null && !a.activeSelf) try { a.SetActive(true); } catch { } }
  // 1.5.3: ZNetScene.RemoveObjects safety: never let it see a destroyed ZNetView or one without a ZDO.
  static readonly List<ZDO> rmDead = new List<ZDO>(); static int sRmPurged;
  // 2.1.0: cached field accessor instead of FieldInfo.GetValue every pass
  static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> instRef = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
  static int PurgeDead(Dictionary<ZDO, ZNetView> d) {
    if (d == null) return 0; rmDead.Clear();
    foreach (var kv in d) if (kv.Key == null || kv.Value == null || kv.Value.GetZDO() == null) rmDead.Add(kv.Key);
    for (int i = 0; i < rmDead.Count; i++) d.Remove(rmDead[i]);
    sRmPurged += rmDead.Count; return rmDead.Count;
  }
  static bool RemoveObjectsPre(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects) {
    lastHeld = 0;
    try { LocKeepAlive(currentNearObjects, currentDistantObjects); } catch (Exception e) { Log("location hysteresis threw: " + e.Message); }
    if ((RmGuardOnly == null || !RmGuardOnly.Value) && !global::ValheimPerformanceFixes.Overlap.Yield(global::ValheimPerformanceFixes.Feature.OrphanPrune)) {
      try { int n = PurgeDead(instRef(__instance)); if (n > 0) Log("ZNetScene.RemoveObjects guard: purged " + n + " dead instance(s) (total " + sRmPurged + ")"); }
      catch (Exception e) { Log("RemoveObjects guard failed: " + e.Message); }
    }
    // 2.1.0 #6: optional per-pass removal cap (in-game only; stands down if another mod changes RemoveObjects)
    if (RmCapOn != null && RmCapOn.Value && rmCapOk && Settled()) {
      if (!rmCapChecked) { rmCapChecked = true; if (!RmCapDecide()) return true; }
      // 2.1.1: FGN only throttles removals while its own limit is above 0; if that is switched on later (F1), hand removal to it
      if (fgnMaxDestroys != null && FgnLimit() > 0) { if (!rmFgnYield) { rmFgnYield = true; Log("removal cap: FiresGhettoNetworkMod 'Max Destroys Per Frame' is now " + FgnLimit() + ", its throttle handles removals"); } return true; }
      if (rmFgnYield) { rmFgnYield = false; Log("removal cap: FiresGhettoNetworkMod 'Max Destroys Per Frame' is 0 again, this cap handles removals"); }
      try { CappedRemove(__instance, currentNearObjects, currentDistantObjects); return false; }
      catch (Exception e) { rmCapOk = false; Log("removal cap threw, vanilla removal from now on: " + e.Message); }
    }
    return true;
  }
  // 2.1.1: FiresGhettoNetworkMod also patches RemoveObjects, but its throttle only acts when its own
  // "02 - Client Performance" / "Max Destroys Per Frame" is above 0 (0 = vanilla). So: stand down only for other mods,
  // or for FGN while that limit is above 0; otherwise our cap runs first (before FGN) and FGN's pass is vanilla anyway.
  const string FgnGuid = "com.Fire.FiresGhettoNetworkMod";
  static BepInEx.Configuration.ConfigEntryBase fgnMaxDestroys; static bool rmFgnYield;
  static double FgnLimit() { try { return fgnMaxDestroys == null ? 0 : Convert.ToDouble(fgnMaxDestroys.BoxedValue, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; } }
  static BepInEx.Configuration.ConfigEntryBase FindFgnLimit() {
    try {
      BepInEx.PluginInfo pi; if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(FgnGuid, out pi) || pi == null || pi.Instance == null) return null;
      var cfg = pi.Instance.Config; if (cfg == null) return null;
      BepInEx.Configuration.ConfigEntryBase e;
      if (((IDictionary<BepInEx.Configuration.ConfigDefinition, BepInEx.Configuration.ConfigEntryBase>)cfg).TryGetValue(new BepInEx.Configuration.ConfigDefinition("02 - Client Performance", "Max Destroys Per Frame"), out e) && e != null) return e;
      foreach (var kv in cfg) if (kv.Key != null && string.Equals(kv.Key.Key, "Max Destroys Per Frame", StringComparison.OrdinalIgnoreCase)) return kv.Value; // section renamed
    } catch (Exception ex) { Log("removal cap: reading FiresGhettoNetworkMod config failed: " + ex.Message); }
    return null;
  }
  // false = stand down for good
  static bool RmCapDecide() {
    var m = AccessTools.DeclaredMethod(typeof(ZNetScene), "RemoveObjects");
    var r = global::ValheimPerformanceFixes.PatchGuard.Rivals(m);
    if (r.Count == 0) { Log("removal cap: active (no other mod changes ZNetScene.RemoveObjects)"); return true; }
    var other = new List<string>(); bool fgn = false;
    foreach (var x in r) { if (x.IndexOf(FgnGuid, StringComparison.OrdinalIgnoreCase) >= 0) fgn = true; else other.Add(x); }
    if (other.Count > 0) { rmCapOk = false; Log("removal cap stands down: ZNetScene.RemoveObjects is also changed by " + string.Join(", ", r)); return false; }
    if (fgn) {
      fgnMaxDestroys = FindFgnLimit();
      if (fgnMaxDestroys == null) { rmCapOk = false; Log("removal cap stands down: FiresGhettoNetworkMod changes ZNetScene.RemoveObjects and its 'Max Destroys Per Frame' setting could not be read"); return false; }
      double lim = FgnLimit();
      if (lim > 0) { rmFgnYield = true; Log("removal cap stands down while FiresGhettoNetworkMod 'Max Destroys Per Frame' = " + lim + " (its throttle handles removals; checked again every pass)"); return true; }
      Log("removal cap: active alongside FiresGhettoNetworkMod (" + string.Join(", ", r) + "): its 'Max Destroys Per Frame' is 0 so it does not cap removals; this cap runs first and FGN's throttle takes over if that setting goes above 0");
    }
    return true;
  }
  // 2.3.6: something waits for a later object pass (removals over the cap, location proxies kept at the edge)
  static int lastHeld;
  internal static bool HoldingBack() => lastHeld > 0 || keptNow.Count > 0;
  static bool rmCapOk = true, rmCapChecked; static int sRmCapped, sRmCapFrames, kaStampFrame = -1; static readonly List<ZNetView> rmList = new List<ZNetView>();
  // Same steps as vanilla ZNetScene.RemoveObjects (mark everything in range, collect the rest, destroy it), but at most
  // RemovalsPerFrame objects per pass; the remainder is still out of range on the next pass and goes then. Dead entries are skipped.
  static void CappedRemove(ZNetScene zs, List<ZDO> near, List<ZDO> dist) {
    int f = Time.frameCount; byte b = (byte)(f & 0xFF);
    if (near != null) for (int i = 0; i < near.Count; i++) near[i].TempRemoveEarmark = b;
    if (dist != null && kaStampFrame != f) for (int i = 0; i < dist.Count; i++) dist[i].TempRemoveEarmark = b;
    var d = instRef(zs); if (d == null) return;
    rmList.Clear(); bool dead = false;
    foreach (var kv in d) { var nv = kv.Value; ZDO z = nv != null ? nv.GetZDO() : null; if (z == null) { dead = true; continue; } if (z.TempRemoveEarmark != b) rmList.Add(nv); }
    if (dead) { int n0 = PurgeDead(d); if (n0 > 0) Log("ZNetScene.RemoveObjects guard: purged " + n0 + " dead instance(s) (total " + sRmPurged + ")"); }
    int cap = Math.Max(1, RmCapN.Value), others = 0, held = 0; bool pb = PbOn();
    var zm = ZDOMan.instance;
    // 2.1.1: player builds are never capped: every one that is out of range goes this pass, in vanilla order; only other objects count against the cap
    for (int i = 0; i < rmList.Count; i++) {
      var nv = rmList[i]; if (nv == null) continue; var z = nv.GetZDO(); if (z == null) continue;
      bool mine = pb && PlayerBuilt(z);
      if (!mine) { if (others >= cap) { held++; continue; } others++; } else if (others >= cap) sPbRmKept++;
      nv.ResetZDO(); UnityEngine.Object.Destroy(nv.gameObject);
      if (!z.Persistent && z.IsOwner()) zm.DestroyZDO(z);
      d.Remove(z);
    }
    lastHeld = held;
    if (held > 0) { sRmCapped += held; sRmCapFrames++; }
    rmList.Clear();
  }
  // 2.1.0 #6: when the dead-entry scan is off, a NullReference/MissingReference inside RemoveObjects is caught here instead:
  // dead entries are removed and the pass is simply repeated next frame.
  static int sRmTrips; static float nextTripLog;
  static int sOvYield, sOvOrphan; // 2.3.0 Mod Overlap counters
  static Exception RemoveObjectsFin(ZNetScene __instance, Exception __exception) {
    if (__exception == null) return null;
    if (RmGuardOnly == null || !RmGuardOnly.Value) return __exception;
    if (!(__exception is NullReferenceException || __exception is MissingReferenceException)) return __exception;
    // 2.3.0 Mod Overlap: FiresGhettoNetworkMod catches exactly this error in its server pass, prunes and retries
    if (__exception is NullReferenceException && global::ValheimPerformanceFixes.Overlap.Yield(global::ValheimPerformanceFixes.Feature.OrphanPrune)) { sOvOrphan++; return __exception; }
    try {
      int n = PurgeDead(instRef(__instance)); sRmTrips++;
      if (Time.unscaledTime >= nextTripLog) { nextTripLog = Time.unscaledTime + 10f; Log("ZNetScene.RemoveObjects tripped over a broken entry (" + __exception.GetType().Name + "): purged " + n + " dead instance(s), continuing (trips " + sRmTrips + ")"); }
      return null;
    } catch { return __exception; }
  }
  // ---- 1.7.2 location edge hysteresis ----
  // Root cause of the 1.7.1 "same 3 locations re-instantiated every 2-10 s" loop: vanilla ZDOMan.FindSectorObjects collects distant
  // objects (LocationProxy is m_distant) inside a hard circle of (TotalSimulationDistance + 0.8) zones around the CENTRE of the zone
  // the player stands in, with no hysteresis. Moving back and forth over a 64 m zone line shifts that circle by one zone each time,
  // so proxies on its rim drop out (ZNetScene.RemoveObjects destroys proxy + location; the ZDO stays) and come back (new proxy
  // Awake -> ShouldDelayProxyLocationSpawning -> SpawnProxyLocation, full instantiate). Vanilla does the same. The split/prewarm
  // path is not the cause: SpawnLocation still returns the (inactive) root, LocationProxy stores it and clears m_locationNeedsSpawn.
  // Here: a proxy that just left the area is kept (earmarked like vanilla's in-area objects) for KeepLoadedSec while it is within
  // KeepExtraZones of the rim; one that was unloaded anyway is not re-created for ReloadCooldownSec unless it is close.
  static ConfigEntry<bool> HyOn; static ConfigEntry<float> HyKeepSec, HyCooldown, HyClose; static ConfigEntry<int> HyExtra;
  static readonly List<ZNetView> proxies = new List<ZNetView>(); static readonly HashSet<ZNetView> proxySet = new HashSet<ZNetView>();
  static readonly Dictionary<ZDO, float> proxyOutSince = new Dictionary<ZDO, float>();
  static readonly Dictionary<ZDOID, float> proxyUnloadedAt = new Dictionary<ZDOID, float>(); static readonly HashSet<ZDOID> proxySuppressedOnce = new HashSet<ZDOID>();
  static int kaFrame = -1; static readonly HashSet<ZDO> kaSeen = new HashSet<ZDO>(), keptNow = new HashSet<ZDO>(); static readonly Dictionary<Vector3Int, ZDOID> kaPos = new Dictionary<Vector3Int, ZDOID>(); static readonly List<string> kaNew = new List<string>();
  static int sLocKept, sLocKeptFrames, sLocSuppressed, sLocUnloaded, sLocDup;
  static AccessTools.FieldRef<LocationProxy, GameObject> lpInst; static AccessTools.FieldRef<LocationProxy, bool> lpNeeds;
  // 2.1.1 IncludeDungeons: a dungeon interior (DG_ object, own ZNetView) is kept and cooled down like a location proxy
  static readonly Dictionary<int, bool> dgPrefab = new Dictionary<int, bool>(); static readonly HashSet<int> dgHashes = new HashSet<int>();
  static int sDgKept, sDgSuppressed, sDgUnloaded;
  static bool IsDgHash(int h) => dgHashes.Contains(h);
  static void TrackProxy(ZDO zdo, GameObject go) {
    if (go == null || zdo == null) return; int ph = zdo.GetPrefab();
    if (ph != proxyHash) {
      if (HyDg == null || !HyDg.Value) return;
      if (!dgPrefab.TryGetValue(ph, out bool isDg)) { isDg = go.GetComponent<DungeonGenerator>() != null; dgPrefab[ph] = isDg; if (isDg) dgHashes.Add(ph); }
      if (!isDg) return;
    }
    var nv = go.GetComponent<ZNetView>(); if (nv == null || !proxySet.Add(nv)) return; proxies.Add(nv);
  }
  static int dgUnloadLastFrame = -1000, sDgUnloadSpread;
  static void LocKeepAlive(List<ZDO> near, List<ZDO> dist) {
    if (proxies.Count == 0 || HyOn == null) return;
    bool on = HyOn.Value && HyKeepSec.Value > 0f && Settled() && ZoneSystem.instance != null && ZNet.instance != null;
    byte b = (byte)(Time.frameCount & 0xFF); float now = Time.unscaledTime; Vector2s cz = default; int lim = 0;
    bool light = HyLight != null && HyLight.Value; int nearLim = 0; bool classic = false;
    if (on) {
      // same marks vanilla sets next. 2.1.0 LightScan: proxies are distant objects, so only the distant list is marked here;
      // a proxy in the near list is recognised by its sector (same rule ZDOMan uses to build the near list).
      if (near != null && !light) for (int i = 0; i < near.Count; i++) near[i].TempRemoveEarmark = b;
      if (dist != null) { for (int i = 0; i < dist.Count; i++) dist[i].TempRemoveEarmark = b; kaStampFrame = Time.frameCount; }
      cz = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition()); var simd = ZNet.instance.GetSyncedSimulationDistance(); lim = simd.TotalSimulationDistance + Math.Max(0, HyExtra.Value);
      nearLim = simd.NearSimulationDistance; classic = simd.IsClassic;
    }
    int f = Time.frameCount; if (kaFrame != f) { kaFrame = f; keptNow.Clear(); }
    kaSeen.Clear(); kaPos.Clear(); kaNew.Clear();
    for (int i = proxies.Count - 1; i >= 0; i--) {
      var nv = proxies[i]; ZDO z = nv != null ? nv.GetZDO() : null;
      if (z == null) { proxySet.Remove(nv); proxies.RemoveAt(i); continue; } // already unloaded/destroyed
      // 1.7.3: two tracked views on one ZDO must not be processed twice (the 2nd pass saw our own earmark and reset the timer)
      if (!kaSeen.Add(z)) { proxySet.Remove(nv); proxies.RemoveAt(i); sLocDupTracked++; continue; }
      if (!on) continue;
      if (z.TempRemoveEarmark == b || (light && InNearArea(z, cz, nearLim, classic))) { if (!keptNow.Contains(z)) proxyOutSince.Remove(z); continue; } // inside the vanilla area (or kept by us earlier this frame)
      bool first = false; if (!proxyOutSince.TryGetValue(z, out float since)) { since = now; proxyOutSince[z] = now; first = true; }
      bool isDgV = z.GetPrefab() != proxyHash;
      if (isDgV && (HyDg == null || !HyDg.Value)) { proxyOutSince.Remove(z); proxySet.Remove(nv); proxies.RemoveAt(i); continue; }
      // a near-type dungeon object leaves with the near area, so it is kept up to KeepExtraZones beyond the near area
      int klim = isDgV && !(nv != null && nv.m_distant) ? nearLim + Math.Max(0, HyExtra.Value) : lim;
      if (now - since < HyKeepSec.Value && ZoneSystem.instance.ZonesWithinRadius(cz, z.GetSector(), klim, true)) {
        z.TempRemoveEarmark = b; keptNow.Add(z); sLocKeptFrames++;
        if (first && isDgV) { sDgKept++; Vector3 zp = z.GetPosition(); string nm = "?"; try { nm = nv.name; } catch { } kaNew.Add("dungeon " + nm + " [" + z.m_uid + " @" + zp.x.ToString("0") + "," + zp.z.ToString("0") + "]"); }
        else if (first) {
          sLocKept++; int lh = z.GetInt(ZDOVars.s_location); Vector3 zp = z.GetPosition();
          var key = new Vector3Int(lh, Mathf.RoundToInt(zp.x), Mathf.RoundToInt(zp.z));
          if (kaPos.TryGetValue(key, out var other) && other != z.m_uid) { sLocSamePos++; if (LogSlow.Value) Log("WARN two location ZDOs at the same position: " + LocName(lh) + " " + other + " and " + z.m_uid + " at " + zp.x.ToString("0") + "," + zp.z.ToString("0")); }
          else kaPos[key] = z.m_uid;
          kaNew.Add(LocName(lh) + " [" + z.m_uid + " @" + zp.x.ToString("0") + "," + zp.z.ToString("0") + "]");
        }
        continue;
      }
      // 2.3.9: dungeon interiors are let go one per DungeonUnloadGapFrames (3 went in the same ms in the 2.3.8 logs); at most 2 s extra
      if (isDgV && DgUnSpread != null && DgUnSpread.Value && f - dgUnloadLastFrame < Math.Max(1, DgUnGap.Value) && now - since < HyKeepSec.Value + 2f) { z.TempRemoveEarmark = b; keptNow.Add(z); sDgUnloadSpread++; continue; }
      if (isDgV) dgUnloadLastFrame = f;
      proxyOutSince.Remove(z); proxySet.Remove(nv); proxies.RemoveAt(i); if (isDgV) { sDgUnloaded++; if (LogSlow.Value) { string nm = "?"; try { nm = nv.name; } catch { } Log("dungeon interior unloaded (left the area): " + nm + " [" + z.m_uid + "]"); } } else sLocUnloaded++; // vanilla unloads it now
      if (proxyUnloadedAt.Count > 4096) { proxyUnloadedAt.Clear(); proxySuppressedOnce.Clear(); }
      proxyUnloadedAt[z.m_uid] = now; proxySuppressedOnce.Remove(z.m_uid);
    }
    if (kaNew.Count > 0 && LogSlow.Value) Log("location(s) kept loaded at area edge (hysteresis): " + kaNew.Count + " -> " + string.Join(", ", kaNew));
    if (proxyOutSince.Count > 1024) proxyOutSince.Clear();
  }
  static bool InNearArea(ZDO z, Vector2s cz, int nearLim, bool classic) {
    var sc = z.GetSector(); int dx = Math.Abs(sc.x - cz.x), dy = Math.Abs(sc.y - cz.y); int ch = Math.Max(dx, dy);
    if (ch == 0) return true; if (ch > nearLim) return false;
    return classic || ZoneSystem.instance.ZonesWithinRadius(cz, sc, nearLim);
  }
  static bool SuppressReload(ZDO z, Vector3 rp) {
    if (proxyUnloadedAt.Count == 0 || !HyOn.Value) return false;
    int ph = z.GetPrefab(); bool isDgZ = ph != proxyHash;
    if (isDgZ && (HyDg == null || !HyDg.Value || !IsDgHash(ph))) return false;
    if (!proxyUnloadedAt.TryGetValue(z.m_uid, out float t)) return false;
    // 2.1.1: next to player builds a location comes back at once (PlaceLocationsBeforeBuilds)
    if (SfExtFirst != null && SfExtFirst.Value && PbNear(z.GetSector())) { proxyUnloadedAt.Remove(z.m_uid); proxySuppressedOnce.Remove(z.m_uid); return false; }
    float age = Time.unscaledTime - t;
    if (age >= HyCooldown.Value || !Settled()) { proxyUnloadedAt.Remove(z.m_uid); proxySuppressedOnce.Remove(z.m_uid); return false; }
    Vector3 d = z.GetPosition() - rp; d.y = 0f;
    if (d.sqrMagnitude <= HyClose.Value * HyClose.Value) { proxyUnloadedAt.Remove(z.m_uid); proxySuppressedOnce.Remove(z.m_uid); return false; }
    if (proxySuppressedOnce.Add(z.m_uid)) { if (isDgZ) sDgSuppressed++; else sLocSuppressed++; if (LogSlow.Value) Log((isDgZ ? "dungeon interior" : "location") + " re-spawn suppressed (hysteresis, unloaded " + age.ToString("0.0") + " s ago, " + d.magnitude.ToString("0") + " m away): " + (isDgZ ? "id " + z.m_uid : LocName(z.GetInt(ZDOVars.s_location)))); }
    return true;
  }
  // Never instantiate a second location under the same proxy instance (it already holds one).
  static bool ProxySpawnPre(LocationProxy __instance, ref bool __result) {
    try { if (lpInst != null && lpInst(__instance) != null) { lpNeeds(__instance) = false; __result = true; sLocDup++; return false; } } catch { }
    return true;
  }
  static Exception LocFin(Exception __exception) { LocRestore(); return __exception; }
  static void LocPost(int hash) {
    bool split = locDeferNext; LocRestore();
    locSw.Stop(); if (locFrame != Time.frameCount) locCount = 0; locCount++; locFrame = Time.frameCount; sLocSpawns++; double ms = locSw.Elapsed.TotalMilliseconds; if (Settled()) WbCharge(ms);
    if (InGame()) global::ValheimPerformanceFixes.FrameCost.Add("location", ms, LocName(hash) + (split ? " (instantiate)" : ""));
    if (!split) locLastMs[hash] = ms;
    if (split && LogSlow.Value) Log("location split: " + LocName(hash) + " instantiate " + ms.ToString("0.0") + " ms, activation next frame" + (ms < 8.0 ? " (pre-inst or light)" : ""));
    string n = null;
    if (ms > sLocMax) { sLocMax = ms; n = LocName(hash); sLocMaxName = n; }
    if (LogSlow.Value && ms > ListObjMs.Value) Log("SLOW location " + (n ?? LocName(hash)) + " " + ms.ToString("0.0") + " ms (" + (InGame() ? "world" : "loading") + ", spawn this frame " + spawnMs.ToString("0.0") + " ms, dt " + (Time.unscaledDeltaTime * 1000f).ToString("0") + " ms)");
  }
  static System.Reflection.MethodInfo getLoc; static readonly Dictionary<int, string> locNames = new Dictionary<int, string>();
  static string LocName(int hash) {
    if (locNames.TryGetValue(hash, out var n)) return n;
    n = hash.ToString();
    try { var zl = GetLoc(hash); if (zl != null && !string.IsNullOrEmpty(zl.m_prefabName)) n = zl.m_prefabName; } catch { }
    locNames[hash] = n; return n;
  }
  static readonly int proxyHash = "LocationProxy".GetStableHashCode(); static readonly HashSet<int> prewarmed = new HashSet<int>(); static float nextPrewarmClear; static int sPreEdge;
  static bool LocRemembered(int h) { if (h == 0 || locSlowNames.Count == 0) return false; string n = null; try { var zl = GetLoc(h); if (zl != null) n = zl.m_prefabName; } catch { } return !string.IsNullOrEmpty(n) && locSlowNames.Contains(n); }
  static void Prewarm(List<ZDO> l) {
    if (l == null || !LocPrewarm.Value || !(Settled() || ArrivalOk())) return;
    var zsys = ZoneSystem.instance; if (zsys == null) return;
    if (Time.unscaledTime > nextPrewarmClear) { prewarmed.Clear(); nextPrewarmClear = Time.unscaledTime + 10f; }
    for (int i = 0; i < l.Count; i++) {
      var z = l[i]; if (z == null || z.Created || z.GetPrefab() != proxyHash) continue;
      int h = z.GetInt(ZDOVars.s_location); if (h == 0 || !prewarmed.Add(h)) continue;
      bool loading; inPrewarm = true; try { loading = zsys.ShouldDelayProxyLocationSpawning(h); } catch { loading = false; } finally { inPrewarm = false; }
      try {
        if (loading) { sLocPrewarm++; LocWatch(h); Log("location prewarm requested: " + LocName(h)); }
        else {
          // 2.3.4: a remembered slow location at the edge of the loaded area gets its hidden copy now, not only when measured heavy
          bool rem = LaEdge != null && LaEdge.Value && LocRemembered(h) && LocMeasuredOk(h); // 2.3.5: unmeasured ones are placed normally once first
          if (rem && !preReady.ContainsKey(h) && !preOps.ContainsKey(h)) { if (TryStartPreInst(h, true)) sPreEdge++; else prewarmed.Remove(h); } // not started: try again on the next pass
          else TryStartPreInst(h);
        }
      } catch { }
    }
  }
  // ---- 2.3.1: location asset-ready tracking ----
  // A location asset that is still loading when its proxy comes in range finishes on some later frame; that frame can be long
  // without any measured main-thread work. The frame it turns ready is remembered so the hitch logger names it (owner
  // location-load), and assets that caused such a hitch are requested again behind the next loading / teleport screen.
  static readonly Dictionary<int, float> locWatch = new Dictionary<int, float>(); static readonly List<int> locWatchTmp = new List<int>();
  static int locLandFrame = -10, locLandHash; static string locLandName = "-";
  static readonly List<KeyValuePair<int, SoftReferenceableAssets.SoftReference<GameObject>>> locHeldRefs = new List<KeyValuePair<int, SoftReferenceableAssets.SoftReference<GameObject>>>();
  static ZoneSystem locRefZs; static int sLocLandFrames, sLocLandHitch, sLocReloads;
  static void LocWatch(int h) { if (h != 0 && locWatch.Count < 32 && !locWatch.ContainsKey(h)) locWatch[h] = Time.unscaledTime; }
  static void LocWatchTick() {
    var zs = ZoneSystem.instance;
    if (zs != locRefZs) { LocReleaseAll(); locRefZs = zs; locWatch.Clear(); locScreenOn = false; }
    if (zs == null) return;
    if (locWatch.Count > 0) {
      locWatchTmp.Clear(); float t = Time.unscaledTime;
      foreach (var kv in locWatch) {
        bool done = false;
        try { var zl = GetLoc(kv.Key); done = zl == null || zl.m_prefab.IsLoaded; } catch { done = true; }
        if (done) { if (InGame()) { locLandFrame = Time.frameCount; locLandHash = kv.Key; MarkAssetHitch(); locLandName = LocName(kv.Key) + " (asset ready after " + (t - kv.Value).ToString("0.0") + " s)"; sLocLandFrames++; } locWatchTmp.Add(kv.Key); }
        else if (t - kv.Value > 60f) locWatchTmp.Add(kv.Key);
      }
      for (int i = 0; i < locWatchTmp.Count; i++) locWatch.Remove(locWatchTmp[i]);
    }
    LocPreloadTick(zs);
    if (locSlowDirty && Time.unscaledTime >= locSaveAt) LocSlowSave();
  }
  // ---- 2.3.2: remembered slow location assets (by prefab name, saved per PC), loaded and kept loaded behind loading screens ----
  static ConfigEntry<bool> LaKeep, LaPre, LaPrebuild, LaEdge, LaMeasure, LaSpare; static ConfigEntry<string> LaStalls; static ConfigEntry<int> LaMax, LaPrebuildMax; static ConfigEntry<float> LaCalm, LaWaitCopy;
  // 2.3.4: one-time move of settings whose default changed: BepInEx keeps a saved value, so a value still at the old default is
  // moved to the new one once (a marker file remembers it was done, so a value you set yourself later is left alone)
  static void DefaultsBump() {
    try {
      var p = Path.Combine(Application.persistentDataPath, "HitchFix.defaults.txt");
      var done = File.Exists(p) ? new List<string>(File.ReadAllLines(p)) : new List<string>();
      const string key = "2.3.4 MaxPrebuiltCopies 4->8"; bool dirty = false;
      if (!done.Contains(key)) {
        if (LaPrebuildMax.Value == 4) { LaPrebuildMax.Value = 8; Log("setting moved to the new default: MaxPrebuiltCopies 4 -> 8 (one time only; set it again in the config if you want 4)"); }
        done.Add(key); dirty = true;
      }
      const string key239 = "2.3.9 SizeGuessCandidates 3->0";
      if (!done.Contains(key239)) {
        if (GuessSeeds != null && GuessSeeds.Value == 3) { GuessSeeds.Value = 0; Log("setting moved to the new default: SizeGuessCandidates 3 -> 0 (one time only; set it again in the config if you want 3)"); }
        done.Add(key239); dirty = true;
      }
      if (dirty) File.WriteAllLines(p, done.ToArray());
    } catch (Exception e) { Log("default update check failed: " + e.Message); }
  }
  // 2.3.3: hidden pre-built copies of remembered slow locations, made behind loading screens
  static readonly HashSet<int> prebuilt = new HashSet<int>(), prebuildTried = new HashSet<int>(); static int prebuildNext, sPrebuilt, sPrebuiltUsed;
  // 2.3.4: copies are built worst recorded hitch first. An asset that is still loading is waited for (its slot is kept free) instead
  // of being skipped; one slot is always kept for the worst one (a lower-ranked ready copy is dropped for it if needed). Why each
  // remembered location did or did not get a copy is logged in one line when the loading screen ends.
  static readonly List<string> prebuildOrder = new List<string>();
  static readonly Dictionary<string, string> prebuildWhy = new Dictionary<string, string>();
  static readonly Dictionary<int, float> prebuildWaitAt = new Dictionary<int, float>();
  static int sPrebuildWaits, sPrebuildEvicted, sPrebuildLimit, sPrebuildUnmeasured; static string preWhy = "-";
  static readonly HashSet<int> prebuildLimitCounted = new HashSet<int>(); // 2.3.5: limitHit counts each location once per loading screen
  static int PrebuildCap() { return Mathf.Clamp(LaPrebuildMax != null ? LaPrebuildMax.Value : 8, 1, 16); }
  static int PreReadyCap() { return Math.Min(Math.Max(8, PrebuildCap() + 2), ResMaxV()); } // 2.3.8: MaxReserveCopies
  static float LocWorstMs(string n) { return n != null && locSlowMs.TryGetValue(n, out var v) ? v : 0f; }
  // 2.3.7: measured ones by worst hitch, then unmeasured ones by remembered load stall (unknown last)
  static readonly Dictionary<string, float> locStallMs = new Dictionary<string, float>();
  static float LocStallMs(string n) { return n != null && locStallMs.TryGetValue(n, out var v) ? v : 0f; }
  static float RankKey(string n) { float w = LocWorstMs(n); if (w > 0f) return 1e6f + w; float st = LocStallMs(n); return st > 0f ? st : GuessRank(n); } // 2.3.8: then size guess (< 1)
  static void SortWorstFirst(List<string> l) {
    for (int i = 1; i < l.Count; i++) { var x = l[i]; float mx = RankKey(x); int j = i - 1; while (j >= 0 && RankKey(l[j]) < mx) { l[j + 1] = l[j]; j--; } l[j + 1] = x; } // stable: equal keys keep list order
  }
  static string MsText(string n) { float v = LocWorstMs(n); if (v > 0f) return v.ToString("0") + " ms"; float s = LocStallMs(n); return s > 0f ? "not measured yet, load stall " + s.ToString("0") + " ms" : n != null && locGuess.Contains(n) ? "size guess " + (locSize.TryGetValue(n, out var gr) ? gr.Guess().ToString("0") : "?") + " MB" : "not measured yet"; }
  static int sPrebuildSpare, sStallsImported;
  static void LocStallNote(string n, float ms) {
    if (string.IsNullOrEmpty(n) || ms <= 0f || ms <= LocStallMs(n)) return;
    locStallMs[n] = ms; if (locSlowNames.Contains(n)) locSlowDirty = true;
  }
  static void LocStallImport(string[] old) {
    if (LaKeep == null || !LaKeep.Value) return;
    var inv = System.Globalization.CultureInfo.InvariantCulture; var fl = System.Globalization.NumberStyles.Float;
    var seen = new List<string>();
    if (LaStalls != null && !string.IsNullOrEmpty(LaStalls.Value))
      foreach (var part in LaStalls.Value.Split(',')) {
        int eq = part.IndexOf('='); if (eq <= 0) continue;
        string n = part.Substring(0, eq).Trim(); if (!float.TryParse(part.Substring(eq + 1).Trim(), fl, inv, out float ms)) continue;
        if (n.Length == 0) continue;
        if (!locSlowNames.Contains(n) && locSlowNames.Count < (LaMax != null ? LaMax.Value : 16)) { locSlowNames.Add(n); locSlowDirty = true; }
        if (ms > LocStallMs(n)) { LocStallNote(n, ms); seen.Add(n + " " + ms.ToString("0") + " ms (setting)"); }
      }
    if (old != null) {
      var rx = new System.Text.RegularExpressions.Regex(@"HITCH (\d+) ms \(frame \d+\): owner=location-load \|.*?location asset: ([^\s|(]+)");
      foreach (var line in old) {
        if (line.IndexOf("owner=location-load", StringComparison.Ordinal) < 0) continue;
        var m = rx.Match(line); if (!m.Success) continue;
        string n = m.Groups[2].Value.Trim(); float.TryParse(m.Groups[1].Value, fl, inv, out float ms);
        if (!locSlowNames.Contains(n) || ms <= LocStallMs(n)) continue;
        LocStallNote(n, ms); sStallsImported++; seen.Add(n + " " + ms.ToString("0") + " ms (last session's log)");
      }
    }
    if (seen.Count > 0) { Log("remembered location load stalls (rank unmeasured ones for spare copy slots): " + string.Join(", ", seen.ToArray())); if (locSlowDirty) locSaveAt = 0f; }
  }
  static void LocPrebuildTick(ZoneSystem zs) {
    if (LaPrebuild == null || !LaPrebuild.Value || LaPre == null || !LaPre.Value || LocPreInst == null || !LocPreInst.Value || locSlowNames.Count == 0) return;
    if (Time.frameCount < prebuildNext || zs.m_locations == null || zs.m_locations.Count == 0) return;
    prebuildNext = Time.frameCount + 5;
    int max = PrebuildCap(); float t = Time.unscaledTime; bool busy = preOps.Count > 0;
    prebuildOrder.Clear(); prebuildOrder.AddRange(locSlowNames); SortWorstFirst(prebuildOrder);
    int have = PrebuiltReady(); foreach (var kv in preOps) if (prebuilt.Contains(kv.Key)) have++;
    int reserve = 0; bool worstNeeds = true, seenWorst = false;
    for (int r = 0; r < prebuildOrder.Count; r++) {
      string n = prebuildOrder[r]; int h = LocHashByName(zs, n);
      if (h == 0) { prebuildWhy[n] = "not in this world"; continue; }
      bool isWorst = !seenWorst; seenWorst = true; // the worst one that exists in this world
      if (preReady.ContainsKey(h)) { prebuildWhy[n] = prebuilt.Contains(h) ? "copy ready" : "copy ready (made earlier)"; if (isWorst) worstNeeds = false; continue; }
      if (preOps.ContainsKey(h)) { prebuildWhy[n] = "copy being built"; if (isWorst) worstNeeds = false; continue; }
      if (SlReady(h)) { prebuildWhy[n] = "light copy ready, no full copy needed"; if (isWorst) worstNeeds = false; continue; }
      if (prebuildTried.Contains(h)) { if (isWorst) worstNeeds = false; continue; } // reason already noted on this screen
      // 2.3.5: cost not known yet: no copy (it is placed the normal way once and timed); it does not count as the worst one
      bool spare = false;
      if (!LocMeasuredOk(n)) {
        if (isWorst) { seenWorst = false; isWorst = false; } // an unmeasured one never counts as the worst one
        if (LaSpare == null || !LaSpare.Value) { prebuildTried.Add(h); prebuildWhy[n] = "not measured yet (placed the normal way once to time it)"; sPrebuildUnmeasured++; continue; }
        spare = true; // 2.3.7: may take a slot the measured ones left free
      }
      ZoneSystem.ZoneLocation zl = null; try { zl = GetLoc(h); } catch { }
      if (zl == null) { prebuildTried.Add(h); prebuildWhy[n] = "not in this world"; continue; }
      bool valid = false, loaded = false; try { valid = zl.m_prefab.IsValid; loaded = valid && zl.m_prefab.IsLoaded; } catch { }
      if (!valid) { prebuildTried.Add(h); prebuildWhy[n] = "asset reference not valid"; continue; }
      if (!loaded && spare) { prebuildWhy[n] = "not measured yet; asset still loading (no slot kept for it)"; continue; }
      if (!loaded) {
        if (!prebuildWaitAt.TryGetValue(h, out float w0)) { prebuildWaitAt[h] = w0 = t; sPrebuildWaits++; }
        prebuildWhy[n] = "asset still loading (waited " + (t - w0).ToString("0.0") + " s)";
        reserve++;
        if (t - w0 < 1.5f) return; // worse ones first: lower-ranked copies wait a little for it
        continue; // waited long enough: lower-ranked ones may go, but its slot stays free
      }
      if (busy) { prebuildWhy[n] = "waiting: another copy is being built"; return; }
      if (preReady.Count >= PreReadyCap() || have >= max - reserve) {
        if (isWorst && worstNeeds && PrebuildEvict(n, false)) have--;
        else if (!spare && PrebuildEvict(n, true)) have--; // 2.3.7: a measured one takes its slot back from a spare-slot copy
        else if (spare) { prebuildWhy[n] = "not measured yet; no spare copy slot (placed the normal way once to time it)"; if (prebuildLimitCounted.Add(h)) sPrebuildUnmeasured++; continue; }
        else { prebuildWhy[n] = "copy limit reached (MaxPrebuiltCopies=" + max + (reserve > 0 ? ", " + reserve + " kept free for worse ones still loading" : "") + ")"; if (prebuildLimitCounted.Add(h)) sPrebuildLimit++; continue; } // 2.3.5: counted once per location per screen
      }
      prebuildTried.Add(h);
      if (TryStartPreInst(h, true)) {
        prebuilt.Add(h); sPrebuilt++; prebuildWhy[n] = spare ? "copy started (spare slot, not measured)" : "copy started"; if (spare) sPrebuildSpare++;
        Log("location copy being pre-built behind the loading screen: " + n + " (" + (spare ? "spare slot: " : "worst ") + MsText(n) + ", rank " + (r + 1) + ", " + (have + 1) + "/" + max + ")");
      } else prebuildWhy[n] = "start refused: " + preWhy;
      return; // one per tick; the copy itself is made over the next frames
    }
  }
  // drops the lowest-ranked ready pre-built copy so the worst one gets a slot
  static bool PrebuildEvict(string forName, bool spareOnly) {
    var zs = ZoneSystem.instance; if (zs == null) return false;
    for (int i = prebuildOrder.Count - 1; i > 0; i--) {
      string n = prebuildOrder[i]; int h = LocHashByName(zs, n);
      if (h == 0 || !prebuilt.Contains(h) || !preReady.TryGetValue(h, out var go)) continue;
      if (spareOnly && LocMeasuredOk(n)) continue;
      preReady.Remove(h); prebuilt.Remove(h); try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
      sPrebuildEvicted++; prebuildWhy[n] = "copy dropped to make room for " + forName;
      Log("location copy dropped to make room for a worse one: " + n + " (" + MsText(n) + ") for " + forName + " (" + MsText(forName) + ")");
      return true;
    }
    return false;
  }
  static void PrebuildReport() {
    if (prebuildWhy.Count == 0) return;
    var sb = new StringBuilder("hidden location copies behind this loading screen (worst first):");
    for (int i = 0; i < prebuildOrder.Count; i++) { string n = prebuildOrder[i]; if (prebuildWhy.TryGetValue(n, out var w)) sb.Append(i == 0 ? " " : " | ").Append(n).Append(" (").Append(MsText(n)).Append("): ").Append(w); }
    sb.Append(" | ready now=").Append(PrebuiltReady()).Append("/").Append(PrebuildCap());
    Log(sb.ToString()); prebuildWhy.Clear();
  }
  static readonly List<string> locSlowNames = new List<string>(); static bool locSlowDirty; static float locSaveAt;
  static readonly Dictionary<string, float> locSlowMs = new Dictionary<string, float>(); // 2.3.4: worst hitch seen per remembered asset
  static bool locScreenOn, locScreenDone; static int locScreenTries, locScreenNext, sPreCalmWait, sLocScreens, sLocScreensNone; static string locLastWhy = "-";
  static float assetHitchAt = -100f, roughAt = -100f;
  static string LocSlowPath() { try { return Path.Combine(Application.persistentDataPath, "HitchFix.slowlocations.txt"); } catch { return null; } }
  static void LocSlowLoad() {
    try {
      if (LaKeep == null || !LaKeep.Value) return;
      var p = LocSlowPath(); if (p == null || !File.Exists(p)) { Log("slow location assets: no saved list yet"); return; }
      // 2.3.4: "name<TAB>worst ms"; lines from 2.3.3 have only the name (ms unknown, ranked after known ones)
      foreach (var line in File.ReadAllLines(p)) {
        var raw = line.Trim(); if (raw.Length == 0 || raw.StartsWith("#")) continue;
        if (raw.StartsWith("!")) { var nb = raw.Substring(1).Trim(); if (nb.Length > 0) locNotBig.Add(nb); continue; } // 2.3.8: measured as not big
        // 2.3.7: optional third column = remembered load stall in ms
        var col = raw.Split('\t'); string n = col[0].Trim(); float ms = 0f, st = 0f;
        if (col.Length > 1) float.TryParse(col[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ms);
        if (col.Length > 2) float.TryParse(col[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out st);
        if (n.Length == 0) continue;
        if (col.Length > 3 && int.TryParse(col[3].Trim(), out int fc) && fc > 0) locFast[n] = fc; // 2.3.8: fast placements in a row
        locNotBig.Remove(n);
        if (!locSlowNames.Contains(n)) locSlowNames.Add(n);
        if (ms > LocWorstMs(n)) locSlowMs[n] = ms;
        if (st > LocStallMs(n)) locStallMs[n] = st;
      }
      while (locSlowNames.Count > LaMax.Value) LocSlowDropOne();
      var shown = new List<string>(); foreach (var n in locSlowNames) shown.Add(n + " " + MsText(n));
      Log("slow location assets: " + locSlowNames.Count + " remembered from earlier sessions" + (locSlowNames.Count > 0 ? " (" + string.Join(", ", shown.ToArray()) + ")" : ""));
    } catch (Exception e) { Log("slow location assets: could not read the saved list: " + e.Message); }
  }
  static void LocSlowSave() {
    locSlowDirty = false;
    if (LaKeep == null || !LaKeep.Value) return;
    try {
      var p = LocSlowPath(); if (p == null) return;
      var lines = new List<string> { "# location assets that caused a hitch when they finished loading (written by the performance mod; safe to delete)" };
      lines.Add("# name<TAB>worst hitch in ms (0 = not measured yet)<TAB>load stall in ms (0 = none seen)<TAB>normal placements under BigLocationMs in a row");
      lines.Add("# !name = measured as not big (not tried again as a size guess)");
      foreach (var n in locSlowNames) { if (locGuess.Contains(n)) continue; int fc; locFast.TryGetValue(n, out fc); lines.Add(n + "\t" + LocWorstMs(n).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "\t" + LocStallMs(n).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "\t" + fc); }
      int nbc = 0; foreach (var nb in locNotBig) { if (locSlowNames.Contains(nb)) continue; if (++nbc > 300) break; lines.Add("!" + nb); }
      File.WriteAllLines(p, lines.ToArray());
    } catch (Exception e) { Log("slow location assets: could not save the list: " + e.Message); }
  }
  // finds a location by its prefab name in the current world
  static readonly Dictionary<string, int> locByName = new Dictionary<string, int>();
  static int LocHashByName(ZoneSystem zs, string name) {
    if (locByName.TryGetValue(name, out int h)) return h;
    h = 0;
    try {
      int g = name.GetStableHashCode(); var zl = GetLoc(g);
      if (zl != null) h = g;
      else if (zs.m_locations != null) foreach (var l in zs.m_locations) { if (l != null && l.m_prefabName == name) { h = l.Hash; break; } }
    } catch { h = 0; }
    if (h != 0) locByName[name] = h; // unknown names are tried again on the next screen (the location list may not be ready yet)
    return h;
  }
  static bool LocHeld(int h) { foreach (var r in locHeldRefs) if (r.Key == h) return true; return false; }
  // once per loading screen: request every remembered slow asset that this mod does not already hold (even if the game has it
  // loaded right now: the game lets go of its own reference after placing a location, ours keeps the asset in memory)
  static void LocPreloadTick(ZoneSystem zs) {
    bool l = Loading();
    if (!l) {
      if (locScreenOn && !locScreenDone) LocScreenReport(0, 0, 0, 0, "the loading screen ended before the world's location list was ready");
      if (locScreenOn) { try { PrebuildReport(); } catch { } locScreenEndT = Time.unscaledTime; }
      locScreenOn = false; return;
    }
    if (!locScreenOn) { locScreenStartT = Time.unscaledTime; locScreenOn = true; locScreenDone = false; locScreenTries = 0; locScreenNext = 0; sLocScreens++; prebuildTried.Clear(); prebuildWaitAt.Clear(); prebuildWhy.Clear(); prebuildLimitCounted.Clear(); }
    try { LocPrebuildTick(zs); } catch (Exception e) { Log("location pre-build behind loading screen: " + e.Message); }
    if (locScreenDone || Time.frameCount < locScreenNext) return;
    locScreenNext = Time.frameCount + 30; locScreenTries++;
    if (LaPre == null || !LaPre.Value) { LocScreenReport(0, 0, 0, 0, "PreloadBehindLoadingScreens is off"); return; }
    if (locSlowNames.Count == 0) { LocScreenReport(0, 0, 0, 0, "no slow location assets remembered yet"); return; }
    if (zs.m_locations == null || zs.m_locations.Count == 0) { if (locScreenTries > 20) LocScreenReport(0, 0, 0, 0, "the world has no location list yet"); return; }
    int asked = 0, held = 0, unknown = 0, invalid = 0; var names = new List<string>();
    for (int i = 0; i < locSlowNames.Count; i++) {
      string n = locSlowNames[i]; int h = LocHashByName(zs, n);
      if (h == 0) { unknown++; continue; }
      if (LocHeld(h)) { held++; continue; }
      try {
        var zl = GetLoc(h); if (zl == null) { unknown++; continue; }
        var sr = zl.m_prefab; if (!sr.IsValid) { invalid++; continue; }
        sr.LoadAsync(); locHeldRefs.Add(new KeyValuePair<int, SoftReferenceableAssets.SoftReference<GameObject>>(h, sr)); NoteReq(h); sLocReloads++; asked++; names.Add(n);
        while (locHeldRefs.Count > LaMax.Value) { try { locHeldRefs[0].Value.Release(); } catch { } locHeldRefs.RemoveAt(0); }
      } catch (Exception e) { invalid++; Log("location asset request failed: " + n + " " + e.Message); }
    }
    if (unknown > 0 && asked == 0 && held == 0 && locScreenTries <= 20) return; // the location list may still be filling in: try again
    string why = asked > 0 ? null : held > 0 && unknown == 0 && invalid == 0 ? "all " + held + " remembered assets are already loaded and kept" : unknown > 0 && held == 0 && invalid == 0 ? "none of the " + unknown + " remembered assets exist in this world" : "nothing left to request (" + held + " kept, " + unknown + " not in this world, " + invalid + " without an asset)";
    LocScreenReport(asked, held, unknown, invalid, why, names);
  }
  static void LocScreenReport(int asked, int held, int unknown, int invalid, string why, List<string> names = null) {
    locScreenDone = true;
    if (asked == 0) { sLocScreensNone++; locLastWhy = why; Log("location assets behind this loading screen: nothing requested: " + why + " | kept loaded now=" + locHeldRefs.Count); return; }
    locLastWhy = asked + " requested";
    Log("location assets behind this loading screen: requested " + asked + " (" + string.Join(", ", names.ToArray()) + ")" + (held > 0 ? ", " + held + " already kept" : "") + (unknown > 0 ? ", " + unknown + " not in this world" : "") + (invalid > 0 ? ", " + invalid + " without an asset" : "") + " | kept loaded now=" + locHeldRefs.Count + " (until you log out)");
  }
  // true = a location asset just finished loading with a hitch and the frames have not been calm for CalmSecAfterAssetHitch yet
  // 2.3.3 FIX: the rough-frame time is now recorded every frame by CalmTick (2.3.2 only recorded it inside this check, which ran
  // only on frames that were not slow, so it never saw a rough frame and never waited)
  static bool CalmWait() {
    if (LaCalm == null || LaCalm.Value <= 0f) return false;
    float t = Time.unscaledTime;
    return t - assetHitchAt < 20f && t - roughAt < LaCalm.Value;
  }
  static void CalmTick() {
    float lim = LocOverMs != null && LocOverMs.Value > 0f ? LocOverMs.Value : 15f;
    if (Time.unscaledDeltaTime * 1000f > lim) roughAt = Time.unscaledTime;
  }
  static int LocUnmeasured() { int n = 0; foreach (var s in locSlowNames) if (LocWorstMs(s) <= 0f) n++; return n; }
  static int PrebuiltReady() { int n = 0; foreach (var h in prebuilt) if (preReady.ContainsKey(h)) n++; return n; }
  static void MarkAssetHitch() { assetHitchAt = roughAt = Time.unscaledTime; }
  static void LocRememberSlow(int h, double ms) {
    if (h == 0) return;
    string n = null; try { var zl = GetLoc(h); if (zl != null) n = zl.m_prefabName; } catch { }
    if (string.IsNullOrEmpty(n)) return;
    float m = (float)ms; LocStallNote(n, m); // 2.3.7
    int i = locSlowNames.IndexOf(n); float big = BigV();
    if (i >= 0 && big > 0f && m >= big) { if (locFast.Remove(n)) locSlowDirty = true; if (locGuess.Remove(n)) { sGuessKept++; locSlowDirty = true; locSaveAt = Time.unscaledTime + 3f; Log("size guess confirmed: " + n + " (its asset load froze the game for " + m.ToString("0") + " ms; kept and saved)"); } } // 2.3.8
    if (i < 0 && big > 0f && m < big) { sBigUnder++; return; } // 2.3.8: under BigLocationMs: not remembered
    if (i < 0 && big > 0f) { LocBigAdd(n, h, m, "its asset load froze the game for " + m.ToString("0") + " ms"); return; }
    if (i >= 0) {
      // 2.3.4: already listed: keep the worst hitch seen so copies are built worst-first
      if (m > LocWorstMs(n) + 0.5f) { float was = LocWorstMs(n); locSlowMs[n] = m; locSlowDirty = true; locSaveAt = Time.unscaledTime + 3f; Log("slow location asset worst hitch updated: " + n + " " + (was > 0f ? was.ToString("0") : "?") + " -> " + m.ToString("0") + " ms"); }
      return;
    }
    locSlowNames.Add(n); locSlowMs[n] = m; locByName[n] = h; while (locSlowNames.Count > (LaMax != null ? LaMax.Value : 16)) LocSlowDropOne();
    locSlowDirty = true; locSaveAt = Time.unscaledTime + 3f;
    Log("slow location asset remembered: " + n + " (" + m.ToString("0") + " ms, " + locSlowNames.Count + " on the list" + (LaKeep != null && LaKeep.Value ? ", saved for the next session" : "") + ")");
  }
  // list full: drop the one with the smallest known hitch (the oldest among equal ones)
  static void LocSlowDropOne() {
    if (locSlowNames.Count == 0) return;
    int k = 0; for (int i = 1; i < locSlowNames.Count; i++) if (RankKey(locSlowNames[i]) < RankKey(locSlowNames[k])) k = i;
    locSlowMs.Remove(locSlowNames[k]); locStallMs.Remove(locSlowNames[k]); locFast.Remove(locSlowNames[k]); locGuess.Remove(locSlowNames[k]); locSlowNames.RemoveAt(k);
  }
  static void LocReleaseAll() { ReserveReset(); if (locSlowDirty) LocSlowSave(); locByName.Clear(); foreach (var r in locHeldRefs) { try { r.Value.Release(); } catch { } } locHeldRefs.Clear(); }
  // ---- zones ----
  static int zoneFrame = -1, zoneDefer, sZones, sZoneDeferred;
  static bool ZonePre(Vector2s zoneID, ZoneSystem.SpawnMode mode, ref GameObject root, ref bool __result) {
    if (mode == ZoneSystem.SpawnMode.Client) { try { EarlyLoadAround(zoneID); } catch { } } // 2.3.9
    if (!ZoneThrottle.Value || mode != ZoneSystem.SpawnMode.Client) return true;
    if (PbNear(zoneID)) { sPbZoneKept++; zoneDefer = 0; return true; } // 2.1.1: area with player builds -> never postponed
    try {
      if (!Settled()) { zoneDefer = 0; return true; }
      NewFrame(); int f = Time.frameCount;
      string why = zoneFrame >= f - 1 ? "zoneLastFrame" : locFrame >= f - 1 ? "location" : dgActive > 0 || dgLoading > 0 ? "dungeon" : spawnMs > BudgetMs.Value || prevSpawnMs > BudgetMs.Value ? "objBudget" : Time.unscaledDeltaTime * 1000f > ZoneHeavyMs.Value ? "heavyFrame" : HeavyBusyOther("zone") ? "heavyJob" : !WbCanStart() ? "worldBudget" : null;
      if (why == null) { if (!TryClaimHeavy("zone")) why = "heavyJob"; }
      if (why == null || zoneDefer >= Math.Max(1, Math.Min(ZoneMaxDefer.Value, 60))) { zoneDefer = 0; zoneT0 = coSw.Elapsed.TotalMilliseconds; zoneAcc0 = global::ValheimPerformanceFixes.FrameCost.Total; return true; }
      if (zoneDefer == 0 && LogSlow.Value) {
        string owner = "";
        if (why == "heavyFrame" || why == "heavyJob") owner = " owner=" + prevHfName + "/" + prevHfOwner + " " + prevHfMs.ToString("0.0") + "ms" + (heavyJobWhy != "-" ? (" job=" + heavyJobWhy) : "");
        Log("zone spawn postponed (" + why + ", dt " + (Time.unscaledDeltaTime * 1000f).ToString("0") + " ms" + owner + ")");
      }
      zoneDefer++; sZoneDeferred++; root = null; __result = false; return false;
    } catch { return true; }
  }
  static double zoneT0 = -1, zoneAcc0;
  static void ZonePost(ZoneSystem.SpawnMode mode, bool __result) {
    if (zoneT0 >= 0) { double ms = coSw.Elapsed.TotalMilliseconds - zoneT0; zoneT0 = -1; if (__result) { WbCharge(ms); global::ValheimPerformanceFixes.FrameCost.AddOwn("zone", ms, zoneAcc0, "zone build"); } }
    if (__result && mode == ZoneSystem.SpawnMode.Client) { zoneFrame = Time.frameCount; sZones++; }
  }
  // ---- dungeons ----
  static bool dgOk = true; static int dgActive, sDungeons, sDgFrames, sDgAborted; static double sDgMaxFrame;
  static System.Reflection.MethodInfo placeRoom, release, dnsBegin, dnsEnd;
  // 2.1.0: cached delegates instead of MethodInfo.Invoke
  static Func<DungeonGenerator, DungeonDB.RoomData, Vector3, Quaternion, RoomConnection, ZoneSystem.SpawnMode, Room> placeRoomD; static Action<DungeonGenerator> releaseD;
  static Func<DungeonGenerator, bool> dnsBeginD; static Action dnsEndD;
  // 2.1.0 fix: the one-job-per-frame claim is per dungeon instance (two dungeons both named "dungeon" used to pass together)
  static string DgClaim(DungeonGenerator dg) { if (DgPerInstance == null || !DgPerInstance.Value || dg == null) return "dungeon"; return "dungeon#" + dg.GetInstanceID(); } static int dgLoading, sDgStaggered; static double sDgStaggerWall;
  static readonly AccessTools.FieldRef<DungeonGenerator, List<SoftReferenceableAssets.IReferenceCounted>> heldRefs = AccessTools.FieldRefAccess<DungeonGenerator, List<SoftReferenceableAssets.IReferenceCounted>>("m_heldReferences");
  static readonly AccessTools.FieldRef<DungeonGenerator, ZDO> dgLoadingZdo = AccessTools.FieldRefAccess<DungeonGenerator, ZDO>("m_zdoSetToBeLoadingInZone");
  static readonly AccessTools.FieldRef<DungeonGenerator, ZNetView> dgNview = AccessTools.FieldRefAccess<DungeonGenerator, ZNetView>("m_nview");
  static Action<DungeonGenerator, SoftReferenceableAssets.AssetID, SoftReferenceableAssets.LoadResult> onRoomLoaded;
  // Replaces vanilla LoadRoomPrefabsAsync (same bookkeeping) but issues the LoadAsync calls a few unique assets at a time.
  static bool SoftDungeon() { try { return DgSoftEntry != null && DgSoftEntry.Value && InGame(); } catch { return false; } }
  static ConfigEntry<bool> DgUnSpread, DgVerify, DgStagModded; static ConfigEntry<int> DgUnGap; static ConfigEntry<string> DgVanillaNames, DgStagExclude;
  static string[] dgVanCache, dgExCache; static string dgVanSrc, dgExSrc; static readonly HashSet<string> dgSkipLogged = new HashSet<string>(); static int sDgStagSkipped;
  static string[] SplitList(string s) { var l = new List<string>(); if (s != null) foreach (var p in s.Split(',')) { var t = p.Trim(); if (t.Length > 0) l.Add(t); } return l.ToArray(); }
  // 2.3.9: null = stagger, otherwise the reason it stays vanilla
  static string DgStaggerSkip(DungeonGenerator dg) {
    if (dg == null) return null;
    string n = dg.name ?? ""; string root = ""; try { root = dg.transform.root.name.Replace("(Clone)", ""); } catch { }
    if (DgStagExclude != null) {
      if (dgExSrc != DgStagExclude.Value) { dgExSrc = DgStagExclude.Value; dgExCache = SplitList(dgExSrc); }
      foreach (var e in dgExCache) if (n.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0 || root.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return "in StaggerExcludeList (" + e + ")";
    }
    if (DgStagModded != null && !DgStagModded.Value) {
      if (dgVanSrc != DgVanillaNames.Value) { dgVanSrc = DgVanillaNames.Value; dgVanCache = SplitList(dgVanSrc); }
      bool van = false; foreach (var v in dgVanCache) if (n.StartsWith(v, StringComparison.OrdinalIgnoreCase)) { van = true; break; }
      if (!van) return "not in VanillaDungeonNames and StaggerModdedDungeons is off";
    }
    return null;
  }
  // ---- 2.3.9: room check after staggered loading ----
  static int sDgChecked, sDgMissing, sDgRebuilt, sDgReactivated, sDgStuck, sDgForced;
  static string DgCheckStats() { return "dungeon checks=" + sDgChecked + " missingRooms=" + sDgMissing + " rebuilt=" + sDgRebuilt + " switchedOn=" + sDgReactivated + " stuck=" + sDgStuck + " finished=" + sDgForced + " staggerSkipped=" + sDgStagSkipped + " unloadsSpread=" + sDgUnloadSpread; }
  static bool DgSpreading(DungeonGenerator dg) { foreach (var j in dgJobs) if (j.dg == dg && !j.done) return true; foreach (var j in dgLoadJobs) if (j.dg == dg && !j.done) return true; return false; }
  static System.Collections.IEnumerator DgCheckRooms(DungeonGenerator dg, Array rooms, string name) {
    float t0 = Time.unscaledTime; bool forced = false;
    while (true) {
      for (int k = 0; k < 10; k++) yield return null;
      if (dg == null) yield break;
      bool built = false; try { built = loadedRooms(dg) == null && !DgSpreading(dg); } catch { yield break; }
      if (built) break;
      if (Time.unscaledTime - t0 < 20f || DgSpreading(dg)) continue;
      if (forced) { Log("dungeon room check: " + name + " still not built after it was finished by hand; left to the game"); yield break; }
      forced = true; sDgStuck++; int left = -1; try { left = roomsToLoad(dg); } catch { }
      Log("dungeon room check: " + name + " still waiting for " + left + " room load(s) after 20 s; loading the rest now and building it");
      try {
        int cnt = 0;
        for (int i = 0; i < rooms.Length; i++) { var rd = (DungeonDB.RoomData)fRd.GetValue(rooms.GetValue(i)); var pref = rd.m_prefab; if (!pref.IsLoaded) { pref.Load(); heldRefs(dg).Add(pref); cnt++; } }
        if (loadedRooms(dg) != null && onRoomLoaded != null && rooms.Length > 0) {
          roomsToLoad(dg) = 1; var rd0 = (DungeonDB.RoomData)fRd.GetValue(rooms.GetValue(0));
          onRoomLoaded(dg, rd0.m_prefab.m_assetID, SoftReferenceableAssets.LoadResult.Succeeded); sDgForced++;
          Log("dungeon room check: " + name + " finished (" + cnt + " room asset(s) loaded at once)");
        }
      } catch (Exception e) { Log("dungeon room check: finishing " + name + " threw: " + e.Message); yield break; }
      t0 = Time.unscaledTime;
    }
    if (DgVerify == null || !DgVerify.Value) yield break;
    try {
      var have = new List<Transform>(); var tr = dg.transform;
      for (int c = 0; c < tr.childCount; c++) { var ch = tr.GetChild(c); if (ch != null && ch.GetComponent<Room>() != null) have.Add(ch); }
      int created = 0, missing = 0, rebuilt = 0, inactive = 0; var miss = new List<string>();
      bool ctx = false;
      try {
        for (int i = 0; i < rooms.Length; i++) {
          object r = rooms.GetValue(i); var rd = (DungeonDB.RoomData)fRd.GetValue(r); Vector3 pos = (Vector3)fPos.GetValue(r); Quaternion rot = (Quaternion)fRot.GetValue(r);
          string rn = rd.m_prefab.Name; int found = -1;
          for (int k = 0; k < have.Count; k++) { var t = have[k]; if (t == null || t.name != rn) continue; if ((t.position - pos).sqrMagnitude < 1f) { found = k; break; } }
          if (found >= 0) {
            created++; var go = have[found].gameObject; have.RemoveAt(found);
            if (!go.activeSelf) { go.SetActive(true); inactive++; sDgReactivated++; }
            continue;
          }
          missing++; sDgMissing++; miss.Add(rn + " @" + pos.x.ToString("0") + "," + pos.y.ToString("0") + "," + pos.z.ToString("0"));
          if (placeRoomD == null) continue;
          if (!ctx) { try { if (dnsBeginD != null) ctx = dnsBeginD(dg); } catch { } }
          try { placeRoomD(dg, rd, pos, rot, null, ZoneSystem.SpawnMode.Client); rebuilt++; sDgRebuilt++; } catch (Exception e) { Log("dungeon room check: building " + rn + " threw: " + e.Message); }
        }
        if (rebuilt > 0) try { SnapToGround.SnappAll(); } catch { }
      } finally { if (ctx) try { dnsEndD(); } catch { } }
      sDgChecked++;
      if (missing > 0 || inactive > 0) Log("dungeon room check: " + name + " " + DgWhere(dg) + " planned=" + rooms.Length + " created=" + created + " missing=" + missing + " rebuilt=" + rebuilt + " switchedOn=" + inactive + (miss.Count > 0 ? " -> " + string.Join(", ", miss) : ""));
      else if (LogSlow.Value) Log("dungeon room check: " + name + " planned=" + rooms.Length + " all built and on");
    } catch (Exception e) { Log("dungeon room check threw for " + name + ": " + e.Message); }
  }
  static bool LoadRoomsPre(DungeonGenerator __instance) {
    if (genDepth > 0) return true;
    NoteDgBuild(__instance);
    try { if ((SfDgInterior == null || !SfDgInterior.Value) && __instance != null && PbNearPos(__instance.transform.position)) { sPbDgKept++; return true; } } catch { } // 2.1.1: interiors are spread even near builds unless SpreadDungeonInteriorsNearBuilds is off
    if (!(DgStagger.Value || SoftDungeon())) return true;
    if (!SoftDungeon() && !Settled()) return true;
    try { string why = DgStaggerSkip(__instance); if (why != null) { sDgStagSkipped++; if (dgSkipLogged.Add(__instance.name ?? "?")) Log("dungeon room loads not staggered: " + __instance.name + " " + why); return true; } } catch { }
    try {
      var rooms = loadedRooms(__instance); if (rooms == null || rooms.Length == 0) return true;
      if (onRoomLoaded == null) onRoomLoaded = AccessTools.MethodDelegate<Action<DungeonGenerator, SoftReferenceableAssets.AssetID, SoftReferenceableAssets.LoadResult>>(AccessTools.DeclaredMethod(typeof(DungeonGenerator), "OnRoomLoaded"));
      if (heldRefs(__instance) == null) return true;
      ZLog.Log("Loading room prefabs asynchronously (SpawnBudget staggered)");
      if (dgLoadingZdo(__instance) == null) { var z = dgNview(__instance)?.GetZDO(); if (z == null) return true; dgLoadingZdo(__instance) = z; ZoneSystem.instance.SetLoadingInZone(z); }
      roomsToLoad(__instance) = rooms.Length;
      inst.StartCoroutine(StaggerLoads(__instance, rooms));
      return false;
    } catch (Exception e) { Log("dungeon stagger setup failed, vanilla: " + e.Message); return true; }
  }
  // 2.1.1: one in-flight count for all dungeons (GlobalLoadsInFlight); the frame a room asset lands in is measured (MeasureRoomLoadFrames)
  static int dgInFlightAll, dgLandFrame = -10, sDgLandFrames, sDgLandSlow, sDgWaitFrames, sDgForcedStarts, sDgCostSplits, sDgOwnFrameRooms; static string dgLandName = "-"; static float dgFlightTouched; static double sDgLandMax, sDgSnapMax;
  // the frame ledger total, so nested timers do not count the same work twice
  static double dgAccounted => global::ValheimPerformanceFixes.FrameCost.Total;
  static double DgAccount(double ms, double accountedAtStart, string what) { return global::ValheimPerformanceFixes.FrameCost.AddOwn("dungeon", ms, accountedAtStart, what); }
  static System.Collections.IEnumerator StaggerLoads(DungeonGenerator dg, Array rooms) {
    string claim = DgClaim(dg); dgLoading++; var wall = Stopwatch.StartNew(); string name = dg.name; int n = rooms.Length, frames = 0, uniq = 0, waits = 0, waited = 0; int inFlight = 0; double frameMaxMs = 0;
    var job = new DgLoadJob { dg = dg, rooms = rooms, n = n }; dgLoadJobs.Add(job);
    var started = new HashSet<SoftReferenceableAssets.AssetID>();
    try {
      while (!job.done && job.i < n) {
        if (dg == null) { job.done = true; Log("dungeon stagger aborted (generator destroyed): " + name); yield break; }
        // 1.7.5: SoftEntry room loads never share a frame with SoftCap / PreInst / zone
        if (SoftDungeon() && !TryClaimHeavy(claim)) { yield return null; continue; }
        bool budgeted = DgBudgetIssue != null && DgBudgetIssue.Value;
        // 2.1.1: load requests wait while the shared world budget is used up (bounded)
        if (budgeted && !WbCanStart() && waits < (DgMaxWait != null ? DgMaxWait.Value : 30)) { waits++; waited++; sDgWaitFrames++; yield return null; continue; }
        waits = 0;
        frames++;
        int issuedNew = 0, issued = 0;
        var frameSw = Stopwatch.StartNew(); double acc0 = dgAccounted;
        double wallMs = (DgSoftEntry != null && DgSoftEntry.Value) ? DgWallMs.Value : 9999.0;
        if (budgeted) wallMs = Math.Min(wallMs, Math.Min(DgBudgetMs.Value, WbLeft()));
        bool global = DgGlobalFlight != null && DgGlobalFlight.Value;
        while (job.i < n) {
          var rd = (DungeonDB.RoomData)fRd.GetValue(rooms.GetValue(job.i));
          var pref = rd.m_prefab;
          bool isNew = !started.Contains(pref.m_assetID);
          int maxFlight = SoftDungeon() ? 1 : DgMaxLoads.Value; int flying = global ? dgInFlightAll : inFlight;
          if (isNew && (flying >= maxFlight || issuedNew >= maxFlight) && frames < 600) break;
          if (issued > 0 && frameSw.Elapsed.TotalMilliseconds >= wallMs) break;
          if (isNew) { started.Add(pref.m_assetID); uniq++; inFlight++; dgInFlightAll++; dgFlightTouched = Time.realtimeSinceStartup; issuedNew++; }
          var d = dg; bool counted = isNew;
          heldRefs(dg).Add(pref);
          job.i++; issued++;
          pref.LoadAsync((id, res) => {
            if (counted) { counted = false; inFlight--; if (dgInFlightAll > 0) dgInFlightAll--; dgFlightTouched = Time.realtimeSinceStartup; dgLandFrame = Time.frameCount; dgLandName = name; }
            onRoomLoaded(d, id, res); });
          if (dg == null) { job.done = true; yield break; }
          if (job.done) break; // a save flush (from inside a load callback) requested the rest
        }
        double ims = frameSw.Elapsed.TotalMilliseconds;
        double own = DgAccount(ims, acc0, name + " room load requests");
        if (budgeted) WbCharge(Math.Max(0.0, own));
        if (ims > frameMaxMs) frameMaxMs = ims;
        if (!job.done && job.i < n) yield return null;
      }
      if (job.done) { Log("dungeon stagger handed over to save flush: " + name + " rooms=" + n + " issueFrames=" + frames); yield break; }
      job.done = true;
      sDgStaggered++; if (frameMaxMs > sDgStaggerWall) sDgStaggerWall = frameMaxMs;
      Log("dungeon room loads staggered: " + name + " " + DgWhere(dg) + " rooms=" + n + " uniquePrefabs=" + uniq + " issueFrames=" + frames + (waited > 0 ? " budgetWaitFrames=" + waited : "") + " wallTotal=" + wall.ElapsedMilliseconds + "ms frameMax=" + frameMaxMs.ToString("0") + "ms");
      if (fRd != null) try { inst.StartCoroutine(DgCheckRooms(dg, rooms, name)); } catch { }
    } finally { dgLoading--; dgLoadJobs.Remove(job); }
  } static System.Reflection.FieldInfo fRd, fPos, fRot;
  static readonly AccessTools.FieldRef<DungeonGenerator, int> roomsToLoad = AccessTools.FieldRefAccess<DungeonGenerator, int>("m_roomsToLoad");
  static readonly AccessTools.FieldRef<DungeonGenerator, Array> loadedRooms = AccessTools.FieldRefAccess<DungeonGenerator, Array>("m_loadedRooms");
  static bool RoomLoadedPre(DungeonGenerator __instance, SoftReferenceableAssets.LoadResult result) {
    if (genDepth > 0) return true;
    // 2.1.1: a dungeon whose loads were all requested by a save flush builds its rooms at once (vanilla) when the last one lands
    try { if (flushDg.Count > 0 && __instance != null && roomsToLoad(__instance) <= 1 && flushDg.Remove(__instance.GetInstanceID())) return true; } catch { }
    if (!dgOk || !(DgSpread.Value || SoftDungeon())) return true;
    try { if ((SfDgInterior == null || !SfDgInterior.Value) && __instance != null && PbNearPos(__instance.transform.position)) return true; } catch { } // 2.1.1: next to player builds -> vanilla only when interiors near builds are not spread
    try {
      if (!InGame() || result != SoftReferenceableAssets.LoadResult.Succeeded || __instance == null || __instance.gameObject == null) return true;
      if (roomsToLoad(__instance) > 1) return true; // not the last room: vanilla just decrements
      var rooms = loadedRooms(__instance); if (rooms == null) return true;
      roomsToLoad(__instance) = 0;
      inst.StartCoroutine(SpreadSpawn(__instance, rooms));
      return false;
    } catch (Exception e) { Log("dungeon spread setup failed, vanilla: " + e); return true; }
  }
  // 2.1.1 LearnRoomCost: per room asset, a slowly decaying high estimate of its placement time
  static readonly Dictionary<SoftReferenceableAssets.AssetID, double> roomCost = new Dictionary<SoftReferenceableAssets.AssetID, double>();
  static double KnownRoomCost(DungeonDB.RoomData rd) { if (DgLearn == null || !DgLearn.Value || rd == null) return -1; try { return roomCost.TryGetValue(rd.m_prefab.m_assetID, out var v) ? v : -1; } catch { return -1; } }
  static void LearnRoomCost(DungeonDB.RoomData rd, double ms) { if (DgLearn == null || !DgLearn.Value || rd == null) return; try { if (roomCost.Count > 2048) roomCost.Clear(); var k = rd.m_prefab.m_assetID; roomCost[k] = roomCost.TryGetValue(k, out var v) ? Math.Max(ms, v * 0.75 + ms * 0.25) : ms; } catch { } }
  // nothing else has used the shared world budget in this frame yet
  static bool WbFresh() { if (!WbActive()) return true; WbRoll(); return wbJobs == 0; }
  static string DgWhere(DungeonGenerator dg) { try { var p = dg.transform.position; var zc = ZoneSystem.GetZone(p); return "at " + p.x.ToString("0") + "," + p.z.ToString("0") + " (zone " + zc.x + "," + zc.y + ")"; } catch { return "at ?"; } }
  // 2.1.1: every dungeon build is logged with its position and how often this one was built this session
  static readonly Dictionary<ZDOID, int> dgBuilds = new Dictionary<ZDOID, int>();
  static void NoteDgBuild(DungeonGenerator dg) {
    try {
      if (dg == null) return; var z = dgNview(dg)?.GetZDO(); int k = 1;
      if (z != null) { if (dgBuilds.Count > 4096) dgBuilds.Clear(); dgBuilds.TryGetValue(z.m_uid, out k); k++; dgBuilds[z.m_uid] = k; }
      Log("dungeon build: " + dg.name + " " + DgWhere(dg) + (z != null ? " id " + z.m_uid : "") + " build #" + k + " this session" + (PbNearPos(dg.transform.position) ? " (next to player builds)" : ""));
    } catch { }
  }
  // vanilla dungeon builds (last room loaded -> all rooms at once) are timed for the hitch log
  static double dgTimeStart = -1, dgTimeAcc; static int dgTimeDepth;
  static void DgTimePre() { if (dgTimeDepth++ == 0) { dgTimeStart = coSw.Elapsed.TotalMilliseconds; dgTimeAcc = dgAccounted; } }
  static Exception DgTimeFin(DungeonGenerator __instance, Exception __exception) {
    try { if (dgTimeDepth > 0 && --dgTimeDepth == 0 && dgTimeStart >= 0) { double ms = coSw.Elapsed.TotalMilliseconds - dgTimeStart; dgTimeStart = -1;
      string nm = "?"; try { nm = __instance != null ? __instance.name : "?"; } catch { }
      double own = DgAccount(ms, dgTimeAcc, nm + " room loaded"); if (own > 4.0) NoteHeavyCandidate(nm + " build", "dungeon", own); } } catch { }
    return __exception;
  }
  static System.Collections.IEnumerator SpreadSpawn(DungeonGenerator dg, Array rooms) {
    string claim = DgClaim(dg); int wbWait = 0;
    var job = new DgJob { dg = dg, rooms = rooms, n = rooms.Length }; dgJobs.Add(job);
    dgActive++; sDungeons++; int n = job.n; var sw = new Stopwatch(); string name = dg.name; var total = Stopwatch.StartNew(); int frames = 0, waited = 0, forced = 0; double snapMs = -1;
    try {
      Log("dungeon spread start: " + name + " rooms=" + n + " " + DgWhere(dg));
      while (!job.done && job.i < n) {
        if (dg == null) { job.done = true; sDgAborted++; Log("dungeon spread aborted (generator destroyed): " + name); yield break; }
        if (SoftDungeon() && HeavyBusyOther(claim)) { yield return null; continue; }
        int maxWait = DgMaxWait != null ? DgMaxWait.Value : 30; bool go;
        if (DgStrict != null && DgStrict.Value) {
          // 2.1.1: no room while the shared world budget is used up; a room known to be slower than what is left waits for a frame nothing else used
          double left0 = WbLeft(); double known = KnownRoomCost((DungeonDB.RoomData)fRd.GetValue(rooms.GetValue(job.i)));
          go = left0 > 0.0 && (known < 0 || known <= left0 || WbFresh());
        } else go = wbWait >= 3 || WbCanStart(); // old rule: after 3 waiting frames the dungeon goes anyway
        if (!go && wbWait < maxWait) { wbWait++; waited++; sDgWaitFrames++; yield return null; continue; }
        if (!go) { forced++; sDgForcedStarts++; } // waited MaxWaitFrames: one room goes so the dungeon can never get stuck
        if (SoftDungeon() && !TryClaimHeavy(claim)) { yield return null; continue; }
        wbWait = 0;
        sw.Restart(); frames++; sDgFrames++; double acc0 = dgAccounted;
        double roomBudget = (DgSoftEntry != null && DgSoftEntry.Value) ? Math.Min(DgBudgetMs.Value, DgWallMs.Value) : DgBudgetMs.Value;
        roomBudget = Math.Min(roomBudget, WbLeft());
        int placed = 0;
        bool ctx = false; try { if (dnsBeginD != null) ctx = dnsBeginD(dg); } catch (Exception e) { Log("DropNSpawn context begin threw: " + e.Message); }
        try {
          while (job.i < n) {
            object r = rooms.GetValue(job.i); var rd = (DungeonDB.RoomData)fRd.GetValue(r);
            if (placed > 0) {
              double el = sw.Elapsed.TotalMilliseconds; if (el >= roomBudget) break;
              double k = KnownRoomCost(rd); if (k > 0 && el + k > roomBudget) { sDgCostSplits++; break; } // known to be too slow for what is left -> next frame
            }
            job.i++; double t0 = sw.Elapsed.TotalMilliseconds;
            try { placeRoomD(dg, rd, (Vector3)fPos.GetValue(r), (Quaternion)fRot.GetValue(r), null, ZoneSystem.SpawnMode.Client); }
            catch (Exception e) { Log("PlaceRoom threw (room " + (job.i - 1) + "): " + e); }
            double rms = sw.Elapsed.TotalMilliseconds - t0; LearnRoomCost(rd, rms); placed++;
            if (DgLearn != null && DgLearn.Value && rms >= roomBudget) { if (placed == 1) sDgOwnFrameRooms++; break; } // a slow room ends the frame
          }
        } finally { if (ctx) try { dnsEndD(); } catch { } }
        double ms = sw.Elapsed.TotalMilliseconds; if (ms > sDgMaxFrame) sDgMaxFrame = ms; WbCharge(ms);
        DgAccount(ms, acc0, name + " " + placed + " room(s)"); if (ms > 4.0) NoteHeavyCandidate(name + " rooms", "dungeon", ms);
        if (job.i < n) yield return null;
      }
      if (job.done) { Log("dungeon spread finished by save flush: " + name + " rooms=" + n + " frames=" + frames); yield break; }
      if (dg == null) { job.done = true; sDgAborted++; yield break; }
      if (DgSnapOwn != null && DgSnapOwn.Value) {
        // 2.1.1: the final ground snap + release run in a frame of their own (nothing else charged yet), bounded by MaxWaitFrames
        int sWait = 0; yield return null;
        while (!job.done) {
          if (dg == null) { job.done = true; sDgAborted++; yield break; }
          bool free = WbFresh() && !HeavyBusyOther(claim);
          if (!free && sWait < (DgMaxWait != null ? DgMaxWait.Value : 30)) { sWait++; waited++; sDgWaitFrames++; yield return null; continue; }
          TryClaimHeavy(claim); break;
        }
        if (job.done) { Log("dungeon spread finished by save flush: " + name + " rooms=" + n + " frames=" + frames); yield break; }
      }
      job.done = true;
      sw.Restart(); double accS = dgAccounted;
      { bool ctx = false; try { if (dnsBeginD != null) ctx = dnsBeginD(dg); } catch { }
        try { SnapToGround.SnappAll(); } catch (Exception e) { Log("dungeon SnapToGround threw: " + e.Message); } finally { if (ctx) try { dnsEndD(); } catch { } } }
      loadedRooms(dg) = null;
      try { releaseD(dg); } catch (Exception e) { Log("ReleaseHeldReferences threw: " + e); }
      snapMs = sw.Elapsed.TotalMilliseconds; if (DgSnapOwn != null && DgSnapOwn.Value) WbCharge(snapMs); if (snapMs > sDgSnapMax) sDgSnapMax = snapMs;
      DgAccount(snapMs, accS, name + " ground snap + release"); if (snapMs > 4.0) NoteHeavyCandidate(name + " snap", "dungeon", snapMs);
      Log("dungeon spread done: " + name + " rooms=" + n + " frames=" + frames + " snap+release=" + snapMs.ToString("0.0") + "ms" + (waited > 0 ? " budgetWaitFrames=" + waited : "") + (forced > 0 ? " wentAnyway=" + forced : "") + " wall=" + total.ElapsedMilliseconds + "ms");
    } finally { dgActive--; dgJobs.Remove(job); }
  }
  // ---- 2.1.1 save safety ----
  sealed class DgJob { public DungeonGenerator dg; public Array rooms; public int i, n; public bool done; }
  sealed class DgLoadJob { public DungeonGenerator dg; public Array rooms; public int i, n; public bool done; }
  static readonly List<DgJob> dgJobs = new List<DgJob>(); static readonly List<DgLoadJob> dgLoadJobs = new List<DgLoadJob>();
  static readonly HashSet<int> flushDg = new HashSet<int>();
  // zones the world owner is generating right now (Full/Ghost SpawnZone on a zone not yet generated), innermost last
  static readonly List<Vector2s> genOpen = new List<Vector2s>(); static int genDepth { get { return genOpen.Count; } }
  static bool flushing, commitOk = true; static int sFlushes, sCommitCuts, sGenThrew;
  static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> zsGenLive = AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
  static bool ZoneGen(ZoneSystem zs, Vector2s z) { var h = zsGenLive(zs); return h != null && h.Contains(z); }
  static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> zsGenClone = AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_tempGeneratedZonesSaveClone");
  static readonly AccessTools.FieldRef<ZoneSystem, List<ZoneSystem.LocationInstance>> zsLocClone = AccessTools.FieldRefAccess<ZoneSystem, List<ZoneSystem.LocationInstance>>("m_tempLocationsSaveClone");
  // true = this PC owns the world and HostSpawnsAtOnce is on -> every split path stays vanilla (all at once)
  static bool OwnerAtOnce() { try { return ZNet.instance != null && ZNet.instance.IsServer() && (SfOwner == null || SfOwner.Value); } catch { return true; } }
  static void GenPre(ZoneSystem __instance, Vector2s zoneID, ZoneSystem.SpawnMode mode, out bool __state) {
    __state = false;
    try { if ((mode == ZoneSystem.SpawnMode.Full || mode == ZoneSystem.SpawnMode.Ghost) && !ZoneGen(__instance, zoneID)) { genOpen.Add(zoneID); __state = true; } } catch { }
  }
  static Exception GenFin(ZoneSystem __instance, Vector2s zoneID, Exception __exception, bool __state) {
    if (!__state) return __exception;
    try {
      int k = genOpen.LastIndexOf(zoneID); if (k >= 0) genOpen.RemoveAt(k);
      if (__exception != null) { sGenThrew++; bool g = false; try { g = ZoneGen(__instance, zoneID); } catch { }
        Log("zone generation threw in zone " + zoneID.ToString() + " (" + __exception.GetType().Name + ": " + __exception.Message + "); zone generated=" + g + (g ? "" : ", it will be generated again on the next visit")); }
    } catch { }
    return __exception;
  }
  // Runs right after vanilla copied the generated-zone and location lists for the save; edits the copies only.
  static void CommitPost(ZoneSystem __instance) {
    if (!commitOk || SfCommit == null || !SfCommit.Value || genOpen.Count == 0) return;
    try {
      var open = new HashSet<Vector2s>(genOpen); int z = 0, l = 0;
      var gz = zsGenClone(__instance); if (gz != null) foreach (var v in open) if (gz.Remove(v)) z++;
      var lc = zsLocClone(__instance);
      if (lc != null) for (int i = 0; i < lc.Count; i++) { var li = lc[i]; if (li.m_placed && open.Contains(ZoneSystem.GetZone(li.m_position))) { li.m_placed = false; lc[i] = li; l++; } }
      sCommitCuts++;
      Log("save safety: " + open.Count + " zone(s) still being generated were saved as not generated (zones " + z + ", locations " + l + ")");
    } catch (Exception e) { Log("save safety commit gate threw: " + e.Message); }
  }
  static void FlushSaveWorld() { FlushPending("ZNet.SaveWorld"); }
  static void FlushSave() { FlushPending("ZNet.Save"); }
  static void FlushNetShutdown() { FlushPending("ZNet.Shutdown"); }
  static void FlushGameShutdown() { FlushPending("Game.Shutdown"); }
  // Finish synchronously everything this module is still spreading over frames. Idempotent and cheap when idle.
  static void FlushPending(string why) {
    if (flushing || SfFlush == null || !SfFlush.Value) return;
    if (locPendingAct.Count == 0 && locPendingSnap.Count == 0 && dgJobs.Count == 0 && dgLoadJobs.Count == 0) return;
    flushing = true; var sw = Stopwatch.StartNew(); int la = 0, dgs = 0, rooms = 0, loads = 0;
    try {
      for (int i = 0; i < locPendingAct.Count; i++) { var go = locPendingAct[i]; if (go == null || go.activeSelf) continue;
        try { go.SetActive(true); la++; locPendingSnap.Add(go); } catch (Exception e) { Log("save flush: location activation threw: " + e.Message); } }
      locPendingAct.Clear();
      if (locPendingSnap.Count > 0) {
        try { SnapToGround.SnappAll(); } catch (Exception e) { Log("save flush: SnapToGround threw: " + e.Message); }
        for (int i = 0; i < locPendingSnap.Count; i++) { var g = locPendingSnap[i]; if (g != null) locPendingNames.Remove(g.GetInstanceID()); }
        locPendingSnap.Clear();
      }
      foreach (var j in dgJobs.ToArray()) { if (j.done) continue; dgs++; rooms += FinishDungeon(j); }
      foreach (var j in dgLoadJobs.ToArray()) { if (j.done) continue; loads += RequestAllLoads(j); }
    } catch (Exception e) { Log("save flush threw: " + e); }
    finally { flushing = false; }
 sFlushes++; global::ValheimPerformanceFixes.FrameCost.Add("save", sw.Elapsed.TotalMilliseconds, "flush before save (" + why + ")");
    Log("save flush (" + why + "): " + la + " location(s) switched on, " + dgs + " dungeon(s) finished (" + rooms + " rooms), " + loads + " waiting room load(s) requested, " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
  }
  static int FinishDungeon(DgJob j) {
    j.done = true; var dg = j.dg; if (dg == null) return 0; int placed = 0;
    bool ctx = false; try { if (dnsBeginD != null) ctx = dnsBeginD(dg); } catch (Exception e) { Log("DropNSpawn context begin threw: " + e.Message); }
    try {
      while (j.i < j.n) { object r = j.rooms.GetValue(j.i++);
        try { placeRoomD(dg, (DungeonDB.RoomData)fRd.GetValue(r), (Vector3)fPos.GetValue(r), (Quaternion)fRot.GetValue(r), null, ZoneSystem.SpawnMode.Client); placed++; }
        catch (Exception e) { Log("PlaceRoom threw (room " + (j.i - 1) + "): " + e.Message); } }
      try { SnapToGround.SnappAll(); } catch (Exception e) { Log("save flush: SnapToGround threw: " + e.Message); }
    } finally { if (ctx) try { dnsEndD(); } catch { } }
    loadedRooms(dg) = null;
    try { releaseD(dg); } catch (Exception e) { Log("ReleaseHeldReferences threw: " + e); }
    return placed;
  }
  // Asset loads cannot be finished synchronously; request every remaining one now and let the last one build all rooms at once.
  static int RequestAllLoads(DgLoadJob j) {
    j.done = true; var dg = j.dg; if (dg == null) return 0; int c = 0;
    if (flushDg.Count > 64) flushDg.Clear();
    flushDg.Add(dg.GetInstanceID());
    while (j.i < j.n) {
      var pref = ((DungeonDB.RoomData)fRd.GetValue(j.rooms.GetValue(j.i))).m_prefab; j.i++;
      heldRefs(dg).Add(pref); var d = dg; c++;
      pref.LoadAsync((id, res) => onRoomLoaded(d, id, res));
    }
    return c;
  }
  static readonly List<System.Reflection.MethodBase> flushHooks = new List<System.Reflection.MethodBase>();
  // 2.1.1: save timing for the hitch log (outermost save call only)
  static int saveDepth; static double saveT0, saveAcc0;
  static void SaveTimePre() { if (saveDepth++ == 0) { saveT0 = coSw.Elapsed.TotalMilliseconds; saveAcc0 = global::ValheimPerformanceFixes.FrameCost.Total; } }
  static Exception SaveTimeFin(System.Reflection.MethodBase __originalMethod, Exception __exception) {
    try { if (saveDepth > 0 && --saveDepth == 0) { double ms = coSw.Elapsed.TotalMilliseconds - saveT0; double own = global::ValheimPerformanceFixes.FrameCost.AddOwn("save", ms, saveAcc0, __originalMethod != null ? __originalMethod.DeclaringType.Name + "." + __originalMethod.Name : "save");
      if (own > 4.0) NoteHeavyCandidate("save", "vanilla", own); } } catch { }
    return __exception;
  }
  static void HookFlush(Harmony h, Type t, string method, string prefix) {
    try { var m = AccessTools.DeclaredMethod(t, method); if (m == null) { Log("save flush hook " + t.Name + "." + method + " not found"); return; }
      h.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), prefix) { priority = Priority.First }); flushHooks.Add(m); }
    catch (Exception e) { Log("save flush patch on " + t.Name + "." + method + " failed: " + e.Message); }
  }
  static Harmony locSplitHarmony; static bool audited;
  // 2.1.0 #9: once in-game (all mods loaded and patched), log every other mod on the methods this module changes, and
  // stand down where a later-loaded mod clashes (transpiler on SpawnLocation -> remove ours; RemoveObjects -> cap off).
  static void Audit() {
    audited = true;
    try {
      var slm = AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation");
      if (locSplitHarmony != null && locSplitOk) { var rv = global::ValheimPerformanceFixes.PatchGuard.Rivals(slm, false, true); if (rv.Count > 0) { locSplitHarmony.Unpatch(slm, HarmonyPatchType.Transpiler, locSplitHarmony.Id); locSplitOk = false; Log("location split-activation patch removed: ZoneSystem.SpawnLocation is also transpiled by " + string.Join(", ", rv)); } }
      var ms = new System.Reflection.MethodBase[] { AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) }), AccessTools.DeclaredMethod(typeof(ZNetScene), "RemoveObjects"), slm,
        AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone"), AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnProxyLocation"), AccessTools.DeclaredMethod(typeof(Heightmap), "CustomLateUpdate"), AccessTools.DeclaredMethod(typeof(DungeonGenerator), "OnRoomLoaded"), AccessTools.DeclaredMethod(typeof(DungeonGenerator), "LoadRoomPrefabsAsync") };
      foreach (var m in ms) { var o = global::ValheimPerformanceFixes.PatchGuard.Others(m); if (o.Count > 0) Log("patch audit: " + global::ValheimPerformanceFixes.PatchGuard.Name(m) + " is also patched by " + string.Join(", ", o)); }
      var ps = AccessTools.DeclaredMethod(typeof(ZoneSystem), "PrepareSave");
      if (commitOk) { var rv = global::ValheimPerformanceFixes.PatchGuard.Rivals(ps); if (rv.Count > 0) { commitOk = false; Log("save safety: commit gate stands down: ZoneSystem.PrepareSave is also changed by " + string.Join(", ", rv)); } }
      var sm = new List<System.Reflection.MethodBase>(flushHooks); sm.Add(ps);
      foreach (var m in sm) { var o = global::ValheimPerformanceFixes.PatchGuard.Others(m); if (o.Count > 0) Log("patch audit: " + global::ValheimPerformanceFixes.PatchGuard.Name(m) + " is also patched by " + string.Join(", ", o)); }
    } catch (Exception e) { Log("patch audit failed: " + e.Message); }
  }
  void Update() {
    long a0 = ArT0(); try { CalmTick(); } catch { } ArCost(a0, "sb-calm", "SpawnBudget calm check");
    try { EarlyChecksTick(); EarlyRecheck(); } catch { } // 2.3.7
    try { if (!audited && (Settled() || (ZNet.instance != null && ZNet.instance.IsServer() && Player.m_localPlayer != null))) { var asw = Stopwatch.StartNew(); Audit(); global::ValheimPerformanceFixes.FrameCost.Add("audit", asw.Elapsed.TotalMilliseconds, "patch audit (once)"); } } catch { }
    a0 = ArT0(); try { PollPreInst(); RunPendingLocActivation(); } catch { } ArCost(a0, "sb-preinst", "SpawnBudget copies / activations");
    a0 = ArT0(); try { LocWatchTick(); } catch (Exception e) { Log("location asset watch: " + e.Message); } ArCost(a0, "sb-locwatch", "SpawnBudget location asset watch");
    a0 = ArT0(); try { ReserveTick(); } catch (Exception e) { Log("reserve copies / size scan: " + e.Message); } ArCost(a0, "sb-reserve", "SpawnBudget reserve copies / size scan"); // 2.3.8
    try { global::ValheimPerformanceFixes.Overlap.Tick(); } catch { }
    try { MarkBuilds(); } catch { }
    a0 = ArT0(); try { SlimTick(); } catch (Exception e) { Log("slim location tick: " + e.Message); } ArCost(a0, "sb-slim", "SpawnBudget light copies");
    if (ReportSec == null) return;
    if (Time.realtimeSinceStartup < nextReport) return;
    nextReport = Time.realtimeSinceStartup + Mathf.Max(5f, ReportSec.Value); Report();
  }
  // 2.1.1: tell other modules (DropNSpawn pacing) while a dungeon / location / zone build is running
  static void MarkBuilds() {
    int f = Time.frameCount; string why = null;
    if (dgActive > 0) why = "dungeon rooms"; else if (dgLoading > 0) why = "dungeon room loads"; else if (locPendingAct.Count > 0 || locPendingSnap.Count > 0) why = "location activation";
    else if (locFrame >= f - 1) why = "location"; else if (zoneFrame >= f - 1) why = "zone";
    if (why != null) global::ValheimPerformanceFixes.FrameCost.MarkWorldBuild(why);
    // the shared in-flight count can never stay stuck: a load that never called back is forgotten after 30 s
    if (dgInFlightAll > 0 && dgLoadJobs.Count == 0 && Time.realtimeSinceStartup - dgFlightTouched > 30f) { Log("dungeon room loads in flight reset (" + dgInFlightAll + " never reported back)"); dgInFlightAll = 0; }
  }
  void LateUpdate() { try { if (log != null) HitchTick(); } catch { } try { if (log != null) { if (AtOn != null && AtOn.Value) InGame(); ArFrameTick(); } } catch { } }
  static int sHitches, hitchLines, sLandHitch; static double sHitchMax; static readonly Dictionary<string, int> hitchOwners = new Dictionary<string, int>();
  // Time.unscaledDeltaTime in frame f is the length of frame f-1, so the previous frame's ledger is described.
  static void HitchTick() {
    int f = Time.frameCount; double dt = Time.unscaledDeltaTime * 1000.0;
    bool landed = dgLandFrame == f - 1 && DgMeasureLand != null && DgMeasureLand.Value;
    if (landed) { sDgLandFrames++; if (dt > sDgLandMax) sDgLandMax = dt; if (HitchMs != null && dt > HitchMs.Value) sDgLandSlow++; }
    if (HitchOn == null || !HitchOn.Value || dt <= HitchMs.Value) return;
    bool vis; try { vis = ZNet.instance != null && !Loading(); } catch { vis = false; }
    if (!vis) return;
    string d = global::ValheimPerformanceFixes.FrameCost.Describe(f - 1, 0.5, out double sum, out string first);
    bool locLanded = !landed && locLandFrame >= f - 2 && locLandFrame < f;
    string tagWhat; string tag = global::ValheimPerformanceFixes.FrameCost.TagAt(f - 1, out tagWhat);
    string owner = first ?? (landed ? "dungeon-load" : locLanded ? "location-load" : tag ?? "none measured");
    if (landed && sum < dt * 0.25) { owner = "dungeon-load"; sLandHitch++; }
    else if (locLanded && sum < dt * 0.25) { owner = "location-load"; sLocLandHitch++; MarkAssetHitch(); LocRememberSlow(locLandHash, dt); }
    else if (tag != null && sum < dt * 0.25) owner = tag;
    sHitches++; if (dt > sHitchMax) sHitchMax = dt;
    hitchOwners.TryGetValue(owner, out int c0); hitchOwners[owner] = c0 + 1;
    if (hitchLines >= 400) return; hitchLines++; if (arOpen) arLogged = f - 1;
    string act = "";
    if (dgActive > 0) act += " dungeonRooms=" + dgActive; if (dgLoading > 0) act += " dungeonLoads=" + dgLoading + "(" + dgInFlightAll + " in flight)";
    if (locPendingAct.Count + locPendingSnap.Count > 0) act += " locationsPending=" + (locPendingAct.Count + locPendingSnap.Count);
    if (lastPendNear > 0) act += " objectsPending=" + lastPendNear;
    Log("HITCH " + dt.ToString("0") + " ms (frame " + (f - 1) + "): owner=" + owner + (d.Length > 0 ? " | " + d : "") + (landed ? " | dungeon room asset finished loading: " + dgLandName : "") + (locLanded ? " | location asset: " + locLandName : "") + (tag != null && owner == tag ? " | " + tagWhat : "")
      + " | measured " + sum.ToString("0.0") + " ms, rest " + Math.Max(0.0, dt - sum).ToString("0") + " ms (rendering, physics, other mods, asset loading)" + (act.Length > 0 ? " | running:" + act : "") + (arOpen ? ArDetail(f - 1) : ""));
  }
  static void Log(string s) { try { log?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); } catch { } if (Mirror != null && Mirror.Value && (s.Contains("patch") || s.StartsWith("budget ENGAGED") || s.StartsWith("dungeon spread") || s.StartsWith("dungeon room loads") || s.StartsWith("dungeon stagger") || s.StartsWith("SLOW location") || s.StartsWith("DungeonGenerator") || s.StartsWith("save ") || s.StartsWith("zone generation") || s.Contains("threw"))) try { BLog?.LogInfo(s); } catch { } }
  static readonly string[] Others = { "com.Fire.FiresGhettoNetworkMod", "vpf.ValheimPerformanceFixes.RenderTrim", "com.Skarif.ValheimPerformanceOverhaul_WATER" };
  // Opaque loading screen: no local player yet (world load / death respawn), dead, or teleporting (Hud.UpdateBlackScreen fades to black for all three).
  static bool Loading() { var p = Player.m_localPlayer; if (p == null || p.IsDead() || p.IsTeleporting()) return true; var hd = Hud.instance; return hd != null && hd.m_loadingScreen != null && hd.m_loadingScreen.gameObject.activeInHierarchy && hd.m_loadingScreen.alpha >= 0.99f; }
  static int phaseFrame = -1; static bool phaseIn; static float tailUntil; static bool wasLoading = true;
  // true = world visible on a client -> budget applies.
  static bool InGame() {
    int f = Time.frameCount; if (f == phaseFrame) return phaseIn; phaseFrame = f;
    try {
      if (ZNet.instance == null || OwnerAtOnce()) { phaseIn = false; return false; }
      bool l = Loading();
      if (wasLoading && !l) { tailUntil = Time.unscaledTime + TailSec.Value; sTails++; try { ArOpen(); } catch { } } // 2.3.7: arrival timing
      wasLoading = l; phaseIn = !l;
    } catch { phaseIn = false; }
    return phaseIn;
  }
  static float settledSince = -1f;
  // true only when not loading, local player exists, and that has been continuously true for SettleGraceSec
  static bool Settled() {
    if (!InGame()) { settledSince = -1f; return false; }
    try { if (Player.m_localPlayer == null) { settledSince = -1f; return false; } } catch { settledSince = -1f; return false; }
    float t = Time.unscaledTime; if (settledSince < 0f) settledSince = t;
    return t - settledSince >= (SettleGrace != null ? SettleGrace.Value : 10f);
  }
  static bool InTail() => Time.unscaledTime < tailUntil;
  static double prevSpawnMs; static void NewFrame() { int f = Time.frameCount; if (f == frame) return; prevSpawnMs = frame == f - 1 ? spawnMs : 0; frame = f; spawnMs = 0; hmMs = 0; hmRuns = 0; frameSw.Restart(); }
  static GameObject Create(ZNetScene zs, ZDO zdo, bool distant) {
    objSw.Restart(); var go = createObj(zs, zdo); objSw.Stop(); sObjs++;
    float ms = (float)objSw.Elapsed.TotalMilliseconds;
    if (ms > BudgetMs.Value) sOverOne++;
    return go;
  }
  // ---- 2.1.1: player builds load and unload exactly like vanilla ----
  // A ZDO with a creator id was placed by a player (Piece / WearNTear). pbZones = zones that hold at least one such object the
  // client knows about (rebuilt from the near + distant lists every 0.5 s, plus at once for every object still waiting to load).
  static readonly int creatorHash = ZDOVars.s_creator;
  static bool PlayerBuilt(ZDO z) { try { return z != null && z.GetLong(creatorHash, 0L) != 0L; } catch { return false; } }
  static HashSet<Vector2s> pbZones = new HashSet<Vector2s>(), pbNext = new HashSet<Vector2s>(); static float pbNextFull; static int pbFrame = -1; static bool pbPending;
  static int sPbVanilla, sPbZoneKept, sPbLocKept, sPbDgKept, sPbHmKept, sPbRmKept;
  static bool PbOn() => SfBuilds != null && SfBuilds.Value;
  static void PbScan(List<ZDO> near, List<ZDO> dist) {
    int f = Time.frameCount; if (f == pbFrame) return; pbFrame = f; pbPending = false;
    float now = Time.unscaledTime; bool full = now >= pbNextFull;
    if (full) { pbNextFull = now + 0.5f; pbNext.Clear(); }
    for (int l = 0; l < 2; l++) { var li = l == 0 ? near : dist; if (li == null) continue;
      for (int i = 0; i < li.Count; i++) { var z = li[i]; if (z == null) continue; bool pend = !z.Created; if (!pend && !full) continue;
        if (!PlayerBuilt(z)) continue; var sc = z.GetSector(); if (pend) { pbPending = true; pbZones.Add(sc); } if (full) pbNext.Add(sc); } }
    if (full) { var t = pbZones; pbZones = pbNext; pbNext = t; pbNext.Clear(); }
  }
  // zone or any of its 8 neighbours holds a player build
  static bool PbNear(Vector2s z) {
    if (!PbOn() || pbZones.Count == 0) return false;
    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (pbZones.Contains(new Vector2s(z.x + dx, z.y + dy))) return true;
    return false;
  }
  static bool PbNearPos(Vector3 p) { if (!PbOn() || pbZones.Count == 0) return false; try { return PbNear(ZoneSystem.GetZone(p)); } catch { return true; } }
  // 2.1.1 PlaceLocationsBeforeBuilds: before the game's own pass creates player pieces in an area with builds, create what
  // they stand on or against: location exteriors (LocationProxy), location stonework/walls that are separate objects
  // (WearNTear without a creator) and terrain edits (TerrainComp / TerrainModifier). Player pieces are not touched here.
  static readonly Dictionary<int, byte> pbKind = new Dictionary<int, byte>(); static int sPbFirstLoc, sPbFirstStone, sPbFirstTerrain;
  static byte PbKind(ZNetScene zs, int h) {
    if (h == proxyHash) return 1;
    if (pbKind.TryGetValue(h, out byte k)) return k;
    k = 0; try { var pf = zs.GetPrefab(h); if (pf != null) { if (pf.GetComponent<TerrainComp>() != null || pf.GetComponent<TerrainModifier>() != null) k = 3; else if (pf.GetComponent<WearNTear>() != null) k = 2; } } catch { }
    if (pbKind.Count > 8192) pbKind.Clear(); pbKind[h] = k; return k;
  }
  static void PlaceBeforeBuilds(ZNetScene zs, List<ZDO> near, List<ZDO> dist) {
    var zsys = ZoneSystem.instance; if (zsys == null || pbZones.Count == 0) return;
    var sw = Stopwatch.StartNew(); double acc0 = global::ValheimPerformanceFixes.FrameCost.Total; int nl = 0, ns = 0, nt = 0;
    for (int pass = 0; pass < 2; pass++) {   // pass 0: terrain + location exteriors, pass 1: stonework
      for (int l = 0; l < 2; l++) { var li = l == 0 ? near : dist; if (li == null) continue;
        if (pass == 0 && l == 0) Prewarm(li);
        for (int i = 0; i < li.Count; i++) { var z = li[i]; if (z == null || z.Created) continue;
          byte k = PbKind(zs, z.GetPrefab()); if (k == 0 || (pass == 0) != (k != 2)) continue;
          if (k == 2 && PlayerBuilt(z)) continue; // player pieces keep the game's order
          var sc = z.GetSector(); if (!PbNear(sc)) continue;
          if (!zsys.IsZoneReadyForType(sc, z.Type)) continue;
          if (Create(zs, z, l == 1) == null) continue;
          if (k == 1) nl++; else if (k == 2) ns++; else nt++;
        } }
    }
    if (nl + ns + nt == 0) return;
    sPbFirstLoc += nl; sPbFirstStone += ns; sPbFirstTerrain += nt;
    double ms = sw.Elapsed.TotalMilliseconds; global::ValheimPerformanceFixes.FrameCost.AddOwn("location", ms, acc0, "placed before player builds");
    if (LogSlow.Value) Log("placed before player builds: " + nl + " location(s), " + ns + " stonework object(s), " + nt + " terrain edit(s), " + ms.ToString("0.0") + " ms");
  }
  static bool pbProxyHere; // set while a LocationProxy near player builds is spawning its location
  static void ProxySpawnCtx(LocationProxy __instance) { pbProxyHere = false; try { pbProxyHere = PbNearPos(__instance.transform.position); } catch { } }
  static Exception ProxySpawnFin(Exception __exception) { pbProxyHere = false; return __exception; }
  static double ovT0 = -1, sOvMs; static int ovFrame = -1;
  // 2.3.1: last postfix on CreateObjects; only acts on frames handed to the other mod
  static void CreateObjectsPost() {
    if (ovT0 < 0) return;
    if (ovFrame == Time.frameCount) { double ms = frameSw.Elapsed.TotalMilliseconds - ovT0; if (ms > 0) { spawnMs += ms; sOvMs += ms; } }
    ovT0 = -1;
  }
  // Replaces ZNetScene.CreateObjects (vanilla 1.0.20: CreateObjectsSorted + CreateDistantObjects) in-game.
  static bool CreateObjectsPre(ZNetScene __instance, ref List<ZDO> currentNearObjects, ref List<ZDO> currentDistantObjects) {
    if (!objOk || !Enabled.Value) { if (PbOn()) try { PbScan(currentNearObjects, currentDistantObjects); } catch { } return true; }
    bool inGame = InGame(), arrival = false;
    if (!inGame) { if (!ArOn.Value || !ArrivalOk()) { arrivalWas = false; if (PbOn()) try { PbScan(currentNearObjects, currentDistantObjects); } catch { } return true; } arrival = true; if (!arrivalWas) { arrivalWas = true; sArrEntered++; Log("arrival budget ENGAGED (" + ArBudget.Value.ToString("0") + " ms/frame, nearest first)"); } }
    else if (arrivalWas) { arrivalWas = false; arrivalEndAt = Time.unscaledTime; }
    if (PbOn()) { try { PbScan(currentNearObjects, currentDistantObjects); } catch { } if (pbPending) { sPbVanilla++; if (SfExtFirst != null && SfExtFirst.Value) try { PlaceBeforeBuilds(__instance, currentNearObjects, currentDistantObjects); } catch (Exception e) { Log("place-before-builds pass threw: " + e.Message); } return true; } } // 2.1.1: a player build is waiting -> this pass is the game's own
    NewFrame();
    // 1.7.5: also Prewarm/PreInstantiate during arrival/teleport screen so clones are ready when the world appears
    // 2.3.1: runs before the hand-over below, so location prewarm works whoever budgets object creation
    if (inGame || arrival) try { Prewarm(currentDistantObjects); Prewarm(currentNearObjects); } catch { }
    // 2.3.0 Mod Overlap: with Cede_SpawnBudget = true, FiresGhettoNetworkMod's time-slice instantiation (when on) budgets object creation while you play (arrival stays here)
    // 2.3.1: the time the other mod / vanilla spends creating objects is still counted into this frame's spawn time (CreateObjectsPost),
    // so location / dungeon / zone work keeps seeing how busy the frame already is
    if (inGame && global::ValheimPerformanceFixes.Overlap.Yield(global::ValheimPerformanceFixes.Feature.SpawnBudget)) { sOvYield++; ovT0 = frameSw.Elapsed.TotalMilliseconds; ovFrame = Time.frameCount; return true; }
    if (inGame && !engagedLogged) { engagedLogged = true; Log("budget ENGAGED (in-game, player spawned)"); }
    try { CountPendDist(currentDistantObjects); } catch { }
    double t0 = frameSw.Elapsed.TotalMilliseconds; double budget; int created = 0, cap = Mathf.Max(1, MaxPerFrame.Value); bool hit = false;
    int distCap = arrival ? Mathf.Max(1, ArDistMax.Value) : Mathf.Max(1, DistMax.Value);
    if (arrival) { budget = ArBudget.Value; cap = 100; }
    else { budget = EffBudget(); float since = Time.unscaledTime - arrivalEndAt; if (ArRamp.Value > 0f && since < ArRamp.Value) budget = Math.Max(budget, Mathf.Lerp(ArBudget.Value, (float)budget, since / ArRamp.Value)); }
    try {
      if (currentNearObjects != null && ZoneSystem.instance.IsActiveAreaLoaded()) {
        tmp.Clear(); Vector3 rp = ZNet.instance.GetReferencePosition(); float pr2 = PrioRadius.Value * PrioRadius.Value;
        long nearSum = 0;
        for (int i = 0; i < currentNearObjects.Count; i++) { var z = currentNearObjects[i]; if (!z.Created) { Vector3 d = z.GetPosition() - rp; z.m_tempSortValue = d.x * d.x + d.y * d.y + d.z * d.z; tmp.Add(z); nearSum += z.m_uid.GetHashCode(); } }
        int num = Mathf.Max(tmp.Count / 100, cap);
        lastPendNear = tmp.Count; SpawnBudgetPressure.PendingNear = tmp.Count; SpawnBudgetPressure.PendingFrame = Time.frameCount; // 2.3.9: fresh count for the spawn hold
        var ord = (BlKeep != null && BlKeep.Value) ? nearBl.Order(tmp, nearSum, cmp, rp, BlMeters.Value, BlFrames.Value) : SortNow(tmp, cmp);
        for (int i = 0; i < ord.Count; i++) {
          bool prio = ord[i].m_tempSortValue <= pr2 && created < PrioMax.Value;
          if (!prio && created > 0 && spawnMs + (frameSw.Elapsed.TotalMilliseconds - t0) > budget) { hit = true; sNearDeferred += ord.Count - i; break; }
          var z = ord[i];
          if (z.Created || SuppressReload(z, rp)) continue;
          if (!ZoneSystem.instance.IsZoneReadyForType(z.GetSector(), z.Type)) continue;
          if (Create(__instance, z, false) != null) { created++; if (prio && created > num) sPrio++; if (created >= num && !(i + 1 < ord.Count && ord[i + 1].m_tempSortValue <= pr2 && created < PrioMax.Value)) break; }
        }
        tmp.Clear();
      }
      if (currentDistantObjects != null && (arrival || created <= cap)) {
        bool backlogSkip = DistBacklogAware != null && DistBacklogAware.Value && lastPendNear >= NearBacklog.Value;
        if (SkipDistant.Value && (spawnMs + (frameSw.Elapsed.TotalMilliseconds - t0) > budget || backlogSkip)) { hit = true; sDistantSkips++; }
        else { Vector3 rpD = ZNet.instance.GetReferencePosition(); tmpD.Clear();
          long distSum = 0;
          for (int i = 0; i < currentDistantObjects.Count; i++) { var o = currentDistantObjects[i]; if (o == null || o.Created) continue; Vector3 d = o.GetPosition() - rpD; o.m_tempSortValue = d.x * d.x + d.z * d.z; tmpD.Add(o); distSum += o.m_uid.GetHashCode(); }
          var ordD = (BlKeep != null && BlKeep.Value) ? distBl.Order(tmpD, distSum, cmpD, rpD, BlMeters.Value, BlFrames.Value) : SortNow(tmpD, cmpD);
          int dc = 0;
          for (int i = 0; i < ordD.Count; i++) {
            var o = ordD[i]; if (o.Created || SuppressReload(o, rpD)) continue;
            if (SkipDistant.Value && (created > 0 || dc > 0) && (spawnMs + (frameSw.Elapsed.TotalMilliseconds - t0) > budget || backlogSkip)) { hit = true; sDistantSkips++; break; }
            if (Create(__instance, o, true) != null) { created++; dc++; if (dc >= distCap) break; }
          }
          tmpD.Clear(); }
      }
    } catch (Exception e) { tmp.Clear(); objOk = false; Log("object budget threw, falling back to vanilla/other mods for the rest of the session: " + e); return true; }
    double used = frameSw.Elapsed.TotalMilliseconds - t0; spawnMs += used;
    if (arrival) { sArrFrames++; sArrObjs += created; if (used > sArrMax) sArrMax = used; if (hit) sArrHit++; currentNearObjects = empty; currentDistantObjects = empty; return false; }
    sSpawnTotal += used; sActiveFrames++;
    if (spawnMs > sMaxSpawn) sMaxSpawn = spawnMs; if (hit) sBudgetHitFrames++;
    if (InTail()) { sTailFrames++; if (spawnMs > sTailMax) sTailMax = spawnMs; if (hit) sTailHit++; }
    currentNearObjects = empty; currentDistantObjects = empty; // later prefixes (FGN time-slice) see nothing to do
    return false;
  }
  static readonly Comparison<ZDO> cmpD = (x, y) => x.m_tempSortValue.CompareTo(y.m_tempSortValue);
  // ---- 2.1.0 #5: keep the sorted creation backlog between passes ----
  // The waiting set only changes slowly (a few objects created per frame, a few new ones per zone), so re-sorting thousands of
  // entries every pass is wasted work. The sorted order is reused while the player moved less than ResortAfterMeters and it is
  // younger than ResortAfterFrames, AND the not-yet-created entries of the cached order are exactly the fresh waiting set
  // (same count and same sum of ids). Otherwise the fresh set is sorted and cached again.
  static readonly Backlog nearBl = new Backlog(), distBl = new Backlog(); static int sBlReused, sBlSorted;
  static List<ZDO> SortNow(List<ZDO> l, Comparison<ZDO> c) { if (l.Count > 1) l.Sort(c); return l; }
  sealed class Backlog {
    readonly List<ZDO> order = new List<ZDO>(); readonly List<ZDOID> ids = new List<ZDOID>(); readonly List<ZDO> keep = new List<ZDO>(); readonly List<ZDOID> keepIds = new List<ZDOID>();
    Vector3 at; int frame = -100000;
    internal List<ZDO> Order(List<ZDO> fresh, long freshSum, Comparison<ZDO> c, Vector3 pos, float meters, int frames) {
      int f = Time.frameCount; Vector3 d = pos - at;
      if (order.Count > 0 && f - frame < frames && d.sqrMagnitude < meters * meters) {
        keep.Clear(); keepIds.Clear(); long sum = 0;
        for (int i = 0; i < order.Count; i++) { var z = order[i]; if (z == null || z.Created || z.m_uid != ids[i]) continue; keep.Add(z); keepIds.Add(ids[i]); sum += ids[i].GetHashCode(); }
        if (keep.Count == fresh.Count && sum == freshSum) {
          order.Clear(); order.AddRange(keep); ids.Clear(); ids.AddRange(keepIds); sBlReused++; return order;
        }
      }
      if (fresh.Count > 1) fresh.Sort(c);
      order.Clear(); ids.Clear(); order.AddRange(fresh); for (int i = 0; i < fresh.Count; i++) ids.Add(fresh[i].m_uid);
      at = pos; frame = f; sBlSorted++; return order;
    }
  }
  static int Cmp(ZDO x, ZDO y) { if (x.Type == y.Type) return x.m_tempSortValue.CompareTo(y.m_tempSortValue); return ((int)y.Type).CompareTo((int)x.Type); }
  // ---- whole CreateObject timing ----
  static readonly Stopwatch coSw = Stopwatch.StartNew(); static readonly Stack<double> coStart = new Stack<double>();
  static int coFrame = -1; static double coFrameMs; static int sLoadFrames; static double sLoadMax, sLoadTotal; static int sLoadObjs;
  static int sTails, sTailFrames, sTailHit; static double sTailMax;
  static readonly Dictionary<int, PStat> pstats = new Dictionary<int, PStat>(); static readonly Dictionary<int, string> owners = new Dictionary<int, string>();
  class PStat { public int hash; public string name; public int n, nLoad; public double total, totalLoad, max; }
  static double coAcc0; static readonly Dictionary<int, string> coNames = new Dictionary<int, string>();
  static void CoPre() { if (coStart.Count > 16) coStart.Clear(); if (coStart.Count == 0) coAcc0 = global::ValheimPerformanceFixes.FrameCost.Total; coStart.Push(coSw.Elapsed.TotalMilliseconds); }
  static void CoPost(ZNetScene __instance, ZDO zdo, GameObject __result) {
    if (coStart.Count == 0) return; double ms = coSw.Elapsed.TotalMilliseconds - coStart.Pop();
    try { TrackProxy(zdo, __result); } catch { }
    if (coStart.Count > 0) return; // nested creation is counted in the outer object
    try {
      if (zdo != null && InGame()) { int h0 = zdo.GetPrefab(); if (h0 != proxyHash) {
        if (!coNames.TryGetValue(h0, out var nm0)) { try { nm0 = __result != null ? __result.name.Replace("(Clone)", "") : h0.ToString(); } catch { nm0 = "?"; } if (coNames.Count > 4096) coNames.Clear(); coNames[h0] = nm0; }
        global::ValheimPerformanceFixes.FrameCost.AddOwn(PlayerBuilt(zdo) ? "object(player build)" : "object", ms, coAcc0, nm0); } }
      bool ld = !InGame(); int f = Time.frameCount;
      if (ld) { if (coFrame != f) { if (coFrame >= 0 && coFrameMs > sLoadMax) sLoadMax = coFrameMs; coFrame = f; coFrameMs = 0; sLoadFrames++; } coFrameMs += ms; sLoadTotal += ms; sLoadObjs++; }
      if (!LogSlow.Value || zdo == null) return;
      int hash = zdo.GetPrefab();
      if (!pstats.TryGetValue(hash, out var p)) { string n; try { var pf = __instance.GetPrefab(hash); n = pf != null ? pf.name : hash.ToString(); } catch { n = "?"; } pstats[hash] = p = new PStat { hash = hash, name = n }; }
      p.n++; p.total += ms; if (ld) { p.nLoad++; p.totalLoad += ms; } if (ms > p.max) p.max = ms;
      string own = Owner(__instance, hash, p.name);
      if (ms > ListObjMs.Value) Log("SLOW " + (ld ? "[loading] " : "[world] ") + p.name + " " + ms.ToString("0.0") + " ms (" + own + ")");
      if (ms > 4.0) NoteHeavyCandidate(p.name, own, ms);
      // 1.7.5: SoftCap LocationProxy path when proxy create (or location spawn) exceeded SoftCapProxyCreateMs
      if (LocProxySoftMs != null && ms > LocProxySoftMs.Value) {
        if (hash == proxyHash) { int lh = zdo.GetInt(ZDOVars.s_location); if (lh != 0) proxyHeavyHashes.Add(lh); }
        else proxyHeavyHashes.Add(hash);
        if (proxyHeavyHashes.Count > 256) proxyHeavyHashes.Clear();
      }
    } catch { }
  }
  // Owning mod: a plugin assembly among the prefab's components, else the asset bundle that contains it, else vanilla.
  static Dictionary<System.Reflection.Assembly, string> asmToMod;
  static string Owner(ZNetScene zs, int hash, string name) {
    if (owners.TryGetValue(hash, out var o)) return o;
    o = "vanilla";
    try {
      if (asmToMod == null) { asmToMod = new Dictionary<System.Reflection.Assembly, string>(); foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos) { try { var a = kv.Value.Instance?.GetType().Assembly; if (a != null && !asmToMod.ContainsKey(a)) asmToMod[a] = kv.Value.Metadata.Name; } catch { } } }
      var pf = zs.GetPrefab(hash); string found = null;
      if (pf != null) foreach (var c in pf.GetComponentsInChildren<Component>(true)) { if (c == null) continue; if (asmToMod.TryGetValue(c.GetType().Assembly, out var m)) { found = "mod:" + m; break; } }
      if (found == null) foreach (var b in AssetBundle.GetAllLoadedAssetBundles()) { try { if (b != null && !b.isStreamedSceneAssetBundle && b.Contains(name)) { found = "bundle:" + b.name; break; } } catch { } }
      if (found != null) o = found;
    } catch { o = "?"; }
    owners[hash] = o; return o;
  }
  // ---- heightmap: every regenerate path ----
  static bool inLateRegen, inForce; static int sRegenDeferred;
  static readonly AccessTools.FieldRef<Heightmap, Mesh> collMesh = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_collisionMesh");
  static void ForcePre() { inForce = true; } static Exception ForceFin(Exception __exception) { inForce = false; return __exception; }
  static bool RegenPre(Heightmap __instance) {
    if (inLateRegen || inForce || !HmEnabled.Value || !HmAllPaths.Value) return true;
    try {
      if (__instance.IsDistantLod || collMesh(__instance) == null || !InGame()) return true; // never built -> build now (collider safety)
      if (PbNearPos(__instance.transform.position)) { sPbHmKept++; return true; } // 2.1.1: ground under / next to player builds -> vanilla
      var pp = Player.m_localPlayer.transform.position; var hp = __instance.transform.position; float dx = pp.x - hp.x, dz = pp.z - hp.z;
      if (dx * dx + dz * dz <= HmProtect.Value * HmProtect.Value) return true;
      __instance.m_doLateUpdate = 2; sRegenDeferred++; return false; // CustomLateUpdate will rebuild it under the slicing budget
    } catch { return true; }
  }
  static bool HmPre(Heightmap __instance, out double __state) {
    __state = -1; if (__instance.m_doLateUpdate != 2) return true;
    if (!HmEnabled.Value || !InGame()) return true;
    NewFrame();
    bool protect; int n = 0; deferred.TryGetValue(__instance, out n);
    try { var pp = Player.m_localPlayer.transform.position; var hp = __instance.transform.position; float dx = pp.x - hp.x, dz = pp.z - hp.z; protect = dx * dx + dz * dz <= HmProtect.Value * HmProtect.Value; } catch { protect = true; }
    if (!protect && !__instance.IsDistantLod && PbNearPos(__instance.transform.position)) { protect = true; sPbHmKept++; } // 2.1.1
    bool wbOk = protect || n >= HmMaxDefer.Value || WbCanStart(); // 2.1.0 shared world budget
    bool run = protect || n >= HmMaxDefer.Value
      || (wbOk && hmRuns == 0 && spawnMs + hmMs < FrameBudgetMs.Value)
      || (wbOk && hmMs < HmBudgetMs.Value && spawnMs + hmMs < FrameBudgetMs.Value);
    if (!run) { deferred[__instance] = n + 1; sHmDeferrals++; return false; }
    if (n >= HmMaxDefer.Value && !protect) sHmForced++;
    if (n > 0) deferred.Remove(__instance); __state = frameSw.Elapsed.TotalMilliseconds; inLateRegen = true; return true;
  }
  static void HmPost(Heightmap __instance, double __state) {
    inLateRegen = false;
    if (__state < 0) return; double ms = frameSw.Elapsed.TotalMilliseconds - __state; hmMs += ms; if (Settled()) WbCharge(ms); global::ValheimPerformanceFixes.FrameCost.Add("heightmap", ms, "ground rebuild"); hmRuns++; sHmRuns++; sHmTotal += ms; if (ms > sHmMax) sHmMax = ms;
    if (ms > 8 && LogSlow.Value) { string w = "?"; try { w = __instance.transform.position.ToString("F0") + (__instance.IsDistantLod ? " distantLOD" : ""); } catch { } Log("SLOW heightmap rebuild " + ms.ToString("0.0") + " ms at " + w + " (spawn this frame " + spawnMs.ToString("0.0") + " ms)"); }
  }
  static string HitchOwners() { if (hitchOwners.Count == 0) return "-"; var l = new List<KeyValuePair<string, int>>(hitchOwners); l.Sort((a, b) => b.Value.CompareTo(a.Value)); var sb = new StringBuilder(); foreach (var kv in l) { if (sb.Length > 0) sb.Append(','); sb.Append(kv.Key).Append(':').Append(kv.Value); } return sb.ToString(); }
  static void Report() {
    var sb = new StringBuilder();
    sb.Append("STATS ").Append(ReportSec.Value.ToString("0")).Append("s inGame=").Append(InGame()).Append(" objBudget=").Append(objOk ? "on" : "OFF")
      .Append(" | activeFrames=").Append(sActiveFrames).Append(" objs=").Append(sObjs).Append(" budgetHitFrames=").Append(sBudgetHitFrames)
      .Append(" nearDeferred=").Append(sNearDeferred).Append(" prioExtra=").Append(sPrio).Append(" combatFrames=").Append(sCombatFrames).Append(" distantSkips=").Append(sDistantSkips)
      .Append(" maxFrameSpawn=").Append(sMaxSpawn.ToString("0.0")).Append("ms totalSpawn=").Append(sSpawnTotal.ToString("0")).Append("ms singleObjOverBudget=").Append(sOverOne)
      .Append(" | heightmap runs=").Append(sHmRuns).Append(" avg=").Append((sHmRuns > 0 ? sHmTotal / sHmRuns : 0).ToString("0.0")).Append("ms max=").Append(sHmMax.ToString("0.0"))
      .Append("ms deferrals=").Append(sHmDeferrals).Append(" forcedAfterMaxDefer=").Append(sHmForced).Append(" pendingDeferred=").Append(deferred.Count).Append(" otherPathRegensSliced=").Append(sRegenDeferred)
      .Append("\n   loading-screen (vanilla speed): frames=").Append(sLoadFrames).Append(" objs=").Append(sLoadObjs).Append(" totalCreate=").Append(sLoadTotal.ToString("0")).Append("ms maxFrameCreate=").Append(Math.Max(sLoadMax, coFrameMs).ToString("0.0"))
      .Append("ms | post-load tail (").Append(TailSec.Value.ToString("0")).Append("s): entered=").Append(sTails).Append(" frames=").Append(sTailFrames).Append(" budgetHit=").Append(sTailHit).Append(" maxFrameSpawn=").Append(sTailMax.ToString("0.0")).Append("ms");
    var zs = ZNetScene.instance;
    var l = new List<PStat>(pstats.Values); l.Sort((a, b) => b.total.CompareTo(a.total));
    if (l.Count > 0) {
      sb.Append("\n   top prefabs by total create time [n(loading)/totalMs(loading)/maxMs owner]:");
      for (int i = 0; i < l.Count && i < 15; i++) { var p = l[i]; sb.Append("\n     ").Append(p.name).Append(" ").Append(p.n).Append("(").Append(p.nLoad).Append(")/").Append(p.total.ToString("0")).Append("(").Append(p.totalLoad.ToString("0")).Append(")/").Append(p.max.ToString("0.0")).Append(" ").Append(zs != null ? Owner(zs, p.hash, p.name) : "?"); }
      var big = l.FindAll(p => p.max > ListObjMs.Value); big.Sort((a, b) => b.max.CompareTo(a.max));
      if (big.Count > 0) { sb.Append("\n   objects over ").Append(ListObjMs.Value.ToString("0")).Append(" ms (max):"); foreach (var p in big) sb.Append(" ").Append(p.name).Append(" ").Append(p.max.ToString("0.0")).Append(" [").Append(zs != null ? Owner(zs, p.hash, p.name) : "?").Append("] |"); }
    }
    sb.Append("\n   locations: spawned=").Append(sLocSpawns).Append(" maxMs=").Append(sLocMax.ToString("0.0")).Append(" delayedFrames=").Append(sLocDelayed).Append(" forced=").Append(sLocForced)
      .Append(" | zones: spawned=").Append(sZones).Append(" postponed=").Append(sZoneDeferred)
      .Append(" | dungeons spread=").Append(sDungeons).Append(" frames=").Append(sDgFrames).Append(" maxFrameMs=").Append(sDgMaxFrame.ToString("0.0")).Append(" aborted=").Append(sDgAborted).Append(" active=").Append(dgActive).Append(" roomLoadsStaggered=").Append(sDgStaggered).Append(" maxStaggerWall=").Append(sDgStaggerWall.ToString("0")).Append("ms")
      .Append(" | locHeld1Frame=").Append(sLocHeld).Append(" locPrewarm=").Append(sLocPrewarm).Append(" locSplit=").Append(sLocSplit).Append(" locPreInst=").Append(sLocPreInst).Append("/").Append(sLocPreInstUsed).Append("u/").Append(sLocPreInstSync).Append("sync maxLocName=").Append(sLocMaxName)
      .Append(" | locEdgeKept=").Append(sLocKept).Append(" locEdgeKeptFrames=").Append(sLocKeptFrames).Append(" locRespawnSuppressed=").Append(sLocSuppressed).Append(" locUnloaded=").Append(sLocUnloaded).Append(" locDupSpawnBlocked=").Append(sLocDup).Append(" proxiesTracked=").Append(proxies.Count).Append(" dupViewsDropped=").Append(sLocDupTracked).Append(" samePosZdos=").Append(sLocSamePos)
      .Append(" locCapDelayed=").Append(sLocCapDelayed).Append(" locSlowFrameDelayed=").Append(sLocSlowFrameDelayed)
      .Append("\n   worldBudget jobs=").Append(sWbJobs).Append(" deferred=").Append(sWbDeferred).Append(" maxFrame=").Append(sWbMaxFrame.ToString("0.0")).Append("ms (budget ").Append(WbMs.Value.ToString("0")).Append(WbOn.Value ? "" : ", OFF").Append(")")
      .Append(" | backlog reused=").Append(sBlReused).Append(" sorted=").Append(sBlSorted)
      .Append(" | removalCap ").Append(RmCapOn.Value ? (rmCapOk ? "on" : "STOOD DOWN") : "off").Append(" cappedFrames=").Append(sRmCapFrames).Append(" postponed=").Append(sRmCapped).Append(" | cleanupTrips=").Append(sRmTrips).Append(" purged=").Append(sRmPurged)
      .Append("\n   arrival: entered=").Append(sArrEntered).Append(" frames=").Append(sArrFrames).Append(" objs=").Append(sArrObjs).Append(" budgetHit=").Append(sArrHit).Append(" maxFrameSpawn=").Append(sArrMax.ToString("0.0")).Append("ms")
      .Append(" | pendDist=").Append(lastPendDist).Append(" stale=").Append(lastPendDistStale).Append(" combat=").Append(cmbIn)
      .Append("\n   save safety (since start): flushes=").Append(sFlushes).Append(" zonesKeptOpenInSave=").Append(sCommitCuts).Append(" zoneGenThrew=").Append(sGenThrew).Append(" commitGate=").Append(commitOk ? "on" : "OFF").Append(" hostAtOnce=").Append(SfOwner.Value)
      .Append("\n   dungeons: waitFrames=").Append(sDgWaitFrames).Append(" wentAnyway=").Append(sDgForcedStarts).Append(" splitByKnownCost=").Append(sDgCostSplits).Append(" slowRoomOwnFrame=").Append(sDgOwnFrameRooms).Append(" maxSnap=").Append(sDgSnapMax.ToString("0.0")).Append("ms knownRooms=").Append(roomCost.Count)
      .Append(" loadsInFlight=").Append(dgInFlightAll).Append(" loadLandedFrames=").Append(sDgLandFrames).Append(" slow=").Append(sDgLandSlow).Append(" max=").Append(sDgLandMax.ToString("0")).Append("ms")
      .Append(" | interiors keptAtEdge=").Append(sDgKept).Append(" reloadSuppressed=").Append(sDgSuppressed).Append(" unloaded=").Append(sDgUnloaded)
      .Append("\n   hitches over ").Append(HitchMs.Value.ToString("0")).Append(" ms: ").Append(sHitches).Append(" max=").Append(sHitchMax.ToString("0")).Append("ms byOwner=").Append(HitchOwners())
      .Append("\n   player builds vanilla (since start): on=").Append(PbOn()).Append(" placedFirst loc/stone/terrain=").Append(sPbFirstLoc).Append("/").Append(sPbFirstStone).Append("/").Append(sPbFirstTerrain).Append(" zonesWithBuilds=").Append(pbZones.Count).Append(" vanillaPasses=").Append(sPbVanilla).Append(" zonesNotPostponed=").Append(sPbZoneKept).Append(" locationsNotSplit=").Append(sPbLocKept).Append(" dungeonsNotSpread=").Append(sPbDgKept).Append(" groundNotSliced=").Append(sPbHmKept).Append(" unloadsPastCap=").Append(sPbRmKept)
      .Append("\n   bake/slim: ").Append(BakeStats()).Append(" | ").Append(SlimStats())
      .Append("\n   overlapYield=").Append(global::ValheimPerformanceFixes.Overlap.Summary()).Append(" sbYieldCalls=").Append(sOvYield).Append(" otherModSpawnMs=").Append(sOvMs.ToString("0")).Append(" orphanRepairLeftToFgn=").Append(sOvOrphan)
      .Append(" | preInst startWaits=").Append(sPreStartWait).Append(" letInWaits=").Append(sPreLetInWait).Append(" letInForced=").Append(sPreLetInForced)
      .Append(" | locAssetReady=").Append(sLocLandFrames).Append(" locLoadHitches=").Append(sLocLandHitch).Append(" requestedBehindScreen=").Append(sLocReloads)
      .Append("\n   rememberedSlow=").Append(locSlowNames.Count).Append(" keptLoaded=").Append(locHeldRefs.Count).Append(" loadingScreens=").Append(sLocScreens).Append(" screensNothingRequested=").Append(sLocScreensNone).Append(" lastScreen=").Append(locLastWhy)
      .Append(" | ").Append(global::ValheimPerformanceFixes.SyncWatch.Stats()).Append(" | ").Append(global::RenderTrim.GlyphFix.Stats())
      .Append(" | ").Append(global::ValheimPerformanceFixes.WorldLoad.WorldLoad.Stats())
      .Append("\n   preInst calmWaits=").Append(sPreCalmWait).Append(" lastRoughFrame=").Append(roughAt < 0f ? "-" : (Time.unscaledTime - roughAt).ToString("0.0") + "s ago")
      .Append(" | prebuiltCopies started=").Append(sPrebuilt).Append(" used=").Append(sPrebuiltUsed).Append(" readyNow=").Append(PrebuiltReady())
      .Append(" | ").Append(global::RenderTrim.Plugin.Stats233())
      .Append("\n   prebuild worstFirst waitedForLoading=").Append(sPrebuildWaits).Append(" limitHit=").Append(sPrebuildLimit).Append(" droppedForWorse=").Append(sPrebuildEvicted).Append(" atLoadedEdge=").Append(sPreEdge)
      .Append(" | copyWait longWaits=").Append(sPreWaitLong).Append(" dropped=").Append(sPreDiscarded).Append(" droppedObjects=").Append(sPreDiscardDestroyed).Append(" pendingDrops=").Append(preDiscardOps.Count)
      .Append(" | ").Append(global::RenderTrim.Plugin.Stats234())
      .Append("\n   locations measuredAtPlacement=").Append(sLocMeasured).Append(" notMeasuredYet=").Append(LocUnmeasured()).Append(" noCopyUntilMeasured=").Append(sPrebuildUnmeasured)
      .Append(" | ").Append(global::RenderTrim.Plugin.Stats235())
      .Append("\n   lean: ").Append(global::ValheimPerformanceFixes.Lean.Lean.Stats())
      .Append("\n   prebuild spareSlotsToUnmeasured=").Append(sPrebuildSpare).Append(" (").Append(LaSpare != null && LaSpare.Value ? "on" : "off").Append(") loadStallsKnown=").Append(locStallMs.Count).Append(" importedFromLastLog=").Append(sStallsImported)
      .Append(" | slimLocations ").Append(slOk ? "on" : "off for this session (checked before building)")
      .Append(" | ").Append(ArStats()).Append(" | ").Append(global::RenderTrim.Plugin.GcStats237())
      .Append("\n   reserve: ").Append(Stats238()).Append(" | ").Append(global::RenderTrim.Plugin.Stats238()).Append(" | ").Append(global::MinimapCache.SaveFix.Stats238()).Append(" | ").Append(global::ValheimPerformanceFixes.Overlap.RecheckStats())
      .Append("\n   ramFloor/hold: ").Append(Stats239()).Append(" | ").Append(global::RenderTrim.Plugin.Stats239()).Append(" | ").Append(global::MinimapCache.SaveFix.Stats239());
    sOvYield = sOvOrphan = 0; sOvMs = 0; sPreStartWait = sPreLetInWait = sPreLetInForced = 0; sLocLandFrames = sLocLandHitch = sLocReloads = 0; sPreCalmWait = sLocScreens = sLocScreensNone = 0; sPrebuilt = sPrebuiltUsed = 0; sPrebuildWaits = sPrebuildLimit = sPrebuildEvicted = sLocMeasured = sPrebuildUnmeasured = sPreEdge = sPreWaitLong = sPreDiscarded = sPreDiscardDestroyed = 0; global::ValheimPerformanceFixes.SyncWatch.Reset();
    BakeStatsReset(); SlimStatsReset();
    Log("location hysteresis " + ReportSec.Value.ToString("0") + "s: kept at edge=" + sLocKept + " suppressed re-spawns=" + sLocSuppressed + " unloaded=" + sLocUnloaded + " duplicate spawns blocked=" + sLocDup + " (enabled=" + HyOn.Value + ")");
    sLocKept = sLocKeptFrames = sLocSuppressed = sLocUnloaded = sLocDup = sLocDupTracked = sLocSamePos = 0;
    sDgWaitFrames = sDgForcedStarts = sDgCostSplits = sDgOwnFrameRooms = sDgLandFrames = sDgLandSlow = sDgKept = sDgSuppressed = sDgUnloaded = sHitches = hitchLines = sLandHitch = 0; sDgSnapMax = sDgLandMax = sHitchMax = 0; hitchOwners.Clear();
    if (Mirror.Value) try { BLog.LogInfo(sb.ToString().Split('\n')[0] + " | loc=" + sLocSpawns + "/" + sLocDelayed + "d" + " maxLoc=" + sLocMax.ToString("0") + "ms(" + sLocMaxName + ") held=" + sLocHeld + " prewarm=" + sLocPrewarm + " zonesPostponed=" + sZoneDeferred + " dungeons=" + sDungeons + " dgMaxFrame=" + sDgMaxFrame.ToString("0.0") + "ms"); } catch { }
    sLocSpawns = sLocDelayed = sLocForced = sZones = sZoneDeferred = sDungeons = sDgFrames = sDgAborted = sDgStaggered = sLocHeld = sLocPrewarm = sLocSplit = sLocPreInst = sLocPreInstUsed = sLocPreInstSync = 0; sDgStaggerWall = 0; sLocMaxName = "-"; sLocMax = sDgMaxFrame = 0;
    Log(sb.ToString()); pstats.Clear(); sLoadFrames = sLoadObjs = sTails = sTailFrames = sTailHit = sRegenDeferred = 0; sLoadMax = sLoadTotal = sTailMax = 0; coFrameMs = 0;
    sWbJobs = sWbDeferred = sBlReused = sBlSorted = sRmCapFrames = sRmCapped = sRmTrips = 0; sWbMaxFrame = 0;
    sPrio = sCombatFrames = 0; sObjs = sBudgetHitFrames = sNearDeferred = sDistantSkips = sHmDeferrals = sHmForced = sHmRuns = sActiveFrames = sOverOne = 0; sHmMax = sHmTotal = sMaxSpawn = sSpawnTotal = 0;
    var dead = new List<Heightmap>(); foreach (var k in deferred.Keys) if (k == null) dead.Add(k); foreach (var k in dead) deferred.Remove(k);
  }
}

internal sealed class ConfigurationManagerAttributes { public bool? IsAdvanced; }

/// <summary>Read by RenderTrim (reflection, soft) for adaptive trimming.</summary>

}
// Stays in the global namespace: RenderTrim finds it by name (AccessTools.TypeByName("SpawnBudgetPressure")).
public static class SpawnBudgetPressure { public static float Value, Raw, BudgetMs; public static int Instances, PendingNear, PendingDistant, PendingFrame = -1000; public static bool Combat; }