using UnityEngine.Networking;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using System;
using BepInEx.Logging;

namespace SBGL.UnifiedMod.Utils
{
    // ==========================================
    // SSL CERTIFICATE BYPASS HANDLER
    // ==========================================
    public class BypassCertificate : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }

    // ==========================================
    // API REQUEST UTILITIES
    // ==========================================
    public static class ApiUtilities
    {
        /// <summary>
        /// Makes an API request with proper headers, SSL bypass, and error handling
        /// </summary>
        public static IEnumerator MakeApiRequest(
            string url,
            Dictionary<string, string> headers,
            System.Action<bool, string> onComplete,
            ManualLogSource logger = null)
        {
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                // Apply SSL bypass
                req.certificateHandler = new BypassCertificate();

                // Apply all headers
                if (headers != null)
                {
                    foreach (var kvp in headers)
                    {
                        req.SetRequestHeader(kvp.Key, kvp.Value);
                    }
                }

                // Send request
                yield return req.SendWebRequest();

                // Handle response
                if (req.result == UnityWebRequest.Result.Success)
                {
                    onComplete?.Invoke(true, req.downloadHandler.text);
                }
                else
                {
                    string errorMsg = $"HTTP {req.responseCode}: {req.error}";
                    if (!string.IsNullOrEmpty(req.downloadHandler?.text))
                    {
                        errorMsg += $" | Response: {req.downloadHandler.text}";
                    }
                    logger?.LogWarning($"[API] Request failed: {errorMsg}");
                    onComplete?.Invoke(false, errorMsg);
                }
            }
        }

        /// <summary>
        /// Parses a JSON response as a JObject
        /// </summary>
        public static JObject ParseJsonResponse(string json)
        {
            try
            {
                return JObject.Parse(json);
            }
            catch (Exception ex)
            {
                throw new System.InvalidOperationException($"Failed to parse JSON response: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a JSON array response
        /// </summary>
        public static JArray ParseJsonArrayResponse(string json)
        {
            try
            {
                return JArray.Parse(json);
            }
            catch (Exception ex)
            {
                throw new System.InvalidOperationException($"Failed to parse JSON array response: {ex.Message}");
            }
        }
    }

    // ==========================================
    // PLAYER PROFILE FETCHER (Shared Utility)
    // ==========================================
    public static class PlayerProfileFetcher
    {
        public class PlayerProfile
        {
            public string ID;
            public string DisplayName;
            public float CurrentMMR;
            public string Region;
            public string ProfilePicUrl;
        }

        /// <summary>
        /// Fetches a player profile by ID or display name from the API
        /// </summary>
        public static IEnumerator FetchPlayerProfile(
            string playerIdOrName,
            string playerApiUrl,
            string appId,
            string authToken,
            System.Action<bool, PlayerProfile> onComplete,
            ManualLogSource logger = null)
        {
            if (string.IsNullOrEmpty(playerIdOrName))
            {
                onComplete?.Invoke(false, null);
                yield break;
            }

            // Try exact match first
            string query = playerIdOrName.All(char.IsDigit)
                ? "{\"id\":\"" + playerIdOrName + "\"}"
                : "{\"display_name\":{\"$regex\":\"^" + playerIdOrName + "$\",\"$options\":\"i\"}}";

            string url = $"{playerApiUrl}?q={UnityWebRequest.EscapeURL(query)}";
            var headers = new Dictionary<string, string>
            {
                { "X-App-Id", appId },
                { "api_key", authToken }
            };

            bool found = false;
            PlayerProfile profile = null;

            yield return ApiUtilities.MakeApiRequest(url, headers, (success, response) =>
            {
                if (success)
                {
                    try
                    {
                        var res = ApiUtilities.ParseJsonArrayResponse(response);
                        if (res.Count > 0)
                        {
                            profile = new PlayerProfile
                            {
                                ID = res[0]["id"]?.ToString() ?? "",
                                DisplayName = res[0]["display_name"]?.ToString() ?? "",
                                Region = res[0]["region"]?.ToString() ?? "US",
                                ProfilePicUrl = res[0]["profile_pic_url"]?.ToString(),
                                CurrentMMR = float.TryParse(res[0]["current_mmr"]?.ToString() ?? "0", out float mmr) ? mmr : 0f
                            };
                            found = true;
                            logger?.LogInfo($"[PlayerProfileFetcher] ✓ Found profile: {profile.DisplayName} (ID: {profile.ID})");
                        }
                        else
                        {
                            logger?.LogWarning($"[PlayerProfileFetcher] No results for: {playerIdOrName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError($"[PlayerProfileFetcher] Parse error: {ex.Message}");
                    }
                }
                else
                {
                    logger?.LogError($"[PlayerProfileFetcher] Request failed: {response}");
                }
            }, logger);

            onComplete?.Invoke(found, profile);
        }

        /// <summary>
        /// Fuzzy search for a player by display name (substring match)
        /// </summary>
        public static IEnumerator FuzzySearchPlayer(
            string playerName,
            string playerApiUrl,
            string appId,
            string authToken,
            System.Action<bool, PlayerProfile> onComplete,
            ManualLogSource logger = null)
        {
            string query = "{\"display_name\":{\"$regex\":\"" + playerName + "\",\"$options\":\"i\"}}";
            string url = $"{playerApiUrl}?q={UnityWebRequest.EscapeURL(query)}";
            var headers = new Dictionary<string, string>
            {
                { "X-App-Id", appId },
                { "api_key", authToken }
            };

            bool found = false;
            PlayerProfile profile = null;

            yield return ApiUtilities.MakeApiRequest(url, headers, (success, response) =>
            {
                if (success)
                {
                    try
                    {
                        var res = ApiUtilities.ParseJsonArrayResponse(response);
                        if (res.Count > 0)
                        {
                            profile = new PlayerProfile
                            {
                                ID = res[0]["id"]?.ToString() ?? "",
                                DisplayName = res[0]["display_name"]?.ToString() ?? "",
                                Region = res[0]["region"]?.ToString() ?? "US",
                                ProfilePicUrl = res[0]["profile_pic_url"]?.ToString(),
                                CurrentMMR = float.TryParse(res[0]["current_mmr"]?.ToString() ?? "0", out float mmr) ? mmr : 0f
                            };
                            found = true;
                            logger?.LogInfo($"[PlayerProfileFetcher] ✓ Fuzzy match: {profile.DisplayName}");
                        }
                        else
                        {
                            logger?.LogWarning($"[PlayerProfileFetcher] No fuzzy matches for: {playerName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError($"[PlayerProfileFetcher] Fuzzy parse error: {ex.Message}");
                    }
                }
                else
                {
                    logger?.LogError($"[PlayerProfileFetcher] Fuzzy request failed: {response}");
                }
            }, logger);

            onComplete?.Invoke(found, profile);
        }
    }
}
