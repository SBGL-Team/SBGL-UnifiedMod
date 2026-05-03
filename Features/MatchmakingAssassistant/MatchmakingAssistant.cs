using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FMODUnity;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using Newtonsoft.Json.Linq;
using SBGL.UnifiedMod.Core;

// Explicit alias for Facepunch.Steamworks
using FacepunchLib = Steamworks; 

namespace SBGLeagueAutomation
{
    public class SBGLPlugin : MonoBehaviour
    {
        // API Configuration - dynamically sourced from UnifiedPlugin
        // Only one field for randomize-all-players config
        // Per-match random mapping: in-game name -> SBGL name
        private Dictionary<string, string> _randomizedPlayerMap = null;
        private static readonly List<string> _sbglTestNames = new List<string> {
            "TrashxCat","KingCire03","FucklTheIRS","Jackie","TikTok Z4C_FN","adthykrshnn","mrguy7608","Noah_Boatt","YoMaMMeJr","TheMrEStudio","Odius9064","Jake paul","f19chy","Kodakblackarack","ArrowTheFighter","limbo","Marcus_Pipes","That Traynor","Blinkerfluid","TaxiCAB","Achunl2","Cinereous","Liafeon","Peter","Inkie","LLENN","Nanainasuit","Midnight","PattyTits","Bekuh","Lunwik","yackback","AlpineMilk","HandsomeSkippy","TheOneKP","RealJosher","Ooshida","Moto","Jozza","Patrick Swayze","ryan.scibetta","Zaikr","Thalosii","paradiorevey","Ricee","Slacker87","MRGoldberg","trusted","designedsilence","The Oreo Orgy","Zoboomafoo","Yoda Cage","Black Dolphin","Pengini","antyde","Glider","lyth","Jabobus.o7","dkgaming219","Cody","Sidimmu","снусмумрик","Skydown26","Jeb","Vac","Slem Dogg","Batto","Broj0e"
        };
        private string GetBaseApiUrl() => UnifiedPlugin.GetCurrentPlayerApi().Replace("/Player", "");
        private string GetAppId() => UnifiedPlugin.GetCurrentAppId();
        private string GetAuthToken() => UnifiedPlugin.GetCurrentAuthToken();

        // Config Entries
        private ConfigEntry<bool> _showLogsConfig;
        private ConfigEntry<bool> _showFlowDebugConfig;
        private ConfigEntry<bool> _showUploadNoticesConfig;
        private ConfigEntry<bool> _ignoreSbglLobbyRequirementConfig;
        private ConfigEntry<string> _testPlayerOverridesConfig;
        private ConfigEntry<bool> _testRandomizeAllPlayersConfig;
        private ManualLogSource _bepinexLogger;
        private bool _isInitializing = true;
        private int _onlineCount = 0;
        private int _queuedCount = 0;
        private int _matchedCount = 0;

        public void SetConfig(
            ConfigEntry<bool> showLogs,
            ConfigEntry<bool> showFlowDebug,
            ConfigEntry<bool> showUploadNotices,
            ConfigEntry<bool> ignoreSbglLobbyRequirement,
            ConfigEntry<string> testPlayerOverrides,
            ConfigEntry<bool> testRandomizeAllPlayers,
            ManualLogSource bepinexLogger)
        {
            _showLogsConfig = showLogs;
            _showFlowDebugConfig = showFlowDebug;
            _showUploadNoticesConfig = showUploadNotices;
            _ignoreSbglLobbyRequirementConfig = ignoreSbglLobbyRequirement;
            _testPlayerOverridesConfig = testPlayerOverrides;
            _testRandomizeAllPlayersConfig = testRandomizeAllPlayers;
            _bepinexLogger = bepinexLogger;
        }

        /// <summary>
        /// Parses the TestPlayerOverrides config value into a dictionary mapping
        /// in-game Steam names to real SBGL player names for offline testing.
        /// Format: "SteamName1=SBGLName1,SteamName2=SBGLName2"
        /// </summary>
        private Dictionary<string, string> GetTestPlayerOverrides(List<string> currentPlayerNames = null) {
            // If randomize-all-players is enabled, build a random mapping for this match
            if (_testRandomizeAllPlayersConfig != null && _testRandomizeAllPlayersConfig.Value && currentPlayerNames != null)
            {
                // Only (re)generate the mapping if not already present or player list changed
                if (_randomizedPlayerMap == null || _randomizedPlayerMap.Count != currentPlayerNames.Count || !_randomizedPlayerMap.Keys.SequenceEqual(currentPlayerNames))
                {
                    var availableNames = new List<string>(_sbglTestNames);
                    var rand = new System.Random();
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var shuffled = availableNames.OrderBy(x => rand.Next()).ToList();
                    int idx = 0;
                    foreach (var name in currentPlayerNames)
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        // Don't override the local player
                        if (_userProfile != null && string.Equals(name, _userProfile.display_name, StringComparison.OrdinalIgnoreCase))
                        {
                            map[name] = name;
                            continue;
                        }
                        if (idx >= shuffled.Count) idx = 0;
                        map[name] = shuffled[idx++];
                    }
                    _randomizedPlayerMap = map;
                }
                return _randomizedPlayerMap;
            }

            // Default: parse manual overrides
            var manualMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string raw = _testPlayerOverridesConfig?.Value;
            if (string.IsNullOrWhiteSpace(raw)) return manualMap;

            foreach (string pair in raw.Split(',')) {
                int eq = pair.IndexOf('=');
                if (eq <= 0 || eq >= pair.Length - 1) continue;
                string steamName = pair.Substring(0, eq).Trim();
                string sbglName  = pair.Substring(eq + 1).Trim();
                if (!string.IsNullOrWhiteSpace(steamName) && !string.IsNullOrWhiteSpace(sbglName))
                    manualMap[steamName] = sbglName;
            }

            return manualMap;
        }

        /// <summary>
        /// Returns the SBGL player name to look up for a given in-game display name,
        /// substituting a test override when one is configured.
        /// </summary>
        private string ApplyTestPlayerOverride(string displayName, Dictionary<string, string> overrides) {
            if (overrides != null && overrides.TryGetValue(displayName, out string sbglName)) {
                Log($"<color=magenta>[Test Override] '{displayName}' → '{sbglName}'</color>");
                return sbglName;
            }
            return displayName;
        }

        private bool IgnoreSbglLobbyRequirementEnabled => _ignoreSbglLobbyRequirementConfig?.Value ?? false;

        // State Tracking
        public static bool IsRankedTriggered = false;
        private bool _isQueueing = false;
        private string _webStatus = "IDLE";
        private string _currentQueueId = "";
        private PlayerProfile _userProfile = null;
        private MatchmakingSession _currentSession = null;
        private bool _isHost = false;
        private bool _hasAccepted = false;
        private string _hostRulesetSelection = "ranked"; // "ranked" or "pro_series"
        private DateTime? _queueStartTime = null;
        private bool _hostLobbyStarted = false;
        private bool _hostServerWasActive = false;
        private bool _hostCancelSent = false;
#pragma warning disable CS0414
        private bool _matchStatsSubmitted = false;
        private bool _proSeriesSkipLogged = false;
#pragma warning restore CS0414
        private DateTime? _matchStartTime = null;
        private Dictionary<string, int> _cachedLeaderboardScores = new Dictionary<string, int>();
        private Dictionary<string, int> _cachedLeaderboardScoresVsPar = new Dictionary<string, int>();
        
        // Progressive match tracking - for updating scores after each hole
        private string _currentMatchId = null;
        private Dictionary<string, string> _playerMatchEntryIds = new Dictionary<string, string>(); // player_id -> entry_id
        private HashSet<string> _matchEntryCreationInProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _playerIdsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // player_name -> player_id
        private Dictionary<string, int> _lastSubmittedScores = new Dictionary<string, int>(); // player_name -> score
        private Dictionary<string, int> _lastSubmittedScoresVsPar = new Dictionary<string, int>(); // player_name -> vs_par
        private int _matchExpectedPlayerCount = 2;
        private int _lastUploadedPlayerCount = -1;
        private List<CachedLeaderboardPlayer> _finalLeaderboardSnapshot = new List<CachedLeaderboardPlayer>();
        private byte[] _pendingMatchScreenshotBytes = null;
        private string _pendingMatchScreenshotFileName = null;
        private string _pendingUploadedScreenshotUrl = null;
        private bool _matchScreenshotUploadInProgress = false;
        private bool _matchScreenshotUploadCompleted = false;
        private bool _matchEntriesCreated = false;
        private bool _matchCreationInProgress = false;
        private bool _isInGameplay = false;
        private bool _matchEndedReceived = false;
        private Coroutine _monitorCoroutine = null;
        private Coroutine _lobbyMonitorCoroutine = null;
        private Coroutine _endOfMatchSignalCoroutine = null;
        private Coroutine _matchScreenshotSignalCoroutine = null;
        private Coroutine _courseEndRpcCoroutine = null;
        private float _nextEnsureMatchCreateAttemptAt = 0f;
        private string _localManualSessionId = null;

        private const int MatchScreenshotMaxWidth = 1920;
        private const int MatchScreenshotMaxHeight = 1080;
        private const int MatchScreenshotTargetMaxBytes = 800 * 1024;
        private const int MatchScreenshotHardMaxBytes = 1024 * 1024;
        
        // Active season cache - fetched from API at startup
        private string _activeSeasonId = null;
        private bool _activeSeasonFetched = false;
        
        // UI Helpers
        private List<string> _debugLogs = new List<string>();
        private List<PlayerData> _queuedPlayers = new List<PlayerData>();
        private Vector2 _logScroll;
        private Texture2D _profileTexture = null;
        private Texture2D _solidBgTex = null;
        private bool _hasFetchedProfilePic = false;
        private GUIStyle _centerLabelStyle = null;
        private GUIStyle _debugLineStyle = null;
        
        // Upload notifications
        private string _uploadNotification = "";
        private DateTime _uploadNotificationTime = DateTime.MinValue;
        private const float _uploadNotificationDuration = 4f; // Show for 4 seconds
        private Color _uploadNotificationColor = new Color(0.2f, 0.85f, 1f);
        // Temporary live diagnostics for lobby-name resolution and mode/ruleset source.
        private string _debugLobbySessionSource = "";
        private string _debugLobbyPrefsSource = "";
        private string _debugLobbyCapturedSource = "";
        private string _debugLobbyResolved = "";
        private string _debugLobbyResolvedBy = "none";

        // Match Result Submission Service
        private MatchResultSubmissionService _matchResultSubmission;

        // ==========================================
        // PUBLIC ACCESSORS (for PseudoDedicatedServer)
        // ==========================================
        public bool IsQueueing => _isQueueing;
        public bool IsHost => _isHost;
        public bool HasAccepted => _hasAccepted;
        public PlayerProfile UserProfile => _userProfile;
        public MatchmakingSession CurrentSession => _currentSession;

        /// <summary>Starts the matchmaking queue. Safe to call from PseudoDedicatedServer.</summary>
        public IEnumerator MatchmakingLoopCoroutine() => MatchmakingLoop();

        /// <summary>Accepts the current pending match. Safe to call from PseudoDedicatedServer.</summary>
        public IEnumerator AcceptMatchCoroutine() => AcceptMatch();

        /// <summary>Initiates the host lobby sequence. Reads HostRuleset from PlayerPrefs.</summary>
        public void InitiateHostSequencePublic() => InitiateHostSequence();

        /// <summary>
        /// PATCHes the current session's host_player_id to this player's ID, then updates
        /// local state so _isHost becomes true.  Called by PseudoDedicatedServer before
        /// accepting so the existing host-flow logic takes over automatically.
        /// </summary>
        public IEnumerator ClaimHostRoleCoroutine()
        {
            if (_currentSession == null || _userProfile == null) yield break;

            // Already host — nothing to do
            if (_isHost) yield break;

            Log($"<color=cyan>[PDS] Claiming host role for session {_currentSession.id}...</color>");

            string json = "{\"host_player_id\":\"" + _userProfile.id + "\"}";
            yield return CallAPI($"/MatchmakingSession/{_currentSession.id}", "PUT", json, (res) => {
                JObject response = ParseApiSingleObject(res);
                if (response != null)
                {
                    _currentSession.host_player_id = _userProfile.id;
                    _isHost = true;
                    Log("<color=green>[PDS] ✓ Host role claimed — session host_player_id set to our player ID.</color>");
                }
                else
                {
                    Log("<color=red>[PDS] Failed to claim host role — API response was null.</color>");
                }
            });
        }

        private static readonly WaitForSeconds _syncLoopDelay = new WaitForSeconds(5.0f);
        private static readonly WaitForSeconds _readyTransitionDelay = new WaitForSeconds(0.5f);

        // Temporary diagnostics for host/upload/join flow verification
        private int _syncTickCount = 0;
        private int _lobbyCreatedEventCount = 0;
        private int _steamLinkUploadAttempts = 0;
        private int _steamLinkUploadSuccesses = 0;
        private int _steamLinkUploadFailures = 0;
        private int _autoJoinAttempts = 0;
        private int _autoJoinSuccesses = 0;
        private int _autoJoinFailures = 0;
        private DateTime? _lastLobbyCreatedAt = null;
        private DateTime? _lastUploadAttemptAt = null;
        private DateTime? _lastUploadSuccessAt = null;
        private DateTime? _lastAutoJoinAttemptAt = null;
        private DateTime? _lastAutoJoinSuccessAt = null;
        private string _lastGeneratedSteamLink = "-";
        private string _lastUploadedSteamLink = "-";
        private string _lastAutoJoinSteamLink = "-";
        private string _lastUploadError = "-";
        private string _lastAutoJoinError = "-";

        private void Awake() {
            _solidBgTex = new Texture2D(1, 1);
            _solidBgTex.SetPixel(0, 0, new Color(0.02f, 0.02f, 0.02f, 1.0f)); 
            _solidBgTex.Apply();

            SceneManager.sceneLoaded += OnSceneLoaded;
            CourseManager.MatchStateChanged += OnCourseManagerMatchStateChanged;
            CourseManager.ForceDisplayScoreboardChanged += OnCourseManagerForceDisplayScoreboardChanged;

            new Harmony("com.sbgl.matchmaking").PatchAll();
            Log("Plugin Loaded v6.2.1. Self-Sync Reconciliation Active.");
            
            // Initialize Match Result Submission Service
            _matchResultSubmission = new MatchResultSubmissionService(
                getBaseApiUrl: GetBaseApiUrl,
                logger: Log,
                callApi: CallAPI,
                parseApiSingleObject: ParseApiSingleObject,
                startCoroutine: (coro) => StartCoroutine(coro)
            );
            
            // Hook into unified plugin API changes
            UnifiedPlugin.ApiConfigChanged += OnApiConfigChanged;
            
            StartCoroutine(BackgroundSyncLoop());
        }

        private void OnApiConfigChanged() {
            Log("⚡ API Configuration changed - Switching environments and refreshing profile");
            // Reset user profile to force re-resolution with new API endpoints
            _userProfile = null;
            _isInitializing = true;
            _webStatus = "ENVIRONMENT SWITCHING...";
            // Clear session state to prevent stale data
            ResetPluginState();
        }

        private void OnEnable() {
            try {
                FacepunchLib.SteamMatchmaking.OnLobbyCreated += OnLobbyCreatedCallback;
            } catch (System.Exception ex) {
                Log($"Warning: Could not hook LobbyCreated event: {ex.Message}");
            }
        }

        private void OnDisable() {
            try {
                FacepunchLib.SteamMatchmaking.OnLobbyCreated -= OnLobbyCreatedCallback;
            } catch { }
        }

