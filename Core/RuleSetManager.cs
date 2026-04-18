using BepInEx.Logging;
using System.Collections.Generic;

namespace SBGL.UnifiedMod.Core
{
    /// <summary>
    /// Manages ruleset loading, validation, and application for ranked matches.
    /// </summary>
    public static class RuleSetManager
    {
        private static ManualLogSource _logger = null;

        public static void SetLogger(ManualLogSource logger)
        {
            _logger = logger;
        }

        private static void Log(string message)
        {
            if (_logger != null)
                _logger.LogInfo($"[RuleSetManager] {message}");
        }

        private static void LogWarning(string message)
        {
            if (_logger != null)
                _logger.LogWarning($"[RuleSetManager] {message}");
        }

        private static void LogError(string message)
        {
            if (_logger != null)
                _logger.LogError($"[RuleSetManager] {message}");
        }

        /// <summary>
        /// Get the ruleset for a given season.
        /// Currently only Season 1 is supported.
        /// Returns a dictionary mapping Rule enum indices to float values.
        /// </summary>
        public static Dictionary<MatchSetupRules.Rule, float> GetRuleSetForSeason(int season)
        {
            if (season != Season1RuleSet.SEASON)
            {
                LogWarning($"Season {season} not supported, defaulting to Season 1");
            }

            return Season1RuleSet.GetRulesSettings();
        }

        /// <summary>
        /// Validate a course for use in ranked mode.
        /// Returns true if the course is approved, false otherwise.
        /// </summary>
        public static bool ValidateCourseForRanked(string courseName)
        {
            if (string.IsNullOrEmpty(courseName))
            {
                LogError("Course name is null or empty");
                return false;
            }

            bool isApproved = MapPoolConfig.IsCourseApproved(courseName);

            if (!isApproved)
            {
                LogWarning($"Course '{courseName}' is not approved for ranked play");
            }

            return isApproved;
        }

        /// <summary>
        /// Validate that a match type and course combination is valid for ranked play.
        /// </summary>
        public static bool ValidateMatchConfiguration(string matchType, string courseName, int season)
        {
            if (string.IsNullOrEmpty(matchType))
            {
                LogError("Match type is null or empty");
                return false;
            }

            if (string.IsNullOrEmpty(courseName))
            {
                LogError("Course name is null or empty");
                return false;
            }

            // Validate season
            if (season != Season1RuleSet.SEASON)
            {
                LogWarning($"Season {season} not officially supported");
            }

            // Validate course
            if (!ValidateCourseForRanked(courseName))
            {
                LogError($"Course '{courseName}' is not approved for ranked season {season}");
                return false;
            }

            // Validate match type
            if (!matchType.Contains("ranked") && !matchType.Contains("pro_series"))
            {
                LogWarning($"Match type '{matchType}' may not be a ranked/competitive type");
            }

            return true;
        }

        /// <summary>
        /// Get a fallback course if the provided course is invalid.
        /// Randomly selects from approved courses.
        /// </summary>
        public static string GetFallbackCourse()
        {
            var fallback = MapPoolConfig.GetRandomApprovedCourse();
            LogWarning($"Using fallback course: {fallback.Name}");
            return fallback.Name;
        }

        /// <summary>
        /// Get a safe course name, with fallback if invalid.
        /// </summary>
        public static string ValidateAndGetCourseName(string courseName)
        {
            if (string.IsNullOrEmpty(courseName))
            {
                LogError("Course name is null or empty, using fallback");
                return GetFallbackCourse();
            }

            if (!MapPoolConfig.IsCourseApproved(courseName))
            {
                LogError($"Course '{courseName}' is not approved, using fallback");
                return GetFallbackCourse();
            }

            return courseName;
        }

        /// <summary>
        /// Log the matched ruleset and course for audit trail.
        /// </summary>
        public static void LogMatchConfiguration(string matchType, string courseName, int season)
        {
            Log($"Match Configuration Applied - Type: {matchType}, Course: {courseName}, Season: {season}");
            Log($"Course Biome: {MapPoolConfig.FindCourseByName(courseName)?.Biome ?? "Unknown"}");
            Log("Season 1 Ranked Rules Applied - Wind: Enabled, Comeback: Enabled, Knockouts: Disabled, OutOfBounds: 5s");
        }

        /// <summary>
        /// Check if a given match type is considered ranked/competitive.
        /// </summary>
        public static bool IsCompetitiveMatch(string matchType)
        {
            if (string.IsNullOrEmpty(matchType))
                return false;

            return matchType.Contains("ranked") || matchType.Contains("pro_series") || matchType.Contains("competitive");
        }
    }
}
