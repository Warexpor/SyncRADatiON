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
        /// <summary>Host diffs puzzles/locks/elevators/radio/storage/event zones. Local pref: read PuzzlesEnabled instead.</summary>
        public static MelonPreferences_Entry<bool> SyncPuzzles;
        /// <summary>Local pref: read WorldPickupsEnabled instead.</summary>
        public static MelonPreferences_Entry<bool> SyncWorldPickups;
        public static MelonPreferences_Entry<bool> SyncPlayerVitals;
        public static MelonPreferences_Entry<bool> VerboseLogging;
        /// <summary>Diagnostic instrumentation (traces, phase timing, stall breakdown, full patch audit). Read DiagnosticsOn.</summary>
        public static MelonPreferences_Entry<bool> Diagnostics;
        /// <summary>Rewrite Cursor.lockState Confined/Locked to None (free pointer for dual-box).</summary>
        public static MelonPreferences_Entry<bool> FreeCursor;
        /// <summary>Host only: session capacity (host + clients). 2..8, default 4.</summary>
        public static MelonPreferences_Entry<int> MaxPlayers;

        public const int DefaultMaxPlayers = 4;
        public const int MinMaxPlayers = 2;
        public const int HardMaxPlayers = 8;
        /// <summary>Seconds a downed player waits before respawning next to a living teammate.</summary>
        public static MelonPreferences_Entry<float> DownedRespawnDelay;
        /// <summary>Host: let clients use the F6 / F7 / F11 cheats that touch the shared world. Default off.</summary>
        public static MelonPreferences_Entry<bool> AllowClientCheats;

        public static void Bind()
        {
            Category = MelonPreferences.CreateCategory("SyncRADation", "SyncRADation");
            ConnectAddress = Category.CreateEntry("ConnectAddress", "127.0.0.1", "Default IP for F3 quick connect");
            ConnectPort = Category.CreateEntry("ConnectPort", PluginInfo.DefaultPort, "Default UDP port");
            FriendlyFire = Category.CreateEntry("FriendlyFire", false, "Allow players to damage each other");
            SyncPuzzles = Category.CreateEntry("SyncPuzzles", true,
                "Sync puzzles, locks, elevators, radio, storage, interactions, alert (host-authoritative). Clients use the host's value.");
            SyncWorldPickups = Category.CreateEntry("SyncWorldPickups", true,
                "Sync world ItemPickups (keys, modules, docs, ground ammo)");
            SyncPlayerVitals = Category.CreateEntry("SyncPlayerVitals", true,
                "Share HP / death / game-state for remote Elster display");
            VerboseLogging = Category.CreateEntry("VerboseLogging", false,
                "Log volume only: extra lines from code that runs anyway (FMOD Play/Stop, proxy clone/FX internals, incremental puzzle apply). Costs nothing but log size. Instrumentation is the separate Diagnostics pref. Set in MelonPreferences.cfg under [SyncRADation] on BOTH installs.");
            Diagnostics = Category.CreateEntry("Diagnostics", false,
                "Instrumentation that costs CPU every frame, read once at game start (restart to change). When true: [Room]/[Proxy]/[Enemy] flicker trace, [Move] blocked trace, [Bag] trace, [EventCam] click trace, [Hitch] phase= timing (wraps every patched Update/LateUpdate/FixedUpdate) and stall breakdowns, and the full boot [Harmony] audit. Off: only the cheap always-on [Hitch] spike lines and 5 s summary remain. Set on BOTH installs for a dual-box hunt.");
            DiagnosticsOn = Diagnostics.Value;

            FreeCursor = Category.CreateEntry("FreeCursor", false,
                "Never confine/lock the mouse to the game window (dual-box testing under Proton/Wayland). Off = vanilla.");

            MaxPlayers = Category.CreateEntry("MaxPlayers", DefaultMaxPlayers,
                "Host session capacity incl. host (2-8). Applies on next Host Game.");
            if (MaxPlayers.Value < MinMaxPlayers || MaxPlayers.Value > HardMaxPlayers)
                MaxPlayers.Value = System.Math.Max(MinMaxPlayers, System.Math.Min(HardMaxPlayers, MaxPlayers.Value));
            DownedRespawnDelay = Category.CreateEntry("DownedRespawnDelay", 20f,
                "Seconds a downed player (host or client) waits before respawning next to the nearest living teammate. The party wipes (host reloads its last save) only when everyone is down.");

            AllowClientCheats = Category.CreateEntry("AllowClientCheats", false,
                "Host: allow clients' F6 (keys onto the party ring), F7 (chapter loads) and F11 (enemy spawns). Off = only the host can cheat.");
        }

        /// <summary>
        /// Diagnostics pref, latched at Bind (game start). A plain static read so every gated trace costs one branch when off;
        /// Harmony phase timing is installed (or not) at boot, so a runtime flip could not take full effect anyway.
        /// </summary>
        // persistent: pref latched at boot
        public static bool DiagnosticsOn { get; private set; }

        public static float DownedRespawnSeconds
        {
            get
            {
                float v = DownedRespawnDelay != null ? DownedRespawnDelay.Value : 20f;
                return v < 1f ? 1f : (v > 600f ? 600f : v);
            }
        }

        // ---- Effective sync toggles. The host is authoritative: it sends its prefs in every PlayerRoster, and a connected
        // client uses those for the session (two installs with different toggles would otherwise desync silently).
        // Host, solo and offline use the local prefs. Read these, not the MelonPreferences entries.

        public const byte FlagPuzzles = 1;
        public const byte FlagWorldPickups = 2;
        public const byte FlagPlayerVitals = 4;
        public const byte FlagFriendlyFire = 8;
        public const byte FlagClientCheats = 16;

        // Session state (cleared by SessionReset "HostSyncFlags" on start / stop).
        private static bool _hasHostFlags;
        private static byte _hostFlags;

        /// <summary>This install's toggles as roster flags (what a host sends).</summary>
        public static byte LocalSyncFlags =>
            (byte)((SyncPuzzles?.Value == true ? FlagPuzzles : 0)
                | (SyncWorldPickups?.Value == true ? FlagWorldPickups : 0)
                | (SyncPlayerVitals?.Value == true ? FlagPlayerVitals : 0)
                | (FriendlyFire?.Value == true ? FlagFriendlyFire : 0)
                | (AllowClientCheats?.Value == true ? FlagClientCheats : 0));

        /// <summary>Client: the host's toggles arrived (PlayerRoster). Logged when they differ from the local prefs.</summary>
        public static void ApplyHostSyncFlags(byte flags)
        {
            bool changed = !_hasHostFlags || _hostFlags != flags;
            _hasHostFlags = true;
            _hostFlags = flags;
            if (changed && flags != LocalSyncFlags)
                ModRuntime.Log?.Msg("[Config] using the host's sync toggles: " + Describe(flags) + " (local prefs: " + Describe(LocalSyncFlags) + ")");
        }

        public static void ClearHostSyncFlags()
        {
            _hasHostFlags = false;
            _hostFlags = 0;
        }

        static bool Effective(byte flag, MelonPreferences_Entry<bool> local) =>
            _hasHostFlags ? (_hostFlags & flag) != 0 : local?.Value == true;

        public static bool PuzzlesEnabled => Effective(FlagPuzzles, SyncPuzzles);
        public static bool WorldPickupsEnabled => Effective(FlagWorldPickups, SyncWorldPickups);
        public static bool PlayerVitalsEnabled => Effective(FlagPlayerVitals, SyncPlayerVitals);
        public static bool FriendlyFireEnabled => Effective(FlagFriendlyFire, FriendlyFire);
        /// <summary>Clients may use the shared-world cheats (host's AllowClientCheats while connected).</summary>
        public static bool ClientCheatsAllowed => Effective(FlagClientCheats, AllowClientCheats);

        public static string Describe(byte flags) =>
            "FF=" + ((flags & FlagFriendlyFire) != 0 ? "ON" : "OFF")
            + " clientCheats=" + ((flags & FlagClientCheats) != 0 ? "ON" : "OFF")
            + " puzzles=" + ((flags & FlagPuzzles) != 0 ? "ON" : "OFF")
            + " pickups=" + ((flags & FlagWorldPickups) != 0 ? "ON" : "OFF")
            + " vitals=" + ((flags & FlagPlayerVitals) != 0 ? "ON" : "OFF");

        /// <summary>The toggles in effect right now (host's while connected as a client).</summary>
        public static byte EffectiveSyncFlags => _hasHostFlags ? _hostFlags : LocalSyncFlags;

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
