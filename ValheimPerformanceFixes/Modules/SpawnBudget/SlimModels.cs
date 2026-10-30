using System; using System.Collections; using System.Collections.Generic; using System.Diagnostics; using System.Reflection;
using BepInEx.Configuration; using HarmonyLib; using UnityEngine; using SoftReferenceableAssets;
namespace SpawnBudget {
// 2.2.0 "Slim Location Models" (client only).
//  When the client loads a location (a ruin, a camp, a tower...), the game copies the whole location prefab, including a copy of
//  every networked object in it (stones, walls, chests, creatures' spawners...). Those copies are switched off before the copy
//  and never woken: the real networked objects arrive separately from the network. On big locations that is most of what is
//  copied, so most of the copy cost is wasted.
//  After a location type has been loaded twice this session, a stripped-down "template" of it is built in the background: a
//  copy kept under a hidden, switched-off holder, from which the never-woken networked copies are removed a few per frame.
//  A networked copy that some kept part points at (a door's lock, a spawner's object, a switch...) is kept but left switched off,
//  so nothing that expects it finds it missing. From then on the location is copied from the template instead of the full prefab.
//  Just before every copy, the template is given the same on/off state as the prefab has at that moment (vanilla's random
//  pieces, its biome, its interior placement), so the result matches what the game would have built. If anything does not
//  match (the prefab changed, a removed object would have been awake, another mod patches the spawn), the game's own copy is used.
//  A template is dropped after IdleUnloadSec without use and on logout; its asset reference is never released while the game quits.
public partial class Plugin {
  static ConfigEntry<bool> SlOn; static ConfigEntry<int> SlMinDead; static ConfigEntry<float> SlMinShare, SlWorkMs, SlIdleSec;
  enum SlState { None, Wanted, Working, NotWorth, Ready }
  sealed class SlEntry {
    public int hash; public string name; public SoftReference<GameObject> sref; public bool held; public GameObject asset, template; public SlState state; public int uses; public float lastUse;
    public IEnumerator work; public AsyncInstantiateOperation<GameObject> op;
    public GameObject[] mirSrc, mirDst; public GameObject[] strippedSrc; public int dead, total, stripped, keptRef;
  }
  static readonly Dictionary<int, SlEntry> slim = new Dictionary<int, SlEntry>();
  static GameObject slHolder; static bool slOk, slAudited, slQuitting; static ZoneSystem.ZoneLocation curZl;
  static int sSlBuilt, sSlUsed, sSlNotWorth, sSlDropped, sSlStripped, sSlFallback;
  const int SlMaxTemplates = 32;

  void SlimConfig() {
    SlOn = Config.Bind(SBS + "Slim Location Models", "Enabled", true, "Client only. After a location type has loaded twice, build a lighter copy of it (without the switched-off networked copies the game throws away) and load the location from that copy from then on. Built a little at a time in quiet frames. Off = the game copies the full prefab every time.");
    SlMinDead = Config.Bind(SBS + "Slim Location Models", "MinDeadObjects", 100, new ConfigDescription("Only build a light copy when at least this many objects of the location are never-woken networked copies.", new AcceptableValueRange<int>(10, 5000)));
    SlMinShare = Config.Bind(SBS + "Slim Location Models", "MinDeadShare", 0.3f, new ConfigDescription("...and when they are at least this share (0-1) of the whole location.", new AcceptableValueRange<float>(0.05f, 0.95f)));
    SlWorkMs = Config.Bind(SBS + "Slim Location Models", "WorkMsPerFrame", 2f, new ConfigDescription("Time per frame spent building light copies (only in frames with nothing else heavy going on). Set by the Quick Setup slider.", new AcceptableValueRange<float>(0.5f, 8f)));
    SlIdleSec = Config.Bind(SBS + "Slim Location Models", "IdleUnloadSec", 300f, new ConfigDescription("Drop a light copy that has not been used for this many seconds (frees its memory).", new AcceptableValueRange<float>(30f, 3600f)));
    Application.quitting += () => slQuitting = true;
  }
  // SpawnLocation prefix: remember which location this is, and never let the prefab change while Unity is still copying it
  static void SlSpawnPre(ZoneSystem.ZoneLocation location) {
    curZl = location;
    try {
      if (location == null) return; int hash = location.Hash;
      if (preOps.TryGetValue(hash, out var op) && op != null && !op.isDone) { op.WaitForCompletion(); }
      if (slim.TryGetValue(hash, out var e) && e.op != null && !e.op.isDone) e.op.WaitForCompletion();
    } catch { }
  }
  static Exception SlSpawnFin(Exception __exception) { curZl = null; return __exception; }
  static bool SlActive() { return slOk && SlOn != null && SlOn.Value && locSplitOk && !OwnerAtOnce(); }
  static bool SlReady(int hash) { return SlActive() && slim.TryGetValue(hash, out var e) && e.state == SlState.Ready && e.template != null; }
  static void SlAudit() {
    slAudited = true;
    try { var slm = AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnLocation"); var o = global::ValheimPerformanceFixes.PatchGuard.Others(slm);
      // 2.3.7: checked before any light copy is built (first use, every build start), so none is built and then thrown away
      if (o.Count > 0 && slOk) { int had = slim.Count; slOk = false; SlDropAll(null); Log("slim location models off for this session: ZoneSystem.SpawnLocation is also patched by " + string.Join(", ", o) + " (checked before building any; " + sSlBuilt + " built, " + had + " tracked until now)"); } } catch { }
  }

