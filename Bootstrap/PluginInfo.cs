// SyncRADation — constants: version, protocol, port, send rates
namespace SyncRADation
{
    public static class PluginInfo
    {
        public const string Name = "SyncRADation";
        public const string Version = "0.5.65";
        public const string Author = "Warexpor";
        public const string Description = "LAN multiplayer mod for SIGNALIS — host-authoritative world/story, native client UX";
        /// <summary>Protocol v17: v16 + PlayerState velocity is planar (VelX, VelY): SIGNALIS walks XY, Z is height (was VelX/VelZ).
        /// v16: v15 + StoryCommit.Authoritative (full commit after a host SaveManager.Load / NewGame replaces the client's SProgress); the handshake tail read is guarded.
        /// v15: v14 + SceneHello/SceneFollow carry the WorldRegistry per-category checksum (WorldCategoryStat[] Stats), SceneDiff (74, host to client: host WorldIds of the differing categories, chunked).
        /// v14: v13 + PartyLife.Scene, Handshake GameBuildHash/GameBuild, ItemPickedUp.ClaimerPlayerId, FmodEmitter.Comp, FmodEmitterRequest (67), DropRekey (73),
        /// BonePose clamp 1023, Room as capped string, incremental StoryCommit carrying only dirty keys.
        /// v13: v12 + PuzzleStateEntry Seq/Mask, PuzzleType 78-81, StoryCmd 20-23 and WorldPickupDeny/AvatarOneShot/BossHit/EnemyAction (NetMessageType 60-63).</summary>
        public const int ProtocolVersion = 17;
        public const int DefaultPort = 7777;
        /// <summary>Session capacity incl. host. Config-driven (2..8, default 4): ModConfig.MaxPlayers.</summary>
        public static int MaxPlayers => Config.ModConfig.MaxPlayersClamped;
        public const float SendInterval = 1f / 30f;
        /// <summary>~2.5 packets behind at the real ~25 Hz send cadence (SnapClock-smoothed stamps). Shared by root pose and bone sampling.</summary>
        public const float PoseInterpDelay = 0.1f;
        /// <summary>Planar units/s above which a pose delta is a teleport (room door), not movement: velocity is dropped.</summary>
        public const float MaxProxySpeed = 80f;
        public const float EntitySendInterval = 1f / 15f;
        public const string ConnectionKey = "SyncRADation";
    }
}
