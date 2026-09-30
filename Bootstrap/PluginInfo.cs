// SyncRADation — constants: version, protocol, port, send rates
namespace SyncRADation
{
    public static class PluginInfo
    {
        public const string Name = "SyncRADation";
        public const string Version = "0.5.60";
        public const string Author = "Warexpor";
        public const string Description = "LAN multiplayer mod for SIGNALIS — host-authoritative world/story, native client UX";
        /// <summary>Protocol v15: v14 + SceneHello/SceneFollow carry the WorldRegistry per-category checksum (WorldCategoryStat[] Stats), SceneDiff (74, host to client: host WorldIds of the differing categories, chunked).
        /// v14: v13 + PartyLife.Scene, Handshake GameBuildHash/GameBuild, ItemPickedUp.ClaimerPlayerId, FmodEmitter.Comp, FmodEmitterRequest (67), DropRekey (73),
        /// BonePose clamp 1023, Room as capped string, incremental StoryCommit carrying only dirty keys.
        /// v13: v12 + PuzzleStateEntry Seq/Mask, PuzzleType 78-81, StoryCmd 20-23 and WorldPickupDeny/AvatarOneShot/BossHit/EnemyAction (NetMessageType 60-63).</summary>
        public const int ProtocolVersion = 15;
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
