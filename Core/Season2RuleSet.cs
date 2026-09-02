using System.Collections.Generic;
using System.Text;

namespace SBGL.UnifiedMod.Core
{
    public static class Season2RuleSet
    {
        public const int SEASON = 2;

        // Internal identifiers — stored in PlayerPrefs and used for logic branching
        public const string MATCH_TYPE_CASUAL     = "casual";
        public const string MATCH_TYPE_RANKED     = "ranked_season_2";
        public const string MATCH_TYPE_PRO_SERIES = "pro_series_season_2";

        // Team-ranked internal identifiers. These double as the gateway's mode values,
        // which must be sent exactly as written here.
        public const string MATCH_TYPE_TEAM_2V2 = "team_2v2_ranked";
        public const string MATCH_TYPE_TEAM_3V3 = "team_3v3_ranked";
        public const string MATCH_TYPE_TEAM_4V4 = "team_4v4_ranked";

        // Database values for the match table
        public const string DB_MATCH_TYPE_CASUAL     = "casual";
        public const string DB_MATCH_TYPE_RANKED     = "mmr";
        public const string DB_MATCH_TYPE_PRO_SERIES = "pro_series";

        // Database values for the matchmaking_queue table (different schema from match)
        public const string QUEUE_MATCH_TYPE_CASUAL     = "casual";
        public const string QUEUE_MATCH_TYPE_RANKED     = "ranked";
        public const string QUEUE_MATCH_TYPE_PRO_SERIES = "pro_series";

        // Gateway mode values sent in match.submit's payload.
        public const string GATEWAY_MODE_CASUAL     = "casual";
        public const string GATEWAY_MODE_RANKED     = "ranked";
        public const string GATEWAY_MODE_PRO_SERIES = "pro_series";

        /// <summary>All supported team-ranked match types, smallest roster first.</summary>
        public static readonly string[] TEAM_MATCH_TYPES = {
            MATCH_TYPE_TEAM_2V2,
            MATCH_TYPE_TEAM_3V3,
            MATCH_TYPE_TEAM_4V4,
        };

        public static bool IsRankedMatchType(string t) =>
            !string.IsNullOrWhiteSpace(t) &&
            t.IndexOf("ranked_season_2", System.StringComparison.OrdinalIgnoreCase) >= 0;

        public static bool IsProSeriesMatchType(string t) =>
            !string.IsNullOrWhiteSpace(t) &&
            t.IndexOf("pro_series_season_2", System.StringComparison.OrdinalIgnoreCase) >= 0;

