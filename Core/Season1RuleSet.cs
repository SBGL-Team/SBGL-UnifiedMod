using System.Collections.Generic;

namespace SBGL.UnifiedMod.Core
{
    public static class Season1RuleSet
    {
        public const int SEASON = 1;
        public const string SEASON_ID = "season_1";
        public const string MATCH_TYPE_RANKED = "ranked_season_1";

        /// <summary>
        /// Items banned in Season 1 — kept for reference; weights are set to 0f in GetItemSpawnWeights().
        /// </summary>
        public static HashSet<ItemType> GetDisabledItems() => new HashSet<ItemType>
        {
            ItemType.OrbitalLaser,
            ItemType.RocketDriver,
            // FreezeBomb intentionally absent — enabled by ruleset
        };

        /// <summary>
        /// Explicit item spawn weights for every pool in Season 1.
        /// Pool indices: 0=Ahead of own ball, 1=In the lead, 2=Behind 50m,
        ///               3=Behind 125m, 4=Behind 200m, 5=Mobility item boxes.
        /// Weights match the Season 1 ruleset screenshots. Banned items (OrbitalLaser,
        /// RocketDriver) are set to 0f in every pool even where game defaults are non-zero.
        /// </summary>
        public static Dictionary<MatchSetupRules.ItemPoolId, float> GetItemSpawnWeights()
        {
            var w = new Dictionary<MatchSetupRules.ItemPoolId, float>();
            void Set(int pool, ItemType item, float weight)
                => w[MatchSetupRules.ItemPoolId.Get(pool, item)] = weight;

            // Pool 1 — In the lead
            Set(1, ItemType.Landmine,        20f);
            Set(1, ItemType.Airhorn,         10f);
            Set(1, ItemType.Electromagnet,   10f);
            Set(1, ItemType.DuelingPistol,   35f);
            Set(1, ItemType.SpringBoots,     20f);
            Set(1, ItemType.Coffee,           0f);
            Set(1, ItemType.ElephantGun,      0f);
            Set(1, ItemType.RocketLauncher,   0f);
            Set(1, ItemType.GolfCart,         0f);
            Set(1, ItemType.OrbitalLaser,     0f); // banned
            Set(1, ItemType.RocketDriver,     0f); // banned
            Set(1, ItemType.FreezeBomb,       5f);

            // Pool 2 — Behind 50m
            Set(2, ItemType.Landmine,         0f);
            Set(2, ItemType.Airhorn,          0f);
            Set(2, ItemType.Electromagnet,    5f);
            Set(2, ItemType.DuelingPistol,    0f);
            Set(2, ItemType.SpringBoots,     10f);
            Set(2, ItemType.Coffee,          30f);
            Set(2, ItemType.ElephantGun,     20f);
            Set(2, ItemType.RocketLauncher,   0f);
            Set(2, ItemType.GolfCart,        10f);
            Set(2, ItemType.OrbitalLaser,     0f); // banned
            Set(2, ItemType.RocketDriver,     0f); // banned (default 5%)
            Set(2, ItemType.FreezeBomb,      20f);

            // Pool 3 — Behind 125m
            Set(3, ItemType.Landmine,         0f);
            Set(3, ItemType.Airhorn,          0f);
            Set(3, ItemType.Electromagnet,    0f);
            Set(3, ItemType.DuelingPistol,    0f);
            Set(3, ItemType.SpringBoots,     10f);
            Set(3, ItemType.Coffee,          20f);
            Set(3, ItemType.ElephantGun,      0f);
            Set(3, ItemType.RocketLauncher,  20f);
            Set(3, ItemType.GolfCart,        27.5f);
            Set(3, ItemType.OrbitalLaser,     0f); // banned (default 2.5%)
            Set(3, ItemType.RocketDriver,     0f); // banned (default 20%)
            Set(3, ItemType.FreezeBomb,       0f);

            // Pool 4 — Behind 200m
            Set(4, ItemType.Landmine,         0f);
            Set(4, ItemType.Airhorn,          0f);
            Set(4, ItemType.Electromagnet,    0f);
            Set(4, ItemType.DuelingPistol,    0f);
            Set(4, ItemType.SpringBoots,      0f);
            Set(4, ItemType.Coffee,          20f);
            Set(4, ItemType.ElephantGun,      0f);
            Set(4, ItemType.RocketLauncher,  20f);
            Set(4, ItemType.GolfCart,        35f);
            Set(4, ItemType.OrbitalLaser,     0f); // banned (default 5%)
            Set(4, ItemType.RocketDriver,     0f); // banned (default 20%)
            Set(4, ItemType.FreezeBomb,       0f);

            // Pool 5 — Mobility item boxes
            Set(5, ItemType.Landmine,         0f);
            Set(5, ItemType.Airhorn,          0f);
            Set(5, ItemType.Electromagnet,    0f);
            Set(5, ItemType.DuelingPistol,    0f);
            Set(5, ItemType.SpringBoots,     30f);
            Set(5, ItemType.Coffee,          30f);
            Set(5, ItemType.ElephantGun,      0f);
            Set(5, ItemType.RocketLauncher,   0f);
            Set(5, ItemType.GolfCart,        20f);
            Set(5, ItemType.OrbitalLaser,     0f); // banned
            Set(5, ItemType.RocketDriver,     0f); // banned (default 20%)
            Set(5, ItemType.FreezeBomb,       0f);

            // Pool 0 — Ahead of own ball
            Set(0, ItemType.Landmine,         0f);
            Set(0, ItemType.Airhorn,          0f);
            Set(0, ItemType.Electromagnet,    0f);
            Set(0, ItemType.DuelingPistol,    0f);
            Set(0, ItemType.SpringBoots,     33.3f);
            Set(0, ItemType.Coffee,          66.7f);
            Set(0, ItemType.ElephantGun,      0f);
            Set(0, ItemType.RocketLauncher,   0f);
            Set(0, ItemType.GolfCart,         0f);
            Set(0, ItemType.OrbitalLaser,     0f); // banned
            Set(0, ItemType.RocketDriver,     0f); // banned
            Set(0, ItemType.FreezeBomb,       0f);

            return w;
        }

