// SyncRADation — constants: version, protocol, port, send rates
namespace SyncRADation
{
    public static class PluginInfo
    {
        public const string Name = "SyncRADation";
        public const string Version = "0.4.2-dev";
        public const string Author = "Warexpor";
        public const string Description = "LAN multiplayer mod for SIGNALIS — host-authoritative world/story, native client UX";
        /// <summary>Protocol v8: v7 roster + host-authored enemy spawn.</summary>
        public const int ProtocolVersion = 8;
        public const int DefaultPort = 7777;
        public const int MaxPlayers = 4;
        public const float SendInterval = 1f / 30f;
        public const int BoneSendDivider = 1; // bones with every state packet (~30 Hz)
        /// <summary>~1.35 packets behind at 30 Hz. Shared by root pose and bone sampling.</summary>
        public const float PoseInterpDelay = 0.045f;
        public const float EntitySendInterval = 1f / 15f;
        public const string ConnectionKey = "SyncRADation";
    }
}
