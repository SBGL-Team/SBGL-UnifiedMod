using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

namespace SBGL.UnifiedMod.Features
{
    /// <summary>
    /// Pseudo Dedicated Server feature.
    ///
    /// Automates the full matchmaking → match → return loop so the client acts as a
    /// self-cycling host node.  This client is ALWAYS the host: when a session is
    /// detected, the mod immediately PATCHes host_player_id to our player ID before
    /// accepting, so the existing host-flow in SBGLPlugin takes over automatically.
    ///
    /// Cycle (main menu only — queue cannot be started from driving range):
    ///   Main Menu  →  auto-join queue
    ///   Session pending_accept  →  claim host (PATCH host_player_id)  →  auto-accept
    ///   Session ready  →  store ruleset in PlayerPrefs  →  auto-initialize host lobby
    ///   Driving Range (match over)  →  wait requeue delay  →  return to main menu
    ///   Main Menu (again)  →  repeat
    /// </summary>
    public class PseudoDedicatedServer : MonoBehaviour
    {
        // ── Config ────────────────────────────────────────────────────────────────
        private ConfigEntry<bool>   _enabled;
        private ConfigEntry<float>  _requeueDelay;   // seconds to wait on driving range before returning to menu
        private ConfigEntry<string> _defaultRuleset; // "ranked" or "pro_series"
        private ManualLogSource     _logger;

        // ── State ────────────────────────────────────────────────────────────────
        private bool  _inMainMenu           = false;
        private bool  _inDrivingRange       = false;
        private bool  _hasQueuedThisCycle   = false;
        private bool  _hasClaimedHostRole   = false;
        private bool  _hasAcceptedThisCycle = false;
        private bool  _hasHostedThisCycle   = false;
        private bool  _waitingToReturnToMenu = false;
        private float _returnToMenuAt       = 0f;

        private SBGLeagueAutomation.SBGLPlugin _matchmakingPlugin;

        // ── Public setup ─────────────────────────────────────────────────────────
        public void SetConfig(
            ConfigEntry<bool>   enabled,
            ConfigEntry<float>  requeueDelay,
            ConfigEntry<string> defaultRuleset,
            ManualLogSource     logger)
        {
            _enabled        = enabled;
            _requeueDelay   = requeueDelay;
            _defaultRuleset = defaultRuleset;
            _logger         = logger;
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────────
        private void Awake()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            Log("[PDS] Pseudo Dedicated Server component loaded.");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            string name = scene.name.ToLower();

            _inMainMenu     = name.Contains("menu");
            _inDrivingRange = name.Contains("drivingrange") || name.Contains("driving range");

            if (!IsEnabled()) return;

            if (_inMainMenu)
            {
                Log("[PDS] Entered main menu — resetting cycle state.");
                _hasQueuedThisCycle   = false;
                _hasClaimedHostRole   = false;
                _hasAcceptedThisCycle = false;
                _hasHostedThisCycle   = false;
                _waitingToReturnToMenu = false;

                // Delay so SBGLPlugin finishes its own scene-load work first (syncs existing queue entry etc.)
                StartCoroutine(TryAutoQueueAfterDelay(2.5f));
            }
            else if (_inDrivingRange)
            {
                Log("[PDS] Entered driving range.");
                if (!_waitingToReturnToMenu)
                {
                    _waitingToReturnToMenu = true;
                    float delay = _requeueDelay?.Value ?? 10f;
                    _returnToMenuAt = Time.time + delay;
                    Log($"[PDS] Will return to main menu in {delay}s to requeue.");
                }
            }
            else
            {
                // Gameplay scene — clear the driving-range return flag
                _waitingToReturnToMenu = false;
            }
        }

        private void Update()
        {
            if (!IsEnabled()) return;

            // Return to main menu once the requeue delay elapses on the driving range
            if (_inDrivingRange && _waitingToReturnToMenu && Time.time >= _returnToMenuAt)
            {
                _waitingToReturnToMenu = false;
                Log("[PDS] Requeue delay elapsed — returning to main menu.");
                ReturnToMainMenu();
                return;
            }

            // While in the main menu, watch session state and fire host actions
            if (_inMainMenu)
                PollSessionActions();
        }

        // ── Core automation ───────────────────────────────────────────────────────

