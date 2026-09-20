namespace SyncRADation.Networking
{
    /// <summary>
    /// Domain handler ownership on <see cref="LanNetworkManager"/>.
    /// Properties live here; Dispatch calls handlers directly.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal DoorNetHandlers DoorHandlers { get; private set; }
        internal AvatarNetHandlers AvatarHandlers { get; private set; }
        internal EnemyNetHandlers EnemyHandlers { get; private set; }
        internal BossNetHandlers BossHandlers { get; private set; }
        internal FmodNetHandlers FmodHandlers { get; private set; }
        internal StoryNetHandlers StoryHandlers { get; private set; }
        internal InteractionNetHandlers InteractionHandlers { get; private set; }
        internal DroppedItemNetHandlers DroppedItemHandlers { get; private set; }
        internal WorldPickupNetHandlers WorldPickupHandlers { get; private set; }
        internal PuzzleNetHandlers PuzzleHandlers { get; private set; }
        internal CombatNetHandlers CombatHandlers { get; private set; }
        internal SceneNetHandlers SceneHandlers { get; private set; }
        internal InventoryNetHandlers InventoryHandlers { get; private set; }
        internal SessionNetHandlers SessionHandlers { get; private set; }

        void ConstructDomainHandlers()
        {
            DoorHandlers = new DoorNetHandlers(this);
            AvatarHandlers = new AvatarNetHandlers(this);
            EnemyHandlers = new EnemyNetHandlers(this);
            BossHandlers = new BossNetHandlers(this);
            FmodHandlers = new FmodNetHandlers(this);
            StoryHandlers = new StoryNetHandlers(this);
            InteractionHandlers = new InteractionNetHandlers(this);
            DroppedItemHandlers = new DroppedItemNetHandlers(this);
            WorldPickupHandlers = new WorldPickupNetHandlers(this);
            PuzzleHandlers = new PuzzleNetHandlers(this);
            CombatHandlers = new CombatNetHandlers(this);
            SceneHandlers = new SceneNetHandlers(this);
            InventoryHandlers = new InventoryNetHandlers(this);
            SessionHandlers = new SessionNetHandlers(this);
        }
    }
}
