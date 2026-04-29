using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SBGLLiveLeaderboard
{
    public class LiveLeaderboardPlugin : MonoBehaviour
    {
        // Config entry references (injected by UnifiedPlugin)
        private ConfigEntry<float> _configWidth, _configMaxHeight, _configPosX, _configPosY, _configOpacity;
        private ConfigEntry<int> _configMaxPlayers;


        private const float K_FACTOR = 24f;

        // Config helpers using ConfigEntry references
        private float ConfigWidth { get => _configWidth?.Value ?? PlayerPrefs.GetFloat("LL_Width", 350f); }
        private float ConfigMaxHeight { get => _configMaxHeight?.Value ?? PlayerPrefs.GetFloat("LL_MaxHeight", 999f); }
        private float ConfigPosX { get => _configPosX?.Value ?? PlayerPrefs.GetFloat("LL_PosX", 5f); }
        private float ConfigPosY { get => _configPosY?.Value ?? PlayerPrefs.GetFloat("LL_PosY", 200f); }
        private float ConfigOpacity { get => _configOpacity?.Value ?? PlayerPrefs.GetFloat("LL_Opacity", 0.85f); }
        private int ConfigMaxPlayers { get => _configMaxPlayers?.Value ?? PlayerPrefs.GetInt("LL_MaxPlayers", 16); }
        private Key ToggleKey => Key.F8;

        // Set config entries from parent plugin
        public void SetConfig(ConfigEntry<float> width, ConfigEntry<float> maxHeight, ConfigEntry<float> posX, 
            ConfigEntry<float> posY, ConfigEntry<float> opacity, ConfigEntry<int> maxPlayers)
        {
            _configWidth = width;
            _configMaxHeight = maxHeight;
            _configPosX = posX;
            _configPosY = posY;
            _configOpacity = opacity;
            _configMaxPlayers = maxPlayers;
        }

        private bool _showWindow = true;
        private float _updateInterval = 2.0f;
        private float _nextUpdateTime = 0f;
        private float _lastOpacity = -1f;

        private List<SBGLPlayer> _persistentLeaderboard = new List<SBGLPlayer>();
        private List<SBGLPlayer> _finalLeaderboardSnapshot = new List<SBGLPlayer>(); // Store final scores when leaving gameplay
        private bool _showFinalSnapshot = false; // True when in driving range to freeze display
        private readonly Dictionary<string, SBGLPlayer> _lastKnownRoundPlayers = new Dictionary<string, SBGLPlayer>(System.StringComparer.OrdinalIgnoreCase);
        private bool _roundCacheActive = false;
        private Dictionary<string, (string mmr, float lastFetchTime)> _mmrCache = new Dictionary<string, (string, float)>();
        private HashSet<string> _pendingRequests = new HashSet<string>();
        
        private FieldInfo _entriesField;
        private Scoreboard _cachedScoreboard;
        private GUIStyle _windowStyle, _centeredStyle, _headerStyle, _nameStyle, _posStyle, _negStyle;
        private Texture2D _blackTexture;

        public class SBGLPlayer {
            public string Name;
            public int BaseScore;
            public int AdjustedPoints;
            public string RawStrokes;
            public string MMR = "...";
            public string ProjectedDisplay = "..."; 
        }

        void Awake()
        {
            // Initialize PlayerPrefs with defaults
            if (!PlayerPrefs.HasKey("LL_Width")) PlayerPrefs.SetFloat("LL_Width", 350f);
            if (!PlayerPrefs.HasKey("LL_MaxHeight")) PlayerPrefs.SetFloat("LL_MaxHeight", 999f);
            if (!PlayerPrefs.HasKey("LL_PosX")) PlayerPrefs.SetFloat("LL_PosX", 5f);
            if (!PlayerPrefs.HasKey("LL_PosY")) PlayerPrefs.SetFloat("LL_PosY", 200f);
            if (!PlayerPrefs.HasKey("LL_Opacity")) PlayerPrefs.SetFloat("LL_Opacity", 0.85f);
            if (!PlayerPrefs.HasKey("LL_MaxPlayers")) PlayerPrefs.SetInt("LL_MaxPlayers", 16);

            _entriesField = typeof(Scoreboard).GetField("entries", BindingFlags.NonPublic | BindingFlags.Instance);
            _blackTexture = new Texture2D(1, 1);
            
            // Subscribe to API configuration changes
            SBGL.UnifiedMod.Core.UnifiedPlugin.ApiConfigChanged += OnApiConfigChanged;
            
            UnityEngine.Object.DontDestroyOnLoad(this.gameObject);
        }

        private void OnApiConfigChanged()
        {
            // Clear cache and force immediate resync when API changes
            _mmrCache.Clear();
            _pendingRequests.Clear();
            _nextUpdateTime = Time.time;
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current[ToggleKey].wasPressedThisFrame)
                _showWindow = !_showWindow;

            // Check current scene and adjust behavior
            string currentScene = SceneManager.GetActiveScene().name ?? string.Empty;
            bool inDrivingRange = currentScene.Contains("Driving") || currentScene.Contains("Range");
            bool inMenuScene = currentScene.ToLowerInvariant().Contains("menu");

            if (inDrivingRange) {
                _showFinalSnapshot = true;  // Freeze on final snapshot
            } else {
                _showFinalSnapshot = false; // Go back to live updates
            }

            // Reset all leaderboard state when entering the main menu to avoid stale data
            if (inMenuScene) {
                ResetForMainMenu();
            }

            if (Time.time >= _nextUpdateTime)
            {
                ScrapeData();
                _nextUpdateTime = Time.time + _updateInterval;
            }
        }

        void OnGUI()
        {
            // Use final snapshot if in driving range, otherwise use live data
            List<SBGLPlayer> displayLeaderboard = _showFinalSnapshot && _finalLeaderboardSnapshot.Count > 0 
                ? _finalLeaderboardSnapshot 
                : _persistentLeaderboard;
            
            // If we're trying to show final snapshot but it's empty yet, still show the window (prevent flickering)
            if (!_showWindow) return;
            if (!_showFinalSnapshot && displayLeaderboard.Count == 0) return;

            float scale = Screen.height / 1080f;
            float edgePadding = 4f * scale;
            float headerHeight = 26f * scale;
            float baseRowHeight = 22f * scale;

            if (_windowStyle == null || Mathf.Abs(_lastOpacity - ConfigOpacity) > 0.01f)
            {
                _lastOpacity = ConfigOpacity;
                _blackTexture.SetPixel(0, 0, new Color(0, 0, 0, _lastOpacity));
                _blackTexture.Apply();

                _windowStyle = new GUIStyle(GUI.skin.window) { normal = { background = _blackTexture }, border = new RectOffset(0, 0, 0, 0) };
                
                _centeredStyle = new GUIStyle(GUI.skin.label) { 
                    fontSize = Mathf.RoundToInt(11 * scale), 
                    alignment = TextAnchor.MiddleCenter, 
                    richText = true,
                    wordWrap = false 
                };

                _headerStyle = new GUIStyle(_centeredStyle) { fontStyle = FontStyle.Bold, wordWrap = false };
                _nameStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, richText = true, wordWrap = false, fontSize = Mathf.RoundToInt(12 * scale) };
                
                _posStyle = new GUIStyle(_centeredStyle) { normal = { textColor = Color.green } };
                // Lighter, more readable red (Soft Coral)
                _negStyle = new GUIStyle(_centeredStyle) { normal = { textColor = new Color(1f, 0.45f, 0.45f) } };
            }

            // Percentage Weights
            float wRank = 0.08f;
            float wName = 0.35f;
            float wMMR = 0.26f;
            float wPoints = 0.12f;
            float wStroke = 0.09f;
            float wBase = 0.10f;

            float finalWidth = ConfigWidth * scale;
            float usableWidth = finalWidth - (edgePadding * 2);

            float rankW = usableWidth * wRank;
            float nameW = usableWidth * wName;
            float mmrW = usableWidth * wMMR;
            float ptsW = usableWidth * wPoints;
            float strokeW = usableWidth * wStroke;
            float baseW = usableWidth * wBase;

            float totalHeight = (edgePadding * 2) + (headerHeight * 2) + (displayLeaderboard.Count * baseRowHeight);
            float dynamicHeight = Mathf.Min(totalHeight, ConfigMaxHeight * scale);
            
            // Clamp window position to screen bounds
            float clampedX = Mathf.Clamp(ConfigPosX * scale, 0, Screen.width - finalWidth);
            float clampedY = Mathf.Clamp(ConfigPosY * scale, 0, Screen.height - dynamicHeight);
            
            Rect windowRect = new Rect(clampedX, clampedY, finalWidth, dynamicHeight);
            
            // Update config if position changed (manual clamping)
            if (clampedX != ConfigPosX * scale) _configPosX.Value = clampedX / scale;
            if (clampedY != ConfigPosY * scale) _configPosY.Value = clampedY / scale;

            GUI.Box(windowRect, "", _windowStyle);
            GUILayout.BeginArea(windowRect);
            
            float currentY = edgePadding;
            GUIStyle goldStyle = new GUIStyle(_centeredStyle) { normal = { textColor = new Color(1f, 0.85f, 0f) }, fontStyle = FontStyle.Bold };
            GUIStyle titleStyle = new GUIStyle(_headerStyle) { fontSize = Mathf.RoundToInt(10 * scale) };
            if (_showFinalSnapshot) {
                titleStyle.normal.textColor = new Color(1f, 0.65f, 0.2f); // Orange for Final Score
            } else {
                titleStyle.normal.textColor = new Color(0.4f, 1f, 0.4f); // Green for Live
            }

            // Draw title showing Live or Final Score
            string title = _showFinalSnapshot ? "FINAL SCORE" : "LIVE";
            GUI.Label(new Rect(edgePadding, currentY, finalWidth - (edgePadding * 2), headerHeight), title, titleStyle);
            currentY += headerHeight;

            // Draw Headers
            float hX = edgePadding;
            GUI.Label(new Rect(hX, currentY, rankW, headerHeight), "#", _headerStyle); hX += rankW;
            GUI.Label(new Rect(hX, currentY, nameW, headerHeight), "PLAYER", _headerStyle); hX += nameW;
            GUI.Label(new Rect(hX, currentY, mmrW, headerHeight), "MMR", _headerStyle); hX += mmrW;
            GUI.Label(new Rect(hX, currentY, ptsW, headerHeight), "PTS", _headerStyle); hX += ptsW;
            GUI.Label(new Rect(hX, currentY, strokeW, headerHeight), "+/-", _headerStyle); hX += strokeW;
            GUI.Label(new Rect(hX, currentY, baseW, headerHeight), "BASE", _headerStyle);

            currentY += headerHeight;

            // Draw Players (or loading message if snapshot not ready)
            if (displayLeaderboard.Count == 0 && _showFinalSnapshot) {
                // Show placeholder while snapshot is loading
                GUIStyle placeholderStyle = new GUIStyle(_centeredStyle) { normal = { textColor = Color.gray } };
                GUI.Label(new Rect(edgePadding, currentY, finalWidth - (edgePadding * 2), baseRowHeight), "Loading final scores...", placeholderStyle);
            } else {
                foreach (var p in displayLeaderboard)
                {
                    float rX = edgePadding;

                    GUI.Label(new Rect(rX, currentY, rankW, baseRowHeight), $"{displayLeaderboard.IndexOf(p) + 1}.", _centeredStyle);
                    rX += rankW;

                GUI.Label(new Rect(rX, currentY, nameW, baseRowHeight), p.Name, _nameStyle);
                rX += nameW;

                // MMR Projection Logic
                string rawMMR = p.MMR;
                string projection = p.ProjectedDisplay.Replace(rawMMR, "").Trim();
                Vector2 mmrSize = _centeredStyle.CalcSize(new GUIContent(rawMMR));
                Vector2 projSize = _centeredStyle.CalcSize(new GUIContent(projection));
                float gap = 3f * scale;
                float groupW = mmrSize.x + (string.IsNullOrEmpty(projection) ? 0 : projSize.x + gap);
                float mmrStartX = rX + (mmrW / 2f) - (groupW / 2f);

                GUI.Label(new Rect(mmrStartX, currentY, mmrSize.x, baseRowHeight), rawMMR, _centeredStyle);
                if (!string.IsNullOrEmpty(projection))
                {
                    GUIStyle colorStyle = projection.Contains("+") ? _posStyle : _negStyle;
                    GUI.Label(new Rect(mmrStartX + mmrSize.x + gap, currentY, projSize.x, baseRowHeight), projection, colorStyle);
                }
                rX += mmrW;

                GUI.Label(new Rect(rX, currentY, ptsW, baseRowHeight), $"{p.AdjustedPoints}", goldStyle);
                rX += ptsW;

                GUI.Label(new Rect(rX, currentY, strokeW, baseRowHeight), p.RawStrokes, _centeredStyle);
                rX += strokeW;

                GUI.Label(new Rect(rX, currentY, baseW, baseRowHeight), $"{p.BaseScore}", _centeredStyle);

                currentY += baseRowHeight;
                if (currentY > dynamicHeight - edgePadding) break;
                }
            }
            GUILayout.EndArea();
        }

        private void ScrapeData()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene.Contains("Driving") || scene.Contains("Range")) {
                // End of round/lobby scene: next gameplay scene should start a fresh cache.
                _roundCacheActive = false;
                return;
            }

            if (!_roundCacheActive) {
                _lastKnownRoundPlayers.Clear();
                _roundCacheActive = true;
            }

            if (_cachedScoreboard == null)
                _cachedScoreboard = Object.FindAnyObjectByType<Scoreboard>(FindObjectsInactive.Include);

            if (_cachedScoreboard == null || _entriesField == null) return;
            var entries = _entriesField.GetValue(_cachedScoreboard) as List<ScoreboardEntry>;
            if (entries == null || entries.Count == 0) return;

            List<SBGLPlayer> newList = new List<SBGLPlayer>();
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                string rawName = CleanTMP(entry.name?.text);
                if (string.IsNullOrEmpty(rawName)) continue;

                string nameLower = rawName.ToLowerInvariant();
                // Filter common spectator/observer/ui placeholder labels
                if (rawName == "Name" || nameLower.Contains("spectator") || nameLower.Contains("observer") || nameLower.Contains("spectating") || nameLower.Contains("spec ")) continue;

                string strokesStr = CleanTMP(entry.strokes?.text);
                string courseScoreStr = CleanTMP(entry.courseScore?.text);

                if (!string.IsNullOrEmpty(strokesStr) && strokesStr.ToUpper().Contains("SPEC")) continue;

                // Skip entries that look like UI placeholders (dashes) with no numeric data
                bool strokesHasDigits = Regex.IsMatch(strokesStr ?? string.Empty, @"\d");
                bool courseHasDigits = Regex.IsMatch(courseScoreStr ?? string.Empty, @"\d");
                    bool strokesOnlyDashes = !string.IsNullOrEmpty(strokesStr) && Regex.IsMatch(strokesStr, @"^[\-\u2013\u2014\s]+$");
                if (!strokesHasDigits && !courseHasDigits && strokesOnlyDashes) continue;

                int.TryParse(courseScoreStr, out int baseScore);
                int.TryParse(strokesStr.Replace("±", "").Replace("+", "").Trim(), out int strokeOffset);
                
                string playerMMR = "...";
                if (_mmrCache.TryGetValue(rawName, out var cachedData))
                {
                    playerMMR = cachedData.mmr;
                    if (Time.time - cachedData.lastFetchTime > 300f && !_pendingRequests.Contains(rawName))
                        StartCoroutine(GetMMRForPlayer(rawName));
                }
                else if (!_pendingRequests.Contains(rawName))
                {
                    StartCoroutine(GetMMRForPlayer(rawName));
                }

                newList.Add(new SBGLPlayer {
                    Name = rawName, 
                    BaseScore = baseScore, 
                    RawStrokes = strokesStr,
                    AdjustedPoints = baseScore + (strokeOffset * -10),
                    MMR = playerMMR,
                    ProjectedDisplay = playerMMR 
                });
            }

            HashSet<string> liveNames = new HashSet<string>(newList.Select(p => p.Name), System.StringComparer.OrdinalIgnoreCase);

            // Keep disconnected players visible with the score they had when they left.
            foreach (var kvp in _lastKnownRoundPlayers) {
                if (!liveNames.Contains(kvp.Key) && kvp.Value != null) {
                    newList.Add(ClonePlayer(kvp.Value));
                }
            }

            if (newList.Count > 1) CalculateProjectedMMR(newList);
            _persistentLeaderboard = newList.OrderByDescending(p => p.AdjustedPoints).Take(ConfigMaxPlayers).ToList();

            _lastKnownRoundPlayers.Clear();
            foreach (var player in _persistentLeaderboard) {
                if (player == null || string.IsNullOrEmpty(player.Name)) continue;
                _lastKnownRoundPlayers[player.Name] = ClonePlayer(player);
            }
        }

        private SBGLPlayer ClonePlayer(SBGLPlayer source)
        {
            if (source == null) return null;
            return new SBGLPlayer {
                Name = source.Name,
                BaseScore = source.BaseScore,
                AdjustedPoints = source.AdjustedPoints,
                RawStrokes = source.RawStrokes,
                MMR = source.MMR,
                ProjectedDisplay = source.ProjectedDisplay
            };
        }

        private void CalculateProjectedMMR(List<SBGLPlayer> players)
        {
            Dictionary<string, float> deltas = new Dictionary<string, float>();
            foreach (var p in players) deltas[p.Name] = 0f;

            for (int i = 0; i < players.Count; i++)
            {
                for (int j = i + 1; j < players.Count; j++)
                {
                    var pA = players[i]; var pB = players[j];
                    if (float.TryParse(pA.MMR, out float mmrA) && float.TryParse(pB.MMR, out float mmrB))
                    {
                        float expectedA = 1f / (1f + Mathf.Pow(10f, (mmrB - mmrA) / 400f));
                        float actualA = (pA.AdjustedPoints > pB.AdjustedPoints) ? 1.0f : (pA.AdjustedPoints < pB.AdjustedPoints ? 0.0f : 0.5f);
                        float change = K_FACTOR * (actualA - expectedA);
                        deltas[pA.Name] += change;
                        deltas[pB.Name] -= change;
                    }
                }
            }

            foreach (var p in players)
            {
                if (float.TryParse(p.MMR, out float _))
                {
                    int d = Mathf.RoundToInt(deltas[p.Name]);
                    p.ProjectedDisplay = $"{p.MMR} ({(d >= 0 ? "+" : "")}{d})";
                }
            }
        }

        IEnumerator GetMMRForPlayer(string originalName)
        {
            if (_pendingRequests.Contains(originalName)) yield break;
            _pendingRequests.Add(originalName);

            // Replace BASE_API with the accessor from your UnifiedPlugin
            string query = "{\"display_name\":{\"$regex\":\"^" + originalName + "$\",\"$options\":\"i\"}}";
            string url = $"{SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentPlayerApi()}?q={UnityWebRequest.EscapeURL(query)}";
            
            bool needsFallback = false;
            using (UnityWebRequest r = UnityWebRequest.Get(url)) {
                // Replace APP_ID and AUTH_TOKEN with UnifiedPlugin accessors
                r.SetRequestHeader("X-App-Id", SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAppId());
                r.SetRequestHeader("api_key", SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAuthToken());
                
                yield return r.SendWebRequest();

                if (r.result == UnityWebRequest.Result.Success) {
                    var res = JArray.Parse(r.downloadHandler.text);
                    if (res.Count > 0) _mmrCache[originalName] = (res[0]["current_mmr"]?.ToString() ?? "--", Time.time);
                    else needsFallback = true;
                } else needsFallback = true;
            }

            if (needsFallback) yield return GetMMRFuzzyFallback(originalName);
            _pendingRequests.Remove(originalName);
        }

        // Update these lines in GetMMRFuzzyFallback
        IEnumerator GetMMRFuzzyFallback(string originalName)
        {
            string query = "{\"display_name\":{\"$regex\":\"" + originalName + "\",\"$options\":\"i\"}}";
            // Replace BASE_API here as well
            string url = $"{SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentPlayerApi()}?q={UnityWebRequest.EscapeURL(query)}";
            
            using (UnityWebRequest r = UnityWebRequest.Get(url)) {
                // Replace APP_ID and AUTH_TOKEN here as well
                r.SetRequestHeader("X-App-Id", SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAppId());
                r.SetRequestHeader("api_key", SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAuthToken());
                
                yield return r.SendWebRequest();
                string val = "--";
                if (r.result == UnityWebRequest.Result.Success) {
                    var res = JArray.Parse(r.downloadHandler.text);
                    if (res.Count > 0) val = res[0]["current_mmr"]?.ToString() ?? "--";
                }
                _mmrCache[originalName] = (val, Time.time);
            }
        }

        private string CleanTMP(string input) => string.IsNullOrEmpty(input) ? "" : Regex.Replace(input, "<.*?>", string.Empty).Trim();

        /// <summary>
        /// Public method to retrieve a player's current leaderboard stats for match submission.
        /// Used by MatchmakingAssistant to collect game_points and score_vs_par.
        /// </summary>
        public SBGLPlayer GetPlayerLeaderboardData(string playerName)
        {
            return _persistentLeaderboard.FirstOrDefault(p => p.Name.Equals(playerName, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get all current leaderboard players.
        /// </summary>
        public List<SBGLPlayer> GetCurrentLeaderboard()
        {
            return new List<SBGLPlayer>(_persistentLeaderboard);
        }

        /// <summary>
        /// Reset internal leaderboard state when returning to the main menu.
        /// Clears persistent and final snapshots, caches, and pending requests.
        /// </summary>
        private void ResetForMainMenu()
        {
            if (_persistentLeaderboard.Count == 0 && _finalLeaderboardSnapshot.Count == 0 && _mmrCache.Count == 0) return;

            _persistentLeaderboard.Clear();
            _finalLeaderboardSnapshot.Clear();
            _showFinalSnapshot = false;
            _lastKnownRoundPlayers.Clear();
            _roundCacheActive = false;
            _mmrCache.Clear();
            _pendingRequests.Clear();
            _cachedScoreboard = null;
            _nextUpdateTime = Time.time + _updateInterval;
            Debug.Log("[LiveLeaderboard] Reset leaderboard state on main menu");
        }

        /// <summary>
        /// Captures the current leaderboard state as a final snapshot before leaving gameplay.
        /// Call this when transitioning away from a gameplay scene to preserve match-end scores.
        /// </summary>
        public void CaptureLeaderboardSnapshot()
        {
            _finalLeaderboardSnapshot = _persistentLeaderboard.Select(ClonePlayer).Where(p => p != null).ToList();
            Debug.Log($"[LiveLeaderboard] Captured final snapshot: {_finalLeaderboardSnapshot.Count} players");
        }

        /// <summary>
        /// Retrieves the final leaderboard snapshot from when the match ended.
        /// Falls back to current leaderboard if snapshot is empty.
        /// </summary>
        public List<SBGLPlayer> GetFinalLeaderboardSnapshot()
        {
            if (_finalLeaderboardSnapshot.Count > 0) {
                return _finalLeaderboardSnapshot.Select(ClonePlayer).Where(p => p != null).ToList();
            }
            return _persistentLeaderboard.Select(ClonePlayer).Where(p => p != null).ToList();
        }
    }
}