namespace SyncRADation
{
    public static class PluginInfo
    {
        public const string Name = "SyncRADation";
        public const string Version = "0.3.0";
        public const string Author = "Warexpor";
        public const string Description = "LAN multiplayer mod for SIGNALIS — proxy sync, doors, audio, item drops";
        public const int ProtocolVersion = 1;
        public const int DefaultPort = 7777;
        public const float SendInterval = 1f / 60f;
        public const int BoneSendDivider = 3; // send bones every 3rd state packet (20Hz)
        public const float EntitySendInterval = 1f / 30f;
    }
}
