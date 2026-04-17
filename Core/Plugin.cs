using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System;
using SBGL.UnifiedMod.Features.CompetitivePluginCheck;
using SBGL.UnifiedMod.Utils;
using SBGLeagueAutomation;
using SBGLLiveLeaderboard;
//using SBGL.UnifiedMod.Features.Leaderboard;

namespace SBGL.UnifiedMod.Core
{
    public class SharedPlayerProfile
    {
        public string ID { get; set; }
        public string DisplayName { get; set; }
        public float CurrentMMR { get; set; }
        public string Region { get; set; }
        public string ProfilePicUrl { get; set; }
        public bool IsResolved { get; set; }
    }
    [BepInPlugin("com.sbgl.unified", "SBGL Unified Mod", "1.0.0")]
    public class UnifiedPlugin : BaseUnityPlugin
    {
        // ==========================================
        // SINGLETON INSTANCE
        // ==========================================
        public static UnifiedPlugin Instance { get; private set; }

        // ==========================================
        // STAFF LIST
        // ==========================================
        // The raw link to your Gist (important: use the /raw/ path)
        private const string GIST_URL = "https://gist.githubusercontent.com/Kingcox22/f1b51955d78305177533759cc4ae6024/raw/86f00f088db32e68e9cd5429b3695db22b435ba0/staff.txt";

        // Change from static readonly to just private
        private HashSet<string> _dynamicStaffList = new HashSet<string>();

        // ==========================================
        // API CONFIGURATION
        // ==========================================
        // Production API
        private const string PROD_PLAYER_API = "https://sbgleague.com/api/apps/69b0f4aba3975f2440fbf070/entities/Player";
        private const string PROD_APP_ID = "69b0f4aba3975f2440fbf070";
        private const string PROD_AUTH_TOKEN = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJraW5nY294MjJAZ21haWwuY29tIiwiZXhwIjoxNzgyNzg5NDU0LCJpYXQiOjE3NzUwMTM0NTR9.2pCmAKXH98n3fvUaheN-e6kx3BoJflStfUb-Kq-i-gU";
        
        // PreProd API
        private const string PREPROD_PLAYER_API = "https://sbg-league-manager-preprod.base44.app/api/apps/69d5bc0bb18e58e435ff4e3f/entities/Player";
        private const string PREPROD_APP_ID = "69d5bc0bb18e58e435ff4e3f";
        private const string PREPROD_AUTH_TOKEN = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJraW5nY294MjJAZ21haWwuY29tIiwiZXhwIjoxNzgzNjU2MjQ0LCJpYXQiOjE3NzU4ODAyNDR9.zApbrIgaGMF_UeB1X3ugPbxVUtdcBvBA8YU_1fwUqvk";
        
        // Dynamic API properties
        private string _currentPlayerApi;
        private string _currentAppId;
        private string _currentAuthToken;
        private bool _isStaffUser = false;       


        // ==========================================
        // SHARED PLAYER PROFILE
        // ==========================================
        private static SharedPlayerProfile _sharedProfile = new SharedPlayerProfile();
    private static Texture2D _profilePicture = null;
        // ==========================================
        // API ACCESSORS (For Staff Environment Switching)
        // ==========================================
        public static string GetCurrentPlayerApi() => Instance != null ? Instance._currentPlayerApi : PROD_PLAYER_API;
        public static string GetCurrentAppId() => Instance != null ? Instance._currentAppId : PROD_APP_ID;
        public static string GetCurrentAuthToken() => Instance != null ? Instance._currentAuthToken : PROD_AUTH_TOKEN;
        public static bool IsStaffUser() => Instance != null && Instance._isStaffUser;
        public static SharedPlayerProfile GetPlayerProfile() => _sharedProfile;
        public static bool IsPlayerProfileResolved() => _sharedProfile.IsResolved;
        public static Texture2D GetProfilePicture() => _profilePicture;
        // ==========================================
        // COMPETITIVE PLUGIN CHECK CONFIG
        // ==========================================
        public ConfigEntry<float> CompCheck_X;
        public ConfigEntry<float> CompCheck_Y;
        public ConfigEntry<float> CompCheck_Width;
        public ConfigEntry<float> CompCheck_Alpha;
        public ConfigEntry<bool> CompCheck_ShowModList;
        public ConfigEntry<bool> CompCheck_ShowLobbyMods;
        public ConfigEntry<bool> CompCheck_ShowDebugWindow;
        public ConfigEntry<float> CompCheck_UpdateInterval;
        public ConfigEntry<string> CompCheck_PlayerId;