        private void OnLobbyCreatedCallback(FacepunchLib.Result result, FacepunchLib.Data.Lobby lobby) {
            if (result != FacepunchLib.Result.OK) {
                _lastUploadError = $"Lobby create callback failed: {result}";
                Log($"Lobby creation failed: {result}");
                return;
            }
            
            if (!lobby.Id.IsValid) {
                _lastUploadError = "Lobby created callback had invalid lobby id.";
                Log("Lobby ID is invalid!");
                return;
            }

            if (!_isHost || _currentSession == null) return;

            ulong lobbyId = lobby.Id.Value;
            ulong mySteamId = 0;
            try {
                if (FacepunchLib.SteamClient.IsValid) mySteamId = FacepunchLib.SteamClient.SteamId.Value;
            } catch { }

            if (mySteamId != 0) {
                string steamLink = $"steam://joinlobby/4069520/{lobbyId}/{mySteamId}";
                _lobbyCreatedEventCount++;
                _lastLobbyCreatedAt = DateTime.Now;
                _lastGeneratedSteamLink = steamLink;
                Log($"Lobby created! ID: {lobbyId}, uploading link...");
                StartCoroutine(UploadSteamLobbyLink(steamLink));
            } else {
                _lastUploadError = "Steam client invalid while trying to build host steam link.";
                Log("<color=orange>Lobby created but Steam ID could not be resolved.</color>");
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            string sceneName = scene.name.ToLower();
            Log($"<color=cyan>[Scene] Loaded: {scene.name}</color>");

            string currentLobbyName = ResolveCurrentLobbyName();
            bool isSbglLobby = !string.IsNullOrEmpty(currentLobbyName) && currentLobbyName.StartsWith("SBGL-", StringComparison.OrdinalIgnoreCase);
            bool shouldTrackForUpload = IgnoreSbglLobbyRequirementEnabled || IsRankedTriggered || isSbglLobby;
            
            // Create match and entries when starting a ranked game (entering course/gameplay scene)
            // Typically scenes like "Forest" "Desert" etc are the actual gameplay scenes
            // Only create if session is 'ready' (all players accepted) to avoid premature creation
            if (!sceneName.Contains("menu") && !sceneName.Contains("drivingrange") && !sceneName.Contains("driving range") && !sceneName.Contains("lobby")) {
                // Always mark as in gameplay so mid-round lobby rename detection can work
                _isInGameplay = true;

                // If our previous MatchmakingSession was marked completed, clear it so
                // we can create a fresh Match record for this new round.
                if (_currentSession != null && string.Equals(_currentSession.status, "completed", StringComparison.OrdinalIgnoreCase)) {
                    Log("<color=cyan>[Match] Previous MatchmakingSession status was 'completed' — clearing session so new match can be created</color>");
                    _currentSession = null;
                }

                if (shouldTrackForUpload && (_currentSession != null && (_currentSession.status == "ready" || _currentSession.status == "in_progress") || _currentSession == null) && !_matchEntriesCreated) {
                    Log("<color=yellow>[Match] Entering gameplay - validating match eligibility...</color>");
                    // Validate match upload eligibility before creating entries
                    StartCoroutine(ValidateMatchUpload((shouldUpload) => {
                        if (shouldUpload)
                        {
                            Log("<color=yellow>[Match] Creating match records...</color>");
                            StartCoroutine(CreateMatchAndEntries());
                        }
                        else
                        {
                            Log("<color=orange>[Match] Match does not meet upload criteria at load - starting lobby rename monitor</color>");
                            // Don't mark _matchEntriesCreated=true here; let the rename monitor handle it if lobby changes
                        }
                    }));
                }

                // Always keep monitor alive during gameplay so late creation can happen even if match-start creation was missed.
                if (_monitorCoroutine == null) {
                    _monitorCoroutine = StartCoroutine(MonitorAndUpdateScores());
                }

                // Start lobby rename monitor so a mid-round rename to SBGL-* triggers upload
                if (_lobbyMonitorCoroutine != null) {
                    StopCoroutine(_lobbyMonitorCoroutine);
                }
                _lobbyMonitorCoroutine = StartCoroutine(MonitorLobbyNameForUpload());
            }
            
            // Capture final leaderboard scores before they're lost when leaving gameplay
            if (_isInGameplay && (sceneName.Contains("drivingrange") || sceneName.Contains("driving range"))) {
                _isInGameplay = false;

                StopGameplayCoroutinesForMatchEnd("scene transition to driving range");
                TryCaptureFinalLeaderboardSnapshot("scene transition");
            }
            
            // On return to driving range, finalize one last time before clearing per-match state.
            if (sceneName.Contains("drivingrange") || sceneName.Contains("driving range")) {
                if (!_matchStatsSubmitted && !string.IsNullOrEmpty(_currentMatchId)) {
                    Log("<color=cyan>[Match Stats] Returned to Driving Range - running final match finalization before reset...</color>");
                    StartCoroutine(FinalizeAndResetAfterDrivingRange());
                } else {
                    ResetPerMatchState();
                }
                
                // Cancel queue if player enters Driving Range while queued (but not if they already accepted a match)
                if (_isQueueing && !_hasAccepted) {
                    Log("<color=orange>[Queue] Player entered Driving Range while queued - cancelling queue entry...</color>");
                    StartCoroutine(LeaveQueue());
                }
            }
            
            // Reset state when returning to main menu
            if (sceneName.Contains("menu")) {
                // If host returns to menu after having an active hosted lobby, cancel the web session.
                if (_isHost && _hostLobbyStarted && _hostServerWasActive && _currentSession != null) {
                    StartCoroutine(CancelSessionAsHost("host_returned_to_menu"));
                }
                ResetPluginState();
                Log("Returned to Menu: State Reset.");
            }
        }

        private void OnCourseManagerMatchStateChanged(MatchState previousState, MatchState currentState) {
            // MatchState.Ended fires after each hole; ignore as a reliable course-end signal.
            if (!_isInGameplay) return;
            Log($"<color=cyan>[Match Signal] CourseManager.MatchStateChanged: {previousState} -> {currentState} (ignored for course-end)</color>");
        }

        private void OnCourseManagerForceDisplayScoreboardChanged() {
            if (!_isInGameplay || !CourseManager.ForceDisplayScoreboard) {
                return;
            }
            // ForceDisplayScoreboard fires after each hole — capture a per-hole screenshot
            // and keep it as the pending screenshot (do not upload here). The authoritative
            // course-end upload will be driven by CourseManager.RpcInformEndingCourse (patched).
            Log("<color=cyan>[Match Signal] CourseManager forced the scoreboard (capture-only)</color>");

            if (_matchScreenshotSignalCoroutine == null && !_matchScreenshotUploadCompleted) {
                _matchScreenshotSignalCoroutine = StartCoroutine(CaptureAndStorePerHoleScreenshot("CourseManager.ForceDisplayScoreboardChanged"));
            }
        }

        private IEnumerator CaptureAndStorePerHoleScreenshot(string source) {
            try {
                // Capture immediately when the scoreboard appears to reduce chance of values clearing.
                yield return CaptureMatchScreenshotForReview(source + " (per-hole immediate)");
            } finally {
                _matchScreenshotSignalCoroutine = null;
            }
        }

        private IEnumerator CaptureAndUploadScreenshotFromForcedScoreboard(string source) {
            try {
                // Wait for the scoreboard to fully animate in before capturing.
                yield return new WaitForSeconds(2f);

                // Wait briefly for a final leaderboard snapshot to become available.
                // This prevents capturing mid-round scoreboard flashes (per-hole).
                float waited = 0f;
                float snapshotTimeout = 6f; // total wait time for final snapshot
                bool gotFinalSnapshot = false;
                while (waited < snapshotTimeout) {
                    // If match end signal already received, proceed immediately
                    if (_matchEndedReceived) { gotFinalSnapshot = true; break; }

                    // Try to capture a final leaderboard snapshot; if available, proceed
                    try {
                        if (TryCaptureFinalLeaderboardSnapshot(source + " (screenshot-wait)")) { gotFinalSnapshot = true; break; }
                    } catch { }

                    yield return new WaitForSeconds(0.25f);
                    waited += 0.25f;
                }

                if (!gotFinalSnapshot) {
                    Log($"<color=yellow>[Match Screenshot] Final leaderboard snapshot not available within {snapshotTimeout}s - skipping forced-scoreboard capture ({source})</color>");
                    yield break;
                }

                yield return CaptureMatchScreenshotForReview(source);
                yield return UploadCapturedMatchScreenshotIfNeeded(source);
            }
            finally {
                _matchScreenshotSignalCoroutine = null;
            }
        }

        private void TryBeginAssemblyDrivenFinalization(string source) {
            if (_endOfMatchSignalCoroutine != null || _matchStatsSubmitted || string.IsNullOrWhiteSpace(_currentMatchId)) {
                return;
            }

            _endOfMatchSignalCoroutine = StartCoroutine(FinalizeFromGameplaySignal(source));
        }

        private IEnumerator FinalizeFromGameplaySignal(string source) {
            Log($"<color=cyan>[Match Signal] {source} detected - attempting early end-of-match finalization...</color>");
            StopGameplayCoroutinesForMatchEnd(source);

            yield return CaptureMatchScreenshotForReview(source);

            for (int attempt = 0; attempt < 12; attempt++) {
                if (TryCaptureFinalLeaderboardSnapshot(source)) {
                    break;
                }

                yield return new WaitForSeconds(0.25f);
            }

            if (_finalLeaderboardSnapshot == null || _finalLeaderboardSnapshot.Count == 0) {
                yield return UploadCapturedMatchScreenshotIfNeeded($"{source} (no final snapshot yet)");
                Log($"<color=yellow>[Match Signal] {source} did not yield a final snapshot yet - falling back to Driving Range finalization</color>");
                _endOfMatchSignalCoroutine = null;
                yield break;
            }

            if (!_matchStatsSubmitted && !string.IsNullOrWhiteSpace(_currentMatchId)) {
                yield return FinalizeMatchStats();

                if (IsRankedTriggered && _currentSession != null) {
                    yield return UpdateSessionStatus("completed");
                }
            }

            yield return UploadCapturedMatchScreenshotIfNeeded(source);

            _endOfMatchSignalCoroutine = null;
        }

        private void StopGameplayCoroutinesForMatchEnd(string source) {
            if (_monitorCoroutine != null) {
                StopCoroutine(_monitorCoroutine);
                _monitorCoroutine = null;
                Log($"<color=cyan>[Match] Score monitoring stopped ({source})</color>");
            }

            if (_lobbyMonitorCoroutine != null) {
                StopCoroutine(_lobbyMonitorCoroutine);
                _lobbyMonitorCoroutine = null;
            }
        }

        private bool TryCaptureFinalLeaderboardSnapshot(string source) {
            try {
                var liveLeaderboard = UnityEngine.Object.FindAnyObjectByType<SBGLLiveLeaderboard.LiveLeaderboardPlugin>(FindObjectsInactive.Include);
                if (liveLeaderboard == null) {
                    return _finalLeaderboardSnapshot != null && _finalLeaderboardSnapshot.Count > 0;
                }

                liveLeaderboard.CaptureLeaderboardSnapshot();
                CacheLeaderboardSnapshot(liveLeaderboard.GetFinalLeaderboardSnapshot(), source);

                if (_finalLeaderboardSnapshot != null && _finalLeaderboardSnapshot.Count > 0) {
                    Log($"<color=green>[Match] ✓ Leaderboard snapshot captured from {source}</color>");
                    return true;
                }
            } catch (System.Exception ex) {
                Log($"<color=yellow>[Match] Could not capture {source} snapshot: {ex.Message}</color>");
            }

            return _finalLeaderboardSnapshot != null && _finalLeaderboardSnapshot.Count > 0;
        }

        private IEnumerator CaptureMatchScreenshotForReview(string source) {
            // If upload already finished or currently uploading, don't capture.
            if (_matchScreenshotUploadCompleted || _matchScreenshotUploadInProgress) {
                yield break;
            }

            // If we already have a pending screenshot, we'll replace it with the newest
            // capture so the final upload uses the latest per-hole scoreboard.
            if (_pendingMatchScreenshotBytes != null && _pendingMatchScreenshotBytes.Length > 0) {
                Log($"<color=cyan>[Match Screenshot] Replacing previous pending screenshot with a newer capture ({source})</color>");
            }

            float waitedSeconds = 0f;
            while (!CourseManager.ForceDisplayScoreboard && waitedSeconds < 4f) {
                yield return null;
                waitedSeconds += Mathf.Max(Time.unscaledDeltaTime, 0.016f);
            }

            if (!CourseManager.ForceDisplayScoreboard) {
                Log($"<color=yellow>[Match Screenshot] Forced scoreboard was not visible in time for {source} - skipping capture for now</color>");
                yield break;
            }

            // Capture as soon as possible: one end-of-frame should be sufficient.
            yield return new WaitForEndOfFrame();

            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            if (screenWidth <= 0 || screenHeight <= 0) {
                Log("<color=yellow>[Match Screenshot] Screen dimensions were invalid - skipping capture</color>");
                yield break;
            }

            Texture2D capturedTexture = null;
            Texture2D encodedTexture = null;

            try {
                capturedTexture = new Texture2D(screenWidth, screenHeight, TextureFormat.RGB24, false);
                capturedTexture.ReadPixels(new Rect(0f, 0f, screenWidth, screenHeight), 0, 0, false);
                capturedTexture.Apply(false, false);

                encodedTexture = ResizeTextureToFit(capturedTexture, MatchScreenshotMaxWidth, MatchScreenshotMaxHeight);
                byte[] imageBytes = EncodeScreenshotBytes(encodedTexture);
                if (imageBytes == null || imageBytes.Length == 0) {
                    Log("<color=yellow>[Match Screenshot] JPEG encoding returned no data - skipping capture</color>");
                    yield break;
                }

                // Replace pending screenshot bytes with this newest capture.
                _pendingMatchScreenshotBytes = imageBytes;
                _pendingMatchScreenshotFileName = BuildMatchScreenshotFileName();
                _pendingUploadedScreenshotUrl = null;
                Log($"<color=green>[Match Screenshot] ✓ Captured final scoreboard screenshot from {source} ({encodedTexture.width}x{encodedTexture.height}, {imageBytes.Length / 1024f:0.#} KB)</color>");
            } catch (System.Exception ex) {
                Log($"<color=yellow>[Match Screenshot] Could not capture screenshot from {source}: {ex.Message}</color>");
            } finally {
                if (encodedTexture != null && !ReferenceEquals(encodedTexture, capturedTexture)) {
                    UnityEngine.Object.Destroy(encodedTexture);
                }

                if (capturedTexture != null) {
                    UnityEngine.Object.Destroy(capturedTexture);
                }
            }
        }

        private IEnumerator UploadCapturedMatchScreenshotIfNeeded(string source) {
            if (_matchScreenshotUploadCompleted || _matchScreenshotUploadInProgress) {
                yield break;
            }

            if ((string.IsNullOrWhiteSpace(_pendingUploadedScreenshotUrl) && (_pendingMatchScreenshotBytes == null || _pendingMatchScreenshotBytes.Length == 0))
                || string.IsNullOrWhiteSpace(_currentMatchId)) {
                yield break;
            }

            _matchScreenshotUploadInProgress = true;
            try {
                float staggerDelay = GetCurrentPlayerScreenshotUploadDelaySeconds();
                if (staggerDelay > 0f) {
                    yield return new WaitForSeconds(staggerDelay);
                }

                string existingScreenshotUrl = null;
                bool readSucceeded = false;
                yield return CallAPI($"/Match/{_currentMatchId}", "GET", "", (res) => {
                    readSucceeded = true;
                    JObject match = ParseApiSingleObject(res);
                    existingScreenshotUrl = (string)match?["screenshot_url"];
                });

                if (readSucceeded && !string.IsNullOrWhiteSpace(existingScreenshotUrl)) {
                    _matchScreenshotUploadCompleted = true;
                    ClearPendingMatchScreenshot();
                    Log($"<color=cyan>[Match Screenshot] Match {_currentMatchId} already has a screenshot - skipping duplicate upload ({source})</color>");
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(_pendingUploadedScreenshotUrl)) {
                    string uploadedFileUrl = null;
                    yield return UploadMatchScreenshotFile((url) => uploadedFileUrl = url);

                    if (string.IsNullOrWhiteSpace(uploadedFileUrl)) {
                        Log($"<color=yellow>[Match Screenshot] UploadFile did not return a file URL ({source}) - keeping screenshot for retry</color>");
                        yield break;
                    }

                    _pendingUploadedScreenshotUrl = uploadedFileUrl;
                }

                var payload = new JObject {
                    ["screenshot_url"] = _pendingUploadedScreenshotUrl
                };

                bool uploadSucceeded = false;
                yield return CallAPI($"/Match/{_currentMatchId}", "PUT", payload.ToString(Newtonsoft.Json.Formatting.None), (res) => {
                    JObject response = ParseApiSingleObject(res);
                    uploadSucceeded = response != null;
                });

                if (uploadSucceeded) {
                    _matchScreenshotUploadCompleted = true;
                    ClearPendingMatchScreenshot();
                    Log($"<color=green>[Match Screenshot] ✓ Attached scoreboard screenshot to Match {_currentMatchId}</color>");
                    ShowUploadNotification("Attached end-of-match screenshot for review.", "info");
                } else {
                    Log($"<color=yellow>[Match Screenshot] Screenshot upload did not succeed yet ({source}) - will keep the screenshot for fallback retry</color>");
                }
            } finally {
                _matchScreenshotUploadInProgress = false;
            }
        }

        private IEnumerator UploadMatchScreenshotFile(Action<string> onUploaded) {
            if (_pendingMatchScreenshotBytes == null || _pendingMatchScreenshotBytes.Length == 0) {
                onUploaded?.Invoke(null);
                yield break;
            }

            string uploadUrl = GetCoreIntegrationEndpointUrl("UploadFile");
            WWWForm form = new WWWForm();
            form.AddBinaryData("file", _pendingMatchScreenshotBytes, _pendingMatchScreenshotFileName ?? BuildMatchScreenshotFileName(), "image/jpeg");

            Log($"<color=cyan>[Match Screenshot] Uploading screenshot file via Core/UploadFile ({_pendingMatchScreenshotBytes.Length / 1024f:0.#} KB)</color>");

            using (UnityWebRequest req = UnityWebRequest.Post(uploadUrl, form)) {
                ApplyIntegrationHeaders(req);
                req.SetRequestHeader("Accept", "application/json");

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success) {
                    JObject response = ParseApiSingleObject(req.downloadHandler.text);
                    string fileUrl = (string)response?["file_url"] ?? (string)response?["url"];
                    if (!string.IsNullOrWhiteSpace(fileUrl)) {
                        Log($"<color=green>[Match Screenshot] ✓ Screenshot uploaded to file storage</color>");
                        onUploaded?.Invoke(fileUrl);
                    } else {
                        Log("<color=yellow>[Match Screenshot] UploadFile succeeded but response did not contain file_url</color>");
                        onUploaded?.Invoke(null);
                    }
                } else {
                    string errorMsg = $"[Match Screenshot] UploadFile failed: {req.result}";
                    if (!string.IsNullOrEmpty(req.error)) errorMsg += $" - {req.error}";
                    if (req.responseCode > 0) errorMsg += $" (HTTP {req.responseCode})";
                    if (!string.IsNullOrEmpty(req.downloadHandler?.text)) {
                        int length = Math.Min(240, req.downloadHandler.text.Length);
                        errorMsg += $" - Response: {req.downloadHandler.text.Substring(0, length)}";
                    }
                    Log($"<color=red>{errorMsg}</color>");
                    onUploaded?.Invoke(null);
                }
            }
        }

        private string BuildMatchScreenshotFileName() {
            string matchIdPart = !string.IsNullOrWhiteSpace(_currentMatchId) ? _currentMatchId : DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            return $"match-{matchIdPart}.jpg";
        }

        private string GetCoreIntegrationEndpointUrl(string functionName) {
            string baseApiUrl = GetBaseApiUrl();
            int entitiesIndex = baseApiUrl.LastIndexOf("/entities", StringComparison.OrdinalIgnoreCase);
            string appApiRoot = entitiesIndex >= 0 ? baseApiUrl.Substring(0, entitiesIndex) : baseApiUrl;
            return $"{appApiRoot}/integration-endpoints/Core/{functionName}";
        }

        private void ApplyIntegrationHeaders(UnityWebRequest req) {
            if (req == null) return;

            string authToken = GetAuthToken();
            if (!string.IsNullOrWhiteSpace(authToken)) {
                req.SetRequestHeader("api_key", authToken);
            }

            req.SetRequestHeader("X-App-Id", GetAppId());
        }

        private void ClearPendingMatchScreenshot() {
            _pendingMatchScreenshotBytes = null;
            _pendingMatchScreenshotFileName = null;
            _pendingUploadedScreenshotUrl = null;
        }

        /// <summary>
        /// Called by Harmony postfix (CourseManager client RPC) to trigger final upload
        /// when the server has informed clients that the course is ending.
        /// </summary>
        public void OnCourseRpcInformEndingCourse() {
            try {
                if (_courseEndRpcCoroutine == null) {
                    _courseEndRpcCoroutine = StartCoroutine(HandleCourseEndRpcCoroutine());
                }
            } catch (System.Exception ex) {
                Log($"<color=yellow>[Match Signal] Error starting CourseEnd RPC handler: {ex.Message}</color>");
            }
        }

        private IEnumerator HandleCourseEndRpcCoroutine() {
            try {
                Log("<color=cyan>[Match Signal] CourseManager client RPC InformEndingCourse received — preparing final upload</color>");

                // Attempt to capture or refresh the final leaderboard snapshot quickly
                for (int i = 0; i < 8; i++) {
                    try {
                        if (TryCaptureFinalLeaderboardSnapshot("CourseManager.RpcInformEndingCourse")) break;
                    } catch { }
                    yield return new WaitForSeconds(0.125f);
                }

                // Ensure we have a screenshot (overwrite any previous pending with the newest)
                yield return CaptureMatchScreenshotForReview("CourseManager.RpcInformEndingCourse (forced)");

                // Give a short window for match id to appear if it hasn't yet
                float waited = 0f;
                while (string.IsNullOrWhiteSpace(_currentMatchId) && waited < 6f) {
                    yield return new WaitForSeconds(0.25f);
                    waited += 0.25f;
                }

                // Try attaching/uploading the latest screenshot now
                yield return UploadCapturedMatchScreenshotIfNeeded("CourseManager.RpcInformEndingCourse");
            }
            finally {
                _courseEndRpcCoroutine = null;
            }
        }

        [HarmonyLib.HarmonyPatch]
        private static class CourseManager_RpcInformEndingCourse_Patch {
            private static System.Reflection.MethodBase TargetMethod() {
                var t = HarmonyLib.AccessTools.TypeByName("CourseManager");
                if (t == null) return null;
                return HarmonyLib.AccessTools.Method(t, "UserCode_RpcInformEndingCourse");
            }

            private static void Postfix() {
                try {
                    var plugin = UnityEngine.Object.FindAnyObjectByType<SBGLeagueAutomation.SBGLPlugin>(FindObjectsInactive.Include);
                    if (plugin != null) plugin.OnCourseRpcInformEndingCourse();
                } catch (System.Exception ex) {
                    UnityEngine.Debug.Log($"[SBGL.Matchmaking] CourseManager.RpcInformEndingCourse postfix error: {ex}");
                }
            }
        }

        private float GetCurrentPlayerScreenshotUploadDelaySeconds() {
            if (_currentSession?.player_ids == null || string.IsNullOrWhiteSpace(_userProfile?.id)) {
                return 0f;
            }

            List<string> orderedPlayerIds = _currentSession.player_ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            if (orderedPlayerIds.Count == 0) {
                return 0f;
            }

            int slotIndex = orderedPlayerIds.FindIndex(id => string.Equals(id, _userProfile.id, StringComparison.Ordinal));
            if (slotIndex <= 0) {
                return 0f;
            }

            const float secondsPerSlot = 0.5f;
            float delaySeconds = slotIndex * secondsPerSlot;
            Log($"<color=cyan>[Match Screenshot] Upload slot {slotIndex + 1}/{orderedPlayerIds.Count}; waiting {delaySeconds:0.#}s before checking Match.screenshot_url</color>");
            return delaySeconds;
        }

        private Texture2D ResizeTextureToFit(Texture2D source, int maxWidth, int maxHeight) {
            if (source == null || source.width <= 0 || source.height <= 0) {
                return source;
            }

            if (source.width <= maxWidth && source.height <= maxHeight) {
                return source;
            }

            float scale = Mathf.Min((float)maxWidth / source.width, (float)maxHeight / source.height);
            int targetWidth = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            try {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;

                Texture2D resized = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
                resized.ReadPixels(new Rect(0f, 0f, targetWidth, targetHeight), 0, 0, false);
                resized.Apply(false, false);
                return resized;
            } finally {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private byte[] EncodeScreenshotBytes(Texture2D texture) {
            if (texture == null) {
                return null;
            }

            byte[] bestBytes = null;
            int[] qualitySteps = { 90, 80, 70, 60, 50 };
            foreach (int quality in qualitySteps) {
                byte[] encoded = UnityEngine.ImageConversion.EncodeToJPG(texture, quality);
                if (encoded == null || encoded.Length == 0) {
                    continue;
                }

                bestBytes = encoded;
                if (encoded.Length <= MatchScreenshotTargetMaxBytes) {
                    return encoded;
                }
            }

            if (bestBytes != null && bestBytes.Length <= MatchScreenshotHardMaxBytes) {
                return bestBytes;
            }

            if (bestBytes != null) {
                Log($"<color=yellow>[Match Screenshot] Best JPEG was still too large ({bestBytes.Length / 1024f:0.#} KB) - skipping screenshot upload</color>");
            }

            return null;
        }

        private IEnumerator CheckAndSubmitMatchStats() {
            if (_userProfile == null || _currentSession == null) {
                Log("<color=red>[Match Stats] Failed: Missing profile or session</color>");
                yield break;
            }

            // Wait a moment for leaderboard to populate after returning to Driving Range
            Log($"<color=cyan>[Match Stats] Waiting for leaderboard to populate...</color>");
            yield return new WaitForSeconds(1.5f);

            // Query Match endpoint to check if entry already exists for this session
            string query = $"{{\"matchmaking_session_id\":\"{_currentSession.id}\"}}";
            string fullUrl = $"{GetBaseApiUrl()}/Match?q={UnityWebRequest.EscapeURL(query)}";
            
            Log($"<color=cyan>[Match Stats] Checking for existing match entry...</color>");
            Log($"<color=cyan>[Match Stats] Query URL: {fullUrl.Substring(0, Math.Min(150, fullUrl.Length))}...</color>");

            using (UnityWebRequest req = UnityWebRequest.Get(fullUrl)) {
                ApplyApiHeaders(req);
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success) {
                    List<JObject> existingMatches = ParseApiObjectList(req.downloadHandler.text);
                    
                    if (existingMatches.Count > 0) {
                        Log($"<color=orange>[Match Stats] Match already exists for this session (found {existingMatches.Count} entries)</color>");
                        _matchStatsSubmitted = true;
                        yield break;
                    }
                    
                    Log("<color=cyan>[Match Stats] No existing match found - proceeding with submission</color>");
                } else {
                    Log($"<color=yellow>[Match Stats] Could not query existing matches: {req.result} - proceeding anyway</color>");
                }
            }

            // No existing match found, proceed with submission
            yield return SubmitMatchStats();
        }

        private IEnumerator ResolveExistingMatchIdForCurrentSession(Action<string> onResolved, bool logMisses = true) {
            string resolvedId = null;

            string activeSessionId = _currentSession != null ? _currentSession.id : _localManualSessionId;
            if (string.IsNullOrWhiteSpace(activeSessionId)) {
                onResolved?.Invoke(null);
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(_currentSession?.match_id)) {
                resolvedId = _currentSession.match_id;
                Log($"<color=cyan>[Match Creation] Session already linked to Match ID {resolvedId}</color>");
                onResolved?.Invoke(resolvedId);
                yield break;
            }

            string query = $"{{\"matchmaking_session_id\":\"{activeSessionId}\"}}";
            string fullUrl = $"{GetBaseApiUrl()}/Match?q={UnityWebRequest.EscapeURL(query)}&limit=1&sort_by=created_date";

            using (UnityWebRequest req = UnityWebRequest.Get(fullUrl)) {
                ApplyApiHeaders(req);
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success) {
                    JObject existingMatch = ParseApiObjectList(req.downloadHandler.text).FirstOrDefault();
                    resolvedId = (string)existingMatch?["id"];
                    if (!string.IsNullOrWhiteSpace(resolvedId)) {
                        if (_currentSession != null) {
                            _currentSession.match_id = resolvedId;
                        }
                        Log($"<color=green>[Match Creation] ✓ Reusing existing Match ID from API for session {activeSessionId}: {resolvedId}</color>");
                    } else if (logMisses) {
                        Log($"<color=cyan>[Match Creation] No existing Match found yet for session {activeSessionId}</color>");
                    }
                } else {
                    Log($"<color=yellow>[Match Creation] Existing Match lookup failed: {req.result} - {req.error}</color>");
                }
            }

            onResolved?.Invoke(resolvedId);
        }

