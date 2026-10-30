using System;
using UnityEngine;

namespace ValheimPerformanceFixes
{
    // 2.3.2: shared state between the loading-screen hold (RenderTrim) and the DropNSpawn module.
    // The DropNSpawn module says whether that mod still has start-up config work queued (Busy); the hold keeps the screen
    // black a little longer while it does (capped), and while the hold is active (Holding) that work may run unthrottled.
    internal static class SyncWatch
    {
        internal static Func<bool> Busy;
        internal static Func<string> What;
        static int holdFrame = -10;
        internal static void MarkHolding() { try { holdFrame = Time.frameCount; } catch { } }
        internal static bool Holding { get { try { return Time.frameCount - holdFrame <= 1; } catch { return false; } } }
        internal static bool IsBusy() { var b = Busy; if (b == null) return false; try { return b(); } catch { return false; } }
        internal static string BusyWhat() { var w = What; if (w == null) return "-"; try { return w(); } catch { return "?"; } }
        // statistics (reset with every STATS line)
        internal static int passes, slowPasses, behindFrames; internal static double passMs, maxMs, behindMs, holdExtraSec; internal static string maxWhat = "-";
        internal static void Pass(string what, double ms)
        {
            passes++; passMs += ms; if (ms >= 8.0) slowPasses++;
            if (ms > maxMs) { maxMs = ms; maxWhat = what; }
        }
        internal static string Stats()
        {
            if (Busy == null && passes == 0) return "dnsSync=n/a";
            return "dnsSync passes=" + passes + " slow=" + slowPasses + " ms=" + passMs.ToString("0") + " max=" + maxMs.ToString("0") + "ms(" + maxWhat + ") ranBehindScreen=" + behindFrames + "f/" + behindMs.ToString("0") + "ms holdExtra=" + holdExtraSec.ToString("0.0") + "s";
        }
        internal static void Reset() { passes = slowPasses = behindFrames = 0; passMs = maxMs = behindMs = holdExtraSec = 0; maxWhat = "-"; }
    }
}