        // ==========================================
        // MATCHMAKING ASSISTANT CONFIG
        // ==========================================
        public ConfigEntry<bool> MM_ShowSystemLogs;
        public ConfigEntry<bool> MM_EnableEmulation;
        public ConfigEntry<bool> MM_ShowFlowDebug;

        // ==========================================
        // LIVE LEADERBOARD CONFIG
        // ==========================================
        public ConfigEntry<float> LL_Width;
        public ConfigEntry<float> LL_MaxHeight;
        public ConfigEntry<float> LL_PosX;
        public ConfigEntry<float> LL_PosY;
        public ConfigEntry<float> LL_Opacity;
        public ConfigEntry<int> LL_MaxPlayers;

        // ==========================================
        // API ENVIRONMENT CONFIG (Staff Only)
        // ==========================================
        public ConfigEntry<string> API_Environment;

        // ==========================================
        // API CONFIGURATION CHANGE EVENT
        // ==========================================
        public delegate void OnApiConfigChanged();
        public static event OnApiConfigChanged ApiConfigChanged;

        void Awake()
        {
            // Set singleton instance
            Instance = this;
            
            // === COMPETITIVE PLUGIN CHECK CONFIG ===
            CompCheck_X = Config.Bind("CompetitiveCheck.UI Position", "X Offset", 20f, 
                new ConfigDescription("Horizontal position", new AcceptableValueRange<float>(0f, 4000f)));
            CompCheck_Y = Config.Bind("CompetitiveCheck.UI Position", "Y Offset", 100f, 
                new ConfigDescription("Vertical position", new AcceptableValueRange<float>(0f, 4000f)));
            CompCheck_Width = Config.Bind("CompetitiveCheck.UI Size", "Width", 200f, 
                new ConfigDescription("Panel width", new AcceptableValueRange<float>(150f, 800f)));
            CompCheck_Alpha = Config.Bind("CompetitiveCheck.UI Appearance", "Opacity", 0.85f, 
                new ConfigDescription("Panel transparency", new AcceptableValueRange<float>(0f, 1f)));
            CompCheck_ShowModList = Config.Bind("CompetitiveCheck.UI Appearance", "Show My Mods", false, "Display local mod list");
            CompCheck_ShowLobbyMods = Config.Bind("CompetitiveCheck.UI Appearance", "Show Lobby Mods", true, "Display remote mod lists");
            CompCheck_ShowDebugWindow = Config.Bind("CompetitiveCheck.UI Appearance", "Show Debug Window", false, "Display debug information");
            CompCheck_UpdateInterval = Config.Bind("CompetitiveCheck.Logic", "Update Interval", 5f, 
                new ConfigDescription("Update frequency in minutes", new AcceptableValueRange<float>(1f, 60f)));
            CompCheck_PlayerId = Config.Bind("CompetitiveCheck.League", "Player ID", "PASTE_ID_HERE", "Your SBGL player ID");
            Logger.LogInfo($"[Init] CompCheck_PlayerId initialized: '{CompCheck_PlayerId.Value}'");

            // === MATCHMAKING ASSISTANT CONFIG ===
            MM_ShowSystemLogs = Config.Bind("Matchmaking.UI Settings", "Show System Logs", true, "Display system event logs");
            MM_EnableEmulation = Config.Bind("Matchmaking.Debug", "Enable Emulation", true, "Show manual host emulation button");
            MM_ShowFlowDebug = Config.Bind("Matchmaking.UI Settings", "Show Flow Debug", false, "Display flow diagnostics");

            // === LIVE LEADERBOARD CONFIG ===
            LL_Width = Config.Bind("LiveLeaderboard.UI", "Width", 350f, 
                new ConfigDescription("Total width of leaderboard panel", new AcceptableValueRange<float>(250f, 800f)));
            LL_MaxHeight = Config.Bind("LiveLeaderboard.UI", "Max Height", 999f, 
                new ConfigDescription("Maximum height of leaderboard panel", new AcceptableValueRange<float>(200f, 1440f)));
            LL_PosX = Config.Bind("LiveLeaderboard.UI", "X Position", 5f, 
                new ConfigDescription("Horizontal position", new AcceptableValueRange<float>(0f, 3000f)));
            LL_PosY = Config.Bind("LiveLeaderboard.UI", "Y Position", 200f, 
                new ConfigDescription("Vertical position", new AcceptableValueRange<float>(0f, 2160f)));
            LL_Opacity = Config.Bind("LiveLeaderboard.UI", "Opacity", 0.85f, 
                new ConfigDescription("Panel transparency", new AcceptableValueRange<float>(0f, 1f)));
            LL_MaxPlayers = Config.Bind("LiveLeaderboard.UI", "Max Players", 16, 
                new ConfigDescription("Maximum rows to display", new AcceptableValueRange<int>(1, 50)));

            // === API ENVIRONMENT CONFIG (Staff Only) ===
            API_Environment = Config.Bind("API.Environment", "API Environment", "Production", 
                new ConfigDescription("Select API environment: Production or PreProd (Staff Only)", 
                    new AcceptableValueList<string>("Production", "PreProd")));
            
            // Subscribe to environment changes with logging and steam recheck
            API_Environment.SettingChanged += (sender, args) => 
            {
                Logger.LogInfo($"[API Setup] ⚡ Config setting changed to: {API_Environment.Value}");
                Logger.LogInfo("[API Setup] Rechecking staff status for environment change...");
                DetectStaffUser();
                SetupApiEndpoints();
            };

            // Initialize player profile resolution and features
            StartCoroutine(InitializeAsync());
            
            // Retry staff detection after Steam initializes (typically 3-5 seconds)
            StartCoroutine(RetryStaffDetectionAfterSteamInit());
        }

