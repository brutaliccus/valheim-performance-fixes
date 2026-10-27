using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Riverheim;
using Riverheim.Pipeline;
using Riverheim.Rendering;
using Riverheim.Util.Random;
using Riverheim.World;

namespace RiverheimCache {

// Caches Riverheim's final worldgen products (WorldState + Rendering/HeightMap, i.e. everything WorldRendererStage
// consumes) to disk. On a later join with identical key, the 60-stage pipeline is skipped and the WorldRenderer is
// rebuilt from the cached data exactly as WorldRendererStage would build it. Any failure -> normal computation.
public class Plugin : global::ValheimPerformanceFixes.ModuleBehaviour {
  internal static BepInEx.Logging.ManualLogSource L;
  const int FORMAT = 1;
  const string MAGIC = "RHCACHE1";
  static ConfigEntry<bool> Enabled, VerifyFirstLoad, VerifyEveryLoad;
  static ConfigEntry<int> SamplePoints;

  // per-Compute state
  static volatile WorldState capWs; static volatile HeightMap capHm;
  static string curKey, curPath; static int curSeed; static bool curVerifyRun; static byte[] curLoadedSha; static WorldRenderer curLoadedRenderer;

  void Awake() {
    L = Logger;
    Enabled = Config.Bind(global::ValheimPerformanceFixes.Sections.RH + "General", "Enabled", true, "Cache Riverheim worldgen results on disk (BepInEx/cache/RiverheimCache). Disable to always compute normally.");
    VerifyFirstLoad = Config.Bind(global::ValheimPerformanceFixes.Sections.RH + "Safety", "VerifyFirstLoad", true, "The first time a newly written cache file would be used, also compute normally and require the computed data to be byte-identical to the cache (proves determinism). The computed result is used for that join. Afterwards the cache is trusted (still SHA256-checked on every load).");
    VerifyEveryLoad = Config.Bind(global::ValheimPerformanceFixes.Sections.RH + "Safety", "VerifyEveryLoad", false, "Debug: verify against a full computation on every join (no time saving).");
    SamplePoints = Config.Bind(global::ValheimPerformanceFixes.Sections.RH + "Safety", "SamplePoints", 4096, "Number of world points sampled to compare the cache-built renderer against the computed one when a cache is written/verified.");
    SeedFromPluginFolder();
    try {
      var h = new Harmony("vpf.ValheimPerformanceFixes.RiverheimCache");
      h.Patch(AccessTools.Method(typeof(GeneratorPipeline), "Compute", new[] { typeof(int), typeof(object) }),
        prefix: new HarmonyMethod(typeof(Plugin), nameof(ComputePre)) { priority = Priority.First },
        postfix: new HarmonyMethod(typeof(Plugin), nameof(ComputePost)));
      h.Patch(AccessTools.Method(typeof(WorldStateCompiler), "Compute"), postfix: new HarmonyMethod(typeof(Plugin), nameof(WsPost)));
      h.Patch(AccessTools.Method(typeof(HeightMapBuilder), "Compute"), postfix: new HarmonyMethod(typeof(Plugin), nameof(HmPost)));
      L.LogInfo("RiverheimCache patches applied");
    } catch (Exception e) { L.LogError("RiverheimCache disabled (patch failed): " + e); }
  }

  static void WsPost(WorldState __result) { capWs = __result; }
  static void HmPost(HeightMap __result) { capHm = __result; }

  static string Sha(byte[] b) { using (var s = SHA256.Create()) return Hex(s.ComputeHash(b)); }
  // Seed shipped cache files (*.rhc, *.rhc.verified) from this plugin's folder into BepInEx/cache/RiverheimCache.
  // Never overwrites. Key/verify logic is unchanged: a seeded file with a non-matching key is simply never looked up.
  void SeedFromPluginFolder() {
    try {
      var src = Path.GetDirectoryName(Info.Location); if (string.IsNullOrEmpty(src) || !Directory.Exists(src)) return;
      var dir = Path.Combine(Paths.CachePath, "RiverheimCache"); Directory.CreateDirectory(dir);
      int n = 0;
      foreach (var f in Directory.GetFiles(src).Where(x => x.EndsWith(".rhc", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".rhc.verified", StringComparison.OrdinalIgnoreCase))) {
        var dst = Path.Combine(dir, Path.GetFileName(f));
        if (File.Exists(dst) || File.Exists(dst + ".bad") || File.Exists(Path.ChangeExtension(dst, null) + ".bad")) continue;
        try { File.Copy(f, dst, false); n++; } catch (Exception e) { L.LogWarning("RiverheimCache seed copy failed (harmless): " + Path.GetFileName(f) + " " + e.Message); }
      }
      if (n > 0) L.LogMessage("RiverheimCache seeded " + n + " file(s) from plugin folder into " + dir);
    } catch (Exception e) { L.LogWarning("RiverheimCache seeding failed (harmless): " + e.Message); }
  }

