using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using SBGL.UnifiedMod.Core;

namespace SBGL.UnifiedMod.Patches
{
    [HarmonyPatch]
    public static class RulePatches
    {
        private static ManualLogSource _logger = null;

        public static void SetLogger(ManualLogSource logger)
        {
            _logger = logger;
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

                string matchType = PlayerPrefs.GetString("MatchType", "");
                if (string.IsNullOrEmpty(matchType) || !matchType.Contains("season"))
                {
                    Log($"Not a ranked/season match (MatchType='{matchType}'), skipping");
                    return;
                }

                var matchSetup = __instance.rules;
                if (matchSetup == null)
                {
                    LogError("__instance.rules is null");
                    return;
                }

                Log($"=== APPLYING SEASON 1 RULES (OnStartClient) ===");
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
            var rulesDict = Season1RuleSet.GetRulesSettings();
            int appliedCount = 0;

            foreach (var kvp in rulesDict)
            {
                try
                {
                    // SetValue writes to the SyncDictionary (only runs on server, which is already gated above)
                    matchSetup.SetValue(kvp.Key, kvp.Value);

                    // Update corresponding UI element so the rules panel reflects the new value
                    if (matchSetup.onOffDropdownLookup.TryGetValue(kvp.Key, out var dropdown))
                    {
                        dropdown.SetValue((!matchSetup.GetValueAsBoolInternal(kvp.Key)) ? 1 : 0);
                    }
                    else if (matchSetup.sliderLookup.TryGetValue(kvp.Key, out var slider))
                    {
                        slider.SetValue(matchSetup.GetValueInternal(kvp.Key));
                    }

                    matchSetup.UpdateRule(kvp.Key);

                    if (kvp.Key == MatchSetupRules.Rule.ConsoleCommands)
                        matchSetup.CheckAndShowCheatsWarning();

                    Log($"  ✓ Set {kvp.Key} = {kvp.Value}");
                    appliedCount++;
                }
                catch (System.Exception ex)
                {
                    LogError($"  ✗ Failed to set {kvp.Key}: {ex.Message}");
                }
            }

            Log($"✓ Applied {appliedCount}/{rulesDict.Count} rules");

            // Apply explicit item spawn weights for all 6 pools
            var itemWeights = Season1RuleSet.GetItemSpawnWeights();
            foreach (var kvp in itemWeights)
                matchSetup.SetSpawnChance(kvp.Key.itemPoolIndex, kvp.Key.itemType, kvp.Value);

            // ServerUpdateSpawnChanceValue syncs each pool's ItemPool.SpawnChances array.
            // Call it once per distinct pool index — it updates all items in that pool.
            var seenPools = new System.Collections.Generic.HashSet<int>();
            foreach (var key in itemWeights.Keys)
            {
                if (seenPools.Add(key.itemPoolIndex))
                    matchSetup.ServerUpdateSpawnChanceValue(key);
            }
            Log($"✓ Applied item weights for {seenPools.Count} pools ({itemWeights.Count} entries)");

            matchSetup.SetPreset(MatchSetupRules.Preset.Custom);
            Log($"✓ Set Preset to Custom");
        }

        public static void ApplyCourseSelection(MatchSetupMenu menu)
        {
            // Build set of approved hole names from MapPoolConfig (all biomes combined)
            var approvedNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var course in MapPoolConfig.GetApprovedCourses())
                approvedNames.Add(course.Name);

            // Filter allHoles to approved ones by ScriptableObject asset name
            var allHoles = GameManager.AllCourses.allHoles;
            var approvedHoles = new System.Collections.Generic.List<HoleData>();
            var matchedNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var hole in allHoles)
            {
                if (approvedNames.Contains(hole.name))
                {
                    approvedHoles.Add(hole);
                    matchedNames.Add(hole.name);
                }
            }

            // Log any approved names that had no matching hole asset — helps fix MapPoolConfig mismatches
            foreach (var name in approvedNames)
            {
                if (!matchedNames.Contains(name))
                    LogError($"  [UNMATCHED] Approved name '{name}' not found in allHoles — check MapPoolConfig spelling");
            }

            if (approvedHoles.Count == 0)
            {
                LogError("  No approved holes matched — aborting course selection");
                return;
            }

            // Inject approved holes into CustomCourseData and switch to custom mode
            MatchSetupMenu.CustomCourseData.OverrideHoles(approvedHoles.ToArray());
            menu.SetCourse(-1);

            // Enable random order and set 18 holes
            menu.NetworkrandomEnabled = true;
            menu.courseRandomToggle.isOn = true;
            menu.NetworkrandomCupNumHoles = 18;
            menu.numberOfHolesSlider.value = 18;

            Log($"  ✓ Set {approvedHoles.Count} approved holes, random order ON, 18 holes");
        }
    }
}
