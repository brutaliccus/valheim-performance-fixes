using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace ValheimPerformanceFixes
{
    /// <summary>
    /// Base class for a merged module. It used to be its own BaseUnityPlugin; now it is a component on the same
    /// BepInEx manager object, but shares the host plugin's single config file (one entry in Configuration Manager).
    /// </summary>
    public abstract class ModuleBehaviour : MonoBehaviour
    {
        ManualLogSource log;
        public ConfigFile Config => PerformanceFixesPlugin.Instance.Config;
        public PluginInfo Info => PerformanceFixesPlugin.Instance.Info;
        /// <summary>Log source keeps the old plugin name (namespace), so log lines look exactly like before.</summary>
        public ManualLogSource Logger => log ?? (log = BepInEx.Logging.Logger.CreateLogSource(GetType().Namespace));
    }

    /// <summary>
    /// First-run migration: when the merged .cfg does not exist yet, the old standalone .cfg files in BepInEx/config are
    /// found by their content (their section|key names), not by file name, and copied in with the merged section names
    /// BEFORE any module binds its settings. BepInEx then reads the values exactly as if they had been typed into the new
    /// file, so no change events fire. Old files are only read, never changed.
    /// </summary>
    internal static class ConfigMigration
    {
        internal sealed class Source
        {
            public readonly string Module, Prefix; public readonly HashSet<string> Keys;
            public Source(string module, string prefix, string[] keys) { Module = module; Prefix = prefix; Keys = new HashSet<string>(keys, StringComparer.Ordinal); }
        }

        internal static void Run(ConfigFile cfg, ManualLogSource log, IList<Source> sources)
        {
            try
            {
                string self = Path.GetFullPath(cfg.ConfigFilePath);
                var files = new List<KeyValuePair<string, List<string[]>>>();
                var names = Directory.GetFiles(Paths.ConfigPath, "*.cfg"); Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                foreach (var f in names)
                {
                    if (string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase)) continue;
                    try { var e = Parse(f); if (e.Count > 0) files.Add(new KeyValuePair<string, List<string[]>>(f, e)); } catch { }
                }
                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sb = new StringBuilder();
                sb.AppendLine("## Settings copied on first run from the old standalone plugin config files.");
                int used = 0, keys = 0;
                foreach (var src in sources)
                {
                    string best = null; List<string[]> bestE = null; int bestHit = 0;
                    int need = Math.Max(2, src.Keys.Count / 3);
                    foreach (var f in files)
                    {
                        if (claimed.Contains(f.Key)) continue;
                        int hit = 0; foreach (var e in f.Value) if (src.Keys.Contains(e[0] + "|" + e[1])) hit++;
                        // Most of the file must be this module's settings, and enough of the module's settings must be there.
                        if (hit < need || hit * 10 < f.Value.Count * 6) continue;
                        if (hit > bestHit) { best = f.Key; bestE = f.Value; bestHit = hit; }
                    }
                    if (best == null) continue;
                    claimed.Add(best);
                    string last = null;
                    foreach (var e in bestE)
                    {
                        if (e[0] != last) { sb.AppendLine().AppendLine("[" + src.Prefix + e[0] + "]"); last = e[0]; }
                        sb.AppendLine(e[1] + " = " + e[2]);
                    }
                    used++; keys += bestE.Count;
                    log.LogInfo("Config migration: copied " + bestE.Count + " settings from " + Path.GetFileName(best) + " (" + src.Module + ")");
                }
                if (keys == 0) { log.LogInfo("Config migration: no old config files found, using defaults"); return; }
                File.WriteAllText(cfg.ConfigFilePath, sb.ToString());
                cfg.Reload();
                log.LogInfo("Config migration: " + keys + " settings from " + used + " old file(s) copied into " + Path.GetFileName(cfg.ConfigFilePath) + ". The old files were left untouched and are no longer read.");
            }
            catch (Exception e) { log.LogWarning("Config migration failed, defaults are used: " + e.Message); }
        }

        static List<string[]> Parse(string path)
        {
            var list = new List<string[]>(); string section = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var ln = raw.Trim();
                if (ln.Length == 0 || ln[0] == '#') continue;
                if (ln[0] == '[' && ln[ln.Length - 1] == ']') { section = ln.Substring(1, ln.Length - 2).Trim(); continue; }
                int i = ln.IndexOf('=');
                if (i <= 0 || section == null) continue;
                list.Add(new[] { section, ln.Substring(0, i).Trim(), ln.Substring(i + 1).Trim() });
            }
            return list;
        }
    }
}