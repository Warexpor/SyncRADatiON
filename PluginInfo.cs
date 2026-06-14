// SyncRADation — constants: version, protocol, port, send rates
namespace SyncRADation
{
    public static class PluginInfo
    {
        public const string Name = "SyncRADation";
        public const string Version = "0.3.0";
        public const string Author = "Warexpor";
        public const string Description = "LAN multiplayer mod for SIGNALIS â€” proxy sync, doors, audio, item drops";
        public const int ProtocolVersion = 1;
        public const int DefaultPort = 7777;
        public const float SendInterval = 1f / 30f;
        public const int BoneSendDivider = 2; // send bones every 2nd state packet (15Hz)
        public const float EntitySendInterval = 1f / 30f;
    }
}
