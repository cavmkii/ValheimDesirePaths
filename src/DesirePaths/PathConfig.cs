using BepInEx.Configuration;

namespace DesirePaths
{
    /// <summary>
    /// All user-facing settings. On a multiplayer server the server's values for thresholds,
    /// decay and terrain shaping win; the client-side values only matter for single player,
    /// hosting, or when the server does not run the mod.
    /// </summary>
    internal static class PathConfig
    {
        public static ConfigEntry<bool> Enabled;

        public static ConfigEntry<float> CellSize;
        public static ConfigEntry<float> SameCellCooldown;

        public static ConfigEntry<int> StepsToSmooth;
        public static ConfigEntry<int> StepsToDirtPath;
        public static ConfigEntry<int> StepsToStoneRoad;

        public static ConfigEntry<float> DecayPerDay;

        public static ConfigEntry<float> SmoothRadius;
        public static ConfigEntry<float> SmoothPower;
        public static ConfigEntry<float> DirtPathRadius;
        public static ConfigEntry<float> StoneRoadRadius;
        public static ConfigEntry<float> BuildingClearance;
        public static ConfigEntry<bool> ProtectCultivated;
        public static ConfigEntry<Heightmap.Biome> ExcludedBiomes;

        public static ConfigEntry<bool> ShowStageMessages;
        public static ConfigEntry<bool> VerboseLogging;

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("1 - General", "Enabled", true,
                "Master switch. When off, no steps are counted and no terrain is changed.");

            CellSize = cfg.Bind("1 - General", "CellSize", 1.0f,
                new ConfigDescription(
                    "Size in metres of the grid squares steps are tallied in. Smaller cells give narrower, more precise trails but need more traffic to wear in.",
                    new AcceptableValueRange<float>(0.5f, 4f)));

            SameCellCooldown = cfg.Bind("1 - General", "SameCellCooldown", 5f,
                new ConfigDescription(
                    "Seconds before the same player can add another step to the same cell. Stops standing still or circling in place from wearing a path.",
                    new AcceptableValueRange<float>(0f, 600f)));

            StepsToSmooth = cfg.Bind("2 - Thresholds", "StepsToSmooth", 30,
                new ConfigDescription(
                    "Steps through a cell before the ground there is smoothed. 0 skips this stage.",
                    new AcceptableValueRange<int>(0, 100000)));

            StepsToDirtPath = cfg.Bind("2 - Thresholds", "StepsToDirtPath", 100,
                new ConfigDescription(
                    "Steps before the cell becomes a dirt path, as if the hoe's path tool was used. 0 skips this stage.",
                    new AcceptableValueRange<int>(0, 100000)));

            StepsToStoneRoad = cfg.Bind("2 - Thresholds", "StepsToStoneRoad", 400,
                new ConfigDescription(
                    "Steps before the cell becomes a paved stone road. 0 skips this stage.",
                    new AcceptableValueRange<int>(0, 100000)));

            DecayPerDay = cfg.Bind("3 - Decay", "DecayPerDay", 2f,
                new ConfigDescription(
                    "Steps a cell forgets per in-game day with no traffic, so a route you used once a month ago does not keep counting. Stages already reached are never undone. 0 disables decay.",
                    new AcceptableValueRange<float>(0f, 1000f)));

            SmoothRadius = cfg.Bind("4 - Terrain", "SmoothRadius", 1.5f,
                new ConfigDescription("Radius in metres of the smoothing applied at each stage.",
                    new AcceptableValueRange<float>(0.5f, 6f)));

            SmoothPower = cfg.Bind("4 - Terrain", "SmoothPower", 3f,
                new ConfigDescription("Falloff of the smoothing (same meaning as the game's own terrain tools). Higher means gentler at the edges.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            DirtPathRadius = cfg.Bind("4 - Terrain", "DirtPathRadius", 1.0f,
                new ConfigDescription("Radius in metres of the dirt painted when a cell becomes a path.",
                    new AcceptableValueRange<float>(0.25f, 6f)));

            StoneRoadRadius = cfg.Bind("4 - Terrain", "StoneRoadRadius", 1.0f,
                new ConfigDescription("Radius in metres of the paving painted when a cell becomes a road.",
                    new AcceptableValueRange<float>(0.25f, 6f)));

            BuildingClearance = cfg.Bind("4 - Terrain", "BuildingClearance", 2f,
                new ConfigDescription(
                    "Smoothing changes ground height, which can leave building pieces unsupported. Smoothing is skipped when any built piece is within SmoothRadius plus this many metres. Painting still happens. Set to -1 to smooth regardless (not recommended).",
                    new AcceptableValueRange<float>(-1f, 20f)));

            ProtectCultivated = cfg.Bind("4 - Terrain", "ProtectCultivated", true,
                "Never smooth or repaint cultivated soil, so walking through your farm does not turn it into a road.");

            ExcludedBiomes = cfg.Bind("4 - Terrain", "ExcludedBiomes", Heightmap.Biome.Ocean,
                "Biomes where steps are not counted. Combine with commas, e.g. \"Ocean, AshLands\".");

            ShowStageMessages = cfg.Bind("5 - Debug", "ShowStageMessages", false,
                "Show a message in the corner of the screen when ground under you wears to a new stage.");

            VerboseLogging = cfg.Bind("5 - Debug", "VerboseLogging", false,
                "Log every counted step and stage change to the BepInEx console.");
        }

        /// <summary>Thresholds indexed by stage (1..3). A threshold of 0 disables that stage.</summary>
        public static int[] Thresholds() => new[] { 0, StepsToSmooth.Value, StepsToDirtPath.Value, StepsToStoneRoad.Value };
    }
}
