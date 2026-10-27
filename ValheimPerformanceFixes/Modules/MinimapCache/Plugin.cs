using System; using System.Collections.Generic; using System.Diagnostics; using System.IO; using System.Linq; using System.Security.Cryptography; using System.Text;
using BepInEx; using HarmonyLib; using UnityEngine;

namespace MinimapCache {
// Points vanilla's own minimap texture cache (TryLoadMinimapTextureData / SaveMapTextureDataToDisk) at a keyed folder when it
// is unset (dedicated-server clients).
// 1.1.1 ROOT CAUSE of "miss on every join": 1.1.0 kept the cache under BepInEx\cache, and the modpack launcher re-syncs that BepInEx tree
// at every launch, so the folder (same key every time) was recreated empty before each join (cache dir created 20:30:53, files
// written 20:31:05 on 2026-10-25 after a launch at 20:28). Riverheim's cache survived only because RiverheimCache re-seeds it from
// its plugin folder. Fix: default root is Application.persistentDataPath\MinimapCache (LocalLow\IronGate\Valheim, outside the modpack launcher),
// a stable key (SHA1 of comment-stripped worldgen cfgs instead of string.GetHashCode of the whole file, RiverheimCache excluded),
// a key.txt per dir, explicit HIT/MISS logging with the reason, load/generate/save timing, and migration of an old 1.1.0 dir.
// 1.1.2: SaveHitchFix (see SaveFix.cs).
public class Plugin : global::ValheimPerformanceFixes.ModuleBehaviour {
  static BepInEx.Logging.ManualLogSource L; static bool done, usingOurDir; static string ourDir, keyStr;
  static BepInEx.Configuration.ConfigEntry<bool> BypassVer, Migrate, Verbose; static BepInEx.Configuration.ConfigEntry<string> Root;
  static readonly string[] Files = { "cacheMinimapMask", "cacheMinimapBiome", "cacheMinimapHeight", "cacheMinimapMeta" };
  void Awake() { L = Logger;
    BypassVer = Config.Bind(global::ValheimPerformanceFixes.Sections.MC + "General", "BypassClientWorldVersionCheck", true, "Vanilla rejects the minimap cache when ZNet.World.m_worldVersion != current world version. On a dedicated-server client that value is 0, so the cache NEVER hits and the minimap is regenerated (~11 s) every join. When true, the check is bypassed only while loading from this mod's own keyed cache dir (key = world name+seed+game version+worldgen mods+their configs).");
    Root = Config.Bind(global::ValheimPerformanceFixes.Sections.MC + "General", "CacheRoot", "", "1.1.1: folder that holds the keyed cache dirs. Empty = <Valheim persistent data>\\MinimapCache (LocalLow\\IronGate\\Valheim\\MinimapCache), which the modpack launcher does not wipe. Do NOT point it inside the the modpack launcher BepInEx folder: that is re-synced every launch (the cause of the 1.1.0 miss on every join).");
    Migrate = Config.Bind(global::ValheimPerformanceFixes.Sections.MC + "General", "MigrateOldCache", true, "1.1.1: if the new dir is empty, copy a complete 1.1.0 cache (BepInEx\\cache\\MinimapCache\\<world>_*) for the same world and seed into it.");
    Verbose = Config.Bind(global::ValheimPerformanceFixes.Sections.MC + "Diagnostics", "LogKeyDetails", true, "1.1.1: log the full cache key and the other cache dirs found for this world (to spot key drift).");
    var h = new Harmony("vpf.ValheimPerformanceFixes.MinimapCache");
    try { h.Patch(AccessTools.DeclaredMethod(typeof(Minimap), "Update"), prefix: new HarmonyMethod(typeof(Plugin), nameof(Pre))); } catch (Exception e) { L.LogError("patch failed: " + e.Message); }
    try { var m = AccessTools.DeclaredMethod(typeof(Minimap), "TryLoadMinimapTextureData"); if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(TryPre)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(TryFin))); else L.LogWarning("TryLoadMinimapTextureData not found; version bypass inactive"); } catch (Exception e) { L.LogError("version-bypass patch failed: " + e.Message); }
    try { h.Patch(AccessTools.DeclaredMethod(typeof(Minimap), "GenerateWorldMap"), prefix: new HarmonyMethod(typeof(Plugin), nameof(GenPre)), postfix: new HarmonyMethod(typeof(Plugin), nameof(GenPost))); } catch (Exception e) { L.LogWarning("generate timing patch failed: " + e.Message); }
    try { h.Patch(AccessTools.DeclaredMethod(typeof(Minimap), "SaveMapTextureDataToDisk"), postfix: new HarmonyMethod(typeof(Plugin), nameof(SavePost))); } catch (Exception e) { L.LogWarning("save verify patch failed: " + e.Message); }
    SaveFix.Init(Config, h, L);
  }
  void Update() { try { SaveFix.Tick(); } catch { }  }
  static global::Version.World? savedVer = null; static readonly Stopwatch sw = new Stopwatch();
  static void TryPre() {
    savedVer = null; sw.Restart();
    try {
      if (!usingOurDir || !BypassVer.Value || ZNet.World == null) return;
      var cur = global::Version.World.CheatCleanSlate;
      if (ZNet.World.m_worldVersion != cur) { savedVer = ZNet.World.m_worldVersion; ZNet.World.m_worldVersion = cur; L.LogInfo("bypassing client worldVersion check (" + savedVer + " -> " + cur + ") for cached minimap"); }
    } catch (Exception e) { L.LogWarning("bypass skipped: " + e.Message); }
  }
  static Exception TryFin(Exception __exception, bool __result) {
    try { if (savedVer.HasValue && ZNet.World != null) ZNet.World.m_worldVersion = savedVer.Value; } catch { }
    if (usingOurDir) L.LogMessage("Minimap cache " + (__result ? "HIT: loaded in " + sw.ElapsedMilliseconds + " ms (skipped regeneration)" : "MISS at load: " + MissReason(ourDir) + "; vanilla will regenerate") + (__exception != null ? " (exception " + __exception.Message + ")" : ""));
    savedVer = null; return __exception;
  }
  static readonly Stopwatch gsw = new Stopwatch();
  static void GenPre() { gsw.Restart(); }
  static void GenPost() { if (!usingOurDir) return; int ls = -1; try { ls = Convert.ToInt32(AccessTools.Property(AccessTools.TypeByName("FileHelpers"), "LocalStorageSupport")?.GetValue(null) ?? AccessTools.Field(AccessTools.TypeByName("FileHelpers"), "LocalStorageSupport")?.GetValue(null) ?? -1); } catch { }
    L.LogMessage("Minimap regenerated in " + gsw.ElapsedMilliseconds + " ms" + (ls == 2 ? " (saved to " + ourDir + ")" : " but vanilla did NOT save it: FileHelpers.LocalStorageSupport=" + ls + " (needs 2)")); }
  static void SavePost() {
    if (!usingOurDir) return;
    try {
      string why = MissReason(ourDir);
      if (why == null) { File.WriteAllText(ourDir + "key.txt", keyStr); L.LogMessage("Minimap cache SAVED: " + string.Join(", ", Files.Select(f => f + "=" + new FileInfo(ourDir + f).Length))); }
      else L.LogWarning("Minimap cache save FAILED/incomplete (" + why + ") in " + ourDir + " (see vanilla SaveMapTextureDataToDisk error above)");
    } catch (Exception e) { L.LogWarning("save verify failed: " + e.Message); }
  }
  // null = complete and valid for this world seed
  static string MissReason(string dir) {
    try {
      var missing = Files.Where(f => !File.Exists(dir + f)).ToList();
      if (missing.Count == 4) return "no cache files in dir (first join with this key, or the dir was wiped)";
      if (missing.Count > 0) return "missing " + string.Join(",", missing) + " (incomplete save)";
      var b = File.ReadAllBytes(dir + "cacheMinimapMeta");
      if (b.Length < 8) return "meta file truncated (" + b.Length + " bytes)";
      int seed = BitConverter.ToInt32(b, 0), ver = BitConverter.ToInt32(b, 4);
      if (ZNet.World != null && seed != ZNet.World.m_seed) return "meta seed " + seed + " != world seed " + ZNet.World.m_seed;
      if (ver != 1) return "meta version " + ver + " != 1";
      if (ZNet.World != null && !BypassVer.Value && ZNet.World.m_worldVersion != global::Version.World.CheatCleanSlate) return "client worldVersion " + ZNet.World.m_worldVersion + " (BypassClientWorldVersionCheck is off)";
      foreach (var f in Files) if (new FileInfo(dir + f).Length == 0) return f + " is empty";
      return null;
    } catch (Exception e) { return "check failed: " + e.Message; }
  }
  static string CfgHash(string path) {
    if (!File.Exists(path)) return "nocfg";
    var sb = new StringBuilder();
    foreach (var raw in File.ReadAllLines(path)) { var l = raw.Trim(); if (l.Length == 0 || l.StartsWith("#") || l.StartsWith(";")) continue; sb.Append(l).Append('\n'); }
    using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 12);
  }
  static void Pre(Minimap __instance) {
    if (done) return;
    try {
      var t = Traverse.Create(__instance);
      if (t.Field<bool>("m_hasGenerated").Value || WorldGenerator.instance == null || ZNet.World == null) return;
      done = true;
      var vp = t.Field<string>("m_cachedMinimapMaskTexturePath").Value;
      if (!string.IsNullOrEmpty(vp) && File.Exists(vp)) { L.LogMessage("vanilla cache path already set and present (" + vp + "); leaving it to vanilla"); return; }
      // key: world + seed + game version + worldgen plugins (GUID@version) + normalized hash of their cfg. RiverheimCache only caches; it is excluded.
      var sb = new StringBuilder(ZNet.World.m_name + "|" + ZNet.World.m_seed + "|" + global::Version.GetVersionString());
      foreach (var p in BepInEx.Bootstrap.Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.GUID, StringComparer.Ordinal)) {
        var g = p.Metadata.GUID.ToLowerInvariant();
        if (g.Contains("riverheimcache") || g.Contains("minimapcache")) continue;
        if (g.Contains("riverheim") || g.Contains("worldgen") || g.Contains("expandworld") || g.Contains("betterbiome") || g.Contains("customseed"))
          sb.Append("|" + p.Metadata.GUID + "@" + p.Metadata.Version + "#" + CfgHash(Path.Combine(Paths.ConfigPath, p.Metadata.GUID + ".cfg")));
      }
      keyStr = sb.ToString();
      string key; using (var sha = SHA1.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(keyStr))).Replace("-", "").Substring(0, 16);
      string root = Root.Value.Trim(); if (root.Length == 0) root = Path.Combine(Application.persistentDataPath, "MinimapCache");
      string prefix = San(ZNet.World.m_name) + "_";
      var dir = Path.Combine(root, prefix + key) + Path.DirectorySeparatorChar;
      Directory.CreateDirectory(dir);
      string why = MissReason(dir);
      if (why != null && Migrate.Value) {
        try {
          var old = Path.Combine(Paths.CachePath, "MinimapCache");
          if (Directory.Exists(old)) foreach (var od in Directory.GetDirectories(old, prefix + "*").OrderByDescending(Directory.GetLastWriteTimeUtc)) {
            var ods = od + Path.DirectorySeparatorChar; if (MissReason(ods) != null) continue;
            foreach (var f in Files) File.Copy(ods + f, dir + f, true);
            L.LogMessage("migrated 1.1.0 cache " + od + " -> " + dir + " (same world+seed; worldgen cfg drift since then would show as wrong terrain colours: delete the dir to force a rebuild)"); why = MissReason(dir); break;
          }
        } catch (Exception e) { L.LogWarning("migration skipped: " + e.Message); }
      }
      t.Field("m_saveDirectory").SetValue(dir);
      t.Field("m_cachedMinimapMaskTexturePath").SetValue(dir + "cacheMinimapMask");
      t.Field("m_cachedMinimapBiomeTexturePath").SetValue(dir + "cacheMinimapBiome");
      t.Field("m_cachedMinimapHeightTexturePath").SetValue(dir + "cacheMinimapHeight");
      t.Field("m_cachedMinimapMetaPath").SetValue(dir + "cacheMinimapMeta"); usingOurDir = true; ourDir = dir;
      L.LogMessage("Minimap cache " + (why == null ? "HIT expected" : "MISS expected: " + why) + " dir=" + dir);
      if (Verbose.Value) {
        L.LogInfo("Minimap cache key=" + keyStr);
        try { var others = Directory.GetDirectories(root, prefix + "*").Where(d => !d.TrimEnd('\\', '/').Equals(dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();
          if (others.Count > 0) L.LogInfo("other cache dirs for this world (key drift if they are recent): " + string.Join(" ; ", others.Select(d => Path.GetFileName(d) + " " + Directory.GetLastWriteTime(d).ToString("yyyy-MM-dd HH:mm") + (File.Exists(Path.Combine(d, "key.txt")) ? " key=" + File.ReadAllText(Path.Combine(d, "key.txt")) : "")))); } catch { }
      }
    } catch (Exception e) { L.LogWarning("MinimapCache disabled this session: " + e.Message); }
  }
  static string San(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }
}
}
