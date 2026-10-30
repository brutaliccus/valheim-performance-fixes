using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimPerformanceFixes
{
    // 2.1.0: shared safety rules for every patch added or touched in this version.
    // - Rivals(): which OTHER mods change the same method in a way that could clash with ours
    //   (transpilers, and prefixes that return bool and so may skip the original). Our own Harmony ids are ignored.
    // - ReplaceCalls(): the only way our transpilers edit IL. Works on copies of the instructions, checks that the
    //   expected number of matches was found, and returns the untouched original list otherwise.
    // - CheckLabels(): every branch target of the result is marked and exception blocks still balance.
    internal static class PatchGuard
    {
        internal const string OwnPrefix = "vpf.ValheimPerformanceFixes";
        static bool Own(string owner) => owner != null && owner.StartsWith(OwnPrefix, StringComparison.Ordinal);

        internal static List<string> Rivals(MethodBase m, bool skippingPrefixes = true, bool transpilers = true)
        {
            var r = new List<string>(); if (m == null) return r;
            HarmonyLib.Patches info; try { info = Harmony.GetPatchInfo(m); } catch { return r; }
            if (info == null) return r;
            if (transpilers) foreach (var p in info.Transpilers) Add(r, p.owner, "transpiler");
            if (skippingPrefixes) foreach (var p in info.Prefixes) if (p.PatchMethod != null && p.PatchMethod.ReturnType == typeof(bool)) Add(r, p.owner, "prefix");
            return r;
        }
        // every other owner, any patch kind (for the conflict log only)
        internal static List<string> Others(MethodBase m)
        {
            var r = new List<string>(); if (m == null) return r;
            HarmonyLib.Patches info; try { info = Harmony.GetPatchInfo(m); } catch { return r; }
            if (info == null) return r;
            foreach (var p in info.Prefixes) Add(r, p.owner, "prefix");
            foreach (var p in info.Postfixes) Add(r, p.owner, "postfix");
            foreach (var p in info.Transpilers) Add(r, p.owner, "transpiler");
            foreach (var p in info.Finalizers) Add(r, p.owner, "finalizer");
            return r;
        }
        static void Add(List<string> r, string owner, string kind) { if (Own(owner)) return; var s = owner + " (" + kind + ")"; if (!r.Contains(s)) r.Add(s); }
        internal static string Name(MethodBase m) => m == null ? "?" : (m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name;

        internal static List<CodeInstruction> ReplaceCalls(IEnumerable<CodeInstruction> original, Func<CodeInstruction, bool> match, MethodInfo replacement, int expected, string what, Action<string> log, out bool ok)
        {
            var orig = new List<CodeInstruction>(original);
            // 2.1.1 fix: new CodeInstruction(c) keeps labels and exception blocks (Clone() drops both -> "Label #n is not marked")
            var copy = new List<CodeInstruction>(orig.Count); foreach (var c in orig) copy.Add(new CodeInstruction(c));
            int hits = 0;
            for (int i = 0; i < copy.Count; i++)
            {
                if (!match(copy[i])) continue;
                var n = new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(copy[i]).MoveBlocksFrom(copy[i]);
                copy[i] = n; hits++;
            }
            ok = hits == expected;
            if (!ok) { log?.Invoke(what + ": expected " + expected + " match(es) but found " + hits + " - left unchanged (another mod or a game update changed this code)"); return orig; }
            var bad = CheckLabels(copy);
            if (bad != null) { ok = false; log?.Invoke(what + ": result failed the label check (" + bad + ") - left unchanged"); return orig; }
            return copy;
        }

        // 2.1.1: structural check of a transpiler result: every Label used as an operand is marked on some instruction,
        // and the exception-block begin/end markers balance. Returns null when fine, else a reason.
        static readonly System.Reflection.FieldInfo fLabels = typeof(CodeInstruction).GetField("labels");
        internal static string CheckLabels(List<CodeInstruction> list)
        {
            if (fLabels == null) return null;
            var marked = new HashSet<object>(); int depth = 0; // Label is handled as object: the netstandard reference set cannot name HarmonyX's Label type
            foreach (var c in list) { if (fLabels?.GetValue(c) is System.Collections.IEnumerable ll) foreach (object l in ll) marked.Add(l);
                foreach (var b in c.blocks) { if (b.blockType == ExceptionBlockType.BeginExceptionBlock) depth++; else if (b.blockType == ExceptionBlockType.EndExceptionBlock) { depth--; if (depth < 0) return "exception block closed before it was opened"; } } }
            if (depth != 0) return "unbalanced exception blocks (" + depth + " left open)";
            foreach (var c in list) {
                var op = c.operand; if (op == null) continue; var ot = op.GetType();
                if (ot.FullName == "System.Reflection.Emit.Label" && !marked.Contains(op)) return "branch target not marked at " + c.opcode;
                if (ot.IsArray && ot.GetElementType().FullName == "System.Reflection.Emit.Label") foreach (object l2 in (System.Array)op) if (!marked.Contains(l2)) return "switch target not marked";
            }
            return null;
        }
    }
}