        public static bool IsCasualMatchType(string t) =>
            !string.IsNullOrWhiteSpace(t) &&
            t.IndexOf("casual", System.StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>True for the team-ranked formats (2v2 / 3v3 / 4v4).</summary>
        public static bool IsTeamMatchType(string t) =>
            !string.IsNullOrWhiteSpace(t) &&
            System.Array.IndexOf(TEAM_MATCH_TYPES, t.Trim().ToLowerInvariant()) >= 0;

        /// <summary>Players per side for a team match type, or 0 if it isn't one.</summary>
        public static int GetTeamSize(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return 0;
            switch (t.Trim().ToLowerInvariant())
            {
                case MATCH_TYPE_TEAM_2V2: return 2;
                case MATCH_TYPE_TEAM_3V3: return 3;
                case MATCH_TYPE_TEAM_4V4: return 4;
                default: return 0;
            }
        }

        public static bool IsManagedMatchType(string t) =>
            IsRankedMatchType(t) || IsProSeriesMatchType(t) || IsCasualMatchType(t) || IsTeamMatchType(t);

        /// <summary>Maps an internal match type to its match table DB value.</summary>
        public static string ToDbMatchType(string internalMatchType)
        {
            if (IsTeamMatchType(internalMatchType))      return DB_MATCH_TYPE_RANKED;
            if (IsCasualMatchType(internalMatchType))    return DB_MATCH_TYPE_CASUAL;
            if (IsProSeriesMatchType(internalMatchType)) return DB_MATCH_TYPE_PRO_SERIES;
            return DB_MATCH_TYPE_RANKED;
        }

        /// <summary>Maps an internal match type to its matchmaking_queue table value.</summary>
        public static string ToQueueMatchType(string internalMatchType)
        {
            // Team queues use the mode string directly so the website can size the lobby.
            if (IsTeamMatchType(internalMatchType))      return internalMatchType.Trim().ToLowerInvariant();
            if (IsCasualMatchType(internalMatchType))    return QUEUE_MATCH_TYPE_CASUAL;
            if (IsProSeriesMatchType(internalMatchType)) return QUEUE_MATCH_TYPE_PRO_SERIES;
            return QUEUE_MATCH_TYPE_RANKED;
        }

        /// <summary>
        /// Maps an internal match type to the mode value the mod gateway expects in
        /// match.submit. Team formats pass through unchanged (team_2v2_ranked, etc).
        /// </summary>
        public static string ToGatewayMode(string internalMatchType)
        {
            if (IsTeamMatchType(internalMatchType))      return internalMatchType.Trim().ToLowerInvariant();
            if (IsCasualMatchType(internalMatchType))    return GATEWAY_MODE_CASUAL;
            if (IsProSeriesMatchType(internalMatchType)) return GATEWAY_MODE_PRO_SERIES;
            return GATEWAY_MODE_RANKED;
        }

        /// <summary>Ranked — moderate wind, White Flag off, Comeback off. All other settings are game defaults.</summary>
        public static Dictionary<MatchSetupRules.Rule, float> GetRankedRulesSettings() =>
            new Dictionary<MatchSetupRules.Rule, float>
            {
                { MatchSetupRules.Rule.Wind,      2f }, // Moderate
                { MatchSetupRules.Rule.Comeback,  0f },
                { MatchSetupRules.Rule.WhiteFlag, 0f },
            };

        /// <summary>Pro Series — same base settings as ranked.</summary>
        public static Dictionary<MatchSetupRules.Rule, float> GetProSeriesRulesSettings() =>
            new Dictionary<MatchSetupRules.Rule, float>
            {
                { MatchSetupRules.Rule.Wind,      2f }, // Moderate
                { MatchSetupRules.Rule.Comeback,  0f },
                { MatchSetupRules.Rule.WhiteFlag, 0f },
            };

        /// <summary>Casual — low wind, White Flag off, Comeback off. All other settings are game defaults.</summary>
        public static Dictionary<MatchSetupRules.Rule, float> GetCasualRulesSettings() =>
            new Dictionary<MatchSetupRules.Rule, float>
            {
                { MatchSetupRules.Rule.Wind,      1f }, // Low
                { MatchSetupRules.Rule.Comeback,  0f },
                { MatchSetupRules.Rule.WhiteFlag, 0f },
            };

        public static string GetRankName(float mmr)
        {
            if (mmr < 800)  return "Copper";
            if (mmr < 950)  return "Bronze";
            if (mmr < 1100) return "Silver";
            if (mmr < 1250) return "Gold";
            if (mmr < 1400) return "Platinum";
            if (mmr < 1600) return "Diamond";
            return "Master";
        }

        public static string GetRankColor(float mmr)
        {
            if (mmr < 800)  return "#B87333"; // Copper
            if (mmr < 950)  return "#CD7F32"; // Bronze
            if (mmr < 1100) return "#C0C0C0"; // Silver
            if (mmr < 1250) return "#FFD700"; // Gold
            if (mmr < 1400) return "#A8DADC"; // Platinum
            if (mmr < 1600) return "#00FFFF"; // Diamond
            return "#CC00FF";                 // Master
        }

        public static string BuildRulesDescription(Dictionary<MatchSetupRules.Rule, float> rules)
        {
            var lines = new List<string>();
            foreach (var kvp in rules)
            {
                string color = kvp.Value != 0f ? "#AAFFAA" : "#FFAAAA";
                lines.Add($"<b>{FormatRuleName(kvp.Key)}:</b> <color={color}>{FormatRuleValue(kvp.Key, kvp.Value)}</color>");
            }
            return string.Join("\n", lines);
        }

        private static string FormatRuleName(MatchSetupRules.Rule rule)
        {
            string name = rule.ToString();
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }

        private static string FormatRuleValue(MatchSetupRules.Rule rule, float value)
        {
            switch (rule)
            {
                case MatchSetupRules.Rule.Wind:
                    if (value == 0f) return "Off";
                    if (value == 1f) return "Low";
                    if (value == 2f) return "Medium";
                    if (value == 3f) return "High";
                    return value.ToString("G");
                default:
                    if (value == 0f) return "Off";
                    if (value == 1f) return "On";
                    return value.ToString("G");
            }
        }
    }
}
