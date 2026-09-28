using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SBGL.UnifiedMod.Core
{
    /// <summary>
    /// Everything about lobby setup that can change from one season to the next.
    /// </summary>
    public sealed class SeasonRules
    {
        public int Season { get; }

        /// <summary>Holes played in ranked and team-ranked matches.</summary>
        public int RankedNumHoles { get; }

        /// <summary>Whether the Driving Range panel offers a PRO SERIES button.</summary>
        public bool ShowProSeriesButton { get; }

        /// <summary>
        /// Rules that apply to 2v2 on top of the ranked set. Null when the season plays 2v2 by
        /// the same rules as everything else. 3v3 and 4v4 are not affected.
        /// </summary>
        public Func<Dictionary<MatchSetupRules.Rule, float>> GetTwoVsTwoOverrides { get; }

        /// <summary>Whether the Driving Range panel offers a 2V2 button for this season.</summary>
        public bool ShowTwoVsTwoButton => GetTwoVsTwoOverrides != null;

        public MapPoolConfig.Course[] ApprovedCourses { get; }
        public MapPoolConfig.Course[] BannedCourses { get; }

        public Func<Dictionary<MatchSetupRules.Rule, float>> GetRankedRules { get; }
        public Func<Dictionary<MatchSetupRules.Rule, float>> GetProSeriesRules { get; }
        public Func<Dictionary<MatchSetupRules.Rule, float>> GetCasualRules { get; }

        public SeasonRules(
            int season,
            int rankedNumHoles,
            bool showProSeriesButton,
            MapPoolConfig.Course[] approvedCourses,
            MapPoolConfig.Course[] bannedCourses,
            Func<Dictionary<MatchSetupRules.Rule, float>> rankedRules,
            Func<Dictionary<MatchSetupRules.Rule, float>> proSeriesRules,
            Func<Dictionary<MatchSetupRules.Rule, float>> casualRules,
            Func<Dictionary<MatchSetupRules.Rule, float>> twoVsTwoOverrides = null)
        {
            Season = season;
            RankedNumHoles = rankedNumHoles;
            ShowProSeriesButton = showProSeriesButton;
            GetTwoVsTwoOverrides = twoVsTwoOverrides;
            ApprovedCourses = approvedCourses;
            BannedCourses = bannedCourses;
            GetRankedRules = rankedRules;
            GetProSeriesRules = proSeriesRules;
            GetCasualRules = casualRules;
        }
    }

    /// <summary>
    /// Picks the rules to apply from the season the website reports. A season with no rules
    /// defined here plays under the most recent season before it, so the site can roll over to
    /// a new season before the mod ships rules for it.
    /// </summary>
    public static class SeasonRuleSets
    {
        private const string WebsiteSeasonPrefKey = "WebsiteSeason";

        // Used until the website has reported a season on this machine.
        private const int DefaultSeason = Season2RuleSet.SEASON;

        // Ascending by season.
        private static readonly SeasonRules[] Defined =
        {
            new SeasonRules(
                season: Season2RuleSet.SEASON,
                rankedNumHoles: 9,
                showProSeriesButton: true,
                approvedCourses: MapPoolConfig.Season2ApprovedCourses,
                bannedCourses: MapPoolConfig.Season2BannedCourses,
                rankedRules: Season2RuleSet.GetRankedRulesSettings,
                proSeriesRules: Season2RuleSet.GetProSeriesRulesSettings,
                casualRules: Season2RuleSet.GetCasualRulesSettings),

            // Season 3: 12 holes, Vertigo in and Uptown out, no Pro Series button, and a
            // 30-second shot countdown in 2v2 only.
            // Wind / Comeback / White Flag are unchanged from Season 2.
            new SeasonRules(
                season: 3,
                rankedNumHoles: 12,
                showProSeriesButton: false,
                approvedCourses: MapPoolConfig.Season3ApprovedCourses,
                bannedCourses: MapPoolConfig.Season3BannedCourses,
                rankedRules: Season2RuleSet.GetRankedRulesSettings,
                proSeriesRules: Season2RuleSet.GetProSeriesRulesSettings,
                casualRules: Season2RuleSet.GetCasualRulesSettings,
                twoVsTwoOverrides: Season3TwoVsTwoOverrides),
        };

        /// <summary>Season 3 plays 2v2 on a shorter shot clock, with hole time scaled to par.</summary>
        private static Dictionary<MatchSetupRules.Rule, float> Season3TwoVsTwoOverrides() =>
            new Dictionary<MatchSetupRules.Rule, float>
            {
                { MatchSetupRules.Rule.Countdown,         30f },
                { MatchSetupRules.Rule.MaxTimeBasedOnPar,  1f },
            };

        // -1 until read from PlayerPrefs. PlayerPrefs can't be touched from a static initializer.
        private static int _websiteSeason = -1;

        /// <summary>
        /// The season number the website last reported, cached across game restarts so a failed
        /// fetch doesn't drop back to an older season. 0 if it has never been reported.
        /// </summary>
        public static int WebsiteSeason
        {
            get
            {
                if (_websiteSeason < 0)
                    _websiteSeason = PlayerPrefs.GetInt(WebsiteSeasonPrefKey, 0);
                return _websiteSeason;
            }
        }

        /// <summary>The website's season, or the default season if it hasn't reported one yet.</summary>
        public static int CurrentSeasonNumber => WebsiteSeason > 0 ? WebsiteSeason : DefaultSeason;

        /// <summary>Rules for the website's current season.</summary>
        public static SeasonRules Current => For(CurrentSeasonNumber);

        /// <summary>
        /// Rules for <paramref name="season"/>: its own rules if defined, otherwise the most recent
        /// season before it. Seasons older than every defined ruleset get the oldest one.
        /// </summary>
        public static SeasonRules For(int season)
        {
            SeasonRules match = Defined[0];
            foreach (var rules in Defined)
            {
                if (rules.Season <= season)
                    match = rules;
            }
            return match;
        }

        public static void SetWebsiteSeason(int season)
        {
            if (season <= 0 || season == WebsiteSeason) return;
            _websiteSeason = season;
            PlayerPrefs.SetInt(WebsiteSeasonPrefKey, season);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Reads the season number off a season record: <c>season_number</c> if set, otherwise a
        /// "Season N" name. Returns 0 if neither is present.
        /// </summary>
        public static int ParseSeasonNumber(string seasonNumber, string seasonName)
        {
            if (double.TryParse(seasonNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && number >= 1 && number == Math.Floor(number))
                return (int)number;

            if (!string.IsNullOrEmpty(seasonName))
            {
                var m = Regex.Match(seasonName, @"\bseason\s*(\d+)\b", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > 0)
                    return n;
            }

            return 0;
        }
    }
}