  // ---- the copy, called from LocInstantiate (client SpawnLocation) ----
  static GameObject SlimTake(int hash, SoftReference<GameObject> original, Vector3 pos, Quaternion rot) {
    if (!SlActive() || hash == 0 || !slim.TryGetValue(hash, out var e) || e.state != SlState.Ready) return null;
    GameObject asset = null; try { asset = original.Asset; } catch { }
    if (asset == null || asset != e.asset || e.template == null) { SlDrop(e, "prefab changed"); return null; }
    try {
      for (int i = 0; i < e.strippedSrc.Length; i++) { var s = e.strippedSrc[i]; if (s == null || s.activeSelf) { sSlFallback++; return null; } }
      if (!ApplyMirror(asset, e.template, e.mirSrc, e.mirDst)) { SlDrop(e, "copy no longer matches the prefab"); return null; }
    } catch (Exception x) { SlDrop(e, "state copy failed: " + x.Message); return null; }
    if (original.Load() != LoadResult.Succeeded) { try { original.Release(); } catch { } return null; }
    GameObject go = null;
    try {
      e.template.SetActive(asset.activeSelf); // the split activation may have the prefab switched off for this one
      go = UnityEngine.Object.Instantiate(e.template, pos, rot);
    } catch (Exception x) { Log("slim location copy failed for " + e.name + ": " + x.Message); }
    finally { try { if (e.template != null) e.template.SetActive(false); } catch { } }
    if (go == null) { try { original.Release(); } catch { } SlDrop(e, "copy failed"); return null; }
    go.TransferReference(original); sSlUsed++;
    return go;
  }
  // every client location copy counts as a use (also the ones served by a pre-built clone)
  static void SlNoteUse(int hash, SoftReference<GameObject> original) {
    if (!slAudited && slOk) SlAudit(); // 2.3.7
    if (!SlActive() || hash == 0) return;
    if (!slim.TryGetValue(hash, out var e)) { e = new SlEntry { hash = hash, sref = original }; try { e.name = curZl != null ? curZl.m_prefabName : null; } catch { } slim[hash] = e; }
    e.uses++; e.lastUse = Time.unscaledTime;
    if (e.state == SlState.None && e.uses >= 2) e.state = SlState.Wanted;
  }
  // give dst (a copy of asset) the asset's current on/off state, biome and interior placement. dst pairs come from a walk of the same hierarchy.
  static bool ApplyMirror(GameObject asset, GameObject dstRoot, GameObject[] src, GameObject[] dst) {
    for (int i = 0; i < src.Length; i++) { var s = src[i]; var d = dst[i]; if (s == null || d == null) return false; bool a = s.activeSelf; if (d.activeSelf != a) d.SetActive(a); }
    var sl = asset.GetComponent<Location>(); var dl = dstRoot.GetComponent<Location>();
    if (sl != null && dl != null) {
      dl.m_biome = sl.m_biome;
      if (sl.m_interiorTransform != null && dl.m_interiorTransform != null) { dl.m_interiorTransform.localPosition = sl.m_interiorTransform.localPosition; dl.m_interiorTransform.localRotation = sl.m_interiorTransform.localRotation; }
      if (sl.m_generator != null && dl.m_generator != null) dl.m_generator.transform.localPosition = sl.m_generator.transform.localPosition;
    }
    return true;
  }
  // every object whose on/off state vanilla changes on the prefab before copying it
  static List<GameObject> MirrorCandidates(GameObject asset) {
    var set = new HashSet<GameObject>(); var l = new List<GameObject>();
    void Add(GameObject g) { if (g != null && g != asset && set.Add(g)) l.Add(g); }
    foreach (var c in asset.GetComponentsInChildren<ZNetView>(true)) if (c != null) Add(c.gameObject);
    foreach (var c in asset.GetComponentsInChildren<RandomSpawn>(true)) if (c != null) { Add(c.gameObject); Add(c.m_OffObject); }
    foreach (var c in asset.GetComponentsInChildren<RandomObject>(true)) if (c != null) { Add(c.gameObject); if (c.m_objects != null) foreach (var o in c.m_objects) if (o != null) Add(o.m_object); }
    for (int i = l.Count - 1; i >= 0; i--) if (!l[i].transform.IsChildOf(asset.transform)) l.RemoveAt(i); // entries pointing outside the prefab are not ours to mirror
    return l;
  }
  // pre-instantiated clones: per prefab, the candidates as child-index paths (resolved on each clone)
  sealed class PathPlan { public GameObject asset; public GameObject[] src; public int[][] paths; }
  static readonly Dictionary<int, PathPlan> pathPlans = new Dictionary<int, PathPlan>();
  static int[] PathOf(Transform root, Transform t) { var p = new List<int>(); while (t != null && t != root) { p.Add(t.GetSiblingIndex()); t = t.parent; } p.Reverse(); return p.ToArray(); }
  static bool MirrorClone(GameObject asset, GameObject clone) {
    int id = asset.GetInstanceID();
    if (!pathPlans.TryGetValue(id, out var pl) || pl.asset != asset) {
      var c = MirrorCandidates(asset); pl = new PathPlan { asset = asset, src = c.ToArray(), paths = new int[c.Count][] };
      for (int i = 0; i < c.Count; i++) pl.paths[i] = PathOf(asset.transform, c[i].transform);
      pathPlans[id] = pl; if (pathPlans.Count > 64) { pathPlans.Clear(); pathPlans[id] = pl; }
    }
    var dst = new GameObject[pl.src.Length];
    for (int i = 0; i < dst.Length; i++) {
      Transform t = clone.transform; var p = pl.paths[i];
      for (int k = 0; k < p.Length; k++) { if (t == null || p[k] >= t.childCount) return false; t = t.GetChild(p[k]); }
      if (t == null || t.name != pl.src[i].name) return false; dst[i] = t.gameObject;
    }
    return ApplyMirror(asset, clone, pl.src, dst);
  }

