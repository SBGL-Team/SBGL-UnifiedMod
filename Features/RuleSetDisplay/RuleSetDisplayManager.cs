using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;
using BepInEx.Logging;

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

        private GUIStyle _labelStyle;
        private Texture2D _bgTexture;

        public void SetConfig(
            ConfigEntry<float> posX,
            ConfigEntry<float> posY,
            ConfigEntry<bool> showDetails)
        {
            _posX = posX;
            _posY = posY;
            _showDetails = showDetails;
        }

        public void Awake() {
            Debug.Log("[RuleSetDisplayManager] Awake - Feature loaded");
        }

        public void OnGUI() {
            try {
                // Only show on the Driving Range
                string scene = SceneManager.GetActiveScene().name.ToLower();
                if (!scene.Contains("drivingrange") && !scene.Contains("driving range")) return;

                // Only show to the server host
                if (!Mirror.NetworkServer.active) return;

                RenderPanel();
            } catch (System.Exception ex) {
                Debug.LogError($"[RuleSetDisplayManager] OnGUI error: {ex.Message}\n{ex.StackTrace}");
            }
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

            if (_bgTexture == null) {
                _bgTexture = new Texture2D(1, 1);
                _bgTexture.SetPixel(0, 0, new Color(0.1f, 0.1f, 0.1f, 0.8f));
                _bgTexture.Apply();
            }

            bool showDetails = _showDetails?.Value ?? false;
            float panelWidth = 320f;
            float buttonHeight = 40f;
            float detailsHeight = showDetails ? 110f : 0f;
            float panelHeight = 30f + buttonHeight + detailsHeight + 10f;

            float cfgX = _posX?.Value ?? -1f;
            float panelX = (cfgX < 0f) ? (Screen.width - panelWidth - 20f) : cfgX;
            float panelY = _posY?.Value ?? 20f;

            GUI.DrawTexture(new Rect(panelX, panelY, panelWidth, panelHeight), _bgTexture);
            GUI.Box(new Rect(panelX, panelY, panelWidth, panelHeight), "<b>APPLY RULESET</b>");

            float btnY = panelY + 28f;
            float btnW = (panelWidth - 30f) / 2f;

            GUI.backgroundColor = Color.green;
            if (GUI.Button(new Rect(panelX + 10f, btnY, btnW, buttonHeight), "<b>RANKED</b>"))
                ApplyRuleset("ranked");

            GUI.backgroundColor = Color.magenta;
            if (GUI.Button(new Rect(panelX + 10f + btnW + 10f, btnY, btnW, buttonHeight), "<b>PRO SERIES</b>"))
                ApplyRuleset("pro_series");

            GUI.backgroundColor = Color.white;

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
            
            // Pick a random approved course
            var randomCourse = Core.MapPoolConfig.GetRandomApprovedCourse();
            PlayerPrefs.SetString("SelectedCourse", randomCourse.Name);
            
            PlayerPrefs.Save();
            
            Debug.Log($"[RuleSetDisplayManager] ✓ Ruleset {rulesetName} applied. Season=1, Course={randomCourse.Name}. Stored in PlayerPrefs");
            
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
    }
}
