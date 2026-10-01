using LiteNetLib;
using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    /// <summary>Door open/close send + apply (thin wrapper over DoorSyncService).</summary>
    internal sealed class DoorNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal DoorNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendDoorState(DoorStateMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.DoorState);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleDoorState(DoorStateMessage doorMsg, int senderId)
        {
            // Same gate as PuzzleSyncService.ApplyPuzzleState: a loading / other-scene peer has no matching
            // doors. The lock fact is held and re-applied when the scene is mounted; the host still relays
            // so 3+ peers stay in sync.
            bool canApply = !SceneFollowService.LocalIsTransient() && !_net.SceneMismatch;
            bool relay = true;
            if (canApply)
                relay = DoorSyncService.HandleMessage(ref doorMsg);
            else if (_net.Role != NetworkRole.Host)
                DoorSyncService.HoldMessage(doorMsg); // host state is truth: never adopt an unverified client lock flag

            if (_net.Role == NetworkRole.Host && relay)
            {
                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.DoorState);
                doorMsg.Serialize(w);
                // The applied state goes to everyone, the sender included: two peers flipping the same door at once
                // otherwise end split (each applied the other's change last, and open state is never polled). The
                // host's ReliableOrdered stream orders it after any change the host sent before, so every peer ends
                // on the host's order. Echo apply is idempotent: DoorNative returns when the door already matches,
                // the hooks find Last* already at the applied state (no re-emit) and a client never relays.
                _net.RelayRaw(w, DeliveryMethod.ReliableOrdered, -1);
            }
        }
    }
}
