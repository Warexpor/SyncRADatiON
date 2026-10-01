// Every scene / session / connection reset, in run order. Registered once at boot (ModRuntime.Start).
//   Scene      : LanNetworkManager.OnSceneChanged -> SessionReset.RunScene, after ModRuntime.OnSceneChanged's WorldId scan +
//                WorldRegistry.Rebuild and HostReload.OnSceneArrived, before the scene hello / world dump.
//   Session    : StopNetwork (StartHost / ConnectToHost run it first) and after a host wipe reload (clients: when the wipe
//                message lands).
//   Connection : StopNetwork only. A wipe keeps the same party, and these either touch the bag / key ring (the save
//                reload just restored them) or are the session itself.
// Order matters where noted: proxies go first; the scene block follows the old LanNetworkManager.OnSceneChanged order
// (dropped visuals cleared before the respawn, registry fresh before the door / domain scene refreshes, SceneFollow
// arrival last); NetGate's apply counter is cleared after the session block, before the domain services re-enable AI.
// StaticStateGuardTests: a class whose static mutable fields are cleared here must be named in this file (a registration
// or an "also clears" comment); a static that is never reset carries a "// persistent: <reason>" line instead.
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Patches;
using SyncRADation.Players;
using SyncRADation.Sync;

namespace SyncRADation
{
    internal static class SessionResetRegistrations
    {
        // persistent: boot-once guard
        private static bool _done;

        private const ResetScope Scene = ResetScope.Scene;
        private const ResetScope Session = ResetScope.Session;
        private const ResetScope Connection = ResetScope.Connection;

