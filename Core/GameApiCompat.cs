using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SBGL.UnifiedMod.Core
{
    /// <summary>
    /// Game members the mod calls that moved between builds. Everything is resolved by name at
    /// runtime so a single mod build runs on retail and on the playtest.
    ///
    ///   retail 1.2.2-657 : MainMenu.StartHost(), CourseManager.PlayerState.name
    ///   playtest 1.2.2-691: hosting moved into LobbyInitializationMenu (OpenMenu + StartGame,
    ///                       which awaits NetworkManager.StartHost); name split into RawName and
    ///                       NameWithNoParsedRichText.
    ///
    /// See also <see cref="LobbyNameCompat"/> for the lobby-name API.
    /// </summary>
    public static class GameApiCompat
    {
        // --- hosting -------------------------------------------------------
        private static readonly MethodInfo MainMenuStartHost = AccessTools.Method(typeof(MainMenu), "StartHost");
        private static readonly Type LobbyInitMenuType = AccessTools.TypeByName("LobbyInitializationMenu");
        private static readonly MethodInfo LobbyInitOpenMenu = LobbyInitMenuType != null ? AccessTools.Method(LobbyInitMenuType, "OpenMenu") : null;
        private static readonly MethodInfo LobbyInitStartGame = LobbyInitMenuType != null ? AccessTools.Method(LobbyInitMenuType, "StartGame") : null;

        // --- player name ---------------------------------------------------
        // RawName keeps whatever the player typed, matching the old 'name' property.
        private static readonly MethodInfo PlayerStateName =
               AccessTools.PropertyGetter(typeof(CourseManager.PlayerState), "name")
            ?? AccessTools.PropertyGetter(typeof(CourseManager.PlayerState), "RawName")
            ?? AccessTools.PropertyGetter(typeof(CourseManager.PlayerState), "NameWithNoParsedRichText");

        /// <summary>
        /// Starts hosting a lobby. Retail calls MainMenu.StartHost directly; the playtest routes
        /// it through the lobby setup menu, whose start button is what reaches StartHost there.
        /// Returns false if neither path exists, so callers can log rather than throw.
        /// </summary>
        public static bool TryStartHost(MainMenu mainMenu)
        {
            try
            {
                if (MainMenuStartHost != null && mainMenu != null)
                {
                    MainMenuStartHost.Invoke(mainMenu, null);
                    return true;
                }

                if (LobbyInitStartGame != null)
                {
                    // Inactive too: the menu object exists in the scene before it is opened.
                    var menu = Resources.FindObjectsOfTypeAll(LobbyInitMenuType).FirstOrDefault();
                    if (menu == null)
                    {
                        Debug.LogError("[GameApiCompat] LobbyInitializationMenu not found — cannot start host");
                        return false;
                    }

                    // Open first: the menu loads its saved settings (crossplay, max players) when
                    // shown, and those are what StartGame reads.
                    LobbyInitOpenMenu?.Invoke(menu, null);
                    LobbyInitStartGame.Invoke(menu, null);
                    return true;
                }

                Debug.LogError("[GameApiCompat] No known way to start hosting in this game build");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameApiCompat] Failed to start host: {ex}");
                return false;
            }
        }

        // --- lobby roster ---------------------------------------------------
        // The playtest gives every player a synced PlayerId component carrying their name. It is
        // how the mod can see players it has no Steam ID for — console and crossplay players,
        // who can't install mods at all, and who were previously missing from the panel entirely.
        private static readonly Type PlayerIdType = AccessTools.TypeByName("PlayerId");
        private static readonly MethodInfo PlayerIdName =
               (PlayerIdType != null ? AccessTools.PropertyGetter(PlayerIdType, "PlayerNameNoRichText") : null)
            ?? (PlayerIdType != null ? AccessTools.PropertyGetter(PlayerIdType, "PlayerName") : null);
        private static readonly MethodInfo PlayerIdGuid = PlayerIdType != null ? AccessTools.PropertyGetter(PlayerIdType, "Guid") : null;
        private static readonly MethodInfo PlayerIdEosId = PlayerIdType != null ? AccessTools.PropertyGetter(PlayerIdType, "EosProductUserId") : null;

        /// <summary>One player in the lobby, as the game itself sees them.</summary>
        public sealed class RosterPlayer
        {
            public ulong Guid;
            public string Name;
            public PlayerPlatform Platform;
            /// <summary>Their real Steam ID when the platform is Steam, otherwise 0.</summary>
            public ulong SteamId;
            /// <summary>The raw EOS account type, for logs and the debug window.</summary>
            public string AccountType;
        }

        /// <summary>True when this game build exposes a player roster the mod can read.</summary>
        public static bool HasPlayerRoster => PlayerIdType != null && PlayerIdName != null && PlayerIdGuid != null;

        /// <summary>
        /// Every player currently in the lobby, by the game's own id and name. Empty on builds
        /// without PlayerId, where the mod keeps to its Steam-based discovery.
        /// </summary>
        public static List<RosterPlayer> GetLobbyRoster()
        {
            var roster = new List<RosterPlayer>();
            if (!HasPlayerRoster) return roster;

            try
            {
                var found = UnityEngine.Object.FindObjectsByType(PlayerIdType, FindObjectsSortMode.None);
                foreach (var obj in found)
                {
                    // PlayerNameNoRichText is wrapped as "<noparse>name</noparse>", so it never
                    // compared equal to the player's Steam name.
                    string name = StripNoParse(PlayerIdName.Invoke(obj, null) as string);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var player = new RosterPlayer
                    {
                        Guid = Convert.ToUInt64(PlayerIdGuid.Invoke(obj, null)),
                        Name = name.Trim(),
                        Platform = PlayerPlatform.Unknown,
                    };

                    // Over Steam networking the game sets the player guid to their Steam ID
                    // (BNetworkManager.ServerGetPlayerGuid), so a Steam-shaped guid identifies a
                    // Steam player even on builds with no EOS account info.
                    if (IsIndividualSteamId(player.Guid))
                    {
                        player.Platform = PlayerPlatform.Steam;
                        player.SteamId = player.Guid;
                    }

                    // Platform comes from the account info the game caches for player icons.
                    object eosId = PlayerIdEosId?.Invoke(obj, null);
                    if (eosId != null &&
                        EosIdentityCompat.TryIdentify(eosId, out var platform, out ulong steamId, out string accountType))
                    {
                        player.Platform = platform;
                        player.SteamId = steamId;
                        player.AccountType = accountType;
                    }

                    roster.Add(player);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GameApiCompat] Could not read player roster: {ex.Message}");
            }

            return roster;
        }

        private static string StripNoParse(string name) =>
            name?.Replace("<noparse>", string.Empty).Replace("</noparse>", string.Empty);

        /// <summary>
        /// True for a public-universe individual Steam account ID (upper 32 bits 0x01100001).
        /// Other transports use small connection-based guids, which never match this.
        /// </summary>
        private static bool IsIndividualSteamId(ulong id) => (id >> 32) == 0x01100001UL;

        /// <summary>The player's name from a CourseManager.PlayerState, or "" if unreadable.</summary>
        public static string GetPlayerStateName(CourseManager.PlayerState state)
        {
            try { return PlayerStateName?.Invoke(state, null) as string ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>One line for the startup log, naming which build's API this resolved to.</summary>
        public static string Describe()
        {
            string host = MainMenuStartHost != null ? "MainMenu.StartHost"
                        : LobbyInitStartGame != null ? "LobbyInitializationMenu.StartGame"
                        : "none";
            return $"host start={host}, player name={PlayerStateName?.Name ?? "none"}, roster={(HasPlayerRoster ? "PlayerId" : "none")}";
        }
    }
}