  static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "");

  static string BuildKey(int seed, WorldGenerationConfig cfg) {
    var sb = new StringBuilder();
    sb.Append("fmt=").Append(FORMAT).Append("|seed=").Append(seed);
    var pi = BepInEx.Bootstrap.Chainloader.PluginInfos["dev.gurebu.riverheim.stable"];
    sb.Append("|rh=").Append(pi.Metadata.Version);
    sb.Append("|rhdll=").Append(Sha(File.ReadAllBytes(typeof(GeneratorPipeline).Assembly.Location)));
    sb.Append("|game=").Append(global::Version.GetVersionString());
    sb.Append("|cfg=").Append(Sha(GraphSerializer.Serialize(cfg)));
    return sb.ToString();
  }

  static bool ComputePre(GeneratorPipeline __instance, int seed, object masterConfig) {
    capWs = null; capHm = null; curKey = null; curPath = null; curVerifyRun = false; curLoadedSha = null; curLoadedRenderer = null;
    if (!Enabled.Value) return true;
    try {
      if (!(masterConfig is WorldGenerationConfig cfg)) return true;
      curSeed = seed;
      curKey = BuildKey(seed, cfg);
      string kh; using (var s = SHA256.Create()) kh = Hex(s.ComputeHash(Encoding.UTF8.GetBytes(curKey))).Substring(0, 16);
      var dir = Path.Combine(Paths.CachePath, "RiverheimCache"); Directory.CreateDirectory(dir);
      string world = "world"; try { world = San(ZNet.World?.m_name ?? "world"); } catch { }
      curPath = Path.Combine(dir, world + "_" + seed + "_" + kh + ".rhc");
      if (File.Exists(curPath + ".bad")) { L.LogWarning("cache key previously failed verification; computing normally"); curPath = null; return true; }
      if (!File.Exists(curPath)) { L.LogMessage("Riverheim cache MISS (" + curPath + "); computing normally, will save"); return true; }
      var sw = System.Diagnostics.Stopwatch.StartNew();
      byte[] payload = ReadFile(curPath, curKey, out var sha);
      var roots = (object[])GraphSerializer.Deserialize(payload);
      var ws = (WorldState)roots[0]; var hm = (HeightMap)roots[1];
      var r = MakeRenderer(seed, cfg, ws, hm);
      long tLoad = sw.ElapsedMilliseconds;
      bool verify = VerifyEveryLoad.Value || (VerifyFirstLoad.Value && !File.Exists(curPath + ".verified"));
      if (verify) {
        curVerifyRun = true; curLoadedSha = sha; curLoadedRenderer = r;
        L.LogMessage($"Riverheim cache loaded in {tLoad}ms; VERIFY run: computing normally too and comparing");
        return true;
      }
      PutResult(__instance, r);
      L.LogMessage($"Riverheim cache HIT: loaded world gen from cache in {tLoad}ms (skipped pipeline) {curPath}");
      curPath = null; // nothing to save
      return false;
    } catch (Exception e) {
      L.LogWarning("Riverheim cache load failed, computing normally: " + e);
      try { if (curPath != null && File.Exists(curPath)) File.Delete(curPath); } catch { }
      curVerifyRun = false; curLoadedRenderer = null;
      return true;
    }
  }

