using BepInEx;
using BepInEx.Configuration;
using BepInEx.Bootstrap;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Linq;
using System;
using System.Reflection;
using SBGL.UnifiedMod.Core;
using SBGL.UnifiedMod.Utils;
using HarmonyLib;
using Steamworks; // Added for P2P Networking

namespace SBGL.UnifiedMod.Features.CompetitivePluginCheck
{

    public class ProSeriesEvent
    {
        public string name;
        public string event_date;
    }

    public class MatchEntry
    {
        public string match_summary;
        public string match_date;
    }

    public class CompetitivePluginCheck : MonoBehaviour
    {
        // Config entry references (injected by UnifiedPlugin)
        private ConfigEntry<float> _configX, _configY, _configWidth, _configAlpha, _configUpdateInterval;
        private ConfigEntry<bool> _configShowModList, _configShowLobbyMods, _configShowDebugWindow;
        private ConfigEntry<string> _configPlayerId;
        private const string ALLOWED_MODS_URL = "https://gist.githubusercontent.com/Kingcox22/32b1bcf1bdbec4ec47d086fec70628c1/raw/allowed_mods.txt";

        // --- NETWORKING CONSTANTS ---
        private const int SBGL_NET_CHANNEL = 2622;
        private Dictionary<ulong, string> _remotePlayerMods = new Dictionary<ulong, string>();

        private GameObject _canvasObj, _profilePicContainer, _bgObj, _warnContainer, _debugWindowObj;
        private TextMeshProUGUI _statsText, _illegalWarningText, _missingWarningText, _debugWindowText;
        private Image _bgImage, _debugWindowBg;
        private RawImage _profileIcon;
        private RectTransform _bgRect, _debugWindowRect;

        private Dictionary<string, string> _allowedModsMap = new Dictionary<string, string>();
        private List<string> _missingModNames = new List<string>();
        public List<ProSeriesEvent> _upcomingEvents = new List<ProSeriesEvent>();
        public List<MatchEntry> _recentMatches = new List<MatchEntry>();

        private bool _anyIllegalMods = false, _isSyncing = false;
        private float _timeUntilNextUpdate = 0f;
        private string _lastSyncTime = "Never";

        public string _activeUsername = "Searching...", _playerRank = "N/A", _totalPlayers = "0", _playerMMR = "0", _matches = "0", _playerPeak = "0", _winRate = "0%", _lastChange = "0", _top3s = "0", _avgScore = "0.0", _syncStatus = "Idle";

        public string ResolvedName = "Not Found";
        public string ResolvedID = "None";

        // Config value accessors using ConfigEntry references
        private float ConfigX { get => _configX?.Value ?? PlayerPrefs.GetFloat("CompCheck_X", 20f); }
        private float ConfigY { get => _configY?.Value ?? PlayerPrefs.GetFloat("CompCheck_Y", 100f); }
        private float ConfigWidth { get => _configWidth?.Value ?? PlayerPrefs.GetFloat("CompCheck_Width", 200f); }
        private float ConfigAlpha { get => _configAlpha?.Value ?? PlayerPrefs.GetFloat("CompCheck_Alpha", 0.85f); }
        private bool ConfigShowModList { get => _configShowModList?.Value ?? (PlayerPrefs.GetInt("CompCheck_ShowModList", 0) == 1); set { if (_configShowModList != null) _configShowModList.Value = value; } }
        private bool ConfigShowLobbyMods { get => _configShowLobbyMods?.Value ?? (PlayerPrefs.GetInt("CompCheck_ShowLobbyMods", 1) == 1); }
        private bool ConfigShowDebugWindow { get => _configShowDebugWindow?.Value ?? (PlayerPrefs.GetInt("CompCheck_ShowDebugWindow", 0) == 1); set { if (_configShowDebugWindow != null) _configShowDebugWindow.Value = value; } }
        internal string ConfigPlayerId { get => _configPlayerId?.Value ?? PlayerPrefs.GetString("CompCheck_PlayerId", "PASTE_ID_HERE"); set { if (_configPlayerId != null) _configPlayerId.Value = value; } }

        // Set config entries from parent plugin
        public void SetConfig(ConfigEntry<float> x, ConfigEntry<float> y, ConfigEntry<float> width, ConfigEntry<float> alpha,
            ConfigEntry<bool> showModList, ConfigEntry<bool> showLobbyMods, ConfigEntry<bool> showDebugWindow,
            ConfigEntry<float> updateInterval, ConfigEntry<string> playerId)
        {
            _configX = x;
            _configY = y;
            _configWidth = width;
            _configAlpha = alpha;
            _configShowModList = showModList;
            _configShowLobbyMods = showLobbyMods;
            _configShowDebugWindow = showDebugWindow;
            _configUpdateInterval = updateInterval;
            _configPlayerId = playerId;
        }

        void Awake()
        {
            // Initialize configuration values from PlayerPrefs with defaults
            if (!PlayerPrefs.HasKey("CompCheck_X")) PlayerPrefs.SetFloat("CompCheck_X", 20f);
            if (!PlayerPrefs.HasKey("CompCheck_Y")) PlayerPrefs.SetFloat("CompCheck_Y", 100f);
            if (!PlayerPrefs.HasKey("CompCheck_Width")) PlayerPrefs.SetFloat("CompCheck_Width", 200f);
            if (!PlayerPrefs.HasKey("CompCheck_Alpha")) PlayerPrefs.SetFloat("CompCheck_Alpha", 0.85f);
            if (!PlayerPrefs.HasKey("CompCheck_UpdateInterval")) PlayerPrefs.SetFloat("CompCheck_UpdateInterval", 5f);
            if (!PlayerPrefs.HasKey("CompCheck_ShowModList")) PlayerPrefs.SetInt("CompCheck_ShowModList", 0);
            if (!PlayerPrefs.HasKey("CompCheck_ShowLobbyMods")) PlayerPrefs.SetInt("CompCheck_ShowLobbyMods", 1);
            if (!PlayerPrefs.HasKey("CompCheck_ShowDebugWindow")) PlayerPrefs.SetInt("CompCheck_ShowDebugWindow", 0);
            if (!PlayerPrefs.HasKey("CompCheck_PlayerId")) PlayerPrefs.SetString("CompCheck_PlayerId", "PASTE_ID_HERE");
            
            // Subscribe to API configuration changes
            UnifiedPlugin.ApiConfigChanged += OnApiConfigChanged;
            
            StartCoroutine(GetNetworkTime());
        }

