using System.Collections.Generic;
using System.Linq;

namespace SBGL.UnifiedMod.Core
{
    /// <summary>
    /// Immutable Season 1 map pool configuration with approved and banned courses.
    /// Course names use readable identifiers matching the Season 1 ruleset document.
    /// </summary>
    public static class MapPoolConfig
    {
        public class Course
        {
            public string Name { get; set; }
            public string Biome { get; set; } // Snow, Coast, Forest, Desert
            public bool IsApproved { get; set; }

            public Course(string name, string biome, bool isApproved)
            {
                Name = name;
                Biome = biome;
                IsApproved = isApproved;
            }
        }

        // Season 1 Approved Maps (28 total)
        private static readonly Course[] ApprovedCourses = new[]
        {
            // Snow (7) — UI# = ParentCourseIndex+1; asset suffix ≠ UI#
            new Course("Snow 1", "Snow", true),  // UI 1 — Taiga Woods
            new Course("Snow 3", "Snow", true),  // UI 2 — Frozen Bay
            new Course("Snow 6", "Snow", true),  // UI 5 — Gorge
            new Course("Snow 7", "Snow", true),  // UI 6 — Slippery Slope
            new Course("Snow 2", "Snow", true),  // UI 7 — Ice Skating
            new Course("Snow 8", "Snow", true),  // UI 8 — Mountain Ridge
            new Course("Snow 9", "Snow", true),  // UI 9 — Outcrop
            
            // Coast (10)
            new Course("Twin Beach", "Coast", true),
            new Course("Cove", "Coast", true),
            new Course("Woodland Bay", "Coast", true),
            new Course("Rolling Hills", "Coast", true),
            new Course("Sandbanks", "Coast", true),
            new Course("Lone Island", "Coast", true),
            new Course("Treasure Island", "Coast", true),
            new Course("Atoll", "Coast", true),
            new Course("Hidden Lagoon", "Coast", true),
            new Course("Jungle", "Coast", true),
            
            // Forest (6)
            new Course("Twin Path", "Forest", true),
            new Course("Overgrown", "Forest", true),
            new Course("Roundabout", "Forest", true),
            new Course("Blast Off", "Forest", true),
            new Course("Hilltops", "Forest", true),
            new Course("Terraces", "Forest", true),
            
            // Desert (5)
            new Course("Patches", "Desert", true),
            new Course("Big Rock", "Desert", true),
            new Course("Oasis", "Desert", true),
            new Course("Sand Traps", "Desert", true),
            new Course("Serpent Trail", "Desert", true),
        };

        // Season 1 Banned Maps (17 total)
        private static readonly Course[] BannedCourses = new[]
        {
            // Snow (2)
            new Course("Snow 4", "Snow", false), // UI 3 — Over the Hill
            new Course("Snow 5", "Snow", false), // UI 4 — Base Jump
            
            // Coast (8)
            new Course("Long Beach", "Coast", false),
            new Course("Downhill", "Coast", false),
            new Course("Bullseye", "Coast", false),
            new Course("Shallows", "Coast", false),
            new Course("Catwalk", "Coast", false),
            new Course("Spiral", "Coast", false),
            new Course("Seaside Cliff", "Coast", false),
            new Course("Gauntlet", "Coast", false),
            
            // Forest (3)
            new Course("Overlook", "Forest", false),
            new Course("Donut", "Forest", false),
            new Course("Upward", "Forest", false),
            
            // Desert (4)
            new Course("Showdown", "Desert", false),
            new Course("Underpass", "Desert", false),
            new Course("Chasm", "Desert", false),
            new Course("Vertigo", "Desert", false),
        };

        private static readonly List<Course> AllCourses;

        static MapPoolConfig()
        {
            AllCourses = new List<Course>();
            AllCourses.AddRange(ApprovedCourses);
            AllCourses.AddRange(BannedCourses);
        }

        /// <summary>
        /// Get all approved courses for Season 1.
        /// </summary>
        public static List<Course> GetApprovedCourses()
        {
            return ApprovedCourses.ToList();
        }

        /// <summary>
        /// Get all banned courses for Season 1.
        /// </summary>
        public static List<Course> GetBannedCourses()
        {
            return BannedCourses.ToList();
        }

        /// <summary>
        /// Check if a course by name is in the approved pool.
        /// </summary>
        public static bool IsCourseApproved(string courseName)
        {
            if (string.IsNullOrEmpty(courseName))
                return false;

            return ApprovedCourses.Any(c => c.Name.Equals(courseName, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Check if a course by name is banned.
        /// </summary>
        public static bool IsCourseBanned(string courseName)
        {
            if (string.IsNullOrEmpty(courseName))
                return false;

            return BannedCourses.Any(c => c.Name.Equals(courseName, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get a random approved course for Season 1.
        /// </summary>
        public static Course GetRandomApprovedCourse()
        {
            return ApprovedCourses[UnityEngine.Random.Range(0, ApprovedCourses.Length)];
        }

        /// <summary>
        /// Get random approved courses for a specific biome.
        /// </summary>
        public static List<Course> GetApprovedCoursesByBiome(string biome)
        {
            return ApprovedCourses.Where(c => c.Biome.Equals(biome, System.StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Get the total count of approved and banned courses.
        /// </summary>
        public static int GetTotalCourseCount()
        {
            return AllCourses.Count;
        }

        /// <summary>
        /// Find a course by name (case-insensitive).
        /// </summary>
        public static Course FindCourseByName(string courseName)
        {
            if (string.IsNullOrEmpty(courseName))
                return null;

            return AllCourses.FirstOrDefault(c => c.Name.Equals(courseName, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Maps a Season 1 biome name to the game's AllCourses index (as observed at runtime).
        /// Coast=0, Forest=1, Desert=2, Snow=3.
        /// Returns -1 if the biome is unknown.
        /// </summary>
        public static int GetGameCourseIndexForBiome(string biome)
        {
            switch (biome?.ToLowerInvariant())
            {
                case "coast":  return 0;
                case "forest": return 1;
                case "desert": return 2;
                case "snow":   return 3;
                default:       return -1;
            }
        }

        /// <summary>
        /// Returns the game's AllCourses index for the biome that contains the named course.
        /// Returns -1 if the course is not found or its biome is unknown.
        /// </summary>
        public static int GetGameCourseIndexForCourseName(string courseName)
        {
            var course = FindCourseByName(courseName);
            if (course == null) return -1;
            return GetGameCourseIndexForBiome(course.Biome);
        }
    }
}
