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
using System.IO;
using System.Security.Cryptography;
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
        private ConfigEntry<float> _configCompliancePanelX, _configCompliancePanelY;
        private ConfigEntry<bool> _configHideUIWindow, _configShowModList, _configShowDebugWindow;
        private ConfigEntry<bool> _configMelonLoaderChatEnabled;
        private ConfigEntry<string> _configPlayerId;
        private const string ALLOWED_MODS_URL = "https://gist.githubusercontent.com/Kingcox22/59765f02af8dd87179ca920409ff3b27/raw/0d83e319856c884a802644864261f526888e05b1/Approved_Mods.json";

        // --- NETWORKING CONSTANTS ---
        private const int SBGL_NET_CHANNEL = 2622;
        private Dictionary<ulong, string> _remotePlayerMods = new Dictionary<ulong, string>();
        private Dictionary<ulong, string> _playerDisplayNames = new Dictionary<ulong, string>();
        
        // --- PLAYER COMPLIANCE TRACKING ---
        private class PlayerComplianceStatus
        {
            public ulong SteamId { get; set; }
            public bool HasReportedMods { get; set; }
            public bool IsCompliant { get; set; } // Has ⚡SBGL.UnifiedMod
            public bool HasMelonLoader { get; set; }
            public string ModList { get; set; }
            public float FirstSeenTime { get; set; }
        }

        private class LocalPluginScanResult
        {
            public List<string> MissingModNames { get; } = new List<string>();
            public List<string> TamperedModNames { get; } = new List<string>();
            public List<string> SuspiciousRuntimeAssemblies { get; } = new List<string>();
            public bool HasIllegalMods { get; set; }
        }

        private sealed class AllowedModsSnapshot
        {
            // allowedHashesByGuid: null value = guid present in manifest but no hash constraints (legacy text format)
            // non-null value = set of acceptable sha256 hex strings for that guid's assemblies
            public static readonly AllowedModsSnapshot Empty = new AllowedModsSnapshot(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                null);

            private readonly HashSet<string> _allowedGuids;
            private readonly Dictionary<string, string> _displayNamesByGuid;
            private readonly Dictionary<string, HashSet<string>> _allowedHashesByGuid; // null = no hash enforcement

            private AllowedModsSnapshot(
                HashSet<string> allowedGuids,
                Dictionary<string, string> displayNamesByGuid,
                Dictionary<string, HashSet<string>> allowedHashesByGuid)
            {
                _allowedGuids = allowedGuids;
                _displayNamesByGuid = displayNamesByGuid;
                _allowedHashesByGuid = allowedHashesByGuid;
            }

            public int Count => _displayNamesByGuid.Count;
            public bool HasHashConstraints => _allowedHashesByGuid != null;

            public IEnumerable<string> DisplayNames => _displayNamesByGuid.Values;

            public bool ContainsGuid(string guid)
            {
                return !string.IsNullOrWhiteSpace(guid) && _allowedGuids.Contains(guid);
            }

            /// <summary>Returns the set of allowed SHA-256 hashes for the given GUID, or null if no constraints.</summary>
            public HashSet<string> GetAllowedHashes(string guid)
            {
                if (_allowedHashesByGuid == null || string.IsNullOrWhiteSpace(guid)) return null;
                _allowedHashesByGuid.TryGetValue(guid, out var hashes);
                return hashes;
            }

            public IEnumerable<string> GetMissingModNames(HashSet<string> installedGuids)
            {
                foreach (var entry in _displayNamesByGuid)
                {
                    if (ShouldIgnorePluginGuid(entry.Key) || installedGuids.Contains(entry.Key)) continue;
                    yield return entry.Value;
                }
            }

            /// <summary>Parses the new JSON manifest format (version 1).</summary>
            public static AllowedModsSnapshot FromJson(string json)
            {
                var allowedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var displayNamesByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var allowedHashesByGuid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    var root = JObject.Parse(json);
                    var mods = root["mods"] as JArray;
                    if (mods == null) return Empty;

                    foreach (var mod in mods)
                    {
                        string guid = mod["guid"]?.Value<string>();
                        string name = mod["name"]?.Value<string>();
                        if (string.IsNullOrWhiteSpace(guid)) continue;

                        allowedGuids.Add(guid);
                        displayNamesByGuid[guid] = string.IsNullOrWhiteSpace(name) ? guid : name;

                        var assemblies = mod["assemblies"] as JArray;
                        if (assemblies != null && assemblies.Count > 0)
                        {
                            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var asm in assemblies)
                            {
                                string sha256 = asm["sha256"]?.Value<string>();
                                if (!string.IsNullOrWhiteSpace(sha256))
                                    hashes.Add(sha256);
                            }
                            if (hashes.Count > 0)
                                allowedHashesByGuid[guid] = hashes;
                        }
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[CompCheck] Failed to parse approved mods JSON manifest: {ex.Message}");
                    return Empty;
                }

                return new AllowedModsSnapshot(allowedGuids, displayNamesByGuid, allowedHashesByGuid);
            }

            /// <summary>Parses the legacy pipe-delimited text format (fallback).</summary>
            public static AllowedModsSnapshot FromText(string rawText)
            {
                var allowedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var displayNamesByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (string line in rawText.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string guid;
                    string displayName;

                    if (line.Contains("|"))
                    {
                        var parts = line.Split('|');
                        if (parts.Length < 2) continue;
                        displayName = parts[0].Trim();
                        guid = parts[1].Trim();
                    }
                    else
                    {
                        guid = line.Trim();
                        displayName = guid;
                    }

                    if (string.IsNullOrWhiteSpace(guid)) continue;

                    allowedGuids.Add(guid);
                    displayNamesByGuid[guid] = string.IsNullOrWhiteSpace(displayName) ? guid : displayName;
                }

                // No hash constraints when loading legacy text format
                return new AllowedModsSnapshot(allowedGuids, displayNamesByGuid, null);
            }

            /// <summary>Auto-detects JSON vs legacy text and parses accordingly.</summary>
            public static AllowedModsSnapshot Parse(string rawText)
            {
                if (string.IsNullOrWhiteSpace(rawText)) return Empty;
                string trimmed = rawText.TrimStart();
                return trimmed.StartsWith("{") ? FromJson(trimmed) : FromText(rawText);
            }
        }

        private Dictionary<ulong, PlayerComplianceStatus> _playerComplianceStatus = new Dictionary<ulong, PlayerComplianceStatus>();
        private HashSet<ulong> _playersNotifiedAbout = new HashSet<ulong>(); // Track players we've already notified
        private HashSet<ulong> _knownPeers = new HashSet<ulong>(); // All peers we've discovered (from P2P or Mirror)
        private Dictionary<ulong, float> _playerFirstSeenTime = new Dictionary<ulong, float>(); // When we first saw each player in lobby
        private const float COMPLIANCE_TIMEOUT = 15f; // Mark as non-compliant if no report received after 15 seconds
        private bool _hasLoggedDiscoveryInfo = false; // Only log discovery info once per scene
        private string _lastDiscoveryScene = ""; // Track the last scene where we discovered players
        private float _lastMelonLoaderAnnouncementTime = -100f; // Track when we last announced MelonLoader (init to far past)
        // Lobby name captured in real-time by Harmony patch on BNetworkManager.set_LobbyName
        internal static string _currentLobbyName = "";
        private int _lastPlayerCosmeticsCount = 0; // Track count of PlayerCosmetics to detect when players join/leave
        private Rect _compliancePanelRect;
        private bool _compliancePanelRectInit = false;
        private GUIStyle _compWindowStyle;

