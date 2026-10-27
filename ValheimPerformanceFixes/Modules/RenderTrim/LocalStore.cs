using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace RenderTrim
{
    // 1.7.8: a server modpack sync can overwrite BepInEx/config/*.cfg, which would wipe settings changed on this PC.
    // Every value changed in-game (F1 / Quick Setup slider) for RenderTrim and SpawnBudget is therefore also written to a
    // per-PC file outside BepInEx (%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\RenderTrim.local.txt) and re-applied at start.
    // Merged plugin: both modules share one ConfigFile; the file keeps the old 'plugin|section|key' format (SpawnBudget sections without prefix).
    internal static class LocalStore
    {
        internal static ConfigEntry<bool> useLocal, clearLocal;
        static readonly Dictionary<string, string> vals = new Dictionary<string, string>();
        static string path; static bool applying, sbHooked; static Action<string> log; static ConfigFile rt;

        internal static void Bind(ConfigFile c, Action<string> l)
        {
            rt = c; log = l;
            useLocal = c.Bind("0. Quick Setup", "KeepMySettingsOnThisPC", true, new ConfigDescription("Remember every setting you change in-game on this PC (in AppData\\LocalLow\\IronGate\\Valheim\\RenderTrim.local.txt) so a server update does not reset it. Off = always use the server pack's values.", null, new ConfigurationManagerAttributes { Order = 97 }));
            clearLocal = c.Bind("0. Quick Setup", "ForgetMySettings", false, new ConfigDescription("Tick to delete the remembered settings of this PC (takes effect at next start; the box un-ticks itself).", null, new ConfigurationManagerAttributes { Order = 96 }));
            try { path = Path.Combine(Application.persistentDataPath, "RenderTrim.local.txt"); } catch { path = null; }
            Load();
            clearLocal.SettingChanged += (s, e) => { if (!clearLocal.Value) return; vals.Clear(); Save(); log("Local settings: forgotten (pack values are used from next start)"); clearLocal.Value = false; };
            c.SettingChanged += (s, e) => OnChanged(e.ChangedSetting);
        }
        static void Load()
        {
            vals.Clear(); if (path == null || !File.Exists(path)) return;
            foreach (var ln in File.ReadAllLines(path)) { int i = ln.IndexOf('='); if (i > 0 && !ln.StartsWith("#")) vals[Normalize(ln.Substring(0, i))] = ln.Substring(i + 1); }
        }
        // Files written by the old standalone plugins start each line with the old plugin GUID ("<author>.rendertrim|...").
        // Only the part after the last dot matters, so those lines keep working without naming the old GUIDs here.
        static string Normalize(string k)
        {
            int b = k.IndexOf('|'); if (b <= 0) return k;
            string pl = k.Substring(0, b), tail = k.Substring(b);
            if (pl.EndsWith(".rendertrim", StringComparison.OrdinalIgnoreCase)) return "rendertrim" + tail;
            if (pl.EndsWith(".spawnbudget", StringComparison.OrdinalIgnoreCase)) return "spawnbudget" + tail;
            return k;
        }
        static void Save()
        {
            if (path == null) return;
            try { var l = new List<string> { "# RenderTrim per-PC settings (changed in-game). Format: plugin|section|key=value. Delete this file to use the server pack values." }; foreach (var kv in vals) l.Add(kv.Key + "=" + kv.Value); File.WriteAllLines(path, l.ToArray()); }
            catch (Exception ex) { log("Local settings save failed: " + ex.Message); }
        }
        static void OnChanged(ConfigEntryBase e)
        {
            if (e == null) return; var sec = e.Definition.Section;
            if (sec.StartsWith(global::ValheimPerformanceFixes.Sections.SB, StringComparison.Ordinal)) { if (sbHooked) Record("spawnbudget", e, sec.Substring(global::ValheimPerformanceFixes.Sections.SB.Length)); }
            else if (global::ValheimPerformanceFixes.Sections.IsRenderTrim(sec)) Record("rendertrim", e, sec);
        }
        static void Record(string guid, ConfigEntryBase e, string section)
        {
            if (applying || e == null || !useLocal.Value) return;
            var d = e.Definition; if (section == "0. Quick Setup" && (d.Key == "ForgetMySettings" || d.Key == "KeepMySettingsOnThisPC")) return;
            vals[guid + "|" + section + "|" + d.Key] = e.GetSerializedValue(); Save();
        }
        static int ApplyTo(string guid, ConfigFile c, string sectionPrefix = "")
        {
            int n = 0; applying = true;
            try
            {
                foreach (var kv in vals)
                {
                    var p = kv.Key.Split('|'); if (p.Length != 3 || p[0] != guid) continue;
                    var def = new ConfigDefinition(sectionPrefix + p[1], p[2]); if (!c.ContainsKey(def)) continue;
                    var e = c[def]; if (e.GetSerializedValue() == kv.Value) continue;
                    try { e.SetSerializedValue(kv.Value); n++; } catch { }
                }
            }
            finally { applying = false; }
            return n;
        }
        // called once on the first Update (all plugins loaded)
        internal static void Start()
        {
            try
            {
                if (useLocal.Value)
                {
                    int a = ApplyTo("rendertrim", rt); int b = 0;
                    if (global::ValheimPerformanceFixes.PerformanceFixesPlugin.SpawnBudgetLoaded) b = ApplyTo("spawnbudget", rt, global::ValheimPerformanceFixes.Sections.SB);
                    log("Local settings: " + vals.Count + " remembered, re-applied " + a + " RenderTrim + " + b + " SpawnBudget values (" + path + ")");
                }
                if (!sbHooked && global::ValheimPerformanceFixes.PerformanceFixesPlugin.SpawnBudgetLoaded) sbHooked = true;
            }
            catch (Exception ex) { log("Local settings start failed: " + ex.Message); }
        }
    }
}