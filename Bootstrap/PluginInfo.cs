// SyncRADation — constants: version, protocol, port, send rates
namespace SyncRADation
{
    public static class PluginInfo
    {
        public const string Name = "SyncRADation";
        public const string Version = "0.5.56";
        public const string Author = "Warexpor";
        public const string Description = "LAN multiplayer mod for SIGNALIS — host-authoritative world/story, native client UX";
        /// <summary>Protocol v11: v10 + MED_Adler_EVdoors (PuzzleType 77 DoorL/DoorR local X).</summary>
        public const int ProtocolVersion = 11;
        public const int DefaultPort = 7777;
        public const int MaxPlayers = 4;
        public const float SendInterval = 1f / 30f;
        /// <summary>~1.35 packets behind at 30 Hz. Shared by root pose and bone sampling.</summary>
        public const float PoseInterpDelay = 0.045f;
        public const float EntitySendInterval = 1f / 15f;
        public const string ConnectionKey = "SyncRADation";
    }
}
