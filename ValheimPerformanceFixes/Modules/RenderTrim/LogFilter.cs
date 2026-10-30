using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace RenderTrim
{
    // 2.3.1: the statistics page (TextsDialog.AddStats) writes its whole text to the log every time it is built.
    // The one log call inside that method is pointed at a small forwarder that drops the text while the F1 toggle is on.
    // (Patching the logger itself is not reliable: it is tiny and can be inlined into its callers.)
    internal static class LogFilter
    {
        static MethodInfo logM;
        internal static int dropped;

        internal static void Init(Harmony h, Action<string> log)
        {
            try
            {
                logM = AccessTools.Method(typeof(ZLog), "Log", new[] { typeof(object) });
                var target = AccessTools.DeclaredMethod(typeof(TextsDialog), "AddStats");
                if (logM == null || target == null) { log("stats log filter: method not found (logger=" + (logM != null) + " AddStats=" + (target != null) + "), off"); return; }
                h.Patch(target, transpiler: new HarmonyMethod(typeof(LogFilter), nameof(Transpile)));
                log("stats log filter: patched, " + replaced + " log call(s) redirected" + (replaced == 1 ? "" : " (expected 1)") + "; hide=" + (Plugin.statsLogOff != null && Plugin.statsLogOff.Value));
            }
            catch (Exception e) { log("stats log filter patch FAILED: " + e.Message); }
        }

        internal static int replaced; internal static bool statsOk;
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> code)
        {
            var fwd = AccessTools.Method(typeof(LogFilter), nameof(StatsLog));
            var m = logM ?? AccessTools.Method(typeof(ZLog), "Log", new[] { typeof(object) });
            replaced = 0;
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == m)
                { ci.opcode = OpCodes.Call; ci.operand = fwd; replaced++; }
                yield return ci;
            }
            statsOk = replaced == 1;
        }

        public static void StatsLog(object o)
        {
            var e = Plugin.statsLogOff;
            if (e != null && e.Value) { dropped++; return; }
            ZLog.Log(o);
        }
    }

    // 2.3.1: U+25C8 (a diamond with a dot) is used by some item texts but no game font has it, so every draw searches all
    // fallback fonts and logs a warning. 2.3.2: four layers, each with its own F1 toggle, until no font is left without it:
    //  1. a font whose character table already has a similar character gets an entry pointing at that glyph (2.3.1),
    //  2. a dynamic font (glyphs made at run time from its font file) is asked to add the real character, or else a similar one,
    //  3. fonts that still lack it get a fallback font made from a system symbol font that has it,
    //  4. as a last resort the character is swapped for a similar one in texts whose font still cannot show it.
    // Warnings about the character are counted so the result can be checked (target: 0). Nothing is written to disk.
    internal static class GlyphFix
    {
        const uint Missing = 0x25C8; const char MissingC = '\u25C8';
        static readonly uint[] Similar = { 0x25C6, 0x2666, 0x25CA, 0x2022, 0x00B7, '*' };
        static readonly HashSet<int> done = new HashSet<int>();
        internal static bool useDynamic = true, useFallback = true, useReplace = true;
        internal static int warnings, warningsAtRun, missingNow = -1, replaced; static int warnAtLastRun; static float rerunAt = -1f;
        static TMP_FontAsset fallback; static bool fallbackTried; static string fallbackName = "-";
        static readonly Dictionary<int, char> subFor = new Dictionary<int, char>();

        internal static void Init(Harmony h, Action<string> log)
        {
            try { Application.logMessageReceived += OnLog; } catch (Exception e) { log("glyph warning counter FAILED: " + e.Message); }
            try
            {
                var pre = new HarmonyMethod(typeof(GlyphFix), nameof(TextPre));
                var setter = AccessTools.PropertySetter(typeof(TMP_Text), "text");
                if (setter != null) h.Patch(setter, prefix: pre);
                var st = AccessTools.Method(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(bool) });
                if (st != null) h.Patch(st, prefix: new HarmonyMethod(typeof(GlyphFix), nameof(SetTextPre)));
                log("glyph fix: text swap patch on (text setter " + (setter != null) + ", SetText " + (st != null) + ")");
            }
            catch (Exception e) { log("glyph fix text swap patch FAILED: " + e.Message); }
        }
        static void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Warning || msg == null || msg.IndexOf("25C8", StringComparison.Ordinal) < 0) return;
            warnings++;
            if (rerunAt < 0f) { try { rerunAt = Time.unscaledTime + 5f; } catch { } }
        }
        // a font loaded later (another mod, a new UI) triggered a warning: run the fix again (at most every 5 s)
        internal static void Tick(Action<string> log)
        {
            if (rerunAt < 0f || Time.unscaledTime < rerunAt) return;
            rerunAt = -1f; log("glyph fix re-run after " + (warnings - warnAtLastRun) + " new warning(s): " + Run());
        }
        static void TextPre(TMP_Text __instance, ref string value) { value = Swap(__instance, value); }
        static void SetTextPre(TMP_Text __instance, ref string sourceText) { sourceText = Swap(__instance, sourceText); }
        static string Swap(TMP_Text t, string v)
        {
            if (!useReplace || v == null || missingNow == 0 || v.IndexOf(MissingC) < 0) return v;
            try
            {
                var fa = t != null ? t.font : null; if (fa == null) return v;
                int id = fa.GetInstanceID();
                if (!subFor.TryGetValue(id, out char c))
                {
                    c = '\0';
                    if (!fa.HasCharacter(MissingC, true, false))
                    {
                        c = '*';
                        foreach (var u in Similar) if (fa.HasCharacter((char)u, true, false)) { c = (char)u; break; }
                    }
                    subFor[id] = c;
                }
                if (c == '\0') return v;
                replaced++; return v.Replace(MissingC, c);
            }
            catch { return v; }
        }
        static bool Dynamic(TMP_FontAsset fa) { try { return fa.atlasPopulationMode != AtlasPopulationMode.Static; } catch { return false; } }
        static bool TryAdd(TMP_FontAsset fa, uint u) { try { return fa.TryAddCharacters(new[] { u }, out uint[] miss, false) && fa.characterLookupTable != null && fa.characterLookupTable.ContainsKey(u); } catch { return false; } }
        static bool Has(TMP_FontAsset fa) { try { return fa.HasCharacter(MissingC, true, false); } catch { return false; } }
        // 2.3.3: the fallback search remembers which system font worked (cache line "fallback="), so later launches go straight to it
        static readonly string[][] fbFamilies = { new[] { "Segoe UI Symbol", "Regular" }, new[] { "Segoe UI Symbol", "Normal" }, new[] { "DejaVu Sans", "Book" }, new[] { "Arial Unicode MS", "Regular" } };
        static readonly string[] fbFiles = { @"C:\Windows\Fonts\seguisym.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" };
        static TMP_FontAsset FallbackTry(int i)
        {
            try
            {
                TMP_FontAsset fa; string nm;
                if (i < fbFamilies.Length) { fa = TMP_FontAsset.CreateFontAsset(fbFamilies[i][0], fbFamilies[i][1], 90); nm = fbFamilies[i][0]; }
                else
                {
                    string p = fbFiles[i - fbFamilies.Length]; if (!System.IO.File.Exists(p)) return null;
                    fa = TMP_FontAsset.CreateFontAsset(p, 0, 90, 9, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 512, 512); nm = System.IO.Path.GetFileName(p);
                }
                if (fa != null && TryAdd(fa, Missing)) { fa.name = "vpf glyph fallback (" + nm + ")"; fallbackName = nm; UnityEngine.Object.DontDestroyOnLoad(fa); return fa; }
            }
            catch { }
            return null;
        }
        static TMP_FontAsset Fallback()
        {
            if (fallbackTried) return fallback; fallbackTried = true;
            int n = fbFamilies.Length + fbFiles.Length;
            if (cacheFallback == -1) return null; // this PC has none of the fallback fonts (cached)
            if (cacheFallback >= 0 && cacheFallback < n) { fallback = FallbackTry(cacheFallback); if (fallback != null) return fallback; }
            for (int i = 0; i < n; i++) { if (i == cacheFallback) continue; fallback = FallbackTry(i); if (fallback != null) { SetFallbackCache(i); return fallback; } }
            SetFallbackCache(-1); return null;
        }

        // ---- 2.3.3: per-PC glyph cache (persistent data folder, HitchFix.glyphcache.txt): per font what worked last time, so later
        // launches skip the slow font-file probes that are known to fail. Codes: T = font has it, R = real character added from the font
        // file, N = font file lacks it (fallback font used), S<hex> = similar character used, X = nothing worked.
        // 2.3.4: F = covered through another font it falls back to (2.3.3 never noted those, so they looked unknown on every launch).
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);
        static bool cacheLoaded, cacheDirty; static int cacheFallback = -2; static string cacheSet = "";
        static int lastHits, lastFonts, lastProbes; static double lastMs = -1;
        static string CachePath() { try { return System.IO.Path.Combine(Application.persistentDataPath, "HitchFix.glyphcache.txt"); } catch { return null; } }
        static void LoadCache()
        {
            if (cacheLoaded) return; cacheLoaded = true;
            try
            {
                var p = CachePath(); if (p == null || !System.IO.File.Exists(p)) return;
                foreach (var line in System.IO.File.ReadAllLines(p))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("fallback=", StringComparison.Ordinal)) { int.TryParse(line.Substring(9), out cacheFallback); continue; }
                    if (line.StartsWith("set=", StringComparison.Ordinal)) { cacheSet = line.Substring(4); continue; }
                    int tab = line.LastIndexOf('\t'); if (tab > 0) cache[line.Substring(0, tab)] = line.Substring(tab + 1);
                }
            }
            catch { }
        }
        static void SaveCache(string set)
        {
            if (!cacheDirty && set == cacheSet) return;
            try
            {
                var p = CachePath(); if (p == null) return;
                var sb = new System.Text.StringBuilder();
                sb.Append("# HitchFix diamond glyph cache (safe to delete; it is rebuilt on the next launch)\n");
                sb.Append("set=").Append(set).Append('\n').Append("fallback=").Append(cacheFallback).Append('\n');
                foreach (var kv in cache) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                System.IO.File.WriteAllText(p, sb.ToString()); cacheDirty = false; cacheSet = set;
            }
            catch { }
        }
        static void SetFallbackCache(int i) { if (cacheFallback != i) { cacheFallback = i; cacheDirty = true; } }
        static void Note(string key, string code) { if (!cache.TryGetValue(key, out var c) || c != code) { cache[key] = code; cacheDirty = true; } }
        static string Key(TMP_FontAsset fa)
        {
            string src = "-"; try { if (fa.sourceFontFile != null) src = fa.sourceFontFile.name; } catch { }
            return (fa.name ?? "?").Replace('\t', ' ') + "|" + src.Replace('\t', ' ') + "|" + (Dynamic(fa) ? "dyn" : "static");
        }
        static int SrcId(TMP_FontAsset fa) { try { if (fa.sourceFontFile != null) return fa.sourceFontFile.GetInstanceID(); } catch { } return fa.GetInstanceID(); }
        static bool Pending(TMP_FontAsset fa)
        {
            if (fa == null || fa == fallback || done.Contains(fa.GetInstanceID())) return false;
            var lut = fa.characterLookupTable; return lut != null && fa.characterTable != null && !lut.ContainsKey(Missing) && !Has(fa);
        }
        // fonts that still need the fix and are not in the cache (only those can need the slow probes)
        internal static int UnknownFonts()
        {
            LoadCache(); int n = 0;
            try { foreach (var fa in Resources.FindObjectsOfTypeAll<TMP_FontAsset>()) if (Pending(fa) && !cache.ContainsKey(Key(fa))) n++; } catch { }
            return n;
        }
        internal static bool NeedsProbe() { return UnknownFonts() > 0; }
        internal static string CacheStats() { return "glyphCache entries=" + cache.Count + " lastRun=" + (lastMs < 0 ? "not run" : lastMs.ToString("0") + "ms hits=" + lastHits + "/" + lastFonts + " probes=" + lastProbes); }

        // 2.3.3 order: font has it -> real character from the font file (skipped when this PC's cache or this run already knows that
        // font file lacks it) -> fallback font with the real character -> similar character -> (text swap at runtime)
        // 2.3.4: cachedOnly = only what this PC's cache already knows (cheap; run before the inventory warm-up so its texts find the
        // character); fonts not in the cache are left for a full run (no font-file probes now)
        internal static string Run(bool cachedOnly = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); probesSaved = 0; int lut1 = 0, dynReal = 0, dynSim = 0, fb = 0, fonts = 0, missing = 0, hits = 0, probes = 0, viaOther = 0, putOff = 0;
            warnAtLastRun = warnings; subFor.Clear(); LoadCache();
            string set = "";
            try
            {
                var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                var fbFirst = new HashSet<int>();
                var noReal = new HashSet<int>(); var noSim = new HashSet<int>(); var rest = new List<TMP_FontAsset>(); var keys = new List<string>(); var later = new List<TMP_FontAsset>();
                foreach (var fa in all)
                {
                    if (fa == null || fa == fallback) continue; fonts++;
                    string key = Key(fa); keys.Add(key);
                    int id = fa.GetInstanceID(); if (done.Contains(id)) continue;
                    var lut = fa.characterLookupTable; var table = fa.characterTable;
                    if (lut == null || table == null) continue;
                    cache.TryGetValue(key, out string code); if (code != null) hits++;
                    if (lut.ContainsKey(Missing)) { done.Add(id); Note(key, "T"); continue; }
                    if (Has(fa)) { done.Add(id); if (code == null) Note(key, "F"); continue; } // already covered through a fallback font
                    // 2.3.4: covered through another font last time (checked after that font is fixed), or not in the cache in a cached-only run
                    if (code == "F" || (cachedOnly && code == null)) { later.Add(fa); continue; }
                    bool knownNo = code != null && code != "R" && code != "T";
                    // 2.3.5: this PC's cache knows a fallback font that has the character: use it instead of asking the font file
                    // (each font-file probe loads the whole font file, about 90 ms for the big CJK fonts; the fallback is made once)
                    if (useFallback && cacheFallback >= 0 && !knownNo) { fbFirst.Add(fa.GetInstanceID()); probesSaved++; rest.Add(fa); continue; }
                    if (useDynamic && Dynamic(fa) && !knownNo && !noReal.Contains(SrcId(fa)))
                    {
                        probes++;
                        if (TryAdd(fa, Missing)) { done.Add(id); dynReal++; Note(key, "R"); continue; }
                        noReal.Add(SrcId(fa));
                    }
                    rest.Add(fa);
                }
                keys.Sort(StringComparer.Ordinal); set = ((uint)string.Join("\n", keys).GetHashCode()).ToString("x8") + "/" + keys.Count;
                // fallback font with the real character
                if (useFallback && rest.Count > 0)
                {
                    long f0 = System.Diagnostics.Stopwatch.GetTimestamp(); bool made = !fallbackTried;
                    var fbf = Fallback();
                    if (made) fbMs = (System.Diagnostics.Stopwatch.GetTimestamp() - f0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (fbf != null)
                        for (int i = rest.Count - 1; i >= 0; i--)
                        {
                            var fa = rest[i];
                            if (fa.fallbackFontAssetTable == null) fa.fallbackFontAssetTable = new List<TMP_FontAsset>();
                            if (!fa.fallbackFontAssetTable.Contains(fbf)) { fa.fallbackFontAssetTable.Add(fbf); fb++; }
                            if (Has(fa)) { Note(Key(fa), "N"); rest.RemoveAt(i); }
                        }
                }
                // 2.3.5: fonts sent to the fallback first that it did not cover: the font-file probe as before
                for (int i = rest.Count - 1; i >= 0; i--)
                {
                    var fa = rest[i]; if (!fbFirst.Contains(fa.GetInstanceID())) continue;
                    probesSaved--;
                    if (useDynamic && Dynamic(fa) && !noReal.Contains(SrcId(fa)))
                    {
                        probes++;
                        if (TryAdd(fa, Missing)) { done.Add(fa.GetInstanceID()); dynReal++; Note(Key(fa), "R"); rest.RemoveAt(i); continue; }
                        noReal.Add(SrcId(fa));
                    }
                }
                // a similar character, from the table or (dynamic fonts) from the font file; the cached one is tried first
                foreach (var fa in rest)
                {
                    string key = Key(fa); cache.TryGetValue(key, out string code);
                    var lut = fa.characterLookupTable; TMP_Character src = null; bool viaDyn = false; uint used = 0;
                    var order = new List<uint>();
                    if (code != null && code.Length > 1 && code[0] == 'S' && uint.TryParse(code.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out uint cu)) order.Add(cu);
                    foreach (var u in Similar) if (!order.Contains(u)) order.Add(u);
                    foreach (var u in order) { if (lut.TryGetValue(u, out var c) && c != null && c.glyph != null) { src = c; used = u; break; } }
                    if (src == null && useDynamic && Dynamic(fa) && code != "X" && !noSim.Contains(SrcId(fa)))
                    {
                        foreach (var u in order) { probes++; if (TryAdd(fa, u) && fa.characterLookupTable.TryGetValue(u, out var c) && c != null && c.glyph != null) { src = c; used = u; viaDyn = true; break; } }
                        if (src == null) noSim.Add(SrcId(fa));
                    }
                    if (src == null) { Note(key, "X"); continue; }
                    lut = fa.characterLookupTable; var table = fa.characterTable;
                    var ch = new TMP_Character(Missing, fa, src.glyph);
                    table.Add(ch); lut[Missing] = ch; done.Add(fa.GetInstanceID()); if (viaDyn) dynSim++; else lut1++;
                    Note(key, "S" + used.ToString("X"));
                }
                foreach (var fa in later)
                {
                    string key = Key(fa);
                    if (Has(fa)) { done.Add(fa.GetInstanceID()); viaOther++; Note(key, "F"); continue; }
                    if (cache.TryGetValue(key, out string c2) && c2 == "F") { cache.Remove(key); cacheDirty = true; } // no longer covered: probe it next time
                    putOff++;
                }
                foreach (var fa in all) if (fa != null && fa != fallback && !Has(fa)) missing++;
                missingNow = missing;
            }
            catch (Exception e) { return "glyph fix FAILED " + e.Message; }
            bool sameSet = set == cacheSet;
            SaveCache(set);
            sProbesSavedTotal += probesSaved;
            lastMs = sw.Elapsed.TotalMilliseconds; lastHits = hits; lastFonts = fonts; lastProbes = probes;
            return "glyph U+25C8" + (cachedOnly ? " (cached only)" : "") + ": fonts=" + fonts + " viaOtherFont=" + viaOther + (putOff > 0 ? " leftForFullRun=" + putOff : "") + " fromFontFile=" + dynReal + " fallbackAdded=" + fb + (fb > 0 ? "(" + fallbackName + ")" : "") + " similarFromTable=" + lut1 + " similarFromFontFile=" + dynSim
                + " stillMissing=" + missing + (missing > 0 ? (useReplace ? " (those texts get a similar character)" : " (text swap off)") : "")
                + " | cache hits=" + hits + " fontFileProbes=" + probes + (probesSaved > 0 ? " probesSkippedViaFallback=" + probesSaved : "") + (fbMs >= 0 ? " fallbackFontMade=" + fbMs.ToString("0") + "ms" : "") + (sameSet ? " sameFontSet" : " newFontSet") + " took=" + sw.ElapsedMilliseconds + "ms";
        }
        static int probesSaved, sProbesSavedTotal; static double fbMs = -1;
        internal static string Stats235() { return "glyph lastRun=" + (lastMs < 0 ? "not run" : lastMs.ToString("0") + "ms") + " probes=" + lastProbes + " probesSkippedViaFallback=" + sProbesSavedTotal; }
        internal static string Stats() { return "glyph warnings=" + warnings + " fontsMissing=" + (missingNow < 0 ? "not run" : missingNow.ToString()) + " textsSwapped=" + replaced; }
    }
}
