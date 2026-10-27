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
public class Plugin : global::ValheimPerformanceFixes.ModuleBehaviour {
  public const string Ver = "1.7.5";
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
    try { if (ZNet.instance == null || ZNet.instance.IsServer()) return false; var p = Player.m_localPlayer; return p != null && !p.IsDead() && ZoneSystem.instance != null; } catch { return false; }
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
  void Awake() {
    inst = this; BLog = Logger;
    if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { Logger.LogInfo("SpawnBudget " + Ver + ": headless/server process detected, not patching anything."); return; }
    Mirror = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "MirrorToLogOutput", true, "Also write patch status, ENGAGED and the STATS headline to BepInEx LogOutput.log (full detail stays in BepInEx/SpawnBudget.log).");
    DgSpread = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "SpreadRooms", false, "EXPERIMENTAL, off by default. Client only, world visible: place a dungeon's rooms over several frames (RoomBudgetMs per frame, >=1 room/frame) instead of all in one frame. Rooms are seeded per position, so the result is identical; the zone stays 'loading' until done (as vanilla during asset load). Disabled automatically if another mod patches DungeonGenerator.Spawn.");
    DgStagger = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "StaggerRoomLoads", true, "1.5.0: client, world visible: issue a dungeon's room prefab async loads a few unique assets at a time instead of all at once, so Unity integrates them on the main thread over several frames. Room list/positions come from the ZDO (unchanged); only load timing changes.");
    DgMaxLoads = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "MaxRoomLoadsInFlight", 1, new ConfigDescription("1.7.5: unique room prefabs loading at the same time while staggering. Default 1 so SoftEntry crypt loads do not stack EarlyUpdate spikes.", new AcceptableValueRange<int>(1, 16)));
    LocHoldFrame = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "HoldOneFrameAfterLoad", true, "1.5.0: when a location's asset has just finished loading, delay its instantiate to the next frame so asset integration and instantiate do not land in the same frame.");
    LocPrewarm = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "PrewarmFromQueue", true, "1.5.0: request the location asset load (vanilla PokeCanSpawnLocation via ShouldDelayProxyLocationSpawning) as soon as a LocationProxy ZDO is in the pending creation queue, before the proxy is instantiated.");
    LocSplit = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SplitHeavyLocationActivation", true, "1.5.2: client, settled in-game only: a heavy location is instantiated inactive in one frame and activated (Awake/OnEnable, renderers, SnapToGround) in the next frame, so its cost is spread over 2 frames. The next location is held until the activation is done.");
    LocHeavyMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "HeavyLocationMs", 8f, "1.5.2: generic rule for ALL locations (vanilla and modded): a location whose last unsplit spawn took longer than this is treated as heavy (learned per session).");
    LocHeavyPieces = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "HeavyLocationTransforms", 80, "1.7.5: a location prefab with more than this many child transforms is treated as heavy from its first spawn (0 = split every location). Lower = more POIs split across frames; default 80 covers more swamp/MWL buildings.");
    LocHeavyList = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "HeavyLocations", "", "1.5.2: optional extra names (normally empty; the ms/transform rules already cover every location).");
    LocSoftCap = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SoftCapHeavyActivation", true, "1.7.5: after a heavy location is instantiated inactive, activate it under SoftCapBudgetMs and defer SnapToGround to the next frame so activation does not hitch. Off = old 2-frame split only.");
    LocPreInst = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "PreInstantiateHeavy", true, "1.7.5: for heavy overworld LocationProxy locations, start Unity InstantiateAsync while approaching AND during arrival/teleport screens so SoftReferenceableAssets.Instantiate is not paid on the spawn frame. Spawn waits (up to MaxDeferFrames) for the clone.");
    LocSoftCapMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SoftCapBudgetMs", 10f, new ConfigDescription("1.7.5: per-frame ms budget for deferred heavy-location activation (Awake/OnEnable). SnapToGround runs on the following frame. Default 10.", new AcceptableValueRange<float>(4f, 40f)));
    LocProxySoftMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SoftCapProxyCreateMs", 12f, new ConfigDescription("1.7.5: if creating a LocationProxy (or its location spawn) took longer than this many ms, SoftCap that location on the next activation path so Awake/SnapToGround do not stack on the same hitch.", new AcceptableValueRange<float>(4f, 40f)));
    DgSoftEntry = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "SoftEntry", true, "1.7.4: when entering a crypt/dungeon, stagger room asset loads AND place rooms across frames with SoftEntryWallMs as a hard per-frame wall (~18 ms) so opening a crypt does not freeze for hundreds of ms. Starts as soon as the world is visible (loads before you can reach the next rooms). Independent of SpreadRooms; leave SpreadRooms off unless you want spread without SoftEntry.");
    DgWallMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "SoftEntryWallMs", 18f, new ConfigDescription("1.7.4: hard per-frame wall (ms) for SoftEntry room loads and room placement. Aim: no dungeon wall above ~20 ms.", new AcceptableValueRange<float>(6f, 40f)));
    DistBacklogAware = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "BacklogAwareDistantSkip", true, "1.7.4: also skip distant object creation (LocationProxy etc.) while many nearby objects are still waiting (NearBacklogThreshold), so nearby scenery fills in first.");
    NearBacklog = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "NearBacklogThreshold", 40, new ConfigDescription("1.7.4: with BacklogAwareDistantSkip, skip distant creation while at least this many nearby objects are still waiting to appear.", new AcceptableValueRange<int>(5, 300)));
    HyOn = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "Enabled", true, "1.7.2: client, settled in-game only: stop the unload/reload loop of locations at the rim of the loaded area when you move back and forth over a zone line (fights, kiting). A location that just left the area stays loaded for KeepLoadedSec if it is within KeepExtraZones of the rim, and one that was unloaded anyway is not re-created for ReloadCooldownSec unless it is within AlwaysReloadWithinMeters. Nearby objects are never affected.");
    HyKeepSec = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "KeepLoadedSec", 30f, new ConfigDescription("Seconds a location that left the loaded area is kept loaded before it is unloaded (0 = unload immediately, vanilla).", new AcceptableValueRange<float>(0f, 300f)));
    HyExtra = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "KeepExtraZones", 1, new ConfigDescription("Only keep a location loaded while it is at most this many zones (64 m) beyond the vanilla loaded area.", new AcceptableValueRange<int>(0, 3)));
    HyCooldown = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "ReloadCooldownSec", 10f, new ConfigDescription("After a location was unloaded, do not re-create it for this many seconds unless you are close to it (0 = off).", new AcceptableValueRange<float>(0f, 120f)));
    HyClose = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Location Hysteresis", "AlwaysReloadWithinMeters", 128f, new ConfigDescription("A location within this distance (XZ) of you is always re-created immediately, cooldown or not.", new AcceptableValueRange<float>(0f, 500f)));
    SettleGrace = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "SettleGraceSec", 10f, new ConfigDescription("1.5.1: zone throttle / location hold+prewarm / room-load stagger stay vanilla until the player has spawned and the loading screen has been gone this long.", new AcceptableValueRange<float>(0f, 120f)));
    ZoneHeavyMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Zones", "PostponeIfLastFrameOverMs", 40f, "1.5.0: postpone a client zone spawn if the previous frame took longer than this (or spent the object budget, or spawned a location).");
    DgBudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Dungeon", "RoomBudgetMs", 8f, new ConfigDescription("1.7.4: per-frame ms for dungeon room placement when SoftEntry/SpreadRooms is on. SoftEntry also clamps to SoftEntryWallMs.", new AcceptableValueRange<float>(1f, 50f)));
    LocSerialize = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "SerializeProxySpawns", true, "Client only, world visible: at most one LocationProxy location spawn per frame, and none while this frame's object budget is spent or a dungeon is being spread (uses vanilla's own ShouldDelayProxyLocationSpawning retry).");
    LocBudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "DelayWhenFrameSpawnOverMs", 6f, "Delay a location spawn if object creation this frame already exceeded this.");
    LocMaxDefer = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "MaxDeferFrames", 30, "After this many consecutive delayed frames one location is let through regardless.");
    ZoneThrottle = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Zones", "ThrottleClientZoneSpawns", true, "Client only, world visible: postpone ZoneSystem.SpawnZone (client mode) to a later tick if a zone or location already spawned this frame or the object budget is spent (vanilla retries every 0.1 s).");
    ZoneMaxDefer = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Zones", "MaxDeferTicks", 10, "After this many consecutive postponed attempts a zone spawns regardless.");
    Enabled = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "Enabled", true, "Limit time spent creating objects per frame while in-game (never during the loading screen, never on a server).");
    BudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "BudgetMs", 6f, new ConfigDescription("Per-frame milliseconds for object creation (near + distant combined). At least one near object is always created per frame.", new AcceptableValueRange<float>(1f, 50f)));
    PrioRadius = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "PriorityRadius", 40f, new ConfigDescription("1.6.0: objects within this many meters of you are never deferred by the time budget or count cap (up to PriorityMaxPerFrame per frame); spreading only happens farther out. Live. 0 = off.", new AcceptableValueRange<float>(0f, 100f)));
    PrioMax = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "PriorityMaxPerFrame", 40, new ConfigDescription("Safety cap for priority-radius objects created in one frame.", new AcceptableValueRange<int>(5, 200), new ConfigurationManagerAttributes { IsAdvanced = true }));
    AdOn = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "Enabled", true, "1.7.0: out of combat, scale the per-frame object budget with load pressure (pending near/distant objects, postponed zones/locations, frame time, active instance count -> 0..1, smoothed). Low pressure = LenientBudgetMs (near vanilla), high = AggressiveBudgetMs. Replaces General/BudgetMs while on. PriorityRadius objects are never deferred either way.");
    AdLen = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "LenientBudgetMs", 10f, new ConfigDescription("Object creation ms/frame at low pressure.", new AcceptableValueRange<float>(2f, 50f)));
    AdAgg = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "AggressiveBudgetMs", 4f, new ConfigDescription("Object creation ms/frame at high pressure.", new AcceptableValueRange<float>(1f, 50f)));
    AdLow = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "LowPressure", 0.25f, new ConfigDescription("Pressure at/below which the lenient value is used.", new AcceptableValueRange<float>(0f, 1f)));
    AdHigh = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "HighPressure", 0.75f, new ConfigDescription("Pressure at/above which the aggressive value is used.", new AcceptableValueRange<float>(0f, 1f)));
    AdStr = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "Strength", 1f, new ConfigDescription("How far toward the aggressive value full pressure goes (0 = always lenient, 1 = all the way).", new AcceptableValueRange<float>(0f, 1f)));
    AdRise = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "RiseSec", 4f, new ConfigDescription("Smoothing time (EMA) when pressure rises.", new AcceptableValueRange<float>(0.5f, 30f)));
    AdFall = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "FallSec", 10f, new ConfigDescription("Smoothing time (EMA) when pressure falls (slower = no flip-flopping while traveling).", new AcceptableValueRange<float>(0.5f, 60f)));
    AdFrameMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "TargetFrameMs", 16.7f, new ConfigDescription("Frame time treated as 'no headroom left' starts above this (16.7 = 60 fps).", new AcceptableValueRange<float>(5f, 50f), new ConfigurationManagerAttributes { IsAdvanced = true }));
    CmbOn = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "Enabled", true, "1.6.0: while in combat (alerted hostile within DetectRange, or you took/dealt damage) use CombatBudgetMs for object creation beyond PriorityRadius, so spawning far stuff does not steal frame time mid-fight. Checked 4x/s; eases back over EaseSec after ExitDelaySec.");
    CmbBudget = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "CombatBudgetMs", 3f, new ConfigDescription("Per-frame object creation ms while in combat (normal: General/BudgetMs).", new AcceptableValueRange<float>(1f, 50f)));
    CmbDetect = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "DetectRange", 30f, new ConfigDescription("Alerted hostiles within this many meters count as combat.", new AcceptableValueRange<float>(10f, 80f)));
    CmbExit = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "ExitDelaySec", 5f, new ConfigDescription("Ignored since 1.7.3 (kept so existing configs stay valid); see CombatExitDelaySec.", new AcceptableValueRange<float>(1f, 30f)));
    CmbEase = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "EaseSec", 3f, new ConfigDescription("Seconds to blend between normal and combat budget.", new AcceptableValueRange<float>(0.5f, 10f)));
    SkipDistant = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "DeferDistantWhenOverBudget", true, "Skip distant-object creation once the frame budget is spent.");
    MaxPerFrame = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "MaxObjectsPerFrame", 10, "Count cap per frame (vanilla 10; near cap is max(pending/100, this) like vanilla). The time budget usually stops earlier.");
    LogSlow = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "LogSlowObjects", true, "Log single objects whose creation exceeds SlowObjectMs to BepInEx/SpawnBudget.log (+ a top list every 60 s).");
    SlowMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "SlowObjectMs", 3f, "Unused since 1.3.0 (kept so existing configs stay valid); see ListObjectsOverMs.");
    ReportSec = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "ReportIntervalSeconds", 60f, "Interval of the STATS summary line.");
    HmEnabled = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "Enabled", true, "Time-slice delayed terrain (Heightmap) rebuilds so they don't stack with each other or with object spawns.");
    HmBudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "HeightmapBudgetMs", 4f, "After the first rebuild of a frame, further rebuilds only run while heightmap time this frame is below this.");
    FrameBudgetMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "CombinedFrameBudgetMs", 10f, "Defer a (non-protected) rebuild if object creation + heightmap rebuilds already used this much of the frame.");
    HmProtect = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "ProtectRadius", 96f, "Heightmaps whose center is within this many metres (XZ) of the player are never deferred.");
    HmMaxDefer = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "MaxDeferFrames", 20, "A heightmap is never deferred more than this many frames in a row.");
    HmAllPaths = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Heightmap", "SliceAllRegeneratePaths", true, "Also route immediate rebuilds (TerrainComp load / Poke(0), re-enable) of already-built heightmaps beyond ProtectRadius through the sliced path. Heightmaps that have never been built (no collider) are never deferred.");
    TailSec = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "TailSeconds", 15f, "Seconds after a loading screen / teleport / respawn ends that count as the 'tail' in STATS.");
    ArOn = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Arrival", "Enabled", true, "1.7.3: client only: while a teleport/respawn screen is up and the local player exists and is alive, create objects nearest-first within ArrivalBudgetMs per frame instead of vanilla's 100+ objects per frame (fewer 100-200 ms frames). The first world load (no player yet) stays vanilla. Can make the black screen last slightly longer because the game waits for nearby objects.");
    ArBudget = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Arrival", "ArrivalBudgetMs", 25f, new ConfigDescription("Object creation ms/frame during the arrival screen.", new AcceptableValueRange<float>(5f, 200f)));
    ArRamp = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Arrival", "ArrivalRampSec", 4f, new ConfigDescription("After the screen clears, blend from ArrivalBudgetMs down to the normal budget over this many seconds (0 = switch at once).", new AcceptableValueRange<float>(0f, 30f)));
    ArDistMax = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Arrival", "ArrivalDistantMaxPerFrame", 40, new ConfigDescription("Distant objects (location proxies etc.) created per frame during the arrival screen.", new AcceptableValueRange<int>(1, 500)));
    DistMax = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "General", "DistantMaxPerFrame", 10, new ConfigDescription("1.7.3: in-game cap on distant objects (LocationProxy etc.) created per frame; they are now created nearest-first. The time budget still applies.", new AcceptableValueRange<int>(1, 200)));
    PendStale = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Adaptive", "PendDistStaleSec", 30f, new ConfigDescription("1.7.3: a distant object that stayed un-created this long (invalid prefab, suppressed reload, zone not ready) stops counting toward pendDist pressure (0 = count all un-created).", new AcceptableValueRange<float>(0f, 600f)));
    LocMaxPF = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "LocationMaxPerFrame", 1, new ConfigDescription("1.7.3: at most this many LocationProxy location spawns per frame (also during the arrival screen once the player exists).", new AcceptableValueRange<int>(1, 10)));
    LocOverMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Locations", "LocationDeferIfFrameOverMs", 15f, new ConfigDescription("1.7.4: in-game, delay a location spawn while the previous frame took longer than this (still bounded by MaxDeferFrames; 0 = off). Default 15 so heavy swamp locations do not stack on a slow frame.", new AcceptableValueRange<float>(0f, 200f)));
    CmbEnterHold = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "CombatEnterHoldSec", 3f, new ConfigDescription("1.7.3: an alerted-hostile signal must last this long before combat mode starts (taking or dealing damage enters at once).", new AcceptableValueRange<float>(0f, 15f)));
    CmbExit2 = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Combat Mode", "CombatExitDelaySec", 12f, new ConfigDescription("1.7.3: seconds without any combat signal before combat mode ends (replaces ExitDelaySec).", new AcceptableValueRange<float>(1f, 60f)));
    ListObjMs = Config.Bind(global::ValheimPerformanceFixes.Sections.SB + "Diagnostics", "ListObjectsOverMs", 10f, "STATS lists every prefab with a single creation over this many ms (whole CreateObject incl. all mods' patches).");
    try { log = new StreamWriter(new FileStream(Path.Combine(Paths.BepInExRootPath, "SpawnBudget.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true }; } catch { }
    var h = new Harmony("vpf.ValheimPerformanceFixes.SpawnBudget");
    try { h.Patch(AccessTools.Method(typeof(Character), "ApplyDamage"), postfix: new HarmonyMethod(typeof(Plugin), nameof(DmgPost))); } catch (Exception e) { Log("combat damage hook FAILED: " + e.Message); }
    try {
      createObj = AccessTools.MethodDelegate<Func<ZNetScene, ZDO, GameObject>>(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) }));
      var target = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) });
      h.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), nameof(CreateObjectsPre)) { priority = Priority.First, before = new[] { "com.Fire.FiresGhettoNetworkMod" } });
      Log("SpawnBudget " + Ver + " object budget patched on ZNetScene.CreateObjects (first prefix), " + BudgetMs.Value + " ms/frame");
    } catch (Exception e) { Log("object patch failed, disabling that part: " + e); objOk = false; }
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
    try {
      h.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), "ShouldDelayProxyLocationSpawning"), postfix: new HarmonyMethod(typeof(Plugin), nameof(DelayPost)));
      h.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnProxyLocation"), prefix: new HarmonyMethod(typeof(Plugin), nameof(LocPre)), postfix: new HarmonyMethod(typeof(Plugin), nameof(LocPost)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(LocFin)));
      try { h.Patch(AccessTools.DeclaredMethod(typeof(ZNetScene), "RemoveObjects"), prefix: new HarmonyMethod(typeof(Plugin), nameof(RemoveObjectsPre)) { priority = Priority.First }); Log("ZNetScene.RemoveObjects guard + location edge hysteresis patched"); } catch (Exception e) { Log("RemoveObjects guard patch failed: " + e.Message); }
    try { h.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation"), transpiler: new HarmonyMethod(typeof(Plugin), nameof(LocTranspiler))); Log("location split-activation patch " + (locSplitOk ? "ok" : "NOT applied (pattern not found)") + " (enabled=" + LocSplit.Value + ")"); } catch (Exception e) { locSplitOk = false; Log("location split-activation patch failed: " + e); }
      try { lpInst = AccessTools.FieldRefAccess<LocationProxy, GameObject>("m_instance"); lpNeeds = AccessTools.FieldRefAccess<LocationProxy, bool>("m_locationNeedsSpawn");
        h.Patch(AccessTools.DeclaredMethod(typeof(LocationProxy), "SpawnLocation"), prefix: new HarmonyMethod(typeof(Plugin), nameof(ProxySpawnPre)) { priority = Priority.First }); Log("LocationProxy duplicate-spawn guard patched"); }
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
        h.Patch(AccessTools.DeclaredMethod(typeof(DungeonGenerator), "OnRoomLoaded"), prefix: new HarmonyMethod(typeof(Plugin), nameof(RoomLoadedPre)) { priority = Priority.Last });
        try {
          h.Patch(AccessTools.DeclaredMethod(typeof(DungeonGenerator), "LoadRoomPrefabsAsync"), prefix: new HarmonyMethod(typeof(Plugin), nameof(LoadRoomsPre)) { priority = Priority.Last });
          var lp = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(DungeonGenerator), "LoadRoomPrefabsAsync"));
          Log("dungeon room-load stagger patch ok (enabled=" + DgStagger.Value + ", inFlight=" + DgMaxLoads.Value + ", owners=" + (lp == null ? "-" : string.Join(",", lp.Owners)) + ")");
        } catch (Exception e) { Log("dungeon room-load stagger patch failed: " + e.Message); }
        Log("dungeon spread patch ok (enabled=" + DgSpread.Value + ", " + DgBudgetMs.Value + " ms/frame)");
      }
    } catch (Exception e) { dgOk = false; Log("dungeon patch failed, vanilla dungeon spawning: " + e); }
    nextReport = 60f;
  }
  // ---- locations ----
  static int locFrame = -1, locDeferRun, locCount; static int sLocSpawns, sLocDelayed, sLocForced; static double sLocMax; static readonly Stopwatch locSw = new Stopwatch();
  static readonly Dictionary<int, int> locLoadedSeen = new Dictionary<int, int>(); static int sLocHeld, sLocPrewarm; static bool inPrewarm;
  static void DelayPost(int hash, ref bool __result) {
    if (inPrewarm) return;
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
      if (LocPreInst != null && LocPreInst.Value && PreInstPending(hash) && locDeferRun < Math.Max(1, Math.Min(LocMaxDefer.Value, 120))) {
        locDeferRun++; sLocDelayed++; __result = true; return;
      }
      bool capHit = locFrame == Time.frameCount && locCount >= Math.Max(1, LocMaxPF.Value);
      bool slowFrame = LocOverMs.Value > 0f && Time.unscaledDeltaTime * 1000f > LocOverMs.Value;
      if (capHit) sLocCapDelayed++; else if (slowFrame) sLocSlowFrameDelayed++;
      bool busy = locPendingAct.Count > 0 || locPendingSnap.Count > 0 || capHit || slowFrame || dgActive > 0 || spawnMs > LocBudgetMs.Value || zoneFrame == Time.frameCount || HeavyBusyOther("location");
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
    var list = new List<CodeInstruction>(ins); var setActive = AccessTools.Method(typeof(GameObject), "SetActive", new[] { typeof(bool) });
    var locInst = AccessTools.Method(typeof(Plugin), nameof(LocInstantiate), new[] { typeof(SoftReferenceableAssets.SoftReference<GameObject>), typeof(Vector3), typeof(Quaternion) });
    for (int i = 0; i < list.Count; i++) {
      var mi = list[i].operand as System.Reflection.MethodInfo;
      if ((list[i].opcode == System.Reflection.Emit.OpCodes.Call || list[i].opcode == System.Reflection.Emit.OpCodes.Callvirt) && mi != null && mi.Name == "Instantiate" && mi.DeclaringType != null && mi.DeclaringType.FullName != null && mi.DeclaringType.FullName.StartsWith("SoftReferenceableAssets")) {
        var pars = mi.GetParameters();
        if (pars.Length != 3 || pars[1].ParameterType != typeof(Vector3)) continue;
        for (int j = i + 1; j < Math.Min(list.Count, i + 8); j++) {
          if ((list[j].opcode == System.Reflection.Emit.OpCodes.Callvirt || list[j].opcode == System.Reflection.Emit.OpCodes.Call) && Equals(list[j].operand, setActive)) {
            list[j] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(LocActivate))).MoveLabelsFrom(list[j]);
            list[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, locInst).MoveLabelsFrom(list[i]);
            locSplitOk = true; return list;
          }
        }
      }
    }
    return list;
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
      if (TryTakePreInst(locCurHash, position, rotation, out var pre) && pre != null) { sLocPreInstUsed++; return pre; }
    } catch { }
    LocBeforeInstantiate();
    sLocPreInstSync++;
    return SoftReferenceableAssets.Utils.Instantiate(original, position, rotation);
  }
  static GameObject LocAsset(int hash) {
    try {
      if (getLoc == null) getLoc = AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) });
      var zl = getLoc?.Invoke(ZoneSystem.instance, new object[] { hash });
      return zl == null ? null : Traverse.Create(zl).Field("m_prefab").Property("Asset").GetValue<GameObject>();
    } catch { return null; }
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
    try {
      var rs = go.GetComponentsInChildren<RandomSpawn>(true);
      for (int i = 0; i < rs.Length; i++) { var c = rs[i]; if (c != null && c.gameObject.activeSelf) c.gameObject.SetActive(false); }
    } catch { }
    try {
      var ro = go.GetComponentsInChildren<RandomObject>(true);
      for (int i = 0; i < ro.Length; i++) { var c = ro[i]; if (c != null && c.gameObject.activeSelf) c.gameObject.SetActive(false); }
    } catch { }
    if (go.activeSelf) go.SetActive(false);
  }
  static void PollPreInst() {
    if (preOps.Count == 0) return;
    tmpPreKeys.Clear();
    foreach (var kv in preOps) {
      var op = kv.Value;
      if (op == null) { tmpPreKeys.Add(kv.Key); continue; }
      if (!op.isDone) continue;
      // 1.7.5: InstantiateAsync completion (PrepLocationClone) is a heavy job â€” one per frame
      if (!TryClaimHeavy("preinst")) continue;
      var swPi = Stopwatch.StartNew();
      try {
        var arr = op.Result; GameObject go = (arr != null && arr.Length > 0) ? arr[0] : null;
        if (go != null) { PrepLocationClone(go); preReady[kv.Key] = go; if (LogSlow.Value) Log("location pre-instantiate ready: " + LocName(kv.Key) + " prep " + swPi.Elapsed.TotalMilliseconds.ToString("0.0") + " ms"); }
        else if (LogSlow.Value) Log("location pre-instantiate empty: " + LocName(kv.Key));
      } catch (Exception e) { Log("location pre-instantiate finish failed: " + LocName(kv.Key) + " " + e.Message); }
      NoteHeavyCandidate("PreInst." + LocName(kv.Key), "SpawnBudget", swPi.Elapsed.TotalMilliseconds);
      tmpPreKeys.Add(kv.Key);
    }
    for (int i = 0; i < tmpPreKeys.Count; i++) preOps.Remove(tmpPreKeys[i]);
  }
  static readonly List<int> tmpPreKeys = new List<int>();
  static bool TryStartPreInst(int hash) {
    if (LocPreInst == null || !LocPreInst.Value || hash == 0) return false;
    if (preReady.ContainsKey(hash) || preOps.ContainsKey(hash)) return true;
    if (preOps.Count >= 1) return false; // one InstantiateAsync in flight
    if (preReady.Count >= 8) return false;
    var asset = LocAsset(hash); if (asset == null) return false;
    if (!LocIsHeavyAsset(hash, asset)) return false;
    bool was = asset.activeSelf;
    try {
      if (was) asset.SetActive(false);
      var op = UnityEngine.Object.InstantiateAsync(asset, prePark, Quaternion.identity);
      if (op == null) return false;
      preOps[hash] = op; sLocPreInst++;
      if (LogSlow.Value) Log("location pre-instantiate started: " + LocName(hash));
      return true;
    } catch (Exception e) { Log("location pre-instantiate start failed: " + LocName(hash) + " " + e.Message); return false; }
    finally { if (was && asset != null && !asset.activeSelf) try { asset.SetActive(true); } catch { } }
  }
  static bool TryTakePreInst(int hash, Vector3 position, Quaternion rotation, out GameObject go) {
    go = null;
    if (hash == 0 || !preReady.TryGetValue(hash, out go) || go == null) { go = null; preReady.Remove(hash); return false; }
    preReady.Remove(hash);
    try { go.transform.SetPositionAndRotation(position, rotation); if (go.activeSelf) go.SetActive(false); }
    catch { try { UnityEngine.Object.Destroy(go); } catch { } go = null; return false; }
    return true;
  }
  static bool PreInstPending(int hash) {
    if (LocPreInst == null || !LocPreInst.Value || hash == 0) return false;
    PollPreInst();
    if (preReady.ContainsKey(hash)) return false;
    TryStartPreInst(hash);
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
      if (!TryClaimHeavy("softcap-snap")) return;
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
      NoteHeavyCandidate("SoftCap.SnapToGround", "SpawnBudget", sw0.Elapsed.TotalMilliseconds);
      if (LocSoftCap != null && LocSoftCap.Value) return; // do not also activate more on the snap frame
    }
    if (locPendingAct.Count == 0 || Time.frameCount == locPendingFrame) return;
    if (!TryClaimHeavy("softcap-act")) return;
    var sw = Stopwatch.StartNew(); int n = 0; string actNames = "";
    double bud = (LocSoftCap != null && LocSoftCap.Value) ? LocSoftCapMs.Value : 9999.0;
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
      NoteHeavyCandidate("SoftCap.Activation", "SpawnBudget", sw.Elapsed.TotalMilliseconds);
      return;
    }
    try { SnapToGround.SnappAll(); } catch { }
    locPendingSnap.Clear();
    locFrame = Time.frameCount; locCount = Math.Max(1, LocMaxPF.Value); sw.Stop();
    if (LogSlow.Value) Log("location deferred activation " + (actNames.Length > 0 ? actNames : (n + " obj")) + " " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
    NoteHeavyCandidate("LocActivation", "SpawnBudget", sw.Elapsed.TotalMilliseconds);
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
      if (locSplitOk && LocSplit.Value && locPendingAct.Count == 0 && locPendingSnap.Count == 0 && Settled()) {
        bool heavy = IsHeavy(hash) || proxyHeavyHashes.Contains(hash);
        if (getLoc == null) getLoc = AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) });
        var zl = getLoc?.Invoke(ZoneSystem.instance, new object[] { hash });
        GameObject asset = null; try { asset = zl == null ? null : Traverse.Create(zl).Field("m_prefab").Property("Asset").GetValue<GameObject>(); } catch { }
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
  static System.Reflection.FieldInfo fInst;
  static void RemoveObjectsPre(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects) {
    try { LocKeepAlive(currentNearObjects, currentDistantObjects); } catch (Exception e) { Log("location hysteresis threw: " + e.Message); }
    try {
      if (fInst == null) fInst = AccessTools.Field(typeof(ZNetScene), "m_instances");
      if (!(fInst?.GetValue(__instance) is Dictionary<ZDO, ZNetView> d)) return;
      rmDead.Clear();
      foreach (var kv in d) if (kv.Value == null || kv.Value.GetZDO() == null) rmDead.Add(kv.Key);
      if (rmDead.Count == 0) return;
      foreach (var z in rmDead) d.Remove(z);
      sRmPurged += rmDead.Count; Log("ZNetScene.RemoveObjects guard: purged " + rmDead.Count + " dead instance(s) (total " + sRmPurged + ")");
    } catch (Exception e) { Log("RemoveObjects guard failed: " + e.Message); }
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
  static void TrackProxy(ZDO zdo, GameObject go) {
    if (go == null || zdo == null || zdo.GetPrefab() != proxyHash) return;
    var nv = go.GetComponent<ZNetView>(); if (nv == null || !proxySet.Add(nv)) return; proxies.Add(nv);
  }
  static void LocKeepAlive(List<ZDO> near, List<ZDO> dist) {
    if (proxies.Count == 0 || HyOn == null) return;
    bool on = HyOn.Value && HyKeepSec.Value > 0f && Settled() && ZoneSystem.instance != null && ZNet.instance != null;
    byte b = (byte)(Time.frameCount & 0xFF); float now = Time.unscaledTime; Vector2s cz = default; int lim = 0;
    if (on) {
      if (near != null) for (int i = 0; i < near.Count; i++) near[i].TempRemoveEarmark = b;   // same marks vanilla sets next
      if (dist != null) for (int i = 0; i < dist.Count; i++) dist[i].TempRemoveEarmark = b;
      cz = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition()); lim = ZNet.instance.GetSyncedSimulationDistance().TotalSimulationDistance + Math.Max(0, HyExtra.Value);
    }
    int f = Time.frameCount; if (kaFrame != f) { kaFrame = f; keptNow.Clear(); }
    kaSeen.Clear(); kaPos.Clear(); kaNew.Clear();
    for (int i = proxies.Count - 1; i >= 0; i--) {
      var nv = proxies[i]; ZDO z = nv != null ? nv.GetZDO() : null;
      if (z == null) { proxySet.Remove(nv); proxies.RemoveAt(i); continue; } // already unloaded/destroyed
      // 1.7.3: two tracked views on one ZDO must not be processed twice (the 2nd pass saw our own earmark and reset the timer)
      if (!kaSeen.Add(z)) { proxySet.Remove(nv); proxies.RemoveAt(i); sLocDupTracked++; continue; }
      if (!on) continue;
      if (z.TempRemoveEarmark == b) { if (!keptNow.Contains(z)) proxyOutSince.Remove(z); continue; } // inside the vanilla area (or kept by us earlier this frame)
      bool first = false; if (!proxyOutSince.TryGetValue(z, out float since)) { since = now; proxyOutSince[z] = now; first = true; }
      if (now - since < HyKeepSec.Value && ZoneSystem.instance.ZonesWithinRadius(cz, z.GetSector(), lim, true)) {
        z.TempRemoveEarmark = b; keptNow.Add(z); sLocKeptFrames++;
        if (first) {
          sLocKept++; int lh = z.GetInt(ZDOVars.s_location); Vector3 zp = z.GetPosition();
          var key = new Vector3Int(lh, Mathf.RoundToInt(zp.x), Mathf.RoundToInt(zp.z));
          if (kaPos.TryGetValue(key, out var other) && other != z.m_uid) { sLocSamePos++; if (LogSlow.Value) Log("WARN two location ZDOs at the same position: " + LocName(lh) + " " + other + " and " + z.m_uid + " at " + zp.x.ToString("0") + "," + zp.z.ToString("0")); }
          else kaPos[key] = z.m_uid;
          kaNew.Add(LocName(lh) + " [" + z.m_uid + " @" + zp.x.ToString("0") + "," + zp.z.ToString("0") + "]");
        }
        continue;
      }
      proxyOutSince.Remove(z); proxySet.Remove(nv); proxies.RemoveAt(i); sLocUnloaded++; // vanilla unloads it now
      if (proxyUnloadedAt.Count > 4096) { proxyUnloadedAt.Clear(); proxySuppressedOnce.Clear(); }
      proxyUnloadedAt[z.m_uid] = now; proxySuppressedOnce.Remove(z.m_uid);
    }
    if (kaNew.Count > 0 && LogSlow.Value) Log("location(s) kept loaded at area edge (hysteresis): " + kaNew.Count + " -> " + string.Join(", ", kaNew));
    if (proxyOutSince.Count > 1024) proxyOutSince.Clear();
  }
  static bool SuppressReload(ZDO z, Vector3 rp) {
    if (proxyUnloadedAt.Count == 0 || z.GetPrefab() != proxyHash || !HyOn.Value) return false;
    if (!proxyUnloadedAt.TryGetValue(z.m_uid, out float t)) return false;
    float age = Time.unscaledTime - t;
    if (age >= HyCooldown.Value || !Settled()) { proxyUnloadedAt.Remove(z.m_uid); proxySuppressedOnce.Remove(z.m_uid); return false; }
    Vector3 d = z.GetPosition() - rp; d.y = 0f;
    if (d.sqrMagnitude <= HyClose.Value * HyClose.Value) { proxyUnloadedAt.Remove(z.m_uid); proxySuppressedOnce.Remove(z.m_uid); return false; }
    if (proxySuppressedOnce.Add(z.m_uid)) { sLocSuppressed++; if (LogSlow.Value) Log("location re-spawn suppressed (hysteresis, unloaded " + age.ToString("0.0") + " s ago, " + d.magnitude.ToString("0") + " m away): " + LocName(z.GetInt(ZDOVars.s_location))); }
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
    locSw.Stop(); if (locFrame != Time.frameCount) locCount = 0; locCount++; locFrame = Time.frameCount; sLocSpawns++; double ms = locSw.Elapsed.TotalMilliseconds;
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
    try { if (getLoc == null) getLoc = AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) }); var zl = getLoc?.Invoke(ZoneSystem.instance, new object[] { hash }); if (zl != null) { var t = Traverse.Create(zl); n = t.Field("m_prefabName").GetValue<string>() ?? t.Field("m_name").GetValue<string>() ?? n; } } catch { }
    locNames[hash] = n; return n;
  }
  static readonly int proxyHash = "LocationProxy".GetStableHashCode(); static readonly HashSet<int> prewarmed = new HashSet<int>(); static float nextPrewarmClear;
  static void Prewarm(List<ZDO> l) {
    if (l == null || !LocPrewarm.Value || !(Settled() || ArrivalOk())) return;
    var zsys = ZoneSystem.instance; if (zsys == null) return;
    if (Time.unscaledTime > nextPrewarmClear) { prewarmed.Clear(); nextPrewarmClear = Time.unscaledTime + 10f; }
    for (int i = 0; i < l.Count; i++) {
      var z = l[i]; if (z == null || z.Created || z.GetPrefab() != proxyHash) continue;
      int h = z.GetInt(ZDOVars.s_location); if (h == 0 || !prewarmed.Add(h)) continue;
      bool loading; inPrewarm = true; try { loading = zsys.ShouldDelayProxyLocationSpawning(h); } catch { loading = false; } finally { inPrewarm = false; }
      try { if (loading) { sLocPrewarm++; Log("location prewarm requested: " + LocName(h)); } else { TryStartPreInst(h); } } catch { }
    }
  }
  // ---- zones ----
  static int zoneFrame = -1, zoneDefer, sZones, sZoneDeferred;
  static bool ZonePre(ZoneSystem.SpawnMode mode, ref GameObject root, ref bool __result) {
    if (!ZoneThrottle.Value || mode != ZoneSystem.SpawnMode.Client) return true;
    try {
      if (!Settled()) { zoneDefer = 0; return true; }
      NewFrame(); int f = Time.frameCount;
      string why = zoneFrame >= f - 1 ? "zoneLastFrame" : locFrame >= f - 1 ? "location" : dgActive > 0 || dgLoading > 0 ? "dungeon" : spawnMs > BudgetMs.Value || prevSpawnMs > BudgetMs.Value ? "objBudget" : Time.unscaledDeltaTime * 1000f > ZoneHeavyMs.Value ? "heavyFrame" : HeavyBusyOther("zone") ? "heavyJob" : null;
      if (why == null) { if (!TryClaimHeavy("zone")) why = "heavyJob"; }
      if (why == null || zoneDefer >= Math.Max(1, Math.Min(ZoneMaxDefer.Value, 60))) { zoneDefer = 0; return true; }
      if (zoneDefer == 0 && LogSlow.Value) {
        string owner = "";
        if (why == "heavyFrame" || why == "heavyJob") owner = " owner=" + prevHfName + "/" + prevHfOwner + " " + prevHfMs.ToString("0.0") + "ms" + (heavyJobWhy != "-" ? (" job=" + heavyJobWhy) : "");
        Log("zone spawn postponed (" + why + ", dt " + (Time.unscaledDeltaTime * 1000f).ToString("0") + " ms" + owner + ")");
      }
      zoneDefer++; sZoneDeferred++; root = null; __result = false; return false;
    } catch { return true; }
  }
  static void ZonePost(ZoneSystem.SpawnMode mode, bool __result) { if (__result && mode == ZoneSystem.SpawnMode.Client) { zoneFrame = Time.frameCount; sZones++; } }
  // ---- dungeons ----
  static bool dgOk = true; static int dgActive, sDungeons, sDgFrames, sDgAborted; static double sDgMaxFrame;
  static System.Reflection.MethodInfo placeRoom, release, dnsBegin, dnsEnd; static int dgLoading, sDgStaggered; static double sDgStaggerWall;
  static readonly AccessTools.FieldRef<DungeonGenerator, List<SoftReferenceableAssets.IReferenceCounted>> heldRefs = AccessTools.FieldRefAccess<DungeonGenerator, List<SoftReferenceableAssets.IReferenceCounted>>("m_heldReferences");
  static readonly AccessTools.FieldRef<DungeonGenerator, ZDO> dgLoadingZdo = AccessTools.FieldRefAccess<DungeonGenerator, ZDO>("m_zdoSetToBeLoadingInZone");
  static readonly AccessTools.FieldRef<DungeonGenerator, ZNetView> dgNview = AccessTools.FieldRefAccess<DungeonGenerator, ZNetView>("m_nview");
  static Action<DungeonGenerator, SoftReferenceableAssets.AssetID, SoftReferenceableAssets.LoadResult> onRoomLoaded;
  // Replaces vanilla LoadRoomPrefabsAsync (same bookkeeping) but issues the LoadAsync calls a few unique assets at a time.
  static bool SoftDungeon() { try { return DgSoftEntry != null && DgSoftEntry.Value && InGame(); } catch { return false; } }
  static bool LoadRoomsPre(DungeonGenerator __instance) {
    if (!(DgStagger.Value || SoftDungeon())) return true;
    if (!SoftDungeon() && !Settled()) return true;
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
  static System.Collections.IEnumerator StaggerLoads(DungeonGenerator dg, Array rooms) {
    dgLoading++; var wall = Stopwatch.StartNew(); string name = dg.name; int n = rooms.Length, i = 0, frames = 0, uniq = 0; int inFlight = 0; double frameMaxMs = 0;
    var started = new HashSet<SoftReferenceableAssets.AssetID>();
    try {
      while (i < n) {
        if (dg == null) { Log("dungeon stagger aborted (generator destroyed): " + name); yield break; }
        // 1.7.5: SoftEntry room loads never share a frame with SoftCap / PreInst / zone
        if (SoftDungeon() && HeavyBusyOther("dungeon")) { yield return null; continue; }
        if (SoftDungeon()) TryClaimHeavy("dungeon");
        frames++;
        int issuedNew = 0;
        var frameSw = Stopwatch.StartNew();
        double wallMs = (DgSoftEntry != null && DgSoftEntry.Value) ? DgWallMs.Value : 9999.0;
        while (i < n) {
          var rd = (DungeonDB.RoomData)fRd.GetValue(rooms.GetValue(i));
          var pref = rd.m_prefab;
          bool isNew = !started.Contains(pref.m_assetID);
          int maxFlight = SoftDungeon() ? 1 : DgMaxLoads.Value; if (isNew && (inFlight >= maxFlight || issuedNew >= maxFlight) && frames < 600) break;
          if (frameSw.Elapsed.TotalMilliseconds >= wallMs) break;
          if (isNew) { started.Add(pref.m_assetID); uniq++; inFlight++; issuedNew++; }
          var d = dg; bool counted = isNew;
          heldRefs(dg).Add(pref);
          i++;
          pref.LoadAsync((id, res) => { if (counted) { counted = false; inFlight--; } onRoomLoaded(d, id, res); });
          if (dg == null) yield break;
        }
        if (frameSw.Elapsed.TotalMilliseconds > frameMaxMs) frameMaxMs = frameSw.Elapsed.TotalMilliseconds;
        if (i < n) yield return null;
      }
      sDgStaggered++; if (frameMaxMs > sDgStaggerWall) sDgStaggerWall = frameMaxMs;
      Log("dungeon room loads staggered: " + name + " rooms=" + n + " uniquePrefabs=" + uniq + " issueFrames=" + frames + " wallTotal=" + wall.ElapsedMilliseconds + "ms frameMax=" + frameMaxMs.ToString("0") + "ms");
    } finally { dgLoading--; }
  } static System.Reflection.FieldInfo fRd, fPos, fRot;
  static readonly AccessTools.FieldRef<DungeonGenerator, int> roomsToLoad = AccessTools.FieldRefAccess<DungeonGenerator, int>("m_roomsToLoad");
  static readonly AccessTools.FieldRef<DungeonGenerator, Array> loadedRooms = AccessTools.FieldRefAccess<DungeonGenerator, Array>("m_loadedRooms");
  static bool RoomLoadedPre(DungeonGenerator __instance, SoftReferenceableAssets.LoadResult result) {
    if (!dgOk || !(DgSpread.Value || SoftDungeon())) return true;
    try {
      if (!InGame() || result != SoftReferenceableAssets.LoadResult.Succeeded || __instance == null || __instance.gameObject == null) return true;
      if (roomsToLoad(__instance) > 1) return true; // not the last room: vanilla just decrements
      var rooms = loadedRooms(__instance); if (rooms == null) return true;
      roomsToLoad(__instance) = 0;
      inst.StartCoroutine(SpreadSpawn(__instance, rooms));
      return false;
    } catch (Exception e) { Log("dungeon spread setup failed, vanilla: " + e); return true; }
  }
  static System.Collections.IEnumerator SpreadSpawn(DungeonGenerator dg, Array rooms) {
    dgActive++; sDungeons++; int i = 0, n = rooms.Length; var sw = new Stopwatch(); string name = dg.name; var total = Stopwatch.StartNew(); int frames = 0;
    try {
      Log("dungeon spread start: " + name + " rooms=" + n);
      while (i < n) {
        if (dg == null) { sDgAborted++; Log("dungeon spread aborted (generator destroyed): " + name); yield break; }
        if (SoftDungeon() && HeavyBusyOther("dungeon")) { yield return null; continue; }
        if (SoftDungeon()) TryClaimHeavy("dungeon");
        sw.Restart(); frames++; sDgFrames++;
        bool ctx = false; try { if (dnsBegin != null) ctx = (bool)dnsBegin.Invoke(null, new object[] { dg }); } catch (Exception e) { Log("DropNSpawn context begin threw: " + e.Message); }
        try {
        do {
          object r = rooms.GetValue(i++);
          try { placeRoom.Invoke(dg, new object[] { fRd.GetValue(r), fPos.GetValue(r), fRot.GetValue(r), null, ZoneSystem.SpawnMode.Client }); }
          catch (Exception e) { Log("PlaceRoom threw (room " + (i - 1) + "): " + e); }
        } while (i < n && sw.Elapsed.TotalMilliseconds < ((DgSoftEntry != null && DgSoftEntry.Value) ? Math.Min(DgBudgetMs.Value, DgWallMs.Value) : DgBudgetMs.Value));
        } finally { if (ctx) try { dnsEnd.Invoke(null, null); } catch { } }
        double ms = sw.Elapsed.TotalMilliseconds; if (ms > sDgMaxFrame) sDgMaxFrame = ms;
        if (i < n) yield return null;
      }
      if (dg == null) { sDgAborted++; yield break; }
      { bool ctx = false; try { if (dnsBegin != null) ctx = (bool)dnsBegin.Invoke(null, new object[] { dg }); } catch { }
        try { SnapToGround.SnappAll(); } finally { if (ctx) try { dnsEnd.Invoke(null, null); } catch { } } }
      loadedRooms(dg) = null;
      try { release.Invoke(dg, null); } catch (Exception e) { Log("ReleaseHeldReferences threw: " + e); }
      Log("dungeon spread done: " + name + " rooms=" + n + " frames=" + frames + " wall=" + total.ElapsedMilliseconds + "ms");
    } finally { dgActive--; }
  }
  void Update() {
    try { PollPreInst(); RunPendingLocActivation(); } catch { }
    if (ReportSec == null) return;
    if (Time.realtimeSinceStartup < nextReport) return;
    nextReport = Time.realtimeSinceStartup + Mathf.Max(5f, ReportSec.Value); Report();
  }
  static void Log(string s) { try { log?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); } catch { } if (Mirror != null && Mirror.Value && (s.Contains("patch") || s.StartsWith("budget ENGAGED") || s.StartsWith("dungeon spread") || s.StartsWith("dungeon room loads") || s.StartsWith("dungeon stagger") || s.StartsWith("SLOW location") || s.StartsWith("DungeonGenerator") || s.Contains("threw"))) try { BLog?.LogInfo(s); } catch { } }
  static readonly string[] Others = { "com.Fire.FiresGhettoNetworkMod", "vpf.ValheimPerformanceFixes.RenderTrim", "com.Skarif.ValheimPerformanceOverhaul_WATER" };
  // Opaque loading screen: no local player yet (world load / death respawn), dead, or teleporting (Hud.UpdateBlackScreen fades to black for all three).
  static bool Loading() { var p = Player.m_localPlayer; if (p == null || p.IsDead() || p.IsTeleporting()) return true; var hd = Hud.instance; return hd != null && hd.m_loadingScreen != null && hd.m_loadingScreen.gameObject.activeInHierarchy && hd.m_loadingScreen.alpha >= 0.99f; }
  static int phaseFrame = -1; static bool phaseIn; static float tailUntil; static bool wasLoading = true;
  // true = world visible on a client -> budget applies.
  static bool InGame() {
    int f = Time.frameCount; if (f == phaseFrame) return phaseIn; phaseFrame = f;
    try {
      if (ZNet.instance == null || ZNet.instance.IsServer()) { phaseIn = false; return false; }
      bool l = Loading();
      if (wasLoading && !l) { tailUntil = Time.unscaledTime + TailSec.Value; sTails++; }
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
  // Replaces ZNetScene.CreateObjects (vanilla 1.0.20: CreateObjectsSorted + CreateDistantObjects) in-game.
  static bool CreateObjectsPre(ZNetScene __instance, ref List<ZDO> currentNearObjects, ref List<ZDO> currentDistantObjects) {
    if (!objOk || !Enabled.Value) return true;
    bool inGame = InGame(), arrival = false;
    if (!inGame) { if (!ArOn.Value || !ArrivalOk()) { arrivalWas = false; return true; } arrival = true; if (!arrivalWas) { arrivalWas = true; sArrEntered++; Log("arrival budget ENGAGED (" + ArBudget.Value.ToString("0") + " ms/frame, nearest first)"); } }
    else if (arrivalWas) { arrivalWas = false; arrivalEndAt = Time.unscaledTime; }
    NewFrame();
    // 1.7.5: also Prewarm/PreInstantiate during arrival/teleport screen so clones are ready when the world appears
    if (inGame || arrival) try { Prewarm(currentDistantObjects); Prewarm(currentNearObjects); } catch { }
    if (inGame && !engagedLogged) { engagedLogged = true; Log("budget ENGAGED (in-game, player spawned)"); }
    try { CountPendDist(currentDistantObjects); } catch { }
    double t0 = frameSw.Elapsed.TotalMilliseconds; double budget; int created = 0, cap = Mathf.Max(1, MaxPerFrame.Value); bool hit = false;
    int distCap = arrival ? Mathf.Max(1, ArDistMax.Value) : Mathf.Max(1, DistMax.Value);
    if (arrival) { budget = ArBudget.Value; cap = 100; }
    else { budget = EffBudget(); float since = Time.unscaledTime - arrivalEndAt; if (ArRamp.Value > 0f && since < ArRamp.Value) budget = Math.Max(budget, Mathf.Lerp(ArBudget.Value, (float)budget, since / ArRamp.Value)); }
    try {
      if (currentNearObjects != null && ZoneSystem.instance.IsActiveAreaLoaded()) {
        tmp.Clear(); Vector3 rp = ZNet.instance.GetReferencePosition(); float pr2 = PrioRadius.Value * PrioRadius.Value;
        for (int i = 0; i < currentNearObjects.Count; i++) { var z = currentNearObjects[i]; if (!z.Created) { Vector3 d = z.GetPosition() - rp; z.m_tempSortValue = d.x * d.x + d.y * d.y + d.z * d.z; tmp.Add(z); } }
        int num = Mathf.Max(tmp.Count / 100, cap);
        tmp.Sort(cmp); lastPendNear = tmp.Count;
        for (int i = 0; i < tmp.Count; i++) {
          bool prio = tmp[i].m_tempSortValue <= pr2 && created < PrioMax.Value;
          if (!prio && created > 0 && spawnMs + (frameSw.Elapsed.TotalMilliseconds - t0) > budget) { hit = true; sNearDeferred += tmp.Count - i; break; }
          var z = tmp[i];
          if (SuppressReload(z, rp)) continue;
          if (!ZoneSystem.instance.IsZoneReadyForType(z.GetSector(), z.Type)) continue;
          if (Create(__instance, z, false) != null) { created++; if (prio && created > num) sPrio++; if (created >= num && !(i + 1 < tmp.Count && tmp[i + 1].m_tempSortValue <= pr2 && created < PrioMax.Value)) break; }
        }
        tmp.Clear();
      }
      if (currentDistantObjects != null && (arrival || created <= cap)) {
        bool backlogSkip = DistBacklogAware != null && DistBacklogAware.Value && lastPendNear >= NearBacklog.Value;
        if (SkipDistant.Value && (spawnMs + (frameSw.Elapsed.TotalMilliseconds - t0) > budget || backlogSkip)) { hit = true; sDistantSkips++; }
        else { Vector3 rpD = ZNet.instance.GetReferencePosition(); tmpD.Clear();
          for (int i = 0; i < currentDistantObjects.Count; i++) { var o = currentDistantObjects[i]; if (o == null || o.Created) continue; Vector3 d = o.GetPosition() - rpD; o.m_tempSortValue = d.x * d.x + d.z * d.z; tmpD.Add(o); }
          if (tmpD.Count > 1) tmpD.Sort(cmpD);
          int dc = 0;
          for (int i = 0; i < tmpD.Count; i++) {
            var o = tmpD[i]; if (o.Created || SuppressReload(o, rpD)) continue;
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
  static int Cmp(ZDO x, ZDO y) { if (x.Type == y.Type) return x.m_tempSortValue.CompareTo(y.m_tempSortValue); return ((int)y.Type).CompareTo((int)x.Type); }
  // ---- whole CreateObject timing ----
  static readonly Stopwatch coSw = Stopwatch.StartNew(); static readonly Stack<double> coStart = new Stack<double>();
  static int coFrame = -1; static double coFrameMs; static int sLoadFrames; static double sLoadMax, sLoadTotal; static int sLoadObjs;
  static int sTails, sTailFrames, sTailHit; static double sTailMax;
  static readonly Dictionary<int, PStat> pstats = new Dictionary<int, PStat>(); static readonly Dictionary<int, string> owners = new Dictionary<int, string>();
  class PStat { public int hash; public string name; public int n, nLoad; public double total, totalLoad, max; }
  static void CoPre() { if (coStart.Count > 16) coStart.Clear(); coStart.Push(coSw.Elapsed.TotalMilliseconds); }
  static void CoPost(ZNetScene __instance, ZDO zdo, GameObject __result) {
    if (coStart.Count == 0) return; double ms = coSw.Elapsed.TotalMilliseconds - coStart.Pop();
    try { TrackProxy(zdo, __result); } catch { }
    if (coStart.Count > 0) return; // nested creation is counted in the outer object
    try {
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
    bool run = protect || n >= HmMaxDefer.Value
      || (hmRuns == 0 && spawnMs + hmMs < FrameBudgetMs.Value)
      || (hmMs < HmBudgetMs.Value && spawnMs + hmMs < FrameBudgetMs.Value);
    if (!run) { deferred[__instance] = n + 1; sHmDeferrals++; return false; }
    if (n >= HmMaxDefer.Value && !protect) sHmForced++;
    if (n > 0) deferred.Remove(__instance); __state = frameSw.Elapsed.TotalMilliseconds; inLateRegen = true; return true;
  }
  static void HmPost(Heightmap __instance, double __state) {
    inLateRegen = false;
    if (__state < 0) return; double ms = frameSw.Elapsed.TotalMilliseconds - __state; hmMs += ms; hmRuns++; sHmRuns++; sHmTotal += ms; if (ms > sHmMax) sHmMax = ms;
    if (ms > 8 && LogSlow.Value) { string w = "?"; try { w = __instance.transform.position.ToString("F0") + (__instance.IsDistantLod ? " distantLOD" : ""); } catch { } Log("SLOW heightmap rebuild " + ms.ToString("0.0") + " ms at " + w + " (spawn this frame " + spawnMs.ToString("0.0") + " ms)"); }
  }
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
      .Append("\n   arrival: entered=").Append(sArrEntered).Append(" frames=").Append(sArrFrames).Append(" objs=").Append(sArrObjs).Append(" budgetHit=").Append(sArrHit).Append(" maxFrameSpawn=").Append(sArrMax.ToString("0.0")).Append("ms")
      .Append(" | pendDist=").Append(lastPendDist).Append(" stale=").Append(lastPendDistStale).Append(" combat=").Append(cmbIn);
    Log("location hysteresis " + ReportSec.Value.ToString("0") + "s: kept at edge=" + sLocKept + " suppressed re-spawns=" + sLocSuppressed + " unloaded=" + sLocUnloaded + " duplicate spawns blocked=" + sLocDup + " (enabled=" + HyOn.Value + ")");
    sLocKept = sLocKeptFrames = sLocSuppressed = sLocUnloaded = sLocDup = sLocDupTracked = sLocSamePos = 0;
    if (Mirror.Value) try { BLog.LogInfo(sb.ToString().Split('\n')[0] + " | loc=" + sLocSpawns + "/" + sLocDelayed + "d" + " maxLoc=" + sLocMax.ToString("0") + "ms(" + sLocMaxName + ") held=" + sLocHeld + " prewarm=" + sLocPrewarm + " zonesPostponed=" + sZoneDeferred + " dungeons=" + sDungeons + " dgMaxFrame=" + sDgMaxFrame.ToString("0.0") + "ms"); } catch { }
    sLocSpawns = sLocDelayed = sLocForced = sZones = sZoneDeferred = sDungeons = sDgFrames = sDgAborted = sDgStaggered = sLocHeld = sLocPrewarm = sLocSplit = sLocPreInst = sLocPreInstUsed = sLocPreInstSync = 0; sDgStaggerWall = 0; sLocMaxName = "-"; sLocMax = sDgMaxFrame = 0;
    Log(sb.ToString()); pstats.Clear(); sLoadFrames = sLoadObjs = sTails = sTailFrames = sTailHit = sRegenDeferred = 0; sLoadMax = sLoadTotal = sTailMax = 0; coFrameMs = 0;
    sPrio = sCombatFrames = 0; sObjs = sBudgetHitFrames = sNearDeferred = sDistantSkips = sHmDeferrals = sHmForced = sHmRuns = sActiveFrames = sOverOne = 0; sHmMax = sHmTotal = sMaxSpawn = sSpawnTotal = 0;
    var dead = new List<Heightmap>(); foreach (var k in deferred.Keys) if (k == null) dead.Add(k); foreach (var k in dead) deferred.Remove(k);
  }
}

internal sealed class ConfigurationManagerAttributes { public bool? IsAdvanced; }

/// <summary>Read by RenderTrim (reflection, soft) for adaptive trimming.</summary>

}
// Stays in the global namespace: RenderTrim finds it by name (AccessTools.TypeByName("SpawnBudgetPressure")).
public static class SpawnBudgetPressure { public static float Value, Raw, BudgetMs; public static int Instances, PendingNear, PendingDistant; public static bool Combat; }