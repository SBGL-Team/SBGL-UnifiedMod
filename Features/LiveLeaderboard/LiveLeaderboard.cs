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
        private int _cachedHolePar = -1;
        private int _cachedPlayedPar = 0;
        private int _cachedHoleNum = 0; // 1-based hole within the course; 0 before any match
        private CourseData _cachedCourse;
        // Team games: read from the game each frame while the overlay is open
        private bool _liveIsTeamMatch = false;
        private int _liveRedWins, _liveBlueWins, _liveRedHolePts, _liveBlueHolePts;
        // Team result frozen with the final snapshot for the Driving Range display
        private bool _finalIsTeamMatch = false;
        private int _finalRedWins, _finalBlueWins, _finalRedHolePts, _finalBlueHolePts;
        // Per-frame caches. OnGUI runs at least twice a frame, so nothing here is rebuilt
        // unless its inputs change.
        private OverlayStyles _styles;
        private float _stylesScale = -1f;
        private readonly GUIContent _measureContent = new GUIContent();
        private Key _hintKey;
        private string _hintText;
        private string _matchTypeLabel = "";
        private float _nextMatchTypeRefresh = 0f;
        private List<SBGLPlayer> _splitSource;
        private int _splitCount = -1;
        private readonly List<SBGLPlayer> _splitRed = new List<SBGLPlayer>();
        private readonly List<SBGLPlayer> _splitBlue = new List<SBGLPlayer>();
        private readonly List<SBGLPlayer> _splitSpec = new List<SBGLPlayer>();
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
            public Team Team = Team.None; // Red or Blue in team games, None in free-for-all

            /// <summary>
            /// The registered display_name from the site, which may differ in case from the
            /// in-game Name (e.g. "JaBob" vs "JaBoB"). Presentation only — Name stays the key
            /// used for score lookups, so it must not be overwritten with this.
            /// </summary>
            public string CanonicalName = "";

            /// <summary>Registered casing when known, otherwise the name as the game renders it.</summary>
            public string PresentationName =>
                string.IsNullOrWhiteSpace(CanonicalName) ? Name : CanonicalName;

            /// <summary>True for the player at this keyboard, so their row can be highlighted.</summary>
            public bool IsLocal;

            // The overlay draws these every frame, so each derived text is built once and kept
            // until the value it came from changes.
            private string _displayWithTag, _displayTagSource, _displayNameSource;
            private string _scoreText; private int _scoreTextValue = int.MinValue;
            private string _strokeLabel, _strokeSource; private int _strokeValue;
            private string _projSuffix, _projSource, _projMmrSource;
            private string _rankHex, _rankHexSource; private bool _rankHexSpectator;

            /// <summary>Name prefixed with the clan tag when the player belongs to a faction.</summary>
            public string DisplayNameWithTag
            {
                get
                {
                    string pres = PresentationName;
                    if (_displayWithTag == null || !ReferenceEquals(_displayTagSource, ClanTag) || !ReferenceEquals(_displayNameSource, pres))
                    {
                        _displayTagSource = ClanTag;
                        _displayNameSource = pres;
                        _displayWithTag = string.IsNullOrWhiteSpace(ClanTag) ? pres : $"[{ClanTag.ToUpper()}] {pres}";
                    }
                    return _displayWithTag;
                }
            }

            public string BaseScoreText
            {
                get
                {
                    if (_scoreText == null || _scoreTextValue != BaseScore) { _scoreTextValue = BaseScore; _scoreText = BaseScore.ToString(); }
                    return _scoreText;
                }
            }

            /// <summary>Strokes relative to par parsed from the scoreboard text; 0 when unknown.</summary>
            public int StrokeValue { get { EnsureStroke(); return _strokeValue; } }

            /// <summary>"+2", "-1" or "E".</summary>
            public string StrokeLabel { get { EnsureStroke(); return _strokeLabel; } }

            private void EnsureStroke()
            {
                if (_strokeLabel != null && ReferenceEquals(_strokeSource, RawStrokes)) return;
                _strokeSource = RawStrokes;
                _strokeValue = 0;
                if (!string.IsNullOrEmpty(RawStrokes))
                    int.TryParse(RawStrokes.Replace("±", "").Replace("+", "").Trim(), out _strokeValue);
                _strokeLabel = _strokeValue > 0 ? $"+{_strokeValue}" : _strokeValue < 0 ? _strokeValue.ToString() : "E";
            }

            /// <summary>The projected change, e.g. "(+12)", or "" when there is none.</summary>
            public string ProjectedSuffix
            {
                get
                {
                    if (_projSuffix == null || !ReferenceEquals(_projSource, ProjectedDisplay) || !ReferenceEquals(_projMmrSource, MMR))
                    {
                        _projSource = ProjectedDisplay;
                        _projMmrSource = MMR;
                        _projSuffix = (ProjectedDisplay ?? "").Replace(MMR ?? "", "").Trim();
                    }
                    return _projSuffix;
                }
            }

            /// <summary>Rank-tier colour for the MMR; grey while unknown or for spectators.</summary>
            public string RankHex
            {
                get
                {
                    if (_rankHex == null || !ReferenceEquals(_rankHexSource, MMR) || _rankHexSpectator != IsSpectator)
                    {
                        _rankHexSource = MMR;
                        _rankHexSpectator = IsSpectator;
                        float mmrVal = 0f;
                        float.TryParse(MMR, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mmrVal);
                        _rankHex = IsSpectator ? "#555555"
                                 : (MMR == "..." || MMR == "--") ? "#888888"
                                 : SBGL.UnifiedMod.Core.Season2RuleSet.GetRankColor(mmrVal);
                    }
                    return _rankHex;
                }
            }
        }

        void Awake()
        {
            Instance = this;

            if (!PlayerPrefs.HasKey("LL_MaxPlayers")) PlayerPrefs.SetInt("LL_MaxPlayers", 16);

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
            {
                UseNativeScoreboard = !UseNativeScoreboard;
                SBGL.UnifiedMod.Patches.LeaderboardPatches.ApplyViewMode();
            }

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
                if (!_showFinalSnapshot) RefreshLiveTeamScores();

                // PlayerPrefs goes through the registry on Windows; once a second is plenty
                if (Time.unscaledTime >= _nextMatchTypeRefresh)
                {
                    _nextMatchTypeRefresh = Time.unscaledTime + 1f;
                    _matchTypeLabel = GetMatchTypeDisplayName(PlayerPrefs.GetString("MatchType", ""));
                }

                var matchSetupMenu = SingletonNetworkBehaviour<MatchSetupMenu>.HasInstance
                    ? SingletonNetworkBehaviour<MatchSetupMenu>.Instance : null;
                if (matchSetupMenu != null)
                    _cachedTotalHoles = matchSetupMenu.randomCupNumHoles;

                // The Driving Range has a "hole" of its own. Keep the finished match's hole and par
                // there, so the FINAL screen shows the match rather than the range.
                int globalIndex = inDrivingRange ? _cachedHoleGlobalIndex : CourseManager.CurrentHoleGlobalIndex;
                var course = inDrivingRange ? _cachedCourse : GameManager.CurrentCourse;
                if (!inDrivingRange) _cachedHoleNum = CourseManager.CurrentHoleCourseIndex + 1;
                if (globalIndex != _cachedHoleGlobalIndex || !ReferenceEquals(course, _cachedCourse))
                {
                    _cachedHoleGlobalIndex = globalIndex;
                    _cachedCourse = course;
                    try
                    {
                        var locName = CourseManager.GetCurrentHoleLocalizedName();
                        _cachedCourseName = locName != null ? locName.GetLocalizedString() : "";
                    }
                    catch { _cachedCourseName = ""; }

                    try
                    {
                        _cachedHolePar = CourseManager.GetCurrentHolePar();
                        // Par of completed holes only (same basis as the +/- column)
                        var holes = course != null ? course.Holes : null;
                        int courseIndex = CourseManager.CurrentHoleCourseIndex;
                        int playedPar = 0;
                        if (holes != null)
                            for (int i = 0; i < courseIndex && i < holes.Length; i++)
                                if (holes[i] != null) playedPar += holes[i].Par;
                        _cachedPlayedPar = playedPar;
                    }
                    catch { _cachedHolePar = -1; _cachedPlayedPar = 0; }
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

            if (_styles == null || Mathf.Abs(scale - _stylesScale) > 0.001f)
            {
                _styles = new OverlayStyles(scale, _centeredStyle, _headerStyle, _nameStyle);
                _stylesScale = scale;
            }

            // Native mode: show a small hint label so players know how to switch back
            if (UseNativeScoreboard && Scoreboard.IsVisible)
            {
                if (_hintText == null || _hintKey != ViewToggleKey)
                {
                    _hintKey = ViewToggleKey;
                    _hintText = $"[{_hintKey}] Switch to SBGL View";
                }
                float lblW = 160f * scale;
                float lblH = 20f * scale;
                GUI.Label(new Rect((Screen.width - lblW) / 2f, Screen.height * 0.055f, lblW, lblH), _hintText, _toggleStyle);
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
            if (_solidStyle == null || _styles == null) return;
            var S = _styles;

            string lobbyName = SBGL.UnifiedMod.Features.CompetitivePluginCheck.CompetitivePluginCheck._currentLobbyName ?? "";
            string matchId = SBGLeagueAutomation.SBGLPlugin.CurrentMatchId ?? "";
            bool isFinal = _showFinalSnapshot;
            bool teams = isFinal ? _finalIsTeamMatch : _liveIsTeamMatch;

            // Dimensions — all relative to screen size so any resolution works
            // Two team columns need the extra room. The height-based cap keeps ultrawide screens
            // from stretching the columns apart; 16:9 screens hit the width limit first and are unchanged.
            float panelW = Mathf.Min(Screen.width * (teams ? 0.60f : 0.52f), Screen.height * (teams ? 1.07f : 0.93f));
            float accentH = 5f * scale;          // top green stripe
            float titleH = 30f * scale;           // lobby / match info row
            float colH = 22f * scale;             // column header row
            float pRowH = 26f * scale;            // player row
            float bannerH = 42f * scale;          // team names and hole wins (team games only)
            float leftStripW = 4f * scale;        // rank-tier colour strip
            float pad = 10f * scale;

            int bodyRows;
            if (teams)
            {
                // Red and blue side by side, spectators underneath across the full width
                EnsureTeamSplit(players);
                bodyRows = Mathf.Max(Mathf.Max(_splitRed.Count, _splitBlue.Count), 1) + _splitSpec.Count;
            }
            else
            {
                bodyRows = Mathf.Max(players.Count, 1);
            }

            float footH = Mathf.Max(pad * 1.8f, 18f * scale);
            float panelH = accentH + titleH + (teams ? bannerH : 0f) + colH + bodyRows * pRowH + footH;
            float panelX = (Screen.width - panelW) / 2f;
            float panelY = Screen.height * 0.055f;

            var saved = GUI.backgroundColor;

            // ── Cream background and the SBGL-green accent stripe ─────────
            TintRow(panelX, panelY, panelW, panelH, OverlayStyles.Cream, saved);
            TintRow(panelX, panelY, panelW, accentH, OverlayStyles.Green, saved);
            float y = panelY + accentH;

            // ── Title row: "SBGL SEASON n" left · lobby name center · par / type / course / hole right ──
            GUI.Label(new Rect(panelX + pad, y, panelW * 0.32f, titleH), SeasonTitle(), S.titleLeft);
            GUI.Label(new Rect(panelX, y, panelW, titleH), lobbyName, S.titleCenter);
            GUI.Label(new Rect(panelX, y, panelW - pad, titleH), RightLabel(isFinal), isFinal ? S.titleRightFinal : S.titleRight);
            y += titleH;

            // ── Thin divider under title ──────────────────────────────────
            DrawDivider(panelX + pad, y - 1f, panelW - pad * 2f, saved);

            y = teams
                ? DrawTeamBody(_splitRed, _splitBlue, _splitSpec, panelX, panelW, y, scale, pad, leftStripW, colH, pRowH, bannerH, isFinal, saved)
                : DrawFfaBody(players, panelX, panelW, y, scale, pad, leftStripW, colH, pRowH, saved);

            // ── Footer row: site left, match ID right ─────────────────────
            GUI.Label(new Rect(panelX + pad, y, panelW * 0.45f, footH), "SBGLeague.com", S.foot);
            GUI.Label(new Rect(panelX, y, panelW - pad * 0.5f, footH), FooterRight(matchId), S.footRight);
        }

        /// <summary>
        /// Free-for-all body: one ranked list with position numbers. Returns the y below the
        /// last row so the footer can follow.
        /// </summary>
        private float DrawFfaBody(List<SBGLPlayer> players, float panelX, float panelW, float y, float scale,
                                  float pad, float leftStripW, float colH, float pRowH, Color saved)
        {
            var S = _styles;

            // ── Column headers ────────────────────────────────────────────
            float usable = panelW - leftStripW - pad * 2f;
            float cPos     = usable * 0.07f;
            float cName    = usable * 0.37f;
            float cMMR     = usable * 0.28f;
            float cPts     = usable * 0.12f;
            float cStrokes = usable * 0.16f;

            float cx = panelX + leftStripW + pad;
            GUI.Label(new Rect(cx, y, cPos, colH), "POS", S.colHeader);   cx += cPos;
            GUI.Label(new Rect(cx, y, cName, colH), "PLAYER", S.colHeaderName); cx += cName;
            GUI.Label(new Rect(cx, y, cMMR, colH), "MMR", S.colHeader);   cx += cMMR;
            GUI.Label(new Rect(cx, y, cPts, colH), "PTS", S.colHeader);    cx += cPts;
            GUI.Label(new Rect(cx, y, cStrokes, colH), "+/-", S.colHeader);
            y += colH;

            // ── Thin divider under column headers ─────────────────────────
            DrawDivider(panelX + pad, y, panelW - pad * 2f, saved);

            // ── Player rows ───────────────────────────────────────────────
            if (players.Count == 0)
            {
                DrawWaitingRow(panelX, panelW, y, pad, pRowH);
                return y + pRowH;
            }

            int rankPos = 0; // counts only active (non-spectator) players for position numbers
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];

                if (!p.IsSpectator) rankPos++;

                // Row tint: gold for leader, subtle alternating for active players; dim for spectators
                if (p.IsSpectator)
                    TintRow(panelX, y, panelW, pRowH, OverlayStyles.SpectatorRow, saved);
                else if (rankPos == 1)
                    TintRow(panelX, y, panelW, pRowH, OverlayStyles.LeaderRow, saved);
                else if (rankPos % 2 == 0)
                    TintRow(panelX, y, panelW, pRowH, OverlayStyles.AltRow, saved);

                // Your own row
                if (p.IsLocal && !p.IsSpectator)
                    TintRow(panelX, y, panelW, pRowH, OverlayStyles.LocalRow, saved);

                // Left rank-tier colour strip — grey for spectators
                string rankHex = p.RankHex;
                DrawRankStrip(panelX, y, leftStripW, pRowH, rankHex, saved);

                cx = panelX + leftStripW + pad;

                if (p.IsSpectator)
                {
                    DrawSpectatorRow(p, cx, y, cPos, cName, cMMR + cPts + cStrokes, pRowH);
                    y += pRowH;
                    continue;
                }

                // Position number
                GUI.Label(new Rect(cx, y, cPos, pRowH), NumText(rankPos), rankPos == 1 ? S.posLeader : S.pos); cx += cPos;

                // Player name (prefixed with the clan tag when they belong to a faction)
                GUI.Label(new Rect(cx, y, cName, pRowH), p.DisplayNameWithTag, p.IsLocal ? S.nameLocal : S.name); cx += cName;

                DrawScoreCells(p, cx, y, cMMR, cPts, cStrokes, pRowH, scale, rankHex);

                y += pRowH;
            }

            return y;
        }

        /// <summary>
        /// Team body: a banner with each team's hole wins and this-hole points, then the red and
        /// blue rosters side by side, then any spectators across the full width. Returns the y
        /// below the last row.
        /// </summary>
        private float DrawTeamBody(List<SBGLPlayer> red, List<SBGLPlayer> blue, List<SBGLPlayer> spectators,
                                   float panelX, float panelW, float y, float scale, float pad, float leftStripW,
                                   float colH, float pRowH, float bannerH, bool isFinal, Color saved)
        {
            var S = _styles;
            int redWins   = isFinal ? _finalRedWins     : _liveRedWins;
            int blueWins  = isFinal ? _finalBlueWins    : _liveBlueWins;
            int redPts    = isFinal ? _finalRedHolePts  : _liveRedHolePts;
            int bluePts   = isFinal ? _finalBlueHolePts : _liveBlueHolePts;

            float halfW = panelW / 2f;

            // ── Team banner: red half, blue half, hole wins in the middle ──
            TintRow(panelX, y, halfW, bannerH, OverlayStyles.RedBanner, saved);
            TintRow(panelX + halfW, y, halfW, bannerH, OverlayStyles.BlueBanner, saved);
            GUI.Label(new Rect(panelX + pad, y, halfW * 0.6f, bannerH), "RED TEAM", S.teamNameL);
            GUI.Label(new Rect(panelX + panelW - pad - halfW * 0.6f, y, halfW * 0.6f, bannerH), "BLUE TEAM", S.teamNameR);

            // Hole wins, big, with the leader in its team colour; this-hole points as a caption
            float centerW = panelW * 0.30f;
            float centerX = panelX + (panelW - centerW) / 2f;
            float bigH = bannerH * 0.62f;
            float numW = centerW * 0.42f;
            float dashW = centerW - numW * 2f;
            GUI.Label(new Rect(centerX, y, numW, bigH), NumText(redWins), redWins >= blueWins ? S.bigRedL : S.bigMidL);
            GUI.Label(new Rect(centerX + numW, y, dashW, bigH), "–", S.bigDash);
            GUI.Label(new Rect(centerX + numW + dashW, y, numW, bigH), NumText(blueWins), blueWins >= redWins ? S.bigBlueR : S.bigMidR);
            GUI.Label(new Rect(centerX, y + bigH, centerW, bannerH - bigH), HoleCaption(redPts, bluePts), S.caption);
            y += bannerH;

            // ── Column headers, one set per team ──────────────────────────
            float gapW = pad * 1.5f;                      // space between the two columns
            float innerPad = 6f * scale;                   // space between a rank strip and the name
            float halfUsable = (panelW - pad * 2f - gapW) / 2f - leftStripW - innerPad;
            float cName    = halfUsable * 0.46f;
            float cMMR     = halfUsable * 0.30f;
            float cPts     = halfUsable * 0.11f;
            float cStrokes = halfUsable * 0.13f;
            float leftX  = panelX + pad;                                                 // left column's rank strip
            float rightX = panelX + pad + leftStripW + innerPad + halfUsable + gapW;     // right column's rank strip

            for (int side = 0; side < 2; side++)
            {
                float hx = (side == 0 ? leftX : rightX) + leftStripW + innerPad;
                GUI.Label(new Rect(hx, y, cName, colH), "PLAYER", S.colHeaderName); hx += cName;
                GUI.Label(new Rect(hx, y, cMMR, colH), "MMR", S.colHeader);   hx += cMMR;
                GUI.Label(new Rect(hx, y, cPts, colH), "PTS", S.colHeader);    hx += cPts;
                GUI.Label(new Rect(hx, y, cStrokes, colH), "+/-", S.colHeader);
            }
            y += colH;
            DrawDivider(panelX + pad, y, panelW - pad * 2f, saved);

            // ── Roster rows: red on the left, blue on the right ───────────
            int rows = Mathf.Max(red.Count, blue.Count);
            if (rows == 0)
            {
                DrawWaitingRow(panelX, panelW, y, pad, pRowH);
                y += pRowH;
            }

            float rowsTop = y;
            for (int i = 0; i < rows; i++)
            {
                // Faint team tint on each half, a little darker on alternate rows
                bool alt = (i % 2 == 1);
                TintRow(panelX, y, halfW, pRowH, alt ? OverlayStyles.RedRowAlt : OverlayStyles.RedRow, saved);
                TintRow(panelX + halfW, y, halfW, pRowH, alt ? OverlayStyles.BlueRowAlt : OverlayStyles.BlueRow, saved);

                if (i < red.Count)
                    DrawTeamPlayerRow(red[i], leftX, panelX, halfW, y, leftStripW, innerPad, cName, cMMR, cPts, cStrokes, pRowH, scale, saved);
                if (i < blue.Count)
                    DrawTeamPlayerRow(blue[i], rightX, panelX + halfW, halfW, y, leftStripW, innerPad, cName, cMMR, cPts, cStrokes, pRowH, scale, saved);

                y += pRowH;
            }

            // Centre line between the two rosters
            if (rows > 0)
                TintRow(panelX + halfW - 0.5f, rowsTop, 1f, y - rowsTop, OverlayStyles.DividerColor, saved);

            // ── Spectators across the full width ──────────────────────────
            for (int i = 0; i < spectators.Count; i++)
            {
                var p = spectators[i];
                TintRow(panelX, y, panelW, pRowH, OverlayStyles.SpectatorRow, saved);
                DrawRankStrip(panelX, y, leftStripW, pRowH, p.RankHex, saved);

                float sx = panelX + leftStripW + pad;
                float restW = panelX + panelW - pad - (sx + cPts + cName);
                DrawSpectatorRow(p, sx, y, cPts, cName, restW, pRowH);
                y += pRowH;
            }

            return y;
        }

        private void DrawTeamPlayerRow(SBGLPlayer p, float x0, float tintX, float tintW, float y, float leftStripW, float innerPad,
                                       float cName, float cMMR, float cPts, float cStrokes, float pRowH, float scale, Color saved)
        {
            var S = _styles;
            if (p.IsLocal)
                TintRow(tintX, y, tintW, pRowH, OverlayStyles.LocalRow, saved);

            string rankHex = p.RankHex;
            DrawRankStrip(x0, y, leftStripW, pRowH, rankHex, saved);

            float cx = x0 + leftStripW + innerPad;
            GUI.Label(new Rect(cx, y, cName, pRowH), p.DisplayNameWithTag, p.IsLocal ? S.nameLocal : S.name); cx += cName;
            DrawScoreCells(p, cx, y, cMMR, cPts, cStrokes, pRowH, scale, rankHex);
        }

        /// <summary>The MMR, PTS and +/- cells shared by the free-for-all and team rows.</summary>
        private void DrawScoreCells(SBGLPlayer p, float cx, float y, float cMMR, float cPts, float cStrokes,
                                    float pRowH, float scale, string rankHex)
        {
            var S = _styles;

            // MMR (rank-colored) + projected change centred in column
            string rawMMR = p.MMR;
            string proj   = p.ProjectedSuffix;
            GUIStyle mmrS = S.Mmr(rankHex);

            _measureContent.text = rawMMR;
            float mmrW = mmrS.CalcSize(_measureContent).x;
            float projW = 0f;
            if (proj.Length > 0)
            {
                _measureContent.text = proj;
                projW = _centeredStyle.CalcSize(_measureContent).x;
            }
            float gap    = 3f * scale;
            float groupW = mmrW + (proj.Length > 0 ? projW + gap : 0f);
            float mmrX   = cx + (cMMR / 2f) - (groupW / 2f);
            GUI.Label(new Rect(mmrX, y, mmrW, pRowH), rawMMR, mmrS);
            if (proj.Length > 0)
                GUI.Label(new Rect(mmrX + mmrW + gap, y, projW, pRowH), proj, proj.Contains("+") ? _posStyle : _negStyle);
            cx += cMMR;

            // Points (gold)
            GUI.Label(new Rect(cx, y, cPts, pRowH), p.BaseScoreText, S.pts); cx += cPts;

            // Strokes vs par — single column: red if over, green if under, grey if even
            int strokeVal = p.StrokeValue;
            GUI.Label(new Rect(cx, y, cStrokes, pRowH), p.StrokeLabel,
                strokeVal > 0 ? S.strokeOver : strokeVal < 0 ? S.strokeUnder : S.strokeEven);
        }

        private void DrawSpectatorRow(SBGLPlayer p, float cx, float y, float badgeW, float nameW, float restW, float pRowH)
        {
            var S = _styles;
            // "SPEC" badge in the position column, dim name, then one wide "SPECTATING" cell
            GUI.Label(new Rect(cx, y, badgeW, pRowH), "👁", S.specBadge); cx += badgeW;
            GUI.Label(new Rect(cx, y, nameW, pRowH), p.DisplayNameWithTag, S.specDim); cx += nameW;
            GUI.Label(new Rect(cx, y, restW, pRowH), "SPECTATING", S.specDim);
        }

        private void DrawWaitingRow(float panelX, float panelW, float y, float pad, float pRowH)
        {
            // On the Driving Range an empty board means no match has finished in this lobby yet
            string text = _showFinalSnapshot ? "No match played yet in this lobby" : "Waiting for player data...";
            GUI.Label(new Rect(panelX + pad, y, panelW - pad * 2f, pRowH), text, _styles.waiting);
        }

        private void DrawRankStrip(float x, float y, float w, float h, string rankHex, Color saved)
        {
            TintRow(x, y, w, h, _styles.Hex(rankHex), saved);
        }

        private void DrawDivider(float x, float y, float w, Color saved)
        {
            TintRow(x, y, w, 1f, OverlayStyles.DividerColor, saved);
        }

        /// <summary>A solid box in the given colour, leaving GUI.backgroundColor as it was.</summary>
        private void TintRow(float x, float y, float w, float h, Color tint, Color saved)
        {
            GUI.backgroundColor = tint;
            GUI.Box(new Rect(x, y, w, h), GUIContent.none, _solidStyle);
            GUI.backgroundColor = saved;
        }

        /// <summary>Splits the list being drawn into red, blue and spectators; redone only when the list changes.</summary>
        private void EnsureTeamSplit(List<SBGLPlayer> players)
        {
            if (ReferenceEquals(players, _splitSource) && players.Count == _splitCount) return;
            _splitSource = players;
            _splitCount = players.Count;
            _splitRed.Clear();
            _splitBlue.Clear();
            _splitSpec.Clear();
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p.IsSpectator) _splitSpec.Add(p);
                else if (p.Team == Team.Red) _splitRed.Add(p);
                else _splitBlue.Add(p);
            }
        }

        // ── Text caches: each string is rebuilt only when the values behind it change ──

        private static readonly string[] SmallNumbers = BuildSmallNumbers();
        private static string[] BuildSmallNumbers()
        {
            var a = new string[100];
            for (int i = 0; i < a.Length; i++) a[i] = i.ToString();
            return a;
        }
        private static string NumText(int n) => (n >= 0 && n < SmallNumbers.Length) ? SmallNumbers[n] : n.ToString();

        private int _seasonTitleSeason = -1;
        private string _seasonTitle = "";
        private string SeasonTitle()
        {
            int season = SBGL.UnifiedMod.Core.SeasonRuleSets.CurrentSeasonNumber;
            if (season != _seasonTitleSeason)
            {
                _seasonTitleSeason = season;
                _seasonTitle = $"SBGL SEASON {season}";
            }
            return _seasonTitle;
        }

        private string _rightLabel = "";
        private int _rlHole = -1, _rlPar = -1, _rlPlayed = -1, _rlTotal = -1;
        private bool _rlFinal;
        private string _rlType = "", _rlCourse = "";
        private string RightLabel(bool isFinal)
        {
            int currentHole = _cachedHoleNum;
            string matchType = _matchTypeLabel;
            if (currentHole == _rlHole && isFinal == _rlFinal && _cachedHolePar == _rlPar && _cachedPlayedPar == _rlPlayed
                && _cachedTotalHoles == _rlTotal && matchType == _rlType && _cachedCourseName == _rlCourse)
                return _rightLabel;

            _rlHole = currentHole; _rlFinal = isFinal; _rlPar = _cachedHolePar; _rlPlayed = _cachedPlayedPar;
            _rlTotal = _cachedTotalHoles; _rlType = matchType; _rlCourse = _cachedCourseName;

            string holeStr = currentHole > 0 ? $"{currentHole}/{_cachedTotalHoles}" : "";
            // On the final screen the last hole is finished too
            int playedPar = _cachedPlayedPar + (isFinal && _cachedHolePar > 0 ? _cachedHolePar : 0);
            int holesThru = currentHole > 0 ? (isFinal ? currentHole : currentHole - 1) : 0;
            string parStr = (_cachedHolePar > 0 ? $"Par {_cachedHolePar}" : "")
                + (_cachedHolePar > 0 && playedPar > 0 ? "  ·  " : "")
                + (playedPar > 0 ? $"Par Thru {holesThru}: {playedPar}" : "");
            _rightLabel = (string.IsNullOrEmpty(parStr) ? "" : parStr + "  ·  ")
                + matchType
                + (string.IsNullOrEmpty(_cachedCourseName) ? "" : $"  ·  {_cachedCourseName}")
                + (string.IsNullOrEmpty(holeStr) ? "" : $"  ·  {holeStr}")
                + (isFinal ? "  ·  FINAL" : "  ·  LIVE");
            return _rightLabel;
        }

        private string _footerText = "";
        private string _footerMatchId;
        private Key _footerKey;
        private string FooterRight(string matchId)
        {
            if (_footerText.Length > 0 && matchId == _footerMatchId && _footerKey == ViewToggleKey) return _footerText;
            _footerMatchId = matchId;
            _footerKey = ViewToggleKey;
            _footerText = string.IsNullOrWhiteSpace(matchId)
                ? $"[{_footerKey}] Native View"
                : $"[{_footerKey}] Native View  ·  Match: {(matchId.Length > 14 ? matchId.Substring(matchId.Length - 14) : matchId)}";
            return _footerText;
        }

        private int _capRed = -1, _capBlue = -1;
        private string _capText = "";
        private string HoleCaption(int redPts, int bluePts)
        {
            if (redPts != _capRed || bluePts != _capBlue)
            {
                _capRed = redPts;
                _capBlue = bluePts;
                _capText = $"HOLES WON   ·   THIS HOLE {redPts} – {bluePts}";
            }
            return _capText;
        }

        /// <summary>
        /// Every GUIStyle the overlay draws with, built once per screen scale. OnGUI runs at
        /// least twice a frame, and building these per call was over a hundred allocations a
        /// frame with a full lobby.
        /// </summary>
        private sealed class OverlayStyles
        {
            public static readonly Color Dark  = new Color(0.12f, 0.12f, 0.14f);
            public static readonly Color Mid   = new Color(0.35f, 0.35f, 0.38f);
            public static readonly Color Cream = new Color(254f / 255f, 250f / 255f, 238f / 255f);  // #FEFAEE
            public static readonly Color Green = new Color(26f / 255f, 189f / 255f, 96f / 255f);    // #1ABD60
            public static readonly Color RedTeam  = new Color(0.80f, 0.20f, 0.18f);
            public static readonly Color BlueTeam = new Color(0.16f, 0.42f, 0.85f);
            public static readonly Color DividerColor = new Color(0.78f, 0.74f, 0.68f, 1f);
            public static readonly Color SpectatorRow = new Color(0f, 0f, 0f, 0.06f);
            public static readonly Color LeaderRow    = new Color(1f, 0.92f, 0.55f, 0.35f);
            public static readonly Color AltRow       = new Color(0f, 0f, 0f, 0.04f);
            public static readonly Color LocalRow     = new Color(0.10f, 0.74f, 0.38f, 0.16f);
            public static readonly Color RedBanner    = new Color(0.80f, 0.20f, 0.18f, 0.12f);
            public static readonly Color BlueBanner   = new Color(0.16f, 0.42f, 0.85f, 0.12f);
            public static readonly Color RedRow       = new Color(0.80f, 0.20f, 0.18f, 0.05f);
            public static readonly Color RedRowAlt    = new Color(0.80f, 0.20f, 0.18f, 0.09f);
            public static readonly Color BlueRow      = new Color(0.16f, 0.42f, 0.85f, 0.05f);
            public static readonly Color BlueRowAlt   = new Color(0.16f, 0.42f, 0.85f, 0.09f);

            public readonly GUIStyle titleLeft, titleCenter, titleRight, titleRightFinal;
            public readonly GUIStyle colHeader, colHeaderName;
            public readonly GUIStyle name, nameLocal, pos, posLeader, pts, strokeOver, strokeUnder, strokeEven;
            public readonly GUIStyle specDim, specBadge, waiting, foot, footRight;
            public readonly GUIStyle teamNameL, teamNameR, bigRedL, bigMidL, bigBlueR, bigMidR, bigDash, caption;

            private readonly GUIStyle _data;
            private readonly Dictionary<string, GUIStyle> _mmrByHex = new Dictionary<string, GUIStyle>();
            private readonly Dictionary<string, Color> _colorByHex = new Dictionary<string, Color>();

            public OverlayStyles(float scale, GUIStyle centered, GUIStyle header, GUIStyle nameBase)
            {
                titleLeft       = Make(header, 13f, scale, TextAnchor.MiddleLeft, Green);
                titleCenter     = Make(header, 14f, scale, TextAnchor.MiddleCenter, Dark);
                titleRight      = Make(header, 11f, scale, TextAnchor.MiddleRight, Mid);
                titleRightFinal = Make(header, 11f, scale, TextAnchor.MiddleRight, new Color(0.80f, 0.45f, 0.05f));
                colHeader       = Make(header, 9f, scale, TextAnchor.MiddleCenter, Mid);
                colHeaderName   = Make(header, 9f, scale, TextAnchor.MiddleLeft, Mid);

                name      = Make(nameBase, 13f, scale, TextAnchor.MiddleLeft, Dark);
                nameLocal = Make(nameBase, 13f, scale, TextAnchor.MiddleLeft, Dark, FontStyle.Bold);
                _data     = Make(centered, 12f, scale, TextAnchor.MiddleCenter, Dark);
                pos       = _data;
                posLeader = Make(centered, 12f, scale, TextAnchor.MiddleCenter, new Color(0.60f, 0.45f, 0.02f), FontStyle.Bold);
                pts       = Make(centered, 12f, scale, TextAnchor.MiddleCenter, new Color(0.72f, 0.52f, 0.02f), FontStyle.Bold);
                strokeOver  = Make(centered, 12f, scale, TextAnchor.MiddleCenter, new Color(0.80f, 0.15f, 0.15f));
                strokeUnder = Make(centered, 12f, scale, TextAnchor.MiddleCenter, new Color(0.10f, 0.60f, 0.20f));
                strokeEven  = Make(centered, 12f, scale, TextAnchor.MiddleCenter, Mid);
                specDim   = strokeEven;   // same look: 12px, mid grey
                waiting   = strokeEven;
                specBadge = Make(centered, 12f, scale, TextAnchor.MiddleCenter, new Color(0.55f, 0.55f, 0.60f), FontStyle.Bold);

                foot      = Make(centered, 11f, scale, TextAnchor.MiddleLeft, new Color(0.18f, 0.85f, 0.32f), FontStyle.Bold);
                footRight = Make(centered, 10f, scale, TextAnchor.MiddleRight, new Color(0.45f, 0.50f, 0.60f));

                teamNameL = Make(header, 14f, scale, TextAnchor.MiddleLeft, RedTeam);
                teamNameR = Make(header, 14f, scale, TextAnchor.MiddleRight, BlueTeam);
                bigRedL   = Make(header, 20f, scale, TextAnchor.LowerRight, RedTeam);
                bigMidL   = Make(header, 20f, scale, TextAnchor.LowerRight, Mid);
                bigBlueR  = Make(header, 20f, scale, TextAnchor.LowerLeft, BlueTeam);
                bigMidR   = Make(header, 20f, scale, TextAnchor.LowerLeft, Mid);
                bigDash   = Make(header, 16f, scale, TextAnchor.LowerCenter, Mid);
                caption   = Make(centered, 9f, scale, TextAnchor.UpperCenter, Mid);
            }

            private static GUIStyle Make(GUIStyle basis, float size, float scale, TextAnchor anchor, Color color, FontStyle fontStyle = FontStyle.Normal)
            {
                var s = new GUIStyle(basis) { fontSize = Mathf.RoundToInt(size * scale), alignment = anchor };
                if (fontStyle != FontStyle.Normal) s.fontStyle = fontStyle;
                s.normal.textColor = color;
                return s;
            }

            /// <summary>The data style tinted with a rank colour, one per distinct colour.</summary>
            public GUIStyle Mmr(string rankHex)
            {
                if (!_mmrByHex.TryGetValue(rankHex, out var s))
                {
                    s = new GUIStyle(_data);
                    s.normal.textColor = Hex(rankHex);
                    _mmrByHex[rankHex] = s;
                }
                return s;
            }

            /// <summary>A parsed "#RRGGBB" colour, parsed once per distinct value.</summary>
            public Color Hex(string hex)
            {
                if (!_colorByHex.TryGetValue(hex, out var c))
                {
                    if (!ColorUtility.TryParseHtmlString(hex, out c)) c = UnityEngine.Color.grey;
                    _colorByHex[hex] = c;
                }
                return c;
            }
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

            if (_cachedScoreboard == null) return;

            // Team games fill the native scoreboard's two team lists and leave its free-for-all
            // list alone, so read whichever the current game mode populates.
            bool isTeams = IsTeamGameMode();
            _liveIsTeamMatch = isTeams;

            string localName = GetLocalPlayerName();
            List<SBGLPlayer> newList = new List<SBGLPlayer>();
            bool haveEntries;
            if (isTeams)
            {
                var redEntries  = _cachedScoreboard.redTeamEntries;
                var blueEntries = _cachedScoreboard.blueTeamEntries;
                haveEntries = (redEntries?.Count ?? 0) + (blueEntries?.Count ?? 0) > 0;
                ScrapeEntries(redEntries,  Team.Red,  true, localName, newList);
                ScrapeEntries(blueEntries, Team.Blue, true, localName, newList);
            }
            else
            {
                var entries = _cachedScoreboard.entries;
                haveEntries = (entries?.Count ?? 0) > 0;
                ScrapeEntries(entries, Team.None, false, localName, newList);
            }
            if (!haveEntries) return;

            // Keep disconnected players visible with their last known score
            HashSet<string> liveNames = new HashSet<string>(newList.Select(p => p.Name), System.StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _lastKnownRoundPlayers) {
                if (!liveNames.Contains(kvp.Key) && kvp.Value != null)
                    newList.Add(ClonePlayer(kvp.Value));
            }

            var activePlayers = newList.Where(p => !p.IsSpectator).ToList();
            var spectators    = newList.Where(p =>  p.IsSpectator).ToList();

            // The projection is a free-for-all Elo between every pair of players, which means
            // nothing between team-mates, so team games show the MMR without a projection.
            if (activePlayers.Count > 1 && !isTeams) CalculateProjectedMMR(activePlayers);

            IEnumerable<SBGLPlayer> ordered = isTeams
                ? activePlayers.OrderBy(p => p.Team == Team.Red ? 0 : 1).ThenByDescending(p => p.BaseScore)
                : activePlayers.OrderByDescending(p => p.BaseScore);

            _persistentLeaderboard = ordered
                .Take(ConfigMaxPlayers)
                .Concat(spectators)
                .ToList();

            _lastKnownRoundPlayers.Clear();
            foreach (var player in _persistentLeaderboard) {
                if (player == null || string.IsNullOrEmpty(player.Name)) continue;
                _lastKnownRoundPlayers[player.Name] = ClonePlayer(player);
            }
        }

        /// <summary>
        /// Reads one of the native scoreboard's entry lists into SBGL players. Team entries are
        /// limited to active ones: a player who switches team leaves a hidden, stale entry in the
        /// old team's list, because the game only hides it rather than clearing its text.
        /// </summary>
        private void ScrapeEntries(List<ScoreboardEntry> entries, Team team, bool activeOnly, string localName, List<SBGLPlayer> into)
        {
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                if (activeOnly && !entry.gameObject.activeSelf) continue;

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
                    bool strokesOnlyDashes = !string.IsNullOrEmpty(strokesStr) && Regex.IsMatch(strokesStr, @"^[\-–—\s]+$");
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

                into.Add(new SBGLPlayer {
                    Name = rawName,
                    BaseScore = baseScore,
                    RawStrokes = isSpec ? "SPEC" : strokesStr,
                    MMR = playerMMR,
                    ProjectedDisplay = playerMMR,
                    IsSpectator = isSpec,
                    ClanTag = clanTag ?? "",
                    CanonicalName = canonicalName ?? "",
                    Team = team,
                    IsLocal = localName.Length > 0 && string.Equals(rawName, localName, System.StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        /// <summary>The local player's name as the scoreboard prints it, or "" when unknown.</summary>
        private string GetLocalPlayerName()
        {
            try
            {
                var state = CourseManager.GetLocalPlayerState();
                if (string.IsNullOrEmpty(state.name)) return "";
                return CleanTMP(GameManager.RichTextNoParse(state.name));
            }
            catch { return ""; }
        }

        private static bool IsTeamGameMode()
        {
            try { return MatchSetupMenu.CurrentGameMode == GameMode.Teams; }
            catch { return false; }
        }

        /// <summary>Copies both teams' hole wins and this-hole points from the game.</summary>
        private void RefreshLiveTeamScores()
        {
            try
            {
                if (!SingletonNetworkBehaviour<CourseManager>.HasInstance) return;
                var states = CourseManager.TeamStates;
                if (states == null) return;
                if (states.TryGetValue(Team.Red, out var red))
                {
                    _liveRedWins = red.courseScore;
                    _liveRedHolePts = red.matchScore;
                }
                if (states.TryGetValue(Team.Blue, out var blue))
                {
                    _liveBlueWins = blue.courseScore;
                    _liveBlueHolePts = blue.matchScore;
                }
            }
            catch { }
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
                CanonicalName = source.CanonicalName,
                Team = source.Team,
                IsLocal = source.IsLocal
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
            // A new lobby starts with no hole info, so its Driving Range shows none
            _cachedHoleNum = 0;
            _cachedHolePar = -1;
            _cachedPlayedPar = 0;
            _cachedHoleGlobalIndex = -1;
            _cachedCourse = null;
            _cachedCourseName = "";

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
            _liveIsTeamMatch = false;
            _finalIsTeamMatch = false;
            _liveRedWins = _liveBlueWins = _liveRedHolePts = _liveBlueHolePts = 0;
            _finalRedWins = _finalBlueWins = _finalRedHolePts = _finalBlueHolePts = 0;
            SbglOverlayOpen = false;
            _nextUpdateTime = Time.time + _updateInterval;
            Debug.Log("[LiveLeaderboard] Reset leaderboard state on main menu");
        }

        /// <summary>
        /// Clears the previous match's live standings between matches in the same lobby. They
        /// used to survive until the main menu, so the next match's roster - read at round start,
        /// before its first scoreboard - came from the previous match, including players who had
        /// since left. The final snapshot is kept for the Driving Range display.
        /// </summary>
        public void ResetForNewMatch()
        {
            _persistentLeaderboard.Clear();
            _lastKnownRoundPlayers.Clear();
            _roundCacheActive = false;
            _liveIsTeamMatch = false;
            _liveRedWins = _liveBlueWins = _liveRedHolePts = _liveBlueHolePts = 0;
        }

        /// <summary>
        /// Captures the current leaderboard state as a final snapshot before leaving gameplay.
        /// Call this when transitioning away from a gameplay scene to preserve match-end scores.
        /// </summary>
        public void CaptureLeaderboardSnapshot()
        {
            _finalLeaderboardSnapshot = _persistentLeaderboard.Select(ClonePlayer).Where(p => p != null).ToList();
            // Freeze the team result alongside the players so the Driving Range shows the final score
            RefreshLiveTeamScores();
            _finalIsTeamMatch = _liveIsTeamMatch;
            _finalRedWins = _liveRedWins;
            _finalBlueWins = _liveBlueWins;
            _finalRedHolePts = _liveRedHolePts;
            _finalBlueHolePts = _liveBlueHolePts;
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