using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SBGL.UnifiedMod.Features.Poll
{
    public class PollManager : MonoBehaviour
    {
        public static PollManager Instance { get; private set; }

        // Active poll state
        private string _activePollId;
        private string _activePollQuestion;
        private string[] _activePollOptions;
        private Dictionary<ulong, int> _votes = new Dictionary<ulong, int>();
        private float _pollEndTime;
        private ulong _pollHostSteamId;
        private bool _hasVoted;

        // Results snapshot updated from incoming SBGL_POLL_VOTE packets
        private int[] _voteCounts = new int[0];

        // Window positions (draggable)
        private Rect _pollWindowRect   = new Rect(0f, 0f, 450f, 0f);
        private Rect _createWindowRect = new Rect(20f, 200f, 330f, 380f);
        private bool _pollWindowPositioned;

        // Create panel state
        private bool _showCreatePanel;
        private int _presetIndex = -1;
        private string _customQuestion = "";
        private string[] _customOptions = { "", "", "", "" };
        private int _customOptionCount = 2;

        private const float POLL_DURATION    = 30f;
        private const int   POLL_WINDOW_ID   = 0x5B670001;
        private const int   CREATE_WINDOW_ID = 0x5B670002;
        private const string PREFIX_START  = "SBGL_POLL_START:";
        private const string PREFIX_VOTE   = "SBGL_POLL_VOTE:";
        private const string PREFIX_CANCEL = "SBGL_POLL_CANCEL:";

        private static readonly Key[] VoteKeys = { Key.F1, Key.F2, Key.F3, Key.F4 };

        private static readonly (string Question, string[] Options)[] Presets =
        {
            ("Ready to start?",  new[] { "Ready!", "Not yet" }),
            ("How many holes?",  new[] { "9", "15", "18" }),
            ("Skip this hole?",  new[] { "Yes", "No" }),
        };

        // Textures
        private Texture2D _texDark;
        private Texture2D _texBtn;
        private Texture2D _texBtnHover;
        private Texture2D _texVoted;
        private Texture2D _texVotedHover;
        private Texture2D _texCancel;
        private Texture2D _texCancelHover;
        private Texture2D _texAccent;
        private Texture2D _texAccentHover;
        private Texture2D _texField;
        private Texture2D _texDivider;

        // Styles
        private GUIStyle _windowStyle;
        private GUIStyle _questionStyle;
        private GUIStyle _optionStyle;
        private GUIStyle _votedStyle;
        private GUIStyle _hintStyle;
        private GUIStyle _cancelStyle;
        private GUIStyle _launchStyle;
        private GUIStyle _presetStyle;
        private GUIStyle _presetActiveStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _fieldStyle;

        // Aliases kept for shared code paths
        private GUIStyle _centeredLabel;
        private GUIStyle _voteButton;
        private GUIStyle _votedButton;
        private GUIStyle _hintLabel;

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            // F7 toggles the create panel
            if (kb[Key.F7].wasPressedThisFrame)
                _showCreatePanel = !_showCreatePanel;

            // F1–F4 cast votes when a poll is active and user hasn't voted yet
            if (_activePollId != null && !_hasVoted)
            {
                for (int i = 0; i < VoteKeys.Length; i++)
                {
                    if (i < (_activePollOptions?.Length ?? 0) && kb[VoteKeys[i]].wasPressedThisFrame)
                    {
                        CastVote(i);
                        break;
                    }
                }
            }

            // Close poll 20 seconds after voting ends
            if (_activePollId != null && Time.time > _pollEndTime + 20f)
                ClearActivePoll();
        }

        // -----------------------------------------------------------------------
        // Packet routing — called by CompetitivePluginCheck.ListenForModReports
        // -----------------------------------------------------------------------
        public void HandlePacket(string msg, ulong senderId)
        {
            if (msg.StartsWith(PREFIX_START))
                HandleStart(msg.Substring(PREFIX_START.Length), senderId);
            else if (msg.StartsWith(PREFIX_VOTE))
                HandleVote(msg.Substring(PREFIX_VOTE.Length));
            else if (msg.StartsWith(PREFIX_CANCEL))
                HandleCancel(msg.Substring(PREFIX_CANCEL.Length));
        }

        // Format: <pollId>|<question>|<opt0>|<opt1>|...
        private void HandleStart(string payload, ulong hostId)
        {
            string[] parts = payload.Split('|');
            if (parts.Length < 3) return;

            _activePollId       = parts[0];
            _activePollQuestion = parts[1];
            _activePollOptions  = parts.Skip(2).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            _votes.Clear();
            _voteCounts         = new int[_activePollOptions.Length];
            _pollEndTime        = Time.time + POLL_DURATION;
            _pollHostSteamId    = hostId;
            _hasVoted           = false;
            _pollWindowPositioned = false; // re-center for new poll

            UnityEngine.Debug.Log($"[SBGL-Poll] Received poll from {hostId}: {_activePollQuestion}");
        }

        // Format: <pollId>|<optionIndex>|<voterSteamId>
        private void HandleVote(string payload)
        {
            string[] parts = payload.Split('|');
            if (parts.Length < 3) return;
            if (parts[0] != _activePollId) return;

            if (!int.TryParse(parts[1], out int optIdx)) return;
            if (!ulong.TryParse(parts[2], out ulong voterId)) return;
            if (optIdx < 0 || optIdx >= _voteCounts.Length) return;

            if (_votes.TryGetValue(voterId, out int prev) && prev >= 0 && prev < _voteCounts.Length)
                _voteCounts[prev] = Mathf.Max(0, _voteCounts[prev] - 1);

            _votes[voterId] = optIdx;
            _voteCounts[optIdx]++;
        }

        private void HandleCancel(string pollId)
        {
            if (pollId == _activePollId)
                ClearActivePoll();
        }

        private void ClearActivePoll()
        {
            _activePollId       = null;
            _activePollQuestion = null;
            _activePollOptions  = null;
            _votes.Clear();
            _voteCounts         = new int[0];
            _hasVoted           = false;
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------
        public void StartPoll(string question, string[] options)
        {
            if (!SteamClient.IsValid) return;

            string pollId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _activePollId         = pollId;
            _activePollQuestion   = question;
            _activePollOptions    = options;
            _votes.Clear();
            _voteCounts           = new int[options.Length];
            _pollEndTime          = Time.time + POLL_DURATION;
            _pollHostSteamId      = SteamClient.SteamId;
            _hasVoted             = false;
            _pollWindowPositioned = false;

            string optStr = string.Join("|", options);
            BroadcastToAll($"{PREFIX_START}{pollId}|{question}|{optStr}");
            UnityEngine.Debug.Log($"[SBGL-Poll] Started poll: {question}");
        }

        public void CastVote(int optionIndex)
        {
            if (_hasVoted || _activePollId == null) return;
            if (optionIndex < 0 || optionIndex >= _activePollOptions.Length) return;

            _hasVoted = true;

            if (_votes.TryGetValue(SteamClient.SteamId, out int prev) && prev >= 0 && prev < _voteCounts.Length)
                _voteCounts[prev] = Mathf.Max(0, _voteCounts[prev] - 1);
            _votes[SteamClient.SteamId] = optionIndex;
            _voteCounts[optionIndex]++;

            BroadcastToAll($"{PREFIX_VOTE}{_activePollId}|{optionIndex}|{SteamClient.SteamId}");
        }

        private void BroadcastToAll(string msg)
        {
            var compCheck = CompetitivePluginCheck.CompetitivePluginCheck.Instance;
            if (compCheck == null) return;

            byte[] data = Encoding.UTF8.GetBytes(msg);
            foreach (ulong peer in compCheck.GetKnownPeerIds())
            {
                try { SteamNetworking.SendP2PPacket(peer, data, -1, CompetitivePluginCheck.CompetitivePluginCheck.NetChannel, P2PSend.Reliable); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[SBGL-Poll] Send failed to {peer}: {ex.Message}"); }
            }
        }

        // -----------------------------------------------------------------------
        // OnGUI
        // -----------------------------------------------------------------------
        void OnGUI()
        {
            EnsureStyles();

            // Absorb Escape while a poll is active so the game doesn't dismiss the overlay
            if (_activePollId != null &&
                Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.Escape)
            {
                Event.current.Use();
            }

            if (_activePollId != null)
            {
                int optCount = _activePollOptions?.Length ?? 0;
                _pollWindowRect.height = 110f + optCount * 46f;

                if (!_pollWindowPositioned)
                {
                    _pollWindowRect.x = Screen.width  - _pollWindowRect.width - 20f;
                    _pollWindowRect.y = (Screen.height - _pollWindowRect.height) / 2f;
                    _pollWindowPositioned = true;
                }

                bool votingOpen = Time.time <= _pollEndTime;
                float timeLeft  = Mathf.Max(0f, _pollEndTime - Time.time);
                string title = votingOpen
                    ? $"VOTE  —  {timeLeft:F0}s remaining"
                    : "RESULTS";

                _pollWindowRect = GUI.Window(POLL_WINDOW_ID, _pollWindowRect, DrawPollWindow, title, _windowStyle);
            }

            if (_showCreatePanel)
                _createWindowRect = GUI.Window(CREATE_WINDOW_ID, _createWindowRect, DrawCreateWindow, "Create Poll  [F7]", _windowStyle);
        }

        private static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, col);
            t.Apply();
            return t;
        }

        private void EnsureStyles()
        {
            if (_windowStyle != null) return;

            _texDark        = MakeTex(new Color(0.07f, 0.08f, 0.10f, 0.94f));
            _texBtn         = MakeTex(new Color(0.17f, 0.19f, 0.23f, 1f));
            _texBtnHover    = MakeTex(new Color(0.25f, 0.28f, 0.34f, 1f));
            _texVoted       = MakeTex(new Color(0.08f, 0.44f, 0.13f, 1f));
            _texVotedHover  = MakeTex(new Color(0.10f, 0.54f, 0.16f, 1f));
            _texCancel      = MakeTex(new Color(0.50f, 0.09f, 0.09f, 1f));
            _texCancelHover = MakeTex(new Color(0.63f, 0.11f, 0.11f, 1f));
            _texAccent      = MakeTex(new Color(0.10f, 0.36f, 0.58f, 1f));
            _texAccentHover = MakeTex(new Color(0.14f, 0.46f, 0.70f, 1f));
            _texField       = MakeTex(new Color(0.12f, 0.14f, 0.17f, 1f));
            _texDivider     = MakeTex(new Color(0.28f, 0.30f, 0.36f, 0.9f));

            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                normal   = { background = _texDark, textColor = new Color(1f, 0.83f, 0.20f) },
                onNormal = { background = _texDark, textColor = new Color(1f, 0.83f, 0.20f) },
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter,
                padding   = new RectOffset(0, 0, 26, 6),
                border    = new RectOffset(2, 2, 2, 2)
            };

            _questionStyle = new GUIStyle(GUI.skin.label)
            {
                normal    = { textColor = Color.white },
                fontSize  = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = true
            };

            _optionStyle = new GUIStyle(GUI.skin.button)
            {
                normal  = { background = _texBtn,      textColor = Color.white },
                hover   = { background = _texBtnHover, textColor = Color.white },
                active  = { background = _texBtnHover, textColor = Color.white },
                focused = { background = _texBtn,      textColor = Color.white },
                fontSize  = 13,
                alignment = TextAnchor.MiddleCenter,
                border    = new RectOffset(2, 2, 2, 2)
            };

            _votedStyle = new GUIStyle(_optionStyle)
            {
                normal  = { background = _texVoted,      textColor = Color.white },
                hover   = { background = _texVotedHover, textColor = Color.white },
                active  = { background = _texVotedHover, textColor = Color.white },
                focused = { background = _texVoted,      textColor = Color.white },
                fontStyle = FontStyle.Bold
            };

            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                normal    = { textColor = new Color(0.55f, 0.60f, 0.68f, 1f) },
                fontSize  = 11,
                alignment = TextAnchor.MiddleRight
            };

            _cancelStyle = new GUIStyle(_optionStyle)
            {
                normal  = { background = _texCancel,      textColor = Color.white },
                hover   = { background = _texCancelHover, textColor = Color.white },
                active  = { background = _texCancelHover, textColor = Color.white },
                focused = { background = _texCancel,      textColor = Color.white },
                fontSize = 11
            };

            _launchStyle = new GUIStyle(_optionStyle)
            {
                normal  = { background = _texAccent,      textColor = Color.white },
                hover   = { background = _texAccentHover, textColor = Color.white },
                active  = { background = _texAccentHover, textColor = Color.white },
                focused = { background = _texAccent,      textColor = Color.white },
                fontSize  = 13,
                fontStyle = FontStyle.Bold
            };

            _presetStyle = new GUIStyle(_optionStyle) { fontSize = 12 };

            _presetActiveStyle = new GUIStyle(_votedStyle) { fontSize = 12 };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                normal    = { textColor = new Color(0.72f, 0.76f, 0.84f, 1f) },
                fontSize  = 12
            };

            _fieldStyle = new GUIStyle(GUI.skin.textField)
            {
                normal  = { background = _texField, textColor = Color.white },
                focused = { background = _texBtnHover, textColor = Color.white },
                fontSize = 12
            };

            // Aliases for shared code
            _centeredLabel = _questionStyle;
            _voteButton    = _optionStyle;
            _votedButton   = _votedStyle;
            _hintLabel     = _hintStyle;
        }

        private void DrawPollWindow(int windowId)
        {
            int   optCount   = _activePollOptions?.Length ?? 0;
            float w          = _pollWindowRect.width - 20f;
            bool  votingOpen = Time.time <= _pollEndTime;
            float cy         = 30f;

            // Question
            GUI.Label(new Rect(10f, cy, w, 44f), _activePollQuestion ?? "", _questionStyle);
            cy += 48f;

            // Divider
            GUI.DrawTexture(new Rect(10f, cy, w, 1f), _texDivider);
            cy += 10f;

            // Option buttons
            for (int i = 0; i < optCount; i++)
            {
                int    count = _voteCounts != null && i < _voteCounts.Length ? _voteCounts[i] : 0;
                int    total = _votes.Count;
                string pct   = total > 0 ? $"  {Mathf.RoundToInt(100f * count / total)}%" : "";
                string lbl   = $"{_activePollOptions[i]}   ({count}{pct})";

                bool myVote = _votes.TryGetValue(SteamClient.IsValid ? (ulong)SteamClient.SteamId : 0UL, out int v) && v == i;

                GUI.enabled = votingOpen && !_hasVoted;
                if (GUI.Button(new Rect(10f, cy, w, 38f), lbl, myVote ? _votedStyle : _optionStyle))
                    CastVote(i);
                GUI.enabled = true;

                if (votingOpen && !_hasVoted && i < VoteKeys.Length)
                    GUI.Label(new Rect(10f, cy, w - 6f, 38f), $"[F{i + 1}]", _hintStyle);

                cy += 46f;
            }

            // Cancel (host only)
            if (SteamClient.IsValid && SteamClient.SteamId == _pollHostSteamId)
            {
                if (GUI.Button(new Rect(_pollWindowRect.width - 86f, 4f, 76f, 22f), "✕  Cancel", _cancelStyle))
                {
                    BroadcastToAll($"{PREFIX_CANCEL}{_activePollId}");
                    ClearActivePoll();
                }
            }

            GUI.DragWindow(new Rect(0f, 0f, _pollWindowRect.width, 28f));
        }

        private void DrawCreateWindow(int windowId)
        {
            float cx = 10f;
            float cy = 28f;
            float cw = _createWindowRect.width - 20f;

            GUI.Label(new Rect(cx, cy, cw, 18f), "Quick polls:", _labelStyle);
            cy += 20f;

            for (int i = 0; i < Presets.Length; i++)
            {
                GUIStyle s = (_presetIndex == i) ? _presetActiveStyle : _presetStyle;
                if (GUI.Button(new Rect(cx, cy, cw, 28f), Presets[i].Question, s))
                    _presetIndex = (_presetIndex == i) ? -1 : i;
                cy += 32f;
            }

            // Divider
            GUI.DrawTexture(new Rect(cx, cy + 4f, cw, 1f), _texDivider);
            cy += 14f;

            GUI.Label(new Rect(cx, cy, cw, 18f), "Custom poll:", _labelStyle);
            cy += 20f;

            GUI.Label(new Rect(cx, cy, 62f, 20f), "Question:", _labelStyle);
            _customQuestion = GUI.TextField(new Rect(cx + 66f, cy, cw - 66f, 22f), _customQuestion, 80, _fieldStyle);
            cy += 28f;

            for (int i = 0; i < _customOptionCount; i++)
            {
                GUI.Label(new Rect(cx, cy, 52f, 20f), $"Option {i + 1}:", _labelStyle);
                _customOptions[i] = GUI.TextField(new Rect(cx + 56f, cy, cw - 56f, 22f), _customOptions[i] ?? "", 40, _fieldStyle);
                cy += 26f;
            }

            if (_customOptionCount < 4 && GUI.Button(new Rect(cx, cy, 32f, 24f), "+", _presetStyle))
                _customOptionCount++;
            if (_customOptionCount > 2 && GUI.Button(new Rect(cx + 38f, cy, 32f, 24f), "−", _presetStyle))
            {
                _customOptions[_customOptionCount - 1] = "";
                _customOptionCount--;
            }
            cy += 32f;

            if (GUI.Button(new Rect(cx, cy, cw, 30f), "Launch Poll", _launchStyle))
                TryLaunchPoll();

            GUI.DragWindow(new Rect(0f, 0f, _createWindowRect.width, 28f));
        }

        private void TryLaunchPoll()
        {
            string question;
            string[] options;

            if (_presetIndex >= 0 && _presetIndex < Presets.Length)
            {
                question = Presets[_presetIndex].Question;
                options  = Presets[_presetIndex].Options;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_customQuestion)) return;
                var opts = _customOptions.Take(_customOptionCount).Where(o => !string.IsNullOrWhiteSpace(o)).ToArray();
                if (opts.Length < 2) return;
                question = _customQuestion.Trim();
                options  = opts.Select(o => o.Trim()).ToArray();
            }

            StartPoll(question, options);
            _showCreatePanel = false;
            _presetIndex     = -1;
            _customQuestion  = "";
            for (int i = 0; i < _customOptions.Length; i++) _customOptions[i] = "";
        }
    }
}
