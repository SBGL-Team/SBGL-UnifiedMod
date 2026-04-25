using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using SBGL.UnifiedMod.Core;
using SBGLLiveLeaderboard;
using Steamworks;

namespace SBGLeagueAutomation
{
    /// <summary>
    /// Handles all match result submission logic including:
    /// - Match creation
    /// - MatchEntry creation and updates
    /// - Score monitoring during gameplay
    /// - Final match stats finalization
    /// </summary>
    public class MatchResultSubmissionService
    {
        // ==========================================
        // P2P MATCH ID COORDINATION
        // Set by CompetitivePluginCheck when a SBGL_MATCH_ID: packet is received,
        // so that non-host players adopt the same Match record instead of creating duplicates.
        // ==========================================
        private const int SBGL_NET_CHANNEL = 2622; // Same channel as CompetitivePluginCheck
        internal static string ReceivedP2PMatchId = null;

        /// <summary>Called by CompetitivePluginCheck when a SBGL_MATCH_ID: P2P packet arrives.</summary>
        internal static void HandleIncomingMatchIdBroadcast(string matchId)
        {
            if (!string.IsNullOrEmpty(matchId))
                ReceivedP2PMatchId = matchId;
        }

        /// <summary>Broadcasts a Match ID to all known peers over Steam P2P.</summary>
        internal static void BroadcastMatchId(string matchId, IEnumerable<ulong> peers)
        {
            if (!SteamClient.IsValid || string.IsNullOrEmpty(matchId)) return;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes($"SBGL_MATCH_ID:{matchId}");
                int sent = 0;
                foreach (var peer in peers)
                {
                    try { SteamNetworking.SendP2PPacket(peer, data, -1, SBGL_NET_CHANNEL, P2PSend.Reliable); sent++; }
                    catch { }
                }
                UnityEngine.Debug.Log($"[SBGL-MatchResult] Broadcast Match ID {matchId} to {sent} peers");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[SBGL-MatchResult] BroadcastMatchId error: {ex.Message}");
            }
        }

        private SBGLPlugin.MatchmakingSession _currentSession;
        private SBGLPlugin.PlayerProfile _userProfile;
        private string _currentMatchId;
        private Dictionary<string, string> _playerMatchEntryIds = new Dictionary<string, string>();
        private Dictionary<string, int> _lastSubmittedScores = new Dictionary<string, int>();
        private Dictionary<string, int> _lastSubmittedScoresVsPar = new Dictionary<string, int>();
        private Dictionary<string, int> _cachedLeaderboardScores = new Dictionary<string, int>();
        private Dictionary<string, int> _cachedLeaderboardScoresVsPar = new Dictionary<string, int>();
#pragma warning disable CS0414
        private bool _matchEntriesCreated = false;
        private bool _isInGameplay = false;
        private DateTime? _matchStartTime = null;
        private bool _matchStatsSubmitted = false;
#pragma warning restore CS0414

        // Dependencies (callbacks to parent MatchmakingAssistant)
        private Func<string> _getBaseApiUrl;
        private Action<string> _logger;
        private Func<string, string, string, Action<string>, IEnumerator> _callApi;
        private Func<string, JObject> _parseApiSingleObject;
        private Action<IEnumerator> _startCoroutine;

        public MatchResultSubmissionService(
            Func<string> getBaseApiUrl,
            Action<string> logger,
            Func<string, string, string, Action<string>, IEnumerator> callApi,
            Func<string, JObject> parseApiSingleObject,
            Action<IEnumerator> startCoroutine)
        {
            _getBaseApiUrl = getBaseApiUrl;
            _logger = logger;
            _callApi = callApi;
            _parseApiSingleObject = parseApiSingleObject;
            _startCoroutine = startCoroutine;
        }

        private void Log(string message) => _logger?.Invoke(message);

        // ==========================================
        // PUBLIC API
        // ==========================================

        public void Initialize(SBGLPlugin.MatchmakingSession session, SBGLPlugin.PlayerProfile profile)
        {
            _currentSession = session;
            _userProfile = profile;
            _playerMatchEntryIds.Clear();
            _lastSubmittedScores.Clear();
            _lastSubmittedScoresVsPar.Clear();
            _matchEntriesCreated = false;
            _matchStatsSubmitted = false;
        }

        public void SetCachedScores(Dictionary<string, int> scores, Dictionary<string, int> scoresVsPar)
        {
            _cachedLeaderboardScores = new Dictionary<string, int>(scores ?? new Dictionary<string, int>());
            _cachedLeaderboardScoresVsPar = new Dictionary<string, int>(scoresVsPar ?? new Dictionary<string, int>());
        }