        private void OnApiConfigChanged()
        {
            // Restart update loop to pick up new API configuration
            StopCoroutine(AutoUpdateLoop());
            StartCoroutine(AutoUpdateLoop());
        }

        void Start()
        {
            if (!string.IsNullOrEmpty(ConfigPlayerId) && ConfigPlayerId != "PASTE_ID_HERE")
            {
                ResolvedID = ConfigPlayerId;
            }
            CreateUI();
            StartCoroutine(AutoUpdateLoop());
        }

        void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current; if (kb == null) return;
            if (kb[UnityEngine.InputSystem.Key.F9].wasPressedThisFrame) { ConfigShowModList = !ConfigShowModList; UpdateUIReport(); }
            if (kb[UnityEngine.InputSystem.Key.F10].wasPressedThisFrame) { TriggerManualSync(); }
            if (kb[UnityEngine.InputSystem.Key.F11].wasPressedThisFrame) { ConfigShowDebugWindow = !ConfigShowDebugWindow; UpdateUIReport(); }

            if (_bgRect != null)
            {
                // Clamp position to keep UI on screen
                float clampedX = Mathf.Clamp(ConfigX, 0, Screen.width - ConfigWidth);
                float clampedY = Mathf.Clamp(ConfigY, 0, Screen.height - _bgRect.sizeDelta.y);
                
                _bgRect.anchoredPosition = new Vector2(clampedX, clampedY);
                _bgRect.sizeDelta = new Vector2(ConfigWidth, _bgRect.sizeDelta.y);
                _bgImage.color = new Color(0, 0, 0, ConfigAlpha);
                
                // Update config if position was clamped
                if (clampedX != ConfigX) _configX.Value = clampedX;
                if (clampedY != ConfigY) _configY.Value = clampedY;
            }

            // Update debug window visibility based on config
            if (_debugWindowObj != null)
            {
                _debugWindowObj.SetActive(ConfigShowDebugWindow);
            }

            // --- NETWORKING LOGIC IN UPDATE ---
            ListenForModReports();
            if (Time.frameCount % 600 == 0) BroadcastMyMods(); // Broadcast roughly every 10 seconds
        }

        // --- NEW P2P NETWORKING LOGIC ---

