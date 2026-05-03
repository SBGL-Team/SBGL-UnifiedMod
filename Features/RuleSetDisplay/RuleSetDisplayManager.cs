using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;
using BepInEx.Logging;
using System;

namespace SBGL.UnifiedMod.Features {
    /// <summary>
    /// Manages the RANKED / PRO SERIES apply-ruleset buttons on the Driving Range.
    /// Only visible when the local player is the server host.
    /// Config options control position and whether the detail panel is shown.
    /// </summary>
    public class RuleSetDisplayManager : MonoBehaviour {

        // Config entries injected by Plugin.cs
        private ConfigEntry<float> _posX;
        private ConfigEntry<float> _posY;
        private ConfigEntry<bool> _showDetails;
        private ConfigEntry<bool> _applyRulesets;

        private GUIStyle _labelStyle;
        private GUIStyle _smallBtnStyle;
        private GUIStyle _tooltipStyle;
        private Texture2D _bgTexture;
        private Texture2D _tooltipBgTexture;
        private string _tooltipRanked = null;
        private string _tooltipPro    = null;
        private string _noRulesetTooltip = "<b>Applies Classic preset only.</b>\n<color=#AAFFAA>No Season 1 rules or item bans enforced.</color>";
        private string _lastSceneName = string.Empty;
        private bool _defaultAppliedForScene = false;

        public void SetConfig(
            ConfigEntry<float> posX,
            ConfigEntry<float> posY,
            ConfigEntry<bool> showDetails,
            ConfigEntry<bool> applyRulesets = null)
        {
            _posX = posX;
            _posY = posY;
            _showDetails = showDetails;
            _applyRulesets = applyRulesets;
        }

        public void Awake() {
            Debug.Log("[RuleSetDisplayManager] Awake - Feature loaded");
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            string name = scene.name.ToLower();
            if (name.Contains("menu")) {
                // Reset ruleset selection whenever the player returns to the main menu so
                // the next driving-range session always starts with Ranked as the default.
                PlayerPrefs.SetString("HostRuleset", "ranked");
                PlayerPrefs.SetString("MatchType", "ranked_season_1");
                PlayerPrefs.SetInt("Season", 1);
                PlayerPrefs.Save();
                Debug.Log("[RuleSetDisplayManager] Returned to main menu — ruleset reset to ranked");
            }
        }