#pragma warning disable CS0649, CS0169
        private GameObject _canvasObj, _profilePicContainer, _bgObj, _warnContainer, _debugWindowObj;
        private TextMeshProUGUI _statsText, _illegalWarningText, _missingWarningText, _debugWindowText;
        private Image _bgImage, _debugWindowBg;
        private RawImage _profileIcon;
        private RectTransform _bgRect, _debugWindowRect;
#pragma warning restore CS0649, CS0169

        private static readonly HashSet<string> IgnoredPluginGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BepInEx",
            "com.sbgl.unified"
        };

        private AllowedModsSnapshot _allowedModsSnapshot = AllowedModsSnapshot.Empty;
        private readonly HashSet<string> _observedRuntimeAssemblyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Steamworks.Data.Lobby _currentLobby;
        private bool _inLobby = false;
        public List<ProSeriesEvent> _upcomingEvents = new List<ProSeriesEvent>();
        public List<MatchEntry> _recentMatches = new List<MatchEntry>();

        private bool _isSyncing = false;
        private float _timeUntilNextUpdate = 0f;
        private string _lastSyncTime = "Never";

        public string _activeUsername = "Searching...", _playerRank = "N/A", _totalPlayers = "0", _playerMMR = "0", _matches = "0", _playerPeak = "0", _winRate = "0%", _lastChange = "0", _top3s = "0", _avgScore = "0.0", _syncStatus = "Idle";

        public string ResolvedName = "Not Found";
        public string ResolvedID = "None";

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }

        private static string TryGetAssemblyLocation(Assembly assembly)
        {
            if (assembly == null) return string.Empty;
            try
            {
                return NormalizePath(assembly.Location);
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsTrackedRuntimeAssemblyPath(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath)) return false;

            string normalizedPath = NormalizePath(assemblyPath);
            string pluginsRoot = NormalizePath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "plugins"));

            if (normalizedPath.StartsWith(pluginsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalizedPath.IndexOf(Path.DirectorySeparatorChar + "Cheats" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void TrackRuntimeAssembly(Assembly assembly)
        {
            string assemblyPath = TryGetAssemblyLocation(assembly);
            if (!IsTrackedRuntimeAssemblyPath(assemblyPath)) return;
            _observedRuntimeAssemblyPaths.Add(assemblyPath);
        }

        private void CaptureLoadedRuntimeAssemblies()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                TrackRuntimeAssembly(assembly);
            }
        }

        private void OnRuntimeAssemblyLoaded(object sender, AssemblyLoadEventArgs args)
        {
            TrackRuntimeAssembly(args.LoadedAssembly);
        }

        private sealed class KnownVisibleRuntimeAssemblies
        {
            public HashSet<string> ExactAssemblyPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AllowedCompanionAssemblyNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static string TryGetAssemblySimpleName(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath)) return string.Empty;

            try
            {
                return AssemblyName.GetAssemblyName(assemblyPath).Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ComputeSha256Hex(string filePath)
        {
            try
            {
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private KnownVisibleRuntimeAssemblies BuildKnownVisibleRuntimeAssemblies()
        {
            var knownAssemblies = new KnownVisibleRuntimeAssemblies();

            foreach (var plugin in Chainloader.PluginInfos.Values)
            {
                try
                {
                    var instanceProperty = plugin.GetType().GetProperty("Instance", BindingFlags.Public | BindingFlags.Instance);
                    var instance = instanceProperty?.GetValue(plugin);
                    var pluginAssembly = instance?.GetType().Assembly;
                    var assemblyPath = TryGetAssemblyLocation(pluginAssembly);
                    if (string.IsNullOrWhiteSpace(assemblyPath)) continue;

                    knownAssemblies.ExactAssemblyPaths.Add(assemblyPath);
                    foreach (var referencedAssembly in pluginAssembly.GetReferencedAssemblies())
                    {
                        if (!string.IsNullOrWhiteSpace(referencedAssembly.Name))
                        {
                            knownAssemblies.AllowedCompanionAssemblyNames.Add(referencedAssembly.Name);
                        }
                    }
                }
                catch
                {
                }
            }

            return knownAssemblies;
        }

        private static bool ShouldIgnorePluginGuid(string guid)
        {
            return !string.IsNullOrWhiteSpace(guid) && IgnoredPluginGuids.Contains(guid);
        }

        private bool IsAllowedPluginGuid(string guid)
        {
            return _allowedModsSnapshot.ContainsGuid(guid);
        }

        private LocalPluginScanResult BuildLocalPluginScanResult()
        {
            var result = new LocalPluginScanResult();
            var installedGuids = new HashSet<string>(
                Chainloader.PluginInfos.Values.Select(plugin => plugin.Metadata.GUID).Where(guid => !string.IsNullOrWhiteSpace(guid)),
                StringComparer.OrdinalIgnoreCase);
            var knownVisibleAssemblies = BuildKnownVisibleRuntimeAssemblies();

            result.MissingModNames.AddRange(_allowedModsSnapshot.GetMissingModNames(installedGuids));

            foreach (var plugin in Chainloader.PluginInfos.Values)
            {
                if (ShouldIgnorePluginGuid(plugin.Metadata.GUID)) continue;
                if (!IsAllowedPluginGuid(plugin.Metadata.GUID))
                {
                    result.HasIllegalMods = true;
                    break;
                }

                // Hash verification: if the manifest has hash constraints for this GUID, the installed DLL must match
                if (_allowedModsSnapshot.HasHashConstraints)
                {
                    var allowedHashes = _allowedModsSnapshot.GetAllowedHashes(plugin.Metadata.GUID);
                    if (allowedHashes != null && allowedHashes.Count > 0)
                    {
                        try
                        {
                            var instanceProperty = plugin.GetType().GetProperty("Instance", BindingFlags.Public | BindingFlags.Instance);
                            var instance = instanceProperty?.GetValue(plugin);
                            var assemblyPath = TryGetAssemblyLocation(instance?.GetType().Assembly);
                            if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
                            {
                                string actualHash = ComputeSha256Hex(assemblyPath);
                                if (!string.IsNullOrWhiteSpace(actualHash) && !allowedHashes.Contains(actualHash))
                                {
                                    result.TamperedModNames.Add(plugin.Metadata.Name);
                                    result.HasIllegalMods = true;
                                }
                            }
                        }
                        catch
                        {
                            // If we can't read the assembly, treat it as suspicious
                        }
                    }
                }
            }

            foreach (var assemblyPath in _observedRuntimeAssemblyPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (knownVisibleAssemblies.ExactAssemblyPaths.Contains(assemblyPath)) continue;

                string assemblySimpleName = TryGetAssemblySimpleName(assemblyPath);
                bool isAllowedCompanion = !assemblyPath.Contains(Path.DirectorySeparatorChar + "Cheats" + Path.DirectorySeparatorChar)
                    && !string.IsNullOrWhiteSpace(assemblySimpleName)
                    && knownVisibleAssemblies.AllowedCompanionAssemblyNames.Contains(assemblySimpleName);

                if (isAllowedCompanion) continue;

                result.SuspiciousRuntimeAssemblies.Add(assemblyPath.Replace(NormalizePath(AppDomain.CurrentDomain.BaseDirectory) + Path.DirectorySeparatorChar, string.Empty));
                result.HasIllegalMods = true;
            }

            return result;
        }

        // Config value accessors using ConfigEntry references
        private float ConfigX { get => _configX?.Value ?? PlayerPrefs.GetFloat("CompCheck_X", 20f); }
        private float ConfigY { get => _configY?.Value ?? PlayerPrefs.GetFloat("CompCheck_Y", 100f); }
        private float ConfigWidth { get => _configWidth?.Value ?? PlayerPrefs.GetFloat("CompCheck_Width", 200f); }
        private float ConfigAlpha { get => _configAlpha?.Value ?? PlayerPrefs.GetFloat("CompCheck_Alpha", 0.85f); }
        private float ConfigCompliancePanelX { get => _configCompliancePanelX?.Value ?? PlayerPrefs.GetFloat("CompCheck_ComplianceX", Screen.width - 420f); set { if (_configCompliancePanelX != null) _configCompliancePanelX.Value = value; } }
        private float ConfigCompliancePanelY { get => _configCompliancePanelY?.Value ?? PlayerPrefs.GetFloat("CompCheck_ComplianceY", Screen.height - 350f); set { if (_configCompliancePanelY != null) _configCompliancePanelY.Value = value; } }
        private bool ConfigHideUIWindow { get => _configHideUIWindow?.Value ?? (PlayerPrefs.GetInt("CompCheck_HideUIWindow", 0) == 1); }
        private bool ConfigShowModList { get => _configShowModList?.Value ?? (PlayerPrefs.GetInt("CompCheck_ShowModList", 0) == 1); set { if (_configShowModList != null) _configShowModList.Value = value; } }
        private bool ConfigShowDebugWindow { get => _configShowDebugWindow?.Value ?? (PlayerPrefs.GetInt("CompCheck_ShowDebugWindow", 0) == 1); set { if (_configShowDebugWindow != null) _configShowDebugWindow.Value = value; } }
        internal string ConfigPlayerId { get => _configPlayerId?.Value ?? PlayerPrefs.GetString("CompCheck_PlayerId", "PASTE_ID_HERE"); set { if (_configPlayerId != null) _configPlayerId.Value = value; } }
        private bool ConfigMelonLoaderChatEnabled { get => _configMelonLoaderChatEnabled?.Value ?? false; }

        // Set config entries from parent plugin
        public void SetConfig(ConfigEntry<float> x, ConfigEntry<float> y, ConfigEntry<float> width, ConfigEntry<float> alpha,
            ConfigEntry<bool> hideUIWindow, ConfigEntry<bool> showModList, ConfigEntry<bool> showDebugWindow,
            ConfigEntry<float> updateInterval, ConfigEntry<string> playerId,
            ConfigEntry<float> compliancePanelX = null, ConfigEntry<float> compliancePanelY = null,
            ConfigEntry<bool> melonLoaderChatEnabled = null)
        {
            _configX = x;
            _configY = y;
            _configWidth = width;
            _configAlpha = alpha;
            _configHideUIWindow = hideUIWindow;
            _configShowModList = showModList;
            _configShowDebugWindow = showDebugWindow;
            _configUpdateInterval = updateInterval;
            _configPlayerId = playerId;
            _configCompliancePanelX = compliancePanelX;
            _configCompliancePanelY = compliancePanelY;
            _configMelonLoaderChatEnabled = melonLoaderChatEnabled;
        }

        void OnEnable()
        {
            try { Steamworks.SteamMatchmaking.OnLobbyEntered += OnLobbyEntered; } catch { }
            try { Steamworks.SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberJoined; } catch { }
            try { Steamworks.SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberLeave; } catch { }
            try { Steamworks.SteamNetworking.OnP2PSessionRequest += OnP2PSessionRequest; } catch { }
        }

        void OnDisable()
        {
            try { Steamworks.SteamMatchmaking.OnLobbyEntered -= OnLobbyEntered; } catch { }
            try { Steamworks.SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberJoined; } catch { }
            try { Steamworks.SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberLeave; } catch { }
            try { Steamworks.SteamNetworking.OnP2PSessionRequest -= OnP2PSessionRequest; } catch { }
        }

        private void OnP2PSessionRequest(Steamworks.SteamId remoteId)
        {
            // Auto-accept from anyone in our current lobby
            if (_inLobby && _currentLobby.Members.Any(m => m.Id == remoteId))
            {
                Steamworks.SteamNetworking.AcceptP2PSessionWithUser(remoteId);
                if (!_playerDisplayNames.ContainsKey(remoteId.Value))
                {
                    // Record display name from lobby member list
                    foreach (var member in _currentLobby.Members)
                    {
                        if (member.Id == remoteId)
                        {
                            _playerDisplayNames[remoteId.Value] = member.Name;
                            break;
                        }
                    }
                }
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Accepted P2P session request from {remoteId.Value}");
            }
        }

        private void OnLobbyEntered(Steamworks.Data.Lobby lobby)
        {
            _currentLobby = lobby;
            _inLobby = true;
            float now = Time.time;
            // Capture lobby name from Steam metadata — available to all players (host and non-host)
            string steamLobbyName = TryReadLobbyName(lobby);
            if (!string.IsNullOrWhiteSpace(steamLobbyName))
                _currentLobbyName = steamLobbyName;
            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ OnLobbyEntered: {lobby.Id.Value}, member count: {lobby.MemberCount}, lobby name: '{steamLobbyName}'");
            foreach (var member in lobby.Members)
            {
                if (member.Id.Value == (ulong)Steamworks.SteamClient.SteamId) continue;
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Lobby member: {member.Name} ({member.Id.Value})");
                _playerDisplayNames[member.Id.Value] = member.Name;
                AddPlayerToTracking(member.Id.Value, now);
            }

            // If the name wasn't available immediately (common for non-hosts), retry with backoff.
            // Steam metadata propagates asynchronously — retrying a few times covers this gap.
            if (string.IsNullOrWhiteSpace(steamLobbyName))
                StartCoroutine(RetryResolveLobbyName(lobby));
        }

        private static string TryReadLobbyName(Steamworks.Data.Lobby lobby)
        {
            // The game may store the name under any of these keys depending on version.
            string[] keys = { "name", "Name", "lobby_name", "LobbyName", "server_name" };
            foreach (var key in keys)
            {
                try
                {
                    string v = lobby.GetData(key);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                catch { }
            }
            return string.Empty;
        }

        private System.Collections.IEnumerator RetryResolveLobbyName(Steamworks.Data.Lobby lobby)
        {
            float[] delays = { 0.5f, 1.5f, 3.0f, 6.0f };
            foreach (float delay in delays)
            {
                yield return new WaitForSeconds(delay);
                string name = TryReadLobbyName(lobby);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _currentLobbyName = name;
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Lobby name resolved (retry +{delay}s): '{name}'");
                    yield break;
                }
            }
            UnityEngine.Debug.LogWarning("[SBGL-CompPluginCheck] Lobby name still blank after all retries (non-host path).");
        }

        private void OnLobbyMemberJoined(Steamworks.Data.Lobby lobby, Steamworks.Friend friend)
        {
            if (friend.Id.Value == (ulong)Steamworks.SteamClient.SteamId) return;
            float now = Time.time;
            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Lobby member joined: {friend.Name} ({friend.Id.Value})");
            _playerDisplayNames[friend.Id.Value] = friend.Name;
            AddPlayerToTracking(friend.Id.Value, now);
        }

        private void OnLobbyMemberLeave(Steamworks.Data.Lobby lobby, Steamworks.Friend friend)
        {
            ulong id = friend.Id.Value;
            if (id == (ulong)Steamworks.SteamClient.SteamId)
            {
                // We left — clear all lobby state
                _inLobby = false;
                _playerComplianceStatus.Clear();
                _playerFirstSeenTime.Clear();
                _knownPeers.Clear();
                _playersNotifiedAbout.Clear();
                _playerDisplayNames.Clear();
                _remotePlayerMods.Clear();
                _hasLoggedDiscoveryInfo = false;
                PlayerPrefs.SetString("LobbyName", ""); // prevent stale name from triggering chat spam
                UnityEngine.Debug.Log("[SBGL-CompPluginCheck] Left lobby — cleared all player tracking");
            }
            else
            {
                // Another player left — remove them
                _playerComplianceStatus.Remove(id);
                _playerFirstSeenTime.Remove(id);
                _knownPeers.Remove(id);
                _playersNotifiedAbout.Remove(id);
                _playerDisplayNames.Remove(id);
                _remotePlayerMods.Remove(id);
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Player left lobby: {friend.Name} ({id}) — removed from tracking");
            }
        }

        void Awake()
        {
            // Initialize configuration values from PlayerPrefs with defaults
            if (!PlayerPrefs.HasKey("CompCheck_X")) PlayerPrefs.SetFloat("CompCheck_X", 20f);
            if (!PlayerPrefs.HasKey("CompCheck_Y")) PlayerPrefs.SetFloat("CompCheck_Y", 100f);
            if (!PlayerPrefs.HasKey("CompCheck_Width")) PlayerPrefs.SetFloat("CompCheck_Width", 200f);
            if (!PlayerPrefs.HasKey("CompCheck_Alpha")) PlayerPrefs.SetFloat("CompCheck_Alpha", 0.85f);
            if (!PlayerPrefs.HasKey("CompCheck_UpdateInterval")) PlayerPrefs.SetFloat("CompCheck_UpdateInterval", 5f);
            if (!PlayerPrefs.HasKey("CompCheck_HideUIWindow")) PlayerPrefs.SetInt("CompCheck_HideUIWindow", 0);
            if (!PlayerPrefs.HasKey("CompCheck_ShowModList")) PlayerPrefs.SetInt("CompCheck_ShowModList", 0);
            if (!PlayerPrefs.HasKey("CompCheck_ShowDebugWindow")) PlayerPrefs.SetInt("CompCheck_ShowDebugWindow", 0);
            if (!PlayerPrefs.HasKey("CompCheck_PlayerId")) PlayerPrefs.SetString("CompCheck_PlayerId", "PASTE_ID_HERE");
            
            // Startup check for MelonLoader (runs once at initialization)
            if (HasMelonLoaderLoaded())
            {
                UnityEngine.Debug.LogError("[SBGL-CompPluginCheck] ⚠️⚠️⚠️ STARTUP DETECTION: MELONLOADER IS LOADED ⚠️⚠️⚠️");
                // Will be announced every 10 seconds in Update() loop
            }
            
            // Subscribe to API configuration changes
            UnifiedPlugin.ApiConfigChanged += OnApiConfigChanged;
            AppDomain.CurrentDomain.AssemblyLoad += OnRuntimeAssemblyLoaded;
            CaptureLoadedRuntimeAssemblies();
            
            StartCoroutine(GetNetworkTime());
        }

        void OnDestroy()
        {
            UnifiedPlugin.ApiConfigChanged -= OnApiConfigChanged;
            AppDomain.CurrentDomain.AssemblyLoad -= OnRuntimeAssemblyLoaded;
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

            if (_canvasObj != null)
            {
                _canvasObj.SetActive(!ConfigHideUIWindow);
            }

            // Update debug window visibility based on config
            if (_debugWindowObj != null)
            {
                _debugWindowObj.SetActive(ConfigShowDebugWindow);
            }

            // --- NETWORKING LOGIC IN UPDATE ---
            // Check if player count has changed - if so, discover immediately (don't wait for 5-second timer)
            int currentPlayerCount = 0;
            try
            {
                currentPlayerCount = UnityEngine.Object.FindObjectsByType<PlayerCosmetics>(UnityEngine.FindObjectsSortMode.None).Length;
            }
            catch { }
            
            if (currentPlayerCount != _lastPlayerCosmeticsCount)
            {
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] PlayerCosmetics count changed: {_lastPlayerCosmeticsCount} → {currentPlayerCount}, triggering immediate discovery");
                _lastPlayerCosmeticsCount = currentPlayerCount;
                _hasLoggedDiscoveryInfo = false; // Reset flag to allow fresh logging for new players
                DiscoverLobbyPlayers();
            }
            
            // Periodically discover all lobby players (fallback in case count doesn't change)
            if (Time.frameCount % 300 == 0) // Every 5 seconds
            {
                DiscoverLobbyPlayers();
            }
            
            // Announce MelonLoader presence every 10 seconds if detected, but only in SBGL- lobbies
            if (HasMelonLoaderLoaded() && _inLobby)
            {
                float timeSinceLastAnnouncement = Time.time - _lastMelonLoaderAnnouncementTime;
                if (timeSinceLastAnnouncement >= 10f)
                {
                    AnnounceOwnMelonLoaderToChat();
                    _lastMelonLoaderAnnouncementTime = Time.time;
                }
            }
            
            // Accept P2P sessions from all peers we're trying to communicate with
            AcceptIncomingP2PSessions();
            
            ListenForModReports();
            if (Time.frameCount % 600 == 0)
            {
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Frame {Time.frameCount}: Triggering BroadcastMyMods()");
                BroadcastMyMods(); // Broadcast roughly every 10 seconds
            }
        }

        void OnGUI()
        {
            if (!ConfigShowDebugWindow) return;
            if (!SceneManager.GetActiveScene().name.ToLower().Contains("driving range")) return;
            
            // Draw player compliance debug panel
            DrawPlayerComplianceDebugUI();
        }

        private void DrawPlayerComplianceDebugUI()
        {
            if (_compWindowStyle == null)
            {
                _compWindowStyle = new GUIStyle(GUI.skin.box)
                {
                    padding = new RectOffset(12, 12, 12, 12)
                };
            }

            if (!_compliancePanelRectInit)
            {
                float startX = ConfigCompliancePanelX > 0 ? ConfigCompliancePanelX : Screen.width - 480f;
                float startY = ConfigCompliancePanelY > 0 ? ConfigCompliancePanelY : Screen.height - 350f;
                _compliancePanelRect = new Rect(startX, startY, 10, 10);
                _compliancePanelRectInit = true;
            }

            GUI.backgroundColor = new Color(0.07f, 0.07f, 0.07f, 0.95f);
            _compliancePanelRect = GUILayout.Window(42424, _compliancePanelRect, DrawComplianceWindowContent, "",
                _compWindowStyle, GUILayout.MinWidth(300f), GUILayout.MaxWidth(500f), GUILayout.MaxHeight(Screen.height - 40f));
            GUI.backgroundColor = Color.white;
        }

        private void DrawComplianceWindowContent(int windowId)
        {
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            var summaryStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, normal = { textColor = Color.white } };
            var nameStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true, fontSize = 13, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            var modStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true, fontSize = 11,
                normal = { textColor = Color.white }
            };

            GUILayout.Label("PLAYER COMPLIANCE", titleStyle);
            GUILayout.Space(4);

            int total     = _playerComplianceStatus.Count;
            int compliant = _playerComplianceStatus.Values.Count(s => s.IsCompliant && s.HasReportedMods && !s.HasMelonLoader);
            int flagged   = _playerComplianceStatus.Values.Count(s => s.HasReportedMods && (!s.IsCompliant || s.HasMelonLoader));
            int pending   = _playerComplianceStatus.Values.Count(s => !s.HasReportedMods);
            GUILayout.Label(
                $"<color=white>Players: <b>{total}</b></color>    <color=lime>✓ {compliant}</color>    <color=red>✗ {flagged}</color>    <color=yellow>? {pending}</color>",
                summaryStyle
            );

            GUILayout.Space(6);

            if (_playerComplianceStatus.Count == 0)
            {
                GUILayout.Label("Waiting for players...",
                    new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } });
            }
            else
            {
                var allowedNames = new HashSet<string>(_allowedModsSnapshot.DisplayNames.Select(v => v.ToLowerInvariant()));

                foreach (var kvp in _playerComplianceStatus)
                {
                    var status = kvp.Value;
                    string icon, color;

                    if (!status.HasReportedMods)    { icon = "?"; color = "yellow"; }
                    else if (status.HasMelonLoader) { icon = "⚠"; color = "orange"; }
                    else if (status.IsCompliant)    { icon = "✓"; color = "lime"; }
                    else                            { icon = "✗"; color = "red"; }

                    string displayName = _playerDisplayNames.TryGetValue(status.SteamId, out string dn) ? dn : status.SteamId.ToString();
                    GUILayout.Label($"<color={color}>[{icon}]</color>  {displayName}", nameStyle);

                    if (status.HasMelonLoader)
                        GUILayout.Label("      <color=orange>⚠ MelonLoader detected</color>", modStyle);

                    if (_remotePlayerMods.TryGetValue(status.SteamId, out string modList))
                    {
                        foreach (var m in modList.Split(';'))
                        {
                            if (string.IsNullOrEmpty(m) || m.Contains("USER_HAS_MELONLOADER")) continue;
                            string modName = m.Split('|')[0];
                            bool isOurMod = modName.StartsWith("⚡");
                            bool isAllowed = isOurMod || allowedNames.Contains(modName.ToLowerInvariant());
                            string modColor = isAllowed ? "#AAFFAA" : "#FF4444";
                            string modPrefix = isAllowed ? "✓" : "✗";
                            GUILayout.Label($"      <color={modColor}>{modPrefix} {modName}</color>", modStyle);
                        }
                    }

                    GUILayout.Space(5);
                }
            }

            GUI.DragWindow();
        }

        // --- NEW P2P NETWORKING LOGIC ---

        private bool HasMelonLoaderLoaded()
        {
            // Use the shared bridge for consistent detection across all components
            return MelonLoaderBridge.IsMelonLoaderLoaded;
        }

        private void DiscoverLobbyPlayers()
        {
            if (!SteamClient.IsValid) return;
            
            try
            {
                var now = Time.time;
                
                // Check if we've changed scenes - if so, reset discovery flag and clear old players
                string currentScene = SceneManager.GetActiveScene().name;
                if (currentScene != _lastDiscoveryScene)
                {
                    _hasLoggedDiscoveryInfo = false;
                    _lastDiscoveryScene = currentScene;
                    _lastPlayerCosmeticsCount = 0; // Reset player count tracker for new scene
                    
                    // Clear old player tracking for new scene
                    _playerComplianceStatus.Clear();
                    _playerFirstSeenTime.Clear();
                    _knownPeers.Clear();
                    _playersNotifiedAbout.Clear();
                    _playerDisplayNames.Clear();
                    _remotePlayerMods.Clear();
                    
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Scene changed to: {currentScene} - cleared player tracking and resetting discovery");
                }
                
                bool isFirstDiscovery = !_hasLoggedDiscoveryInfo;
                
                // Add ourselves to the tracking immediately with our mod list
                // We know we have the mod since we're running it!
                if (isFirstDiscovery && !_playerComplianceStatus.ContainsKey(SteamClient.SteamId))
                {
                    bool selfHasMelon = HasMelonLoaderLoaded();
                    _playerComplianceStatus[SteamClient.SteamId] = new PlayerComplianceStatus
                    {
                        SteamId = SteamClient.SteamId,
                        FirstSeenTime = now,
                        IsCompliant = true,
                        HasReportedMods = true,
                        HasMelonLoader = selfHasMelon,
                        ModList = "⚡SBGL.UnifiedMod|1.0.0"
                    };
                    if (!_playerFirstSeenTime.ContainsKey(SteamClient.SteamId))
                    {
                        _playerFirstSeenTime[SteamClient.SteamId] = now;
                    }
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Added self ({SteamClient.SteamId}) to tracking as COMPLIANT (MelonLoader={selfHasMelon})");
                    _playerDisplayNames[SteamClient.SteamId] = SteamClient.Name ?? "Me";
                }
                
                // SIMPLE METHOD: Look for all PlayerCosmetics components in the scene
                // These represent all players currently in the game
                PlayerCosmetics[] allCosmeticComponents = new PlayerCosmetics[0];
                try
                {
                    allCosmeticComponents = UnityEngine.Object.FindObjectsByType<PlayerCosmetics>(UnityEngine.FindObjectsSortMode.None);
                    
                    if (isFirstDiscovery)
                    {
                        UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] 🔍 Scene discovery: Found {allCosmeticComponents.Length} PlayerCosmetics components");
                    }
                    
                    foreach (var cosmetics in allCosmeticComponents)
                    {
                        try
                        {
                            var playerObj = cosmetics.gameObject;
                            string objName = playerObj.name;
                            
                            if (isFirstDiscovery)
                            {
                                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]   → PlayerCosmetics object: '{objName}'");
                            }
                            
                            // Try to get Steam ID from the NetworkIdentity's owner
                            var netIdentity = playerObj.GetComponent<Mirror.NetworkIdentity>();
                            if (netIdentity != null)
                            {
                                if (isFirstDiscovery)
                                {
                                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]     → Has NetworkIdentity, checking for owner info");
                                }
                                
                                // Try to get the owner's Steam ID from the connectionToServer or connectionToClient
                                ulong extractedSteamId = 0;
                                bool foundSteamId = false;
                                
                                // Attempt 1: Check if connection has a connectionToClient (server-side)
                                try
                                {
                                    var connToClientProp = typeof(Mirror.NetworkIdentity).GetProperty("connectionToClient", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    if (connToClientProp != null)
                                    {
                                        var connToClient = connToClientProp.GetValue(netIdentity);
                                        if (connToClient != null && isFirstDiscovery)
                                        {
                                            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]       → Has connectionToClient: {connToClient.GetType().Name}");
                                            
                                            // Try to extract Steam ID from connection
                                            var connType = connToClient.GetType();
                                            var authProp = connType.GetProperty("authenticationData", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                            if (authProp != null)
                                            {
                                                var authData = authProp.GetValue(connToClient);
                                                if (authData != null)
                                                {
                                                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]       → Auth data: {authData}");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { }
                                
                                // Attempt 2: Try parsing the object name as Steam ID
                                if (ulong.TryParse(objName, out ulong steamId))
                                {
                                    extractedSteamId = steamId;
                                    foundSteamId = true;
                                    if (isFirstDiscovery)
                                    {
                                        UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]     ✓ Extracted Steam ID from name: {steamId}");
                                    }
                                }
                                
                                if (foundSteamId)
                                {
                                    // Successfully extracted Steam ID
                                    if (extractedSteamId == SteamClient.SteamId)
                                    {
                                        if (isFirstDiscovery)
                                        {
                                            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]     ✓ Skipping self ({extractedSteamId})");
                                        }
                                        continue;
                                    }
                                    
                                    if (isFirstDiscovery)
                                    {
                                        UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]     ✓ Discovered player: {extractedSteamId}");
                                    }
                                    
                                    AddPlayerToTracking(extractedSteamId, now);
                                }
                                else
                                {
                                    // Name is not a Steam ID
                                    if (isFirstDiscovery)
                                    {
                                        UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]     ⚠️ PlayerCosmetics '{objName}' has no Steam ID in name. Logging details...");
                                        
                                        // Log all properties we can find
                                        var niType = netIdentity.GetType();
                                        var ownerIdProp = niType.GetProperty("ownerId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                        if (ownerIdProp != null)
                                        {
                                            var ownerId = ownerIdProp.GetValue(netIdentity);
                                            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]       → ownerId: {ownerId}");
                                        }
                                        
                                        var netIdProp = niType.GetProperty("netId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                        if (netIdProp != null)
                                        {
                                            var netId = netIdProp.GetValue(netIdentity);
                                            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]       → netId: {netId}");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (isFirstDiscovery)
                                {
                                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck]     ⚠️ PlayerCosmetics has no NetworkIdentity");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (isFirstDiscovery)
                            {
                                UnityEngine.Debug.LogError($"[SBGL-CompPluginCheck] Error processing PlayerCosmetics: {ex.Message}");
                            }
                        }
                    }
                    
                    if (isFirstDiscovery)
                    {
                        UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Discovery complete: {_playerComplianceStatus.Count} players added to tracking");
                    }
                }
                catch (Exception ex)
                {
                    if (isFirstDiscovery)
                    {
                        UnityEngine.Debug.LogError($"[SBGL-CompPluginCheck] Error in PlayerCosmetics discovery: {ex.Message}");
                    }
                }
                
                // FALLBACK: Refresh from the current Steam lobby if we have one
                if (_inLobby)
                {
                    try
                    {
                        foreach (var member in _currentLobby.Members)
                        {
                            if (member.Id.Value == 0) continue; // skip invalid/stale entries
                            if (member.Id.Value == (ulong)Steamworks.SteamClient.SteamId) continue;
                            if (!_knownPeers.Contains(member.Id.Value))
                            {
                                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Lobby refresh found new peer: {member.Name} ({member.Id.Value})");
                                AddPlayerToTracking(member.Id.Value, now);
                            }
                            // Always refresh display name — it gets cleared on scene change
                            if (!string.IsNullOrEmpty(member.Name))
                                _playerDisplayNames[member.Id.Value] = member.Name;
                        }
                    }
                    catch (Exception lobbyEx)
                    {
                        if (isFirstDiscovery)
                        {
                            UnityEngine.Debug.LogWarning($"[SBGL-CompPluginCheck] Lobby member refresh error: {lobbyEx.Message}");
                        }
                    }
                }
                else if (isFirstDiscovery)
                {
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ⚠️ Not in a tracked lobby yet - OnLobbyEntered has not fired");
                }
                
                _hasLoggedDiscoveryInfo = true;
                
                // Mark players as having timed out if they haven't reported mods
                var playersToMarkNonCompliant = new List<ulong>();
                
                foreach (var kvp in _playerComplianceStatus)
                {
                    var steamId = kvp.Key;
                    var status = kvp.Value;
                    
                    // Skip if they already reported
                    if (status.HasReportedMods) continue;
                    
                    // Check if enough time has passed since we first saw them
                    if (_playerFirstSeenTime.TryGetValue(steamId, out float firstSeenTime))
                    {
                        if (now - firstSeenTime >= COMPLIANCE_TIMEOUT)
                        {
                            playersToMarkNonCompliant.Add(steamId);
                        }
                    }
                }
                
                // Mark timed-out players as non-compliant (missing mod)
                foreach (var steamId in playersToMarkNonCompliant)
                {
                    var status = _playerComplianceStatus[steamId];
                    if (!status.HasReportedMods)
                    {
                        UnityEngine.Debug.LogWarning($"[SBGL-CompPluginCheck] Player {steamId} timed out without reporting mods - marking as non-compliant");
                        status.IsCompliant = false; // They don't have the mod
                        status.ModList = "(No report received)";
                        SendComplianceNotification(steamId, status);
                        UpdateUIReport();
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[SBGL-CompPluginCheck] Fatal error in DiscoverLobbyPlayers: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        private void AddPlayerToTracking(ulong steamId, float now)
        {
            // Add player to tracking if not already there
            if (!_playerComplianceStatus.ContainsKey(steamId))
            {
                _playerComplianceStatus[steamId] = new PlayerComplianceStatus 
                { 
                    SteamId = steamId,
                    FirstSeenTime = now,
                    IsCompliant = false, // Assume non-compliant until they report
                    HasReportedMods = false
                };
                
                if (!_playerFirstSeenTime.ContainsKey(steamId))
                {
                    _playerFirstSeenTime[steamId] = now;
                }
                
                _knownPeers.Add(steamId);
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Added player {steamId} to tracking (knownPeers={_knownPeers.Count})");
                UpdateUIReport();
            }
        }

        private void AcceptIncomingP2PSessions()
        {
            if (!SteamClient.IsValid) return;
            
            try
            {
                // Accept P2P sessions from all discovered peers
                foreach (var peerId in _playerComplianceStatus.Keys.ToList())
                {
                    if (peerId != SteamClient.SteamId)
                    {
                        try
                        {
                            SteamNetworking.AcceptP2PSessionWithUser(peerId);
                        }
                        catch { }
                    }
                }
                
                // Also accept from peers we've heard from via P2P
                foreach (var peerId in _knownPeers.ToList())
                {
                    if (peerId != SteamClient.SteamId)
                    {
                        try
                        {
                            SteamNetworking.AcceptP2PSessionWithUser(peerId);
                        }
                        catch { }
                    }
                }
                
                // Log SteamClient state for diagnostics
                if (Time.frameCount % 1200 == 0) // Every 20 seconds
                {
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] SteamClient.IsValid={SteamClient.IsValid}, SteamClient.SteamId={SteamClient.SteamId}, Known Peers={_knownPeers.Count}, Tracked Players={_playerComplianceStatus.Count}");
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[SBGL-CompPluginCheck] Error in AcceptIncomingP2PSessions: {ex.Message}");
            }
        }

        private void BroadcastMyMods()
        {
            if (!SteamClient.IsValid)
            {
                UnityEngine.Debug.Log("[SBGL-CompPluginCheck] BroadcastMyMods: SteamClient not valid");
                return;
            }
            try
            {
                StringBuilder sb = new StringBuilder("SBGL_REPORT:");
                
                // Broadcast our own mod signature FIRST so others know we're compliant
                sb.Append("⚡SBGL.UnifiedMod|1.0.0;");
                
                // Check for MelonLoader first
                bool hasMelonLoader = HasMelonLoaderLoaded();
                if (hasMelonLoader)
                {
                    sb.Append("⚠️USER_HAS_MELONLOADER;");
                    // Only log the MelonLoader warning once per session, not every broadcast
                    if (_lastMelonLoaderAnnouncementTime < 0)
                        UnityEngine.Debug.LogWarning("[SBGL-CompPluginCheck] MelonLoader detected on this client.");
                }
                
                foreach (var plugin in Chainloader.PluginInfos.Values)
                {
                    if (ShouldIgnorePluginGuid(plugin.Metadata.GUID)) continue;
                    sb.Append($"{plugin.Metadata.Name}|{plugin.Metadata.Version};");
                }
                
                // Add debug logging
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] BroadcastMyMods (MelonLoader={hasMelonLoader})");

                // Store our own mod list locally so the UI can display it (same format as received reports)
                // Keep the MelonLoader token so the UI renders the warning line
                string selfModList = sb.ToString().Substring("SBGL_REPORT:".Length);
                _remotePlayerMods[SteamClient.SteamId] = selfModList;
                // Also keep compliance status in sync with current MelonLoader state
                if (_playerComplianceStatus.TryGetValue(SteamClient.SteamId, out var selfStatus))
                    selfStatus.HasMelonLoader = hasMelonLoader;
                if (!_playerDisplayNames.ContainsKey(SteamClient.SteamId))
                    _playerDisplayNames[SteamClient.SteamId] = SteamClient.Name ?? "Me";

                byte[] data = Encoding.UTF8.GetBytes(sb.ToString());

                // Collect all known peers
                var peersToSend = new HashSet<ulong>(_knownPeers);
                peersToSend.UnionWith(_remotePlayerMods.Keys);
                
                // Also add peers from active P2P connections
                // Suppress the "Found active NetworkManager" log — not useful in steady state
                
                // Remove self
                peersToSend.Remove(SteamClient.SteamId);
                
                // Only log broadcast details if there are actually peers to send to
                if (peersToSend.Count > 0)
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Broadcasting to {peersToSend.Count} peers");
                
                // Remove the verbose per-send logs; only log send errors
                foreach (var peer in peersToSend)
                {
                    try { SteamNetworking.SendP2PPacket(peer, data, -1, SBGL_NET_CHANNEL, P2PSend.Reliable); }
                    catch (Exception sendEx) { UnityEngine.Debug.LogWarning($"[SBGL-CompPluginCheck] Failed to send to peer {peer}: {sendEx.Message}"); }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[SBGL-CompPluginCheck] Error in BroadcastMyMods: {ex.Message}\n{ex.StackTrace}");
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
                    
                    // Add this peer to our known peers so we broadcast back to them
                    _knownPeers.Add(remoteId.Value);
                    
                    if (msg.StartsWith("SBGL_MATCH_ID:"))
                    {
                        string matchId = msg.Replace("SBGL_MATCH_ID:", "").Trim();
                        SBGLeagueAutomation.MatchResultSubmissionService.HandleIncomingMatchIdBroadcast(matchId);
                        UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Forwarded P2P Match ID from {remoteId.Value}: {matchId}");
                    }
                    else if (msg.StartsWith("SBGL_REPORT:"))
                    {
                        string modList = msg.Replace("SBGL_REPORT:", "");
                        
                        // Create/update compliance status for this player
                        if (!_playerComplianceStatus.ContainsKey(remoteId.Value))
                        {
                            _playerComplianceStatus[remoteId.Value] = new PlayerComplianceStatus { FirstSeenTime = Time.time };
                        }
                        
                        var status = _playerComplianceStatus[remoteId.Value];
                        status.SteamId = remoteId.Value;
                        status.HasReportedMods = true;
                        status.ModList = modList;
                        status.IsCompliant = modList.Contains("⚡SBGL.UnifiedMod");
                        status.HasMelonLoader = modList.Contains("⚠️USER_HAS_MELONLOADER");
                        
                        // Check for MelonLoader alert in the mod list
                        if (status.HasMelonLoader)
                        {
                            UnityEngine.Debug.LogError($"[SBGL-CompPluginCheck] ⚠️⚠️⚠️ ALERT: Player {remoteId.Value} HAS MELONLOADER INSTALLED ⚠️⚠️⚠️");
                            modList = modList.Replace("⚠️USER_HAS_MELONLOADER;", "");
                        }
                        
                        // Check if player is missing the SBGL mod
                        if (!status.IsCompliant)
                        {
                            UnityEngine.Debug.LogError($"[SBGL-CompPluginCheck] ⚠️ MISSING MOD ALERT: Player {remoteId.Value} does NOT have SBGL.UnifiedMod installed! Their mod list: {modList}");
                            SendComplianceNotification(remoteId.Value, status);
                        }
                        
                        _remotePlayerMods[remoteId.Value] = modList;
                        UpdateUIReport();
                    }
                }
            }
        }

        // --- END NEW P2P NETWORKING LOGIC ---

        /// <summary>Returns all known peer Steam IDs (excluding self) for use by other features.</summary>
        internal static IEnumerable<ulong> GetKnownPeers()
        {
            var instance = UnityEngine.Object.FindFirstObjectByType<CompetitivePluginCheck>();
            if (instance == null) return System.Array.Empty<ulong>();
            var peers = new HashSet<ulong>(instance._knownPeers);
            peers.UnionWith(instance._remotePlayerMods.Keys);
            if (SteamClient.IsValid) peers.Remove(SteamClient.SteamId);
            return peers;
        }

        internal static string GetCurrentSteamLobbyId()
        {
            var instance = UnityEngine.Object.FindFirstObjectByType<CompetitivePluginCheck>();
            if (instance == null || !instance._inLobby || !instance._currentLobby.Id.IsValid)
                return string.Empty;

            return instance._currentLobby.Id.Value.ToString();
        }

        private void SendComplianceNotification(ulong steamId, PlayerComplianceStatus status)
        {
            if (_playersNotifiedAbout.Contains(steamId)) return; // Already notified about this player
            
            _playersNotifiedAbout.Add(steamId);
            
            string message = $"[SBGL] ⚠️ Player {steamId} failed compliance check: {(status.HasMelonLoader ? "MelonLoader detected" : "Missing SBGL.UnifiedMod")}";
            
            // Log the notification
            UnityEngine.Debug.LogWarning($"[SBGL-CompPluginCheck] NOTIFICATION: {message}");
            
            // Attempt to send via game chat API (using reflection)
            try
            {
                // Try to find and use game chat system
                var chatType = System.Type.GetType("GameAssembly+Chat, GameAssembly");
                if (chatType != null)
                {
                    var sendMethod = chatType.GetMethod("SendChatMessage", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (sendMethod != null)
                    {
                        sendMethod.Invoke(null, new object[] { message });
                        UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Chat message sent via game API");
                    }
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Chat API not available (this is OK): {ex.Message}");
            }
        }

        private void AnnounceOwnMelonLoaderToChat()
        {
            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ AnnounceOwnMelonLoaderToChat() called");
            
            // Only announce if the config option is enabled
            if (!ConfigMelonLoaderChatEnabled)
            {
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] MelonLoader chat announcement is disabled in config - skipping");
                return;
            }

            // Only announce in SBGL-marked lobbies (lobby name starts with "SBGL-")
            // Must be in an active lobby first
            if (!_inLobby) return;

            // Use the lobby name captured in real-time by the Harmony patch on BNetworkManager.set_LobbyName
            string lobbyName = _currentLobbyName;

            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Lobby name check: '{lobbyName}'");
            if (!lobbyName.StartsWith("SBGL-"))
            {
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Lobby '{lobbyName}' is not an SBGL lobby - skipping announcement");
                return;
            }
            
            // Send chat announcement about our own MelonLoader installation
            // This is sent EVERY 10 SECONDS - security concern overrides config preference
            
            // Get display name - try to get from shared player profile, fallback to Steam ID
            string displayName = UnifiedPlugin.GetPlayerProfile()?.DisplayName;
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = SteamClient.SteamId.ToString();
            }
            
            string message = $"⚠️ MELONLOADER DETECTED: Player {displayName} is running MelonLoader!";
            
            UnityEngine.Debug.LogWarning($"[SBGL-CompPluginCheck] Announcing MelonLoader presence: {message}");
            
            // Attempt to send via game chat API (using reflection) - try multiple API paths
            bool sentSuccessfully = TrySendChatMessage(message);
            
            if (!sentSuccessfully)
            {
                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ℹ️ Chat API not available (game may not have public chat API). MelonLoader detection is still being broadcast via P2P to other SBGL mod users.");
            }
        }
        
        private bool TrySendChatMessage(string message)
        {
            // Try TextChatManager - the in-game chat system
            string[] textChatManagerPaths = new[]
            {
                "GameAssembly+TextChatManager, GameAssembly",
                "GameAssembly.TextChatManager, GameAssembly",
                "TextChatManager, GameAssembly"
            };
            
            string[] methodNames = new[] 
            { 
                "SendChatMessage", 
                "PostMessage", 
                "AddMessage",
                "Send",
                "SendMessage"
            };
            
            foreach (var typePath in textChatManagerPaths)
            {
                try
                {
                    var chatType = System.Type.GetType(typePath);
                    if (chatType == null) continue;
                    
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Found TextChatManager type: {typePath}");
                    
                    // Try static methods
                    foreach (var methodName in methodNames)
                    {
                        try
                        {
                            var sendMethod = chatType.GetMethod(methodName, 
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                                null, new System.Type[] { typeof(string) }, null);
                            
                            if (sendMethod != null)
                            {
                                sendMethod.Invoke(null, new object[] { message });
                                UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Chat message sent via static {typePath}.{methodName}");
                                return true;
                            }
                        }
                        catch { }
                    }
                    
                    // Try instance method through singleton (Instance, Singleton, or _instance)
                    var instanceProp = chatType.GetProperty("Instance", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) 
                        ?? chatType.GetProperty("Singleton",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        ?? chatType.GetField("_instance",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static 
                        | System.Reflection.BindingFlags.NonPublic)?.DeclaringType?.GetProperty("_instance", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    
                    if (instanceProp == null)
                    {
                        // Try to find Instance field
                        var instanceField = chatType.GetField("Instance",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (instanceField != null)
                        {
                            var instance = instanceField.GetValue(null);
                            if (instance != null)
                            {
                                foreach (var methodName in methodNames)
                                {
                                    try
                                    {
                                        var sendMethod = chatType.GetMethod(methodName,
                                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                        if (sendMethod != null)
                                        {
                                            sendMethod.Invoke(instance, new object[] { message });
                                            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Chat message sent via {typePath}.{methodName}() [instance field]");
                                            return true;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    else if (instanceProp is System.Reflection.PropertyInfo prop)
                    {
                        var instance = prop.GetValue(null);
                        if (instance != null)
                        {
                            foreach (var methodName in methodNames)
                            {
                                try
                                {
                                    var sendMethod = chatType.GetMethod(methodName,
                                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    if (sendMethod != null)
                                    {
                                        sendMethod.Invoke(instance, new object[] { message });
                                        UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] ✓ Chat message sent via {typePath}.{methodName}() [singleton]");
                                        return true;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    
                    // List available methods for debugging
                    var methods = chatType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Available methods on {typePath}: {string.Join(", ", System.Linq.Enumerable.Select(methods, m => m.Name).Distinct())}");
                }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] Error trying {typePath}: {ex.Message}");
                }
            }
            
            UnityEngine.Debug.Log($"[SBGL-CompPluginCheck] TextChatManager not found or no compatible method available");
            return false;
        }

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
                r.SetRequestHeader("api_key", authToken);
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
                r.SetRequestHeader("api_key", authToken);
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
                r.SetRequestHeader("api_key", authToken);
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
                r.SetRequestHeader("api_key", authToken);
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
                r.SetRequestHeader("api_key", authToken);
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
                    _allowedModsSnapshot = AllowedModsSnapshot.Parse(w.downloadHandler.text);
                }
            }
        }

        private void UpdateUIReport()
        {
            StringBuilder sb = new StringBuilder();
            var localPluginScan = BuildLocalPluginScanResult();
            sb.AppendLine($"User: <color=#FFFFFF>{_activeUsername}</color>");
            sb.AppendLine($"Rank: <color=#00FF00>#{_playerRank}</color> / {_totalPlayers}");
            sb.AppendLine($"Win Rate: <color=#FFA500>{_winRate}</color>");
            float.TryParse(_lastChange, out float delta);
            sb.AppendLine($"MMR: <color=#00FFFF>{_playerMMR}</color> (<color={(delta >= 0 ? "#55FF55" : "#FF5555")}>{(delta >= 0 ? "+" : "")}{_lastChange}</color>)");
            sb.AppendLine($"Avg. Par: <color=#CC88FF>{_avgScore}</color>");
            sb.AppendLine($"Matches: <color=#FFFFFF>{_matches}</color> | Top 3s: <color=#00FF00>{_top3s}</color>");

            if (ConfigShowModList)
            {
                sb.AppendLine("--- <color=#AAAAAA>MY MODS</color> ---");
                foreach (var plugin in Chainloader.PluginInfos.Values)
                {
                    if (ShouldIgnorePluginGuid(plugin.Metadata.GUID)) continue;
                    sb.AppendLine($"{(IsAllowedPluginGuid(plugin.Metadata.GUID) ? "<color=#00FF00>O</color>" : "<color=#FF0000>X</color>")} <size=11>{plugin.Metadata.Name}</size>");
                }
            }

            sb.AppendLine("---");
            sb.AppendLine($"<size=10><color=#888888>Sync: {_lastSyncTime} | Status: {_syncStatus}</color></size>");

            if (_statsText != null) _statsText.text = sb.ToString();
            
            // Update debug window content
            UpdateDebugWindow(localPluginScan);
            
            // Update warning text UI elements
            if (_illegalWarningText != null) _illegalWarningText.gameObject.SetActive(localPluginScan.HasIllegalMods);
            if (_missingWarningText != null)
            {
                string scene = SceneManager.GetActiveScene().name;
                bool isRange = scene.Contains("Driving") || scene.Contains("Range");
                _missingWarningText.gameObject.SetActive(localPluginScan.MissingModNames.Count > 0 && isRange);
                if (_missingWarningText.gameObject.activeSelf) _missingWarningText.text = "<color=yellow>MISSING MODS:</color>\n<size=18>" + string.Join(", ", localPluginScan.MissingModNames) + "</size>";
            }
        }
        
        private void UpdateDebugWindow(LocalPluginScanResult localPluginScan)
        {
            if (_debugWindowText == null || _debugWindowObj == null) return;
            
            if (ConfigShowDebugWindow)
            {
                StringBuilder debugSb = new StringBuilder();
                debugSb.AppendLine("--- DEBUG INFORMATION ---");
                debugSb.AppendLine($"Config Show Debug: {ConfigShowDebugWindow}");
                debugSb.AppendLine($"Remote Players Count: {_remotePlayerMods.Count}");
                debugSb.AppendLine($"Allowed Mods Count: {_allowedModsSnapshot.Count}");
                debugSb.AppendLine($"Hash Enforcement: {(_allowedModsSnapshot.HasHashConstraints ? "ON" : "OFF (legacy)")}");
                debugSb.AppendLine($"Missing Mods Count: {localPluginScan.MissingModNames.Count}");
                debugSb.AppendLine($"Tampered Mods Count: {localPluginScan.TamperedModNames.Count}");
                debugSb.AppendLine($"Suspicious Runtime Assemblies: {localPluginScan.SuspiciousRuntimeAssemblies.Count}");
                debugSb.AppendLine($"Any Illegal Mods: {localPluginScan.HasIllegalMods}");
                debugSb.AppendLine($"Sync Status: {_syncStatus}");
                debugSb.AppendLine($"Last Sync Time: {_lastSyncTime}");
                debugSb.AppendLine($"Player ID: {ConfigPlayerId}");
                debugSb.AppendLine($"Resolved ID: {ResolvedID}");
                if (localPluginScan.TamperedModNames.Count > 0)
                {
                    debugSb.AppendLine("--- HASH MISMATCH (TAMPERED) ---");
                    foreach (var modName in localPluginScan.TamperedModNames)
                        debugSb.AppendLine(modName);
                }
                if (localPluginScan.SuspiciousRuntimeAssemblies.Count > 0)
                {
                    debugSb.AppendLine("--- SUSPICIOUS RUNTIME ASSEMBLIES ---");
                    foreach (var assemblyPath in localPluginScan.SuspiciousRuntimeAssemblies)
                    {
                        debugSb.AppendLine(assemblyPath);
                    }
                }
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
            // Destroy any localization/text-setter components that would re-override the label.
            foreach (var comp in btn.GetComponentsInChildren<MonoBehaviour>())
            {
                if (comp == null) continue;
                string typeName = comp.GetType().Name;
                if (typeName.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Translat", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    UnityEngine.Object.DestroyImmediate(comp);
                }
            }
            foreach (var tmp in btn.GetComponentsInChildren<TextMeshProUGUI>())
                tmp.text = txt;
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
                userText.textWrappingMode = TMPro.TextWrappingModes.NoWrap;

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

    // Harmony patch to capture lobby name whenever BNetworkManager.LobbyName is set
    [HarmonyPatch(typeof(BNetworkManager), nameof(BNetworkManager.LobbyName), MethodType.Setter)]
    public static class LobbyNameCapturePatch
    {
        public static void Postfix(string value)
        {
            CompetitivePluginCheck._currentLobbyName = value ?? "";
        }
    }
}

