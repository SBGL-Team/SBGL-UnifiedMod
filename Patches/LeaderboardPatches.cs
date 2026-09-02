using HarmonyLib;
using UnityEngine;
using CompCheck = SBGL.UnifiedMod.Features.CompetitivePluginCheck.CompetitivePluginCheck;

namespace SBGL.UnifiedMod.Patches
{
    [HarmonyPatch]
    public static class LeaderboardPatches
    {
        /// <summary>
        /// Intercepts Scoreboard.UpdateVisibility when the player has toggled the SBGL leaderboard on.
        /// Blocks the native UI animation so the native scoreboard stays invisible,
        /// but still calls Refresh() so entries are populated for our scraper.
        /// Sets SbglOverlayOpen to drive our overlay.
        /// </summary>
        [HarmonyPatch(typeof(Scoreboard), "UpdateVisibility")]
        [HarmonyPrefix]
        public static bool PatchUpdateVisibility(Scoreboard __instance)
        {
            if (SBGLLiveLeaderboard.LiveLeaderboardPlugin.UseNativeScoreboard) return true; // player chose native view

            bool wantsExternal = (bool)AccessTools.Field(typeof(Scoreboard), "wantsToShowExternal").GetValue(__instance);
            bool shouldShow    = wantsExternal || CourseManager.ForceDisplayScoreboard;
            bool wasVisible    = (bool)AccessTools.Field(typeof(Scoreboard), "isVisible").GetValue(__instance);

            SBGLLiveLeaderboard.LiveLeaderboardPlugin.SbglOverlayOpen = shouldShow;

            if (shouldShow != wasVisible)
            {
                AccessTools.Field(typeof(Scoreboard), "isVisible").SetValue(__instance, shouldShow);
                if (shouldShow)
                {
                    try { AccessTools.Method(typeof(Scoreboard), "Refresh").Invoke(__instance, null); }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[LeaderboardPatches] Refresh failed: {ex.Message}");
                    }
                }
            }

            // Block native animation — visibilityController stays at alpha=0
            return false;
        }

        /// <summary>
        /// Fires every time the game calls Scoreboard.Refresh() — the same moment the
        /// official scoreboard updates its own display. We capture data here rather than
        /// on a 2-second poll so there is zero delay between the game updating and our
        /// overlay reflecting the change. Includes spectator entries.
        /// </summary>
        [HarmonyPatch(typeof(Scoreboard), "Refresh")]
        [HarmonyPostfix]
        public static void PostfixRefresh(Scoreboard __instance)
        {
            try
            {
                var plugin = SBGLLiveLeaderboard.LiveLeaderboardPlugin.Instance;
                if (plugin != null)
                    plugin.OnScoreboardRefreshed(__instance);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[LeaderboardPatches] PostfixRefresh error: {ex.Message}");
            }
        }
    }
}
