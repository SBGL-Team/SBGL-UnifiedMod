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
using Mirror;
using UnityEngine.Localization;

namespace SBGLLiveLeaderboard
{
    public class LiveLeaderboardPlugin : MonoBehaviour
    {
        public static LiveLeaderboardPlugin Instance { get; private set; }

        // Config entry references (injected by UnifiedPlugin)
        private ConfigEntry<int> _configMaxPlayers;
        private ConfigEntry<Key> _configViewToggleKey;

        private const float K_FACTOR = 24f;

        // Config helpers using ConfigEntry references
        private int ConfigMaxPlayers { get => _configMaxPlayers?.Value ?? PlayerPrefs.GetInt("LL_MaxPlayers", 16); }
        private Key ViewToggleKey    { get => _configViewToggleKey?.Value ?? Key.F8; }

        // Set config entries from parent plugin
        public void SetConfig(ConfigEntry<int> maxPlayers, ConfigEntry<Key> viewToggleKey = null)
        {
            _configMaxPlayers    = maxPlayers;
            _configViewToggleKey = viewToggleKey;
        }

        private float _updateInterval = 30f; // Slow background poll — only needed for MMR refresh; data comes from Scoreboard.Refresh() hook
        private float _nextUpdateTime = 0f;

        private List<SBGLPlayer> _persistentLeaderboard = new List<SBGLPlayer>();
        private List<SBGLPlayer> _finalLeaderboardSnapshot = new List<SBGLPlayer>(); // Store final scores when leaving gameplay
        private bool _showFinalSnapshot = false; // True when in driving range to freeze display
        private readonly Dictionary<string, SBGLPlayer> _lastKnownRoundPlayers = new Dictionary<string, SBGLPlayer>(System.StringComparer.OrdinalIgnoreCase);
        private bool _roundCacheActive = false;
        private bool _isInSbglGameplay = false;
        private bool _cachedInMenu = false;
        private bool _cachedInDrivingRange = false;
        private string _cachedSceneName = string.Empty;
        private int _cachedTotalHoles = 9;
        private string _cachedCourseName = "";
        private int _cachedHoleGlobalIndex = -1;
        // Set by LeaderboardPatches when the native scoreboard would open in an SBGL lobby
        public static bool SbglOverlayOpen = false;

        // When true, the native in-game scoreboard is shown instead of the mod overlay (default: true)
        public static bool UseNativeScoreboard
        {
            get => _useNativeScoreboard;
            set { _useNativeScoreboard = value; PlayerPrefs.SetInt("LL_UseNative", value ? 1 : 0); }
        }
        private static bool _useNativeScoreboard = PlayerPrefs.GetInt("LL_UseNative", 1) == 1;
        private Dictionary<string, (string mmr, float lastFetchTime)> _mmrCache = new Dictionary<string, (string, float)>();
        private HashSet<string> _pendingRequests = new HashSet<string>();
        // Clan tags keyed by scoreboard display name. Populated as a follow-up to the MMR lookup,
        // which is what resolves a scoreboard name to an SBGL player id in the first place.
        private Dictionary<string, string> _clanTagCache = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        // Registered display_name keyed by scoreboard name, so the overlay can present the casing
        // the player actually registered even though lookups are case-insensitive.
        private Dictionary<string, string> _canonicalNameCache = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        private FieldInfo _entriesField;
        private Scoreboard _cachedScoreboard;
        private GUIStyle _centeredStyle, _headerStyle, _nameStyle, _posStyle, _negStyle, _solidStyle, _toggleStyle;
        private Texture2D _whiteTexture, _toggleTex, _toggleHoverTex;


        public class SBGLPlayer {
            public string Name;
            public int BaseScore;
            public string RawStrokes;
            public string MMR = "...";
            public string ProjectedDisplay = "...";
            public bool IsSpectator;
            public string ClanTag = ""; // faction clan_tag, shown before the name

            /// <summary>
            /// The registered display_name from the site, which may differ in case from the
            /// in-game Name (e.g. "JaBob" vs "JaBoB"). Presentation only — Name stays the key
            /// used for score lookups, so it must not be overwritten with this.
            /// </summary>
            public string CanonicalName = "";

            /// <summary>Registered casing when known, otherwise the name as the game renders it.</summary>
            public string PresentationName =>
                string.IsNullOrWhiteSpace(CanonicalName) ? Name : CanonicalName;

