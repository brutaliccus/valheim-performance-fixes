using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace RenderTrim
{
    // 1.7.8: first-use warm-ups. Nothing is instantiated: renderers' meshes+materials are drawn by a hidden, disabled
    // camera into a tiny off-screen texture, which makes Unity compile the shader variants and upload meshes/textures
    // before the real object first appears. No ZDOs, no scripts, no sounds, nothing visible.
    internal static class Prewarm
    {
        internal static ConfigEntry<bool> lwOn, lwEquip, lwPlayerFx, lwContainer, lwBiomeNow, lwAllShaders;
        internal static ConfigEntry<float> lwBudget;
        internal static ConfigEntry<bool> bpOn; internal static ConfigEntry<float> bpBudget, bpLook; internal static ConfigEntry<int> bpMax;

        internal static void Bind(ConfigFile c)
        {
            const string A = "9b. Loading Warm-up", B = "9c. Biome Prewarm";
            lwOn = c.Bind(A, "Enabled", true, "Do first-time graphics prep while the loading screen is still black, so the first swing, hit or chest does not stutter.");
            lwBudget = c.Bind(A, "BudgetMsPerFrame", 25f, new ConfigDescription("How many milliseconds per frame the warm-up may use while the screen is black. Anything left when the screen fades continues at the Biome Prewarm budget.", new AcceptableValueRange<float>(5f, 100f)));
            lwEquip = c.Bind(A, "EquippedWeaponEffects", true, "Prepare the swing/hit/block effects (sparks, blood, trails) of the weapons and tools you have equipped.");
            lwPlayerFx = c.Bind(A, "PlayerEffects", true, "Prepare your character's own effects (getting hit, dodging, jumping, etc.).");
            lwContainer = c.Bind(A, "ContainerGrid", true, "Build the chest window's slot grid once in the background, so the first chest you open is quicker.");
            lwBiomeNow = c.Bind(A, "CurrentBiome", true, "Prepare the plants, creatures and weather effects of the biome you spawn in.");
            lwAllShaders = c.Bind(A, "WarmupAllShaders", false, "Brute force: compile every loaded shader at once (Shader.WarmupAllShaders). Can add several seconds to loading. Off unless you still see first-time hitches.");
            bpOn = c.Bind(B, "Enabled", true, "Prepare a biome's plants, creatures and weather effects as soon as that biome comes within LookaheadDistance, before you can see it. Each biome is done once per session.");
            bpBudget = c.Bind(B, "BudgetMsPerFrame", 2f, new ConfigDescription("Milliseconds per frame the background prep may use. Lower = smoother but slower to finish. (A single new shader can take longer than this; it cannot be split.)", new AcceptableValueRange<float>(0.5f, 10f)));
            bpLook = c.Bind(B, "LookaheadDistance", 300f, new ConfigDescription("How far ahead (meters) to look for biomes you have not met yet this session. Further = more time to finish before you arrive.", new AcceptableValueRange<float>(100f, 600f)));
            bpMax = c.Bind(B, "MaxItemsPerFrame", 6, new ConfigDescription("Most mesh/material pairs drawn per frame.", new AcceptableValueRange<int>(1, 40)));
        }

        struct Item { public Mesh m; public Material mat; public int sub; }
        static readonly Queue<GameObject> prefabQ = new Queue<GameObject>();
        static readonly Queue<Item> drawQ = new Queue<Item>();
        static readonly HashSet<int> seenPrefab = new HashSet<int>(), seenMat = new HashSet<int>();
        static readonly HashSet<Heightmap.Biome> warmedBiomes = new HashSet<Heightmap.Biome>();
        static Camera cam; static RenderTexture rt; static Mesh quad;
        static bool loadDone, allShadersDone; static float nextScan; static Vector3 lastPos; static bool hasLast;
        static int drawnTotal, prefabsTotal; static double msTotal; static string phase = "";
        static float playerSeen = -1f;

        static void Reset()
        {
            prefabQ.Clear(); drawQ.Clear(); seenPrefab.Clear(); warmedBiomes.Clear(); loadDone = false; allShadersDone = false; hasLast = false;
            drawnTotal = 0; prefabsTotal = 0; msTotal = 0; playerSeen = -1f;
        }

        internal static void Tick(Plugin p, bool black)
        {
            var pl = Player.m_localPlayer;
            if (pl == null || ZNet.instance == null) { if (playerSeen >= 0) Reset(); return; }
            if (playerSeen < 0) playerSeen = Time.unscaledTime;
            try
            {
                if (black && lwOn.Value && !loadDone)
                {
                    loadDone = true; phase = "loading";
                    var sw = System.Diagnostics.Stopwatch.StartNew(); var what = new List<string>();
                    if (lwAllShaders.Value && !allShadersDone) { allShadersDone = true; Shader.WarmupAllShaders(); what.Add("allShaders"); }
                    if (lwEquip.Value) { int n = 0; foreach (var it in pl.GetInventory().GetEquippedItems()) { CollectItem(it?.m_shared); n++; } what.Add("equipped x" + n); }
                    if (lwPlayerFx.Value) { CollectEffects(pl, 0); what.Add("player fx"); }
                    if (lwContainer.Value) what.Add(WarmContainer(pl));
                    if (lwBiomeNow.Value) { var b = pl.GetCurrentBiome(); QueueBiome(b); what.Add("biome " + b); }
                    p.L("Loading warm-up queued (" + string.Join(", ", what.ToArray()) + "): " + prefabQ.Count + " prefabs " + sw.ElapsedMilliseconds + "ms");
                }
                if (bpOn.Value && Time.unscaledTime >= nextScan && !black) { nextScan = Time.unscaledTime + 0.5f; ScanAhead(p, pl); }
                if (prefabQ.Count == 0 && drawQ.Count == 0) return;
                bool fast = black && lwOn.Value;
                if (!fast && !bpOn.Value && !lwOn.Value) return;
                if (!fast && (Plugin.CleanupLoading() == false && Time.unscaledTime - playerSeen < 1f)) return;
                Work(p, fast ? lwBudget.Value : bpBudget.Value, fast ? 400 : bpMax.Value);
            }
            catch (Exception e) { p.L("Prewarm error: " + e.Message); prefabQ.Clear(); drawQ.Clear(); }
        }

        static void ScanAhead(Plugin p, Player pl)
        {
            var wg = WorldGenerator.instance; if (wg == null) return;
            Vector3 pos = pl.transform.position; Vector3 dir = hasLast ? pos - lastPos : Vector3.zero; lastPos = pos; hasLast = true;
            dir.y = 0; float L = bpLook.Value;
            var pts = new List<Vector2>(20);
            for (int i = 0; i < 8; i++) { float a = i * Mathf.PI / 4f; var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a)); pts.Add(d * L); pts.Add(d * (L * 0.5f)); }
            if (dir.sqrMagnitude > 0.5f) { var d = new Vector2(dir.x, dir.z).normalized; pts.Add(d * L * 1.5f); pts.Add(d * L * 2f); }
            pts.Add(Vector2.zero);
            foreach (var o in pts)
            {
                var b = wg.GetBiome(pos.x + o.x, pos.z + o.y);
                if (!warmedBiomes.Contains(b)) { QueueBiome(b); p.L("Biome prewarm: " + b + " detected " + (int)o.magnitude + "m away, queued (" + prefabQ.Count + " prefabs pending)"); }
            }
        }

        static void QueueBiome(Heightmap.Biome b)
        {
            if (!warmedBiomes.Add(b)) return;
            var zs = ZoneSystem.instance;
            if (zs != null) foreach (var v in zs.m_vegetation) if (v != null && v.m_enable && v.m_prefab != null && (v.m_biome & b) != 0) Add(v.m_prefab);
            foreach (var ss in SpawnSystems())
                foreach (var list in ss.m_spawnLists) if (list != null) foreach (var s in list.m_spawners)
                    if (s != null && s.m_enabled && s.m_prefab != null && (s.m_biome & b) != 0) { Add(s.m_prefab); CollectCreature(s.m_prefab); }
            var em = EnvMan.instance;
            if (em != null) foreach (var be in em.m_biomes) if (be != null && (be.m_biome & b) != 0) foreach (var e in be.m_environments)
                        if (e?.m_env?.m_psystems != null) foreach (var ps in e.m_env.m_psystems) Add(ps);
        }

        static IEnumerable<SpawnSystem> SpawnSystems()
        {
            var r = new List<SpawnSystem>();
            try { var z = ZoneSystem.instance?.m_zoneCtrlPrefab; var s = z != null ? z.GetComponent<SpawnSystem>() : null; if (s != null) r.Add(s); } catch { }
            try { var f = typeof(SpawnSystem).GetField("m_instances", BindingFlags.NonPublic | BindingFlags.Static); if (f?.GetValue(null) is List<SpawnSystem> l) foreach (var s in l) if (s != null && !r.Contains(s)) r.Add(s); } catch { }
            return r;
        }

        static void CollectCreature(GameObject go)
        {
            var c = go.GetComponent<Character>(); if (c != null) CollectEffects(c, 0);
            var h = c as Humanoid;
            if (h != null && h.m_defaultItems != null) foreach (var d in h.m_defaultItems) { var id = d != null ? d.GetComponent<ItemDrop>() : null; if (id != null) CollectItem(id.m_itemData?.m_shared); }
        }
        static void CollectItem(ItemDrop.ItemData.SharedData sd)
        {
            if (sd == null) return; CollectEffects(sd, 0); CollectEffects(sd.m_attack, 0); CollectEffects(sd.m_secondaryAttack, 0);
        }
        static readonly Dictionary<Type, FieldInfo[]> fxFields = new Dictionary<Type, FieldInfo[]>();
        static void CollectEffects(object o, int depth)
        {
            if (o == null) return;
            var t = o.GetType();
            if (!fxFields.TryGetValue(t, out var fs))
            {
                var l = new List<FieldInfo>();
                for (var tt = t; tt != null && tt != typeof(MonoBehaviour) && tt != typeof(object); tt = tt.BaseType)
                    foreach (var f in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) if (f.FieldType == typeof(EffectList)) l.Add(f);
                fxFields[t] = fs = l.ToArray();
            }
            foreach (var f in fs)
            {
                var el = f.GetValue(o) as EffectList;
                if (el?.m_effectPrefabs != null) foreach (var e in el.m_effectPrefabs) if (e != null && e.m_enabled && e.m_prefab != null) Add(e.m_prefab);
            }
        }
        static void Add(GameObject go) { if (go != null && seenPrefab.Add(go.GetInstanceID())) prefabQ.Enqueue(go); }

        static string WarmContainer(Player pl)
        {
            try
            {
                var gui = InventoryGui.instance; if (gui == null) return "container: no gui";
                var f = typeof(InventoryGui).GetField("m_containerGrid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var grid = f?.GetValue(gui) as InventoryGrid; if (grid == null) return "container: no grid";
                if (InventoryGui.IsVisible()) return "container: skipped (open)";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                grid.UpdateInventory(new Inventory("RenderTrimWarm", null, 8, 4), pl, null);
                return "container grid " + sw.ElapsedMilliseconds + "ms";
            }
            catch (Exception e) { return "container FAILED " + e.Message; }
        }

        static void EnsureCam()
        {
            if (cam != null) return;
            rt = new RenderTexture(64, 64, 16) { name = "RenderTrimWarmRT" }; rt.Create();
            var go = new GameObject("RenderTrimWarmCam"); UnityEngine.Object.DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideAndDontSave;
            cam = go.AddComponent<Camera>();
            var main = Camera.main; if (main != null) cam.CopyFrom(main);
            cam.enabled = false; cam.targetTexture = rt; cam.cullingMask = 1 << 31; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 20f; cam.fieldOfView = 60f; cam.allowHDR = main != null && main.allowHDR; cam.useOcclusionCulling = false;
            go.transform.position = new Vector3(0f, -9000f, 0f); go.transform.rotation = Quaternion.identity;
            quad = new Mesh { name = "RenderTrimWarmQuad", vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) }, uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }, triangles = new[] { 0, 2, 1, 2, 3, 1 } };
            quad.RecalculateNormals(); quad.RecalculateBounds();
        }

        static void Expand(GameObject go)
        {
            prefabsTotal++;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                Mesh m = null;
                if (r is SkinnedMeshRenderer smr) m = smr.sharedMesh;
                else if (r is MeshRenderer) { var mf = r.GetComponent<MeshFilter>(); m = mf != null ? mf.sharedMesh : null; }
                else if (r is ParticleSystemRenderer pr) m = pr.renderMode == ParticleSystemRenderMode.Mesh && pr.mesh != null ? pr.mesh : quad;
                else if (r is LineRenderer || r is TrailRenderer) m = quad;
                if (m == null) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i]; if (mat == null || mat.shader == null) continue;
                    if (!seenMat.Add(mat.GetInstanceID())) continue;
                    drawQ.Enqueue(new Item { m = m, mat = mat, sub = Mathf.Min(i, Math.Max(0, m.subMeshCount - 1)) });
                }
                if (r is ParticleSystemRenderer pr2 && pr2.trailMaterial != null && seenMat.Add(pr2.trailMaterial.GetInstanceID())) drawQ.Enqueue(new Item { m = quad, mat = pr2.trailMaterial, sub = 0 });
            }
        }

        static void Work(Plugin p, float budgetMs, int maxItems)
        {
            EnsureCam();
            var sw = System.Diagnostics.Stopwatch.StartNew(); int drawn = 0;
            var ct = cam.transform; var at = ct.position + ct.forward * 3f;
            while (sw.Elapsed.TotalMilliseconds < budgetMs * 0.5 && drawQ.Count < maxItems && prefabQ.Count > 0) Expand(prefabQ.Dequeue());
            while (drawQ.Count > 0 && drawn < maxItems)
            {
                var it = drawQ.Dequeue(); if (it.m == null || it.mat == null) continue;
                var s = it.m.bounds.size.magnitude; float k = s > 0.001f ? 1.5f / s : 1f;
                Graphics.DrawMesh(it.m, Matrix4x4.TRS(at - it.m.bounds.center * k, Quaternion.Euler(20f, 30f, 0f), Vector3.one * k), it.mat, 31, cam, it.sub, null, false, false, false);
                drawn++;
                if (sw.Elapsed.TotalMilliseconds >= budgetMs * 0.5) break;
            }
            if (drawn > 0) cam.Render();
            drawnTotal += drawn; msTotal += sw.Elapsed.TotalMilliseconds;
            if (prefabQ.Count == 0 && drawQ.Count == 0)
                p.L("Prewarm queue done: " + prefabsTotal + " prefabs, " + drawnTotal + " material draws, " + msTotal.ToString("0") + "ms total, biomes warmed: " + string.Join(",", Biomes()));
        }
        static string[] Biomes() { var l = new List<string>(); foreach (var b in warmedBiomes) l.Add(b.ToString()); return l.ToArray(); }
    }
}