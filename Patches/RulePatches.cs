using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;
using SBGL.UnifiedMod.Core;

namespace SBGL.UnifiedMod.Patches
{
    [HarmonyPatch]
    public static class RulePatches
    {
        /// <summary>
        /// HostRuleset value for the Driving Range 2V2 button. It applies the 2v2 rules and sets
        /// the team 2v2 match type, so the result uploads as an official 2v2 with Red/Blue
        /// rosters. Submission is refused unless the in-game teams are two a side.
        /// </summary>
        public const string HOST_RULESET_2V2 = "ranked_2v2";

        /// <summary>
        /// Set by the NO RULESET button: rules are off for this lobby only, and it clears when
        /// the host picks another ruleset or returns to the main menu. Deliberately not saved to
        /// config — a stray click shouldn't leave every future match unenforced. The saved
        /// "Apply Rulesets" setting is the lasting off switch for when rules misbehave.
        /// </summary>
        public static bool SuspendedForLobby = false;

        private static ManualLogSource _logger = null;
        private static BepInEx.Configuration.ConfigEntry<bool> _applyRulesets = null;

        public static void SetLogger(ManualLogSource logger)
        {
            _logger = logger;
        }

        public static void SetApplyRulesetsConfig(BepInEx.Configuration.ConfigEntry<bool> applyRulesets)
        {
            _applyRulesets = applyRulesets;
        }

        private static void Log(string message)
        {
            if (_logger != null)
                _logger.LogInfo($"[RulePatches] {message}");
        }

        private static void LogError(string message)
        {
            if (_logger != null)
                _logger.LogError($"[RulePatches] {message}");
        }

