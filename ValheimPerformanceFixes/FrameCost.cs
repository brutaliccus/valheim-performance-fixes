using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ValheimPerformanceFixes
{
    // 2.1.1: shared per-frame ledger. Every module adds the main-thread milliseconds of the world work it ran in a frame
    // under a short owner name (dungeon, location, zone, object, heightmap, DNS, GC, save, ...). The hitch logger in the
    // spawn budget module reads the previous frame's entries when that frame turned out long.
    // Also carries "a world build is running" for modules that should step aside meanwhile (DropNSpawn pacing).
    internal static class FrameCost
    {
        sealed class Slot
        {
            public int frame = -1;
            public readonly Dictionary<string, double> ms = new Dictionary<string, double>();
            public readonly Dictionary<string, string> top = new Dictionary<string, string>();
            public readonly Dictionary<string, double> topMs = new Dictionary<string, double>();
            public readonly Dictionary<string, int> n = new Dictionary<string, int>();
        }
        static readonly Slot[] slots = { new Slot(), new Slot(), new Slot(), new Slot() };
        static Slot Get(int f)
        {
            var s = slots[f & 3];
            if (s.frame != f) { s.frame = f; s.ms.Clear(); s.top.Clear(); s.topMs.Clear(); s.n.Clear(); }
            return s;
        }
        // running sum of everything added for the current frame; lets an outer timer subtract nested work already added
        internal static double Total;
        // adds only the part of ms that nested timers have not already added since 'totalAtStart' (= Total when the outer timer started)
        internal static double AddOwn(string owner, double ms, double totalAtStart, string what = null)
        {
            double own = ms - (Total - totalAtStart);
            if (own > 0) Add(owner, own, what);
            return own;
        }
        // frameOffset -1 = charge the previous frame (work that is only noticed one frame later, e.g. a natural GC)
        internal static void Add(string owner, double ms, string what = null, int frameOffset = 0)
        {
            if (owner == null || ms <= 0) return;
            try
            {
                var s = Get(Time.frameCount + frameOffset);
                if (frameOffset == 0) Total += ms;
                s.ms.TryGetValue(owner, out var cur); s.ms[owner] = cur + ms;
                s.n.TryGetValue(owner, out var c); s.n[owner] = c + 1;
                if (what != null && (!s.topMs.TryGetValue(owner, out var tm) || ms > tm)) { s.topMs[owner] = ms; s.top[owner] = what; }
            }
            catch { }
        }
        // "dungeon 22.1 ms (DG_ForestCrypt x3), object 8.0 ms (...)"; sum = measured total
        internal static string Describe(int f, double minMs, out double sum, out string first)
        {
            sum = 0; first = null;
            var s = slots[f & 3];
            if (s.frame != f || s.ms.Count == 0) return "";
            var l = new List<KeyValuePair<string, double>>(s.ms);
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            foreach (var kv in l)
            {
                sum += kv.Value;
                if (kv.Value < minMs) continue;
                if (first == null) first = kv.Key;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append(' ').Append(kv.Value.ToString("0.0")).Append(" ms");
                s.n.TryGetValue(kv.Key, out var c);
                if (s.top.TryGetValue(kv.Key, out var w)) sb.Append(" (").Append(w).Append(c > 1 ? " +" + (c - 1) + " more" : "").Append(')');
                else if (c > 1) sb.Append(" (x").Append(c).Append(')');
            }
            return sb.ToString();
        }

        // ---- 2.3.1: frame tags ----
        // marks a short run of frames as belonging to an owner whose cost is not measured on the main thread (e.g. the frames
        // right after an inventory opens: layout / canvas rebuilds happen inside Unity). The hitch logger uses it when little was measured.
        static int tagFrom = -100, tagTo = -100; static string tagOwner, tagWhat;
        internal static void Tag(string owner, string what, int nextFrames)
        {
            try { int f = Time.frameCount; tagFrom = f; tagTo = f + Math.Max(0, nextFrames); tagOwner = owner; tagWhat = what; } catch { }
        }
        internal static string TagAt(int f, out string what)
        {
            what = null; if (tagOwner == null || f < tagFrom || f > tagTo) return null;
            what = tagWhat; return tagOwner;
        }

        // ---- world build activity ----
        static int buildFrame = -100; static string buildWhy = "-";
        internal static void MarkWorldBuild(string why) { try { buildFrame = Time.frameCount; buildWhy = why; } catch { } }
        // true while a dungeon / location / zone build ran this frame or the previous one
        internal static bool WorldBuildBusy { get { try { return Time.frameCount - buildFrame <= 1; } catch { return false; } } }
        internal static string WorldBuildWhy => buildWhy;
    }
}
