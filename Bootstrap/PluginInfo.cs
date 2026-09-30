// SyncRADation — constants: version, protocol, port, send rates
namespace SyncRADation
{
    public static class PluginInfo
    {
        public const string Name = "SyncRADation";
        public const string Version = "0.5.57";
        public const string Author = "Warexpor";
        public const string Description = "LAN multiplayer mod for SIGNALIS — host-authoritative world/story, native client UX";
        /// <summary>Protocol v12: v11 + handshake SchemaHash/ModVersion and PartyLife/PartySave/PartyRoom (NetMessageType 40-42).</summary>
        public const int ProtocolVersion = 12;
        public const int DefaultPort = 7777;
        /// <summary>Session capacity incl. host. Config-driven (2..8, default 4): ModConfig.MaxPlayers.</summary>
        public static int MaxPlayers => Config.ModConfig.MaxPlayersClamped;
        public const float SendInterval = 1f / 30f;
        /// <summary>~1.35 packets behind at 30 Hz. Shared by root pose and bone sampling.</summary>
        public const float PoseInterpDelay = 0.045f;
        public const float EntitySendInterval = 1f / 15f;
        public const string ConnectionKey = "SyncRADation";
    }
}
