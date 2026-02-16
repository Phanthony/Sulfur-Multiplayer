using BepInEx.Configuration;

namespace SulfurMP.Config
{
    /// <summary>
    /// BepInEx configuration entries for multiplayer settings.
    /// Accessible via BepInEx config GUI or config file.
    /// </summary>
    public static class MultiplayerConfig
    {
        // Networking
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<float> TickRate;

        // Interpolation
        public static ConfigEntry<float> InterpolationDelay;

        // NPC Sync
        public static ConfigEntry<float> RelevanceDistance;

        // Debug
        public static ConfigEntry<bool> ShowDebugOverlay;

        public static void Init(ConfigFile config)
        {
            MaxPlayers = config.Bind(
                "Networking", "MaxPlayers", 4,
                new ConfigDescription("Maximum players in a lobby (minimum 2)", new AcceptableValueRange<int>(2, 250)));

            TickRate = config.Bind(
                "Networking", "TickRate", 60f,
                new ConfigDescription("Network updates per second", new AcceptableValueRange<float>(10f, 60f)));

            InterpolationDelay = config.Bind(
                "Interpolation", "InterpolationDelay", 0.1f,
                new ConfigDescription("Interpolation buffer delay in seconds", new AcceptableValueRange<float>(0.05f, 0.5f)));

            RelevanceDistance = config.Bind(
                "NPC Sync", "RelevanceDistance", 80f,
                new ConfigDescription("Max distance from any player to sync NPC positions (meters)",
                    new AcceptableValueRange<float>(30f, 200f)));

            ShowDebugOverlay = config.Bind(
                "Debug", "ShowDebugOverlay", false,
                "Show debug overlay on startup (toggle with F3)");
        }
    }
}
