using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System;
using System.IO;
using System.Security.Cryptography;
using SBGL.UnifiedMod.Features.CompetitivePluginCheck;
using SBGL.UnifiedMod.Utils;
using SBGL.UnifiedMod.Patches;
using SBGLeagueAutomation;
using SBGLLiveLeaderboard;
using Steamworks;


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
    [BepInPlugin("com.sbgl.unified", "SBGL Unified Mod", "0.0.8")]
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
        private const string PROD_AUTH_TOKEN = "3f7c84bf7a734c6a86bbc34245a1e6e4";
        
        // PreProd API
        private const string PREPROD_PLAYER_API = "https://sbg-league-manager-preprod.base44.app/api/apps/69d5bc0bb18e58e435ff4e3f/entities/Player";
        private const string PREPROD_APP_ID = "69d5bc0bb18e58e435ff4e3f";
        private const string PREPROD_AUTH_TOKEN = "1ec5fe0220c041939070ac4690933ba3";
        
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
        public ConfigEntry<bool> CompCheck_HideUIWindow;
        public ConfigEntry<bool> CompCheck_ShowModList;
        public ConfigEntry<bool> CompCheck_ShowDebugWindow;
        public ConfigEntry<float> CompCheck_UpdateInterval;
        public ConfigEntry<string> CompCheck_PlayerId;
        public ConfigEntry<float> CompCheck_CompliancePanelX;
        public ConfigEntry<float> CompCheck_CompliancePanelY;
        public ConfigEntry<bool> CompCheck_MelonLoaderChatEnabled;

        // ==========================================
        // MATCHMAKING ASSISTANT CONFIG
        // ==========================================
        public ConfigEntry<bool> MM_ShowSystemLogs;
        public ConfigEntry<bool> MM_ShowFlowDebug;
        public ConfigEntry<bool> MM_ShowUploadNotices;
        public ConfigEntry<bool> MM_IgnoreSbglLobbyRequirement;

        // ==========================================
        // PSEUDO DEDICATED SERVER CONFIG
        // ==========================================
        public ConfigEntry<bool>   PDS_Enabled;
        public ConfigEntry<float>  PDS_RequeueDelay;
        public ConfigEntry<string> PDS_DefaultRuleset;

        // ==========================================
        // RULESET DISPLAY CONFIG
        // ==========================================
        public ConfigEntry<float> RS_PosX;
        public ConfigEntry<float> RS_PosY;
        public ConfigEntry<bool> RS_ShowDetails;

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
        public ConfigEntry<string> Staff_ModManifestOutputPath;

        private readonly Rect _staffToolsRect = new Rect(20f, 20f, 340f, 110f);
        private string _staffManifestStatus = "Ready";

        // ==========================================
        // API CONFIGURATION CHANGE EVENT
        // ==========================================
        public delegate void OnApiConfigChanged();
        public static event OnApiConfigChanged ApiConfigChanged;

        void Awake()
        {
            // Initialize MelonLoader detection bridge (runs before everything else)
            MelonLoaderBridge.Initialize();
            
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
            CompCheck_HideUIWindow = Config.Bind("CompetitiveCheck.UI Appearance", "Hide UI Window Completely", false,
                "Hide the Competitive Plugin Check overlay window entirely.");
            CompCheck_ShowModList = Config.Bind("CompetitiveCheck.UI Appearance", "Show My Mods", false, "Display local mod list");
            CompCheck_ShowDebugWindow = Config.Bind("CompetitiveCheck.UI Appearance", "Show Debug Window", false, "Display debug information");
            CompCheck_UpdateInterval = Config.Bind("CompetitiveCheck.Logic", "Update Interval", 5f, 
                new ConfigDescription("Update frequency in minutes", new AcceptableValueRange<float>(1f, 60f)));
            CompCheck_PlayerId = Config.Bind("CompetitiveCheck.League", "Player ID", "PASTE_ID_HERE", "Your SBGL player ID");
            Logger.LogInfo($"[Init] CompCheck_PlayerId initialized: '{CompCheck_PlayerId.Value}'");
            CompCheck_CompliancePanelX = Config.Bind("CompetitiveCheck.Debug Panel", "Compliance Panel X", 0f, 
                new ConfigDescription("Compliance debug panel X position (0 = auto)", new AcceptableValueRange<float>(0f, 4000f)));
            CompCheck_CompliancePanelY = Config.Bind("CompetitiveCheck.Debug Panel", "Compliance Panel Y", 0f, 
                new ConfigDescription("Compliance debug panel Y position (0 = auto)", new AcceptableValueRange<float>(0f, 4000f)));
            CompCheck_MelonLoaderChatEnabled = Config.Bind("CompetitiveCheck.Notifications", "Announce MelonLoader In Chat", false,
                "When enabled, broadcasts a chat message in SBGL lobbies when MelonLoader is detected on your client.");

            // === MATCHMAKING ASSISTANT CONFIG ===
            MM_ShowSystemLogs = Config.Bind("Matchmaking.UI Settings", "Show System Logs", true, "Display system event logs");
            MM_ShowFlowDebug = Config.Bind("Matchmaking.UI Settings", "Show Flow Debug", false, "Display flow diagnostics");
            MM_ShowUploadNotices = Config.Bind("Matchmaking.UI Settings", "Show Upload Notices", true, "Show on-screen upload success/failure notices during gameplay");
            MM_IgnoreSbglLobbyRequirement = Config.Bind("Matchmaking.UI Settings", "Upload All Matches", false, "When enabled, uploads match results for any match and ignores the SBGL-* lobby-name requirement.");

            // === PSEUDO DEDICATED SERVER CONFIG ===
            PDS_Enabled = Config.Bind("PseudoDedicatedServer", "Enabled", false,
                "When true, automatically joins queue, accepts matches, hosts/joins lobbies, and requeues after each match.");
            PDS_RequeueDelay = Config.Bind("PseudoDedicatedServer", "Requeue Delay Seconds", 10f,
                new ConfigDescription("How long (seconds) to wait on the Driving Range before returning to main menu to requeue.",
                    new AcceptableValueRange<float>(3f, 120f)));
            PDS_DefaultRuleset = Config.Bind("PseudoDedicatedServer", "Default Ruleset", "ranked",
                new ConfigDescription("Ruleset to apply automatically as host.",
                    new AcceptableValueList<string>("ranked", "pro_series")));

            // === RULESET DISPLAY CONFIG ===
            RS_PosX = Config.Bind("RuleSetDisplay.UI", "X Position", -1f,
                new ConfigDescription("Horizontal position. -1 = auto (right edge)", new AcceptableValueRange<float>(-1f, 4000f)));
            RS_PosY = Config.Bind("RuleSetDisplay.UI", "Y Position", 20f,
                new ConfigDescription("Vertical position", new AcceptableValueRange<float>(0f, 4000f)));
            RS_ShowDetails = Config.Bind("RuleSetDisplay.UI", "Show Details Panel", false,
                "Show match type, course, season and ruleset labels below the buttons");

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
            Staff_ModManifestOutputPath = Config.Bind("Staff.Tools", "Approved Mod Manifest Output Path",
                Path.Combine(Paths.ConfigPath, "sbgl-approved-mods.generated.json"),
                "Staff-only output path for exporting the approved mod hash manifest.");
            
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
                if (!SteamClient.IsValid)
                {
                    Logger.LogWarning("[Steam Detection] SteamClient is not initialized yet");
                    return string.Empty;
                }

                string foundName = SteamClient.Name;
                if (!string.IsNullOrEmpty(foundName))
                {
                    Logger.LogInfo($"[Steam Detection] Retrieved Steam username: '{foundName}'");
                    return foundName;
                }

                Logger.LogWarning("[Steam Detection] SteamClient returned an empty username");
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

        void OnGUI()
        {
            if (!_isStaffUser) return;

            string sceneName = SceneManager.GetActiveScene().name?.ToLowerInvariant() ?? string.Empty;
            bool isMenuScene = sceneName.Contains("menu");
            if (!isMenuScene) return;

            GUI.Box(_staffToolsRect, "SBGL Staff Tools");

            Rect buttonRect = new Rect(_staffToolsRect.x + 10f, _staffToolsRect.y + 28f, _staffToolsRect.width - 20f, 30f);
            if (GUI.Button(buttonRect, "Generate Approved Mods JSON"))
            {
                ExportApprovedModsManifest();
            }

            Rect statusRect = new Rect(_staffToolsRect.x + 10f, _staffToolsRect.y + 65f, _staffToolsRect.width - 20f, 36f);
            GUI.Label(statusRect, _staffManifestStatus);
        }

        private void ExportApprovedModsManifest()
        {
            try
            {
                string outputPath = Staff_ModManifestOutputPath?.Value;
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    throw new InvalidOperationException("Staff manifest output path is empty.");
                }

                string manifestJson = BuildApprovedModsManifestJson();
                string directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(outputPath, manifestJson);
                _staffManifestStatus = $"Manifest written: {outputPath}";
                Logger.LogInfo($"[Staff Tools] Approved mod manifest exported to '{outputPath}'");
            }
            catch (Exception ex)
            {
                _staffManifestStatus = $"Export failed: {ex.Message}";
                Logger.LogError($"[Staff Tools] Failed to export approved mod manifest: {ex}");
            }
        }

        private string BuildApprovedModsManifestJson()
        {
            var mods = new JArray();

            foreach (var plugin in BepInEx.Bootstrap.Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.Name, StringComparer.OrdinalIgnoreCase))
            {
                var pluginInstanceProperty = plugin.GetType().GetProperty("Instance");
                var pluginInstance = pluginInstanceProperty?.GetValue(plugin);
                var pluginAssembly = pluginInstance?.GetType().Assembly;
                if (pluginAssembly == null || string.IsNullOrWhiteSpace(pluginAssembly.Location))
                {
                    continue;
                }

                string pluginAssemblyPath = Path.GetFullPath(pluginAssembly.Location);
                var assemblies = new JArray();
                assemblies.Add(new JObject
                {
                    ["file"] = Path.GetFileName(pluginAssemblyPath),
                    ["relativePath"] = MakeRelativeToPluginsRoot(pluginAssemblyPath),
                    ["sha256"] = ComputeSha256(pluginAssemblyPath)
                });

                mods.Add(new JObject
                {
                    ["name"] = plugin.Metadata.Name,
                    ["guid"] = plugin.Metadata.GUID,
                    ["version"] = plugin.Metadata.Version?.ToString() ?? string.Empty,
                    ["assemblies"] = assemblies
                });
            }

            var manifest = new JObject
            {
                ["version"] = 1,
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["generatedBy"] = GetSteamUsername(),
                ["mods"] = mods
            };

            return manifest.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        private static string MakeRelativeToPluginsRoot(string fullPath)
        {
            try
            {
                string normalizedFullPath = Path.GetFullPath(fullPath).Replace('\\', '/');
                const string pluginsMarker = "/BepInEx/plugins/";
                int markerIndex = normalizedFullPath.IndexOf(pluginsMarker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex >= 0)
                {
                    return normalizedFullPath.Substring(markerIndex + pluginsMarker.Length);
                }

                return Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, fullPath).Replace('\\', '/');
            }
            catch
            {
                return fullPath;
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
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
            // Initialize Harmony patches for rule enforcement
            Logger.LogInfo("[Init] Initializing Harmony patches...");
            try
            {
                var harmony = new Harmony("com.sbgl.unified.patches");
                harmony.PatchAll(typeof(RulePatches));
                RulePatches.SetLogger(Logger);
                Logger.LogInfo("[Init] ✓ Harmony patches initialized successfully");
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"[Init] Failed to initialize Harmony patches: {ex.Message}");
            }
            
            // Sync config values to PlayerPrefs for component access
            SyncConfigToPlayerPrefs();
            
            // Initialize Competitive Plugin Check as a managed component
            GameObject compCheckObj = new GameObject("SBGL-CompetitivePluginCheck");
            UnityEngine.Object.DontDestroyOnLoad(compCheckObj);
            CompetitivePluginCheck compCheck = compCheckObj.AddComponent<CompetitivePluginCheck>();
            compCheck.SetConfig(CompCheck_X, CompCheck_Y, CompCheck_Width, CompCheck_Alpha, 
                CompCheck_HideUIWindow, CompCheck_ShowModList, CompCheck_ShowDebugWindow, 
                CompCheck_UpdateInterval, CompCheck_PlayerId,
                CompCheck_CompliancePanelX, CompCheck_CompliancePanelY,
                CompCheck_MelonLoaderChatEnabled);
            
            // Initialize Matchmaking Assistant as a managed component
            GameObject matchmakingObj = new GameObject("SBGL-MatchmakingAssistant");
            UnityEngine.Object.DontDestroyOnLoad(matchmakingObj);
            SBGLPlugin matchmaking = matchmakingObj.AddComponent<SBGLPlugin>();
            matchmaking.SetConfig(MM_ShowSystemLogs, MM_ShowFlowDebug, MM_ShowUploadNotices, MM_IgnoreSbglLobbyRequirement, Logger);
            
            // Initialize RuleSet Display Manager as a managed component
            GameObject ruleSetObj = new GameObject("SBGL-RuleSetDisplayManager");
            UnityEngine.Object.DontDestroyOnLoad(ruleSetObj);
            Features.RuleSetDisplayManager ruleSetDisplay = ruleSetObj.AddComponent<Features.RuleSetDisplayManager>();
            ruleSetDisplay.SetConfig(RS_PosX, RS_PosY, RS_ShowDetails);
            
            // Initialize Live Leaderboard as a managed component
            GameObject leaderboardObj = new GameObject("SBGL-LiveLeaderboard");
            UnityEngine.Object.DontDestroyOnLoad(leaderboardObj);
            LiveLeaderboardPlugin leaderboard = leaderboardObj.AddComponent<LiveLeaderboardPlugin>();
            leaderboard.SetConfig(LL_Width, LL_MaxHeight, LL_PosX, LL_PosY, LL_Opacity, LL_MaxPlayers);

            // Initialize Pseudo Dedicated Server as a managed component
            GameObject pdsObj = new GameObject("SBGL-PseudoDedicatedServer");
            UnityEngine.Object.DontDestroyOnLoad(pdsObj);
            Features.PseudoDedicatedServer pds = pdsObj.AddComponent<Features.PseudoDedicatedServer>();
            pds.SetConfig(PDS_Enabled, PDS_RequeueDelay, PDS_DefaultRuleset, Logger);
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
            PlayerPrefs.SetInt("CompCheck_HideUIWindow", CompCheck_HideUIWindow.Value ? 1 : 0);
            PlayerPrefs.SetInt("CompCheck_ShowModList", CompCheck_ShowModList.Value ? 1 : 0);
            PlayerPrefs.SetInt("CompCheck_ShowDebugWindow", CompCheck_ShowDebugWindow.Value ? 1 : 0);
            PlayerPrefs.SetFloat("CompCheck_UpdateInterval", CompCheck_UpdateInterval.Value);
            PlayerPrefs.SetString("CompCheck_PlayerId", CompCheck_PlayerId.Value);
            PlayerPrefs.SetFloat("CompCheck_ComplianceX", CompCheck_CompliancePanelX.Value);
            PlayerPrefs.SetFloat("CompCheck_ComplianceY", CompCheck_CompliancePanelY.Value);
            // === MATCHMAKING CONFIG SYNC ===

            PlayerPrefs.SetInt("MM_ShowSystemLogs", MM_ShowSystemLogs.Value ? 1 : 0);
            PlayerPrefs.SetInt("MM_IgnoreSbglLobbyRequirement", MM_IgnoreSbglLobbyRequirement.Value ? 1 : 0);

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