  // ---- building, from Update ----
  static readonly Stopwatch slSw = new Stopwatch();
  static void SlimTick() {
    if (slim.Count == 0) return;
    if (ZNetScene.instance == null || ZNet.instance == null) { SlDropAll(null); return; } // logout: the holder went with the scene
    if (!slAudited) SlAudit(); // 2.3.7: right away (was: once settled in the world, after copies were already built)
    if (!SlActive()) { if (!slOk || (SlOn != null && !SlOn.Value)) SlDropAll(null); return; }
    float now = Time.unscaledTime; SlEntry pick = null; int ready = 0; SlEntry oldest = null;
    foreach (var e in slim.Values) {
      if (e.state == SlState.Ready || e.state == SlState.Working) { ready++; if (now - e.lastUse > SlIdleSec.Value) { SlDrop(e, null); return; } if (oldest == null || e.lastUse < oldest.lastUse) oldest = e; }
      if ((e.state == SlState.Wanted || e.state == SlState.Working) && (pick == null || pick.state != SlState.Working)) pick = e;
    }
    if (pick == null) return;
    if (pick.state == SlState.Wanted && ready >= SlMaxTemplates) { if (oldest != null && oldest.state == SlState.Ready) SlDrop(oldest, null); else return; }
    bool load = Loading(); if (!load && (!Settled() || !WbCanStart() || !TryClaimHeavy("slim"))) return;
    if (pick.op != null && !pick.op.isDone) return; // Unity is still copying; nothing to do this frame
    slSw.Restart(); double budget = Math.Max(0.5, SlWorkMs.Value) * (load ? 3 : 1);
    try {
      if (pick.state == SlState.Wanted) { SlAudit(); if (!slOk) return; pick.work = SlBuild(pick); pick.state = SlState.Working; } // 2.3.7: patch check again right before a build starts
      while (slSw.Elapsed.TotalMilliseconds < budget) {
        if (!pick.work.MoveNext()) { pick.work = null; break; }
        if (pick.work.Current != null) break; // "wait for Unity" (the async copy)
      }
    } catch (Exception x) { Log("slim location build failed for " + pick.name + ": " + x.Message); SlDrop(pick, null); pick.state = SlState.NotWorth; }
    double ms = slSw.Elapsed.TotalMilliseconds; if (!load) WbCharge(ms); global::ValheimPerformanceFixes.FrameCost.Add("slim-locations", ms, "light location copy");
  }
  static GameObject Holder() {
    if (slHolder == null) { slHolder = new GameObject("vpf location templates"); slHolder.SetActive(false); slHolder.hideFlags = HideFlags.HideInHierarchy; }
    return slHolder;
  }
  static IEnumerator SlBuild(SlEntry e) {
    GameObject asset = null; try { if (e.sref.IsLoaded) asset = e.sref.Asset; } catch { }
    if (asset == null) { e.state = SlState.Wanted; yield break; } // retried the next time it is in memory
    if (asset.GetComponent<ZNetView>() != null) { e.state = SlState.NotWorth; sSlNotWorth++; yield break; }
    if (e.name == null) e.name = asset.name;
    // 1. which objects are never-woken networked copies ("dead"): a ZNetView object, switched on all the way up to the root
    var all = asset.GetComponentsInChildren<Transform>(true); e.total = all.Length;
    var deadRoots = new List<Transform>(); var deadSet = new HashSet<Transform>(); var deadOf = new Dictionary<Transform, Transform>();
    foreach (var t in all) {
      if (t == asset.transform) continue; var nv = t.GetComponent<ZNetView>(); if (nv == null || !nv.enabled) continue;
      bool on = true; for (var p = t; p != null && p != asset.transform; p = p.parent) if (!p.gameObject.activeSelf) { on = false; break; }
      if (!on) continue;
      // topmost only: a dead object inside another dead object belongs to the outer one (its ancestors are all switched on here)
      bool inner = false; for (var p = t.parent; p != null && p != asset.transform; p = p.parent) { var pn = p.GetComponent<ZNetView>(); if (pn != null && pn.enabled) { inner = true; break; } }
      if (inner) continue;
      deadRoots.Add(t);
    }
    yield return null;
    foreach (var r in deadRoots) foreach (var t in r.GetComponentsInChildren<Transform>(true)) { deadSet.Add(t); deadOf[t] = r; }
    e.dead = deadSet.Count;
    if (e.dead < SlMinDead.Value || e.dead < e.total * SlMinShare.Value) { e.state = SlState.NotWorth; sSlNotWorth++; Log("slim location models: " + e.name + " not worth it (" + e.dead + " of " + e.total + " objects are never-woken copies)"); yield break; }
    // 2. keep the prefab in memory for as long as the template exists, then let Unity copy it in the background
    try { e.sref.HoldReference(); e.held = true; } catch { e.state = SlState.NotWorth; yield break; }
    e.asset = asset;
    e.op = UnityEngine.Object.InstantiateAsync(asset, Holder().transform);
    yield return e.op; // SlimTick waits for isDone
    while (e.op != null && !e.op.isDone) yield return e.op;
    var res = e.op != null ? e.op.Result : null; e.op = null;
    var tpl = res != null && res.Length > 0 ? res[0] : null;
    if (tpl == null || asset == null) throw new Exception("template copy came back empty");
    tpl.name = asset.name; tpl.SetActive(false); e.template = tpl;
    // 3. walk prefab and template side by side
    var map = new Dictionary<Transform, Transform>(all.Length);
    var stack = new Stack<KeyValuePair<Transform, Transform>>(); stack.Push(new KeyValuePair<Transform, Transform>(asset.transform, tpl.transform));
    int n = 0;
    while (stack.Count > 0) {
      var kv = stack.Pop(); if (kv.Key == null || kv.Value == null || kv.Key.childCount != kv.Value.childCount || kv.Key.name != kv.Value.name && kv.Key != asset.transform) throw new Exception("template does not match the prefab");
      map[kv.Key] = kv.Value; for (int i = 0; i < kv.Key.childCount; i++) stack.Push(new KeyValuePair<Transform, Transform>(kv.Key.GetChild(i), kv.Value.GetChild(i)));
      if (++n % 500 == 0) yield return null;
    }
    // 4. a dead object that any kept part points at stays (switched off). Kept parts include the ones kept because of this.
    var keep = new HashSet<Transform>(); var work = new Queue<Transform>(); var comps = new List<Component>();
    foreach (var t in all) if (!deadSet.Contains(t)) work.Enqueue(t);
    var seen = new HashSet<Transform>(); int cc = 0;
    while (work.Count > 0) {
      var t = work.Dequeue(); if (t == null || !seen.Add(t)) continue;
      comps.Clear(); t.GetComponents(comps);
      foreach (var c in comps) {
        if (c == null) continue;
        RefScan(c, o => {
          Transform ot = o is Component oc ? oc.transform : o is GameObject og ? og.transform : null;
          if (ot == null || !deadOf.TryGetValue(ot, out var root) || !keep.Add(root)) return;
          foreach (var k in root.GetComponentsInChildren<Transform>(true)) work.Enqueue(k);
        });
        if (++cc % 40 == 0) yield return null;
      }
    }
    e.keptRef = keep.Count;
    // 5. mirror pairs (prefab object -> template object)
    var cand = MirrorCandidates(asset); var ms = new List<GameObject>(); var md = new List<GameObject>(); var stripped = new List<GameObject>();
    foreach (var g in cand) { var t = g.transform; if (deadOf.TryGetValue(t, out var r) && !keep.Contains(r)) continue; if (!map.TryGetValue(t, out var d)) throw new Exception("template is missing an object"); ms.Add(g); md.Add(d.gameObject); }
    foreach (var r in deadRoots) if (keep.Contains(r)) { map[r].gameObject.SetActive(false); } else stripped.Add(r.gameObject);
    // 6. remove the rest from the template, a few per step
    int done = 0;
    foreach (var r in stripped) { var d = map[r.transform]; if (d != null) UnityEngine.Object.DestroyImmediate(d.gameObject); done++; sSlStripped++; if (done % 8 == 0) yield return null; }
    for (int i = 0; i < md.Count; i++) if (md[i] == null) throw new Exception("a kept object was removed");
    e.mirSrc = ms.ToArray(); e.mirDst = md.ToArray(); e.strippedSrc = stripped.ToArray(); e.stripped = stripped.Count;
    e.state = SlState.Ready; sSlBuilt++;
    Log("slim location models: light copy of " + e.name + " ready: " + e.total + " objects, " + e.dead + " never-woken copies, " + (e.dead - KeptCount(keep)) + " removed, " + keep.Count + " kept (referenced)");
  }
  static int KeptCount(HashSet<Transform> keep) { int n = 0; foreach (var r in keep) if (r != null) n += r.GetComponentsInChildren<Transform>(true).Length; return n; }

