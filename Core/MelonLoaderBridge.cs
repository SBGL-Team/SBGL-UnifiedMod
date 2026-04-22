using System;
using System.Collections.Generic;
using UnityEngine;

namespace SBGL.UnifiedMod.Core
{
    /// <summary>
    /// This class acts as a bridge to detect MelonLoader presence.
    /// It's designed to work whether the mod loads under BepInEx or MelonLoader.
    /// </summary>
    public static class MelonLoaderBridge
    {
        private static bool _initialized = false;
        private static bool _melonLoaderDetected = false;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _melonLoaderDetected = DetectMelonLoader();

            if (_melonLoaderDetected)
            {
                Debug.LogError("[SBGL-MelonLoaderBridge] ⚠️⚠️⚠️ MELONLOADER DETECTED AT INITIALIZATION ⚠️⚠️⚠️");
            }
        }

        private static bool DetectMelonLoader()
        {
            try
            {
                // Method 1: Check AppDomain assemblies
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name.Equals("MelonLoader", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.LogError("[SBGL-MelonLoaderBridge] ✓ Found MelonLoader in AppDomain.GetAssemblies()");
                        return true;
                    }
                }

                // Method 2: Check for MelonLoader.dll file
                string gameDir = System.AppDomain.CurrentDomain.BaseDirectory;
                string melonLoaderPath = System.IO.Path.Combine(gameDir, "MelonLoader", "MelonLoader.dll");
                if (System.IO.File.Exists(melonLoaderPath))
                {
                    Debug.LogError("[SBGL-MelonLoaderBridge] ✓ Found MelonLoader.dll file at: " + melonLoaderPath);
                    return true;
                }

                Debug.Log("[SBGL-MelonLoaderBridge] No MelonLoader detected");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SBGL-MelonLoaderBridge] Error in DetectMelonLoader: {ex.Message}");
                return false;
            }
        }

        public static bool IsMelonLoaderLoaded => _melonLoaderDetected;
    }
}
