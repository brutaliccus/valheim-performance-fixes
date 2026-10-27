using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RenderTrim
{
    // 1.7.5: BalrondDualMastery (AnimationSpeedLease) and Goo's Combat Overhaul both scale Animator.speed during
    // attacks and only restore it if it still equals the value they set. When they interleave, the local player's
    // animator stays at ~2x after the attack (fast stutter-step running). Reset it to 1 shortly after the attack ends.
    internal static class AnimFix
    {
        const string DualMasteryGuid = "balrond.astafaraios.BalrondDualMastery";
        const string GcoGuid = "goo.valheim.gooscombatoverhaul";
        static ConfigEntry<bool> fixOn, logOn, leaseOn; static ConfigEntry<float> grace;
        static bool wasAttacking; static float endedAt = -1f; static int resets;
        static AccessTools.FieldRef<Humanoid, Attack> curAttack; static AccessTools.FieldRef<Character, Animator> animRef;
        static bool leaseLooked; static Type leaseType; static FieldInfo fActive, fApplied, fBase, fEventSpeed, fEventClip;

        internal static void Init(ConfigFile cfg, Harmony h)
        {
            fixOn = cfg.Bind("11. Animation Fixes", "FixStuckAnimatorSpeed", true, "Reset the local player's Animator.speed to 1 after an attack ends if another mod (BalrondDualMastery, Goo's Combat Overhaul) left it changed. Fixes the fast stutter-step run after dual-wield attacks. Never touches speed 0 freezes or hitstop, never during attacks.");
            grace = cfg.Bind("11. Animation Fixes", "GraceSec", 0.15f, new ConfigDescription("Seconds to wait after the attack ends before checking/resetting (lets the other mods restore first).", new AcceptableValueRange<float>(0f, 2f)));
            logOn = cfg.Bind("11. Animation Fixes", "LogResets", true, "Log before/after speed of every reset to BepInEx\\RenderTrim.log.");
            leaseOn = cfg.Bind("11. Animation Fixes", "ResetDualMasteryLease", true, "Also reset BalrondDualMastery's internal AnimationSpeedLease state (via reflection) so its next attack starts from speed 1.");
            try { animRef = AccessTools.FieldRefAccess<Character, Animator>("m_animator"); } catch (Exception e) { Plugin.I.L("[Animation Fixes] m_animator lookup failed: " + e.Message); }
            try { curAttack = AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack"); } catch (Exception e) { Plugin.I.L("[Animation Fixes] m_currentAttack lookup failed: " + e.Message); }
            try
            {
                h.Patch(AccessTools.DeclaredMethod(typeof(Player), "Update"), postfix: new HarmonyMethod(typeof(AnimFix), nameof(Post)) { priority = Priority.Last - 1, after = new[] { DualMasteryGuid, GcoGuid } });
                Plugin.I.L("[Animation Fixes] stuck animator speed patch ok (enabled=" + fixOn.Value + ")");
            }
            catch (Exception e) { Plugin.I.L("[Animation Fixes] patch FAILED: " + e.Message); }
        }

        static void Post(Player __instance)
        {
            try
            {
                if (!fixOn.Value || __instance == null || __instance != Player.m_localPlayer) return;
                Animator a = animRef != null ? animRef(__instance) : null; if (a == null) return;
                bool attacking = __instance.InAttack() || (curAttack != null && curAttack(__instance) != null);
                if (attacking) { wasAttacking = true; endedAt = -1f; return; }
                if (!wasAttacking) return;
                if (endedAt < 0f) endedAt = Time.time;
                if (Time.time - endedAt < grace.Value) return;
                wasAttacking = false; endedAt = -1f;
                float before = a.speed;
                if (before <= 0.001f) return; // freeze / hitstop: leave alone
                if (Mathf.Abs(before - 1f) <= 0.01f) return;
                a.speed = 1f; resets++;
                string lease = leaseOn.Value ? ResetLease(__instance) : "skipped";
                if (logOn.Value) Plugin.I.L("[Animation Fixes] reset #" + resets + " animator speed " + before.ToString("0.###") + " -> " + a.speed.ToString("0.###") + " (DualMastery lease: " + lease + ")");
            }
            catch (Exception e) { if (logOn != null && logOn.Value) Plugin.I.L("[Animation Fixes] error: " + e.Message); }
        }

        static string ResetLease(Player p)
        {
            try
            {
                if (!leaseLooked)
                {
                    leaseLooked = true;
                    leaseType = AccessTools.TypeByName("Balrond.DualMastery.Assets.AnimationSpeedLease");
                    if (leaseType != null)
                    {
                        const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                        fActive = leaseType.GetField("_active", bf); fApplied = leaseType.GetField("_appliedSpeed", bf); fBase = leaseType.GetField("_baseSpeed", bf);
                        fEventSpeed = leaseType.GetField("_eventSpeed", bf); fEventClip = leaseType.GetField("_eventClipId", bf);
                    }
                    Plugin.I.L("[Animation Fixes] DualMastery lease type " + (leaseType != null ? "found (fields ok=" + (fActive != null && fApplied != null && fBase != null) + ")" : "not found"));
                }
                if (leaseType == null || fActive == null || fApplied == null || fBase == null) return "n/a";
                Component c = p.GetComponent(leaseType); if (c == null) return "none on player";
                fActive.SetValue(c, false); fApplied.SetValue(c, 1f); fBase.SetValue(c, 1f);
                if (fEventSpeed != null) fEventSpeed.SetValue(c, 1f); if (fEventClip != null) fEventClip.SetValue(c, 0);
                return "reset";
            }
            catch (Exception e) { leaseType = null; return "failed: " + e.Message; }
        }
    }
}
