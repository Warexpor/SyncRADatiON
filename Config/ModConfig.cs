// MelonPreferences: IP, port, FF, world sync flags
using MelonLoader;

namespace SyncRADation.Config
{
    public static class ModConfig
    {
        public static MelonPreferences_Category Category;
        public static MelonPreferences_Entry<string> ConnectAddress;
        public static MelonPreferences_Entry<int> ConnectPort;
        public static MelonPreferences_Entry<bool> FriendlyFire;
        /// <summary>Host diffs puzzles/locks/elevators/radio/storage/event zones.</summary>
        public static MelonPreferences_Entry<bool> SyncPuzzles;
        /// <summary>Legacy name kept for old prefs files; mirrors SyncPuzzles when binding.</summary>
        public static MelonPreferences_Entry<bool> ExperimentalPuzzles;
        public static MelonPreferences_Entry<bool> SyncWorldPickups;
        public static MelonPreferences_Entry<bool> SyncPlayerVitals;
        public static MelonPreferences_Entry<bool> VerboseLogging;
        /// <summary>Rewrite Cursor.lockState Confined/Locked to None (free pointer for dual-box).</summary>
        public static MelonPreferences_Entry<bool> FreeCursor;
        /// <summary>Host only: session capacity (host + clients). 2..8, default 4.</summary>
        public static MelonPreferences_Entry<int> MaxPlayers;

        public const int DefaultMaxPlayers = 4;
        public const int MinMaxPlayers = 2;
        public const int HardMaxPlayers = 8;
        /// <summary>Seconds a downed player waits before respawning next to a living teammate.</summary>
        public static MelonPreferences_Entry<float> DownedRespawnDelay;

        public static void Bind()
        {
            Category = MelonPreferences.CreateCategory("SyncRADation", "SyncRADation");
            ConnectAddress = Category.CreateEntry("ConnectAddress", "127.0.0.1", "Default IP for F3 quick connect");
            ConnectPort = Category.CreateEntry("ConnectPort", PluginInfo.DefaultPort, "Default UDP port");
            FriendlyFire = Category.CreateEntry("FriendlyFire", false, "Allow players to damage each other");
            SyncPuzzles = Category.CreateEntry("SyncPuzzles", true,
                "Sync puzzles, locks, elevators, radio, storage, interactions, alert (host-authoritative)");
            ExperimentalPuzzles = Category.CreateEntry("ExperimentalPuzzles", true,
                "Deprecated alias — use SyncPuzzles (kept for old configs)");
            SyncWorldPickups = Category.CreateEntry("SyncWorldPickups", true,
                "Sync world ItemPickups (keys, modules, docs, ground ammo)");
            SyncPlayerVitals = Category.CreateEntry("SyncPlayerVitals", true,
                "Share HP / death / game-state for remote Elster display");
            VerboseLogging = Category.CreateEntry("VerboseLogging", false,
                "OFF unless diagnosing. When true: FMOD Play/Stop, proxy clone/FX internals, incremental puzzle apply. Set in MelonPreferences.cfg under [SyncRADation] on BOTH installs.");

            FreeCursor = Category.CreateEntry("FreeCursor", false,
                "Never confine/lock the mouse to the game window (dual-box testing under Proton/Wayland). Off = vanilla.");

            MaxPlayers = Category.CreateEntry("MaxPlayers", DefaultMaxPlayers,
                "Host session capacity incl. host (2-8). Applies on next Host Game.");
            if (MaxPlayers.Value < MinMaxPlayers || MaxPlayers.Value > HardMaxPlayers)
                MaxPlayers.Value = System.Math.Max(MinMaxPlayers, System.Math.Min(HardMaxPlayers, MaxPlayers.Value));
            DownedRespawnDelay = Category.CreateEntry("DownedRespawnDelay", 20f,
                "Seconds a downed player (host or client) waits before respawning next to the nearest living teammate. The party wipes (host reloads its last save) only when everyone is down.");

            if (!SyncPuzzles.Value && ExperimentalPuzzles.Value)
                SyncPuzzles.Value = true;
            ExperimentalPuzzles.Value = SyncPuzzles.Value;
        }

        public static float DownedRespawnSeconds
        {
            get
            {
                float v = DownedRespawnDelay != null ? DownedRespawnDelay.Value : 20f;
                return v < 1f ? 1f : (v > 600f ? 600f : v);
            }
        }

        public static bool PuzzlesEnabled => SyncPuzzles?.Value == true;

        /// <summary>Clamped MaxPlayers (2..8); DefaultMaxPlayers before Bind().</summary>
        public static int MaxPlayersClamped
        {
            get
            {
                int v = MaxPlayers != null ? MaxPlayers.Value : DefaultMaxPlayers;
                if (v < MinMaxPlayers) v = MinMaxPlayers;
                if (v > HardMaxPlayers) v = HardMaxPlayers;
                return v;
            }
        }
    }
}