        private IEnumerator RetryStaffDetectionAfterSteamInit()
        {
            // Give Steam API time to initialize (empirically 3-5 seconds from logs)
            yield return new WaitForSeconds(6f);
            
            Logger.LogInfo("[Init] Retrying staff detection after Steam initialization...");
            
            // Try to detect staff again now that Steam should be ready
            string steamName = GetSteamUsername();
            
            if (!string.IsNullOrEmpty(steamName))
            {
                Logger.LogInfo($"[Init] ✓ Steam username finally retrieved: '{steamName}'");
                
                // Check if staff
                bool wasStaff = _isStaffUser;
                DetectStaffUser();
                
                if (_isStaffUser && !wasStaff)
                {
                    Logger.LogInfo("[Init] ✓ Staff status changed to TRUE! Updating API configuration...");
                    SetupApiEndpoints();
                    Logger.LogInfo("[Init] API endpoints updated for staff member");
                }
                else if (!_isStaffUser)
                {
                    Logger.LogInfo($"[Init] User '{steamName}' confirmed not in staff list");
                }
                
                // Also retry player profile resolution now that Steam is ready
                Logger.LogInfo("[Init] Retrying player profile resolution with Steam username...");
                yield return ResolvePlayerProfile(CompCheck_PlayerId.Value);
                Logger.LogInfo("[Init] Player profile resolution retry completed");
            }
            else
            {
                Logger.LogInfo("[Init] Could not retrieve Steam username even after delay");
            }
        }

        private IEnumerator InitializeAsync()
        {
            Logger.LogInfo("[Init] Starting InitializeAsync()");
            Logger.LogInfo("[Init] Staff list count before RefreshStaffList: " + _dynamicStaffList.Count);
            
            // Load staff list from gist first, then detect if user is staff
            Logger.LogInfo("[Init] Calling RefreshStaffList() coroutine...");
            yield return RefreshStaffList();
            Logger.LogInfo("[Init] RefreshStaffList() completed. Staff list now has " + _dynamicStaffList.Count + " members");
            
            Logger.LogInfo("[Init] Calling DetectStaffUser()...");
            DetectStaffUser();
            Logger.LogInfo("[Init] DetectStaffUser() completed. _isStaffUser = " + _isStaffUser);
            
            Logger.LogInfo("[Init] Calling SetupApiEndpoints()...");
            SetupApiEndpoints();
            Logger.LogInfo("[Init] SetupApiEndpoints() completed");
            
            // Note: Player profile resolution is deferred to RetryStaffDetectionAfterSteamInit()
            // because Steam isn't initialized yet at this point
            
            // Sync config values to PlayerPrefs for component access
            SyncConfigToPlayerPrefs();
            
            // Initialize all features
            InitializeFeatures();
            
            Logger.LogInfo("SBGL-UnifiedMod is awake and managing features!");
        }

