// Every static session value and how it is cleared. Registered once at boot (ModRuntime.Start).
// Session scope  (Register)           : also cleared after a host wipe reload (and on clients when the wipe message lands).
// Connection scope (RegisterConnection): StartHost / ConnectToHost / StopNetwork only. A wipe keeps the same party, and
//   these either touch the bag / key ring (the save reload just restored them) or are the session itself.
// Statics that are intentionally persistent (config, catalogs, pure caches, warn-once sets, per-process flags) carry a
// one-line comment at their declaration instead; scene-keyed caches are reset by OnSceneChanged.
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Patches;
using SyncRADation.Players;
using SyncRADation.Sync;

namespace SyncRADation
{
    internal static class SessionResetRegistrations
    {
        private static bool _done;

        public static void RegisterAll()
        {
            if (_done) return;
            _done = true;

            // ---- session scope: world-facing sticky state that a save reload must not carry over
            SessionReset.Register("Door", DoorSyncService.Reset);                       // Last* maps, held unlocks
            SessionReset.Register("Fmod", FmodEmitterSync.Reset);
            SessionReset.Register("BossAuth", KolibriAdlerAuthPatches.Clear);           // held Kolibri/Adler snapshots
            SessionReset.Register("SourceAnim", SourceAnimReader.Reset);
            SessionReset.Register("FlickerTrace", FlickerTrace.Reset);
            SessionReset.Register("BagTrace", BagTrace.Reset);
            SessionReset.Register("MoveTrace", MoveTrace.Reset);
            SessionReset.Register("Airlock", AirlockCinematic.Reset);
            SessionReset.Register("DialoguerFlavor", DialoguerGate.ResetSession);   // flavor + held dialogue callbacks + _depth/_localEnd
            SessionReset.Register("EventZone", EventZonePatch.OnSceneChanged);          // fired sets, keypad/use-item dedupe
            SessionReset.Register("DropClaims", ItemPickupPatches.ResetDropClaims);
            SessionReset.Register("ClientDamage", ClientDamageService.OnSceneChanged);
            SessionReset.Register("BossSpear", () => Net()?.BossSync.ResetSession());  // _spearTaker (first taker per spear)
            SessionReset.Register("PuzzleSync", () => Net()?.PuzzleSync.Reset());       // durable solve memory + held entries
            SessionReset.Register("PuzzleFlags", PuzzleSyncService.ResetFlags);         // ApplyingPeerPacket, _liveEdge, _mutateWorld
            SessionReset.Register("ItemPickup", ItemPickupPatches.ResetSession);        // _pendingId/_pendingItem + armed floor take
            SessionReset.Register("DropInteract", InteractorDropUpdatePatch.ResetSession); // _interactThisFrame, _highlighted
            SessionReset.Register("KeyRingCount", InventoryGetCountPatch.ResetSession); // StoryPatches _counting
            SessionReset.Register("PickupTakeScope", ItemPickupTakeScope.ResetSession);
            SessionReset.Register("NoPause", NoPausePatch.ResetSession);
            SessionReset.Register("MenuHit", MenuHit.Reset);
            SessionReset.Register("PuzzleFx", PuzzleFx.Reset);                         // live-apply flag, screen cache
            SessionReset.Register("KeypadLive", KeypadLive.Reset);                     // shared codes + last press
            SessionReset.Register("KeypadPress", KeypadPress.Clear);
            SessionReset.Register("LibraryGlide", LibraryRobotGlide.Reset);
            SessionReset.Register("KeyRingName", ItemLocalizedNamePatch.ResetSession);  // PartyKeyRingPatches _resolving (x2)
            SessionReset.Register("KeyRingGetName", InventoryGetNamePatch.ResetSession);

            // ---- connection scope: the session itself
            SessionReset.RegisterConnection("NetGate", NetGate.Reset);
            SessionReset.RegisterConnection("PartyKeyRing", PartyKeyRing.Reset);        // wipe re-imports the save's ring
            SessionReset.RegisterConnection("PartyVitals", PartyVitals.Reset);
            SessionReset.RegisterConnection("Damage", NetworkDamageSystem.Reset);
            SessionReset.RegisterConnection("PartySave", PartySaveService.Reset);
            SessionReset.RegisterConnection("HostReload", HostReload.Reset);
            SessionReset.RegisterConnection("SceneFollow", SceneFollowService.Reset);
            SessionReset.RegisterConnection("SceneWorldDiff", () => Net()?.SceneHandlers.Reset()); // WorldId divergence state
            SessionReset.RegisterConnection("StorageTxn", StorageTxn.Reset);            // drops an in-flight put/take (a put stays boxed host-side)
            SessionReset.RegisterConnection("DroppedItems", DroppedItemManager.ClearAll); // wipe clears them in the Load postfix
            SessionReset.RegisterConnection("Hitch", HitchTrace.Reset);
            SessionReset.RegisterConnection("PlaytestLog", PlaytestLog.Reset);
            SessionReset.RegisterConnection("EnemySync", () => Net()?.EnemySync.Reset());
            SessionReset.RegisterConnection("BossSync", () => Net()?.BossSync.Reset());
            SessionReset.RegisterConnection("PickupSync", () => Net()?.PickupSync.Reset());
            SessionReset.RegisterConnection("StorySync", () => Net()?.StorySync.Reset()); // also _authorDepth / _suppressForward / _flags
            SessionReset.RegisterConnection("StorageSync", () => Net()?.StorageSync.Reset());
        }

        private static LanNetworkManager Net()
        {
            return ModRuntime.Network;
        }
    }
}
