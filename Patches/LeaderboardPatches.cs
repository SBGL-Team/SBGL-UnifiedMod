using HarmonyLib;
using System;
using UnityEngine;

namespace SBGL.UnifiedMod.Patches
{
    [HarmonyPatch]
    public static class LeaderboardPatches
    {
        // Static events the game's Scoreboard subscribes to while it is open. They outlive the
        // Scoreboard (a scene object), so a handler left behind on a destroyed instance stays
        // there for the rest of the session.
        private static readonly (Type type, string name)[] ScoreboardEvents =
        {
            (typeof(MatchSetupMenu), "GameModeChanged"),
            (typeof(CourseManager),  "PlayerStatesChanged"),
            (typeof(CourseManager),  "PlayerPingsChanged"),
            (typeof(CourseManager),  "TeamStatesChanged"),
            (typeof(CourseManager),  "ForceDisplayScoreboardChanged"),
            (typeof(PlayerId),       "AnyPlayerGuidChanged"),
        };

        /// <summary>
        /// Scoreboard.UpdateVisibility always runs in full. It does more than fade the panel:
        /// on show it subscribes the Scoreboard to the static TeamStatesChanged,
        /// PlayerStatesChanged, PlayerPingsChanged and GameModeChanged events, on hide it
        /// unsubscribes, and Scoreboard.OnDestroy only unsubscribes when isVisible is true.
        ///
        /// An earlier version of this patch skipped the native method and flipped isVisible
        /// itself. A scoreboard shown in the native view and then hidden in the SBGL view kept
        /// its subscriptions, and once its scene unloaded the destroyed instance stayed on those
        /// events. In the next team match every TeamStatesChanged callback threw on the dead
        /// object inside Mirror's deserialize, which aborted the CourseManager sync packet and
        /// dropped the matchState SyncVar. Clients never saw the hole enter Overtime or Ended
        /// and could keep playing.
        ///
        /// The prefix only remembers the previous visibility; the postfix hides the native panel
        /// when the SBGL view is active.
        /// </summary>
        [HarmonyPatch(typeof(Scoreboard), "UpdateVisibility")]
        [HarmonyPrefix]
        public static void PrefixUpdateVisibility(Scoreboard __instance, out bool __state)
        {
            __state = __instance.isVisible;
        }

        [HarmonyPatch(typeof(Scoreboard), "UpdateVisibility")]
        [HarmonyPostfix]
        public static void PostfixUpdateVisibility(Scoreboard __instance, bool __state)
        {
            if (SBGLLiveLeaderboard.LiveLeaderboardPlugin.UseNativeScoreboard) return; // player chose native view

            bool isVisible = __instance.isVisible;
            SBGLLiveLeaderboard.LiveLeaderboardPlugin.SbglOverlayOpen = isVisible;

            // The native method just started a fade in or out; keep the panel invisible either way.
            if (isVisible != __state)
                SuppressNativePanel(__instance);
        }

        /// <summary>
        /// Cancels the native fade and holds the panel at alpha 0 so only the SBGL overlay is
        /// seen. Everything else the game does for an open scoreboard (event subscriptions,
        /// Refresh, hiding the button prompts) is left alone.
        /// </summary>
        public static void SuppressNativePanel(Scoreboard scoreboard)
        {
            if (scoreboard == null) return;
            try
            {
                if (scoreboard.visibilityCoroutine != null)
                    scoreboard.StopCoroutine(scoreboard.visibilityCoroutine);
                if (scoreboard.visibilityController != null)
                    scoreboard.visibilityController.SetDesiredAlpha(0f);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LeaderboardPatches] SuppressNativePanel failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the player presses the view toggle key. Makes the native panel match the
        /// chosen view immediately instead of waiting for the next show or hide.
        /// </summary>
        public static void ApplyViewMode()
        {
            if (!SingletonBehaviour<Scoreboard>.HasInstance) return;
            var board = SingletonBehaviour<Scoreboard>.Instance;
            if (board == null) return;

            try
            {
                if (SBGLLiveLeaderboard.LiveLeaderboardPlugin.UseNativeScoreboard)
                {
                    SBGLLiveLeaderboard.LiveLeaderboardPlugin.SbglOverlayOpen = false;
                    if (board.visibilityController != null)
                        board.visibilityController.SetDesiredAlpha(board.isVisible ? 1f : 0f);
                }
                else
                {
                    SBGLLiveLeaderboard.LiveLeaderboardPlugin.SbglOverlayOpen = board.isVisible;
                    SuppressNativePanel(board);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LeaderboardPatches] ApplyViewMode failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Safety net: whatever state the Scoreboard believed it was in, a destroyed instance
        /// must leave nothing behind on the static events. Runs after the game's own OnDestroy,
        /// which only unsubscribes when isVisible is true.
        /// </summary>
        [HarmonyPatch(typeof(Scoreboard), "OnDestroy")]
        [HarmonyPostfix]
        public static void PostfixOnDestroy(Scoreboard __instance)
        {
            int removed = 0;
            foreach (var (type, name) in ScoreboardEvents)
            {
                try { removed += RemoveHandlersFor(type, name, __instance); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LeaderboardPatches] Could not clean {type.Name}.{name}: {ex.Message}");
                }
            }
            if (removed > 0)
                Debug.LogWarning($"[LeaderboardPatches] Removed {removed} stale Scoreboard event handler(s) on destroy");
        }

        /// <summary>
        /// Removes every handler on a static event whose target is the given object. Works on
        /// the compiler-generated backing field, so duplicate subscriptions are removed too.
        /// </summary>
        private static int RemoveHandlersFor(Type type, string eventName, object target)
        {
            var field = AccessTools.Field(type, eventName);
            if (field == null || !field.IsStatic) return 0;

            var current = field.GetValue(null) as Delegate;
            if (current == null) return 0;

            Delegate result = current;
            int removed = 0;
            foreach (var handler in current.GetInvocationList())
            {
                if (ReferenceEquals(handler.Target, target))
                {
                    result = Delegate.Remove(result, handler);
                    removed++;
                }
            }

            if (removed > 0)
                field.SetValue(null, result);
            return removed;
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
