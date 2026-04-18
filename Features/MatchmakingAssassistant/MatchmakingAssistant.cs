using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
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
        private string GetBaseApiUrl() => UnifiedPlugin.GetCurrentPlayerApi().Replace("/Player", "");
        private string GetAppId() => UnifiedPlugin.GetCurrentAppId();
        private string GetAuthToken() => UnifiedPlugin.GetCurrentAuthToken();

        // Config Entries
        private ConfigEntry<bool> _showLogsConfig;
        private ConfigEntry<bool> _debugMode;
        private ConfigEntry<bool> _showFlowDebugConfig;
        private ManualLogSource _bepinexLogger;
        private bool _isInitializing = true;
        private int _onlineCount = 0;

        public void SetConfig(ConfigEntry<bool> showLogs, ConfigEntry<bool> debugMode, ConfigEntry<bool> showFlowDebug, ManualLogSource bepinexLogger)
        {
            _showLogsConfig = showLogs;
            _debugMode = debugMode;
            _showFlowDebugConfig = showFlowDebug;
            _bepinexLogger = bepinexLogger;
        }

        // State Tracking
        public static bool IsRankedTriggered = false;
        private bool _isQueueing = false;
        private string _webStatus = "IDLE";
        private string _currentQueueId = "";
        private PlayerProfile _userProfile = null;
        private MatchmakingSession _currentSession = null;
        private bool _isHost = false;
        private bool _hasAccepted = false;
        private string _hostRulesetSelection = ""; // "ranked" or "pro_series", empty until selected
        private DateTime? _queueStartTime = null;
        private bool _hostLobbyStarted = false;
        private bool _hostServerWasActive = false;
        private bool _hostCancelSent = false;
#pragma warning disable CS0414
        private bool _matchStatsSubmitted = false;