        /// <summary>
        /// Called every frame while in the main menu.  Drives the claim-host → accept →
        /// initialize-host sequence exactly once per match cycle.
        /// </summary>
        private void PollSessionActions()
        {
            var plugin = GetMatchmakingPlugin();
            if (plugin == null) return;

            var session = plugin.CurrentSession;
            if (session == null) return;

            // Step 1 — claim host role so host_player_id == our ID in the API
            if (!_hasClaimedHostRole && !plugin.IsHost &&
                (session.status == "pending_accept" || session.status == "ready"))
            {
                _hasClaimedHostRole = true;
                Log("[PDS] Claiming host role (PATCHing host_player_id)...");
                StartCoroutine(ClaimThenAccept(plugin));
                return;
            }

            // Step 2 — if claim already done but accept not yet sent, send it
            if (_hasClaimedHostRole && !_hasAcceptedThisCycle && !plugin.HasAccepted
                && session.status == "pending_accept")
            {
                _hasAcceptedThisCycle = true;
                Log("[PDS] Auto-accepting match...");
                StartCoroutine(plugin.AcceptMatchCoroutine());
            }

            // Step 3 — once session is ready and we are host, launch the lobby
            if (session.status == "ready" && plugin.IsHost &&
                plugin.HasAccepted && !_hasHostedThisCycle)
            {
                _hasHostedThisCycle = true;
                Log($"[PDS] Auto-initializing host lobby (ruleset: {GetRuleset()})...");
                PlayerPrefs.SetString("HostRuleset", GetRuleset());
                PlayerPrefs.Save();
                plugin.InitiateHostSequencePublic();
            }
        }

        /// <summary>Claim host role then immediately accept — avoids a 5-second polling gap.</summary>
        private IEnumerator ClaimThenAccept(SBGLeagueAutomation.SBGLPlugin plugin)
        {
            yield return plugin.ClaimHostRoleCoroutine();

            // Accept after claiming (only if still pending and not already accepted)
            if (plugin.CurrentSession != null &&
                plugin.CurrentSession.status == "pending_accept" &&
                !_hasAcceptedThisCycle && !plugin.HasAccepted)
            {
                _hasAcceptedThisCycle = true;
                Log("[PDS] Claimed host — auto-accepting match...");
                yield return plugin.AcceptMatchCoroutine();
            }
        }

        private IEnumerator TryAutoQueueAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);

            if (!IsEnabled() || !_inMainMenu || _hasQueuedThisCycle) yield break;

            var plugin = GetMatchmakingPlugin();
            if (plugin == null)
            {
                Log("[PDS] SBGLPlugin not found — cannot auto-queue.");
                yield break;
            }

            // Already queuing or in a live session — mark queued so we don't double-fire
            if (plugin.IsQueueing || plugin.CurrentSession != null)
            {
                Log("[PDS] Already queuing or in session — skipping auto-queue.");
                _hasQueuedThisCycle = true;
                yield break;
            }

            // Wait for profile resolution (up to 15 s)
            if (plugin.UserProfile == null)
            {
                Log("[PDS] Profile not yet resolved — waiting up to 15s...");
                float waited = 0f;
                while (plugin.UserProfile == null && waited < 15f)
                {
                    yield return new WaitForSeconds(1f);
                    waited += 1f;
                }
                if (plugin.UserProfile == null)
                {
                    Log("[PDS] Profile still unresolved — aborting auto-queue.");
                    yield break;
                }
            }

            _hasQueuedThisCycle = true;
            Log("[PDS] Auto-joining queue...");
            StartCoroutine(plugin.MatchmakingLoopCoroutine());
        }

        /// <summary>
        /// Loads the main menu scene.  We are currently on the driving range so we cannot
        /// use the MainMenu MonoBehaviour — a direct SceneManager load is the right call.
        /// </summary>
        private void ReturnToMainMenu()
        {
            Log("[PDS] Loading MainMenu scene...");
            try
            {
                SceneManager.LoadScene("MainMenu");
            }
            catch (System.Exception ex)
            {
                Log($"[PDS] LoadScene('MainMenu') failed ({ex.Message}) — trying scene index 0.");
                try { SceneManager.LoadScene(0); }
                catch (System.Exception ex2) { Log($"[PDS] Fallback LoadScene(0) also failed: {ex2.Message}"); }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private bool IsEnabled() => _enabled?.Value ?? false;

        private string GetRuleset() => _defaultRuleset?.Value ?? "ranked";

        private SBGLeagueAutomation.SBGLPlugin GetMatchmakingPlugin()
        {
            if (_matchmakingPlugin != null) return _matchmakingPlugin;
            _matchmakingPlugin = FindAnyObjectByType<SBGLeagueAutomation.SBGLPlugin>();
            return _matchmakingPlugin;
        }

        private void Log(string msg)
        {
            if (_logger != null)
                _logger.LogInfo(msg);
            else
                Debug.Log(msg);
        }
    }
}
