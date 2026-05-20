using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SBGL.UnifiedMod.Features.HitTracker
{
    /// <summary>
    /// Tracks per-player, per-hole and cumulative hit counts to help identify
    /// targeting or teaming behaviour.  Toggle the overlay with F6.
    /// </summary>
    public class HitTrackerPlugin : MonoBehaviour
    {
        // ── Singleton ────────────────────────────────────────────────────────
        public static HitTrackerPlugin Instance { get; private set; }

        // ── Hit data ─────────────────────────────────────────────────────────
        // _hitsPerHole[holeIndex][attacker][victim] = count  (completed holes)
        private readonly Dictionary<int, Dictionary<string, Dictionary<string, int>>> _hitsPerHole
            = new Dictionary<int, Dictionary<string, Dictionary<string, int>>>();

        // Current hole accumulator: [attacker][victim] = count
        private readonly Dictionary<string, Dictionary<string, int>> _hitsCurrentHole
            = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        // Match-wide totals: [attacker][victim] = count
        private readonly Dictionary<string, Dictionary<string, int>> _hitsTotal
            = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _allPlayers
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Hole tracking ─────────────────────────────────────────────────────
        private int _lastHoleIndex = -1;

        // ── UI state ──────────────────────────────────────────────────────────
        private bool _showWindow = false;
        private Key ToggleKey => Key.F6;

        private Rect _windowRect = new Rect(10, 10, 520, 380);
        private const int WindowId = 9876543;
        private Vector2 _scrollMain = Vector2.zero;
        private int _selectedHole = -2;   // -2 = totals, -1 = current hole, >=0 = past hole

        private GUIStyle  _headerStyle;
        private GUIStyle  _cellStyle;
        private GUIStyle  _attackerStyle;
        private GUIStyle  _hitStyle;
        private GUIStyle  _windowStyle;
        private Texture2D _bgTexture;
        private bool _stylesInit = false;

        // ── Unity lifecycle ───────────────────────────────────────────────────
        void Awake()
        {
            Instance = this;
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current[ToggleKey].wasPressedThisFrame)
                _showWindow = !_showWindow;

            // Poll hole index – save outgoing hole data when it changes
            try
            {
                int currentHole = CourseManager.CurrentHoleGlobalIndex;
                if (_lastHoleIndex >= 0 && currentHole != _lastHoleIndex)
                    ArchiveCurrentHole(_lastHoleIndex);
                _lastHoleIndex = currentHole;
            }
            catch { /* CourseManager not ready yet */ }
        }

        // ── Public API (called by Harmony patch) ──────────────────────────────
        public void RecordHit(string attackerName, string victimName)
        {
            if (string.IsNullOrWhiteSpace(attackerName) || string.IsNullOrWhiteSpace(victimName))
                return;

            _allPlayers.Add(attackerName);
            _allPlayers.Add(victimName);

            Increment(_hitsCurrentHole, attackerName, victimName);
            Increment(_hitsTotal, attackerName, victimName);
        }

        public void ResetAll()
        {
            _hitsPerHole.Clear();
            _hitsCurrentHole.Clear();
            _hitsTotal.Clear();
            _allPlayers.Clear();
            _lastHoleIndex = -1;
            _selectedHole  = -1;
        }

        // ── Internal helpers ──────────────────────────────────────────────────
        private void ArchiveCurrentHole(int holeIndex)
        {
            if (_hitsCurrentHole.Count > 0)
                _hitsPerHole[holeIndex] = DeepCopy(_hitsCurrentHole);
            _hitsCurrentHole.Clear();
        }

        private static void Increment(
            Dictionary<string, Dictionary<string, int>> table,
            string attacker, string victim)
        {
            if (!table.TryGetValue(attacker, out var inner))
            {
                inner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                table[attacker] = inner;
            }
            inner.TryGetValue(victim, out int prev);
            inner[victim] = prev + 1;
        }

        private static Dictionary<string, Dictionary<string, int>> DeepCopy(
            Dictionary<string, Dictionary<string, int>> src)
        {
            var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in src)
                result[kvp.Key] = new Dictionary<string, int>(kvp.Value, StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // ── GUI ───────────────────────────────────────────────────────────────
        void OnGUI()
        {
            if (!_showWindow) return;
            if (!_stylesInit) InitStyles();

            // Auto-size width so all player columns are always visible
            if (_allPlayers.Count > 0)
            {
                const float nameW = 90f, minCell = 38f, totalW = 50f, pad = 28f;
                float needed = nameW + minCell * _allPlayers.Count + totalW + pad;
                if (_windowRect.width < needed) _windowRect.width = needed;
            }

            _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow,
                $"Hit Tracker · H{_lastHoleIndex + 1} · {ToggleKey}", _windowStyle);
        }

        private void InitStyles()
        {
            const int fs = 11;
            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = false,
                fontSize  = fs,
                normal    = { textColor = Color.white }
            };
            _attackerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap  = false,
                fontSize  = fs,
                normal    = { textColor = Color.white }
            };
            _cellStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = false,
                fontSize  = fs,
                normal    = { textColor = Color.white }
            };
            _hitStyle = new GUIStyle(_cellStyle)
            {
                normal    = { textColor = new Color(1f, 0.45f, 0.45f) },
                fontStyle = FontStyle.Bold
            };

            // Dark semi-transparent window background
            _bgTexture = new Texture2D(1, 1);
            _bgTexture.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.08f, 0.92f));
            _bgTexture.Apply();
            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                normal   = { background = _bgTexture, textColor = Color.white },
                onNormal = { background = _bgTexture, textColor = Color.white },
                padding  = new RectOffset(6, 6, 22, 6)
            };
            _stylesInit = true;
        }

        private void DrawWindow(int id)
        {
            var players = _allPlayers.OrderBy(p => p).ToList();

            // ── Tab bar ───────────────────────────────────────────────────────
            GUILayout.BeginHorizontal();

            GUI.color = _selectedHole == -2 ? Color.yellow : Color.white;
            if (GUILayout.Button("TOTALS", GUILayout.Width(60)))
                _selectedHole = -2;

            GUI.color = _selectedHole == -1 ? Color.yellow : Color.white;
            if (GUILayout.Button($"H{_lastHoleIndex + 1}▶", GUILayout.Width(46)))
                _selectedHole = -1;

            GUI.color = Color.white;
            foreach (int h in _hitsPerHole.Keys.OrderBy(x => x))
            {
                GUI.color = _selectedHole == h ? Color.yellow : Color.white;
                if (GUILayout.Button($"H{h + 1}", GUILayout.Width(34)))
                    _selectedHole = h;
                GUI.color = Color.white;
            }

            GUILayout.FlexibleSpace();
            GUI.color = new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button("Reset", GUILayout.Width(50)))
                ResetAll();
            GUI.color = Color.white;

            GUILayout.EndHorizontal();

            // ── Data source ───────────────────────────────────────────────────
            Dictionary<string, Dictionary<string, int>> data;
            string label;
            if (_selectedHole == -2)
            {
                data  = _hitsTotal;
                label = "MATCH TOTALS";
            }
            else if (_selectedHole == -1)
            {
                data  = _hitsCurrentHole;
                label = $"HOLE {_lastHoleIndex + 1} (LIVE)";
            }
            else
            {
                _hitsPerHole.TryGetValue(_selectedHole, out var hd);
                data  = hd ?? new Dictionary<string, Dictionary<string, int>>();
                label = $"HOLE {_selectedHole + 1}";
            }

            GUILayout.Label(label, _headerStyle);

            if (players.Count == 0)
                GUILayout.Label("No hits recorded yet.", _headerStyle);
            else
                DrawMatrix(data, players, ref _scrollMain, _windowRect.width - 22f);

            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));
        }

        /// <summary>
        /// Draws a hit matrix.  Rows = attacker, columns = victim.
        /// </summary>
        private void DrawMatrix(
            Dictionary<string, Dictionary<string, int>> data,
            List<string> players,
            ref Vector2 scroll,
            float panelWidth)
        {
            int n = players.Count;
            float nameW   = 90f;
            float cellW   = Mathf.Max(38f, (panelWidth - nameW - 70f) / n);
            float totalW  = 50f;

            // Compute per-player sent and received totals
            var sent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var recv = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int grandTotal = 0;
            foreach (var p in players) { sent[p] = 0; recv[p] = 0; }
            foreach (var kvpA in data)
                foreach (var kvpV in kvpA.Value)
                {
                    int c = kvpV.Value;
                    if (sent.ContainsKey(kvpA.Key)) sent[kvpA.Key] += c;
                    if (recv.ContainsKey(kvpV.Key)) recv[kvpV.Key] += c;
                    grandTotal += c;
                }

            float rowH = 20f;
            float scrollH = rowH * (n + 3) + 20f;
            scroll = GUILayout.BeginScrollView(scroll, false, false,
                GUIStyle.none, GUIStyle.none, GUIStyle.none,
                GUILayout.Height(Mathf.Min(scrollH, 300f)));

            // ── Header row ────────────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Label("↓Atk / Vic→", _headerStyle, GUILayout.Width(nameW));
            foreach (var victim in players)
                GUILayout.Label(Shorten(victim, 5), _headerStyle, GUILayout.Width(cellW));
            GUILayout.Label("SENT", _headerStyle, GUILayout.Width(totalW));
            GUILayout.EndHorizontal();

            // ── Data rows ─────────────────────────────────────────────────────
            foreach (var attacker in players)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Shorten(attacker, 10), _attackerStyle, GUILayout.Width(nameW));

                foreach (var victim in players)
                {
                    if (string.Equals(attacker, victim, StringComparison.OrdinalIgnoreCase))
                    {
                        GUILayout.Label("—", _cellStyle, GUILayout.Width(cellW));
                        continue;
                    }
                    int count = 0;
                    if (data.TryGetValue(attacker, out var victims))
                        victims.TryGetValue(victim, out count);

                    GUILayout.Label(count.ToString(),
                        count > 0 ? _hitStyle : _cellStyle,
                        GUILayout.Width(cellW));
                }
                GUILayout.Label(sent[attacker].ToString(), _headerStyle, GUILayout.Width(totalW));
                GUILayout.EndHorizontal();
            }

            // ── Received totals row ───────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Label("RECV", _headerStyle, GUILayout.Width(nameW));
            foreach (var victim in players)
                GUILayout.Label(recv[victim].ToString(), _headerStyle, GUILayout.Width(cellW));
            GUILayout.Label(grandTotal.ToString(), _headerStyle, GUILayout.Width(totalW));
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();
        }

        private static string Shorten(string name, int maxLen)
            => name.Length > maxLen ? name.Substring(0, maxLen) : name;

        /// <summary>
        /// Returns a compact JSON object of non-zero match-total hits, e.g.:
        /// {"Charlie Ki":{"Mehexo":3},"washbura":{"GrootAmI":1}}
        /// Returns null when no hits have been recorded.
        /// </summary>
        public string GetMatchTotalsJson()
        {
            if (_hitsTotal.Count == 0) return null;

            var sb = new System.Text.StringBuilder("{");
            bool firstAtk = true;
            foreach (var atkKvp in _hitsTotal.OrderBy(k => k.Key))
            {
                var nonZero = atkKvp.Value.Where(v => v.Value > 0).OrderBy(k => k.Key).ToList();
                if (nonZero.Count == 0) continue;
                if (!firstAtk) sb.Append(',');
                firstAtk = false;
                string atkName = atkKvp.Key.Replace("\\", "\\\\").Replace("\"", "\\\"");
                sb.Append('"').Append(atkName).Append("\":");
                sb.Append('{');
                bool firstVic = true;
                foreach (var vicKvp in nonZero)
                {
                    if (!firstVic) sb.Append(',');
                    firstVic = false;
                    string vicName = vicKvp.Key.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    sb.Append('"').Append(vicName).Append('"').Append(':').Append(vicKvp.Value);
                }
                sb.Append('}');
            }
            sb.Append('}');
            // Return null when every attacker had only zero-count entries
            return firstAtk ? null : sb.ToString();
        }
    }

    // ── Harmony patch ─────────────────────────────────────────────────────────
    // Patching the private method that runs whenever ANY hittable is hit by a
    // golf swing (fires both on server via Cmd and on every client via Rpc).
    [HarmonyPatch]
    public static class HitWithGolfSwingPatch
    {
        [HarmonyTargetMethod]
        public static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Hittable");
            return AccessTools.Method(t, "HitWithGolfSwingInternal",
                new Type[]
                {
                    typeof(UnityEngine.Vector3),  // localHitPosition
                    typeof(UnityEngine.Vector3),  // localOrigin
                    typeof(UnityEngine.Vector3),  // worldDirection
                    typeof(bool),                 // isPutt
                    typeof(float),                // power
                    typeof(float),                // sideSpin
                    typeof(bool),                 // isRocketDriver
                    AccessTools.TypeByName("PlayerGolfer"),
                    AccessTools.TypeByName("Hittable")
                });
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, object hitter)
        {
            try
            {
                if (__instance == null || hitter == null) return;

                // Only record when the VICTIM entity is a player (not a ball/tee/item)
                var hittable   = __instance as UnityEngine.Component;
                if (hittable == null) return;

                var asEntity   = hittable.GetComponent("Entity");
                if (asEntity == null) return;

                var isPlayerProp = asEntity.GetType().GetProperty("IsPlayer");
                if (isPlayerProp == null) return;
                bool isPlayer = (bool)isPlayerProp.GetValue(asEntity);
                if (!isPlayer) return;

                string victimName  = GetPlayerName(asEntity, "PlayerInfo");
                string attackerName = GetPlayerNameFromGolfer(hitter);

                if (string.IsNullOrWhiteSpace(victimName) || string.IsNullOrWhiteSpace(attackerName))
                    return;
                if (string.Equals(victimName, attackerName, StringComparison.OrdinalIgnoreCase))
                    return;

                HitTrackerPlugin.Instance?.RecordHit(attackerName, victimName);
            }
            catch { /* never crash the game */ }
        }

        private static string GetPlayerName(UnityEngine.Component entity, string playerInfoPropName)
        {
            var playerInfoProp = entity.GetType().GetProperty(playerInfoPropName);
            if (playerInfoProp == null) return null;
            var playerInfo = playerInfoProp.GetValue(entity) as UnityEngine.Component;
            if (playerInfo == null) return null;

            var playerIdProp = playerInfo.GetType().GetProperty("PlayerId");
            if (playerIdProp == null) return null;
            var playerId = playerIdProp.GetValue(playerInfo);
            if (playerId == null) return null;

            var nameProp = playerId.GetType().GetProperty("PlayerName");
            return nameProp?.GetValue(playerId) as string;
        }

        private static string GetPlayerNameFromGolfer(object golfer)
        {
            var golferComp = golfer as UnityEngine.Component;
            if (golferComp == null) return null;

            var playerInfoProp = golferComp.GetType().GetProperty("PlayerInfo");
            if (playerInfoProp == null) return null;
            var playerInfo = playerInfoProp.GetValue(golferComp) as UnityEngine.Component;
            if (playerInfo == null) return null;

            var playerIdProp = playerInfo.GetType().GetProperty("PlayerId");
            if (playerIdProp == null) return null;
            var playerId = playerIdProp.GetValue(playerInfo);
            if (playerId == null) return null;

            var nameProp = playerId.GetType().GetProperty("PlayerName");
            return nameProp?.GetValue(playerId) as string;
        }
    }
}
