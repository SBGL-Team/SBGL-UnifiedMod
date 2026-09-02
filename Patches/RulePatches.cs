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

                if (!(_applyRulesets?.Value ?? false))
                {
                    Log("ApplyRulesets is disabled in config — skipping rule enforcement");
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

                Log($"=== APPLYING SEASON 2 RULES (OnStartClient) ===");
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

            // Season 2: all formats use the same base settings (game defaults) with
            // only Wind, Comeback, and WhiteFlag overridden.
            Dictionary<MatchSetupRules.Rule, float> rulesDict;
            if (isCasual)
                rulesDict = Season2RuleSet.GetCasualRulesSettings();
            else if (isProSeries)
                rulesDict = Season2RuleSet.GetProSeriesRulesSettings();
            else
                rulesDict = Season2RuleSet.GetRankedRulesSettings();

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

            Log($"✓ Applied {appliedCount}/{rulesDict.Count} Season 2 rules (item weights at game defaults)");
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

            // Season 2: every hole except the banned ones
            var bannedNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var course in MapPoolConfig.GetBannedCourses())
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

            // Enable random order and set 9 holes
            menu.NetworkrandomEnabled = true;
            menu.courseRandomToggle.isOn = true;
            menu.NetworkrandomCupNumHoles = 9;
            menu.numberOfHolesSlider.value = 9;

            Log($"  ✓ Set {eligibleHoles.Count} eligible holes ({matchedNames.Count} banned excluded), random order ON, 9 holes");
        }
    }
}