        private float GetCurrentPlayerMatchUploadDelaySeconds() {
            if (_currentSession?.player_ids == null || string.IsNullOrWhiteSpace(_userProfile?.id)) {
                return 0f;
            }

            List<string> orderedPlayerIds = _currentSession.player_ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            if (orderedPlayerIds.Count == 0) {
                return 0f;
            }

            int slotIndex = orderedPlayerIds.FindIndex(id => string.Equals(id, _userProfile.id, StringComparison.Ordinal));
            if (slotIndex <= 0) {
                return 0f;
            }

            const float secondsPerSlot = 2f;
            float delaySeconds = slotIndex * secondsPerSlot;
            Log($"<color=cyan>[Match Creation] Fallback upload slot {slotIndex + 1}/{orderedPlayerIds.Count}; waiting {delaySeconds:0.#}s before POST</color>");
            return delaySeconds;
        }

        private IEnumerator WaitForExistingMatchBeforeFallback(float delaySeconds, Action<string> onResolved) {
            if (_currentSession == null || delaySeconds <= 0f) {
                onResolved?.Invoke(null);
                yield break;
            }

            float waited = 0f;
            while (waited < delaySeconds) {
                string existingMatchId = null;
                yield return ResolveExistingMatchIdForCurrentSession((id) => existingMatchId = id, false);
                if (!string.IsNullOrEmpty(existingMatchId)) {
                    onResolved?.Invoke(existingMatchId);
                    yield break;
                }

                yield return new WaitForSeconds(1f);
                waited += 1f;
            }

            onResolved?.Invoke(null);
        }

        private string BuildStableManualLocalSessionId() {
            string steamLobbyId = SBGL.UnifiedMod.Features.CompetitivePluginCheck.CompetitivePluginCheck.GetCurrentSteamLobbyId();
            if (!string.IsNullOrWhiteSpace(steamLobbyId)) {
                return $"local-lobby-{steamLobbyId}";
            }

            string lobbyName = ResolveCurrentLobbyName();
            if (!string.IsNullOrWhiteSpace(lobbyName)) {
                string normalizedLobbyName = Regex.Replace(lobbyName.Trim(), @"[^A-Za-z0-9_-]+", "-").Trim('-');
                if (!string.IsNullOrWhiteSpace(normalizedLobbyName)) {
                    return $"local-name-{normalizedLobbyName}";
                }
            }

            return "local-manual";
        }

        private IEnumerator ResolveExistingMatchEntryId(string matchId, string playerId, Action<string> onResolved, bool logMisses = false) {
            string resolvedId = null;

            if (string.IsNullOrWhiteSpace(matchId) || string.IsNullOrWhiteSpace(playerId)) {
                onResolved?.Invoke(null);
                yield break;
            }

            string query = $"{{\"match_id\":\"{matchId}\",\"player_id\":\"{playerId}\"}}";
            yield return CallAPI($"/MatchEntry?q={UnityWebRequest.EscapeURL(query)}&limit=1&sort_by=created_date", "GET", "", (res) => {
                JObject existingEntry = ParseApiObjectList(res).FirstOrDefault();
                if (existingEntry != null) {
                    resolvedId = (string)existingEntry["id"];
                }
            });

            if (logMisses && string.IsNullOrWhiteSpace(resolvedId)) {
                Log($"<color=cyan>[Match Entry] No existing MatchEntry found yet for player {playerId}</color>");
            }

            onResolved?.Invoke(resolvedId);
        }

        private IEnumerator WaitForPendingMatchEntryCreation(string playerId, Action<string> onResolved) {
            float waited = 0f;

            while (!string.IsNullOrWhiteSpace(playerId) && _matchEntryCreationInProgress.Contains(playerId) && waited < 3f) {
                if (_playerMatchEntryIds.TryGetValue(playerId, out string entryId) && !string.IsNullOrWhiteSpace(entryId)) {
                    onResolved?.Invoke(entryId);
                    yield break;
                }

                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;
            }

            if (!string.IsNullOrWhiteSpace(playerId) && _playerMatchEntryIds.TryGetValue(playerId, out string resolvedEntryId) && !string.IsNullOrWhiteSpace(resolvedEntryId)) {
                onResolved?.Invoke(resolvedEntryId);
            } else {
                onResolved?.Invoke(null);
            }
        }

        private IEnumerator EnsureMatchEntryForPlayer(
            string sourceTag,
            string playerId,
            string playerName,
            string preMatchMmr,
            string postMatchMmr, // <-- new param
            int gamePoints,
            int scoreVsPar,
            int finishPosition,
            string notes,
            Action<string> onResolved)
        {
            if (string.IsNullOrWhiteSpace(_currentMatchId) || string.IsNullOrWhiteSpace(playerId)) {
                onResolved?.Invoke(null);
                yield break;
            }

            if (_playerMatchEntryIds.TryGetValue(playerId, out string cachedEntryId) && !string.IsNullOrWhiteSpace(cachedEntryId)) {
                onResolved?.Invoke(cachedEntryId);
                yield break;
            }

            if (_matchEntryCreationInProgress.Contains(playerId)) {
                yield return WaitForPendingMatchEntryCreation(playerId, onResolved);
                yield break;
            }

            _matchEntryCreationInProgress.Add(playerId);

            try {
                string existingEntryId = null;
                yield return ResolveExistingMatchEntryId(_currentMatchId, playerId, (id) => existingEntryId = id);

                string resolvedPlayerName = string.IsNullOrWhiteSpace(playerName) ? playerId : playerName.Trim();

                if (!string.IsNullOrWhiteSpace(existingEntryId)) {
                    _playerMatchEntryIds[playerId] = existingEntryId;
                    if (!string.IsNullOrWhiteSpace(resolvedPlayerName)) {
                        _playerIdsByName[resolvedPlayerName] = playerId;
                    }

                    Log($"<color=green>[{sourceTag}] ✓ Adopted existing MatchEntry for {resolvedPlayerName}: {existingEntryId}</color>");
                    onResolved?.Invoke(existingEntryId);
                    yield break;
                }

                int adjustedScore = gamePoints + (scoreVsPar * -10);
                var payload = new JObject {
                    ["match_id"] = _currentMatchId,
                    ["player_id"] = playerId,
                    ["player_name"] = resolvedPlayerName,
                    ["game_points"] = gamePoints,
                    ["over_under"] = scoreVsPar,
                    ["score_vs_par"] = scoreVsPar,
                    ["adjusted_match_score"] = adjustedScore,
                    ["notes"] = notes ?? string.Empty
                };
                float preMmr = 0, postMmr = 0;
                if (!string.IsNullOrWhiteSpace(preMatchMmr)
                    && float.TryParse(preMatchMmr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out preMmr)) {
                    payload["pre_match_mmr"] = preMmr;
                }
                if (!string.IsNullOrWhiteSpace(postMatchMmr)
                    && float.TryParse(postMatchMmr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out postMmr)) {
                    payload["post_match_mmr"] = postMmr;
                    payload["mmr_change"] = postMmr - preMmr;
                }

                if (finishPosition > 0) {
                    payload["finish_position"] = finishPosition;
                }

                string createdEntryId = null;
                yield return CallAPI("/MatchEntry", "POST", payload.ToString(Newtonsoft.Json.Formatting.None), (res) => {
                    try {
                        JObject response = ParseApiSingleObject(res);
                        if (response != null) {
                            createdEntryId = (string)response["id"];
                        }
                    } catch (System.Exception ex) {
                        Log($"<color=yellow>[{sourceTag}] Could not parse MatchEntry response: {ex.Message}</color>");
                    }
                });

                if (!string.IsNullOrWhiteSpace(createdEntryId)) {
                    _playerMatchEntryIds[playerId] = createdEntryId;
                    if (!string.IsNullOrWhiteSpace(resolvedPlayerName)) {
                        _playerIdsByName[resolvedPlayerName] = playerId;
                    }

                    Log($"<color=green>[{sourceTag}] ✓ MatchEntry created for {resolvedPlayerName}: {createdEntryId}</color>");
                }

                onResolved?.Invoke(createdEntryId);
            }
            finally {
                _matchEntryCreationInProgress.Remove(playerId);
            }
        }

        private IEnumerator FinalizeAndResetAfterDrivingRange() {
            if (!_matchStatsSubmitted && !string.IsNullOrEmpty(_currentMatchId)) {
                yield return FinalizeMatchStats();
            }

            yield return UploadCapturedMatchScreenshotIfNeeded("Driving Range fallback");

            if (IsRankedTriggered && _currentSession != null) {
                yield return UpdateSessionStatus("completed");
            }

            ResetPerMatchState();
        }

        private void ResetPerMatchState() {
            if (_endOfMatchSignalCoroutine != null) {
                StopCoroutine(_endOfMatchSignalCoroutine);
                _endOfMatchSignalCoroutine = null;
            }

            if (_matchScreenshotSignalCoroutine != null) {
                StopCoroutine(_matchScreenshotSignalCoroutine);
                _matchScreenshotSignalCoroutine = null;
            }

            _currentMatchId = null;
            _playerMatchEntryIds.Clear();
            _matchEntryCreationInProgress.Clear();
            _playerIdsByName.Clear();
            _lastSubmittedScores.Clear();
            _lastSubmittedScoresVsPar.Clear();
            _matchEntriesCreated = false;
            _matchStatsSubmitted = false;
            _proSeriesSkipLogged = false;
            _matchEndedReceived = false;
            ClearPendingMatchScreenshot();
            _matchScreenshotUploadInProgress = false;
            _matchScreenshotUploadCompleted = false;
            _localManualSessionId = null;
            _cachedLeaderboardScores.Clear();
            _cachedLeaderboardScoresVsPar.Clear();
            _finalLeaderboardSnapshot.Clear();
            MatchResultSubmissionService.ReceivedP2PMatchId = null;
            _lastUploadedPlayerCount = -1;
            Log("<color=cyan>[Match] Per-match state reset - ready for new round</color>");
        }

        private void ResetPluginState() {
            // Stop monitoring coroutine if running
            if (_monitorCoroutine != null) {
                StopCoroutine(_monitorCoroutine);
                _monitorCoroutine = null;
            }

            if (_endOfMatchSignalCoroutine != null) {
                StopCoroutine(_endOfMatchSignalCoroutine);
                _endOfMatchSignalCoroutine = null;
            }

            if (_matchScreenshotSignalCoroutine != null) {
                StopCoroutine(_matchScreenshotSignalCoroutine);
                _matchScreenshotSignalCoroutine = null;
            }
            
            IsRankedTriggered = false;
            _isQueueing = false;
            _webStatus = "IDLE";
            _currentQueueId = "";
            _currentSession = null;
            _isHost = false;
            _hasAccepted = false;
            _hostRulesetSelection = "ranked";
            _queueStartTime = null;
            _hostLobbyStarted = false;
            _hostServerWasActive = false;
            _hostCancelSent = false;
            _matchStatsSubmitted = false;
            _proSeriesSkipLogged = false;
            _matchStartTime = null;

            _syncTickCount = 0;
            _lobbyCreatedEventCount = 0;
            _steamLinkUploadAttempts = 0;
            _steamLinkUploadSuccesses = 0;
            _steamLinkUploadFailures = 0;
            _autoJoinAttempts = 0;
            _autoJoinSuccesses = 0;
            _autoJoinFailures = 0;
            _lastLobbyCreatedAt = null;
            _lastUploadAttemptAt = null;
            _lastUploadSuccessAt = null;
            _lastAutoJoinAttemptAt = null;
            _lastAutoJoinSuccessAt = null;
            _lastGeneratedSteamLink = "-";
            _lastUploadedSteamLink = "-";
            _lastAutoJoinSteamLink = "-";
            _lastUploadError = "-";
            _lastAutoJoinError = "-";
            
            // Stop lobby rename monitor if running
            if (_lobbyMonitorCoroutine != null) {
                StopCoroutine(_lobbyMonitorCoroutine);
                _lobbyMonitorCoroutine = null;
            }

            // Reset progressive match tracking
            _currentMatchId = null;
            _playerMatchEntryIds.Clear();
            _matchEntryCreationInProgress.Clear();
            _playerIdsByName.Clear();
            _lastSubmittedScores.Clear();
            _lastSubmittedScoresVsPar.Clear();
            _matchCreationInProgress = false;
            _matchEntriesCreated = false;
            _isInGameplay = false;
            _matchEndedReceived = false;
            ClearPendingMatchScreenshot();
            _matchScreenshotUploadInProgress = false;
            _matchScreenshotUploadCompleted = false;
            _localManualSessionId = null;
            _cachedLeaderboardScores.Clear();
            _cachedLeaderboardScoresVsPar.Clear();
            _finalLeaderboardSnapshot.Clear();
            _lastUploadedPlayerCount = -1;
            _nextEnsureMatchCreateAttemptAt = 0f;
        }