            /// <summary>Name prefixed with the clan tag when the player belongs to a faction.</summary>
            public string DisplayNameWithTag =>
                string.IsNullOrWhiteSpace(ClanTag) ? PresentationName : $"[{ClanTag.ToUpper()}] {PresentationName}";
        }

        void Awake()
        {
            Instance = this;

            if (!PlayerPrefs.HasKey("LL_MaxPlayers")) PlayerPrefs.SetInt("LL_MaxPlayers", 16);

            _entriesField = typeof(Scoreboard).GetField("entries", BindingFlags.NonPublic | BindingFlags.Instance);
            _whiteTexture = new Texture2D(1, 1);
            _whiteTexture.SetPixel(0, 0, Color.white);
            _whiteTexture.Apply();

            SBGL.UnifiedMod.Core.UnifiedPlugin.ApiConfigChanged += OnApiConfigChanged;

            UnityEngine.Object.DontDestroyOnLoad(this.gameObject);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void OnApiConfigChanged()
        {
            // Clear cache and force immediate resync when API changes
            _mmrCache.Clear();
            _clanTagCache.Clear();
            _canonicalNameCache.Clear();
            _pendingRequests.Clear();
            _nextUpdateTime = Time.time;
        }

        void Update()
        {
            // Configurable key toggles between SBGL leaderboard and native scoreboard
            var kb = Keyboard.current;
            if (kb != null && kb[ViewToggleKey].wasPressedThisFrame)
                UseNativeScoreboard = !UseNativeScoreboard;

            // Cache scene name — only recompute when it actually changes
            string currentScene = SceneManager.GetActiveScene().name ?? string.Empty;
            if (currentScene != _cachedSceneName)
            {
                _cachedSceneName = currentScene;
                string lower = currentScene.ToLowerInvariant();
                _cachedInMenu = lower.Contains("menu");
                _cachedInDrivingRange = lower.Contains("driving") || lower.Contains("range");
            }
            bool inDrivingRange = _cachedInDrivingRange;
            bool inMenuScene = _cachedInMenu;

            if (inDrivingRange) {
                _showFinalSnapshot = true;  // Freeze on final snapshot
            } else {
                _showFinalSnapshot = false; // Go back to live updates
            }

            // Reset all leaderboard state when entering the main menu to avoid stale data
            if (inMenuScene) {
                ResetForMainMenu();
            }

            // Safety poll: the ForceDisplayScoreboardChanged event can fire before the new scene's
            // Scoreboard subscribes, causing SbglOverlayOpen to get stuck true. Clear it whenever
            // neither the native scoreboard nor ForceDisplayScoreboard actually wants it open.
            if (SbglOverlayOpen && !CourseManager.ForceDisplayScoreboard && !Scoreboard.IsVisible)
                SbglOverlayOpen = false;

            _isInSbglGameplay = !inMenuScene && SbglOverlayOpen;

            if (_isInSbglGameplay)
            {
                var matchSetupMenu = SingletonNetworkBehaviour<MatchSetupMenu>.HasInstance
                    ? SingletonNetworkBehaviour<MatchSetupMenu>.Instance : null;
                if (matchSetupMenu != null)
                    _cachedTotalHoles = matchSetupMenu.randomCupNumHoles;

                int globalIndex = CourseManager.CurrentHoleGlobalIndex;
                if (globalIndex != _cachedHoleGlobalIndex)
                {
                    _cachedHoleGlobalIndex = globalIndex;
                    try
                    {
                        var locName = CourseManager.GetCurrentHoleLocalizedName();
                        _cachedCourseName = locName != null ? locName.GetLocalizedString() : "";
                    }
                    catch { _cachedCourseName = ""; }
                }
            }

            if (Time.time >= _nextUpdateTime)
            {
                ScrapeData();
                _nextUpdateTime = Time.time + _updateInterval;
            }
        }

        void OnGUI()
        {
            float scale = Screen.height / 1080f;

            if (_solidStyle == null)
            {
                _centeredStyle = new GUIStyle(GUI.skin.label) {
                    fontSize = Mathf.RoundToInt(11 * scale),
                    alignment = TextAnchor.MiddleCenter,
                    richText = true,
                    wordWrap = false
                };

                _headerStyle = new GUIStyle(_centeredStyle) { fontStyle = FontStyle.Bold, wordWrap = false };
                _nameStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, richText = true, wordWrap = false, fontSize = Mathf.RoundToInt(12 * scale) };

                _posStyle = new GUIStyle(_centeredStyle) { normal = { textColor = Color.green } };
                _negStyle = new GUIStyle(_centeredStyle) { normal = { textColor = new Color(1f, 0.45f, 0.45f) } };
                _solidStyle = new GUIStyle { normal = { background = _whiteTexture }, border = new RectOffset(0, 0, 0, 0) };

                _toggleTex = MakeTex(new Color(0.10f, 0.36f, 0.58f, 0.88f));
                _toggleHoverTex = MakeTex(new Color(0.14f, 0.46f, 0.70f, 0.95f));
                _toggleStyle = new GUIStyle(GUI.skin.button)
                {
                    normal   = { background = _toggleTex,      textColor = Color.white },
                    hover    = { background = _toggleHoverTex, textColor = Color.white },
                    active   = { background = _toggleHoverTex, textColor = Color.white },
                    focused  = { background = _toggleTex,      textColor = Color.white },
                    fontSize  = Mathf.RoundToInt(10f * scale),
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    border    = new RectOffset(2, 2, 2, 2)
                };
            }

            // Native mode: show a small hint label so players know how to switch back
            if (UseNativeScoreboard && Scoreboard.IsVisible)
            {
                float lblW = 160f * scale;
                float lblH = 20f * scale;
                GUI.Label(new Rect((Screen.width - lblW) / 2f, Screen.height * 0.055f, lblW, lblH),
                    $"[{ViewToggleKey}] Switch to SBGL View", _toggleStyle);
                return;
            }

            if (!_isInSbglGameplay) return;

            List<SBGLPlayer> displayLeaderboard = _showFinalSnapshot && _finalLeaderboardSnapshot.Count > 0
                ? _finalLeaderboardSnapshot
                : _persistentLeaderboard;

            float edgePadding = 4f * scale;
            float headerHeight = 26f * scale;
            float baseRowHeight = 22f * scale;

            DrawReplacementLeaderboard(displayLeaderboard, scale, edgePadding, headerHeight, baseRowHeight);
        }

