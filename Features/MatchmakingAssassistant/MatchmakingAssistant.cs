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
        private DateTime? _queueStartTime = null;
        private bool _hostLobbyStarted = false;
        private bool _hostServerWasActive = false;
        private bool _hostCancelSent = false;
        
        // UI Helpers
        private List<string> _debugLogs = new List<string>();
        private List<PlayerData> _queuedPlayers = new List<PlayerData>();
        private Vector2 _logScroll, _playerScroll;
        private Texture2D _profileTexture = null;
        private Texture2D _solidBgTex = null;
        private bool _hasFetchedProfilePic = false;
        private GUIStyle _centerLabelStyle = null;
        private GUIStyle _debugLineStyle = null;

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
            if (scene.name.ToLower().Contains("menu")) {
                // If host returns to menu after having an active hosted lobby, cancel the web session.
                if (_isHost && _hostLobbyStarted && _hostServerWasActive && _currentSession != null) {
                    StartCoroutine(CancelSessionAsHost("host_returned_to_menu"));
                }
                ResetPluginState();
                Log("Returned to Menu: State Reset.");
            }
        }

        private void ResetPluginState() {
            IsRankedTriggered = false;
            _isQueueing = false;
            _webStatus = "IDLE";
            _currentQueueId = "";
            _currentSession = null;
            _isHost = false;
            _hasAccepted = false;
            _queueStartTime = null;
            _hostLobbyStarted = false;
            _hostServerWasActive = false;
            _hostCancelSent = false;

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
                    yield return RefreshPlayerList();
                    
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

                    _currentSession.status = (string)session["status"] ?? _currentSession.status;
                    string latestSteamLobbyLink = (string)session["steam_lobby_link"];
                    if (!string.IsNullOrEmpty(latestSteamLobbyLink) && latestSteamLobbyLink != _currentSession.steam_lobby_link) {
                        _currentSession.steam_lobby_link = latestSteamLobbyLink;
                        Log("Sync: steam_lobby_link received/updated from API.");
                    }
                    
                    if (_currentSession.status == "ready") {
                        _webStatus = _isHost ? "READY: HOST" : "READY: JOIN";
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
            if (sessionData == null) return;

            _currentSession = new MatchmakingSession {
                id = (string)sessionData["id"],
                lobby_name = (string)sessionData["lobby_name"],
                lobby_password = (string)sessionData["lobby_password"],
                host_player_id = (string)sessionData["host_player_id"],
                status = (string)sessionData["status"],
                steam_lobby_link = (string)sessionData["steam_lobby_link"]
            };

            if (!string.IsNullOrEmpty(_currentSession.steam_lobby_link)) {
                Log("Session includes steam_lobby_link from API.");
            }

            // Logic Check: If status is 'ready', it means everyone accepted.
            // If it's 'pending_accept', the website waits.
            _isHost = (_currentSession.host_player_id == _userProfile.id);
            
            if (_currentSession.status == "ready") {
                Log("All players accepted. Match is READY.");
            }
        }

        private IEnumerator AcceptMatch() {
            if (_currentSession == null) yield break;
            _hasAccepted = true;
            
            // 1. Tell the server we accepted
            string acceptJson = "{\"accepted_player_ids\":[\"" + _userProfile.id + "\"]}";
            
            yield return CallAPI($"/MatchmakingSession/{_currentSession.id}", "PUT", acceptJson, (res) => {
                Log("Match Accepted.");
                
                // 2. Check if we are the last one needed to make the match 'ready'
                // We assume a 1v1 based on the session logic, but we can check the player_ids count
                // If the website requires an explicit 'ready' status, we send it now.
                StartCoroutine(TransitionToReady());
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
            // We give the server a split second to process the previous PUT
            yield return _readyTransitionDelay;

            // According to the website's behavior in your HAR file:
            // When the last player accepts, we push the 'ready' status.
            string readyJson = "{\"status\":\"ready\"}";
            
            yield return CallAPI($"/MatchmakingSession/{_currentSession.id}", "PUT", readyJson, (res) => {
                Log("<color=green>Session marked as READY.</color>");
                _currentSession.status = "ready"; // Local update for immediate UI response
            });
        }

        private void InitiateHostSequence() {
            if (_currentSession == null) return;
            var mainMenu = UnityEngine.Object.FindAnyObjectByType<MainMenu>();
            if (mainMenu != null) {
                IsRankedTriggered = true;
                _hostLobbyStarted = true;
                _hostServerWasActive = false;
                _hostCancelSent = false;
                PlayerPrefs.SetString("LobbyName", _currentSession.lobby_name);
                PlayerPrefs.SetString("LobbyPassword", _currentSession.lobby_password);
                PlayerPrefs.Save();
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

        
        // ==========================================
        // UI RENDERING
        // ==========================================
        private void OnGUI() {
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