        private void OnDestroy() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            CourseManager.MatchStateChanged -= OnCourseManagerMatchStateChanged;
            CourseManager.ForceDisplayScoreboardChanged -= OnCourseManagerForceDisplayScoreboardChanged;
            UnifiedPlugin.ApiConfigChanged -= OnApiConfigChanged;
        }

        private void OnApplicationQuit() {
            // Best effort cancel when host closes game while owning a live session.
            if (_isHost && _hostLobbyStarted && _hostServerWasActive && _currentSession != null) {
                StartCoroutine(CancelSessionAsHost("host_application_quit"));
            }
        }

        private void Log(string msg) {
            string timestampedMsg = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            _debugLogs.Add(timestampedMsg);
            if (_debugLogs.Count > 30) _debugLogs.RemoveAt(0);
            _logScroll.y = float.MaxValue;
            
            // Also log to BepInEx logger
            if (_bepinexLogger != null) {
                // Strip HTML tags for BepInEx log
                string cleanMsg = System.Text.RegularExpressions.Regex.Replace(msg, "<[^>]+>", "");
                _bepinexLogger.LogInfo($"[MatchmakingAssistant] {cleanMsg}");
            }
        }

        // ==========================================
        // API LOOPS & RECONCILIATION
        // ==========================================
        private bool TryUseSharedProfile(string logContext) {
            var sharedProfile = UnifiedPlugin.GetPlayerProfile();
            if (sharedProfile == null || !UnifiedPlugin.IsPlayerProfileResolved() || string.IsNullOrWhiteSpace(sharedProfile.ID)) {
                return false;
            }

            _userProfile = new PlayerProfile {
                id = sharedProfile.ID,
                display_name = sharedProfile.DisplayName,
                current_mmr = sharedProfile.CurrentMMR,
                region = sharedProfile.Region,
                state_province = string.Empty
            };

            Log($"<color=green>[{logContext}] ✓ Using shared profile: {_userProfile.display_name} ({_userProfile.id})</color>");
            return true;
        }

        private string TryGetSteamPlayerName() {
            try {
                if (FacepunchLib.SteamClient.IsValid) {
                    return FacepunchLib.SteamClient.Name;
                }
            } catch { }

            return string.Empty;
        }

        private IEnumerator EnsureUserProfileResolved(string logContext, float maxWaitSeconds = 0f) {
            if (_userProfile != null) {
                yield break;
            }

            bool attemptedSteamLookup = false;
            float waited = 0f;

            while (_userProfile == null) {
                if (TryUseSharedProfile(logContext)) {
                    yield break;
                }

                if (!attemptedSteamLookup) {
                    string steamName = TryGetSteamPlayerName();
                    if (!string.IsNullOrWhiteSpace(steamName) && steamName != "Player") {
                        attemptedSteamLookup = true;
                        Log($"<color=cyan>[{logContext}] Attempting local profile resolution for '{steamName}'</color>");
                        yield return ResolveProfile(steamName);

                        if (_userProfile != null || TryUseSharedProfile(logContext)) {
                            yield break;
                        }
                    }
                }

                if (waited >= maxWaitSeconds) {
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
                waited += 0.5f;
            }
        }

       private IEnumerator BackgroundSyncLoop() {
            int retryCount = 0;
            const int maxRetries = 10;
            
            while (true) {
                _syncTickCount++;

                // First, ensure we have the profile resolved from the shared profile or API
                if (_userProfile == null) {
                    yield return EnsureUserProfileResolved("Sync");

                    if (_userProfile != null) {
                        retryCount = 0;
                    } else {
                        retryCount++;
                        if (retryCount == 1) Log($"<color=yellow>Waiting for player profile resolution... (Attempt {retryCount}/{maxRetries})</color>");
                        if (retryCount % 3 == 0) Log($"<color=yellow>Still waiting for player profile... (Attempt {retryCount}/{maxRetries})</color>");
                        if (retryCount >= maxRetries) {
                            Log("<color=orange>Player profile resolution timed out. Background sync will stop until the next reload.</color>");
                            yield break;
                        }
                    }
                }

                // Only proceed with sync if the profile is successfully loaded
                if (_userProfile != null) {
                    // Fetch active season once after profile resolves
                    if (!_activeSeasonFetched) {
                        yield return FetchActiveSeasonId();
                    }

                    bool isInMenuScene = SceneManager.GetActiveScene().name.ToLower().Contains("menu");

                    // Check for a queued entry created via the website (on init and whenever idle)
                    if (!_isQueueing && _currentSession == null && !_isInGameplay) {
                        yield return CheckExistingQueueEntry();
                    }

                    // Keep main-menu stats fresh every sync interval, even when idle.
                    if (isInMenuScene && !IsRankedTriggered && !_isInGameplay) {
                        yield return RefreshPlayerList();
                    }
                    
                    // Unlock the "Join Queue" button only after the first successful server check
                    if (_isInitializing) {
                        _isInitializing = false;
                        Log("Initialization Complete: Queue Unlocked.");
                    }

                    if (_isQueueing && _currentSession == null) {
                        yield return CheckForMatch();
                    }

                    if (_currentSession != null && _currentSession.status == "pending_accept") {
                        yield return PollSessionStatus();
                    }

                    // Host-side disconnect guard: if we were hosting and server goes down, cancel session once.
                    if (_isHost && _hostLobbyStarted && _currentSession != null && !_hostCancelSent) {
                        if (NetworkServer.active) {
                            _hostServerWasActive = true;
                        } else if (_hostServerWasActive && _currentSession.status != "completed") {
                            Log("Host lobby appears closed. Marking MatchmakingSession as completed...");
                            yield return CancelSessionAsHost("host_left_lobby");
                        }
                    }

                    // Auto-join for non-hosts when steam_lobby_link is available and ready
                    if (_currentSession != null && !_isHost && _currentSession.status == "ready" 
                        && !string.IsNullOrEmpty(_currentSession.steam_lobby_link) && !_hasAccepted) {
                        Log("Auto-joining match as non-host...");
                        yield return AutoJoinMatch();
                    }
                }

                yield return _syncLoopDelay;
            }
        }

        private IEnumerator RefreshPlayerList() {
            string query = "{\"$or\":[{\"status\":\"queued\"},{\"status\":\"matched\"}]}";
            string fullUrl = $"{GetBaseApiUrl()}/MatchmakingQueue?q={UnityWebRequest.EscapeURL(query)}";
            Log($"<color=cyan>[Sync] GET /MatchmakingQueue (fetching queued players)</color>");
            Log($"<color=cyan>[Sync] Full URL: {fullUrl.Substring(0, Math.Min(150, fullUrl.Length))}...</color>");
            using (UnityWebRequest req = UnityWebRequest.Get(fullUrl)) {
                ApplyApiHeaders(req);
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success) {
                    string rawJson = req.downloadHandler.text;
                    List<JObject> queueEntries = ParseApiObjectList(rawJson);

                    List<PlayerData> queuedPlayers = new List<PlayerData>(queueEntries.Count);
                    JObject myEntry = null;
                    string myStatus = null;
                    int countQueued = 0;
                    int countMatched = 0;

                    foreach (JObject entry in queueEntries) {
                        string status = (string)entry["status"];

                        if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase)) {
                            countQueued++;
                            queuedPlayers.Add(new PlayerData {
                                name = (string)entry["display_name"] ?? (string)entry["user_id"] ?? "Unknown",
                                mmr = entry["mmr_snapshot"]?.ToString() ?? "0"
                            });
                        } else if (string.Equals(status, "matched", StringComparison.OrdinalIgnoreCase)) {
                            countMatched++;
                        }

                        if (_userProfile != null && myEntry == null &&
                            string.Equals((string)entry["player_id"], _userProfile.id, StringComparison.Ordinal)) {
                            myEntry = entry;
                            myStatus = status;
                        }
                    }

                    _queuedPlayers = queuedPlayers;
                    _onlineCount   = countQueued + countMatched;
                    _queuedCount   = countQueued;
                    _matchedCount  = countMatched;

                    if (_userProfile != null) {
                        if (myEntry != null) {
                            string status = myStatus;

                            if (status == "queued" && !_isQueueing) {
                                _isQueueing = true;
                                _currentQueueId = (string)myEntry["id"] ?? _currentQueueId;
                                _webStatus = "QUEUED (SYNCED)";

                                string serverTime = (string)myEntry["queued_at"];
                                // Uses RoundtripKind to correctly interpret the 'Z' (UTC) suffix from the API
                                if (DateTime.TryParse(serverTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsedTime)) {
                                    _queueStartTime = parsedTime.ToUniversalTime();
                                }
                                Log("Sync: Found active session on server. Reconnecting UI...");
                            }
                            else if (status == "matched" && _currentSession == null) {
                                _webStatus = "MATCH FOUND (SYNCED)";
                                yield return CheckForMatch();
                            }
                        }
                    }
                } else {
                    Log($"<color=red>[Sync] RefreshPlayerList failed: {req.result} - {req.error}</color>");
                }
            }
        }

        private IEnumerator CheckExistingQueueEntry() {
            string checkQuery = $"{{\"player_id\":\"{_userProfile.id}\",\"status\":\"queued\"}}";
            string checkUrl = $"{GetBaseApiUrl()}/MatchmakingQueue?q={UnityWebRequest.EscapeURL(checkQuery)}";
            using (UnityWebRequest req = UnityWebRequest.Get(checkUrl)) {
                ApplyApiHeaders(req);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) {
                    List<JObject> existing = ParseApiObjectList(req.downloadHandler.text);
                    if (existing != null && existing.Count > 0) {
                        _currentQueueId = (string)existing[0]["id"];
                        _isQueueing = true;
                        _webStatus = "QUEUED";
                        _queueStartTime = DateTime.Now;
                        Log($"<color=yellow>[Queue] Detected existing queue entry {_currentQueueId} from website — rejoining.</color>");
                        // Patch has_mod:true onto the web entry
                        string patchJson = "{" +
                            $"\"has_mod\":true," +
                            $"\"created_by\":\"SBGL_UnifiedMod\"" +
                        "}";
                        yield return CallAPI($"/MatchmakingQueue/{_currentQueueId}", "PUT", patchJson, (res) => {
                            if (ParseApiSingleObject(res) != null)
                                Log($"<color=green>[Queue] ✓ Patched has_mod:true onto existing entry {_currentQueueId}.</color>");
                        });
                    }
                }
            }
        }

        private IEnumerator MatchmakingLoop() {
            if (_userProfile == null) { Log("<color=red>No profile loaded.</color>"); yield break; }

            _isQueueing = true;
            _webStatus = "JOINING...";
            _queueStartTime = DateTime.Now;

            // Check if a queue entry already exists for this player (e.g. created via website).
            // If so, PATCH has_mod onto it rather than creating a duplicate.
            string existingId = null;
            string checkQuery = $"{{\"player_id\":\"{_userProfile.id}\",\"status\":\"queued\"}}";
            string checkUrl = $"{GetBaseApiUrl()}/MatchmakingQueue?q={UnityWebRequest.EscapeURL(checkQuery)}";
            Log($"<color=cyan>[Queue] Checking for existing entry for player {_userProfile.id}...</color>");
            using (UnityWebRequest checkReq = UnityWebRequest.Get(checkUrl)) {
                ApplyApiHeaders(checkReq);
                yield return checkReq.SendWebRequest();
                if (checkReq.result == UnityWebRequest.Result.Success) {
                    List<JObject> existing = ParseApiObjectList(checkReq.downloadHandler.text);
                    if (existing != null && existing.Count > 0) {
                        existingId = (string)existing[0]["id"];
                        Log($"<color=yellow>[Queue] Found existing entry {existingId} — patching has_mod:true</color>");
                    } else {
                        Log("<color=cyan>[Queue] No existing entry found — will create new.</color>");
                    }
                } else {
                    Log($"<color=orange>[Queue] Could not check existing entries: {checkReq.result} — will attempt POST anyway.</color>");
                }
            }

            if (existingId != null) {
                // Update the existing entry to mark has_mod:true and refresh queued_at
                string patchJson = "{" +
                    $"\"has_mod\":true," +
                    $"\"created_by\":\"SBGL_UnifiedMod\"," +
                    $"\"queued_at\":\"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\"" +
                "}";
                yield return CallAPI($"/MatchmakingQueue/{existingId}", "PUT", patchJson, (res) => {
                    JObject updated = ParseApiSingleObject(res);
                    if (updated != null) {
                        _currentQueueId = existingId;
                        _webStatus = "QUEUED";
                        Log($"<color=green>[Queue] ✓ Updated existing entry {existingId} with has_mod:true.</color>");
                    } else {
                        Log($"<color=red>[Queue] Failed to update existing entry {existingId}.</color>");
                    }
                });
            } else {
                // No existing entry — create a fresh one with has_mod:true
                string json = "{" +
                    $"\"player_id\":\"{_userProfile.id}\"," +
                    $"\"user_id\":\"{_userProfile.id}\"," +
                    $"\"display_name\":\"{_userProfile.display_name}\"," +
                    $"\"mmr_snapshot\":{_userProfile.current_mmr}," +
                    $"\"region\":\"{_userProfile.region}\"," +
                    $"\"state_province\":\"{_userProfile.state_province ?? ""}\"," +
                    $"\"has_mod\":true," +
                    $"\"created_by\":\"SBGL_UnifiedMod\"," +
                    $"\"status\":\"queued\"," +
                    $"\"queued_at\":\"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\"" +
                "}";
                yield return CallAPI("/MatchmakingQueue", "POST", json, (res) => {
                    JObject queueResponse = ParseApiSingleObject(res);
                    _currentQueueId = (string)queueResponse?["id"] ?? _currentQueueId;
                    _webStatus = "QUEUED";
                    Log($"<color=green>[Queue] ✓ Created new entry {_currentQueueId} with has_mod:true.</color>");
                });
            }
        }

        private IEnumerator LeaveQueue() {
            if (string.IsNullOrEmpty(_currentQueueId)) { ResetPluginState(); yield break; }
            _webStatus = "LEAVING...";
            string json = "{" + $"\"player_id\":\"{_userProfile.id}\",\"status\":\"cancelled\"" + "}";

            bool leaveSuccess = false;
            yield return CallAPI($"/MatchmakingQueue/{_currentQueueId}", "PUT", json, (res) => {
                if (ParseApiSingleObject(res) != null) {
                    ResetPluginState();
                    Log("✓ Left Queue.");
                    leaveSuccess = true;
                }
            });
            
            if (!leaveSuccess) {
                Log("<color=orange>Leave queue request failed. Check logs above for details.</color>");
                ResetPluginState();
            }
        }

        private IEnumerator CheckForMatch() {
            string query = "{\"$or\":[{\"host_player_id\":\"" + _userProfile.id + "\"},{\"player_ids\":{\"$in\":[\"" + _userProfile.id + "\"]}}],\"status\":\"pending_accept\"}";
            string fullUrl = $"{GetBaseApiUrl()}/MatchmakingSession?q={UnityWebRequest.EscapeURL(query)}";
            Log($"<color=cyan>[Sync] GET /MatchmakingSession (checking for pending matches)</color>");
            Log($"<color=cyan>[Sync] Full URL: {fullUrl.Substring(0, Math.Min(150, fullUrl.Length))}...</color>");
            using (UnityWebRequest req = UnityWebRequest.Get(fullUrl)) {
                ApplyApiHeaders(req);
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success) {
                    List<JObject> sessions = ParseApiObjectList(req.downloadHandler.text);
                    JObject activeSession = sessions.FirstOrDefault(session => !string.IsNullOrEmpty((string)session["lobby_name"]));

                    if (activeSession != null) {
                        // Session parsing logic is now handled elsewhere
                        _webStatus = "MATCH FOUND: PENDING";
                        Log("Match Found! Accept needed.");
                    }
                } else {
                    Log($"<color=red>[Sync] CheckForMatch failed: {req.result} - {req.error}</color>");
                }
            }
        }

        private IEnumerator PollSessionStatus() {
                // (removed misplaced closing brace)
            string fullUrl = $"{GetBaseApiUrl()}/MatchmakingSession/{_currentSession.id}";
            Log($"<color=cyan>[Sync] GET /MatchmakingSession/{_currentSession.id} (polling status)</color>");
            Log($"<color=cyan>[Sync] Full URL: {fullUrl}</color>");
            using (UnityWebRequest req = UnityWebRequest.Get(fullUrl)) {
                ApplyApiHeaders(req);
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success) {
                    string raw = req.downloadHandler.text;
                    JObject session = ParseApiSingleObject(raw);
                    if (session == null) {
                        yield break;
                    }

                    string statusFromApi = (string)session["status"];
                    _currentSession.status = statusFromApi ?? _currentSession.status;
                    Log($"<color=cyan>[Sync] Status from API: {statusFromApi}</color>");
                    
                    // Update accepted_player_ids from API response
                    var acceptedIds = session["accepted_player_ids"]?.ToObject<List<string>>();
                    if (acceptedIds != null) {
                        _currentSession.accepted_player_ids = acceptedIds;
                        Log($"<color=cyan>[Sync] Updated accepted_player_ids: {acceptedIds.Count} players have accepted</color>");
                        
                        // Check if all players have accepted and transition to ready if needed
                        if (_currentSession.status == "pending_accept" && 
                            _currentSession.accepted_player_ids.Count == _currentSession.player_ids.Count) {
                            Log($"<color=green>[Sync] All {_currentSession.player_ids.Count} players have accepted! Transitioning to ready...</color>");
                            _currentSession.status = "ready";
                            _webStatus = _isHost ? "READY: HOST" : "READY: JOIN";
                            
                            // Notify server of ready state
                            string readyJson = "{\"status\":\"ready\"}";
                            yield return CallAPI($"/MatchmakingSession/{_currentSession.id}", "PUT", readyJson, (res) => {
                                Log("<color=green>[Sync] ✓ Session transitioned to READY</color>");
                            });
                        }
                    }
                    
                    string latestSteamLobbyLink = (string)session["steam_lobby_link"];
                    if (!string.IsNullOrEmpty(latestSteamLobbyLink) && latestSteamLobbyLink != _currentSession.steam_lobby_link) {
                        _currentSession.steam_lobby_link = latestSteamLobbyLink;
                        Log("Sync: steam_lobby_link received/updated from API.");
                    }

                    string latestMatchId = (string)session["match_id"];
                    if (!string.IsNullOrEmpty(latestMatchId) && latestMatchId != _currentSession.match_id) {
                        _currentSession.match_id = latestMatchId;
                        Log($"Sync: match_id received/updated from API: {latestMatchId}");
                    }
                    
                    if (_currentSession.status == "ready") {
                        _webStatus = _isHost ? "READY: HOST" : "READY: JOIN";
                        Log($"<color=green>[Sync] Session is READY - Host should start game</color>");
                    } else if (_currentSession.status == "cancelled" || _currentSession.status == "completed") {
                        Log($"Match ended with status: {_currentSession.status}.");
                        ResetPluginState();
                    }

                } else {
                    Log($"<color=red>[Sync] PollSessionStatus failed: {req.result} - {req.error}</color>");
                }
            }
        }

        private IEnumerator AcceptMatch() {
            if (_currentSession == null) yield break;
            _hasAccepted = true;
            
            // Refresh session data to get latest accepted_player_ids from API
            Log($"<color=cyan>[Accept] Refreshing session data before accepting...</color>");
            yield return PollSessionStatus();
            
            // Build the accepted_player_ids array by merging with existing acceptances
            var acceptedIds = new List<string>();
            
            // Start with existing accepted player IDs from the session
            if (_currentSession.accepted_player_ids != null) {
                acceptedIds.AddRange(_currentSession.accepted_player_ids);
            }
            
            // Add current player's ID if not already in the list
            if (!acceptedIds.Contains(_userProfile.id)) {
                acceptedIds.Add(_userProfile.id);
            }
            
            string acceptedIdsList = string.Join("\",\"", acceptedIds);
            string acceptJson = "{\"accepted_player_ids\":[\"" + acceptedIdsList + "\"]}";
            
            Log($"<color=cyan>[Accept] Current player accepting: {_userProfile.id}</color>");
            Log($"<color=cyan>[Accept] Total accepted players: {acceptedIds.Count}</color>");
            Log($"<color=cyan>[Accept] JSON payload: {acceptJson}</color>");
            
            // Update local session object immediately to maintain state
            _currentSession.accepted_player_ids = new List<string>(acceptedIds);
            
            yield return CallAPI($"/MatchmakingSession/{_currentSession.id}", "PUT", acceptJson, (res) => {
                Log("<color=green>✓ Match Accepted. Waiting for all players to accept before transitioning to ready...</color>");
                // Do NOT call TransitionToReady() here - the server will handle transitioning to 'ready'
                // when all players have accepted. The mod will poll and detect the status change.
            });
        }

        private IEnumerator AutoJoinMatch() {
            if (_currentSession == null || string.IsNullOrEmpty(_currentSession.steam_lobby_link)) yield break;
            
            _autoJoinAttempts++;
            _lastAutoJoinAttemptAt = DateTime.Now;
            _lastAutoJoinSteamLink = _currentSession.steam_lobby_link;
            _hasAccepted = true;
            Log($"Attempting automatic join with link: {_currentSession.steam_lobby_link}");
            
            // Automatically join the lobby using the steam_lobby_link and password
            JoinBySteamLink(_currentSession.steam_lobby_link, _currentSession.lobby_password);
        }

        private IEnumerator TransitionToReady() {
            // NOTE: This coroutine is now DISABLED and should not be called by the mod.
            // The server is responsible for transitioning the session status from 'pending_accept' to 'ready'
            // when all players have accepted. The mod should only:
            // 1. Accept the match via PUT with accepted_player_ids
            // 2. Poll the session status to detect when the server has set it to 'ready'
            
            // We give the server a split second to process the previous PUT
            yield return _readyTransitionDelay;

            // DO NOT USE THIS - Let the server handle the ready transition
            /*
            string readyJson = "{\"status\":\"ready\"}";
            
            yield return CallAPI($"/MatchmakingSession/{_currentSession.id}", "PUT", readyJson, (res) => {
                Log("<color=green>Session marked as READY.</color>");
                _currentSession.status = "ready"; // Local update for immediate UI response
            });
            */
        }

        /// <summary>
        /// Store match configuration (match_type, selected_course, season) in PlayerPrefs.
        /// These values are read by Harmony patches to apply Season 1 rules during lobby creation.
        /// </summary>
        private void StoreMatchConfigurationInPlayerPrefs(MatchmakingSession session)
        {
            try
            {
                if (session == null)
                {
                    Log("<color=yellow>[Config] Session is null, cannot store configuration</color>");
                    return;
                }

                string rawMatchType = session.match_type ?? string.Empty;
                bool isProSeries = rawMatchType.IndexOf("pro_series", StringComparison.OrdinalIgnoreCase) >= 0;
                string matchTypeToStore = isProSeries ? Season1RuleSet.MATCH_TYPE_PRO_SERIES : Season1RuleSet.MATCH_TYPE_RANKED;
                int seasonToStore = session.season > 0 ? session.season : Season1RuleSet.SEASON;

                // Validate course is approved before storing
                try
                {
                    RuleSetManager.SetLogger(_bepinexLogger);
                    bool isCourseValid = !string.IsNullOrEmpty(session.selected_course)
                        && RuleSetManager.ValidateCourseForRanked(session.selected_course);
                    
                    string courseToStore = isCourseValid ? session.selected_course : MapPoolConfig.GetRandomApprovedCourse().Name;

                    // Store configuration in PlayerPrefs for patches to access
                    PlayerPrefs.SetString("MatchType", matchTypeToStore);
                    PlayerPrefs.SetString("SelectedCourse", courseToStore);
                    PlayerPrefs.SetInt("Season", seasonToStore);
                    PlayerPrefs.SetString("HostRuleset", isProSeries ? "pro_series" : "ranked");
                    PlayerPrefs.Save();

                    Log($"<color=cyan>[Config] Stored match configuration: Type={matchTypeToStore}, Course={courseToStore}, Season={seasonToStore}</color>");

                    // Log the configuration for audit trail
                    RuleSetManager.LogMatchConfiguration(matchTypeToStore, courseToStore, seasonToStore);
                }
                catch (System.Exception ruleEx)
                {
                    Log($"<color=yellow>[Config] Error during rule validation, storing fallback config: {ruleEx.Message}</color>");
                    
                    // Store fallback config anyway
                    PlayerPrefs.SetString("MatchType", matchTypeToStore);
                    PlayerPrefs.SetString("SelectedCourse", string.IsNullOrEmpty(session.selected_course) ? MapPoolConfig.GetRandomApprovedCourse().Name : session.selected_course);
                    PlayerPrefs.SetInt("Season", seasonToStore);
                    PlayerPrefs.SetString("HostRuleset", isProSeries ? "pro_series" : "ranked");
                    PlayerPrefs.Save();
                }
            }
            catch (System.Exception ex)
            {
                Log($"<color=red>[Config] Exception storing match configuration: {ex.Message} | StackTrace: {ex.StackTrace}</color>");
            }
        }

        /// <summary>
        /// Clear match configuration from PlayerPrefs after match has started.
        /// Prevents configuration from leaking to subsequent matches.
        /// </summary>
        private void ClearMatchConfigurationFromPlayerPrefs()
        {
            PlayerPrefs.DeleteKey("MatchType");
            PlayerPrefs.DeleteKey("SelectedCourse");
            PlayerPrefs.DeleteKey("Season");
            PlayerPrefs.Save();
            Log("[Config] Cleared match configuration from PlayerPrefs");
        }

        private void InitiateHostSequence() {
            if (_currentSession == null) return;
            
            // Ensure the host has explicitly accepted before initializing the lobby
            if (!_hasAccepted) {
                Log("<color=yellow>[Host] Must accept match first before initializing lobby</color>");
                StartCoroutine(AcceptMatch());
                return;
            }

            // Ensure ruleset has been selected (read from PlayerPrefs set by driving range panel)
            string rulesetFromPrefs = PlayerPrefs.GetString("HostRuleset", "");
            _hostRulesetSelection = string.Equals(rulesetFromPrefs, "pro_series", StringComparison.OrdinalIgnoreCase)
                ? "pro_series"
                : "ranked";
            
            var mainMenu = UnityEngine.Object.FindAnyObjectByType<MainMenu>();
            if (mainMenu != null) {
                IsRankedTriggered = true;
                _hostLobbyStarted = true;
                _hostServerWasActive = false;
                _hostCancelSent = false;
                _matchStartTime = DateTime.UtcNow;
                _matchStatsSubmitted = false;
                PlayerPrefs.SetString("LobbyName", _currentSession.lobby_name);
                PlayerPrefs.SetString("LobbyPassword", _currentSession.lobby_password);
                PlayerPrefs.SetString("HostRuleset", _hostRulesetSelection);
                PlayerPrefs.Save();
                Log($"Host ruleset stored: {_hostRulesetSelection}");
                mainMenu.StartHost();
                Log("Host lobby initiated. Waiting for Steamworks lobby creation callback...");
                StartCoroutine(UpdateSessionStatus("in_progress"));
            }
        }

        private IEnumerator CancelSessionAsHost(string reason) {
            if (_hostCancelSent) yield break;
            if (!_isHost || _currentSession == null || string.IsNullOrEmpty(_currentSession.id)) yield break;
            if (_currentSession.id == "DEBUG") yield break;

            _hostCancelSent = true;
            string sessionId = _currentSession.id;
            string json = "{\"status\":\"completed\"}";

            using (UnityWebRequest req = new UnityWebRequest(GetBaseApiUrl() + $"/MatchmakingSession/{sessionId}", "PUT")) {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                ApplyApiHeaders(req);

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success) {
                    if (_currentSession != null && _currentSession.id == sessionId) {
                        _currentSession.status = "completed";
                    }
                    Log($"Host leave update sent ({reason}): session marked completed.");
                } else {
                    _hostCancelSent = false;
                    Log($"<color=red>Host cancel failed ({reason}): {req.result} {req.error}</color>");
                }
            }
        }

        private IEnumerator UploadSteamLobbyLink(string steamLink) {
            if (_currentSession == null || string.IsNullOrEmpty(steamLink)) yield break;
            if (string.Equals(_currentSession.steam_lobby_link, steamLink, StringComparison.Ordinal)) {
                Log("Steam Lobby Link already synced. Skipping redundant upload.");
                yield break;
            }
            
            _steamLinkUploadAttempts++;
            _lastUploadAttemptAt = DateTime.Now;
            _lastGeneratedSteamLink = steamLink;
            Log($"Uploading Steam Lobby Link to API...");
            string json = "{" + $"\"steam_lobby_link\":\"{steamLink}\"" + "}";

            using (UnityWebRequest req = new UnityWebRequest(GetBaseApiUrl() + $"/MatchmakingSession/{_currentSession.id}", "PUT")) {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                ApplyApiHeaders(req);

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success) {
                    _steamLinkUploadSuccesses++;
                    _lastUploadSuccessAt = DateTime.Now;
                    _lastUploadedSteamLink = steamLink;
                    _currentSession.steam_lobby_link = steamLink;
                    _lastUploadError = "-";
                    Log("<color=green>Steam Lobby Link uploaded successfully.</color>");
                } else {
                    _steamLinkUploadFailures++;
                    _lastUploadError = $"{req.result}: {req.error}";
                    Log($"<color=red>Steam Lobby Link upload failed: {_lastUploadError}</color>");
                }
            }
        }

        private IEnumerator CreateMatchAndEntries() {
            if (_matchCreationInProgress) {
                Log("<color=orange>[Match Creation] Already in progress - skipping duplicate call</color>");
                yield break;
            }
            _matchCreationInProgress = true;
            yield return CreateMatchAndEntriesInternal();
            _matchCreationInProgress = false;
        }

        private IEnumerator CreateMatchAndEntriesInternal() {
            if (_userProfile == null) {
                Log("<color=red>[Match Creation] Failed: Missing player profile</color>");
                yield break;
            }

            bool isManualLocalLobby = _currentSession == null;
            if (isManualLocalLobby) {
                if (string.IsNullOrWhiteSpace(_localManualSessionId)) {
                    _localManualSessionId = BuildStableManualLocalSessionId();
                }

                Log($"<color=cyan>[Match Creation] Manual local lobby detected. Session surrogate: {_localManualSessionId}</color>");
            }

            // Pro Series match submission is handled manually — skip automated entry creation
            string currentMatchType = PlayerPrefs.GetString("MatchType", "");
            if (currentMatchType.IndexOf("pro_series", StringComparison.OrdinalIgnoreCase) >= 0) {
                if (!_proSeriesSkipLogged) {
                    Log("<color=yellow>[Match] Pro Series match — automated upload skipped</color>");
                    _proSeriesSkipLogged = true;
                }
                _matchEntriesCreated = true;
                yield break;
            }

            string activeSessionId = _currentSession != null ? _currentSession.id : _localManualSessionId;
            Log($"<color=cyan>[Match Creation] Starting new match for session {activeSessionId}</color>");
            _matchStartTime = DateTime.UtcNow;

            // Pre-fetch leaderboard data
            Dictionary<string, int> playerScores = new Dictionary<string, int>();
            Dictionary<string, int> playerScoresVsPar = new Dictionary<string, int>();
            List<SBGLLiveLeaderboard.LiveLeaderboardPlugin.SBGLPlayer> startingLeaderboard = null;
            
            try {
                var liveLeaderboard = UnityEngine.Object.FindAnyObjectByType<SBGLLiveLeaderboard.LiveLeaderboardPlugin>(FindObjectsInactive.Include);
                if (liveLeaderboard != null) {
                    var allLeaderboardPlayers = liveLeaderboard.GetCurrentLeaderboard();
                    startingLeaderboard = new List<SBGLLiveLeaderboard.LiveLeaderboardPlugin.SBGLPlayer>(allLeaderboardPlayers); // Store for later use
                    Log($"<color=cyan>[Match Creation] Found {allLeaderboardPlayers.Count} players on leaderboard</color>");
                    
                    foreach (var player in allLeaderboardPlayers) {
                        if (player == null) continue;
                        playerScores[player.Name] = player.BaseScore;
                        
                        if (!string.IsNullOrEmpty(player.RawStrokes)) {
                            string strokeStr = player.RawStrokes.Replace("±", "").Trim();
                            int.TryParse(strokeStr, out int vsPar);
                            playerScoresVsPar[player.Name] = vsPar;
                        }
                    }
                }
            } catch (System.Exception ex) {
                Log($"<color=yellow>[Match Creation] Error prefetching leaderboard: {ex.Message}</color>");
            }

            _cachedLeaderboardScores = playerScores;
            _cachedLeaderboardScoresVsPar = playerScoresVsPar;
            _matchExpectedPlayerCount = startingLeaderboard?.Count ?? 0;

            // Build a position map for the starting leaderboard so we assign unique, deterministic positions.
            var startingPositionMap = startingLeaderboard != null
                ? BuildFinishPositionMap(startingLeaderboard)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // -----------------------------------------------------------------------
            // MATCH ID COORDINATION
            // Host (game server or ranked host) creates the Match record first.
            // All other mod users wait up to 12 s for the host to upload, then enter
            // a slot-based fallback so at most one non-host posts if the host never did.
            // -----------------------------------------------------------------------
            bool isEffectiveHost = _isHost || NetworkServer.active;

            if (isEffectiveHost) {
                // HOST PATH — one dedup check then post immediately.
                Log("<color=cyan>[Match Creation] We are the host — creating Match record with priority</color>");

                if (_currentSession != null) {
                    string existingMatchId = null;
                    yield return ResolveExistingMatchIdForCurrentSession((id) => existingMatchId = id);
                    if (!string.IsNullOrEmpty(existingMatchId)) {
                        _currentMatchId = existingMatchId;
                        ShowUploadNotification("Match record already exists; reusing existing upload.", "info");
                        goto createEntries;
                    }
                }
                // No existing match — fall through to the POST block below.
            }
            else {
                // NON-HOST PATH — wait for the host to upload and adopt their Match ID.
                MatchResultSubmissionService.ReceivedP2PMatchId = null; // clear any stale value
                Log("<color=cyan>[Match Creation] We are a client — waiting up to 12s for host to upload the match...</color>");

                float waitElapsed = 0f;
                float nextApiCheckAt = 2f; // first API poll after 2 s
                while (waitElapsed < 12f) {
                    // P2P check is cheap (just reads a static field)
                    string p2pId = MatchResultSubmissionService.ReceivedP2PMatchId;
                    if (!string.IsNullOrEmpty(p2pId)) {
                        Log($"<color=green>[Match Creation] ✓ Received P2P Match ID from host: {p2pId}</color>");
                        _currentMatchId = p2pId;
                        if (_currentSession != null) _currentSession.match_id = p2pId;
                        ShowUploadNotification("Match record adopted from host via P2P.", "info");
                        goto createEntries;
                    }

                    // API poll every 2 s (if we have a session to query by)
                    if (_currentSession != null && waitElapsed >= nextApiCheckAt) {
                        nextApiCheckAt += 2f;
                        string apiMatchId = null;
                        yield return ResolveExistingMatchIdForCurrentSession((id) => apiMatchId = id, false);
                        if (!string.IsNullOrEmpty(apiMatchId)) {
                            Log($"<color=green>[Match Creation] ✓ Found host match via API at {waitElapsed:0.#}s: {apiMatchId}</color>");
                            _currentMatchId = apiMatchId;
                            ShowUploadNotification("Match record adopted from host.", "info");
                            goto createEntries;
                        }
                    }

                    yield return new WaitForSeconds(0.5f);
                    waitElapsed += 0.5f;
                }

                // Host did not upload within 12 s.
                // Enter non-host fallback: sorted player-id slot order so exactly one
                // non-host client posts, the rest keep polling and adopt it.
                Log("<color=yellow>[Match Creation] Host did not upload within 12s — non-host fallback slot ordering...</color>");

                if (_currentSession != null) {
                    float fallbackDelay = GetCurrentPlayerMatchUploadDelaySeconds();
                    string fallbackFoundId = null;
                    if (fallbackDelay > 0f) {
                        yield return WaitForExistingMatchBeforeFallback(fallbackDelay, (id) => fallbackFoundId = id);
                    } else {
                        // Slot-0 non-host does a single quick check before posting.
                        yield return ResolveExistingMatchIdForCurrentSession((id) => fallbackFoundId = id, false);
                    }
                    if (!string.IsNullOrEmpty(fallbackFoundId)) {
                        Log($"<color=green>[Match Creation] ✓ Match adopted in non-host fallback: {fallbackFoundId}</color>");
                        _currentMatchId = fallbackFoundId;
                        ShowUploadNotification("Match record adopted from another player.", "info");
                        goto createEntries;
                    }
                }

                Log("<color=cyan>[Match Creation] No match found in non-host fallback — posting as fallback creator</color>");
            }

            Log("<color=cyan>[Match Creation] No existing Match found — proceeding with POST</color>");

            // Final dedup check right before POST — catches same-time submissions when both clients
            // exit the P2P/stagger window simultaneously (e.g. empty player_ids, P2P not yet established).
            {
                string prePostSessionId = _currentSession != null ? _currentSession.id : _localManualSessionId;
                if (!string.IsNullOrWhiteSpace(prePostSessionId)) {
                    string finalCheckId = null;
                    string finalCheckQuery = UnityWebRequest.EscapeURL("{\"matchmaking_session_id\":\"" + prePostSessionId + "\"}");
                    yield return CallAPI($"/Match?q={finalCheckQuery}&limit=1", "GET", "", (res) => {
                        finalCheckId = (string)ParseApiObjectList(res)?.FirstOrDefault()?["id"];
                    });
                    if (!string.IsNullOrWhiteSpace(finalCheckId)) {
                        Log($"<color=green>[Match Creation] ✓ Final pre-POST check found existing match: {finalCheckId} — skipping duplicate POST</color>");
                        _currentMatchId = finalCheckId;
                        if (_currentSession != null) _currentSession.match_id = finalCheckId;
                        ShowUploadNotification("Match record already exists; reusing existing upload.", "info");
                        goto createEntries;
                    }
                }
            }

            // Step 1b: Create Match record (we are the first / only mod user)
            {
                string newMatchId = null;
                yield return SubmitMatchEntry(CollectMatchStats(0f), (id) => newMatchId = id);

                if (string.IsNullOrEmpty(newMatchId)) {
                    Log("<color=red>[Match Creation] Failed to create Match record</color>");
                    ShowUploadNotification("Upload failed: could not create match record.", "failure");
                    yield break;
                }

                _currentMatchId = newMatchId;
                Log($"<color=green>[Match Creation] ✓ Match created: {newMatchId}</color>");
                ShowUploadNotification("Upload success: match record created.", "success");

                // Broadcast Match ID to other players with the mod so they skip creating duplicates
                var peers = SBGL.UnifiedMod.Features.CompetitivePluginCheck.CompetitivePluginCheck.GetKnownPeers();
                MatchResultSubmissionService.BroadcastMatchId(newMatchId, peers);
            }

            createEntries:

            // Link Match ID back to the MatchmakingSession so the website can detect mod-submitted matches
            if (_currentSession != null) {
                string linkMatchId = _currentMatchId;
                yield return CallAPI($"/MatchmakingSession/{_currentSession.id}", "PUT", $"{{\"match_id\":\"{linkMatchId}\"}}", (res) => {
                    JObject response = ParseApiSingleObject(res);
                    if (response != null) {
                        _currentSession.match_id = linkMatchId;
                        Log($"<color=green>[Match Creation] ✓ MatchmakingSession {_currentSession.id} linked to match: {linkMatchId}</color>");
                    }
                    else
                        Log($"<color=yellow>[Match Creation] Could not confirm MatchmakingSession update</color>");
                });
            } else {
                Log("<color=cyan>[Match Creation] Local lobby mode: skipping MatchmakingSession link</color>");
            }

            // Step 2: Create initial MatchEntry records for all players
            _playerMatchEntryIds.Clear();
            _playerIdsByName.Clear();
            _lastSubmittedScores.Clear();
            _lastSubmittedScoresVsPar.Clear();

            // If the leaderboard is empty we can't distinguish players from spectators yet.
            // Leave _matchEntriesCreated false so the gameplay monitor retries once the
            // scoreboard has populated, instead of waiting until finalization.
            if (startingLeaderboard == null || startingLeaderboard.Count == 0) {
                Log("<color=yellow>[Match Creation] Leaderboard empty at round start — deferring MatchEntry creation until the gameplay monitor sees players</color>");
                if (_monitorCoroutine == null) {
                    _monitorCoroutine = StartCoroutine(MonitorAndUpdateScores());
                }
                yield break;
            }

            List<string> playerIds = (_currentSession != null && _currentSession.player_ids != null && _currentSession.player_ids.Count > 0)
                ? _currentSession.player_ids
                : new List<string> { _userProfile.id };

            yield return EnrichPlayerIdsFromLeaderboard(startingLeaderboard, playerIds);
            _matchExpectedPlayerCount = playerIds.Count;

            foreach (string playerId in playerIds) {
                string playerName = null;
                string preMatchMmr = null;
                int gamePoints = 0;
                int scoreVsPar = 0;

                // Get player name, MMR and initial scores
                if (playerId == _userProfile.id) {
                    playerName = _userProfile.display_name;
                    preMatchMmr = _userProfile.current_mmr.ToString();
                    Log($"<color=cyan>[Match Creation] Current player: {playerName} (MMR: {preMatchMmr})</color>");
                } else {
                    yield return CallAPI($"/Player/{playerId}", "GET", "", (res) => {
                        try {
                            JObject profile = ParseApiSingleObject(res);
                            if (profile != null) {
                                playerName = (string)profile["display_name"];
                                if (string.IsNullOrEmpty(playerName)) {
                                    Log($"<color=yellow>[Match Creation] Player {playerId} has no display_name, using ID</color>");
                                    playerName = playerId;
                                }
                                object mmrObj = profile["current_mmr"];
                                if (mmrObj != null) {
                                    preMatchMmr = mmrObj.ToString();
                                }
                                Log($"<color=cyan>[Match Creation] Fetched player: {playerName} (MMR: {preMatchMmr})</color>");
                            } else {
                                Log($"<color=yellow>[Match Creation] Failed to fetch Player {playerId} - response null</color>");
                                playerName = playerId; // Fallback to ID
                            }
                        } catch (System.Exception ex) {
                            Log($"<color=yellow>[Match Creation] Error fetching Player {playerId}: {ex.Message}</color>");
                            playerName = playerId; // Fallback to ID
                        }
                    });
                }

                if (!string.IsNullOrEmpty(playerName)) {
                    _playerIdsByName[playerName] = playerId;

                    if (_cachedLeaderboardScores.TryGetValue(playerName, out int score)) {
                        gamePoints = score;
                        _cachedLeaderboardScoresVsPar.TryGetValue(playerName, out scoreVsPar);
                    }
                    
                    _lastSubmittedScores[playerName] = gamePoints;
                    _lastSubmittedScoresVsPar[playerName] = scoreVsPar;
                }

                // Get starting position from leaderboard
                int startingPosition = 0;
                if (!string.IsNullOrWhiteSpace(playerName)) startingPositionMap.TryGetValue(playerName, out startingPosition);

                string entryId = null;
                // For now, pass postMatchMmr as preMatchMmr (no delta) -- you should update this to the actual post-match value when available
                yield return EnsureMatchEntryForPlayer(
                    "Match Creation",
                    playerId,
                    playerName,
                    preMatchMmr,
                    preMatchMmr, // TODO: Replace with actual post-match MMR when available
                    gamePoints,
                    scoreVsPar,
                    startingPosition,
                    "Progressive match tracking - created at round start",
                    (id) => entryId = id);

                if (string.IsNullOrWhiteSpace(entryId)) {
                    Log($"<color=yellow>[Match Creation] MatchEntry not ready yet for {playerName ?? playerId}; gameplay monitor will retry if needed</color>");
                }
            }

            _matchEntriesCreated = true;
            Log($"<color=green>[Match Creation] ✓ Match and entries initialized. Starting score monitoring...</color>");

            // Start monitoring for score changes during gameplay
            if (_monitorCoroutine == null) {
                _monitorCoroutine = StartCoroutine(MonitorAndUpdateScores());
            }
        }

        private IEnumerator MonitorLobbyNameForUpload() {
            Log("<color=cyan>[LobbyMonitor] Starting mid-round lobby rename monitor</color>");
            var checkDelay = new WaitForSeconds(3f);
            while (_isInGameplay && !_matchEntriesCreated) {
                yield return checkDelay;
                if (!_isInGameplay || _matchEntriesCreated) break;

                string liveLobbyName = SBGL.UnifiedMod.Features.CompetitivePluginCheck.CompetitivePluginCheck._currentLobbyName;
                if (!string.IsNullOrEmpty(liveLobbyName) && liveLobbyName.StartsWith("SBGL-", StringComparison.OrdinalIgnoreCase)) {
                    Log($"<color=yellow>[LobbyMonitor] Lobby renamed to '{liveLobbyName}' mid-round - triggering upload...</color>");
                    StartCoroutine(ValidateMatchUpload((shouldUpload) => {
                        if (shouldUpload) {
                            Log("<color=yellow>[LobbyMonitor] Eligibility confirmed - creating match records...</color>");
                            StartCoroutine(CreateMatchAndEntries());
                        } else {
                            Log("<color=orange>[LobbyMonitor] Lobby is SBGL-* but failed eligibility check - skipping upload</color>");
                            _matchEntriesCreated = true; // Prevent repeated retries
                        }
                    }));
                    break; // Stop polling once triggered
                }
            }
            Log("<color=cyan>[LobbyMonitor] Lobby rename monitor stopped</color>");
        }

        private IEnumerator MonitorAndUpdateScores() {
            Log($"<color=cyan>[Match Monitor] Starting score monitoring for gameplay</color>");
            
            while (_isInGameplay) {
                yield return new WaitForSeconds(2f); // Check every 2 seconds

                // If scene-load creation was missed, retry creating/adopting match mid-round for any mod user.
                if (!_matchEntriesCreated
                    && string.IsNullOrWhiteSpace(_currentMatchId)
                    && !_matchCreationInProgress
                    && Time.realtimeSinceStartup >= _nextEnsureMatchCreateAttemptAt) {
                    _nextEnsureMatchCreateAttemptAt = Time.realtimeSinceStartup + 6f;
                    bool shouldUpload = false;
                    yield return ValidateMatchUpload((ok) => shouldUpload = ok);
                    if (shouldUpload) {
                        Log("<color=yellow>[Match Monitor] Missing match records mid-round - attempting creation/adoption...</color>");
                        yield return CreateMatchAndEntries();
                    }
                }

                if (_currentMatchId == null) continue;

                // Refresh leaderboard data
                var liveLeaderboard = UnityEngine.Object.FindAnyObjectByType<SBGLLiveLeaderboard.LiveLeaderboardPlugin>(FindObjectsInactive.Include);
                if (liveLeaderboard == null) continue;

                var allLeaderboardPlayers = liveLeaderboard.GetCurrentLeaderboard();
                if (allLeaderboardPlayers == null || allLeaderboardPlayers.Count == 0) continue;

                CacheLeaderboardSnapshot(allLeaderboardPlayers, "live gameplay");

                // Build a one-shot mapping of player name -> unique finish position for this live snapshot
                var livePositionMap = BuildFinishPositionMap(allLeaderboardPlayers);

                int activePlayerCount = allLeaderboardPlayers.Count(p => p != null && !string.IsNullOrWhiteSpace(p.Name));
                if (activePlayerCount > 0) {
                    _matchExpectedPlayerCount = activePlayerCount;
                    yield return UpdateMatchPlayerCountIfNeeded(activePlayerCount, "live leaderboard");
                }
                
                // Build test override map for this leaderboard
                List<string> leaderboardNames = allLeaderboardPlayers?.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name).ToList() ?? new List<string>();
                var testOverrides = GetTestPlayerOverrides(leaderboardNames);

                foreach (var player in allLeaderboardPlayers) {
                    if (player == null) continue;

                    int newGamePoints = player.BaseScore;
                    int newScoreVsPar = 0;
                    
                    if (!string.IsNullOrEmpty(player.RawStrokes)) {
                        string strokeStr = player.RawStrokes.Replace("±", "").Trim();
                        int.TryParse(strokeStr, out newScoreVsPar);
                    }

                    // Check if scores changed
                    bool scoresChanged = false;
                    if (_lastSubmittedScores.TryGetValue(player.Name, out int lastScore)) {
                        if (lastScore != newGamePoints || (!_lastSubmittedScoresVsPar.TryGetValue(player.Name, out int lastVsPar) || lastVsPar != newScoreVsPar)) {
                            scoresChanged = true;
                        }
                    } else {
                        scoresChanged = true;
                    }

                    if (scoresChanged) {
                        string playerId = null;
                        string entryId = null;

                        if (!TryGetPlayerIdForName(player.Name, out playerId)) {
                            string monitorLookupName = ApplyTestPlayerOverride(player.Name, testOverrides);
                            yield return ResolvePlayerIdByNameFromApi(monitorLookupName, (id) => playerId = id);

                            if (!string.IsNullOrEmpty(playerId)) {
                                _playerIdsByName[player.Name.Trim()] = playerId;
                            }
                        }

                        if (!string.IsNullOrEmpty(playerId)) {
                            _playerMatchEntryIds.TryGetValue(playerId, out entryId);
                        }

                        // If we have no entry yet for this leaderboard player, create it mid-round.
                        if (!string.IsNullOrEmpty(playerId) && string.IsNullOrEmpty(entryId)) {
                            string preMatchMmr = null;
                            yield return CallAPI($"/Player/{playerId}", "GET", "", (res) => {
                                JObject profile = ParseApiSingleObject(res);
                                object mmrObj = profile?["current_mmr"];
                                if (mmrObj != null) preMatchMmr = mmrObj.ToString();
                            });

                            int finishPosition = 0;
                            if (!string.IsNullOrWhiteSpace(player.Name)) livePositionMap.TryGetValue(player.Name, out finishPosition);
                            yield return EnsureMatchEntryForPlayer(
                                "Match Monitor",
                                playerId,
                                player.Name,
                                preMatchMmr,
                                null, // postMatchMmr
                                newGamePoints,
                                newScoreVsPar,
                                finishPosition,
                                "Live leaderboard backfill during round",
                                (id) => entryId = id);
                        }

                        if (!string.IsNullOrEmpty(entryId) && !string.IsNullOrEmpty(playerId)) {
                            _matchEntriesCreated = true;
                            int finishPosition = 0;
                            if (!string.IsNullOrWhiteSpace(player.Name)) livePositionMap.TryGetValue(player.Name, out finishPosition);
                            yield return UpdateMatchEntry(entryId, playerId, player.Name, newGamePoints, newScoreVsPar, finishPosition);
                            _lastSubmittedScores[player.Name] = newGamePoints;
                            _lastSubmittedScoresVsPar[player.Name] = newScoreVsPar;
                        }
                    }
                }
            }

            Log($"<color=cyan>[Match Monitor] Score monitoring ended</color>");
            _monitorCoroutine = null;
        }

        private IEnumerator UpdateMatchEntry(string entryId, string playerId, string playerName, int gamePoints, int scoreVsPar, int finishPosition = 0) {
            Log($"<color=cyan>[Score Update] Hole completed for {playerName}: {gamePoints} pts, {scoreVsPar} vs par</color>");

            int adjustedScore = gamePoints + (scoreVsPar * -10); // Same calculation as LiveLeaderboard
            string json = "{" +
                $"\"game_points\":{gamePoints}," +
                $"\"over_under\":{scoreVsPar}," +
                $"\"score_vs_par\":{scoreVsPar}," +
                $"\"adjusted_match_score\":{adjustedScore}," +
                $"\"finish_position\":{finishPosition}," +
                $"\"notes\":\"Updated after hole completion\"" +
            "}";

            yield return CallAPI($"/MatchEntry/{entryId}", "PUT", json, (res) => {
                try {
                    JObject response = ParseApiSingleObject(res);
                    if (response != null) {
                        Log($"<color=green>[Score Update] ✓ MatchEntry updated for {playerName}</color>");
                    }
                } catch (System.Exception ex) {
                    Log($"<color=yellow>[Score Update] Could not update MatchEntry: {ex.Message}</color>");
                }
            });
        }

        /// <summary>
        /// Gets the finish position of a player by name from the final leaderboard snapshot.
        /// Returns position (1-based) or 0 if not found.
        /// </summary>
        private int GetPlayerFinishPosition(string playerName, List<SBGLLiveLeaderboard.LiveLeaderboardPlugin.SBGLPlayer> finalLeaderboard) {
            if (string.IsNullOrEmpty(playerName) || finalLeaderboard == null || finalLeaderboard.Count == 0) {
                return 0;
            }

            var map = BuildFinishPositionMap(finalLeaderboard);
            if (map.TryGetValue(playerName, out int pos)) return pos;
            return 0;
        }

        private int GetPlayerFinishPosition(string playerName, List<CachedLeaderboardPlayer> finalLeaderboard) {
            if (string.IsNullOrEmpty(playerName) || finalLeaderboard == null || finalLeaderboard.Count == 0) {
                return 0;
            }

            for (int i = 0; i < finalLeaderboard.Count; i++) {
                if (finalLeaderboard[i] != null && string.Equals(finalLeaderboard[i].Name, playerName, StringComparison.OrdinalIgnoreCase)) {
                    return i + 1;
                }
            }

            return 0;
        }

        private bool TryGetPlayerIdForName(string playerName, out string playerId) {
            playerId = null;
            if (string.IsNullOrWhiteSpace(playerName)) return false;

            string normalizedName = playerName.Trim();

            if (_playerIdsByName.TryGetValue(normalizedName, out string mappedId) && !string.IsNullOrWhiteSpace(mappedId)) {
                playerId = mappedId;
                return true;
            }

            if (_userProfile != null && string.Equals(normalizedName, _userProfile.display_name, StringComparison.OrdinalIgnoreCase)) {
                playerId = _userProfile.id;
                return !string.IsNullOrWhiteSpace(playerId);
            }

            return false;
        }

        private IEnumerator ResolvePlayerIdByNameFromApi(string playerName, Action<string> onResolved) {
            if (onResolved == null) yield break;

            if (string.IsNullOrWhiteSpace(playerName)) {
                onResolved(null);
                yield break;
            }

            string normalizedName = playerName.Trim();
            string resolvedId = null;

            string escapedExactName = normalizedName.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string exactQuery = "{\"$or\":[{\"display_name\":\"" + escapedExactName + "\"},{\"ign\":\"" + escapedExactName + "\"}]}";
            yield return CallAPI($"/Player?q={UnityWebRequest.EscapeURL(exactQuery)}&limit=1", "GET", "", (res) => {
                JObject first = ParseApiObjectList(res)?.FirstOrDefault();
                if (first != null) {
                    resolvedId = (string)first["id"];
                }
            });

            if (string.IsNullOrWhiteSpace(resolvedId)) {
                string escapedRegexName = Regex.Escape(normalizedName).Replace("\\ ", " ");
                string anchoredRegexQuery = "{\"$or\":[{\"display_name\":{\"$regex\":\"^" + escapedRegexName + "$\",\"$options\":\"i\"}},{\"ign\":{\"$regex\":\"^" + escapedRegexName + "$\",\"$options\":\"i\"}}]}";
                yield return CallAPI($"/Player?q={UnityWebRequest.EscapeURL(anchoredRegexQuery)}&limit=1", "GET", "", (res) => {
                    JObject first = ParseApiObjectList(res)?.FirstOrDefault();
                    if (first != null) {
                        resolvedId = (string)first["id"];
                    }
                });
            }

            if (string.IsNullOrWhiteSpace(resolvedId)) {
                string fuzzyName = normalizedName.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string fuzzyQuery = "{\"$or\":[{\"display_name\":{\"$regex\":\"" + fuzzyName + "\",\"$options\":\"i\"}},{\"ign\":{\"$regex\":\"" + fuzzyName + "\",\"$options\":\"i\"}}]}";
                yield return CallAPI($"/Player?q={UnityWebRequest.EscapeURL(fuzzyQuery)}", "GET", "", (res) => {
                    var rows = ParseApiObjectList(res);
                    if (rows == null || rows.Count == 0) return;

                    JObject exact = rows.FirstOrDefault(r => string.Equals((string)r?["display_name"], normalizedName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals((string)r?["ign"], normalizedName, StringComparison.OrdinalIgnoreCase));
                    JObject pick = exact ?? rows.FirstOrDefault();
                    if (pick != null) {
                        resolvedId = (string)pick["id"];
                    }
                });
            }

            onResolved(resolvedId);
        }

        private IEnumerator UpdateMatchPlayerCountIfNeeded(int actualPlayerCount, string source) {
            if (actualPlayerCount <= 0 || string.IsNullOrEmpty(_currentMatchId)) {
                yield break;
            }

            if (_lastUploadedPlayerCount == actualPlayerCount) {
                yield break;
            }

            string json = "{" + $"\"player_count\":{actualPlayerCount}" + "}";
            yield return CallAPI($"/Match/{_currentMatchId}", "PUT", json, (res) => {
                JObject response = ParseApiSingleObject(res);
                if (response != null) {
                    _lastUploadedPlayerCount = actualPlayerCount;
                    Log($"<color=green>[Match Count] ✓ Updated match player_count to {actualPlayerCount} ({source})</color>");
                } else {
                    Log($"<color=yellow>[Match Count] Could not confirm player_count update to {actualPlayerCount} ({source})</color>");
                }
            });
        }

        private void CacheLeaderboardSnapshot(List<SBGLLiveLeaderboard.LiveLeaderboardPlugin.SBGLPlayer> players, string source) {
            if (players == null || players.Count == 0) {
                return;
            }

            var snapshot = players
                .Where(player => player != null && !string.IsNullOrWhiteSpace(player.Name))
                .Select(player => new CachedLeaderboardPlayer {
                    Name = player.Name.Trim(),
                    BaseScore = player.BaseScore,
                    RawStrokes = player.RawStrokes ?? string.Empty
                })
                .ToList();

            if (snapshot.Count == 0) {
                return;
            }

            bool newSnapshotHasScores = SnapshotHasMeaningfulScores(snapshot);
            bool existingSnapshotHasScores = SnapshotHasMeaningfulScores(_finalLeaderboardSnapshot);

            if (!newSnapshotHasScores && existingSnapshotHasScores) {
                Log($"<color=yellow>[Match Snapshot] Ignoring zeroed {source} snapshot and keeping last in-game results</color>");
                return;
            }

            _finalLeaderboardSnapshot = snapshot;
        }

        private bool SnapshotHasMeaningfulScores(List<CachedLeaderboardPlayer> snapshot) {
            if (snapshot == null || snapshot.Count == 0) {
                return false;
            }

            foreach (var player in snapshot) {
                if (player == null) continue;
                if (player.BaseScore != 0) return true;
                if (ParseScoreVsPar(player.RawStrokes) != 0) return true;
            }

            return false;
        }

        private int ParseScoreVsPar(string rawStrokes) {
            if (string.IsNullOrWhiteSpace(rawStrokes)) {
                return 0;
            }

            string strokeStr = rawStrokes.Replace("±", "").Trim();
            int.TryParse(strokeStr, out int scoreVsPar);
            return scoreVsPar;
        }

        /// <summary>
        /// Build a deterministic mapping of player name -> unique finish position (1-based)
        /// based on the provided leaderboard snapshot. Tie-breakers: AdjustedPoints (desc),
        /// BaseScore (desc), stroke vs par (asc), then name (ordinal, case-insensitive).
        /// </summary>
        private Dictionary<string, int> BuildFinishPositionMap(List<SBGLLiveLeaderboard.LiveLeaderboardPlugin.SBGLPlayer> leaderboard) {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (leaderboard == null || leaderboard.Count == 0) return map;

            var ordered = leaderboard
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new { Player = p, Stroke = ParseScoreVsPar(p.RawStrokes) })
                .OrderByDescending(x => x.Player.AdjustedPoints)
                .ThenByDescending(x => x.Player.BaseScore)
                .ThenBy(x => x.Stroke)
                .ThenBy(x => x.Player.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int pos = 1;
            foreach (var item in ordered) {
                if (!map.ContainsKey(item.Player.Name)) {
                    map[item.Player.Name] = pos++;
                }
            }

            return map;
        }

        private Dictionary<string, int> BuildFinishPositionMap(List<CachedLeaderboardPlayer> leaderboard) {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (leaderboard == null || leaderboard.Count == 0) return map;

            var ordered = leaderboard
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new { Player = p, Stroke = ParseScoreVsPar(p.RawStrokes), Adjusted = p.BaseScore + (ParseScoreVsPar(p.RawStrokes) * -10) })
                .OrderByDescending(x => x.Adjusted)
                .ThenByDescending(x => x.Player.BaseScore)
                .ThenBy(x => x.Stroke)
                .ThenBy(x => x.Player.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int pos = 1;
            foreach (var item in ordered) {
                if (!map.ContainsKey(item.Player.Name)) {
                    map[item.Player.Name] = pos++;
                }
            }

            return map;
        }

        private IEnumerator EnrichPlayerIdsFromLeaderboard(List<SBGLLiveLeaderboard.LiveLeaderboardPlugin.SBGLPlayer> startingLeaderboard, List<string> playerIds) {
            if (startingLeaderboard == null || startingLeaderboard.Count == 0 || playerIds == null) yield break;

            List<string> leaderboardNames = startingLeaderboard?.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name).ToList() ?? new List<string>();
            var testOverrides = GetTestPlayerOverrides(leaderboardNames);
            if (testOverrides.Count > 0) {
                Log($"<color=magenta>[Test Override] {testOverrides.Count} player override(s) active for this match</color>");
            }

            var knownIds = new HashSet<string>(playerIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_userProfile != null && !string.IsNullOrWhiteSpace(_userProfile.display_name) && !string.IsNullOrWhiteSpace(_userProfile.id)) {
                knownNames.Add(_userProfile.display_name);
                knownIds.Add(_userProfile.id);
                if (!playerIds.Contains(_userProfile.id)) playerIds.Add(_userProfile.id);
            }

            // Learn names for already-known IDs so we don't duplicate via leaderboard lookup.
            foreach (string existingId in knownIds.ToList()) {
                if (_userProfile != null && string.Equals(existingId, _userProfile.id, StringComparison.OrdinalIgnoreCase)) continue;

                yield return CallAPI($"/Player/{existingId}", "GET", "", (res) => {
                    JObject profile = ParseApiSingleObject(res);
                    string existingName = (string)profile?["display_name"];
                    if (!string.IsNullOrWhiteSpace(existingName)) {
                        knownNames.Add(existingName);
                    }
                });
            }

            // Resolve missing leaderboard players to player IDs via exact name lookup.
            foreach (var lbPlayer in startingLeaderboard) {
                if (lbPlayer == null || string.IsNullOrWhiteSpace(lbPlayer.Name)) continue;
                if (knownNames.Contains(lbPlayer.Name)) continue;

                // Substitute the test override name (if any) when querying the API.
                // The in-game display name is kept as the leaderboard key; the SBGL name
                // is only used for the player-ID lookup so the right record gets linked.
                string lookupName = ApplyTestPlayerOverride(lbPlayer.Name, testOverrides);

                string playerIdFromName = null;
                yield return ResolvePlayerIdByNameFromApi(lookupName, (id) => playerIdFromName = id);

                if (!string.IsNullOrWhiteSpace(playerIdFromName)) {
                    knownNames.Add(lbPlayer.Name);
                    if (knownIds.Add(playerIdFromName)) {
                        playerIds.Add(playerIdFromName);
                        Log($"<color=cyan>[Match Creation] Resolved leaderboard player {lbPlayer.Name} -> {playerIdFromName}</color>");
                    }
                } else {
                    Log($"<color=yellow>[Match Creation] Could not resolve leaderboard player '{lbPlayer.Name}' to a Player ID</color>");
                }
            }
        }

        private IEnumerator FinalizeMatchStats() {
            // Pro Series match submission is handled manually
            if (PlayerPrefs.GetString("MatchType", "").Contains("pro_series")) {
                _matchStatsSubmitted = true;
                yield break;
            }

            Log($"<color=cyan>[Match Finalize] Performing final score update...</color>");

            if (_finalLeaderboardSnapshot == null || _finalLeaderboardSnapshot.Count == 0) {
                Log($"<color=yellow>[Match Finalize] No cached end-of-match leaderboard snapshot available — skipping final upload to avoid driving range zeros</color>");
                _matchStatsSubmitted = true;
                yield break;
            }

            int finalPlayerCount = _finalLeaderboardSnapshot.Count(p => p != null && !string.IsNullOrWhiteSpace(p.Name));
            if (finalPlayerCount > 0) {
                _matchExpectedPlayerCount = finalPlayerCount;
                yield return UpdateMatchPlayerCountIfNeeded(finalPlayerCount, "cached final leaderboard");
            }

            Log($"<color=cyan>[Match Finalize] Using cached final snapshot with {_finalLeaderboardSnapshot.Count} players</color>");

            // Build test override map for this leaderboard
            List<string> leaderboardNames = _finalLeaderboardSnapshot?.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name).ToList() ?? new List<string>();
            var testOverrides = GetTestPlayerOverrides(leaderboardNames);

            // Build a deterministic mapping of player name -> unique finish position for final leaderboard
            var finalPositionMap = BuildFinishPositionMap(_finalLeaderboardSnapshot);

            foreach (var player in _finalLeaderboardSnapshot) {
                if (player == null) continue;

                int finalGamePoints = player.BaseScore;
                int finalScoreVsPar = ParseScoreVsPar(player.RawStrokes);
                int finishPosition = 0;
                if (!string.IsNullOrWhiteSpace(player.Name)) finalPositionMap.TryGetValue(player.Name, out finishPosition);

                // Find entry ID for this player
                TryGetPlayerIdForName(player.Name, out string playerId);
                _playerMatchEntryIds.TryGetValue(playerId ?? "", out string entryId);

                if (!string.IsNullOrEmpty(entryId)) {
                    // Known entry — perform final update
                    Log($"<color=cyan>[Match Finalize] Final update for {player.Name}: {finalGamePoints} pts, {finalScoreVsPar} vs par, pos {finishPosition}</color>");
                    yield return UpdateMatchEntry(entryId, playerId, player.Name, finalGamePoints, finalScoreVsPar, finishPosition);
                } else {
                    // Player is on the final leaderboard but has no entry (joined late or missed round-start capture)
                    Log($"<color=yellow>[Match Finalize] {player.Name} has no entry — creating late entry...</color>");

                    // Resolve player ID if we still don't have one
                    if (string.IsNullOrEmpty(playerId)) {
                        string finalizeLookupName = ApplyTestPlayerOverride(player.Name, testOverrides);
                        yield return ResolvePlayerIdByNameFromApi(finalizeLookupName, (id) => playerId = id);
                    }

                    if (string.IsNullOrEmpty(playerId)) {
                        Log($"<color=yellow>[Match Finalize] Cannot resolve player ID for {player.Name} — skipping late entry</color>");
                        continue;
                    }

                    // Fetch pre-match MMR for the late player
                    string preMatchMmr = null;
                    yield return CallAPI($"/Player/{playerId}", "GET", "", (res) => {
                        JObject profile = ParseApiSingleObject(res);
                        object mmrObj = profile?["current_mmr"];
                        if (mmrObj != null) preMatchMmr = mmrObj.ToString();
                    });

                    yield return EnsureMatchEntryForPlayer(
                        "Match Finalize",
                        playerId,
                        player.Name,
                        preMatchMmr,
                        null, // postMatchMmr
                        finalGamePoints,
                        finalScoreVsPar,
                        finishPosition,
                        "Late entry - joined after match start",
                        (id) => entryId = id);

                    if (!string.IsNullOrEmpty(entryId)) {
                        yield return UpdateMatchEntry(entryId, playerId, player.Name, finalGamePoints, finalScoreVsPar, finishPosition);
                    }
                }
            }

            if (_currentSession?.player_ids != null && _currentSession.player_ids.Count > 0) {
                foreach (string sessionPlayerId in _currentSession.player_ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)) {
                    if (_playerMatchEntryIds.ContainsKey(sessionPlayerId)) {
                        continue;
                    }

                    yield return CreatePlaceholderMatchEntryForMissingSessionPlayer(sessionPlayerId);
                }

                int expectedPlayerCount = _currentSession.player_ids.Count(id => !string.IsNullOrWhiteSpace(id));
                if (expectedPlayerCount > _matchExpectedPlayerCount) {
                    _matchExpectedPlayerCount = expectedPlayerCount;
                    yield return UpdateMatchPlayerCountIfNeeded(expectedPlayerCount, "session player_ids fallback");
                }
            }

            _matchStatsSubmitted = true;
            Log($"<color=green>[Match Finalize] ✓ Match stats finalized</color>");
        }

        private IEnumerator CreatePlaceholderMatchEntryForMissingSessionPlayer(string playerId) {
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(_currentMatchId)) {
                yield break;
            }

            string playerName = null;
            string preMatchMmr = null;

            if (_userProfile != null && string.Equals(playerId, _userProfile.id, StringComparison.OrdinalIgnoreCase)) {
                playerName = _userProfile.display_name;
                preMatchMmr = _userProfile.current_mmr.ToString();
            } else {
                yield return CallAPI($"/Player/{playerId}", "GET", "", (res) => {
                    JObject profile = ParseApiSingleObject(res);
                    if (profile != null) {
                        playerName = (string)profile["display_name"];
                        object mmrObj = profile["current_mmr"];
                        if (mmrObj != null) {
                            preMatchMmr = mmrObj.ToString();
                        }
                    }
                });
            }

            if (string.IsNullOrWhiteSpace(playerName)) {
                playerName = playerId;
            }

            int gamePoints = 0;
            int scoreVsPar = 0;

            if (_lastSubmittedScores.TryGetValue(playerName, out int lastScore)) {
                gamePoints = lastScore;
            } else if (_cachedLeaderboardScores.TryGetValue(playerName, out int cachedScore)) {
                gamePoints = cachedScore;
            }

            if (_lastSubmittedScoresVsPar.TryGetValue(playerName, out int lastVsPar)) {
                scoreVsPar = lastVsPar;
            } else if (_cachedLeaderboardScoresVsPar.TryGetValue(playerName, out int cachedVsPar)) {
                scoreVsPar = cachedVsPar;
            }

            string entryId = null;
            yield return EnsureMatchEntryForPlayer(
                "Match Finalize",
                playerId,
                playerName,
                preMatchMmr,
                null, // postMatchMmr
                gamePoints,
                scoreVsPar,
                0,
                "Placeholder entry created because player was in the session but missing from the final leaderboard snapshot",
                (id) => entryId = id);

            if (!string.IsNullOrEmpty(entryId)) {
                _playerMatchEntryIds[playerId] = entryId;
                _playerIdsByName[playerName.Trim()] = playerId;
                _lastSubmittedScores[playerName] = gamePoints;
                _lastSubmittedScoresVsPar[playerName] = scoreVsPar;
            } else {
                Log($"<color=yellow>[Match Finalize] Could not create placeholder MatchEntry for missing session player {playerName}</color>");
            }
        }

        private IEnumerator SubmitMatchStats() {
            if (_userProfile == null || _currentSession == null) {
                Log("<color=red>[Match Stats] Failed: Missing profile or session</color>");
                yield break;
            }

            // Pro Series match submission is handled manually
            if (PlayerPrefs.GetString("MatchType", "").Contains("pro_series")) {
                _matchStatsSubmitted = true;
                yield break;
            }

            Log($"<color=cyan>[Match Stats] Collecting data for session: {_currentSession.id}</color>");

            // Calculate match duration
            float matchDuration = 0f;
            if (_matchStartTime.HasValue) {
                matchDuration = (float)(DateTime.UtcNow - _matchStartTime.Value).TotalSeconds;
            }

            // Collect available match data
            MatchStats stats = CollectMatchStats(matchDuration);
            if (stats == null) {
                Log("<color=red>[Match Stats] Failed to collect stats</color>");
                yield break;
            }

            Log($"<color=cyan>[Match Stats] Duration: {matchDuration}s | Host: {stats.is_host} | Player: {stats.player_name}</color>");

            // Step 1: Submit Match entry
            string matchId = null;
            yield return SubmitMatchEntry(stats, (id) => matchId = id);

            if (string.IsNullOrEmpty(matchId)) {
                Log("<color=red>[Match Stats] Failed to get Match ID from submission</color>");
                yield break;
            }

            // Link Match ID back to the MatchmakingSession so the website can detect mod-submitted matches
            yield return CallAPI($"/MatchmakingSession/{_currentSession.id}", "PUT", $"{{\"match_id\":\"{matchId}\"}}", (res) => {
                JObject response = ParseApiSingleObject(res);
                if (response != null) {
                    _currentSession.match_id = matchId;
                    Log($"<color=green>[Match Stats] ✓ MatchmakingSession {_currentSession.id} linked to match: {matchId}</color>");
                }
                else
                    Log($"<color=yellow>[Match Stats] Could not confirm MatchmakingSession update</color>");
            });

            // Pre-fetch leaderboard data for ALL players to avoid missing scores
            Dictionary<string, int> playerScores = new Dictionary<string, int>();
            Dictionary<string, int> playerScoresVsPar = new Dictionary<string, int>();
            
            try {
                var liveLeaderboard = UnityEngine.Object.FindAnyObjectByType<SBGLLiveLeaderboard.LiveLeaderboardPlugin>(FindObjectsInactive.Include);
                if (liveLeaderboard != null) {
                    var allLeaderboardPlayers = liveLeaderboard.GetCurrentLeaderboard();
                    Log($"<color=cyan>[Match Stats] Leaderboard has {allLeaderboardPlayers.Count} visible players</color>");
                    
                    foreach (var leaderboardPlayer in allLeaderboardPlayers) {
                        if (leaderboardPlayer == null) continue;
                        
                        int gamePoints = leaderboardPlayer.BaseScore;
                        int scoreVsPar = 0;
                        
                        // Extract stroke offset from RawStrokes (e.g., "+5" or "-2")
                        if (!string.IsNullOrEmpty(leaderboardPlayer.RawStrokes)) {
                            string strokeStr = leaderboardPlayer.RawStrokes.Replace("±", "").Trim();
                            int.TryParse(strokeStr, out scoreVsPar);
                        }
                        
                        playerScores[leaderboardPlayer.Name] = gamePoints;
                        playerScoresVsPar[leaderboardPlayer.Name] = scoreVsPar;
                        Log($"<color=cyan>[Match Stats] Cached: {leaderboardPlayer.Name} = {gamePoints} pts, {scoreVsPar} vs par</color>");
                    }
                } else {
                    Log($"<color=yellow>[Match Stats] LiveLeaderboard not found - will use placeholder scores</color>");
                }
            } catch (System.Exception ex) {
                Log($"<color=yellow>[Match Stats] Error fetching leaderboard data: {ex.Message}</color>");
            }
            
            // Store cached scores for retrieval by SubmitMatchEntryForPlayer
            _cachedLeaderboardScores = playerScores;
            _cachedLeaderboardScoresVsPar = playerScoresVsPar;

            // Step 2: Submit MatchEntry for each player in the session
            if (_currentSession.player_ids != null && _currentSession.player_ids.Count > 0) {
                Log($"<color=cyan>[Match Stats] Creating MatchEntry records for {_currentSession.player_ids.Count} players</color>");
                foreach (string playerId in _currentSession.player_ids) {
                    yield return SubmitMatchEntryForPlayer(matchId, playerId);
                }
            }

            // Only mark as submitted AFTER successful completion
            _matchStatsSubmitted = true;
            Log("<color=green>[Match Stats] ✓ Match and player entries submitted successfully</color>");
        }

        private MatchStats CollectMatchStats(float duration) {
            try {
                string activeSessionId = _currentSession != null ? _currentSession.id : (_localManualSessionId ?? "local-manual-session");

                // Collect basic match metadata
                var stats = new MatchStats {
                    matchmaking_session_id = activeSessionId,
                    match_id = activeSessionId,
                    player_id = _userProfile.id,
                    player_name = _userProfile.display_name,
                    match_date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    duration_seconds = (int)duration,
                    is_host = _isHost,
                    status = "completed"
                };

                // TODO: Integrate with actual game scoreboard data:
                // - Player score
                // - Course played
                // - Opponent info
                // - Hole-by-hole scores
                // This would require hooking into game events or reading from UI elements

                return stats;
            } catch (System.Exception ex) {
                Log($"<color=red>[Match Stats] Error collecting stats: {ex.Message}</color>");
                return null;
            }
        }

        private IEnumerator FetchActiveSeasonId() {
            _activeSeasonFetched = true; // Mark as attempted so we don't retry every loop tick
            string query = UnityWebRequest.EscapeURL("{\"status\":\"Active\"}");
            yield return CallAPI($"/Season?q={query}&limit=1", "GET", "", (res) => {
                if (string.IsNullOrEmpty(res)) return;
                try {
                    JToken token = JToken.Parse(res);
                    JObject season = (token is JArray arr)
                        ? arr.OfType<JObject>().FirstOrDefault()
                        : token as JObject;
                    string id = season?["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) {
                        _activeSeasonId = id;
                        Log($"<color=cyan>[Season] Active season resolved: {id}</color>");
                    } else {
                        Log($"<color=yellow>[Season] No active season found in API — falling back to hardcoded ID</color>");
                    }
                } catch (System.Exception ex) {
                    Log($"<color=orange>[Season] Error parsing season response: {ex.Message}</color>");
                }
            });
        }

        private IEnumerator SubmitMatchEntry(MatchStats stats, System.Action<string> onMatchIdReceived) {
            if (stats == null) yield break;

            // Map to Match schema - these are the fields the API expects
            string seasonId = _activeSeasonId ?? Season1RuleSet.SEASON_ID;
            string rawMatchType = PlayerPrefs.GetString("MatchType", "mmr");
            string apiMatchType = rawMatchType.Contains("pro_series") ? "pro_series" : "mmr";
            bool isProSeries = apiMatchType == "pro_series";

            var payload = new JObject {
                ["matchmaking_session_id"] = stats.matchmaking_session_id,
                ["season_id"] = seasonId,
                ["match_date"] = stats.match_date,
                ["match_type"] = apiMatchType,
                ["player_count"] = Mathf.Max(0, _matchExpectedPlayerCount),
                ["status"] = "Pending",
                ["submitted_by_name"] = stats.player_name,
                ["mode"] = "",
                ["notes"] = "Auto-submitted via SBGL Unified Mod"
            };

            if (isProSeries) {
                payload["pro_series_season_id"] = Season1RuleSet.PRO_SERIES_SEASON_ID;

                int proSeriesWeek = PlayerPrefs.GetInt("ProSeriesWeek", 0);
                if (proSeriesWeek > 0)
                    payload["pro_series_week"] = proSeriesWeek;

                string proSeriesEventName = PlayerPrefs.GetString("ProSeriesEventName", "");
                if (!string.IsNullOrWhiteSpace(proSeriesEventName))
                    payload["pro_series_event_name"] = proSeriesEventName;
            }

            string json = payload.ToString(Newtonsoft.Json.Formatting.None);

            Log($"<color=cyan>[Match Stats] Submitting Match entry to API</color>");
            Log($"<color=cyan>[Match Stats] Full URL: {GetBaseApiUrl()}/Match</color>");
            Log($"<color=cyan>[Match Stats] Payload: {json}</color>");

            yield return CallAPI("/Match", "POST", json, (res) => {
                JObject response = ParseApiSingleObject(res);
                if (response != null) {
                    string entryId = (string)response["id"] ?? "unknown";
                    _lastUploadedPlayerCount = Mathf.Max(0, _matchExpectedPlayerCount);
                    Log($"<color=green>[Match Stats] ✓ Match entry created (ID: {entryId})</color>");
                    ShowUploadNotification($"Upload success: match ID {entryId}.", "success");
                    onMatchIdReceived?.Invoke(entryId);
                } else {
                    Log("<color=yellow>[Match Stats] Response received but could not parse ID</color>");
                    ShowUploadNotification("Upload failed: invalid API response.", "failure");
                }
            });
        }

        private IEnumerator SubmitMatchEntryForPlayer(string matchId, string playerId) {
            // Try to get leaderboard data for this player using cached scores
            int gamePoints = 0;
            int scoreVsPar = 0;
            string playerDisplayName = null;
            
            // First: try to determine the player's display name from the live leaderboard mapping
            // Prefer the exact name shown on the live leaderboard when available
            string leaderboardName = null;
            foreach (var kvp in _playerIdsByName) {
                if (!string.IsNullOrWhiteSpace(kvp.Value) && string.Equals(kvp.Value, playerId, StringComparison.OrdinalIgnoreCase)) {
                    leaderboardName = kvp.Key?.Trim();
                    break;
                }
            }

            if (!string.IsNullOrEmpty(leaderboardName)) {
                playerDisplayName = leaderboardName;
                Log($"<color=cyan>[Match Stats] Using leaderboard name mapping for {playerId}: {playerDisplayName}</color>");
            }

            // If we still don't have a display name and this is the current user, use profile
            if (string.IsNullOrEmpty(playerDisplayName) && playerId == _userProfile.id) {
                playerDisplayName = _userProfile.display_name;
                Log($"<color=cyan>[Match Stats] Current user: {playerDisplayName}</color>");
            }

            // If we still don't have a display name, fetch from the API as a fallback
            if (string.IsNullOrEmpty(playerDisplayName)) {
                Log($"<color=cyan>[Match Stats] Fetching profile for opponent {playerId} (fallback)</color>");
                yield return CallAPI($"/Player/{playerId}", "GET", "", (res) => {
                    try {
                        JObject profile = ParseApiSingleObject(res);
                        if (profile != null) {
                            playerDisplayName = (string)profile["display_name"];
                            Log($"<color=cyan>[Match Stats] Opponent display name (API): {playerDisplayName}</color>");
                        }
                    } catch (System.Exception ex) {
                        Log($"<color=yellow>[Match Stats] Error parsing opponent profile: {ex.Message}</color>");
                    }
                });
            }

            // Second: look up scores in cache by display name (with retry). Use a 'found' flag
            // because legitimate scores can be zero and should not be treated as missing.
            bool foundScores = false;
            int tries = 0;
            while (!foundScores && tries < 5) {
                if (!string.IsNullOrEmpty(playerDisplayName)) {
                    // Exact lookup
                    if (_cachedLeaderboardScores.TryGetValue(playerDisplayName, out int cachedScore)) {
                        gamePoints = cachedScore;
                        _cachedLeaderboardScoresVsPar.TryGetValue(playerDisplayName, out scoreVsPar);
                        foundScores = true;
                        Log($"<color=green>[Match Stats] ✓ Found cached scores for {playerDisplayName}: {gamePoints} pts, {scoreVsPar} vs par</color>");
                        break;
                    }

                    // Case-insensitive / trimmed fallback lookup
                    var matchKey = _cachedLeaderboardScores.Keys.FirstOrDefault(k => string.Equals(k?.Trim(), playerDisplayName?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(matchKey)) {
                        gamePoints = _cachedLeaderboardScores[matchKey];
                        _cachedLeaderboardScoresVsPar.TryGetValue(matchKey, out scoreVsPar);
                        foundScores = true;
                        Log($"<color=green>[Match Stats] ✓ Found cached scores via fallback key '{matchKey}' for {playerDisplayName}: {gamePoints} pts, {scoreVsPar} vs par</color>");
                        break;
                    }
                }

                tries++;
                if (!foundScores && tries < 5) {
                    Log($"<color=yellow>[Match Stats] Scores not cached yet for {playerDisplayName ?? playerId}, retry {tries}/5...</color>");
                    yield return new WaitForSeconds(0.5f);
                }
            }

            if (!foundScores) {
                Log($"<color=yellow>[Match Stats] ⚠ No leaderboard data found for {playerDisplayName ?? playerId} after retries</color>");
            }
            
            // Build JSON with player_name field included
            string json = "{" +
                $"\"match_id\":\"{matchId}\"," +
                $"\"player_id\":\"{playerId}\"," +
                $"\"player_name\":\"{playerDisplayName ?? "Unknown"}\"," +
                $"\"game_points\":{gamePoints}," +
                $"\"score_vs_par\":{scoreVsPar}," +
                $"\"notes\":\"Auto-submitted player entry via SBGL Unified Mod\"" +
            "}";

            Log($"<color=cyan>[Match Stats] Submitting MatchEntry for {playerDisplayName ?? playerId}: {gamePoints} pts, {scoreVsPar} vs par</color>");
            
            yield return CallAPI("/MatchEntry", "POST", json, (res) => {
                JObject response = ParseApiSingleObject(res);
                if (response != null) {
                    string entryId = (string)response["id"] ?? "unknown";
                    Log($"<color=green>[Match Stats] ✓ MatchEntry created (ID: {entryId}) for player {playerDisplayName ?? playerId}</color>");
                } else {
                    Log($"<color=yellow>[Match Stats] Could not parse MatchEntry response for player {playerDisplayName ?? playerId}</color>");
                }
            });
        }

        
        // ==========================================
        // UI RENDERING
        // ==========================================
        private void OnGUI() {
            try
            {
                bool isMenuScene = SceneManager.GetActiveScene().name.ToLower().Contains("menu");

                // Upload notification is relevant in gameplay too; draw it regardless of scene.
                DrawUploadNotification();
                DrawLiveUploadDebugOverlay();

                // Only show menu UI in menu scenes
                // Match configuration display is handled by RuleSetDisplayManager
                if (!isMenuScene) return;

                // GUI.skin can be unavailable during plugin Awake; initialize style lazily at draw time.
                if (_centerLabelStyle == null) {
                    _centerLabelStyle = GUI.skin != null
                        ? new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter }
                        : new GUIStyle { alignment = TextAnchor.MiddleCenter };
                }

                if (_debugLineStyle == null) {
                    _debugLineStyle = GUI.skin != null ? new GUIStyle(GUI.skin.label) : new GUIStyle();
                    _debugLineStyle.alignment = TextAnchor.MiddleLeft;
                    _debugLineStyle.fontSize = 10;
                    _debugLineStyle.wordWrap = false;
                    _debugLineStyle.clipping = TextClipping.Clip;
                    _debugLineStyle.richText = false;
                }

                float uiWidth = 350f;
                float rightX = Screen.width - uiWidth - 30;
                float uiHeight = (_showLogsConfig?.Value ?? false) ? 340f : 230f; 
                if ((_showFlowDebugConfig?.Value ?? false)) uiHeight += 145f;

                GUI.DrawTexture(new Rect(rightX, 20, uiWidth, uiHeight), _solidBgTex);
                GUI.Box(new Rect(rightX, 20, uiWidth, uiHeight), "<b>SBGL MATCH MAKING ASSISTANT</b>");

            // --- MATCHMAKING BUTTONS (WITH INITIALIZATION LOCK) ---
            if (_currentSession != null) {
                if (_currentSession.status == "pending_accept") {
                    GUI.backgroundColor = _hasAccepted ? Color.gray : Color.yellow;
                    if (GUI.Button(new Rect(rightX + 10, 50, uiWidth - 20, 50), _hasAccepted ? "WAITING FOR OTHERS..." : "ACCEPT MATCH")) {
                        if (!_hasAccepted) StartCoroutine(AcceptMatch());
                    }
                } else if (_currentSession.status == "ready") {
                    if (_isHost) {
                        // Ruleset is applied via the Driving Range RuleSetDisplayManager panel.
                        // Show initialize button directly.
                        GUI.backgroundColor = Color.cyan;
                        if (GUI.Button(new Rect(rightX + 10, 50, uiWidth - 20, 50), "INITIALIZE HOST")) {
                            InitiateHostSequence();
                        }
                    } else {
                        // Non-host: Show auto-join status
                        if (_hasAccepted && !string.IsNullOrEmpty(_currentSession.steam_lobby_link)) {
                            GUI.backgroundColor = Color.green;
                            GUI.enabled = false;
                            GUI.Button(new Rect(rightX + 10, 50, uiWidth - 20, 50), "AUTO-JOINING...");
                            GUI.enabled = true;
                        } else if (!_hasAccepted) {
                            GUI.backgroundColor = Color.yellow;
                            if (GUI.Button(new Rect(rightX + 10, 50, uiWidth - 20, 50), "ACCEPT & JOIN")) {
                                StartCoroutine(AcceptMatch());
                            }
                        } else {
                            GUI.backgroundColor = Color.gray;
                            GUI.enabled = false;
                            GUI.Button(new Rect(rightX + 10, 50, uiWidth - 20, 50), "JOINING...");
                            GUI.enabled = true;
                        }
                    }
                }
            } else {
                // Prevent interaction until the profile and queue state are synced
                bool canInteract = !_isInitializing && _userProfile != null;
                GUI.enabled = canInteract;

                GUI.backgroundColor = _isQueueing ? new Color(0.8f, 0.1f, 0.1f, 1.0f) : new Color(0.1f, 0.6f, 0.1f, 1.0f);
                
                string btnText;
                if (_isInitializing) btnText = "SYNCING WITH SERVER...";
                else if (_userProfile == null) btnText = "RESOLVING PROFILE...";
                else btnText = _isQueueing ? "LEAVE QUEUE" : "JOIN QUEUE";

                if (GUI.Button(new Rect(rightX + 10, 50, uiWidth - 20, 50), btnText)) {
                    if (!_isQueueing) StartCoroutine(MatchmakingLoop()); else StartCoroutine(LeaveQueue());
                }
                GUI.enabled = true;
            }

            float offset = 0f;
            GUI.Box(new Rect(rightX + 10, 110 + offset, uiWidth - 20, 100), ""); 

            if (_userProfile != null) {
                if (_profileTexture) GUI.DrawTexture(new Rect(rightX + 20, 120 + offset, 40, 40), _profileTexture);
                GUI.Label(new Rect(rightX + 70, 120 + offset, 240, 20), $"User: <b>{_userProfile.display_name}</b>");
                GUI.Label(new Rect(rightX + 70, 135 + offset, 240, 20), $"<color=#FFA500><size=10>{_webStatus}</size></color>");

                // --- STATS ROW (Mimicking Website) ---
                float statsY = 160 + offset;

                // 4-column stat row: TIME | QUEUED | MATCHED | YOUR MMR
                float statWidth = (uiWidth - 40) / 4f;

                // Column 1: TIME
                GUI.Label(new Rect(rightX + 20, statsY, statWidth, 20), "<color=#FFFFFF><size=10><b>TIME</b></size></color>", _centerLabelStyle);
                string timeStr = "00:00";
                if (_isQueueing && _queueStartTime.HasValue) {
                    TimeSpan elapsed = DateTime.UtcNow - _queueStartTime.Value.ToUniversalTime();
                    timeStr = elapsed.TotalSeconds < 0 ? "00:00" : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
                }
                GUI.Label(new Rect(rightX + 20, statsY + 15, statWidth, 25), $"<color=#00FFCC><size=16><b>{timeStr}</b></size></color>", _centerLabelStyle);

                // Column 2: QUEUED
                GUI.Label(new Rect(rightX + 20 + statWidth, statsY, statWidth, 20), "<color=#FFFFFF><size=10><b>QUEUED</b></size></color>", _centerLabelStyle);
                GUI.Label(new Rect(rightX + 20 + statWidth, statsY + 15, statWidth, 25), $"<color=#00FFCC><size=16><b>{_queuedCount}</b></size></color>", _centerLabelStyle);

                // Column 3: MATCHED
                GUI.Label(new Rect(rightX + 20 + (statWidth * 2), statsY, statWidth, 20), "<color=#FFFFFF><size=10><b>MATCHED</b></size></color>", _centerLabelStyle);
                GUI.Label(new Rect(rightX + 20 + (statWidth * 2), statsY + 15, statWidth, 25), $"<color=#FFD700><size=16><b>{_matchedCount}</b></size></color>", _centerLabelStyle);

                // Column 4: YOUR MMR
                GUI.Label(new Rect(rightX + 20 + (statWidth * 3), statsY, statWidth, 20), "<color=#FFFFFF><size=10><b>YOUR MMR</b></size></color>", _centerLabelStyle);
                GUI.Label(new Rect(rightX + 20 + (statWidth * 3), statsY + 15, statWidth, 25), $"<color=#FFFFFF><size=16><b>{_userProfile.current_mmr}</b></size></color>", _centerLabelStyle);
            }

            float contentY = 215 + offset;

            // --- TEMP FLOW DIAGNOSTICS ---
            if ((_showFlowDebugConfig?.Value ?? false)) {
                GUI.Label(new Rect(rightX + 15, contentY, uiWidth, 20), "<b>FLOW DIAGNOSTICS (TEMP)</b>");
                contentY += 18f;

                float debugLineHeight = 14f;
                float debugTopPadding = 6f;
                int debugLineCount = 7;
                float debugBoxHeight = debugTopPadding + (debugLineCount * debugLineHeight) + 6f;

                GUI.Box(new Rect(rightX + 10, contentY, uiWidth - 20, debugBoxHeight), "");
                GUI.Label(new Rect(rightX + 16, contentY + debugTopPadding + (0 * debugLineHeight), uiWidth - 30, debugLineHeight), $"sync_tick={_syncTickCount} host={_isHost} accepted={_hasAccepted}", _debugLineStyle);
                GUI.Label(new Rect(rightX + 16, contentY + debugTopPadding + (1 * debugLineHeight), uiWidth - 30, debugLineHeight), $"session={(_currentSession != null ? _currentSession.id : "none")} status={(_currentSession != null ? _currentSession.status : "none")}", _debugLineStyle);
                GUI.Label(new Rect(rightX + 16, contentY + debugTopPadding + (2 * debugLineHeight), uiWidth - 30, debugLineHeight), $"lobby_events={_lobbyCreatedEventCount} link_present={!string.IsNullOrEmpty(_currentSession?.steam_lobby_link)}", _debugLineStyle);
                GUI.Label(new Rect(rightX + 16, contentY + debugTopPadding + (3 * debugLineHeight), uiWidth - 30, debugLineHeight), $"upload a/s/f={_steamLinkUploadAttempts}/{_steamLinkUploadSuccesses}/{_steamLinkUploadFailures}", _debugLineStyle);
                GUI.Label(new Rect(rightX + 16, contentY + debugTopPadding + (4 * debugLineHeight), uiWidth - 30, debugLineHeight), $"autojoin a/s/f={_autoJoinAttempts}/{_autoJoinSuccesses}/{_autoJoinFailures}", _debugLineStyle);
                GUI.Label(new Rect(rightX + 16, contentY + debugTopPadding + (5 * debugLineHeight), uiWidth - 30, debugLineHeight), $"last_upload_err={Truncate(_lastUploadError, 42)}", _debugLineStyle);
                GUI.Label(new Rect(rightX + 16, contentY + debugTopPadding + (6 * debugLineHeight), uiWidth - 30, debugLineHeight), $"last_join_err={Truncate(_lastAutoJoinError, 42)}", _debugLineStyle);

                contentY += debugBoxHeight + 5f;
            }

            // --- ACTIVE QUEUE LIST (hidden) ---
            // GUI.Label(new Rect(rightX + 15, contentY, uiWidth, 25), "<b>ACTIVE QUEUE:</b>");
            // _playerScroll = GUI.BeginScrollView(new Rect(rightX + 10, contentY + 25, uiWidth - 20, 70), _playerScroll, new Rect(0,0, uiWidth - 40, _queuedPlayers.Count * 22));
            // for (int i = 0; i < _queuedPlayers.Count; i++) {
            //     GUI.Label(new Rect(5, i * 22, 300, 22), $"• {_queuedPlayers[i].name} <color=#4CAF50>({_queuedPlayers[i].mmr} MMR)</color>");
            // }
            // GUI.EndScrollView();

            if (_showLogsConfig.Value) {
                float logsY = contentY + 5;
                GUI.Label(new Rect(rightX + 15, logsY, uiWidth, 25), "<b>SYSTEM LOGS:</b>");
                _logScroll = GUI.BeginScrollView(new Rect(rightX + 10, logsY + 25, uiWidth - 20, 70), _logScroll, new Rect(0,0, uiWidth - 40, _debugLogs.Count * 20));
                for (int i = 0; i < _debugLogs.Count; i++) {
                    GUI.Label(new Rect(5, i * 20, 300, 20), $"<size=10>{_debugLogs[i]}</size>");
                }
                GUI.EndScrollView();
            }
            
            }
            catch (System.Exception ex)
            {
                Log($"<color=red>[CRITICAL] Exception in OnGUI: {ex.Message} | StackTrace: {ex.StackTrace}</color>");
            }
        }

        private void DrawUploadNotification()
        {
            if (string.IsNullOrEmpty(_uploadNotification)) return;

            float timeSinceNotification = (float)(DateTime.UtcNow - _uploadNotificationTime).TotalSeconds;
            if (timeSinceNotification >= _uploadNotificationDuration)
            {
                _uploadNotification = "";
                return;
            }

            float alpha = 1.0f;
            if (timeSinceNotification > _uploadNotificationDuration - 1.0f)
            {
                alpha = Mathf.Lerp(1.0f, 0.0f, (timeSinceNotification - (_uploadNotificationDuration - 1.0f)) / 1.0f);
            }

            float notificationWidth = 500f;
            float notificationHeight = 50f;
            float notificationX = (Screen.width - notificationWidth) / 2;
            float notificationY = Screen.height - 100f;

            GUI.color = new Color(1, 1, 1, alpha);
            GUI.Box(new Rect(notificationX, notificationY, notificationWidth, notificationHeight), "");

            GUIStyle notificationStyle = GUI.skin != null ? new GUIStyle(GUI.skin.label) : new GUIStyle();
            notificationStyle.alignment = TextAnchor.MiddleCenter;
            notificationStyle.fontSize = 14;
            notificationStyle.richText = true;

            string htmlColor = ColorUtility.ToHtmlStringRGB(_uploadNotificationColor);
            GUI.Label(new Rect(notificationX, notificationY + 10, notificationWidth, 30), $"<color=#{htmlColor}><b>{_uploadNotification}</b></color>", notificationStyle);
            GUI.color = Color.white;
        }

        private void DrawLiveUploadDebugOverlay()
        {
            if (!(_showFlowDebugConfig?.Value ?? false)) return;

            float width = 860f;
            float height = 62f;
            float x = (Screen.width - width) / 2f;
            float y = Screen.height - 170f;

            GUIStyle style = GUI.skin != null ? new GUIStyle(GUI.skin.label) : new GUIStyle();
            style.alignment = TextAnchor.MiddleLeft;
            style.fontSize = 11;
            style.richText = true;

            GUI.Box(new Rect(x, y, width, height), "");
            string line1 = $"LobbySources s='{Truncate(_debugLobbySessionSource, 24)}' p='{Truncate(_debugLobbyPrefsSource, 24)}' c='{Truncate(_debugLobbyCapturedSource, 24)}'";
            string line2 = $"LobbyResolved '{Truncate(_debugLobbyResolved, 40)}' via={_debugLobbyResolvedBy} | SessionType='{Truncate(_currentSession?.match_type ?? "", 24)}' MatchType='{Truncate(PlayerPrefs.GetString("MatchType", ""), 24)}' HostRuleset='{Truncate(PlayerPrefs.GetString("HostRuleset", ""), 14)}'";
            GUI.Label(new Rect(x + 10f, y + 7f, width - 20f, 20f), line1, style);
            GUI.Label(new Rect(x + 10f, y + 29f, width - 20f, 20f), line2, style);
        }

        public async void JoinBySteamLink(string steamLink, string password) {
            if (string.IsNullOrEmpty(steamLink)) return;

            // Save password so the game's internal 'OnClientConnect' logic finds it
            PlayerPrefs.SetString("LobbyPassword", password);
            PlayerPrefs.Save();
            Log($"Password '{password}' cached for join.");

            try {
                // Parse the link (Format: steam://joinlobby/AppID/LobbyID/HostID)
                string[] parts = steamLink.Split('/');
                if (parts.Length < 5 || !ulong.TryParse(parts[4], out ulong lobbyId)) {
                    Log("<color=red>Invalid Steam Link</color>");
                    return;
                }

                Log($"Joining Steam Lobby: {lobbyId}...");
                
                // Use the Steamworks API to join the lobby
                var lobby = new FacepunchLib.Data.Lobby(lobbyId);
                var result = await lobby.Join();

                if (result == FacepunchLib.RoomEnter.Success) {
                    var manager = Mirror.NetworkManager.singleton;
                    manager.networkAddress = lobbyId.ToString(); // Tell Mirror to use the Steam ID

                    // Give the engine a moment to register the Steam lobby state
                    await System.Threading.Tasks.Task.Delay(200);
                    manager.StartClient();
                    _autoJoinSuccesses++;
                    _lastAutoJoinSuccessAt = DateTime.Now;
                    _lastAutoJoinError = "-";
                    Log("Client started via Steam Link.");
                } else {
                    _autoJoinFailures++;
                    _lastAutoJoinError = $"Steam room enter failure: {result}";
                    Log($"<color=red>Steam Join Failed: {result}</color>");
                }
            } catch (System.Exception ex) {
                _autoJoinFailures++;
                _lastAutoJoinError = ex.Message;
                Log($"Join Error: {ex.Message}");
            }
        }

        // ==========================================
        // HELPERS
        // ==========================================
        private IEnumerator UpdateSessionStatus(string status) {
            if (_currentSession == null || _currentSession.id == "DEBUG") yield break;
            string json = "{" + $"\"status\":\"{status}\"" + "}";
            yield return CallAPI($"/MatchmakingSession/{_currentSession.id}", "PUT", json, null);
        }

        private IEnumerator CallAPI(string endpoint, string method, string json, Action<string> onSuccess) {
            string fullUrl = GetBaseApiUrl() + endpoint;
            Log($"<color=cyan>[API] {method} {endpoint}</color>");
            Log($"<color=cyan>[API] Full URL: {fullUrl}</color>");
            
            using (UnityWebRequest req = new UnityWebRequest(fullUrl, method)) {
                if (!string.IsNullOrEmpty(json)) {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                }
                req.downloadHandler = new DownloadHandlerBuffer();
                ApplyApiHeaders(req);
                
                yield return req.SendWebRequest();
                
                if (req.result == UnityWebRequest.Result.Success) {
                    Log($"<color=green>[API] {method} {endpoint} - Success</color>");
                    onSuccess?.Invoke(req.downloadHandler.text);
                } else {
                    string errorMsg = $"[API] {method} {endpoint} failed: {req.result}";
                    if (!string.IsNullOrEmpty(req.error)) errorMsg += $" - {req.error}";
                    if (req.responseCode > 0) errorMsg += $" (HTTP {req.responseCode})";
                    if (!string.IsNullOrEmpty(req.downloadHandler?.text)) {
                        int length = Math.Min(200, req.downloadHandler.text.Length);
                        errorMsg += $" - Response: {req.downloadHandler.text.Substring(0, length)}";
                    }
                    
                    Log($"<color=red>{errorMsg}</color>");
                }
            }
        }

        private void ApplyApiHeaders(UnityWebRequest req) {
            if (req == null) return;

            // Use unified plugin's auth token
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("api_key", GetAuthToken());
            req.SetRequestHeader("X-App-Id", GetAppId());
        }

        private IEnumerator ResolveProfile(string steamName) {
            // Note: Use 'ign' in the query if that's your primary identifier, 
            // but sticking to your current display_name logic:
            string fullUrl = $"{GetBaseApiUrl()}/Player?q={UnityWebRequest.EscapeURL("{\"display_name\":\"" + steamName + "\"}")}"; 
            Log($"<color=cyan>[Init] GET /Player (resolving profile for {steamName})</color>");
            Log($"<color=cyan>[Init] Full URL: {fullUrl.Substring(0, Math.Min(150, fullUrl.Length))}...</color>");
            using (UnityWebRequest req = UnityWebRequest.Get(fullUrl)) {
                ApplyApiHeaders(req);
                yield return req.SendWebRequest();
                
                if (req.result == UnityWebRequest.Result.Success) {
                    string raw = req.downloadHandler.text;
                    JObject profile = ParseApiSingleObject(raw);
                    if (profile == null) {
                        Log("<color=orange>Profile sync failed: response was not valid JSON object.</color>");
                        yield break;
                    }

                    _userProfile = new PlayerProfile { 
                        id = (string)profile["id"],
                        display_name = (string)profile["display_name"],
                        region = (string)profile["region"] ?? "US",
                        state_province = (string)profile["state_province"]
                    };
                    
                    float.TryParse(profile["current_mmr"]?.ToString(), out _userProfile.current_mmr);
                    
                    // Log it to verify
                    Log($"Profile Sync: {_userProfile.display_name} from {_userProfile.state_province}");
                    
                    string picUrl = (string)profile["profile_pic_url"];
                    if (!string.IsNullOrEmpty(picUrl) && !_hasFetchedProfilePic) 
                        StartCoroutine(DownloadProfilePic(picUrl));
                } else {
                    Log($"<color=red>[Init] ResolveProfile failed: {req.result} - {req.error}</color>");
                }
            }
        }

        private List<JObject> ParseApiObjectList(string rawJson) {
            if (string.IsNullOrEmpty(rawJson)) return new List<JObject>();

            try {
                JToken token = JToken.Parse(rawJson);
                if (token is JArray array) {
                    return array.OfType<JObject>().ToList();
                }

                if (token is JObject obj) {
                    return new List<JObject> { obj };
                }
            } catch (System.Exception ex) {
                Log($"<color=orange>JSON parse warning (list): {ex.Message}</color>");
            }

            return new List<JObject>();
        }

        private JObject ParseApiSingleObject(string rawJson) {
            if (string.IsNullOrEmpty(rawJson)) return null;

            try {
                JToken token = JToken.Parse(rawJson);
                if (token is JObject obj) {
                    return obj;
                }

                if (token is JArray array) {
                    return array.OfType<JObject>().FirstOrDefault();
                }
            } catch (System.Exception ex) {
                Log($"<color=orange>JSON parse warning (single): {ex.Message}</color>");
            }

            return null;
        }

        private IEnumerator DownloadProfilePic(string url) {
            using (UnityWebRequest req = UnityWebRequestTexture.GetTexture(url.Replace("http://", "https://"))) {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) _profileTexture = DownloadHandlerTexture.GetContent(req);
                _hasFetchedProfilePic = true;
            }
        }

        /// <summary>
        /// Plays the alert through the same FMOD UI pipeline the base game uses.
        /// </summary>
        private void PlayMatchFoundAlert()
        {
            try
            {
                RuntimeManager.PlayOneShot(GameManager.AudioSettings.AnnouncerMainMenuTitle, default(Vector3));
                Log("<color=yellow>[Alert] ♪ Match found alert played via FMOD AnnouncerMainMenuTitle</color>");
            }
            catch (System.Exception ex)
            {
                Log($"<color=orange>[Alert] Could not play match alert: {ex.Message}</color>");
            }
        }

        private string Truncate(string value, int maxLength) {
            if (string.IsNullOrEmpty(value)) return "-";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// Validates whether a match should be uploaded based on:
        /// 1. Lobby name matches "SBGL-*" pattern
        /// </summary>
        private IEnumerator ValidateMatchUpload(Action<bool> callback)
        {
            // Pro Series matches are never auto-uploaded — skip validation entirely
            if (PlayerPrefs.GetString("MatchType", "").Contains("pro_series"))
            {
                if (!_proSeriesSkipLogged)
                {
                    Log("<color=yellow>[Match] Pro Series match — automated upload skipped</color>");
                    _proSeriesSkipLogged = true;
                }
                callback?.Invoke(false);
                yield break;
            }

            if (_userProfile == null)
            {
                yield return EnsureUserProfileResolved("Match Upload Validation", 6f);
            }

            if (_userProfile == null)
            {
                Log("<color=yellow>[Match Upload Validation] Profile is still null after waiting for resolution</color>");
                ShowUploadNotification("Upload validation failed: missing profile", "failure");
                callback?.Invoke(false);
                yield break;
            }

            // Get lobby name from either session or PlayerPrefs/current lobby
            string lobbyName = ResolveCurrentLobbyName();

            if (IgnoreSbglLobbyRequirementEnabled)
            {
                Log($"<color=orange>[Match Upload Validation] Upload All Matches enabled. Bypassing SBGL-* lobby requirement for '{lobbyName}'.</color>");
                ShowUploadNotification("Uploading match results (Upload All Matches enabled)...", "info");
                callback?.Invoke(true);
                yield break;
            }

            if (string.IsNullOrEmpty(lobbyName))
            {
                bool allowFallback = _isInGameplay || IsRankedTriggered || (_currentSession != null);
                if (allowFallback)
                {
                    Log("<color=orange>[Match Upload Validation] Lobby name unresolved (blank). Allowing upload due to active ranked/session context.</color>");
                    ShowUploadNotification("Uploading match results (lobby name unresolved)...", "info");
                    callback?.Invoke(true);
                    yield break;
                }
            }

            // "SBGL-*" means wildcard after the SBGL- prefix.
            // Accept any normalized lobby name that starts with SBGL-.
            if (!IsSbglLobbyName(lobbyName))
            {
                Log($"<color=yellow>[Match Upload Validation] Lobby name '{lobbyName}' does not match 'SBGL-*' pattern</color>");
                ShowUploadNotification($"Upload blocked: Lobby '{lobbyName}' is not SBGL-*", "warning");
                callback?.Invoke(false);
                yield break;
            }

            Log($"<color=cyan>[Match Upload Validation] ✓ Lobby name matches pattern: {lobbyName}</color>");
            ShowUploadNotification($"Uploading match results for {lobbyName}...", "info");
            
            Log($"<color=green>[Match Upload Validation] ✓ Validation passed - proceeding with upload</color>");
            callback?.Invoke(true);
        }
        
        private void ShowUploadNotification(string message, string level = "info")
        {
            if (_showUploadNoticesConfig != null && !_showUploadNoticesConfig.Value) {
                return;
            }

            _uploadNotification = message;
            _uploadNotificationTime = DateTime.UtcNow;

            switch (level)
            {
                case "success":
                    _uploadNotificationColor = new Color(0.3f, 0.95f, 0.4f);
                    break;
                case "failure":
                    _uploadNotificationColor = new Color(1f, 0.45f, 0.45f);
                    break;
                case "warning":
                    _uploadNotificationColor = new Color(1f, 0.75f, 0.35f);
                    break;
                default:
                    _uploadNotificationColor = new Color(0.2f, 0.85f, 1f);
                    break;
            }

            Log($"<color=cyan>[Upload Notification] {message}</color>");
        }

        private string ResolveCurrentLobbyName()
        {
            string sessionLobbyName = NormalizeLobbyName(_currentSession?.lobby_name);
            string playerPrefsLobbyName = NormalizeLobbyName(PlayerPrefs.GetString("LobbyName", ""));
            string capturedLobbyName = NormalizeLobbyName(SBGL.UnifiedMod.Features.CompetitivePluginCheck.CompetitivePluginCheck._currentLobbyName);

            _debugLobbySessionSource = sessionLobbyName;
            _debugLobbyPrefsSource = playerPrefsLobbyName;
            _debugLobbyCapturedSource = capturedLobbyName;

            Log($"<color=cyan>[LobbyName] Sources: session='{sessionLobbyName}', prefs='{playerPrefsLobbyName}', captured='{capturedLobbyName}'</color>");

            // Prefer whichever source actually matches SBGL-* first, then fallback to any non-empty source.
            if (IsSbglLobbyName(capturedLobbyName)) {
                _debugLobbyResolved = capturedLobbyName;
                _debugLobbyResolvedBy = "captured(sbgl)";
                return capturedLobbyName;
            }
            if (IsSbglLobbyName(sessionLobbyName)) {
                _debugLobbyResolved = sessionLobbyName;
                _debugLobbyResolvedBy = "session(sbgl)";
                return sessionLobbyName;
            }
            if (IsSbglLobbyName(playerPrefsLobbyName)) {
                _debugLobbyResolved = playerPrefsLobbyName;
                _debugLobbyResolvedBy = "prefs(sbgl)";
                return playerPrefsLobbyName;
            }

            if (!string.IsNullOrEmpty(capturedLobbyName)) {
                _debugLobbyResolved = capturedLobbyName;
                _debugLobbyResolvedBy = "captured";
                return capturedLobbyName;
            }
            if (!string.IsNullOrEmpty(sessionLobbyName)) {
                _debugLobbyResolved = sessionLobbyName;
                _debugLobbyResolvedBy = "session";
                return sessionLobbyName;
            }
            if (!string.IsNullOrEmpty(playerPrefsLobbyName)) {
                _debugLobbyResolved = playerPrefsLobbyName;
                _debugLobbyResolvedBy = "prefs";
                return playerPrefsLobbyName;
            }

            _debugLobbyResolved = "";
            _debugLobbyResolvedBy = "none";
            return "";
        }

        private static string NormalizeLobbyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            // Remove rich-text tags and invisible separators before comparison.
            string cleaned = Regex.Replace(value, "<.*?>", string.Empty)
                .Replace("\u200B", string.Empty)
                .Trim();
            return cleaned;
        }

        private static bool IsSbglLobbyName(string lobbyName)
        {
            return !string.IsNullOrEmpty(lobbyName)
                && lobbyName.StartsWith("SBGL-", StringComparison.OrdinalIgnoreCase);
        }

        public class PlayerProfile 
        { 
            public string id, display_name, region, state_province; // Added state_province
            public float current_mmr; 
        }
        public class MatchmakingSession { 
            public string id, lobby_name, lobby_password, host_player_id, status, match_id; 
            public string lobby_id;
            public string host_steam_id;
            public string steam_lobby_link;
            public List<string> player_ids; // All players in the session
            public List<string> accepted_player_ids; // Players who accepted
            
            // Match configuration from API
            public string match_type; // e.g., "ranked_season_1"
            public string selected_course; // e.g., "Taiga Woods" - readable course name
            public int season; // e.g., 1
        }
        public class MatchStats {
            public string matchmaking_session_id;
            public string match_id;
            public string player_id;
            public string player_name;
            public string match_date;
            public int duration_seconds;
            public bool is_host;
            public string status;
            
            // Match configuration from ranked matches
            public string course_name; // e.g., "Taiga Woods"
            public string match_type; // e.g., "ranked_season_1"
            public int season; // e.g., 1
            
            // TODO: Add fields as we collect actual game data:
            // public int player_score;
            // public int opponent_score;
            // public List<int> hole_scores;
            // public string result; // "win" / "loss" / "tie"
        }
        private class CachedLeaderboardPlayer {
            public string Name;
            public int BaseScore;
            public string RawStrokes;
        }
        public struct PlayerData { public string name, mmr; }
    }

    [HarmonyPatch(typeof(BNetworkManager), nameof(BNetworkManager.LobbyName), MethodType.Setter)]
    public static class LobbyPatch { 
        public static void Prefix(ref string value) { 
            if (SBGLPlugin.IsRankedTriggered) {
                var pref = PlayerPrefs.GetString("LobbyName");
                if (!string.IsNullOrEmpty(pref)) value = pref;
            }
        } 
    }
}