        private void DetectStaffUser()
        {
            try
            {
                string steamName = GetSteamUsername();
                Logger.LogInfo($"[Staff Detection] Steam username: '{steamName}' | Staff list contains {_dynamicStaffList.Count} members");
                
                if (!string.IsNullOrEmpty(steamName))
                {
                    bool isInList = _dynamicStaffList.Contains(steamName);
                    Logger.LogInfo($"[Staff Detection] Is '{steamName}' in staff list: {isInList}");
                    
                    _isStaffUser = isInList;
                    
                    if (_isStaffUser)
                    {
                        Logger.LogInfo($"✓ Staff user detected: {steamName}. API environment switching enabled!");
                    }
                    else
                    {
                        Logger.LogInfo($"✗ '{steamName}' is not in staff list. Using Production only.");
                    }
                }
                else
                {
                    Logger.LogWarning("[Staff Detection] Could not retrieve Steam username");
                    _isStaffUser = false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Staff Detection] Exception during staff detection: {ex.Message}");
                _isStaffUser = false;
            }
        }

        private string GetSteamUsername()
        {
            try
            {
                // Approach 1: Try direct Steamworks access with minimal property checks
                var steamAssembly = System.AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "com.rlabrecque.steamworks.net");

                if (steamAssembly != null)
                {
                    try
                    {
                        var steamClientType = steamAssembly.GetType("Steamworks.SteamClient");
                        if (steamClientType != null)
                        {
                            // Just try to get the Name property directly
                            var nameProp = steamClientType.GetProperty("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                            if (nameProp != null)
                            {
                                try
                                {
                                    string foundName = nameProp.GetValue(null)?.ToString();
                                    if (!string.IsNullOrEmpty(foundName))
                                    {
                                        Logger.LogInfo($"[Steam Detection] ✓ Retrieved Steam username: '{foundName}'");
                                        return foundName;
                                    }
                                }
                                catch (System.Reflection.TargetInvocationException tie)
                                {
                                    Logger.LogWarning($"[Steam Detection] TargetInvocationException (Steam not initialized yet): {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}");
                                }
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Logger.LogWarning($"[Steam Detection] Direct Steamworks approach failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Approach 2: Try via reflection on any assembly (with better error handling)
                try
                {
                    var steamType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => 
                        {
                            try { return a.GetTypes(); }
                            catch (System.Reflection.ReflectionTypeLoadException) { return new Type[0]; }
                            catch { return new Type[0]; }
                        })
                        .FirstOrDefault(t => t.Name == "SteamClient");

                    if (steamType != null)
                    {
                        var nameProp = steamType.GetProperty("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (nameProp != null)
                        {
                            try
                            {
                                string foundName = nameProp.GetValue(null)?.ToString();
                                if (!string.IsNullOrEmpty(foundName))
                                {
                                    Logger.LogInfo($"[Steam Detection] ✓ Found Steam username via broad search: '{foundName}'");
                                    return foundName;
                                }
                            }
                            catch (System.Reflection.TargetInvocationException tie)
                            {
                                Logger.LogWarning($"[Steam Detection] TargetInvocationException in broad search (Steam not initialized yet): {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}");
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.LogWarning($"[Steam Detection] Broad search approach failed: {ex.GetType().Name}: {ex.Message}");
                }

                Logger.LogWarning("[Steam Detection] Could not retrieve Steam username via any method");
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning($"[Steam Detection] Outer exception: {ex.GetType().Name}: {ex.Message}");
            }
            return string.Empty;
        }

        private IEnumerator RefreshStaffList()
        {
            Logger.LogInfo("[Staff List] Starting RefreshStaffList() coroutine...");
            Logger.LogInfo($"[Staff List] Gist URL: {GIST_URL}");
            
            using (UnityWebRequest webRequest = UnityWebRequest.Get(GIST_URL))
            {
                webRequest.certificateHandler = new BypassCertificate();
                Logger.LogInfo("[Staff List] Sending web request...");
                yield return webRequest.SendWebRequest();
                Logger.LogInfo($"[Staff List] Web request completed. Result: {webRequest.result}");

                if (webRequest.result == UnityWebRequest.Result.Success)
                {
                    string rawContent = webRequest.downloadHandler.text;
                    Logger.LogInfo($"[Staff List] Downloaded {rawContent.Length} characters");
                    
                    string[] lines = rawContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    Logger.LogInfo($"[Staff List] Split into {lines.Length} lines");
                    
                    _dynamicStaffList.Clear();
                    
                    foreach (string line in lines)
                    {
                        Logger.LogInfo($"[Staff List] Processing line: '{line}'");
                        // Parse username after pipe character (format: "SteamID | Username")
                        string[] parts = line.Split('|');
                        Logger.LogInfo($"[Staff List] Split by pipe: {parts.Length} parts");
                        
                        if (parts.Length >= 2)
                        {
                            string username = parts[1].Trim();
                            Logger.LogInfo($"[Staff List] Extracted username: '{username}'");
                            
                            if (!string.IsNullOrEmpty(username))
                            {
                                _dynamicStaffList.Add(username);
                                Logger.LogInfo($"[Staff List] ✓ Added staff member: {username}");
                            }
                        }
                    }
                    Logger.LogInfo($"[Staff List] ✓ Final staff list has {_dynamicStaffList.Count} members");
                    
                    // Re-run detection now that we have the fresh list
                    DetectStaffUser();
                    SetupApiEndpoints();
                }
                else
                {
                    Logger.LogWarning($"[Staff List] ✗ Web request failed. Result: {webRequest.result}, Error: {webRequest.error}");
                    Logger.LogWarning($"[Staff List] Response Code: {webRequest.responseCode}");
                    if (!string.IsNullOrEmpty(webRequest.downloadHandler?.text))
                    {
                        Logger.LogWarning($"[Staff List] Response Text: {webRequest.downloadHandler.text}");
                    }
                }
            }
        }


        private void SetupApiEndpoints()
        {
            // Clear the resolved profile flag since the player ID may differ in the new environment
            _sharedProfile.IsResolved = false;
            Logger.LogInfo($"[API Setup] Cleared cached profile for environment switch");
            
            Logger.LogInfo($"[API Setup] _isStaffUser={_isStaffUser}, API_Environment.Value='{API_Environment.Value}'");
            
            if (_isStaffUser)
            {
                string environment = API_Environment.Value;
                if (environment == "PreProd")
                {
                    _currentPlayerApi = PREPROD_PLAYER_API;
                    _currentAppId = PREPROD_APP_ID;
                    _currentAuthToken = PREPROD_AUTH_TOKEN;
                    Logger.LogInfo("[API Setup] ✓ Switched to PreProd configuration");
                }
                else
                {
                    _currentPlayerApi = PROD_PLAYER_API;
                    _currentAppId = PROD_APP_ID;
                    _currentAuthToken = PROD_AUTH_TOKEN;
                    Logger.LogInfo("[API Setup] ✓ Switched to Production configuration");
                }
            }
            else
            {
                _currentPlayerApi = PROD_PLAYER_API;
                _currentAppId = PROD_APP_ID;
                _currentAuthToken = PROD_AUTH_TOKEN;
                Logger.LogInfo("[API Setup] User is not staff - using Production only");
            }

            // ==========================================
            // API DEBUG LOGGING
            // ==========================================
            string envName = _isStaffUser ? API_Environment.Value : "Production (Standard)";
            string maskedToken = _currentAuthToken.Length > 15 
                ? $"{_currentAuthToken.Substring(0, 10)}...{_currentAuthToken.Substring(_currentAuthToken.Length - 5)}" 
                : "****";

            Logger.LogInfo("==========================================");
            Logger.LogInfo($"API CONFIGURATION LOADED ({envName})");
            Logger.LogInfo($"Endpoint:  {_currentPlayerApi}");
            Logger.LogInfo($"App ID:    {_currentAppId}");
            Logger.LogInfo($"Auth:      {maskedToken}");
            Logger.LogInfo("==========================================");
            
            // When API environment changes, re-resolve player profile with new endpoint
            // (player ID may be different in different environments)
            Logger.LogInfo("[API Setup] Re-resolving player profile for new API environment...");
            StartCoroutine(ResolvePlayerProfile(CompCheck_PlayerId.Value));
            
            // Fire event to notify components of API change
            Logger.LogInfo("[API Setup] ✓ Notifying components of API configuration change");
            ApiConfigChanged?.Invoke();
        }

        private void InitializeFeatures()
        {
            // Sync config values to PlayerPrefs for component access
            SyncConfigToPlayerPrefs();
            
            // Initialize Competitive Plugin Check as a managed component
            GameObject compCheckObj = new GameObject("SBGL-CompetitivePluginCheck");
            UnityEngine.Object.DontDestroyOnLoad(compCheckObj);
            CompetitivePluginCheck compCheck = compCheckObj.AddComponent<CompetitivePluginCheck>();
            compCheck.SetConfig(CompCheck_X, CompCheck_Y, CompCheck_Width, CompCheck_Alpha, 
                CompCheck_ShowModList, CompCheck_ShowLobbyMods, CompCheck_ShowDebugWindow, 
                CompCheck_UpdateInterval, CompCheck_PlayerId);
            
            // Initialize Matchmaking Assistant as a managed component
            GameObject matchmakingObj = new GameObject("SBGL-MatchmakingAssistant");
            UnityEngine.Object.DontDestroyOnLoad(matchmakingObj);
            SBGLPlugin matchmaking = matchmakingObj.AddComponent<SBGLPlugin>();
            matchmaking.SetConfig(MM_ShowSystemLogs, MM_EnableEmulation, MM_ShowFlowDebug, Logger);
            
            // Initialize Live Leaderboard as a managed component
            GameObject leaderboardObj = new GameObject("SBGL-LiveLeaderboard");
            UnityEngine.Object.DontDestroyOnLoad(leaderboardObj);
            LiveLeaderboardPlugin leaderboard = leaderboardObj.AddComponent<LiveLeaderboardPlugin>();
            leaderboard.SetConfig(LL_Width, LL_MaxHeight, LL_PosX, LL_PosY, LL_Opacity, LL_MaxPlayers);
        }

        private IEnumerator ResolvePlayerProfile(string playerIdOrName)
        {
            // If no manual ID provided, try to auto-resolve using Steam username
            if (string.IsNullOrEmpty(playerIdOrName) || playerIdOrName == "PASTE_ID_HERE")
            {
                string steamName = GetSteamUsername();
                if (!string.IsNullOrEmpty(steamName))
                {
                    Logger.LogInfo($"[Player Lookup] No Player ID configured, using Steam username: {steamName}");
                    playerIdOrName = steamName;
                }
                else
                {
                    Logger.LogWarning("[Player Lookup] Player ID not configured and Steam username unavailable. Skipping profile resolution.");
                    yield break;
                }
            }

            Logger.LogInfo($"[Player Lookup] Resolving player profile for: {playerIdOrName}");
            
            // Try exact match first
            bool found = false;
            yield return PlayerProfileFetcher.FetchPlayerProfile(playerIdOrName, _currentPlayerApi, _currentAppId, _currentAuthToken,
                (success, profile) =>
                {
                    if (success && profile != null)
                    {
                        _sharedProfile.ID = profile.ID;
                        _sharedProfile.DisplayName = profile.DisplayName;
                        _sharedProfile.CurrentMMR = profile.CurrentMMR;
                        _sharedProfile.Region = profile.Region;
                        _sharedProfile.ProfilePicUrl = profile.ProfilePicUrl;
                        _sharedProfile.IsResolved = true;
                        found = true;
                        Logger.LogInfo($"[Player Lookup] ✓ Profile resolved: {_sharedProfile.DisplayName} (ID: {_sharedProfile.ID})");
                    }
                }, Logger);

            // If not found and looks like a name, try fuzzy match
            if (!found && !playerIdOrName.All(char.IsDigit))
            {
                Logger.LogInfo("[Player Lookup] Exact match failed, trying fuzzy match...");
                yield return PlayerProfileFetcher.FuzzySearchPlayer(playerIdOrName, _currentPlayerApi, _currentAppId, _currentAuthToken,
                    (success, profile) =>
                    {
                        if (success && profile != null)
                        {
                            _sharedProfile.ID = profile.ID;
                            _sharedProfile.DisplayName = profile.DisplayName;
                            _sharedProfile.CurrentMMR = profile.CurrentMMR;
                            _sharedProfile.Region = profile.Region;
                            _sharedProfile.ProfilePicUrl = profile.ProfilePicUrl;
                            _sharedProfile.IsResolved = true;
                            found = true;
                            Logger.LogInfo($"[Player Lookup] ✓ Fuzzy match resolved: {_sharedProfile.DisplayName}");
                        }
                    }, Logger);
            }

            if (!_sharedProfile.IsResolved)
            {
                Logger.LogWarning($"[Player Lookup] Failed to resolve player profile for: {playerIdOrName}");
            }
            else
            {
                // Download profile picture if available
                if (!string.IsNullOrEmpty(_sharedProfile.ProfilePicUrl))
                {
                    Logger.LogInfo($"[Player Lookup] Downloading profile picture: {_sharedProfile.ProfilePicUrl}");
                    yield return DownloadProfilePicture(_sharedProfile.ProfilePicUrl);
                }
            }
        }

        private IEnumerator DownloadProfilePicture(string url)
        {
            if (string.IsNullOrEmpty(url)) yield break;
            if (url.StartsWith("http://")) url = url.Replace("http://", "https://");
            
            using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(url))
            {
                request.certificateHandler = new BypassCertificate();
                yield return request.SendWebRequest();
                
                if (request.result == UnityWebRequest.Result.Success)
                {
                    _profilePicture = DownloadHandlerTexture.GetContent(request);
                    Logger.LogInfo($"[Player Lookup] ✓ Profile picture downloaded: {_profilePicture.width}x{_profilePicture.height}");
                }
                else
                {
                    Logger.LogWarning($"[Player Lookup] Failed to download profile picture: {request.error}");
                }
            }
        }

        private void SyncConfigToPlayerPrefs()
        {
            // === COMPETITIVE CHECK CONFIG SYNC ===
            PlayerPrefs.SetFloat("CompCheck_X", CompCheck_X.Value);
            PlayerPrefs.SetFloat("CompCheck_Y", CompCheck_Y.Value);
            PlayerPrefs.SetFloat("CompCheck_Width", CompCheck_Width.Value);
            PlayerPrefs.SetFloat("CompCheck_Alpha", CompCheck_Alpha.Value);
            PlayerPrefs.SetInt("CompCheck_ShowModList", CompCheck_ShowModList.Value ? 1 : 0);
            PlayerPrefs.SetInt("CompCheck_ShowLobbyMods", CompCheck_ShowLobbyMods.Value ? 1 : 0);
            PlayerPrefs.SetInt("CompCheck_ShowDebugWindow", CompCheck_ShowDebugWindow.Value ? 1 : 0);
            PlayerPrefs.SetFloat("CompCheck_UpdateInterval", CompCheck_UpdateInterval.Value);
            PlayerPrefs.SetString("CompCheck_PlayerId", CompCheck_PlayerId.Value);

            // === MATCHMAKING CONFIG SYNC ===
            PlayerPrefs.SetInt("MM_ShowSystemLogs", MM_ShowSystemLogs.Value ? 1 : 0);
            PlayerPrefs.SetInt("MM_EnableEmulation", MM_EnableEmulation.Value ? 1 : 0);

            // === LEADERBOARD CONFIG SYNC ===
            PlayerPrefs.SetFloat("LL_Width", LL_Width.Value);
            PlayerPrefs.SetFloat("LL_MaxHeight", LL_MaxHeight.Value);
            PlayerPrefs.SetFloat("LL_PosX", LL_PosX.Value);
            PlayerPrefs.SetFloat("LL_PosY", LL_PosY.Value);
            PlayerPrefs.SetFloat("LL_Opacity", LL_Opacity.Value);
            PlayerPrefs.SetInt("LL_MaxPlayers", LL_MaxPlayers.Value);
            
            PlayerPrefs.Save();
        }
    }
}