  static void ComputePost(GeneratorPipeline __instance, int seed, object masterConfig) {
    try {
      if (curPath == null || !(masterConfig is WorldGenerationConfig cfg)) return;
      var ws = capWs; var hm = capHm;
      if (ws == null || hm == null) { L.LogWarning("did not capture WorldState/HeightMap; not caching"); return; }
      var real = __instance.GetResult<WorldRenderer>("Rendering/WorldRenderer");
      string path = curPath, key = curKey; bool verifyRun = curVerifyRun; var loadedSha = curLoadedSha; var loadedR = curLoadedRenderer;
      var t = new Thread(() => {
        try {
          var sw = System.Diagnostics.Stopwatch.StartNew();
          byte[] bytes = GraphSerializer.Serialize(new object[] { ws, hm });
          byte[] sha; using (var s = SHA256.Create()) sha = s.ComputeHash(bytes);
          if (verifyRun) {
            bool same = loadedSha != null && sha.SequenceEqual(loadedSha);
            bool samples = same && SamplesEqual(real, loadedR, SamplePoints.Value, out _);
            if (same && samples) { File.WriteAllText(path + ".verified", DateTime.Now.ToString("o")); L.LogMessage($"Riverheim cache VERIFIED: computed data byte-identical to cache ({bytes.Length / 1048576.0:0.0} MB) and {SamplePoints.Value} samples match; future joins use cache"); }
            else { File.WriteAllText(path + ".bad", "mismatch bytes=" + same + " samples=" + samples); TryDel(path); L.LogError("Riverheim cache verification FAILED (data " + (same ? "same" : "differs") + "); cache disabled for this key"); }
            return;
          }
          // round-trip check before writing
          var clone = (object[])GraphSerializer.Deserialize(bytes);
          byte[] again = GraphSerializer.Serialize(clone);
          if (!again.SequenceEqual(bytes)) { L.LogError("round-trip mismatch; not caching"); return; }
          var cr = MakeRenderer(seed, cfg, (WorldState)clone[0], (HeightMap)clone[1]);
          if (!SamplesEqual(real, cr, SamplePoints.Value, out string why)) { L.LogError("cache-built renderer differs from computed (" + why + "); not caching"); File.WriteAllText(path + ".bad", why); return; }
          WriteFile(path, key, bytes, sha);
          Prune(path);
          L.LogMessage($"Riverheim cache SAVED {bytes.Length / 1048576.0:0.0} MB in {sw.ElapsedMilliseconds}ms (round-trip + {SamplePoints.Value} samples OK): {path}");
        } catch (Exception e) { L.LogWarning("Riverheim cache save failed (harmless): " + e); }
      }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "RiverheimCacheSave" };
      t.Start();
    } catch (Exception e) { L.LogWarning("Riverheim cache post failed (harmless): " + e); }
    finally { capWs = null; capHm = null; }
  }

  static WorldRenderer MakeRenderer(int seed, WorldGenerationConfig cfg, WorldState ws, HeightMap hm)
    => new WorldRenderer(new RandomDirectory(seed).Sub("Rendering"), cfg.common, cfg.rendering.renderer, ws, hm);

  static void PutResult(GeneratorPipeline p, WorldRenderer r) {
    var cache = AccessTools.Field(typeof(GeneratorPipeline), "cache").GetValue(p);
    AccessTools.Method(cache.GetType(), "Put").Invoke(cache, new object[] { "Rendering/WorldRenderer", r });
  }

  static bool SamplesEqual(WorldRenderer a, WorldRenderer b, int n, out string why) {
    why = null; var rnd = new System.Random(12345); double R = 10600;
    for (int i = 0; i < n; i++) {
      var p = new Rfloat2((rnd.NextDouble() * 2 - 1) * R, (rnd.NextDouble() * 2 - 1) * R);
      var x = a.RenderPoint(p); var y = b.RenderPoint(p);
      if (x.biome != y.biome || x.height != y.height || x.fertility != y.fertility || x.lava != y.lava || x.snow != y.snow) { why = $"RenderPoint differs at {p}: {x.height} vs {y.height}"; return false; }
      if (a.GetBiome(p) != b.GetBiome(p)) { why = "GetBiome differs at " + p; return false; }
      if (a.GetForestation(p) != b.GetForestation(p)) { why = "GetForestation differs at " + p; return false; }
    }
    return true;
  }

  static void WriteFile(string path, string key, byte[] payload, byte[] sha) {
    var tmp = path + ".tmp";
    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) using (var w = new BinaryWriter(fs)) {
      w.Write(MAGIC); w.Write(key); w.Write(payload.Length); w.Write(sha); w.Write(payload);
    }
    TryDel(path); TryDel(path + ".verified"); File.Move(tmp, path);
  }

  static byte[] ReadFile(string path, string key, out byte[] sha) {
    using (var fs = File.OpenRead(path)) using (var r = new BinaryReader(fs)) {
      if (r.ReadString() != MAGIC) throw new InvalidDataException("bad magic");
      if (r.ReadString() != key) throw new InvalidDataException("key mismatch");
      int len = r.ReadInt32(); sha = r.ReadBytes(32); var payload = r.ReadBytes(len);
      if (payload.Length != len) throw new InvalidDataException("truncated");
      using (var s = SHA256.Create()) if (!s.ComputeHash(payload).SequenceEqual(sha)) throw new InvalidDataException("checksum mismatch");
      return payload;
    }
  }

  static void Prune(string keep) {
    try {
      var name = Path.GetFileName(keep); var prefix = name.Substring(0, name.LastIndexOf('_') + 1);
      foreach (var f in Directory.GetFiles(Path.GetDirectoryName(keep), prefix + "*"))
        if (!f.StartsWith(keep)) TryDel(f);
    } catch { }
  }
  static void TryDel(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
  static string San(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }
}

}
