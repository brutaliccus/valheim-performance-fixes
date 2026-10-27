using System;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;

namespace ValheimPerformanceFixes
{
    /// <summary>Section names in the merged config. RenderTrim keeps its original numbered sections.</summary>
    internal static class Sections
    {
        internal const string SB = "Spawn Budget - ";
        internal const string MC = "Minimap Cache - ";
        internal const string DNS = "Optional - DropNSpawn Cache - ";
        internal const string RH = "Optional - Riverheim Cache - ";
        static readonly string[] Others = { SB, MC, DNS, RH };
        internal static bool IsRenderTrim(string section)
        {
            foreach (var p in Others) if (section.StartsWith(p, StringComparison.Ordinal)) return false;
            return true;
        }
    }

    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("com.Skarif.ValheimPerformanceOverhaul_WATER", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(DropNSpawnGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(RiverheimGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class PerformanceFixesPlugin : BaseUnityPlugin
    {
        public const string GUID = "vpf.ValheimPerformanceFixes";
        public const string NAME = "Valheim Performance Fixes";
        public const string VERSION = "2.0.0";
        const string DropNSpawnGuid = "sighsorry.DropNSpawn", RiverheimGuid = "dev.gurebu.riverheim.stable";

        internal static PerformanceFixesPlugin Instance;
        internal static ManualLogSource Log;
        internal static bool SpawnBudgetLoaded;

        static readonly ConfigMigration.Source[] OldConfigs =
        {
            new ConfigMigration.Source("RenderTrim", "", Signatures.RenderTrim),
            new ConfigMigration.Source("SpawnBudget", Sections.SB, Signatures.SpawnBudget),
            new ConfigMigration.Source("MinimapCache", Sections.MC, Signatures.MinimapCache),
            new ConfigMigration.Source("DropNSpawnCache", Sections.DNS, Signatures.DropNSpawnCache),
            new ConfigMigration.Source("RiverheimCache", Sections.RH, Signatures.RiverheimCache),
        };

        void Awake()
        {
            Instance = this; Log = Logger;
            if (!System.IO.File.Exists(Config.ConfigFilePath)) ConfigMigration.Run(Config, Log, OldConfigs);

            // Same order the standalone plugins used to load in.
            if (Chainloader.PluginInfos.ContainsKey(DropNSpawnGuid)) AddDropNSpawnCache();
            else Log.LogInfo("Optional module DropNSpawn Cache is off: DropNSpawn (" + DropNSpawnGuid + ") is not loaded.");
            Add<MinimapCache.Plugin>("Minimap Cache + Save Hitch Fix", "MinimapCache");
            Add<RenderTrim.Plugin>("RenderTrim", "RenderTrim");
            if (Chainloader.PluginInfos.ContainsKey(RiverheimGuid)) AddRiverheimCache();
            else Log.LogInfo("Optional module Riverheim Cache is off: Riverheim (" + RiverheimGuid + ") is not loaded.");
            SpawnBudgetLoaded = Add<SpawnBudget.Plugin>("SpawnBudget", "SpawnBudget");
            Config.Save();
        }

        bool Add<T>(string module, string oldName) where T : Component
        {
            if (StandaloneLoaded(oldName))
            { Log.LogWarning(module + " module is off because the old standalone " + oldName + " plugin is still installed. Remove the old DLL."); return false; }
            try { gameObject.AddComponent<T>(); return true; }
            catch (Exception e) { Log.LogError(module + " module failed to start: " + e); return false; }
        }
        // An old standalone DLL that is still installed is found by its plugin name. Its GUID sorts before this one, so it has
        // already loaded when this runs; the matching module then stays off so nothing is patched twice.
        static bool StandaloneLoaded(string oldName)
        {
            if (oldName == null) return false;
            foreach (var pi in Chainloader.PluginInfos.Values)
                if (pi.Metadata.Name == oldName && pi.Metadata.GUID != GUID) return true;
            return false;
        }
        // Kept in separate non-inlined methods so the optional modules' types (and Riverheim.dll) are only touched when the target mod is loaded.
        [MethodImpl(MethodImplOptions.NoInlining)]
        void AddDropNSpawnCache() { Add<DropNSpawnCache.Plugin>("DropNSpawn Cache", "DropNSpawnCache"); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AddRiverheimCache() { Add<RiverheimCache.Plugin>("Riverheim Cache", "RiverheimCache"); }
    }
}