  // every UnityEngine.Object a component points at through its saved (serialized) data
  static readonly Dictionary<Type, FieldInfo[]> refFields = new Dictionary<Type, FieldInfo[]>();
  static FieldInfo[] RefFieldsOf(Type t) {
    if (refFields.TryGetValue(t, out var f)) return f;
    var l = new List<FieldInfo>();
    for (var b = t; b != null && b != typeof(MonoBehaviour) && b != typeof(object) && b != typeof(UnityEngine.Object); b = b.BaseType)
      foreach (var fi in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
        if (fi.IsNotSerialized || fi.IsInitOnly && !fi.IsPublic) continue;
        if (!fi.IsPublic && fi.GetCustomAttribute<SerializeField>() == null && fi.GetCustomAttribute<SerializeReference>() == null) continue;
        if (MayHoldRefs(fi.FieldType, 0)) l.Add(fi);
      }
    f = l.ToArray(); refFields[t] = f; return f;
  }
  static bool MayHoldRefs(Type t, int depth) {
    if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t.IsPointer) return false;
    if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return true;
    if (t.IsArray) return MayHoldRefs(t.GetElementType(), depth);
    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return MayHoldRefs(t.GetGenericArguments()[0], depth);
    if (depth > 6) return false;
    if (t.Namespace != null && t.Namespace.StartsWith("System")) return false;
    if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine") && t.IsValueType) return false; // Vector3, Color, ...
    if (!t.IsSerializable && t.GetCustomAttribute<SerializableAttribute>() == null) return false;
    return true;
  }
  static void RefScan(Component c, Action<UnityEngine.Object> hit) {
    switch (c) {
      case Transform _: return;
      case LODGroup lg: foreach (var lod in lg.GetLODs()) if (lod.renderers != null) foreach (var r in lod.renderers) if (r != null) hit(r); return;
      case Joint j: if (j.connectedBody != null) hit(j.connectedBody); return;
      case SkinnedMeshRenderer sm: if (sm.rootBone != null) hit(sm.rootBone); var bs = sm.bones; if (bs != null) foreach (var b in bs) if (b != null) hit(b); if (sm.probeAnchor != null) hit(sm.probeAnchor); return;
      case Renderer rr: if (rr.probeAnchor != null) hit(rr.probeAnchor); return;
      case ParticleSystem ps:
        var se = ps.subEmitters; for (int i = 0; i < se.subEmittersCount; i++) { var s = se.GetSubEmitterSystem(i); if (s != null) hit(s); }
        var sh = ps.shape; if (sh.meshRenderer != null) hit(sh.meshRenderer); if (sh.skinnedMeshRenderer != null) hit(sh.skinnedMeshRenderer); if (sh.spriteRenderer != null) hit(sh.spriteRenderer);
        var tr = ps.trigger; for (int i = 0; i < tr.colliderCount; i++) { var x = tr.GetCollider(i); if (x != null) hit(x); }
        var co = ps.collision; for (int i = 0; i < co.planeCount; i++) { var x = co.GetPlane(i); if (x != null) hit(x); }
        return;
      case UnityEngine.Animations.IConstraint ic: { var src = new List<UnityEngine.Animations.ConstraintSource>(); ic.GetSources(src); foreach (var s in src) if (s.sourceTransform != null) hit(s.sourceTransform); return; }
      case MonoBehaviour mb: ScanObj(mb, mb.GetType(), hit, 0); return;
    }
  }
  static void ScanObj(object o, Type t, Action<UnityEngine.Object> hit, int depth) {
    if (o == null || depth > 6) return;
    foreach (var fi in RefFieldsOf(t)) { object v; try { v = fi.GetValue(o); } catch { continue; } ScanVal(v, hit, depth); }
  }
  static void ScanVal(object v, Action<UnityEngine.Object> hit, int depth) {
    if (v == null) return;
    if (v is UnityEngine.Object uo) { if (uo != null) hit(uo); return; }
    if (v is IList il) { if (il.Count > 4096) return; foreach (var x in il) ScanVal(x, hit, depth + 1); return; }
    var t = v.GetType(); if (!MayHoldRefs(t, depth + 1)) return; ScanObj(v, t, hit, depth + 1);
  }

