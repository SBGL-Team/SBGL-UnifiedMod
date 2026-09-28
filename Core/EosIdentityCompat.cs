using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SBGL.UnifiedMod.Core
{
    /// <summary>Which platform a player is on, as far as compliance checking is concerned.</summary>
    public enum PlayerPlatform
    {
        /// <summary>No external account info yet, or this game build has no EOS identity.</summary>
        Unknown,

        /// <summary>Steam. Can install mods, and is reachable over Steam P2P, so must report.</summary>
        Steam,

        /// <summary>Console (Switch / PlayStation / Xbox). Cannot install mods.</summary>
        Console,

        /// <summary>A PC platform other than Steam, e.g. Epic. Can install mods but is not reachable over Steam P2P.</summary>
        OtherPc,
    }

    /// <summary>
    /// Resolves who a player actually is on a crossplay build, using the external account info the
    /// game already caches for player icons (EOS.dll: BEosLobbyManager).
    ///
    /// This is what lets compliance stay strict with crossplay on. Without it the mod can only say
    /// "no Steam ID seen", which covers both a console player who physically cannot install mods
    /// and a Steam player who dodged Steam discovery — so it would have to either accuse the
    /// console player or excuse the Steam one. With it, the platform is a fact, and a Steam player
    /// is identified by their real Steam ID whether or not they share a Steam lobby with us.
    /// </summary>
    public static class EosIdentityCompat
    {
        private static readonly Type LobbyManagerType = AccessTools.TypeByName("BEosLobbyManager");
        private static readonly MethodInfo TryGetExternalAccountInfo = LobbyManagerType != null
            ? AccessTools.Method(LobbyManagerType, "TryGetExternalAccountInfoFor") : null;
        private static readonly MethodInfo TryGetDisplayName = LobbyManagerType != null
            ? AccessTools.Method(LobbyManagerType, "TryGetDisplayNameFor") : null;

        /// <summary>True when this build can tell us what platform a player is on.</summary>
        public static bool IsAvailable => TryGetExternalAccountInfo != null;

        /// <summary>
        /// Platform and Steam ID for a player's EOS id. steamId is 0 unless the platform is Steam.
        /// Returns false when the game has no cached account info for them yet, which is normal for
        /// a few seconds after someone joins.
        /// </summary>
        public static bool TryIdentify(object eosProductUserId, out PlayerPlatform platform, out ulong steamId, out string accountType)
        {
            platform = PlayerPlatform.Unknown;
            steamId = 0;
            accountType = null;

            if (TryGetExternalAccountInfo == null || eosProductUserId == null) return false;

            try
            {
                var args = new object[] { eosProductUserId, null };
                if (!(bool)TryGetExternalAccountInfo.Invoke(null, args) || args[1] == null) return false;

                object info = args[1];
                accountType = AccessTools.Property(info.GetType(), "AccountIdType")?.GetValue(info)?.ToString();
                string accountId = AccessTools.Property(info.GetType(), "AccountId")?.GetValue(info)?.ToString();

                platform = ClassifyPlatform(accountType);
                if (platform == PlayerPlatform.Steam && !string.IsNullOrWhiteSpace(accountId))
                {
                    // EOS reports the Steam account as the 64-bit id; older SDKs use hex.
                    if (!ulong.TryParse(accountId, out steamId))
                        ulong.TryParse(accountId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out steamId);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EosIdentityCompat] Could not read external account info: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Maps an EOS ExternalAccountType to what it means for compliance. Anything unrecognised
        /// counts as a PC platform: a new storefront must not be waved through as a console.
        /// </summary>
        private static PlayerPlatform ClassifyPlatform(string accountType)
        {
            if (string.IsNullOrWhiteSpace(accountType)) return PlayerPlatform.Unknown;

            switch (accountType.Trim().ToLowerInvariant())
            {
                case "steam":
                    return PlayerPlatform.Steam;

                // Closed platforms: no side-loading, so no mods.
                case "nintendo":
                case "psn":
                case "xbl":
                    return PlayerPlatform.Console;

                default:
                    return PlayerPlatform.OtherPc;
            }
        }

        /// <summary>The player's display name as EOS knows it, or null.</summary>
        public static string TryGetName(object eosProductUserId)
        {
            if (TryGetDisplayName == null || eosProductUserId == null) return null;
            try
            {
                var args = new object[] { eosProductUserId, null };
                return (bool)TryGetDisplayName.Invoke(null, args) ? args[1] as string : null;
            }
            catch { return null; }
        }

        public static string Describe() => IsAvailable ? "BEosLobbyManager external accounts" : "none";
    }
}
