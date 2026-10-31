using System; using BepInEx.Configuration; using HarmonyLib; using UnityEngine;
namespace SpawnBudget {
// 2.1.2 "Hourly asset unload on a quiet moment" (client). The game calls Game.CollectResourcesCheckPeriodic once an hour
// while you play; it runs Resources.UnloadUnusedAssets (a long frame, "Unloading unused assets" top left). The other caller,
// Game.CollectResourcesCheck, already runs only when you arrive in the world and in the pause menu after 5 s without input,
// and is left alone. With this on, the hourly call made during play is held and run on the next loading screen (teleport,
// portal, respawn) or pause; vanilla's pause check also runs it, since it is then more than 20 minutes since the last one.
// After MaxHoldMinutes it runs at the next moment out of combat. Resources.UnloadUnusedAssets itself is not patched.
public partial class Plugin {
  static ConfigEntry<bool> ColOn; static ConfigEntry<float> ColMaxHold;
  static bool colPending, colBypass; static float colSince; static int sColHeld, sColRan, sColAllowed;
  void CollectConfig() {
    string s = SBS + "Asset Unload";
    ColOn = Config.Bind(s, "DeferHourlyUnload", true, "Hold the game's hourly unused-asset unload (Game.CollectResourcesCheckPeriodic, a long frame during play) until the next loading screen, teleport or pause. Off = the game's behavior.");
    ColMaxHold = Config.Bind(s, "MaxHoldMinutes", 90f, new ConfigDescription("If no loading screen or pause came for this long, run it at the next moment out of combat (minutes; 0 = wait for a loading screen or pause however long).", new AcceptableValueRange<float>(0f, 600f)));
  }
  static void CollectPatch(Harmony h) {
    try { var m = AccessTools.DeclaredMethod(typeof(Game), "CollectResourcesCheckPeriodic");
      if (m == null) { Log("asset unload defer: Game.CollectResourcesCheckPeriodic not found"); return; }
      h.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(ColPre)) { priority = Priority.First });
      var c = AccessTools.DeclaredMethod(typeof(Game), "CollectResources"); if (c != null) h.Patch(c, prefix: new HarmonyMethod(typeof(Plugin), nameof(ColDonePre)) { priority = Priority.First });
      Log("asset unload defer patch ok (enabled=" + ColOn.Value + ")"); }
    catch (Exception e) { Log("asset unload defer patch failed, game behavior: " + e.Message); }
  }
  static bool ColQuiet() { try { return Loading() || Game.IsPaused(); } catch { return false; } }
  static bool ColPre() {
    if (colBypass || ColOn == null || !ColOn.Value) return true;
    try { if (ZNet.instance == null || ZNet.instance.IsDedicated()) return true; } catch { return true; }
    if (ColQuiet()) { sColAllowed++; return true; }
    if (!colPending) { colPending = true; colSince = Time.unscaledTime; sColHeld++; Log("hourly asset unload held until a loading screen, teleport or pause"); }
    return false;
  }
  // any unload (also vanilla's own pause check) settles a held one
  static void ColDonePre() { if (colPending && !colBypass) { colPending = false; sColRan++; Log("held hourly asset unload done by the game's own check"); } }
  static void CollectTick() {
    if (!colPending) return;
    if (ColOn == null || !ColOn.Value || Game.instance == null) { colPending = false; return; }
    bool late = ColMaxHold.Value > 0f && Time.unscaledTime - colSince > ColMaxHold.Value * 60f && !SpawnBudgetPressure.Combat;
    if (!ColQuiet() && !late) return;
    colPending = false; colBypass = true;
    try { Game.instance.CollectResourcesCheckPeriodic(); sColRan++; Log("held hourly asset unload run (" + (late ? "held " + ColMaxHold.Value + " min, out of combat" : "loading screen / pause") + ")"); }
    catch (Exception e) { Log("held hourly asset unload threw: " + e.Message); }
    finally { colBypass = false; }
  }
  static string CollectStats() { return "hourly unload " + (ColOn != null && ColOn.Value ? "held " + sColHeld + ", ran later " + sColRan + ", allowed " + sColAllowed + (colPending ? ", waiting" : "") : "off"); }
}
}
