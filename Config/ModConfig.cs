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
            VerboseLogging = Category.CreateEntry("VerboseLogging", false, "Extra diagnostic logs");
        }

        /// <summary>True if either SyncPuzzles or legacy ExperimentalPuzzles is on.</summary>
        public static bool PuzzlesEnabled =>
            (SyncPuzzles?.Value == true) || (ExperimentalPuzzles?.Value == true);
    }
}
