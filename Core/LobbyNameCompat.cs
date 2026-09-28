using System;
using System.Reflection;
using HarmonyLib;

namespace SBGL.UnifiedMod.Core
{
    /// <summary>
    /// The game's lobby-name API differs between builds, so everything here is resolved by name
    /// at runtime and one mod build runs on both:
    ///
    ///   retail 1.2.2-657 : static property BNetworkManager.LobbyName (get_/set_LobbyName)
    ///   playtest 1.2.2-691: GetLobbyName(), ServerSanitizeAndSetLobbyName(name), LobbyNameSet event
    ///
    /// Binding to either by name in source (nameof) is what broke the build when the property was
    /// removed, so callers go through here instead.
    /// </summary>
    public static class LobbyNameCompat
    {
        /// <summary>Reads the current lobby name. Newer API first, so a build with both prefers it.</summary>
        public static readonly MethodInfo Getter;

        /// <summary>Playtest write path. Server-only, and returns the sanitized name it applied.</summary>
        public static readonly MethodBase SanitizeSetter;

        /// <summary>Retail write path: the property setter.</summary>
        public static readonly MethodBase PropertySetter;

        /// <summary>Playtest change notification, raised on clients too. Null on retail.</summary>
        private static readonly EventInfo LobbyNameSetEvent;

        static LobbyNameCompat()
        {
            Getter = AccessTools.Method(typeof(BNetworkManager), "GetLobbyName")
                  ?? AccessTools.Method(typeof(BNetworkManager), "get_LobbyName");
            SanitizeSetter = AccessTools.Method(typeof(BNetworkManager), "ServerSanitizeAndSetLobbyName");
            PropertySetter = AccessTools.Method(typeof(BNetworkManager), "set_LobbyName");
            LobbyNameSetEvent = typeof(BNetworkManager).GetEvent(
                "LobbyNameSet", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }

        /// <summary>The lobby name the game currently holds, or "" if it can't be read.</summary>
        public static string Get()
        {
            try { return Getter?.Invoke(null, null) as string ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>Subscribes to the change event where the build has one. False if it doesn't.</summary>
        public static bool TrySubscribe(Action handler)
        {
            if (LobbyNameSetEvent == null || handler == null) return false;
            try
            {
                LobbyNameSetEvent.AddEventHandler(null, Delegate.CreateDelegate(LobbyNameSetEvent.EventHandlerType, handler.Target, handler.Method));
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[LobbyNameCompat] Could not subscribe to LobbyNameSet: {ex.Message}");
                return false;
            }
        }

        public static bool TryUnsubscribe(Action handler)
        {
            if (LobbyNameSetEvent == null || handler == null) return false;
            try
            {
                LobbyNameSetEvent.RemoveEventHandler(null, Delegate.CreateDelegate(LobbyNameSetEvent.EventHandlerType, handler.Target, handler.Method));
                return true;
            }
            catch { return false; }
        }

        /// <summary>One line for the startup log, so a future API change is obvious from a log.</summary>
        public static string Describe()
        {
            string writer = SanitizeSetter != null ? SanitizeSetter.Name
                          : PropertySetter != null ? PropertySetter.Name
                          : "none";
            return $"read={Getter?.Name ?? "none"}, write={writer}, change event={(LobbyNameSetEvent != null ? "LobbyNameSet" : "none")}";
        }
    }
}