#pragma warning restore CS0414
        private DateTime? _matchStartTime = null;
        private Dictionary<string, int> _cachedLeaderboardScores = new Dictionary<string, int>();
        private Dictionary<string, int> _cachedLeaderboardScoresVsPar = new Dictionary<string, int>();
        
        // Progressive match tracking - for updating scores after each hole
        private string _currentMatchId = null;
        private Dictionary<string, string> _playerMatchEntryIds = new Dictionary<string, string>(); // player_id -> entry_id
        private Dictionary<string, int> _lastSubmittedScores = new Dictionary<string, int>(); // player_name -> score
        private Dictionary<string, int> _lastSubmittedScoresVsPar = new Dictionary<string, int>(); // player_name -> vs_par
        private bool _matchEntriesCreated = false;
        private bool _isInGameplay = false;
        private Coroutine _monitorCoroutine = null;
        
        // UI Helpers
        private List<string> _debugLogs = new List<string>();
        private List<PlayerData> _queuedPlayers = new List<PlayerData>();
        private Vector2 _logScroll, _playerScroll;
        private Texture2D _profileTexture = null;
        private Texture2D _solidBgTex = null;
        private bool _hasFetchedProfilePic = false;
        private GUIStyle _centerLabelStyle = null;
        private GUIStyle _debugLineStyle = null;

        // Match Result Submission Service
        private MatchResultSubmissionService _matchResultSubmission;

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
            
            // Create match and entries when starting a ranked game (entering course/gameplay scene)
            // Typically scenes like "Forest" "Desert" etc are the actual gameplay scenes
            // Only create if session is 'ready' (all players accepted) to avoid premature creation
            if (IsRankedTriggered && _currentSession != null && _currentSession.status == "ready" && !_matchEntriesCreated && !sceneName.Contains("menu")) {
                if (!sceneName.Contains("drivingrange") && !sceneName.Contains("driving range") && !sceneName.Contains("lobby")) {
                    Log("<color=yellow>[Match] Entering gameplay - creating match records...</color>");
                    _isInGameplay = true;
                    StartCoroutine(CreateMatchAndEntries());
                }
            }
            
            // Capture final leaderboard scores before they're lost when leaving gameplay
            if (_isInGameplay && (sceneName.Contains("drivingrange") || sceneName.Contains("driving range"))) {
                _isInGameplay = false;
                
                // Stop score monitoring immediately to prevent uploading 0's
                if (_monitorCoroutine != null) {
                    StopCoroutine(_monitorCoroutine);
                    _monitorCoroutine = null;
                    Log("<color=cyan>[Match] Score monitoring stopped</color>");
                }
                
                Log("<color=cyan>[Match] Capturing final leaderboard snapshot before leaving gameplay...</color>");
                try {
                    var liveLeaderboard = UnityEngine.Object.FindAnyObjectByType<SBGLLiveLeaderboard.LiveLeaderboardPlugin>(FindObjectsInactive.Include);
                    if (liveLeaderboard != null) {
                        liveLeaderboard.CaptureLeaderboardSnapshot();
                        Log("<color=green>[Match] ✓ Leaderboard snapshot captured</color>");
                    }
                } catch (System.Exception ex) {
                    Log($"<color=yellow>[Match] Could not capture snapshot: {ex.Message}</color>");
                }
            }
            
            // Skip match finalization in driving range - scores are already updated after each hole during gameplay
            if (sceneName.Contains("drivingrange") || sceneName.Contains("driving range")) {
                if (IsRankedTriggered && _currentSession != null && _matchEntriesCreated) {
                    Log("<color=green>[Match Stats] Returned to Driving Range - match already finalized. Skipping redundant update.</color>");
                    _matchStatsSubmitted = true;
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

        private void ResetPluginState() {
            // Stop monitoring coroutine if running
            if (_monitorCoroutine != null) {
                StopCoroutine(_monitorCoroutine);
                _monitorCoroutine = null;
            }
            
            IsRankedTriggered = false;
            _isQueueing = false;
            _webStatus = "IDLE";
            _currentQueueId = "";
            _currentSession = null;
            _isHost = false;
            _hasAccepted = false;
            _hostRulesetSelection = "";
            _queueStartTime = null;
            _hostLobbyStarted = false;
            _hostServerWasActive = false;
            _hostCancelSent = false;
            _matchStatsSubmitted = false;
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
            
            // Reset progressive match tracking
            _currentMatchId = null;
            _playerMatchEntryIds.Clear();
            _lastSubmittedScores.Clear();
            _lastSubmittedScoresVsPar.Clear();
            _matchEntriesCreated = false;
            _isInGameplay = false;
            _cachedLeaderboardScores.Clear();
            _cachedLeaderboardScoresVsPar.Clear();
        }

        private void OnDestroy() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
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
       private IEnumerator BackgroundSyncLoop() {
            int retryCount = 0;
            const int maxRetries = 10;
            
            while (true) {
                _syncTickCount++;

                // First, ensure we have the profile resolved from the shared profile or API
                if (_userProfile == null) {
                    // Try to get from shared profile first (UnifiedPlugin resolved it)
                    var sharedProfile = UnifiedPlugin.GetPlayerProfile();
                    if (sharedProfile != null && UnifiedPlugin.IsPlayerProfileResolved()) {
                        _userProfile = new PlayerProfile {
                            id = sharedProfile.ID,
                            display_name = sharedProfile.DisplayName,
                            current_mmr = sharedProfile.CurrentMMR,
                            region = sharedProfile.Region,
                            state_province = "" // Not available in shared profile
                        };
                        Log($"✓ Using shared profile: {_userProfile.display_name} ({_userProfile.current_mmr} MMR)");
                        retryCount = 0; // Reset retry count on success
                    } else {
                        // Fallback: try to resolve by Steam name if available
                        string steamName = "Player";
                        try {
                            if (FacepunchLib.SteamClient.IsValid) steamName = FacepunchLib.SteamClient.Name;
                        } catch { }

                        if (!string.IsNullOrEmpty(steamName) && steamName != "Player") {
                            retryCount = 0; // Reset on new attempt
                            yield return ResolveProfile(steamName);
                        } else {
                            retryCount++;
                            if (retryCount == 1) Log($"<color=yellow>Waiting for Steam initialization... (Attempt {retryCount}/{maxRetries})</color>");
                            if (retryCount % 3 == 0) Log($"<color=yellow>Still waiting for Steam... (Attempt {retryCount}/{maxRetries})</color>");
                            if (retryCount >= maxRetries) {
                                Log("<color=orange>Steam initialization timeout. Using API fallback...</color>");
                                yield break; // Exit to prevent infinite waiting
                            }
                        }
                    }
                }

                // Only proceed with sync if the profile is successfully loaded
                if (_userProfile != null) {
                    // Skip queue polling if we're in an active match (ranked gameplay)
                    if (!IsRankedTriggered) {
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

                    // Query already filters queued+matched, so list length is the online value for this context.
                    _onlineCount = queueEntries.Count;

                    List<PlayerData> queuedPlayers = new List<PlayerData>(queueEntries.Count);
                    JObject myEntry = null;
                    string myStatus = null;

                    foreach (JObject entry in queueEntries) {
                        string status = (string)entry["status"];

                        if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase)) {
                            queuedPlayers.Add(new PlayerData {
                                name = (string)entry["display_name"] ?? (string)entry["user_id"] ?? "Unknown",
                                mmr = entry["mmr_snapshot"]?.ToString() ?? "0"
                            });
                        }

                        if (_userProfile != null && myEntry == null &&
                            string.Equals((string)entry["player_id"], _userProfile.id, StringComparison.Ordinal)) {
                            myEntry = entry;
                            myStatus = status;
                        }
                    }

                    _queuedPlayers = queuedPlayers;

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

        private IEnumerator MatchmakingLoop() {
            if (_userProfile == null) { Log("<color=red>No profile loaded.</color>"); yield break; }

            _isQueueing = true;
            _webStatus = "JOINING...";
            _queueStartTime = DateTime.Now;

            // Inside MatchmakingLoop()
            string json = "{" + 
                $"\"player_id\":\"{_userProfile.id}\"," +
                $"\"user_id\":\"{_userProfile.id}\"," + 
                $"\"display_name\":\"{_userProfile.display_name}\"," +
                $"\"mmr_snapshot\":{_userProfile.current_mmr}," +
                $"\"region\":\"{_userProfile.region}\"," +
                $"\"state_province\":\"{_userProfile.state_province ?? ""}\"," + // Added this
                $"\"status\":\"queued\"," +
                $"\"queued_at\":\"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\"" + 
            "}";
            
            yield return CallAPI("/MatchmakingQueue", "POST", json, (res) => {
                JObject queueResponse = ParseApiSingleObject(res);
                _currentQueueId = (string)queueResponse?["id"] ?? _currentQueueId;
                _webStatus = "QUEUED";
                Log("Queue request sent.");
            });
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
                        ParseSession(activeSession);
                        _webStatus = "MATCH FOUND: PENDING";
                        Log("Match Found! Accept needed.");
                    }
                } else {
                    Log($"<color=red>[Sync] CheckForMatch failed: {req.result} - {req.error}</color>");
                }
            }
        }

        private IEnumerator PollSessionStatus() {
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

        private void ParseSession(JObject sessionData) {
            Log("[ParseSession] Entering ParseSession method");
            if (sessionData == null) {
                Log("[ParseSession] sessionData is null, returning");
                return;
            }

            try
            {
                Log("[ParseSession] Attempting to parse session data");
                _currentSession = new MatchmakingSession {
                    id = (string)sessionData["id"],
                    lobby_name = (string)sessionData["lobby_name"],
                    lobby_password = (string)sessionData["lobby_password"],
                    host_player_id = (string)sessionData["host_player_id"],
                    status = (string)sessionData["status"],
                    steam_lobby_link = (string)sessionData["steam_lobby_link"],
                    player_ids = sessionData["player_ids"]?.ToObject<List<string>>() ?? new List<string>(),
                    accepted_player_ids = sessionData["accepted_player_ids"]?.ToObject<List<string>>() ?? new List<string>(),
                    match_type = (string)sessionData["match_type"] ?? "",
                    selected_course = (string)sessionData["selected_course"] ?? "",
                    season = sessionData["season"]?.ToObject<int>() ?? 1
                };
                Log("[ParseSession] Session data parsed successfully");
            }
            catch (System.Exception ex)
            {
                Log($"<color=red>[Session] Error parsing session data: {ex.Message}</color>");
                Log($"<color=red>[Session] StackTrace: {ex.StackTrace}</color>");
                _currentSession = null;
                return;
            }

            if (!string.IsNullOrEmpty(_currentSession.steam_lobby_link)) {
                Log("Session includes steam_lobby_link from API.");
            }

            Log($"<color=cyan>[Session] Parsed {_currentSession.player_ids.Count} total players, {_currentSession.accepted_player_ids.Count} accepted</color>");

            // Parse match configuration if present
            if (!string.IsNullOrEmpty(_currentSession.match_type))
            {
                Log($"<color=cyan>[Session] Match Type: {_currentSession.match_type}, Course: {_currentSession.selected_course}, Season: {_currentSession.season}</color>");
            }

            // Logic Check: If status is 'ready', it means everyone accepted.
            // If it's 'pending_accept', the website waits.
            _isHost = (_currentSession.host_player_id == _userProfile.id);
            
            if (_currentSession.status == "ready") {
                Log("All players accepted. Match is READY.");
                
                // Store match configuration in PlayerPrefs for Harmony patches to use
                // Only if fields are present (backwards compatibility with older API)
                if (!string.IsNullOrEmpty(_currentSession.match_type) && !string.IsNullOrEmpty(_currentSession.selected_course))
                {
                    try
                    {
                        StoreMatchConfigurationInPlayerPrefs(_currentSession);
                    }
                    catch (System.Exception ex)
                    {
                        Log($"<color=yellow>[Session] Failed to store match configuration: {ex.Message}</color>");
                        // Continue anyway - don't let this block the match flow
                    }
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

                if (string.IsNullOrEmpty(session.match_type) || string.IsNullOrEmpty(session.selected_course))
                {
                    Log("<color=yellow>[Config] Match type or course is empty, skipping configuration storage</color>");
                    return;
                }

                // Validate course is approved before storing
                try
                {
                    RuleSetManager.SetLogger(_bepinexLogger);
                    bool isCourseValid = RuleSetManager.ValidateCourseForRanked(session.selected_course);
                    
                    string courseToStore = isCourseValid ? session.selected_course : MapPoolConfig.GetRandomApprovedCourse().Name;

                    // Store configuration in PlayerPrefs for patches to access
                    PlayerPrefs.SetString("MatchType", session.match_type);
                    PlayerPrefs.SetString("SelectedCourse", courseToStore);
                    PlayerPrefs.SetInt("Season", session.season);
                    PlayerPrefs.Save();

                    Log($"<color=cyan>[Config] Stored match configuration: Type={session.match_type}, Course={courseToStore}, Season={session.season}</color>");

                    // Log the configuration for audit trail
                    RuleSetManager.LogMatchConfiguration(session.match_type, courseToStore, session.season);
                }
                catch (System.Exception ruleEx)
                {
                    Log($"<color=yellow>[Config] Error during rule validation, storing raw config: {ruleEx.Message}</color>");
                    
                    // Store raw config anyway as fallback
                    PlayerPrefs.SetString("MatchType", session.match_type);
                    PlayerPrefs.SetString("SelectedCourse", session.selected_course);
                    PlayerPrefs.SetInt("Season", session.season);
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
            if (string.IsNullOrEmpty(_hostRulesetSelection))
                _hostRulesetSelection = string.IsNullOrEmpty(rulesetFromPrefs) ? "ranked" : rulesetFromPrefs;
            
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
            if (_userProfile == null || _currentSession == null) {
                Log("<color=red>[Match Creation] Failed: Missing profile or session</color>");
                yield break;
            }

            Log($"<color=cyan>[Match Creation] Starting new match for session {_currentSession.id}</color>");
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

            // Step 1: Create Match record
            string matchId = null;
            yield return SubmitMatchEntry(CollectMatchStats(0f), (id) => matchId = id);

            if (string.IsNullOrEmpty(matchId)) {
                Log("<color=red>[Match Creation] Failed to create Match record</color>");
                yield break;
            }

            _currentMatchId = matchId;
            Log($"<color=green>[Match Creation] ✓ Match created: {matchId}</color>");

            // Step 2: Create initial MatchEntry records for all players
            _playerMatchEntryIds.Clear();
            _lastSubmittedScores.Clear();
            _lastSubmittedScoresVsPar.Clear();

            foreach (string playerId in _currentSession.player_ids) {
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
                    if (_cachedLeaderboardScores.TryGetValue(playerName, out int score)) {
                        gamePoints = score;
                        _cachedLeaderboardScoresVsPar.TryGetValue(playerName, out scoreVsPar);
                    }
                    
                    _lastSubmittedScores[playerName] = gamePoints;
                    _lastSubmittedScoresVsPar[playerName] = scoreVsPar;
                }

                // Get starting position from leaderboard
                int startingPosition = GetPlayerFinishPosition(playerName, startingLeaderboard);

                // Create MatchEntry with MMR snapshot and adjusted score
                int adjustedScore = gamePoints + (scoreVsPar * -10); // Same calculation as LiveLeaderboard
                string mmrField = !string.IsNullOrEmpty(preMatchMmr) ? $",\"pre_match_mmr\":{preMatchMmr}" : "";
                string posField = startingPosition > 0 ? $",\"finish_position\":{startingPosition}" : "";
                string json = "{" +
                    $"\"match_id\":\"{matchId}\"," +
                    $"\"player_id\":\"{playerId}\"," +
                    $"\"player_name\":\"{playerName ?? "Unknown"}\"," +
                    $"\"game_points\":{gamePoints}," +
                    $"\"over_under\":{scoreVsPar}," +
                    $"\"score_vs_par\":{scoreVsPar}," +
                    $"\"adjusted_match_score\":{adjustedScore}" +
                    mmrField +
                    posField +
                    $",\"notes\":\"Progressive match tracking - created at round start\"" +
                "}";

                string entryId = null;
                yield return CallAPI("/MatchEntry", "POST", json, (res) => {
                    try {
                        JObject response = ParseApiSingleObject(res);
                        if (response != null) {
                            entryId = (string)response["id"];
                            _playerMatchEntryIds[playerId] = entryId;
                            Log($"<color=green>[Match Creation] ✓ MatchEntry created for {playerName}: {entryId}</color>");
                        }
                    } catch (System.Exception ex) {
                        Log($"<color=yellow>[Match Creation] Could not parse MatchEntry: {ex.Message}</color>");
                    }
                });
            }

            _matchEntriesCreated = true;
            Log($"<color=green>[Match Creation] ✓ Match and entries initialized. Starting score monitoring...</color>");

            // Start monitoring for score changes during gameplay
            _monitorCoroutine = StartCoroutine(MonitorAndUpdateScores());
        }

        private IEnumerator MonitorAndUpdateScores() {
            Log($"<color=cyan>[Match Monitor] Starting score monitoring for gameplay</color>");
            
            while (_isInGameplay && _currentMatchId != null) {
                yield return new WaitForSeconds(2f); // Check every 2 seconds

                // Refresh leaderboard data
                var liveLeaderboard = UnityEngine.Object.FindAnyObjectByType<SBGLLiveLeaderboard.LiveLeaderboardPlugin>(FindObjectsInactive.Include);
                if (liveLeaderboard == null) continue;

                var allLeaderboardPlayers = liveLeaderboard.GetCurrentLeaderboard();
                if (allLeaderboardPlayers == null || allLeaderboardPlayers.Count == 0) continue;
                
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
                        // Find the player ID and entry ID for this player
                        string playerId = null;
                        string entryId = null;

                        if (player.Name == _userProfile.display_name) {
                            playerId = _userProfile.id;
                        } else {
                            // Try to find in player match entry IDs
                            foreach (var pid in _currentSession.player_ids) {
                                if (_playerMatchEntryIds.ContainsKey(pid)) {
                                    // For opponents, we'd need name mapping - skip for now
                                    continue;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(playerId) && _playerMatchEntryIds.TryGetValue(playerId, out entryId)) {
                            int finishPosition = GetPlayerFinishPosition(player.Name, allLeaderboardPlayers);
                            yield return UpdateMatchEntry(entryId, playerId, player.Name, newGamePoints, newScoreVsPar, finishPosition);
                            _lastSubmittedScores[player.Name] = newGamePoints;
                            _lastSubmittedScoresVsPar[player.Name] = newScoreVsPar;
                        }
                    }
                }
            }

            Log($"<color=cyan>[Match Monitor] Score monitoring ended</color>");
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

            for (int i = 0; i < finalLeaderboard.Count; i++) {
                if (finalLeaderboard[i] != null && finalLeaderboard[i].Name == playerName) {
                    return i + 1; // Return 1-based position
                }
            }

            return 0; // Not found
        }

        private IEnumerator FinalizeMatchStats() {
            // Wait for leaderboard to finalize
            yield return new WaitForSeconds(1.5f);

            Log($"<color=cyan>[Match Finalize] Performing final score update...</color>");

            // Get final leaderboard data (uses snapshot captured when leaving gameplay)
            var liveLeaderboard = UnityEngine.Object.FindAnyObjectByType<SBGLLiveLeaderboard.LiveLeaderboardPlugin>(FindObjectsInactive.Include);
            if (liveLeaderboard == null) {
                Log($"<color=yellow>[Match Finalize] LiveLeaderboard not found</color>");
                _matchStatsSubmitted = true;
                yield break;
            }

            var allLeaderboardPlayers = liveLeaderboard.GetFinalLeaderboardSnapshot();
            if (allLeaderboardPlayers == null || allLeaderboardPlayers.Count == 0) {
                Log($"<color=yellow>[Match Finalize] No leaderboard data available</color>");
                _matchStatsSubmitted = true;
                yield break;
            }

            Log($"<color=cyan>[Match Finalize] Using final snapshot with {allLeaderboardPlayers.Count} players</color>");

            foreach (var player in allLeaderboardPlayers) {
                if (player == null) continue;

                int finalGamePoints = player.BaseScore;
                int finalScoreVsPar = 0;
                int finishPosition = GetPlayerFinishPosition(player.Name, allLeaderboardPlayers);

                if (!string.IsNullOrEmpty(player.RawStrokes)) {
                    string strokeStr = player.RawStrokes.Replace("±", "").Trim();
                    int.TryParse(strokeStr, out finalScoreVsPar);
                }

                // Find entry ID for this player
                string playerId = null;
                string entryId = null;

                if (player.Name == _userProfile.display_name) {
                    playerId = _userProfile.id;
                }

                if (!string.IsNullOrEmpty(playerId) && _playerMatchEntryIds.TryGetValue(playerId, out entryId)) {
                    // Perform final update
                    Log($"<color=cyan>[Match Finalize] Final update for {player.Name}: {finalGamePoints} pts, {finalScoreVsPar} vs par, Position: {finishPosition}</color>");
                    yield return UpdateMatchEntry(entryId, playerId, player.Name, finalGamePoints, finalScoreVsPar, finishPosition);
                }
            }

            _matchStatsSubmitted = true;
            Log($"<color=green>[Match Finalize] ✓ Match stats finalized</color>");
        }

        private IEnumerator SubmitMatchStats() {
            if (_userProfile == null || _currentSession == null) {
                Log("<color=red>[Match Stats] Failed: Missing profile or session</color>");
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
                // Collect basic match metadata
                var stats = new MatchStats {
                    matchmaking_session_id = _currentSession.id,
                    match_id = _currentSession.id,
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

        private IEnumerator SubmitMatchEntry(MatchStats stats, System.Action<string> onMatchIdReceived) {
            if (stats == null || _currentSession == null) yield break;

            // Map to Match schema - these are the fields the API expects
            // TODO: Get the actual season_id from the current season configuration
            string seasonId = "69de6bf4fb103cb0d5eb00c5"; // Placeholder - should be dynamic
            
            string json = "{" +
                $"\"matchmaking_session_id\":\"{_currentSession.id}\"," +
                $"\"season_id\":\"{seasonId}\"," +
                $"\"match_date\":\"{stats.match_date}\"," +
                $"\"match_type\":\"mmr\"," +
                $"\"player_count\":2," +
                $"\"status\":\"Pending\"," +
                $"\"submitted_by_name\":\"{stats.player_name}\"," +
                $"\"mode\":\"\"," +
                $"\"notes\":\"Auto-submitted via SBGL Unified Mod\"" +
            "}";

            Log($"<color=cyan>[Match Stats] Submitting Match entry to API</color>");
            Log($"<color=cyan>[Match Stats] Full URL: {GetBaseApiUrl()}/Match</color>");
            Log($"<color=cyan>[Match Stats] Payload: {json}</color>");

            yield return CallAPI("/Match", "POST", json, (res) => {
                JObject response = ParseApiSingleObject(res);
                if (response != null) {
                    string entryId = (string)response["id"] ?? "unknown";
                    Log($"<color=green>[Match Stats] ✓ Match entry created (ID: {entryId})</color>");
                    onMatchIdReceived?.Invoke(entryId);
                } else {
                    Log("<color=yellow>[Match Stats] Response received but could not parse ID</color>");
                }
            });
        }

        private IEnumerator SubmitMatchEntryForPlayer(string matchId, string playerId) {
            // Try to get leaderboard data for this player using cached scores
            int gamePoints = 0;
            int scoreVsPar = 0;
            string playerDisplayName = null;
            
            // First: determine the player's display name
            if (playerId == _userProfile.id) {
                playerDisplayName = _userProfile.display_name;
                Log($"<color=cyan>[Match Stats] Current user: {playerDisplayName}</color>");
            } else {
                // For other players, fetch their profile to get their display_name
                Log($"<color=cyan>[Match Stats] Fetching profile for opponent {playerId}</color>");
                yield return CallAPI($"/Player/{playerId}", "GET", "", (res) => {
                    try {
                        JObject profile = ParseApiSingleObject(res);
                        if (profile != null) {
                            playerDisplayName = (string)profile["display_name"];
                            Log($"<color=cyan>[Match Stats] Opponent display name: {playerDisplayName}</color>");
                        }
                    } catch (System.Exception ex) {
                        Log($"<color=yellow>[Match Stats] Error parsing opponent profile: {ex.Message}</color>");
                    }
                });
            }
            
            // Second: look up scores in cache by display name (with retry)
            if (!string.IsNullOrEmpty(playerDisplayName)) {
                int retries = 0;
                while (retries < 5 && (gamePoints == 0 && scoreVsPar == 0)) {
                    if (_cachedLeaderboardScores.TryGetValue(playerDisplayName, out int cachedScore)) {
                        gamePoints = cachedScore;
                        if (_cachedLeaderboardScoresVsPar.TryGetValue(playerDisplayName, out int cachedVsPar)) {
                            scoreVsPar = cachedVsPar;
                        }
                        Log($"<color=green>[Match Stats] ✓ Found cached scores for {playerDisplayName}: {gamePoints} pts, {scoreVsPar} vs par</color>");
                        break;
                    } else {
                        retries++;
                        if (retries < 5) {
                            Log($"<color=yellow>[Match Stats] Scores not cached yet for {playerDisplayName}, retry {retries}/5...</color>");
                            yield return new WaitForSeconds(0.5f);
                        }
                    }
                }
                
                if (gamePoints == 0 && scoreVsPar == 0) {
                    Log($"<color=yellow>[Match Stats] ⚠ No leaderboard data found for {playerDisplayName} after retries</color>");
                }
            } else {
                Log($"<color=yellow>[Match Stats] ⚠ Could not determine player display name for {playerId}</color>");
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
                // Only show menu UI in menu scenes
                // Match configuration display is handled by RuleSetDisplayManager
                if (!SceneManager.GetActiveScene().name.ToLower().Contains("menu")) return;

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
                float uiHeight = (_showLogsConfig?.Value ?? false) ? 450f : 350f; 
                if ((_debugMode?.Value ?? false)) uiHeight += 30f;
                if ((_showFlowDebugConfig?.Value ?? false)) uiHeight += 145f;

                GUI.DrawTexture(new Rect(rightX, 20, uiWidth, uiHeight), _solidBgTex);
                GUI.Box(new Rect(rightX, 20, uiWidth, uiHeight), "<b>SBGL LEAGUE ASSISTANT</b>");

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

            float offset = _debugMode.Value ? 30f : 0f;
            GUI.Box(new Rect(rightX + 10, 110 + offset, uiWidth - 20, 100), ""); 

            if (_userProfile != null) {
                if (_profileTexture) GUI.DrawTexture(new Rect(rightX + 20, 120 + offset, 40, 40), _profileTexture);
                GUI.Label(new Rect(rightX + 70, 120 + offset, 240, 20), $"User: <b>{_userProfile.display_name}</b>");
                GUI.Label(new Rect(rightX + 70, 135 + offset, 240, 20), $"<color=#FFA500><size=10>{_webStatus}</size></color>");

                // --- STATS ROW (Mimicking Website) ---
                float statWidth = (uiWidth - 40) / 3;
                float statsY = 160 + offset;

                // Column 1: TIME
                GUI.Label(new Rect(rightX + 20, statsY, statWidth, 20), "<color=#FFFFFF><size=10><b>TIME</b></size></color>", _centerLabelStyle);
                string timeStr = "00:00";
                if (_isQueueing && _queueStartTime.HasValue) {
                    TimeSpan elapsed = DateTime.UtcNow - _queueStartTime.Value.ToUniversalTime();
                    timeStr = elapsed.TotalSeconds < 0 ? "00:00" : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
                }
                GUI.Label(new Rect(rightX + 20, statsY + 15, statWidth, 25), $"<color=#00FFCC><size=16><b>{timeStr}</b></size></color>", _centerLabelStyle);

                // Column 2: ONLINE
                GUI.Label(new Rect(rightX + 20 + statWidth, statsY, statWidth, 20), "<color=#FFFFFF><size=10><b>ONLINE</b></size></color>", _centerLabelStyle);
                GUI.Label(new Rect(rightX + 20 + statWidth, statsY + 15, statWidth, 25), $"<color=#FFFFFF><size=16><b>{_onlineCount}</b></size></color>", _centerLabelStyle);

                // Column 3: YOUR MMR
                GUI.Label(new Rect(rightX + 20 + (statWidth * 2), statsY, statWidth, 20), "<color=#FFFFFF><size=10><b>YOUR MMR</b></size></color>", _centerLabelStyle);
                GUI.Label(new Rect(rightX + 20 + (statWidth * 2), statsY + 15, statWidth, 25), $"<color=#FFFFFF><size=16><b>{_userProfile.current_mmr}</b></size></color>", _centerLabelStyle);
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

            // --- ACTIVE QUEUE LIST ---
            GUI.Label(new Rect(rightX + 15, contentY, uiWidth, 25), "<b>ACTIVE QUEUE:</b>");
            _playerScroll = GUI.BeginScrollView(new Rect(rightX + 10, contentY + 25, uiWidth - 20, 70), _playerScroll, new Rect(0,0, uiWidth - 40, _queuedPlayers.Count * 22));
            for (int i = 0; i < _queuedPlayers.Count; i++) {
                GUI.Label(new Rect(5, i * 22, 300, 22), $"• {_queuedPlayers[i].name} <color=#4CAF50>({_queuedPlayers[i].mmr} MMR)</color>");
            }
            GUI.EndScrollView();

            if (_showLogsConfig.Value) {
                float logsY = contentY + 100;
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
            req.SetRequestHeader("Authorization", "Bearer " + GetAuthToken());
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

        private string Truncate(string value, int maxLength) {
            if (string.IsNullOrEmpty(value)) return "-";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        public class PlayerProfile 
        { 
            public string id, display_name, region, state_province; // Added state_province
            public float current_mmr; 
        }
        public class MatchmakingSession { 
            public string id, lobby_name, lobby_password, host_player_id, status; 
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
        public struct PlayerData { public string name, mmr; }
    }

    [HarmonyPatch(typeof(BNetworkManager), nameof(BNetworkManager.LobbyName), MethodType.Setter)]
    public static class LobbyPatch { 
        public static void Prefix(ref string value) { 
            if (SBGLPlugin.IsRankedTriggered) value = PlayerPrefs.GetString("LobbyName"); 
        } 
    }
}