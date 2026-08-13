// SyncRADation — constants: version, protocol, port, send rates
namespace SyncRADation
{
    public static class PluginInfo
    {
        public const string Name = "SyncRADation";
        public const string Version = "0.4.2-dev";
        public const string Author = "Warexpor";
        public const string Description = "LAN multiplayer mod for SIGNALIS — host-authoritative world/story, native client UX";
        /// <summary>Protocol v7: v6 world dump + player roster/leave for 3+ peers.</summary>
        public const int ProtocolVersion = 7;
        public const int DefaultPort = 7777;
        public const int MaxPlayers = 4;
        public const float SendInterval = 1f / 30f;
        public const int BoneSendDivider = 2; // bones every 2nd state packet (~15 Hz)
        public const float EntitySendInterval = 1f / 15f;
        public const string ConnectionKey = "SyncRADation";
    }
}
