using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RenderTrim
{
    // 1.7.6: BalrondMonsterMayhem's LootDropLimitService.Prepare (CharacterDrop.GenerateDropList prefix) keeps only
    // 2-3 drop TYPES per kill (+1 per star), picked at random from rows >= 60% chance. That made a 100% Drop That row
    // (BeeWax_bal on GreylingBeeKeeper_bal) drop ~1 kill in 4. We postfix its GetBudget so the budget is Unlimited
    // (MM's own path for bosses/TrollFrost): every row rolls its own chance, protected rows (trophies, one-per-player,
    // signature items) and the 3-star trophy guarantee stay exactly as MM does them. Bosses were already unlimited.
    // Runs on clients AND the dedicated server (the creature's owner rolls the drops; that is normally a client).
    internal static class DropFix
    {
        const string MmGuid = "balrond.astafaraios.BalrondMonsterMayhem";
        const string SvcName = "BalrondMonsterMayhem.LootDropLimitService";
        static ConfigEntry<bool> capOff, logOn; static ConfigEntry<int> extra;
        static Action<string> log; static Harmony h; static bool patched, gaveUp; static int kills;
        static FieldInfo fTotal, fCommon, fBetter, fRare, fUnl, fSrc, fOrig, fApplied;

        internal static void Init(ConfigFile cfg, Harmony harmony, Action<string> logger)
        {
            log = logger; h = harmony;
            capOff = cfg.Bind("12. Drop Fixes", "DisableMonsterMayhemDropCap", true, "Remove BalrondMonsterMayhem's per-kill cap on how many drop TYPES a creature drops (normally 3 at 0 stars, +1 per star, picked at random among rows >= 60%). On = every drop row rolls its own chance (Drop That / vanilla), like bosses already do. Trophies, one-per-player and the 3-star trophy guarantee are unchanged. Live.");
            extra = cfg.Bind("12. Drop Fixes", "ExtraSlotsInsteadOfDisable", 0, new ConfigDescription("0 = remove the cap completely. Above 0 = keep MM's cap but give every non-boss creature this many extra drop types (added to its common slots). Only used when DisableMonsterMayhemDropCap is on. Live.", new AcceptableValueRange<int>(0, 20)));
            logOn = cfg.Bind("12. Drop Fixes", "LogCapBypass", true, "Log one line per kill whose drops pass through MM's limiter: creature, star level, rows kept / rows on the creature.");
            TryPatch("startup");
            if (!patched && !gaveUp)
            {
                try { h.Patch(AccessTools.DeclaredMethod(typeof(ZNetScene), "Awake"), prefix: new HarmonyMethod(typeof(DropFix), nameof(Retry))); }
                catch (Exception e) { log("[Drop Fixes] retry hook FAILED: " + e.Message); }
            }
        }

        static void Retry() { if (!patched && !gaveUp) TryPatch("ZNetScene.Awake"); }

        static void TryPatch(string when)
        {
            try
            {
                Type svc = AccessTools.TypeByName(SvcName);
                if (svc == null)
                {
                    if (when != "startup") { gaveUp = true; log("[Drop Fixes] MonsterMayhem LootDropLimitService not found (mod absent or renamed) - nothing to patch"); }
                    return;
                }
                MethodInfo getBudget = AccessTools.DeclaredMethod(svc, "GetBudget");
                MethodInfo prepare = AccessTools.DeclaredMethod(svc, "Prepare");
                Type budgetT = AccessTools.Inner(svc, "LootBudget"); Type stateT = AccessTools.Inner(svc, "GenerationState");
                if (getBudget == null || budgetT == null || getBudget.ReturnType != budgetT) { gaveUp = true; log("[Drop Fixes] MonsterMayhem GetBudget/LootBudget changed - NOT patched (cap stays)"); return; }
                fTotal = AccessTools.Field(budgetT, "TotalSlots"); fCommon = AccessTools.Field(budgetT, "CommonSlots"); fBetter = AccessTools.Field(budgetT, "BetterSlots");
                fRare = AccessTools.Field(budgetT, "RarePlusCap"); fUnl = AccessTools.Field(budgetT, "Unlimited");
                if (fTotal == null || fCommon == null || fBetter == null || fRare == null || fUnl == null) { gaveUp = true; log("[Drop Fixes] MonsterMayhem LootBudget fields changed - NOT patched (cap stays)"); return; }
                h.Patch(getBudget, postfix: new HarmonyMethod(typeof(DropFix), nameof(BudgetPost)) { priority = Priority.Last });
                patched = true;
                if (prepare != null && stateT != null)
                {
                    fSrc = AccessTools.Field(stateT, "Source"); fOrig = AccessTools.Field(stateT, "OriginalDrops"); fApplied = AccessTools.Field(stateT, "Applied");
                    try { h.Patch(prepare, postfix: new HarmonyMethod(typeof(DropFix), nameof(PreparePost)) { priority = Priority.Last }); } catch (Exception e) { log("[Drop Fixes] per-kill log hook failed (cap bypass still active): " + e.Message); }
                }
                string ver = "?"; try { if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(MmGuid, out var pi)) ver = pi.Metadata.Version.ToString(); } catch { }
                log("[Drop Fixes] MonsterMayhem " + ver + " drop cap patch ok (at " + when + "; DisableMonsterMayhemDropCap=" + capOff.Value + ", ExtraSlotsInsteadOfDisable=" + extra.Value + ")");
            }
            catch (Exception e) { gaveUp = true; log("[Drop Fixes] patch FAILED (cap stays): " + e.Message); }
        }

        // __result is MM's internal struct LootBudget; HarmonyX boxes/unboxes it through 'ref object'.
        static void BudgetPost(ref object __result)
        {
            try
            {
                if (!capOff.Value || __result == null || (bool)fUnl.GetValue(__result)) return;
                object b = __result; int n = extra.Value;
                if (n <= 0)
                {
                    fUnl.SetValue(b, true); fTotal.SetValue(b, int.MaxValue); fCommon.SetValue(b, int.MaxValue); fBetter.SetValue(b, int.MaxValue); fRare.SetValue(b, int.MaxValue);
                }
                else
                {
                    fCommon.SetValue(b, (int)fCommon.GetValue(b) + n); fTotal.SetValue(b, (int)fTotal.GetValue(b) + n);
                }
                __result = b;
            }
            catch (Exception e) { if (logOn.Value) log("[Drop Fixes] budget error: " + e.Message); }
        }

        static void PreparePost(object __result)
        {
            try
            {
                if (!logOn.Value || __result == null || !(bool)fApplied.GetValue(__result)) return;
                var cd = fSrc.GetValue(__result) as CharacterDrop; if (cd == null) return;
                var orig = fOrig.GetValue(__result) as List<CharacterDrop.Drop>;
                Character c = cd.GetComponent<Character>(); kills++;
                string mode = !capOff.Value ? "MM cap ON" : extra.Value > 0 ? "cap +" + extra.Value : "cap bypassed";
                log("[Drop Fixes] kill #" + kills + " " + cd.gameObject.name.Replace("(Clone)", "") + " lvl " + (c != null ? c.GetLevel() : 0) + ": " + (cd.m_drops != null ? cd.m_drops.Count : 0) + "/" + (orig != null ? orig.Count : 0) + " drop rows rolled in (" + mode + ")");
            }
            catch (Exception e) { log("[Drop Fixes] log error: " + e.Message); }
        }
    }
}