        private static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, col);
            t.Apply();
            return t;
        }

        private void DrawReplacementLeaderboard(List<SBGLPlayer> players, float scale, float edgePad, float headerH, float rowH)
        {
            if (_solidStyle == null) return;

            string lobbyName = SBGL.UnifiedMod.Features.CompetitivePluginCheck.CompetitivePluginCheck._currentLobbyName ?? "";
            string matchType = GetMatchTypeDisplayName(PlayerPrefs.GetString("MatchType", ""));
            string matchId = SBGLeagueAutomation.SBGLPlugin.CurrentMatchId ?? "";
            bool isFinal = _showFinalSnapshot;

            // Dimensions — all relative to screen size so any resolution works
            float panelW = Screen.width * 0.52f;
            float accentH = 5f * scale;          // top green stripe
            float titleH = 30f * scale;           // lobby / match info row
            float colH = 22f * scale;             // column header row
            float pRowH = 26f * scale;            // player row
            float leftStripW = 4f * scale;        // rank-tier colour strip
            float pad = 10f * scale;

            int playerCount = Mathf.Max(players.Count, 1);
            float footH = Mathf.Max(pad * 1.8f, 18f * scale);
            float panelH = accentH + titleH + colH + playerCount * pRowH + footH;
            float panelX = (Screen.width - panelW) / 2f;
            float panelY = Screen.height * 0.055f;

            var saved = GUI.backgroundColor;

            // ── Cream background ─────────────────────────────────────────
            ColorUtility.TryParseHtmlString("#FEFAEE", out Color cream);
            GUI.backgroundColor = cream;
            GUI.Box(new Rect(panelX, panelY, panelW, panelH), GUIContent.none, _solidStyle);

            // ── Top SBGL-green accent stripe ──────────────────────────────
            ColorUtility.TryParseHtmlString("#1ABD60", out Color sbglGreen);
            GUI.backgroundColor = sbglGreen;
            GUI.Box(new Rect(panelX, panelY, panelW, accentH), GUIContent.none, _solidStyle);

            GUI.backgroundColor = saved;
            float y = panelY + accentH;

            // ── Title row: "SBGL SEASON 2" left · lobby name center · type right ──
            Color dark = new Color(0.12f, 0.12f, 0.14f);
            Color mid  = new Color(0.35f, 0.35f, 0.38f);
            Color greenText = sbglGreen;

            GUIStyle titleLeftStyle = new GUIStyle(_headerStyle) {
                fontSize = Mathf.RoundToInt(13f * scale),
                alignment = TextAnchor.MiddleLeft
            };
            titleLeftStyle.normal.textColor = greenText;

            GUIStyle titleCenterStyle = new GUIStyle(_headerStyle) {
                fontSize = Mathf.RoundToInt(14f * scale),
                alignment = TextAnchor.MiddleCenter
            };
            titleCenterStyle.normal.textColor = dark;

            GUIStyle titleRightStyle = new GUIStyle(_headerStyle) {
                fontSize = Mathf.RoundToInt(11f * scale),
                alignment = TextAnchor.MiddleRight
            };
            titleRightStyle.normal.textColor = isFinal ? new Color(0.80f, 0.45f, 0.05f) : mid;

            GUI.Label(new Rect(panelX + pad, y, panelW * 0.32f, titleH), "SBGL SEASON 2", titleLeftStyle);
            GUI.Label(new Rect(panelX, y, panelW, titleH), lobbyName, titleCenterStyle);
            int currentHole = CourseManager.CurrentHoleCourseIndex + 1;
            string holeStr = currentHole > 0 ? $"{currentHole}/{_cachedTotalHoles}" : "";
            string rightLabel = matchType
                + (string.IsNullOrEmpty(_cachedCourseName) ? "" : $"  ·  {_cachedCourseName}")
                + (string.IsNullOrEmpty(holeStr) ? "" : $"  ·  {holeStr}")
                + (isFinal ? "  ·  FINAL" : "  ·  LIVE");
            GUI.Label(new Rect(panelX, y, panelW - pad, titleH), rightLabel, titleRightStyle);
            y += titleH;

            // ── Thin divider under title ──────────────────────────────────
            GUI.backgroundColor = new Color(0.78f, 0.74f, 0.68f, 1f);
            GUI.Box(new Rect(panelX + pad, y - 1f, panelW - pad * 2f, 1f), GUIContent.none, _solidStyle);
            GUI.backgroundColor = saved;

            // ── Column headers ────────────────────────────────────────────
            float usable = panelW - leftStripW - pad * 2f;
            float cPos     = usable * 0.07f;
            float cName    = usable * 0.37f;
            float cMMR     = usable * 0.28f;
            float cPts     = usable * 0.12f;
            float cStrokes = usable * 0.16f;

            GUIStyle colHStyle = new GUIStyle(_headerStyle) {
                fontSize = Mathf.RoundToInt(9f * scale),
                alignment = TextAnchor.MiddleCenter
            };
            colHStyle.normal.textColor = mid;

            GUIStyle colHNameStyle = new GUIStyle(colHStyle) { alignment = TextAnchor.MiddleLeft };

            float cx = panelX + leftStripW + pad;
            GUI.Label(new Rect(cx, y, cPos, colH), "POS", colHStyle);   cx += cPos;
            GUI.Label(new Rect(cx, y, cName, colH), "PLAYER", colHNameStyle); cx += cName;
            GUI.Label(new Rect(cx, y, cMMR, colH), "MMR", colHStyle);   cx += cMMR;
            GUI.Label(new Rect(cx, y, cPts, colH), "PTS", colHStyle);    cx += cPts;
            GUI.Label(new Rect(cx, y, cStrokes, colH), "+/-", colHStyle);
            y += colH;

            // ── Thin divider under column headers ─────────────────────────
            GUI.backgroundColor = new Color(0.78f, 0.74f, 0.68f, 1f);
            GUI.Box(new Rect(panelX + pad, y, panelW - pad * 2f, 1f), GUIContent.none, _solidStyle);
            GUI.backgroundColor = saved;

            // ── Player rows ───────────────────────────────────────────────
            GUIStyle nameStyle = new GUIStyle(_nameStyle) {
                fontSize = Mathf.RoundToInt(13f * scale)
            };
            nameStyle.normal.textColor = dark;

            GUIStyle dataStyle = new GUIStyle(_centeredStyle) {
                fontSize = Mathf.RoundToInt(12f * scale)
            };
            dataStyle.normal.textColor = dark;

            if (players.Count == 0)
            {
                GUIStyle emptyStyle = new GUIStyle(dataStyle);
                emptyStyle.normal.textColor = mid;
                GUI.Label(new Rect(panelX + pad, y, panelW - pad * 2f, pRowH), "Waiting for player data...", emptyStyle);
                return;
            }

            int rankPos = 0; // counts only active (non-spectator) players for position numbers
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];

                if (!p.IsSpectator) rankPos++;

                // Row tint: gold for leader, subtle alternating for active players; dim for spectators
                if (p.IsSpectator)
                {
                    GUI.backgroundColor = new Color(0f, 0f, 0f, 0.06f);
                    GUI.Box(new Rect(panelX, y, panelW, pRowH), GUIContent.none, _solidStyle);
                }
                else if (rankPos == 1)
                {
                    GUI.backgroundColor = new Color(1f, 0.92f, 0.55f, 0.35f);
                    GUI.Box(new Rect(panelX, y, panelW, pRowH), GUIContent.none, _solidStyle);
                }
                else if (rankPos % 2 == 0)
                {
                    GUI.backgroundColor = new Color(0f, 0f, 0f, 0.04f);
                    GUI.Box(new Rect(panelX, y, panelW, pRowH), GUIContent.none, _solidStyle);
                }
                GUI.backgroundColor = saved;

                // Left rank-tier colour strip — grey for spectators
                float mmrVal = 0f;
                float.TryParse(p.MMR, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mmrVal);
                string rankHex = p.IsSpectator ? "#555555" : (p.MMR == "..." || p.MMR == "--") ? "#888888" : SBGL.UnifiedMod.Core.Season2RuleSet.GetRankColor(mmrVal);
                if (ColorUtility.TryParseHtmlString(rankHex, out Color stripColor))
                {
                    GUI.backgroundColor = stripColor;
                    GUI.Box(new Rect(panelX, y, leftStripW, pRowH), GUIContent.none, _solidStyle);
                    GUI.backgroundColor = saved;
                }

                cx = panelX + leftStripW + pad;

                GUIStyle specDimStyle = new GUIStyle(dataStyle);
                specDimStyle.normal.textColor = mid;

                if (p.IsSpectator)
                {
                    // Spectator row: "SPEC" badge in position column, name, then blank score cells
                    GUIStyle specBadge = new GUIStyle(dataStyle) { fontStyle = FontStyle.Bold };
                    specBadge.normal.textColor = new Color(0.55f, 0.55f, 0.60f);
                    GUI.Label(new Rect(cx, y, cPos, pRowH), "👁", specBadge); cx += cPos;
                    GUI.Label(new Rect(cx, y, cName, pRowH), p.DisplayNameWithTag, specDimStyle); cx += cName;
                    GUI.Label(new Rect(cx, y, cMMR + cPts + cStrokes, pRowH), "SPECTATING", specDimStyle);
                    y += pRowH;
                    continue;
                }

                // Position number
                GUIStyle posStyle = new GUIStyle(dataStyle) { fontStyle = rankPos == 1 ? FontStyle.Bold : FontStyle.Normal };
                if (rankPos == 1) posStyle.normal.textColor = new Color(0.60f, 0.45f, 0.02f);
                GUI.Label(new Rect(cx, y, cPos, pRowH), $"{rankPos}", posStyle); cx += cPos;

                // Player name (prefixed with the clan tag when they belong to a faction)
                GUI.Label(new Rect(cx, y, cName, pRowH), p.DisplayNameWithTag, nameStyle); cx += cName;

                // MMR (rank-colored) + projected change centred in column
                string rawMMR = p.MMR;
                string proj   = p.ProjectedDisplay.Replace(rawMMR, "").Trim();
                GUIStyle mmrS = new GUIStyle(dataStyle);
                if (ColorUtility.TryParseHtmlString(rankHex, out Color rc)) mmrS.normal.textColor = rc;

                Vector2 mmrSz  = mmrS.CalcSize(new GUIContent(rawMMR));
                Vector2 projSz = _centeredStyle.CalcSize(new GUIContent(proj));
                float gap      = 3f * scale;
                float groupW   = mmrSz.x + (string.IsNullOrEmpty(proj) ? 0f : projSz.x + gap);
                float mmrX     = cx + (cMMR / 2f) - (groupW / 2f);
                GUI.Label(new Rect(mmrX, y, mmrSz.x, pRowH), rawMMR, mmrS);
                if (!string.IsNullOrEmpty(proj))
                    GUI.Label(new Rect(mmrX + mmrSz.x + gap, y, projSz.x, pRowH),
                        proj, proj.Contains("+") ? _posStyle : _negStyle);
                cx += cMMR;

                // Points (gold)
                GUIStyle ptsS = new GUIStyle(dataStyle) { fontStyle = FontStyle.Bold };
                ptsS.normal.textColor = new Color(0.72f, 0.52f, 0.02f);
                GUI.Label(new Rect(cx, y, cPts, pRowH), $"{p.BaseScore}", ptsS); cx += cPts;

                // Strokes vs par — single column: red if over, green if under, grey if even
                int strokeVal = 0;
                if (!string.IsNullOrEmpty(p.RawStrokes))
                    int.TryParse(p.RawStrokes.Replace("±", "").Replace("+", "").Trim(), out strokeVal);

                GUIStyle strokeS = new GUIStyle(dataStyle);
                strokeS.normal.textColor = strokeVal > 0 ? new Color(0.80f, 0.15f, 0.15f)
                                         : strokeVal < 0 ? new Color(0.10f, 0.60f, 0.20f)
                                         : mid;
                string strokeLabel = strokeVal > 0 ? $"+{strokeVal}" : strokeVal < 0 ? strokeVal.ToString() : "E";
                GUI.Label(new Rect(cx, y, cStrokes, pRowH), strokeLabel, strokeS);

                y += pRowH;
            }

            // ── Footer row: site left, match ID right ─────────────────────
            GUIStyle footStyle = new GUIStyle(_centeredStyle) {
                fontSize = Mathf.RoundToInt(11f * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            footStyle.normal.textColor = new Color(0.18f, 0.85f, 0.32f);
            GUI.Label(new Rect(panelX + pad, y, panelW * 0.45f, footH), "SBGLeague.com", footStyle);

            GUIStyle footRightStyle = new GUIStyle(_centeredStyle) {
                fontSize = Mathf.RoundToInt(10f * scale),
                alignment = TextAnchor.MiddleRight
            };
            footRightStyle.normal.textColor = new Color(0.45f, 0.50f, 0.60f);

            string footRight = string.IsNullOrWhiteSpace(matchId)
                ? $"[{ViewToggleKey}] Native View"
                : $"[{ViewToggleKey}] Native View  ·  Match: {(matchId.Length > 14 ? matchId.Substring(matchId.Length - 14) : matchId)}";
            GUI.Label(new Rect(panelX, y, panelW - pad * 0.5f, footH), footRight, footRightStyle);
        }

        private string GetMatchTypeDisplayName(string matchType)
        {
            if (SBGL.UnifiedMod.Core.Season2RuleSet.IsTeamMatchType(matchType))
            {
                int size = SBGL.UnifiedMod.Core.Season2RuleSet.GetTeamSize(matchType);
                return $"Ranked {size}v{size}";
            }
            if (SBGL.UnifiedMod.Core.Season2RuleSet.IsRankedMatchType(matchType))     return "Ranked";
            if (SBGL.UnifiedMod.Core.Season2RuleSet.IsProSeriesMatchType(matchType))  return "Pro Series";
            if (SBGL.UnifiedMod.Core.Season2RuleSet.IsCasualMatchType(matchType))     return "Casual";
            return "Unknown";
        }

        /// <summary>
        /// Called by the Scoreboard.Refresh() Harmony postfix — fires at the same instant
        /// the official scoreboard updates, giving us zero-delay sync with the game's own data.
        /// </summary>
        public void OnScoreboardRefreshed(Scoreboard scoreboard)
        {
            if (scoreboard == null) return;
            _cachedScoreboard = scoreboard;
            ScrapeData(scoreboard);
        }

        private void ScrapeData(Scoreboard inScoreboard = null)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene.Contains("Driving") || scene.Contains("Range")) {
                _roundCacheActive = false;
                return;
            }

            if (!_roundCacheActive) {
                _lastKnownRoundPlayers.Clear();
                _roundCacheActive = true;
            }

            Scoreboard board = inScoreboard ?? _cachedScoreboard
                ?? Object.FindAnyObjectByType<Scoreboard>(FindObjectsInactive.Include);
            if (board != null) _cachedScoreboard = board;

            if (_cachedScoreboard == null || _entriesField == null) return;
            var entries = _entriesField.GetValue(_cachedScoreboard) as List<ScoreboardEntry>;
            if (entries == null || entries.Count == 0) return;

            List<SBGLPlayer> newList = new List<SBGLPlayer>();
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                string rawName = CleanTMP(entry.name?.text);
                if (string.IsNullOrEmpty(rawName) || rawName == "Name") continue;

                string strokesStr     = CleanTMP(entry.strokes?.text);
                string courseScoreStr = CleanTMP(entry.courseScore?.text);

                // Detect spectators by the game's own "SPEC" marker; keep them rather than discard
                bool isSpec = !string.IsNullOrEmpty(strokesStr) && strokesStr.ToUpper().Contains("SPEC");

                // Skip pure UI placeholder rows (only dashes, no digits) unless spec entry
                if (!isSpec) {
                    bool strokesHasDigits  = Regex.IsMatch(strokesStr ?? string.Empty, @"\d");
                    bool courseHasDigits   = Regex.IsMatch(courseScoreStr ?? string.Empty, @"\d");
                    bool strokesOnlyDashes = !string.IsNullOrEmpty(strokesStr) && Regex.IsMatch(strokesStr, @"^[\-\u2013\u2014\s]+$");
                    if (!strokesHasDigits && !courseHasDigits && strokesOnlyDashes) continue;
                }

                int.TryParse(courseScoreStr, out int baseScore);

                string playerMMR = "...";
                if (_mmrCache.TryGetValue(rawName, out var cachedData)) {
                    playerMMR = cachedData.mmr;
                    if (Time.time - cachedData.lastFetchTime > 300f && !_pendingRequests.Contains(rawName))
                        StartCoroutine(GetMMRForPlayer(rawName));
                } else if (!_pendingRequests.Contains(rawName)) {
                    StartCoroutine(GetMMRForPlayer(rawName));
                }

                _clanTagCache.TryGetValue(rawName, out string clanTag);
                _canonicalNameCache.TryGetValue(rawName, out string canonicalName);

                newList.Add(new SBGLPlayer {
                    Name = rawName,
                    BaseScore = baseScore,
                    RawStrokes = isSpec ? "SPEC" : strokesStr,
                    MMR = playerMMR,
                    ProjectedDisplay = playerMMR,
                    IsSpectator = isSpec,
                    ClanTag = clanTag ?? "",
                    CanonicalName = canonicalName ?? ""
                });
            }

            // Keep disconnected players visible with their last known score
            HashSet<string> liveNames = new HashSet<string>(newList.Select(p => p.Name), System.StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _lastKnownRoundPlayers) {
                if (!liveNames.Contains(kvp.Key) && kvp.Value != null)
                    newList.Add(ClonePlayer(kvp.Value));
            }

            var activePlayers = newList.Where(p => !p.IsSpectator).ToList();
            var spectators    = newList.Where(p =>  p.IsSpectator).ToList();

            if (activePlayers.Count > 1) CalculateProjectedMMR(activePlayers);

            _persistentLeaderboard = activePlayers
                .OrderByDescending(p => p.BaseScore)
                .Take(ConfigMaxPlayers)
                .Concat(spectators)
                .ToList();

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
                RawStrokes = source.RawStrokes,
                MMR = source.MMR,
                ProjectedDisplay = source.ProjectedDisplay,
                IsSpectator = source.IsSpectator,
                ClanTag = source.ClanTag,
                CanonicalName = source.CanonicalName
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
                        float actualA = (pA.BaseScore > pB.BaseScore) ? 1.0f : (pA.BaseScore < pB.BaseScore ? 0.0f : 0.5f);
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

            string url = $"{SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentPlayerApi()}?display_name=ilike.{UnityWebRequest.EscapeURL(originalName)}";

            bool needsFallback = false;
            string resolvedId = null;
            using (UnityWebRequest r = UnityWebRequest.Get(url)) {
                r.SetRequestHeader("apikey", SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAuthToken());
                r.SetRequestHeader("Authorization", $"Bearer {SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAuthToken()}");
                
                yield return r.SendWebRequest();

                if (r.result == UnityWebRequest.Result.Success) {
                    var res = JArray.Parse(r.downloadHandler.text);
                    if (res.Count > 0) {
                        _mmrCache[originalName] = (res[0]["current_mmr"]?.ToString() ?? "--", Time.time);
                        resolvedId = res[0]["id"]?.ToString();
                        CacheCanonicalName(originalName, res[0]["display_name"]?.ToString());
                    }
                    else needsFallback = true;
                } else needsFallback = true;
            }

            if (needsFallback) yield return GetMMRFuzzyFallback(originalName, (id) => resolvedId = id);

            if (!string.IsNullOrEmpty(resolvedId))
                yield return GetClanTagForPlayer(originalName, resolvedId);

            _pendingRequests.Remove(originalName);
        }

        /// <summary>
        /// Records the registered casing for a scoreboard name. Only accepted when the two differ
        /// by case alone — the fuzzy fallback can match a different player entirely (searching
        /// "JaBoB" also turns up "Jabobus.o7"), and relabelling someone with another player's
        /// name would be worse than just showing the in-game casing.
        /// </summary>
        private void CacheCanonicalName(string scrapedName, string registeredName)
        {
            if (string.IsNullOrWhiteSpace(registeredName) || string.IsNullOrWhiteSpace(scrapedName)) return;
            if (!string.Equals(registeredName.Trim(), scrapedName.Trim(), System.StringComparison.OrdinalIgnoreCase)) return;
            _canonicalNameCache[scrapedName] = registeredName.Trim();
        }

        /// <summary>
        /// Resolves a player's faction clan tag and caches it against their scoreboard name.
        /// The faction table stores its roster as a member_player_ids array, so a single
        /// containment query gets the tag without needing a join.
        /// </summary>
        IEnumerator GetClanTagForPlayer(string originalName, string playerId)
        {
            string filter = UnityWebRequest.EscapeURL($"cs.[\"{playerId}\"]");
            string url = $"{SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentBaseApi()}/faction?member_player_ids={filter}&select=clan_tag&limit=1";

            using (UnityWebRequest r = UnityWebRequest.Get(url)) {
                r.SetRequestHeader("apikey", SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAuthToken());
                r.SetRequestHeader("Authorization", $"Bearer {SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAuthToken()}");

                yield return r.SendWebRequest();

                string tag = "";
                if (r.result == UnityWebRequest.Result.Success) {
                    try {
                        var res = JArray.Parse(r.downloadHandler.text);
                        if (res.Count > 0) tag = res[0]["clan_tag"]?.ToString()?.Trim() ?? "";
                    } catch { tag = ""; }
                }

                // Cached even when empty so players without a faction aren't re-queried every refresh
                _clanTagCache[originalName] = tag;
            }
        }

        // Update these lines in GetMMRFuzzyFallback
        IEnumerator GetMMRFuzzyFallback(string originalName, System.Action<string> onPlayerIdResolved = null)
        {
            string url = $"{SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentPlayerApi()}?display_name=ilike.*{UnityWebRequest.EscapeURL(originalName)}*";

            using (UnityWebRequest r = UnityWebRequest.Get(url)) {
                r.SetRequestHeader("apikey", SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAuthToken());
                r.SetRequestHeader("Authorization", $"Bearer {SBGL.UnifiedMod.Core.UnifiedPlugin.GetCurrentAuthToken()}");

                yield return r.SendWebRequest();
                string val = "--";
                if (r.result == UnityWebRequest.Result.Success) {
                    var res = JArray.Parse(r.downloadHandler.text);
                    if (res.Count > 0) {
                        val = res[0]["current_mmr"]?.ToString() ?? "--";
                        onPlayerIdResolved?.Invoke(res[0]["id"]?.ToString());
                        CacheCanonicalName(originalName, res[0]["display_name"]?.ToString());
                    }
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
            _clanTagCache.Clear();
            _canonicalNameCache.Clear();
            _pendingRequests.Clear();
            _cachedScoreboard = null;
            SbglOverlayOpen = false;
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