        public void OnGUI() {
            try {
                // Only show on the Driving Range
                string scene = SceneManager.GetActiveScene().name.ToLower();
                if (!string.Equals(scene, _lastSceneName, System.StringComparison.Ordinal)) {
                    _lastSceneName = scene;
                    _defaultAppliedForScene = false;
                }
                if (!scene.Contains("drivingrange") && !scene.Contains("driving range")) return;

                // Only show to the server host
                if (!Mirror.NetworkServer.active) return;

                // Default to ranked once per driving-range scene unless explicitly changed by button click.
                EnsureDefaultRankedForScene();

                RenderPanel();
            } catch (System.Exception ex) {
                Debug.LogError($"[RuleSetDisplayManager] OnGUI error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void EnsureDefaultRankedForScene()
        {
            if (_defaultAppliedForScene) return;
            _defaultAppliedForScene = true;

            // Default to ranked on every fresh driving-range entry.
            // If the user clicks a ruleset button this visit, PlayerPrefs will be overwritten
            // and _defaultAppliedForScene stays true so this won't run again until the scene
            // changes. When the player returns to the main menu, OnSceneLoaded resets the
            // selection back to ranked for the next visit.
            PlayerPrefs.SetString("HostRuleset", "ranked");
            PlayerPrefs.SetString("MatchType", "ranked_season_1");
            PlayerPrefs.SetInt("Season", 1);

            string currentCourse = PlayerPrefs.GetString("SelectedCourse", "");
            if (string.IsNullOrWhiteSpace(currentCourse))
            {
                var randomCourse = Core.MapPoolConfig.GetRandomApprovedCourse();
                PlayerPrefs.SetString("SelectedCourse", randomCourse.Name);
            }

            PlayerPrefs.Save();
            Debug.Log("[RuleSetDisplayManager] Defaulted ruleset to ranked for this driving-range session");
        }

        private void RenderPanel() {
            if (_labelStyle == null) {
                _labelStyle = new GUIStyle(GUI.skin?.label ?? new GUIStyle()) {
                    alignment = TextAnchor.MiddleLeft,
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    richText = true
                };
            }

            if (_smallBtnStyle == null) {
                _smallBtnStyle = new GUIStyle(GUI.skin?.button ?? new GUIStyle()) {
                    fontSize = 10,
                    fontStyle = FontStyle.Bold,
                    richText = true
                };
            }

            if (_bgTexture == null) {
                _bgTexture = new Texture2D(1, 1);
                _bgTexture.SetPixel(0, 0, new Color(0.1f, 0.1f, 0.1f, 0.8f));
                _bgTexture.Apply();
            }

            if (_tooltipBgTexture == null) {
                _tooltipBgTexture = new Texture2D(1, 1);
                _tooltipBgTexture.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.05f, 0.95f));
                _tooltipBgTexture.Apply();
            }

            if (_tooltipStyle == null) {
                _tooltipStyle = new GUIStyle(GUI.skin?.box ?? new GUIStyle()) {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = 14,
                    wordWrap = true,
                    richText = true,
                    padding = new RectOffset(8, 8, 6, 6)
                };
                _tooltipStyle.normal.background = _tooltipBgTexture;
                _tooltipStyle.normal.textColor = Color.white;
            }

            bool showDetails = _showDetails?.Value ?? false;
            bool rulesEnabled = _applyRulesets?.Value ?? true;
            string activeRuleset = rulesEnabled ? PlayerPrefs.GetString("HostRuleset", "ranked") : "none";

            float panelWidth = 340f;
            float buttonHeight = 40f;
            float detailsHeight = showDetails ? 110f : 0f;
            float panelHeight = 30f + buttonHeight + detailsHeight + 10f;

            float cfgX = _posX?.Value ?? -1f;
            float panelX = (cfgX < 0f) ? (Screen.width - panelWidth - 20f) : cfgX;
            float panelY = _posY?.Value ?? 20f;

            GUI.DrawTexture(new Rect(panelX, panelY, panelWidth, panelHeight), _bgTexture);
            GUI.Box(new Rect(panelX, panelY, panelWidth, panelHeight), "<b>APPLY RULESET</b>");

            float btnY = panelY + 28f;
            float btnW = (panelWidth - 40f) / 3f; // 10 left + 10 gap + 10 gap + 10 right

            float btn1X = panelX + 10f;
            float btn2X = btn1X + btnW + 10f;
            float btn3X = btn2X + btnW + 10f;

            // Build tooltips lazily from the live rule data so they stay accurate if rules change
            if (_tooltipRanked == null)
            {
                int courseCount = Core.MapPoolConfig.GetApprovedCourses().Count;
                _tooltipRanked = Core.Season1RuleSet.BuildRulesDescription(Core.Season1RuleSet.GetRulesSettings())
                    + $"\n<b>Banned Items:</b> <color=#AAFFAA>{Core.Season1RuleSet.BuildBannedItemsDescription()}</color>"
                    + $"\n<b>Courses:</b> <color=#AAFFAA>Random ({courseCount} approved)</color>";
            }
            if (_tooltipPro == null)
            {
                _tooltipPro = Core.Season1RuleSet.BuildRulesDescription(Core.Season1RuleSet.GetProSeriesRulesSettings())
                    + "\n<b>Items:</b> <color=#AAFFAA>Game defaults (all enabled)</color>"
                    + "\n<b>Courses:</b> <color=#AAFFAA>Manual selection</color>";
            }

            var rankedContent    = new GUIContent("<b>RANKED</b>");
            var proContent       = new GUIContent("<b>PRO SERIES</b>");
            var noRulesetContent = new GUIContent("<b>NO RULESET</b>");

            var rect1 = new Rect(btn1X, btnY, btnW, buttonHeight);
            var rect2 = new Rect(btn2X, btnY, btnW, buttonHeight);
            var rect3 = new Rect(btn3X, btnY, btnW, buttonHeight);

            GUI.backgroundColor = activeRuleset == "ranked" ? Color.green : Color.grey;
            if (GUI.Button(rect1, rankedContent)) {
                if (_applyRulesets != null) _applyRulesets.Value = true;
                ApplyRuleset("ranked");
            }

            GUI.backgroundColor = activeRuleset == "pro_series" ? Color.magenta : Color.grey;
            if (GUI.Button(rect2, proContent)) {
                if (_applyRulesets != null) _applyRulesets.Value = true;
                ApplyRuleset("pro_series");
            }

            GUI.backgroundColor = activeRuleset == "none" ? Color.yellow : Color.grey;
            if (GUI.Button(rect3, noRulesetContent, _smallBtnStyle)) {
                if (_applyRulesets != null) _applyRulesets.Value = false;
                ResetToClassicPreset();
                Debug.Log("[RuleSetDisplayManager] Ruleset enforcement disabled via No Ruleset button");
            }

            GUI.backgroundColor = Color.white;

            // Manual hover detection — GUI.tooltip doesn't reliably clear between frames.
            // Determine which tooltip to show based on mouse position over each button rect.
            if (Event.current.type == EventType.Repaint) {
                var mouse = Event.current.mousePosition;
                string tip = null;
                if (rect1.Contains(mouse))      tip = _tooltipRanked;
                else if (rect2.Contains(mouse)) tip = _tooltipPro;
                else if (rect3.Contains(mouse)) tip = _noRulesetTooltip;

                if (!string.IsNullOrEmpty(tip)) {
                    float ttW = 280f;
                    var tipContent = new GUIContent(tip);
                    float ttH = _tooltipStyle.CalcHeight(tipContent, ttW);
                    float ttX = mouse.x + 14f;
                    float ttY = mouse.y + 14f;
                    if (ttX + ttW > Screen.width)  ttX = mouse.x - ttW - 6f;
                    if (ttY + ttH > Screen.height) ttY = mouse.y - ttH - 6f;
                    GUI.Box(new Rect(ttX, ttY, ttW, ttH), tipContent, _tooltipStyle);
                }
            }

            if (showDetails) {
                string matchType   = PlayerPrefs.GetString("MatchType",      "—");
                string course      = PlayerPrefs.GetString("SelectedCourse", "—");
                int    season      = PlayerPrefs.GetInt("Season", 0);
                string ruleset     = PlayerPrefs.GetString("HostRuleset",    "—");

                float lx = panelX + 10f;
                float ly = btnY + buttonHeight + 8f;
                float lh = 20f;
                float ls = 22f;

                GUI.Label(new Rect(lx, ly,      panelWidth - 20f, lh), $"<color=#00FF00>Type: {matchType}</color>",   _labelStyle);
                GUI.Label(new Rect(lx, ly + ls,  panelWidth - 20f, lh), $"<color=#00FF00>Course: {course}</color>",   _labelStyle);
                GUI.Label(new Rect(lx, ly+ls*2,  panelWidth - 20f, lh), $"<color=#00FF00>Season: {season}</color>",   _labelStyle);
                GUI.Label(new Rect(lx, ly+ls*3,  panelWidth - 20f, lh), $"<color=#00FF00>Ruleset: {ruleset}</color>", _labelStyle);
            }
        }

        private void ApplyRuleset(string rulesetName) {
            Debug.Log($"[RuleSetDisplayManager] Applying ruleset: {rulesetName}");
            
            // Store the ruleset choice
            PlayerPrefs.SetString("HostRuleset", rulesetName);
            
            // Update match type to indicate ranked
            if (rulesetName == "ranked") {
                PlayerPrefs.SetString("MatchType", "ranked_season_1");
            } else if (rulesetName == "pro_series") {
                PlayerPrefs.SetString("MatchType", "pro_series_season_1");
            }
            
            // Set Season to 1 so rules apply correctly
            PlayerPrefs.SetInt("Season", 1);
            
            // For Ranked: pick a random approved course.
            // For Pro Series: maps are managed manually — do NOT overwrite the custom map list.
            if (rulesetName != "pro_series")
            {
                var randomCourse = Core.MapPoolConfig.GetRandomApprovedCourse();
                PlayerPrefs.SetString("SelectedCourse", randomCourse.Name);
                PlayerPrefs.Save();
                Debug.Log($"[RuleSetDisplayManager] ✓ Ruleset {rulesetName} applied. Season=1, Course={randomCourse.Name}. Stored in PlayerPrefs");
            }
            else
            {
                PlayerPrefs.Save();
                Debug.Log($"[RuleSetDisplayManager] ✓ Ruleset {rulesetName} applied. Season=1. Course preserved (Pro Series maps are managed manually). Stored in PlayerPrefs");
            }
            
            // IMPORTANT: Immediately apply rules to any existing MatchSetupRules instance on this scene
            // This ensures rules take effect NOW on the driving range, not later when the match loads
            ApplyRulesToExistingMatchSetupRules();
        }
        
        private void ApplyRulesToExistingMatchSetupRules()
        {
            try
            {
                var matchSetupMenu = FindAnyObjectByType<MatchSetupMenu>();
                if (matchSetupMenu == null || !matchSetupMenu.isServer)
                {
                    Debug.Log("[RuleSetDisplayManager] Match setup menu not open - rules will be applied automatically when it opens");
                    return;
                }

                var matchSetup = matchSetupMenu.rules;
                if (matchSetup == null)
                {
                    Debug.Log("[RuleSetDisplayManager] MatchSetupMenu.rules is null");
                    return;
                }

                Debug.Log("[RuleSetDisplayManager] ✓ Match setup menu is open - applying rules NOW");
                Patches.RulePatches.ApplyRulesToMatchSetup(matchSetup);
                Patches.RulePatches.ApplyCourseSelection(matchSetupMenu);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RuleSetDisplayManager] Error applying rules: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void ResetToClassicPreset()
        {
            try
            {
                var matchSetupMenu = FindAnyObjectByType<MatchSetupMenu>();
                if (matchSetupMenu == null || !matchSetupMenu.isServer) return;

                var matchSetup = matchSetupMenu.rules;
                if (matchSetup == null) return;

                matchSetup.SetPreset(MatchSetupRules.Preset.Classic);
                Debug.Log("[RuleSetDisplayManager] ✓ Reset to Classic preset (No Ruleset)");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RuleSetDisplayManager] Error resetting to Classic: {ex.Message}");
            }
        }
    }
}