        public static void RegisterAll()
        {
            if (_done) return;
            _done = true;

            // ---- proxies and per-instance net handler state first (before any domain reset)
            SessionReset.Register("Proxies", Scene | Connection, () => Net()?.ProxyManager.DestroyAll());
            SessionReset.Register("AvatarSend", Scene | Connection, () => Net()?.AvatarHandlers.ResetSendState());
            SessionReset.Register("DroppedHandlers", Connection, () => Net()?.DroppedItemHandlers.Reset());
            SessionReset.Register("SessionDumps", Connection, () => Net()?.SessionHandlers.Reset());

            // ---- scene block (old LanNetworkManager.OnSceneChanged order); some steps are also session / connection clears
            SessionReset.Register("SourceAnim", Scene | Session, SourceAnimReader.Reset);
            SessionReset.Register("Hitch", Scene | Connection, HitchTrace.Reset);
            SessionReset.Register("FriendlyFireEdge", Scene, ModRuntime.ResyncFriendlyFireEdge);
            SessionReset.Register("DroppedVisuals", Scene, DroppedItemRegistry.ClearVisuals);    // before the respawn
            SessionReset.Register("DroppedRespawn", Scene, DroppedItemRegistry.RespawnCurrentScene);
            SessionReset.Register("WorldRegistry", Scene, () => WorldRegistry.RebuildIfStale()); // before the door / domain refreshes
            SessionReset.Register("DoorScene", Scene, DoorSyncService.RefreshScene);
            SessionReset.Register("Fmod", Scene | Session, FmodEmitterSync.Reset);
            SessionReset.Register("EnemyScene", Scene, () => Net()?.EnemySync.OnSceneChanged());   // EnemySyncService room / wake caches
            SessionReset.Register("EnemyAdopted", Scene | Connection, EnemySpawnerPatches.ClearAdopted);
            SessionReset.Register("ClientDamage", Scene | Session, ClientDamageService.OnSceneChanged);
            SessionReset.Register("PuzzleScene", Scene, () => Net()?.PuzzleSync.RefreshScene());  // also clears EnvEmit once-set, BiodomeLockPatch
            SessionReset.Register("BossScene", Scene, () => Net()?.BossSync.OnSceneChanged());
            SessionReset.Register("BossAuth", Scene | Session, KolibriAdlerAuthPatches.Clear);     // held Kolibri/Adler snapshots
            SessionReset.Register("PickupScene", Scene, () => Net()?.PickupSync.RefreshScene());
            SessionReset.Register("StoryScene", Scene, () => Net()?.StorySync.OnSceneChanged());
            SessionReset.Register("EventZone", Scene | Session, EventZonePatch.OnSceneChanged);    // fired sets, request stamps, kind cache
            SessionReset.Register("KeypadPress", Scene | Session, KeypadPress.Clear);
            SessionReset.Register("UseItemSent", Scene | Session, UseItemInteractionPatch.OnSceneChanged);
            SessionReset.Register("InteractionStamps", Scene | Session, InteractionSyncService.OnSceneChanged);
            SessionReset.Register("Airlock", Scene | Session, AirlockCinematic.Reset);
            SessionReset.RegisterScene("SceneFollowArrived", SceneFollowService.NoteArrived);       // last scene step

            // ---- session scope: world-facing sticky state that a save reload must not carry over
            SessionReset.Register("Door", Session, DoorSyncService.Reset);                       // Last* maps, held unlocks
            SessionReset.Register("FlickerTrace", Session, FlickerTrace.Reset);
            SessionReset.Register("BagTrace", Session, BagTrace.Reset);
            SessionReset.Register("MoveTrace", Session, MoveTrace.Reset);
            SessionReset.Register("EventCamTrace", Session, EventCamTrace.Reset);
            SessionReset.Register("DialoguerFlavor", Session, DialoguerGate.ResetSession);       // local line id / flavor flag (key-ring name binding)
            SessionReset.Register("DropClaims", Session, ItemPickupPatches.ResetDropClaims);
            SessionReset.Register("BossSpear", Session, () => Net()?.BossSync.ResetSession());   // _spearTaker (first taker per spear)
            // PuzzleSyncService durable solve memory + held entries; also clears WorldObjectPuzzleSyncService, EnvEmit, BiodomeLockPatch.
            SessionReset.Register("PuzzleSync", Session, () => Net()?.PuzzleSync.Reset());
            SessionReset.Register("PuzzleFlags", Session, PuzzleSyncService.ResetFlags);         // ApplyingPeerPacket, _liveEdge, _mutateWorld
            SessionReset.Register("ItemPickup", Session, ItemPickupPatches.ResetSession);        // _pendingId/_pendingItem + armed floor take
            SessionReset.Register("DropInteract", Session, InteractorDropUpdatePatch.ResetSession); // _interactThisFrame, _highlighted
            SessionReset.Register("KeyRingCount", Session, InventoryGetCountPatch.ResetSession); // StoryPatches _counting
            SessionReset.Register("PickupTakeScope", Session, ItemPickupTakeScope.ResetSession);
            SessionReset.Register("NoPause", Session, NoPausePatch.ResetSession);
            SessionReset.Register("MenuHit", Session, MenuHit.Reset);
            SessionReset.Register("PuzzleFx", Session, PuzzleFx.Reset);                         // live-apply flag, screen cache
            SessionReset.Register("KeypadLive", Session, KeypadLive.Reset);                     // shared codes + last press
            SessionReset.Register("LibraryGlide", Session, LibraryRobotGlide.Reset);
            SessionReset.Register("KeyRingName", Session, ItemLocalizedNamePatch.ResetSession);  // PartyKeyRingPatches _resolving (x2)
            SessionReset.Register("KeyRingGetName", Session, InventoryGetNamePatch.ResetSession);

            // ---- connection scope: the session itself (NetGate first: the service resets below may re-enable AI)
            SessionReset.Register("NetGate", Connection, NetGate.Reset);
            SessionReset.Register("PartyKeyRing", Connection, PartyKeyRing.Reset);        // wipe re-imports the save's ring
            SessionReset.Register("PartyVitals", Connection, PartyVitals.Reset);
            SessionReset.Register("Damage", Connection, NetworkDamageSystem.Reset);
            SessionReset.Register("PartySave", Connection, PartySaveService.Reset);
            SessionReset.Register("HostReload", Connection, HostReload.Reset);
            SessionReset.Register("SceneFollow", Connection, SceneFollowService.Reset);
            SessionReset.Register("SceneWorldDiff", Connection, () => Net()?.SceneHandlers.Reset()); // WorldId divergence state
            SessionReset.Register("StorageTxn", Connection, StorageTxn.Reset);            // drops an in-flight put/take (a put stays boxed host-side)
            SessionReset.Register("DroppedItems", Connection, DroppedItemRegistry.ClearAll); // wipe clears them in the Load postfix; also DroppedItemTemplateCache
            SessionReset.Register("PlaytestLog", Connection, PlaytestLog.Reset);
            // EnemySyncService puppets + room / wake caches (Reset re-runs its scene clear).
            SessionReset.Register("EnemySync", Connection, () => Net()?.EnemySync.Reset());
            SessionReset.Register("BossSync", Connection, () => Net()?.BossSync.Reset());
            SessionReset.Register("PickupSync", Connection, () => Net()?.PickupSync.Reset());
            // StorySyncService statics too (_authorDepth / _suppressForward / _endingApply / _flags).
            SessionReset.Register("StorySync", Connection, () => Net()?.StorySync.Reset());
            SessionReset.Register("StorageSync", Connection, () => Net()?.StorageSync.Reset());
            SessionReset.Register("HostSyncFlags", Connection, Config.ModConfig.ClearHostSyncFlags); // host's toggles (PlayerRoster)
        }

        private static LanNetworkManager Net()
        {
            return LanNetworkManager.Instance;
        }
    }
}