        public IEnumerator CreateMatchAndEntries(List<LiveLeaderboardPlugin.SBGLPlayer> startingLeaderboard, List<string> playerIds)
        {
            Log($"<color=cyan>[Match Creation] Starting new match for session {_currentSession.id}</color>");
            _matchStartTime = DateTime.UtcNow;

            // Step 1: Create Match record
            // Retrieve course and match type from PlayerPrefs (set by API during matchmaking)
            string courseName = PlayerPrefs.GetString("SelectedCourse", "Unknown");
            string matchType = PlayerPrefs.GetString("MatchType", "unranked");
            int season = PlayerPrefs.GetInt("Season", 1);

            SBGLPlugin.MatchStats matchStats = new SBGLPlugin.MatchStats
            {
                matchmaking_session_id = _currentSession.id,
                match_id = _currentSession.id,
                player_id = _userProfile.id,
                player_name = _userProfile.display_name,
                match_date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                duration_seconds = 0,
                is_host = false,
                status = "completed",
                course_name = courseName,
                match_type = matchType,
                season = season
            };

            Log($"<color=cyan>[Match Creation] Match configuration - Course: {courseName}, Type: {matchType}, Season: {season}</color>");

            string matchId = null;
            yield return SubmitMatchEntry(matchStats, (id) => matchId = id);

            if (string.IsNullOrEmpty(matchId))
            {
                Log("<color=red>[Match Creation] Failed to create Match record</color>");
                yield break;
            }

            _currentMatchId = matchId;
            Log($"<color=green>[Match Creation] ✓ Match created: {matchId}</color>");

            // Step 1b: Link Match ID back to the MatchmakingSession so the website can detect mod-submitted matches
            yield return _callApi($"/MatchmakingSession/{_currentSession.id}", "PUT", $"{{\"match_id\":\"{matchId}\"}}", (res) =>
            {
                try
                {
                    JObject response = _parseApiSingleObject(res);
                    if (response != null)
                        Log($"<color=green>[Match Creation] ✓ MatchmakingSession {_currentSession.id} linked to match: {matchId}</color>");
                    else
                        Log($"<color=yellow>[Match Creation] Could not confirm MatchmakingSession update</color>");
                }
                catch (System.Exception ex)
                {
                    Log($"<color=yellow>[Match Creation] Error updating MatchmakingSession: {ex.Message}</color>");
                }
            });

            // Step 2: Create initial MatchEntry records for all players
            _playerMatchEntryIds.Clear();
            _lastSubmittedScores.Clear();
            _lastSubmittedScoresVsPar.Clear();

            foreach (string playerId in playerIds)
            {
                string playerName = null;
                string preMatchMmr = null;
                int gamePoints = 0;
                int scoreVsPar = 0;

                // Get player name, MMR and initial scores
                if (playerId == _userProfile.id)
                {
                    playerName = _userProfile.display_name;
                    preMatchMmr = _userProfile.current_mmr.ToString();
                    Log($"<color=cyan>[Match Creation] Current player: {playerName} (MMR: {preMatchMmr})</color>");
                }
                else
                {
                    yield return _callApi($"/Player/{playerId}", "GET", "", (res) =>
                    {
                        try
                        {
                            JObject profile = _parseApiSingleObject(res);
                            if (profile != null)
                            {
                                playerName = (string)profile["display_name"];
                                if (string.IsNullOrEmpty(playerName))
                                {
                                    Log($"<color=yellow>[Match Creation] Player {playerId} has no display_name, using ID</color>");
                                    playerName = playerId;
                                }
                                object mmrObj = profile["current_mmr"];
                                if (mmrObj != null)
                                {
                                    preMatchMmr = mmrObj.ToString();
                                }
                                Log($"<color=cyan>[Match Creation] Fetched player: {playerName} (MMR: {preMatchMmr})</color>");
                            }
                            else
                            {
                                Log($"<color=yellow>[Match Creation] Failed to fetch Player {playerId} - response null</color>");
                                playerName = playerId;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Log($"<color=yellow>[Match Creation] Error fetching Player {playerId}: {ex.Message}</color>");
                            playerName = playerId;
                        }
                    });
                }

                if (!string.IsNullOrEmpty(playerName))
                {
                    if (_cachedLeaderboardScores.TryGetValue(playerName, out int score))
                    {
                        gamePoints = score;
                        _cachedLeaderboardScoresVsPar.TryGetValue(playerName, out scoreVsPar);
                    }

                    _lastSubmittedScores[playerName] = gamePoints;
                    _lastSubmittedScoresVsPar[playerName] = scoreVsPar;
                }

                // Get starting position from leaderboard
                int startingPosition = GetPlayerFinishPosition(playerName, startingLeaderboard);

                // Create MatchEntry with MMR snapshot and adjusted score
                int adjustedScore = gamePoints + (scoreVsPar * -10);
                string mmrField = !string.IsNullOrEmpty(preMatchMmr) ? $",\"pre_match_mmr\":{preMatchMmr}" : "";
                string posField = startingPosition > 0 ? $",\"finish_position\":{startingPosition}" : "";
                string json = "{" +
                    $"\"match_id\":\"{matchId}\"," +
                    $"\"player_id\":\"{playerId}\"," +
                    $"\"player_name\":\"{playerName ?? "Unknown"}\"," +
                    $"\"game_points\":{gamePoints}," +
                    $"\"over_under\":{scoreVsPar}," +
                    $"\"score_vs_par\":{scoreVsPar}," +
                    $"\"adjusted_match_score\":{adjustedScore}" +
                    mmrField +
                    posField +
                    $",\"notes\":\"Progressive match tracking - created at round start\"" +
                "}";

                Log($"<color=cyan>[Match Creation] JSON: {json}</color>");

                string entryId = null;
                yield return _callApi("/MatchEntry", "POST", json, (res) =>
                {
                    try
                    {
                        JObject response = _parseApiSingleObject(res);
                        if (response != null)
                        {
                            entryId = (string)response["id"];
                            _playerMatchEntryIds[playerId] = entryId;
                            Log($"<color=green>[Match Creation] ✓ MatchEntry created for {playerName}: {entryId}</color>");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Log($"<color=yellow>[Match Creation] Could not parse MatchEntry: {ex.Message}</color>");
                    }
                });
            }

            _matchEntriesCreated = true;
            Log($"<color=green>[Match Creation] ✓ Match and entries initialized. Starting score monitoring...</color>");

            // Start monitoring for score changes during gameplay
            _isInGameplay = true;
            _startCoroutine?.Invoke(MonitorAndUpdateScores());
        }

        public IEnumerator FinalizeMatchStats(List<LiveLeaderboardPlugin.SBGLPlayer> finalLeaderboard)
        {
            // Wait for leaderboard to finalize
            yield return new WaitForSeconds(1.5f);

            Log($"<color=cyan>[Match Finalize] Performing final score update...</color>");

            if (finalLeaderboard == null || finalLeaderboard.Count == 0)
            {
                Log($"<color=yellow>[Match Finalize] No leaderboard data available</color>");
                _matchStatsSubmitted = true;
                yield break;
            }

            Log($"<color=cyan>[Match Finalize] Using final snapshot with {finalLeaderboard.Count} players</color>");

            foreach (var player in finalLeaderboard)
            {
                if (player == null) continue;

                int finalGamePoints = player.BaseScore;
                int finalScoreVsPar = 0;
                int finishPosition = GetPlayerFinishPosition(player.Name, finalLeaderboard);

                if (!string.IsNullOrEmpty(player.RawStrokes))
                {
                    string strokeStr = player.RawStrokes.Replace("±", "").Trim();
                    int.TryParse(strokeStr, out finalScoreVsPar);
                }

                // Find entry ID for this player
                string playerId = null;
                string entryId = null;

                if (player.Name == _userProfile.display_name)
                {
                    playerId = _userProfile.id;
                }

                if (!string.IsNullOrEmpty(playerId) && _playerMatchEntryIds.TryGetValue(playerId, out entryId))
                {
                    // Perform final update
                    Log($"<color=cyan>[Match Finalize] Final update for {player.Name}: {finalGamePoints} pts, {finalScoreVsPar} vs par, Position: {finishPosition}</color>");
                    yield return UpdateMatchEntry(entryId, playerId, player.Name, finalGamePoints, finalScoreVsPar, finishPosition);
                }
            }

            _matchStatsSubmitted = true;
            Log($"<color=green>[Match Finalize] ✓ Match stats finalized</color>");
        }

        // ==========================================
        // PRIVATE IMPLEMENTATION
        // ==========================================

        private IEnumerator MonitorAndUpdateScores()
        {
            Log($"<color=cyan>[Match Monitor] Starting score monitoring for gameplay</color>");

            while (_isInGameplay && _currentMatchId != null)
            {
                yield return new WaitForSeconds(2f);

                // Would get leaderboard data from LiveLeaderboard plugin here
                // For now, this is a placeholder
            }

            Log($"<color=cyan>[Match Monitor] Score monitoring ended</color>");
        }

        private IEnumerator UpdateMatchEntry(string entryId, string playerId, string playerName, int gamePoints, int scoreVsPar, int finishPosition = 0)
        {
            Log($"<color=cyan>[Score Update] Hole completed for {playerName}: {gamePoints} pts, {scoreVsPar} vs par</color>");

            int adjustedScore = gamePoints + (scoreVsPar * -10);
            string json = "{" +
                $"\"game_points\":{gamePoints}," +
                $"\"over_under\":{scoreVsPar}," +
                $"\"score_vs_par\":{scoreVsPar}," +
                $"\"adjusted_match_score\":{adjustedScore}," +
                $"\"finish_position\":{finishPosition}," +
                $"\"notes\":\"Updated after hole completion\"" +
            "}";

            yield return _callApi($"/MatchEntry/{entryId}", "PUT", json, (res) =>
            {
                try
                {
                    JObject response = _parseApiSingleObject(res);
                    if (response != null)
                    {
                        Log($"<color=green>[Score Update] ✓ MatchEntry updated for {playerName}</color>");
                    }
                }
                catch (System.Exception ex)
                {
                    Log($"<color=yellow>[Score Update] Could not update MatchEntry: {ex.Message}</color>");
                }
            });
        }

        private IEnumerator SubmitMatchEntry(SBGLPlugin.MatchStats stats, System.Action<string> onMatchIdReceived)
        {
            if (stats == null || _currentSession == null) yield break;

            string seasonId = Season1RuleSet.SEASON_ID;
            bool isProSeries = stats.match_type != null && stats.match_type.Contains("pro_series");
            string apiMatchType = isProSeries ? "pro_series" : "mmr";
            string mode = isProSeries ? "Pro Series" : "Ranked";

            var payload = new JObject {
                ["matchmaking_session_id"] = _currentSession.id,
                ["season_id"] = seasonId,
                ["match_date"] = stats.match_date,
                ["match_type"] = apiMatchType,
                ["course_name"] = stats.course_name,
                ["player_count"] = 2,
                ["status"] = "Pending",
                ["submitted_by_name"] = stats.player_name,
                ["mode"] = mode,
                ["notes"] = "Auto-submitted via SBGL Unified Mod"
            };

            if (isProSeries) {
                payload["pro_series_season_id"] = Season1RuleSet.PRO_SERIES_SEASON_ID;

                int proSeriesWeek = PlayerPrefs.GetInt("ProSeriesWeek", 0);
                if (proSeriesWeek > 0)
                    payload["pro_series_week"] = proSeriesWeek;

                string proSeriesEventName = PlayerPrefs.GetString("ProSeriesEventName", "");
                if (!string.IsNullOrWhiteSpace(proSeriesEventName))
                    payload["pro_series_event_name"] = proSeriesEventName;
            }

            string json = payload.ToString(Newtonsoft.Json.Formatting.None);

            Log($"<color=cyan>[Match Stats] Submitting Match entry to API</color>");
            Log($"<color=cyan>[Match Stats] Full URL: {_getBaseApiUrl()}/Match</color>");
            Log($"<color=cyan>[Match Stats] Payload: {json}</color>");

            yield return _callApi("/Match", "POST", json, (res) =>
            {
                JObject response = _parseApiSingleObject(res);
                if (response != null)
                {
                    string entryId = (string)response["id"] ?? "unknown";
                    Log($"<color=green>[Match Stats] ✓ Match entry created (ID: {entryId})</color>");
                    onMatchIdReceived?.Invoke(entryId);
                }
                else
                {
                    Log("<color=yellow>[Match Stats] Response received but could not parse ID</color>");
                }
            });
        }

        private int GetPlayerFinishPosition(string playerName, List<LiveLeaderboardPlugin.SBGLPlayer> leaderboard)
        {
            if (string.IsNullOrEmpty(playerName) || leaderboard == null || leaderboard.Count == 0)
            {
                return 0;
            }

            for (int i = 0; i < leaderboard.Count; i++)
            {
                if (leaderboard[i] != null && leaderboard[i].Name == playerName)
                {
                    return i + 1;
                }
            }

            return 0;
        }
    }
}