        /// <summary>
        /// Hook after MatchSetupMenu.OnStartClient - fires after rules.Initialize() runs on the server.
        /// This is the correct timing: dropdowns and sliders are populated, SyncDictionary is ready.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MatchSetupMenu), nameof(MatchSetupMenu.OnStartClient))]
        public static void PatchMatchSetupMenuOnStartClient(MatchSetupMenu __instance)
        {
            try
            {
                if (!__instance.isServer) return;

                // The saved setting is the escape hatch for when rule enforcement itself is
                // misbehaving, so it wins everywhere, including matches from the queue.
                if (!(_applyRulesets?.Value ?? true))
                {
                    Log("Apply Rulesets is turned off in the mod settings — skipping rule enforcement");
                    return;
                }

                // The NO RULESET button only covers the host's own lobby. A match the league's
                // matchmaking created still plays by the league rules.
                bool fromMatchmaking = SBGLeagueAutomation.SBGLPlugin.IsRankedTriggered;
                if (SuspendedForLobby && !fromMatchmaking)
                {
                    Log("NO RULESET selected for this lobby — skipping rule enforcement");
                    return;
                }

                string matchType = PlayerPrefs.GetString("MatchType", "");
                bool isManagedMatch = Season2RuleSet.IsManagedMatchType(matchType)
                    || (!string.IsNullOrEmpty(matchType) && matchType.Contains("season_1"))
                    || string.Equals(matchType, Season1RuleSet.MATCH_TYPE_CASUAL, System.StringComparison.OrdinalIgnoreCase);
                if (!isManagedMatch)
                {
                    Log($"Not a managed ruleset match (MatchType='{matchType}'), skipping");
                    return;
                }

                var matchSetup = __instance.rules;
                if (matchSetup == null)
                {
                    LogError("__instance.rules is null");
                    return;
                }

                Log($"=== APPLYING SEASON {SeasonRuleSets.Current.Season} RULES (OnStartClient) ===");
                Log($"  Website Season: {(SeasonRuleSets.WebsiteSeason > 0 ? SeasonRuleSets.WebsiteSeason.ToString() : "unknown")}");
                Log($"  Match Type: {matchType}");
                Log($"  Host Ruleset: {PlayerPrefs.GetString("HostRuleset", "ranked")}");

                ApplyRulesToMatchSetup(matchSetup);
                ApplyCourseSelection(__instance);

                Log($"============================");
            }
            catch (System.Exception ex)
            {
                LogError($"Exception in PatchMatchSetupMenuOnStartClient: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies Season 1 rules to a MatchSetupRules instance using the game's own private API.
        /// Uses IgnoresAccessChecksTo("GameAssembly") for direct private member access.
        /// Modelled on https://github.com/ryaghain/CustomRulesPresets
        /// </summary>
        public static void ApplyRulesToMatchSetup(MatchSetupRules matchSetup)
        {
            string hostRuleset = PlayerPrefs.GetString("HostRuleset", "ranked");
            bool isProSeries = hostRuleset == "pro_series";
            bool isCasual    = hostRuleset == "casual";

            // Reset to Classic first so our values override any previous state cleanly.
            matchSetup.SetPreset(MatchSetupRules.Preset.Classic);
            Log("✓ Reset to Classic preset");

            // All formats use the same base settings (game defaults) with
            // only Wind, Comeback, and WhiteFlag overridden.
            var season = SeasonRuleSets.Current;
            Dictionary<MatchSetupRules.Rule, float> rulesDict;
            if (isCasual)
                rulesDict = season.GetCasualRules();
            else if (isProSeries)
                rulesDict = season.GetProSeriesRules();
            else
                rulesDict = season.GetRankedRules();

            // 2v2 has its own rules on top of ranked. This covers both a 2v2 from the queue,
            // which arrives as a team match type, and a host picking 2V2 on the Driving Range.
            // 3v3, 4v4 and singles are untouched.
            string matchType = PlayerPrefs.GetString("MatchType", "");
            bool is2v2 = hostRuleset == HOST_RULESET_2V2 || Season2RuleSet.GetTeamSize(matchType) == 2;
            if (is2v2 && season.GetTwoVsTwoOverrides != null)
            {
                foreach (var kvp in season.GetTwoVsTwoOverrides())
                    rulesDict[kvp.Key] = kvp.Value;
                Log("  2v2 ruleset: applying 2v2 rule overrides");
            }

            int appliedCount = 0;
            foreach (var kvp in rulesDict)
            {
                try
                {
                    matchSetup.SetValue(kvp.Key, kvp.Value);

                    if (matchSetup.onOffDropdownLookup.TryGetValue(kvp.Key, out var dropdown))
                        dropdown.SetValue((!matchSetup.GetValueAsBoolInternal(kvp.Key)) ? 1 : 0);
                    else if (matchSetup.sliderLookup.TryGetValue(kvp.Key, out var slider))
                        slider.SetValue(matchSetup.GetValueInternal(kvp.Key));
                    else if (matchSetup.dropdownLookup.TryGetValue(kvp.Key, out var multiDropdown))
                        multiDropdown.SetValue((int)matchSetup.GetValueInternal(kvp.Key));

                    matchSetup.UpdateRule(kvp.Key);
                    Log($"  ✓ Set {kvp.Key} = {kvp.Value}");
                    appliedCount++;
                }
                catch (System.Exception ex)
                {
                    LogError($"  ✗ Failed to set {kvp.Key}: {ex.Message}");
                }
            }

            Log($"✓ Applied {appliedCount}/{rulesDict.Count} Season {season.Season} rules (item weights at game defaults)");
        }

        public static void ApplyCourseSelection(MatchSetupMenu menu)
        {
            string hostRuleset = PlayerPrefs.GetString("HostRuleset", "ranked");
            bool isProSeries = hostRuleset == "pro_series";
            bool isCasual = hostRuleset == "casual";

            // Pro Series and Casual: maps are set manually — skip all course selection logic
            if (isProSeries)
            {
                Log("  Pro Series: skipping course selection (maps set manually)");
                return;
            }

            if (isCasual)
            {
                Log("  Casual: skipping course selection (maps set manually)");
                return;
            }

            var allHoles = GameManager.AllCourses.allHoles;
            var season = SeasonRuleSets.Current;

            // Every hole except the ones banned this season
            var bannedNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var course in season.BannedCourses)
                bannedNames.Add(course.Name);

            var eligibleHoles = new System.Collections.Generic.List<HoleData>();
            var matchedNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var hole in allHoles)
            {
                if (bannedNames.Contains(hole.name))
                    matchedNames.Add(hole.name);
                else
                    eligibleHoles.Add(hole);
            }

            // A banned name that matches no hole asset silently bans nothing — surface it loudly.
            foreach (var name in bannedNames)
            {
                if (!matchedNames.Contains(name))
                    LogError($"  [UNMATCHED] Banned name '{name}' not found in allHoles — check MapPoolConfig spelling");
            }

            if (eligibleHoles.Count == 0)
            {
                LogError("  No eligible holes found — aborting course selection");
                return;
            }

            // Inject all holes into CustomCourseData and switch to custom mode
            MatchSetupMenu.CustomCourseData.OverrideHoles(eligibleHoles.ToArray());
            menu.SetCourse(-1);

            // Enable random order and set the season's hole count (team matches come through here too)
            int numHoles = season.RankedNumHoles;
            menu.NetworkrandomEnabled = true;
            menu.courseRandomToggle.isOn = true;
            menu.NetworkrandomCupNumHoles = numHoles;
            menu.numberOfHolesSlider.value = numHoles;

            Log($"  ✓ Season {season.Season}: set {eligibleHoles.Count} eligible holes ({matchedNames.Count} banned excluded), random order ON, {numHoles} holes");
        }
    }
}