        private void BroadcastMyMods()
        {
            if (!SteamClient.IsValid) return;
            try
            {
                StringBuilder sb = new StringBuilder("SBGL_REPORT:");
                foreach (var plugin in Chainloader.PluginInfos.Values)
                {
                    if (plugin.Metadata.GUID.StartsWith("BepInEx") || plugin.Metadata.GUID.ToLower().Contains("compplugincheck")) continue;
                    sb.Append($"{plugin.Metadata.Name}|{plugin.Metadata.Version};");
                }
                
                // Add debug logging
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Broadcasting mods: {sb.ToString()}");

                byte[] data = Encoding.UTF8.GetBytes(sb.ToString());

                // Facepunch Steamworks API differs across builds; broadcast to peers we've already seen.
                foreach (var peer in _remotePlayerMods.Keys.ToArray())
                {
                    if (peer == SteamClient.SteamId) continue;
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Sending to peer {peer}: {sb.ToString()}");
                    SteamNetworking.SendP2PPacket(peer, data, -1, SBGL_NET_CHANNEL, P2PSend.Reliable);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[SBGL-CompPluginCheck] Error in BroadcastMyMods: {ex.Message}");
            }
        }

        private void ListenForModReports()
        {
            if (!SteamClient.IsValid) return;
            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, SBGL_NET_CHANNEL))
            {
                byte[] buffer = new byte[size];
                uint bytesRead = 0;
                SteamId remoteId = default;

                if (SteamNetworking.ReadP2PPacket(buffer, ref bytesRead, ref remoteId, SBGL_NET_CHANNEL))
                {
                    string msg = Encoding.UTF8.GetString(buffer);
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Received from peer {remoteId.Value}: {msg}");
                    
                    if (msg.StartsWith("SBGL_REPORT:"))
                    {
                        _remotePlayerMods[remoteId.Value] = msg.Replace("SBGL_REPORT:", "");
                        UpdateUIReport();
                    }
                }
            }
        }

        // --- END NEW P2P NETWORKING LOGIC ---

        public void TriggerManualSync()
        {
            StopAllCoroutines();
            _isSyncing = false;
            StartCoroutine(AutoUpdateLoop());
        }

        private string GetOrdinal(int number)
        {
            if (number <= 0) return number.ToString();
            switch (number % 100)
            {
                case 11:
                case 12:
                case 13:
                    return number + "th";
            }
            switch (number % 10)
            {
                case 1: return number + "st";
                case 2: return number + "nd";
                case 3: return number + "rd";
                default: return number + "th";
            }
        }

        private void ResetPlayerData(string userStatus = "Updating...")
        {
            _activeUsername = userStatus;
            _playerRank = "N/A"; _playerMMR = "0"; _playerPeak = "0"; _matches = "0";
            _lastChange = "0"; _top3s = "0"; _avgScore = "0.0"; _winRate = "0%";
            if (_profilePicContainer != null) _profilePicContainer.SetActive(false);
        }

        private string GetInGameName()
        {
            try
            {
                var steamAssembly = System.AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "com.rlabrecque.steamworks.net" || a.GetName().Name.Contains("Steamworks"));

                if (steamAssembly != null)
                {
                    var steamType = steamAssembly.GetType("Steamworks.SteamClient");
                    if (steamType != null)
                    {
                        var nameProp = steamType.GetProperty("Name", BindingFlags.Public | BindingFlags.Static);
                        string foundName = nameProp?.GetValue(null)?.ToString();
                        if (!string.IsNullOrEmpty(foundName)) return foundName;
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.Log($"[SBGL] Steam Reflection failed (Safe): {e.Message}");
            }
            return "";
        }

        IEnumerator AutoUpdateLoop()
        {            // Wait for player profile to be resolved by UnifiedPlugin before starting
            float waitTimeout = 0f;
            while (!UnifiedPlugin.IsPlayerProfileResolved() && waitTimeout < 12f)
            {
                yield return new WaitForSeconds(0.5f);
                waitTimeout += 0.5f;
            }
            
            if (UnifiedPlugin.IsPlayerProfileResolved())
            {
                var profile = UnifiedPlugin.GetPlayerProfile();
                UnityEngine.Debug.Log($"[CompetitivePluginCheck] Profile now resolved: {profile.DisplayName} (ID: {profile.ID})");
            }
                        while (true)
            {
                yield return TriggerFullSync(ConfigPlayerId?.Trim());
                float timer = _configUpdateInterval.Value * 60f;
                while (timer > 0)
                {
                    _timeUntilNextUpdate = timer;
                    UpdateUIReport();
                    yield return new WaitForSeconds(1f);
                    timer -= 1f;
                }
            }
        }

        IEnumerator TriggerFullSync(string configId)
        {
            if (_isSyncing) yield break;
            _isSyncing = true;
            _syncStatus = "Syncing...";
            ResetPlayerData();
            UpdateUIReport();

            string idToUse = "";
            
            // Try to get profile from centralized UnifiedPlugin resolution
            if (UnifiedPlugin.IsPlayerProfileResolved())
            {
                var profile = UnifiedPlugin.GetPlayerProfile();
                idToUse = profile.ID;
                ResolvedID = profile.ID;
                ResolvedName = profile.DisplayName;
            }
            else
            {
                // Fallback: try to resolve from Steam name if unified resolution hasn't completed
                string steamName = GetInGameName();
                if (!string.IsNullOrEmpty(steamName))
                {
                    yield return ResolvePlayerIdFromName(steamName);
                    if (ResolvedID != "None") idToUse = ResolvedID;
                }
            }

            if (string.IsNullOrEmpty(idToUse) || idToUse == "None")
            {
                if (!string.IsNullOrEmpty(configId) && configId != "PASTE_ID_HERE")
                {
                    idToUse = configId;
                    ResolvedID = configId;
                }
            }

            if (!string.IsNullOrEmpty(idToUse) && idToUse != "None" && idToUse != "PASTE_ID_HERE")
            {
                yield return CheckPluginsRoutine();
                yield return FetchPlayerData(idToUse);
                yield return FetchLeaderboardRank(idToUse);
                yield return GetNetworkTime();
                yield return FetchEvents();
                yield return FetchRecentMatches(idToUse);
                _syncStatus = (_activeUsername == "Invalid ID" || _activeUsername == "API Offline") ? "Error" : "Connected";
            }
            else
            {
                _syncStatus = "ID Required";
                ResetPlayerData("Link ID in Settings");
            }

            var menuTabs = UnityEngine.Object.FindFirstObjectByType<MenuTabs>();
            if (menuTabs != null) SBGLTabManager.Inject(menuTabs);

            _isSyncing = false;
            UpdateUIReport();
        }

        IEnumerator FetchRecentMatches(string id)
        {
            string query = "{\"player_id\":\"" + id + "\"}";
            string appId = UnifiedPlugin.GetCurrentAppId();
            string authToken = UnifiedPlugin.GetCurrentAuthToken();
            string url = $"https://sbgleague.com/api/apps/{appId}/entities/MatchEntry?q={UnityWebRequest.EscapeURL(query)}&sort=-match_date&limit=5";

            using (UnityWebRequest r = UnityWebRequest.Get(url))
            {
                r.SetRequestHeader("X-App-Id", appId);
                r.SetRequestHeader("Authorization", "Bearer " + authToken);
                r.certificateHandler = new BypassCertificate();
                yield return r.SendWebRequest();
                if (r.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var ja = JArray.Parse(r.downloadHandler.text);
                        _recentMatches.Clear();

                        foreach (var item in ja.OfType<JObject>())
                        {
                            int pos = (int)(item["finish_position"]?.Value<float>() ?? 0);
                            int adj = (int)(item["adjusted_match_score"]?.Value<float>() ?? 0);
                            int par = (int)(item["score_vs_par"]?.Value<float>() ?? 0);
                            int mmr = (int)(item["mmr_change"]?.Value<float>() ?? 0);
                            int post = (int)(item["post_match_mmr"]?.Value<float>() ?? 0);
                            string dtRaw = item["match_date"]?.ToString() ?? "";
                            string dateStr = DateTime.TryParse(dtRaw, out DateTime dt) ? dt.ToString("MM/dd") : "??/??";

                            string posStr = GetOrdinal(pos);
                            string mmrColor = mmr >= 0 ? "#55FF55" : "#FF5555";
                            string parSign = par > 0 ? "+" : "";

                            string summary =
                                $"<color=#888888>{dateStr}</color> | <b>Pos: {posStr}</b> | Score: {adj} ({parSign}{par}) | MMR: {post} (<color={mmrColor}>{(mmr >= 0 ? "+" : "")}{mmr}</color>)";

                            _recentMatches.Add(new MatchEntry
                            {
                                match_summary = summary,
                                match_date = dtRaw
                            });
                        }
                    }
                    catch { }
                }
            }
        }

        IEnumerator FetchEvents()
        {
            string appId = UnifiedPlugin.GetCurrentAppId();
            string authToken = UnifiedPlugin.GetCurrentAuthToken();
            string url = $"https://sbgleague.com/api/apps/{appId}/entities/ProSeriesEvent?sort=event_date&limit=50";
            using (UnityWebRequest r = UnityWebRequest.Get(url))
            {
                r.SetRequestHeader("X-App-Id", appId);
                r.SetRequestHeader("Authorization", "Bearer " + authToken);
                r.certificateHandler = new BypassCertificate();
                yield return r.SendWebRequest();
                if (r.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var ja = JArray.Parse(r.downloadHandler.text);
                        _upcomingEvents.Clear();
                        DateTime today = DateTime.Now.Date;
                        foreach (var item in ja)
                        {
                            string dateStr = item["event_date"]?.ToString() ?? "";
                            if (DateTime.TryParse(dateStr, out DateTime dt) && dt.Date >= today)
                            {
                                _upcomingEvents.Add(new ProSeriesEvent
                                {
                                    name = item["name"]?.ToString() ?? "Tournament",
                                    event_date = dateStr
                                });
                                if (_upcomingEvents.Count >= 5) break;
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        IEnumerator ResolvePlayerIdFromName(string playerName)
        {
            string query = "{\"display_name\":{\"$regex\":\"^" + playerName + "$\",\"$options\":\"i\"}}";
            string appId = UnifiedPlugin.GetCurrentAppId();
            string authToken = UnifiedPlugin.GetCurrentAuthToken();
            string url = $"https://sbgleague.com/api/apps/{appId}/entities/MatchEntry?q={UnityWebRequest.EscapeURL(query)}&sort=-match_date&limit=5";
            using (UnityWebRequest r = UnityWebRequest.Get(url))
            {
                r.SetRequestHeader("X-App-Id", appId);
                r.SetRequestHeader("Authorization", "Bearer " + authToken);
                r.certificateHandler = new BypassCertificate();
                yield return r.SendWebRequest();
                if (r.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var res = JArray.Parse(r.downloadHandler.text);
                        if (res.Count > 0)
                        {
                            ResolvedID = res[0]["id"]?.ToString() ?? "None";
                            ResolvedName = res[0]["display_name"]?.ToString() ?? "Not Found";
                            if (ConfigPlayerId == "PASTE_ID_HERE") { ConfigPlayerId = ResolvedID; }
                        }
                        else { ResolvedID = "None"; }
                    }
                    catch { ResolvedID = "None"; }
                }
            }
        }
        

        IEnumerator FetchPlayerData(string id)
        {
            string appId = UnifiedPlugin.GetCurrentAppId();
            string authToken = UnifiedPlugin.GetCurrentAuthToken();
            string url = $"https://sbgleague.com/api/apps/{appId}/entities/Player?q=" + UnityWebRequest.EscapeURL("{\"id\":\"" + id + "\"}");
            using (UnityWebRequest r = UnityWebRequest.Get(url))
            {
                r.SetRequestHeader("X-App-Id", appId);
                r.SetRequestHeader("Authorization", "Bearer " + authToken);
                r.certificateHandler = new BypassCertificate();
                yield return r.SendWebRequest();
                if (r.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var ja = JArray.Parse(r.downloadHandler.text);
                        if (ja.Count == 0) { ResetPlayerData("Invalid ID"); yield break; }
                        var p = ja[0];
                        _activeUsername = p["display_name"]?.ToString() ?? "Player";
                        _playerMMR = p["current_mmr"]?.ToString() ?? "0";
                        _playerPeak = p["highest_mmr_ever"]?.ToString() ?? "0";
                        _matches = p["matches_played"]?.ToString() ?? "0";
                        _lastChange = p["latest_mmr_change"]?.ToString() ?? "0";
                        _top3s = p["top_3_finishes"]?.ToString() ?? "0";
                        float.TryParse(p["average_score_vs_par"]?.ToString() ?? "0", out float avg); _avgScore = avg.ToString("F1");
                        float w = 0, t = 0; float.TryParse(p["wins"]?.ToString(), out w); float.TryParse(p["matches_played"]?.ToString(), out t);
                        _winRate = t > 0 ? $"{(w / t * 100):F0}%" : "0%";
                        if (p["profile_pic_url"] != null && !string.IsNullOrEmpty(p["profile_pic_url"].ToString()))
                            StartCoroutine(DownloadProfilePic(p["profile_pic_url"].ToString()));
                    }
                    catch { ResetPlayerData("Data Error"); }
                }
                else { ResetPlayerData("API Offline"); }
            }
        }

        IEnumerator FetchLeaderboardRank(string id)
        {
            string filter = "%7B%22matches_played%22%3A%7B%22%24gt%22%3A0%7D%7D";
            string appId = UnifiedPlugin.GetCurrentAppId();
            string authToken = UnifiedPlugin.GetCurrentAuthToken();
            string url = $"https://sbgleague.com/api/apps/{appId}/entities/Player?q={filter}&sort=-current_mmr";

            using (UnityWebRequest r = UnityWebRequest.Get(url))
            {
                r.SetRequestHeader("X-App-Id", appId);
                r.SetRequestHeader("Authorization", "Bearer " + authToken);
                r.certificateHandler = new BypassCertificate();

                yield return r.SendWebRequest();

                if (r.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var ps = JArray.Parse(r.downloadHandler.text);
                        _totalPlayers = ps.Count.ToString();

                        int rank = 1;
                        foreach (var p in ps)
                        {
                            if (p["id"]?.ToString() == id)
                            {
                                _playerRank = rank.ToString();
                                break;
                            }
                            rank++;
                        }
                    }
                    catch { }
                }
            }
        }

        IEnumerator GetNetworkTime()
        {
            string url = "https://timeapi.io/api/Time/current/zone?timeZone=UTC";
            using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
            {
                webRequest.timeout = 5;
                yield return webRequest.SendWebRequest();
                if (webRequest.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var json = JObject.Parse(webRequest.downloadHandler.text);
                        string timeStr = json["dateTime"]?.ToString();
                        if (DateTime.TryParse(timeStr, out DateTime dt))
                        {
                            _lastSyncTime = dt.ToLocalTime().ToString("HH:mm:ss");
                            yield break;
                        }
                    }
                    catch { }
                }
            }
            _lastSyncTime = DateTime.Now.ToString("HH:mm:ss") + " (Local)";
        }

        IEnumerator DownloadProfilePic(string url)
        {
            if (string.IsNullOrEmpty(url)) yield break;
            if (url.StartsWith("http://")) url = url.Replace("http://", "https://");
            using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(url))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    _profileIcon.texture = DownloadHandlerTexture.GetContent(request);
                    _profilePicContainer.SetActive(true);
                }
            }
        }

        IEnumerator CheckPluginsRoutine()
        {
            string url = ALLOWED_MODS_URL + "?t=" + DateTime.Now.Ticks;
            using (UnityWebRequest w = UnityWebRequest.Get(url))
            {
                yield return w.SendWebRequest();
                if (w.result == UnityWebRequest.Result.Success)
                {
                    _allowedModsMap.Clear();
                    foreach (string line in w.downloadHandler.text.Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (line.Contains("|")) { var p = line.Split('|'); _allowedModsMap[p[1].Trim()] = p[0].Trim(); }
                        else { _allowedModsMap[line.Trim()] = line.Trim(); }
                    }
                }
            }
        }

        private void UpdateUIReport()
        {
            StringBuilder sb = new StringBuilder();
            _anyIllegalMods = false;
            sb.AppendLine($"User: <color=#FFFFFF>{_activeUsername}</color>");
            sb.AppendLine($"Rank: <color=#00FF00>#{_playerRank}</color> / {_totalPlayers}");
            sb.AppendLine($"Win Rate: <color=#FFA500>{_winRate}</color>");
            float.TryParse(_lastChange, out float delta);
            sb.AppendLine($"MMR: <color=#00FFFF>{_playerMMR}</color> (<color={(delta >= 0 ? "#55FF55" : "#FF5555")}>{(delta >= 0 ? "+" : "")}{_lastChange}</color>)");
            sb.AppendLine($"Avg. Par: <color=#CC88FF>{_avgScore}</color>");
            sb.AppendLine($"Matches: <color=#FFFFFF>{_matches}</color> | Top 3s: <color=#00FF00>{_top3s}</color>");

            var instGuids = Chainloader.PluginInfos.Values.Select(p => p.Metadata.GUID).ToList();
            _missingModNames = _allowedModsMap.Where(kvp => !kvp.Key.ToLower().Contains("compplugincheck") && !instGuids.Contains(kvp.Key)).Select(kvp => kvp.Value).ToList();

            foreach (var plugin in Chainloader.PluginInfos.Values)
            {
                if (plugin.Metadata.GUID.ToLower().Contains("compplugincheck") || plugin.Metadata.GUID.StartsWith("BepInEx")) continue;
                if (!_allowedModsMap.ContainsKey(plugin.Metadata.GUID)) _anyIllegalMods = true;
            }

            if (ConfigShowModList)
            {
                sb.AppendLine("--- <color=#AAAAAA>MY MODS</color> ---");
                foreach (var plugin in Chainloader.PluginInfos.Values)
                {
                    if (plugin.Metadata.GUID.ToLower().Contains("compplugincheck") || plugin.Metadata.GUID.StartsWith("BepInEx")) continue;
                    sb.AppendLine($"{(_allowedModsMap.ContainsKey(plugin.Metadata.GUID) ? "<color=#00FF00>O</color>" : "<color=#FF0000>X</color>")} <size=11>{plugin.Metadata.Name}</size>");
                }
            }

            // --- LOBBY MOD VIEW INJECTION ---
            if (ConfigShowLobbyMods && _remotePlayerMods.Count > 0)
            {
                sb.AppendLine("--- <color=#AAAAAA>LOBBY MODS</color> ---");
                foreach (var player in _remotePlayerMods)
                {
                    sb.AppendLine($"<color=#00FFFF>Player {player.Key.ToString().Substring(0, 5)}</color>:");
                    string[] mods = player.Value.Split(';');
                    foreach (var m in mods)
                    {
                        if (string.IsNullOrEmpty(m)) continue;
                        sb.AppendLine($" <size=10>- {m.Split('|')[0]}</size>");
                    }
                }
            }

            sb.AppendLine("---");
            sb.AppendLine($"<size=10><color=#888888>Sync: {_lastSyncTime} | Status: {_syncStatus}</color></size>");

            if (_statsText != null) _statsText.text = sb.ToString();
            
            // Update debug window content
            UpdateDebugWindow();
            
            // Update warning text UI elements
            if (_illegalWarningText != null) _illegalWarningText.gameObject.SetActive(_anyIllegalMods);
            if (_missingWarningText != null)
            {
                string scene = SceneManager.GetActiveScene().name;
                bool isRange = scene.Contains("Driving") || scene.Contains("Range");
                _missingWarningText.gameObject.SetActive(_missingModNames.Count > 0 && isRange);
                if (_missingWarningText.gameObject.activeSelf) _missingWarningText.text = "<color=yellow>MISSING MODS:</color>\n<size=18>" + string.Join(", ", _missingModNames) + "</size>";
            }
        }
        
        private void UpdateDebugWindow()
        {
            if (_debugWindowText == null || _debugWindowObj == null) return;
            
            if (ConfigShowDebugWindow)
            {
                StringBuilder debugSb = new StringBuilder();
                debugSb.AppendLine("--- DEBUG INFORMATION ---");
                debugSb.AppendLine($"Config Show Debug: {ConfigShowDebugWindow}");
                debugSb.AppendLine($"Remote Players Count: {_remotePlayerMods.Count}");
                debugSb.AppendLine($"Allowed Mods Count: {_allowedModsMap.Count}");
                debugSb.AppendLine($"Missing Mods Count: {_missingModNames.Count}");
                debugSb.AppendLine($"Any Illegal Mods: {_anyIllegalMods}");
                debugSb.AppendLine($"Sync Status: {_syncStatus}");
                debugSb.AppendLine($"Last Sync Time: {_lastSyncTime}");
                debugSb.AppendLine($"Player ID: {ConfigPlayerId}");
                debugSb.AppendLine($"Resolved ID: {ResolvedID}");
                debugSb.AppendLine("--- REMOTE PLAYERS ---");
                
                foreach (var player in _remotePlayerMods)
                {
                    debugSb.AppendLine($"Player {player.Key}: {player.Value}");
                }
                
                _debugWindowText.text = debugSb.ToString();
                _debugWindowObj.SetActive(true);
            }
            else
            {
                _debugWindowObj.SetActive(false);
            }
        }

        private void CreateUI()
        {
            _canvasObj = new GameObject("SBG_Canvas");
            Canvas c = _canvasObj.AddComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 99999;
            _bgObj = new GameObject("BG"); _bgObj.transform.SetParent(_canvasObj.transform, false);
            _bgImage = _bgObj.AddComponent<Image>(); _bgImage.color = new Color(0, 0, 0, ConfigAlpha);
            _bgRect = _bgObj.GetComponent<RectTransform>(); _bgRect.anchorMin = _bgRect.anchorMax = _bgRect.pivot = Vector2.zero;
            VerticalLayoutGroup vlg = _bgObj.AddComponent<VerticalLayoutGroup>(); vlg.padding = new RectOffset(10, 10, 10, 10);
            _bgObj.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _profilePicContainer = new GameObject("PicContainer"); _profilePicContainer.transform.SetParent(_bgObj.transform, false);
            RectTransform picRect = _profilePicContainer.AddComponent<RectTransform>();
            picRect.anchorMin = picRect.anchorMax = picRect.pivot = new Vector2(1, 1); picRect.anchoredPosition = new Vector2(-10, -10); picRect.sizeDelta = new Vector2(42, 42);
            _profilePicContainer.AddComponent<LayoutElement>().ignoreLayout = true; _profilePicContainer.AddComponent<RectMask2D>();

            _profileIcon = new GameObject("I").AddComponent<RawImage>(); _profileIcon.transform.SetParent(_profilePicContainer.transform, false);
            _profileIcon.rectTransform.anchorMin = Vector2.zero; _profileIcon.rectTransform.anchorMax = Vector2.one; _profileIcon.rectTransform.sizeDelta = Vector2.zero;
            _profilePicContainer.SetActive(false);

            _statsText = new GameObject("S").AddComponent<TextMeshProUGUI>(); _statsText.transform.SetParent(_bgObj.transform, false);
            _statsText.fontSize = 13; _statsText.richText = true;

            _warnContainer = new GameObject("WarnContainer"); _warnContainer.transform.SetParent(_canvasObj.transform, false);
            RectTransform warnRect = _warnContainer.AddComponent<RectTransform>();
            warnRect.anchorMin = warnRect.anchorMax = new Vector2(0.5f, 0); warnRect.pivot = new Vector2(0.5f, 0); warnRect.anchoredPosition = new Vector2(0, 15); warnRect.sizeDelta = new Vector2(1000, 200);
            VerticalLayoutGroup warnVlg = _warnContainer.AddComponent<VerticalLayoutGroup>(); warnVlg.childAlignment = TextAnchor.LowerCenter;

            _illegalWarningText = new GameObject("RT").AddComponent<TextMeshProUGUI>(); _illegalWarningText.transform.SetParent(_warnContainer.transform, false);
            _illegalWarningText.text = "ILLEGAL MODS DETECTED"; _illegalWarningText.color = Color.red; _illegalWarningText.fontSize = 32; _illegalWarningText.alignment = TextAlignmentOptions.Center; _illegalWarningText.fontStyle = FontStyles.Bold; _illegalWarningText.gameObject.SetActive(false);

            _missingWarningText = new GameObject("YT").AddComponent<TextMeshProUGUI>(); _missingWarningText.transform.SetParent(_warnContainer.transform, false);
            _missingWarningText.color = Color.yellow; _missingWarningText.fontSize = 18; _missingWarningText.alignment = TextAlignmentOptions.Center; _missingWarningText.gameObject.SetActive(false);

            UnityEngine.Object.DontDestroyOnLoad(_canvasObj);
        }
    }

    [HarmonyPatch]
    public static class MenuTabsPatch
    {
        [HarmonyPostfix][HarmonyPatch(typeof(MenuTabs), "Awake")]
        public static void Postfix(MenuTabs __instance) { SBGLTabManager.Inject(__instance); }
    }

    public static class SBGLTabManager
    {
        private const string TAB_NAME = "SBGL";
        public static void Inject(MenuTabs menuTabs)
        {
            if (menuTabs == null) return;
            Transform optionsTab = menuTabs.transform.Find("Menu/Options Tab");
            GameObject existingPage = ((IEnumerable<GameObject>)menuTabs.tabs).FirstOrDefault(t => t.name == TAB_NAME);

            if (existingPage != null)
            {
                var children = new List<GameObject>();
                foreach (Transform child in existingPage.transform) children.Add(child.gameObject);
                children.ForEach(UnityEngine.Object.DestroyImmediate);
                PopulateSBGLPage(existingPage);
                return;
            }

            Transform controlsButton = optionsTab?.Find("Controls");
            GameObject controlsPage = ((IEnumerable<GameObject>)menuTabs.tabs).FirstOrDefault(t => t.name.Contains("Controls"));
            if (controlsButton != null && controlsPage != null)
            {
                optionsTab.GetComponent<HorizontalLayoutGroup>().spacing = -45f;
                foreach (Button item in menuTabs.tabButtons) item.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
                GameObject buttonObj = UnityEngine.Object.Instantiate(controlsButton.gameObject, optionsTab);
                buttonObj.name = TAB_NAME; SetupButtonText(buttonObj, TAB_NAME);
                GameObject pageObj = UnityEngine.Object.Instantiate(controlsPage, controlsPage.transform.parent);
                pageObj.name = TAB_NAME;
                if (pageObj.GetComponent<ControlsRebind>()) UnityEngine.Object.DestroyImmediate(pageObj.GetComponent<ControlsRebind>());
                foreach (Transform child in pageObj.transform) UnityEngine.Object.DestroyImmediate(child.gameObject);
                PopulateSBGLPage(pageObj);
                menuTabs.tabButtons = menuTabs.tabButtons.Append(buttonObj.GetComponent<Button>()).ToArray();
                menuTabs.tabs = menuTabs.tabs.Append(pageObj).ToArray();
                menuTabs.textColors = menuTabs.textColors.Append(new Color(0.0f, 0.71f, 0.50f)).ToArray();
                int newIndex = menuTabs.tabs.Length - 1;
                buttonObj.GetComponent<Button>().onClick.RemoveAllListeners();
                buttonObj.GetComponent<Button>().onClick.AddListener(() => { menuTabs.SelectTab(newIndex); });
            }
        }

        private static void SetupButtonText(GameObject btn, string txt)
        {
            TextMeshProUGUI tmp = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (tmp != null) tmp.text = txt;
        }

        // Helper Method to generate Text cleanly
        private static GameObject CreateText(Transform parent, string text, Color color, int fontSize, int height)
        {
            GameObject obj = new GameObject("TextInfo", typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<RectTransform>().sizeDelta = new Vector2(600, height);
            TextMeshProUGUI tmp = obj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.color = color;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.richText = true;
            return obj;
        }

        // Helper Method to generate Buttons cleanly
        private static GameObject CreateButton(Transform parent, string text, Color color, int width, int height)
        {
            GameObject obj = new GameObject("SBGButton", typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
            obj.GetComponent<Image>().color = color;

            GameObject txtObj = new GameObject("BtnText", typeof(RectTransform));
            txtObj.transform.SetParent(obj.transform, false);
            txtObj.GetComponent<RectTransform>().anchorMin = Vector2.zero;
            txtObj.GetComponent<RectTransform>().anchorMax = Vector2.one;
            txtObj.GetComponent<RectTransform>().sizeDelta = Vector2.zero;

            TextMeshProUGUI tmp = txtObj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.color = Color.white;
            tmp.fontSize = 18;
            tmp.alignment = TextAlignmentOptions.Center;
            return obj;
        }

        private static void PopulateSBGLPage(GameObject page)
        {
            var plugin = UnityEngine.Object.FindFirstObjectByType<CompetitivePluginCheck>();
            string currentId = (!string.IsNullOrEmpty(plugin?.ResolvedID) && plugin.ResolvedID != "None") ? plugin.ResolvedID : plugin?.ConfigPlayerId;
            bool isLinked = !string.IsNullOrEmpty(currentId) && currentId != "PASTE_ID_HERE" && currentId != "None";

            VerticalLayoutGroup vlg = page.GetComponent<VerticalLayoutGroup>() ?? page.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(60, 60, 20, 20); vlg.spacing = 15f; vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlHeight = vlg.childForceExpandHeight = false;

            CreateText(page.transform, "<b>SBG LEAGUE INTEGRATION</b>", new Color(0.16f, 0.99f, 0.75f), 32, 50);

            if (isLinked)
            {
                CreateText(page.transform, "<color=#222222>--- ACCOUNT ACTIVE ---</color>", Color.black, 18, 30);
                
                var profileBtn = CreateButton(page.transform, "VIEW LEAGUE PROFILE", new Color(0.12f, 0.45f, 0.35f), 350, 45);
                profileBtn.GetComponent<Button>().onClick.AddListener(() => { Application.OpenURL($"https://sbgleague.com/PlayerProfile?id={currentId}"); });
                
                // Create account header with profile picture and user info
                GameObject accountHeaderObj = new GameObject("AccountHeader", typeof(RectTransform), typeof(HorizontalLayoutGroup));
                accountHeaderObj.transform.SetParent(page.transform, false);
                RectTransform headerRect = accountHeaderObj.GetComponent<RectTransform>();
                headerRect.sizeDelta = new Vector2(500, 80);
                
                HorizontalLayoutGroup hlg = accountHeaderObj.GetComponent<HorizontalLayoutGroup>();
                hlg.padding = new RectOffset(0, 0, 5, 5);
                hlg.spacing = 15f;
                hlg.childAlignment = TextAnchor.MiddleCenter;
                hlg.childControlHeight = hlg.childControlWidth = false;
                hlg.childForceExpandHeight = hlg.childForceExpandWidth = false;
                
                // Add profile picture
                Texture2D profilePic = UnifiedPlugin.GetProfilePicture();
                if (profilePic != null)
                {
                    GameObject picObj = new GameObject("ProfilePic", typeof(RectTransform), typeof(RawImage));
                    picObj.transform.SetParent(accountHeaderObj.transform, false);
                    RawImage picImage = picObj.GetComponent<RawImage>();
                    picImage.texture = profilePic;
                    RectTransform picRect = picObj.GetComponent<RectTransform>();
                    picRect.sizeDelta = new Vector2(65, 65);
                }
                
                // Add username/ID text
                GameObject textObj = new GameObject("UserInfo", typeof(RectTransform), typeof(TextMeshProUGUI));
                textObj.transform.SetParent(accountHeaderObj.transform, false);
                RectTransform textRect = textObj.GetComponent<RectTransform>();
                textRect.sizeDelta = new Vector2(300, 80);
                
                TextMeshProUGUI userText = textObj.GetComponent<TextMeshProUGUI>();
                userText.text = $"<b><size=32>{plugin?.ResolvedName ?? "Found"}</size></b>\n<color=#555555><size=18>ID: {currentId}</size></color>";
                userText.alignment = TextAlignmentOptions.Center;
                userText.verticalAlignment = VerticalAlignmentOptions.Middle;
                userText.color = Color.black;
                userText.enableWordWrapping = false;

                // RECENT MATCHES
                CreateText(page.transform, "<b>RECENT MATCH HISTORY</b>", new Color(0.12f, 0.65f, 0.55f), 24, 40);
                if (plugin != null && plugin._recentMatches != null && plugin._recentMatches.Count > 0)
                {
                    foreach (var m in plugin._recentMatches)
                    {
                        var lineObj = CreateText(page.transform, m.match_summary ?? "", Color.black, 14, 25);
                        var tmp = lineObj.GetComponent<TextMeshProUGUI>();
                        if (tmp != null)
                        {
                            tmp.textWrappingMode = TextWrappingModes.NoWrap;
                            tmp.overflowMode = TextOverflowModes.Overflow;
                            tmp.alignment = TextAlignmentOptions.Center;
                            tmp.raycastTarget = false;
                        }
                    }
                }
                else CreateText(page.transform, "No matches found.", Color.gray, 14, 25);

                // PRO SERIES EVENTS
                CreateText(page.transform, "<b>PRO SERIES EVENTS</b>", new Color(0.12f, 0.65f, 0.55f), 24, 40);
                if (plugin != null && plugin._upcomingEvents != null && plugin._upcomingEvents.Count > 0)
                {
                    foreach (var e in plugin._upcomingEvents)
                    {
                        string d = DateTime.TryParse(e.event_date, out DateTime dt) ? dt.ToString("MM/dd") : "";
                        CreateText(page.transform, $"<color=#111111>{e.name}</color> - <size=14>{d}</size>", Color.black, 18, 25);
                    }
                }
                else CreateText(page.transform, "No upcoming events scheduled.", Color.gray, 16, 25);

                // Refresh Button
                CreateButton(page.transform, "REFRESH / AUTO-DETECT", new Color(0.2f, 0.2f, 0.25f), 350, 40).GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (plugin != null) { plugin.ConfigPlayerId = "PASTE_ID_HERE"; plugin.ResolvedID = "None"; plugin.TriggerManualSync(); }
                });
            }
            else
            {
                // Manual Setup Logic
                CreateText(page.transform, "<color=#444444>--- MANUAL SETUP REQUIRED ---</color>", Color.black, 18, 30);
                CreateButton(page.transform, "FIND MY PLAYER ID", new Color(0.12f, 0.12f, 0.12f), 350, 45).GetComponent<Button>().onClick.AddListener(() => Application.OpenURL("https://sbgleague.com/AccountSettings"));
                
                GameObject inputBase = new GameObject("Box", typeof(RectTransform), typeof(Image));
                inputBase.transform.SetParent(page.transform, false);
                inputBase.GetComponent<Image>().color = new Color(0.05f, 0.05f, 0.05f, 1f); 
                inputBase.GetComponent<RectTransform>().sizeDelta = new Vector2(350, 50);
                
                GameObject tObj = new GameObject("T", typeof(RectTransform));
                tObj.transform.SetParent(inputBase.transform, false);
                TextMeshProUGUI tmp = tObj.AddComponent<TextMeshProUGUI>(); 
                tmp.fontSize = 20; tmp.color = Color.white; tmp.alignment = TextAlignmentOptions.Center;
                
                TMP_InputField input = inputBase.AddComponent<TMP_InputField>();
                input.textComponent = tmp; 
                input.text = plugin != null ? plugin.ConfigPlayerId : "";

                CreateButton(page.transform, "SAVE & SYNC PROFILE", new Color(0.1f, 0.6f, 0.45f), 400, 55).GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (plugin != null) { plugin.ConfigPlayerId = input.text; plugin.TriggerManualSync(); }
                });
            } 




            // PRO SERIES EVENTS SECTION (Finished logic)
            CreateText(page.transform, "<b>PRO SERIES EVENTS</b>", new Color(0.12f, 0.65f, 0.55f), 24, 40);
            if (plugin != null && plugin._upcomingEvents.Count > 0)
            {
                foreach (var e in plugin._upcomingEvents)
                {
                    string d = DateTime.TryParse(e.event_date, out DateTime dt) ? dt.ToString("MM/dd") : "";
                    CreateText(page.transform, $"<color=#111111>{e.name}</color> - <size=14>{d}</size>", Color.black, 18, 25);
                }
            }
            else
            {
                CreateText(page.transform, "No upcoming events scheduled.", Color.gray, 16, 25);
            }
        }
    }
}