  static void SlDrop(SlEntry e, string why) {
    try { if (e.op != null && !e.op.isDone) e.op.WaitForCompletion(); } catch { }
    try { if (e.op != null && e.op.isDone && e.op.Result != null) foreach (var g in e.op.Result) if (g != null) UnityEngine.Object.Destroy(g); } catch { }
    e.op = null; e.work = null;
    try { if (e.template != null) UnityEngine.Object.Destroy(e.template); } catch { }
    e.template = null; e.asset = null; e.mirSrc = e.mirDst = e.strippedSrc = null;
    if (e.held) { e.held = false; if (!slQuitting) try { e.sref.Release(); } catch { } }
    if (e.state == SlState.Ready || e.state == SlState.Working) sSlDropped++;
    e.state = why == null ? SlState.None : SlState.NotWorth; e.uses = 0;
    if (why != null) Log("slim location models: " + e.name + " uses the game's own copy from now on (" + why + ")");
  }
  static void SlDropAll(string why) { foreach (var e in slim.Values) SlDrop(e, why); slim.Clear(); }
  static string SlimStats() {
    int ready = 0; foreach (var e in slim.Values) if (e.state == SlState.Ready) ready++;
    return "slimLocations " + (!slOk ? "OFF/stood down" : SlOn.Value ? "on" : "off") + " ready=" + ready + " built=" + sSlBuilt + " used=" + sSlUsed + " notWorth=" + sSlNotWorth + " dropped=" + sSlDropped + " removedObjs=" + sSlStripped + " fallback=" + sSlFallback;
  }
  static void SlimStatsReset() { sSlBuilt = sSlUsed = sSlNotWorth = sSlDropped = sSlStripped = sSlFallback = 0; }
}
}