        public static Dictionary<MatchSetupRules.Rule, float> GetRulesSettings()
        {
            return new Dictionary<MatchSetupRules.Rule, float>
            {
                { MatchSetupRules.Rule.Wind,                     1f },
                { MatchSetupRules.Rule.HitOtherPlayers,          1f },
                { MatchSetupRules.Rule.HitOtherPlayersBalls,     0f },
                { MatchSetupRules.Rule.Comeback,                 0f },
                { MatchSetupRules.Rule.OnOrBelowPar,             1f },
                { MatchSetupRules.Rule.Speedrun,                 1f },
                { MatchSetupRules.Rule.ChipIn,                   1f },
                { MatchSetupRules.Rule.Knockouts,                0f },
                { MatchSetupRules.Rule.OutOfBounds,              5f },
                { MatchSetupRules.Rule.ConsoleCommands,          0f },
                { MatchSetupRules.Rule.Countdown,               45f },
                { MatchSetupRules.Rule.MaxTimeBasedOnPar,        0f },
                { MatchSetupRules.Rule.PlayerSpeed,              1f },
                { MatchSetupRules.Rule.CartSpeed,                1f },
                { MatchSetupRules.Rule.SwingPower,               1f },
                { MatchSetupRules.Rule.OverchargeSidespin,       1f },
                { MatchSetupRules.Rule.HomingShots,              1f },
                { MatchSetupRules.Rule.KnockoutSpeedBoost,       1f },
                { MatchSetupRules.Rule.RepeatRecoveryProtection, 1f },
                { MatchSetupRules.Rule.DominationProtection,     1f },
                { MatchSetupRules.Rule.WhiteFlag,                1f },
            };
        }
    }
}
