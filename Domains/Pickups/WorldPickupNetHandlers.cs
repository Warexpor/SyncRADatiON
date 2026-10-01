using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    /// <summary>Host-authoritative world ItemPickup claim / grant / deny / hide wire.</summary>
    internal sealed class WorldPickupNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal WorldPickupNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendWorldPickupState(IList<WorldPickupEntry> entries, bool fullRefresh)
        {
            if (entries == null || entries.Count == 0) return;
            const int chunk = 2048; // under the reader cap
            for (int start = 0; start < entries.Count; start += chunk)
            {
                int n = entries.Count - start;
                if (n > chunk) n = chunk;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.WorldPickupState);
                writer.Put(_net.LocalPlayerId);
                writer.Put(fullRefresh);
                writer.Put(n);
                for (int i = 0; i < n; i++)
                    entries[start + i].Serialize(writer);
                _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>
        /// Client → host. remaining &gt; 0: not a claim but this peer's settled partial take (the prop kept that many),
        /// sent on the same ordered channel right after the claim, so the host always sees the claim first.
        /// </summary>
        internal void SendWorldPickupClaim(ulong worldId, Items.itemlist item = Items.itemlist.None, int count = 1,
            int remaining = 0)
        {
            var msg = new WorldPickupClaimMessage
            {
                ClaimerPlayerId = _net.LocalPlayerId,
                WorldId = unchecked((long)worldId),
                ItemEnum = (ushort)item,
                Count = count > 0 ? count : 1,
                Remaining = remaining > 0 ? remaining : 0
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.WorldPickupClaim);
            msg.Serialize(writer);
            if (_net.TryGetPeer(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host → the (remote) claimer.</summary>
        internal void SendWorldPickupGrant(int targetPlayerId, ulong worldId, Items.itemlist item, int count)
        {
            var msg = new WorldPickupGrantMessage
            {
                TargetPlayerId = targetPlayerId,
                WorldId = unchecked((long)worldId),
                ItemEnum = (ushort)item,
                Count = count
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.WorldPickupGrant);
            msg.Serialize(writer);
            if (_net.TryGetPeer(targetPlayerId, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        void SendWorldPickupDeny(int targetPlayerId, ulong worldId, Items.itemlist item, int count)
        {
            if (targetPlayerId == _net.LocalPlayerId) return;
            var msg = new WorldPickupDenyMessage
            {
                TargetPlayerId = targetPlayerId,
                WorldId = unchecked((long)worldId),
                ItemEnum = (ushort)item,
                Count = count > 0 ? count : 1
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.WorldPickupDeny);
            msg.Serialize(writer);
            if (_net.TryGetPeer(targetPlayerId, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleWorldPickupDeny(WorldPickupDenyMessage deny)
        {
            _net.PickupSync.ApplyDeny(deny);
        }

        internal void HandleWorldPickupState(WorldPickupStateMessage pickMsg)
        {
            if (NetGate.HostRole)
                return;
            _net.PickupSync.ApplyHide(pickMsg);
        }

        internal void HandleWorldPickupClaim(WorldPickupClaimMessage claim)
        {
            if (!NetGate.HostRole)
                return;
            ulong id = unchecked((ulong)claim.WorldId);

            // Settled partial take: give the rest back to everyone (only the claim's owner can).
            if (claim.Remaining > 0)
            {
                _net.PickupSync.HostReleaseRemainder(id, claim.ClaimerPlayerId, claim.Remaining);
                return;
            }

            // Peer disconnected before host handled the claim — do not hide the prop.
            if (claim.ClaimerPlayerId != _net.LocalPlayerId && !_net.HasPeer(claim.ClaimerPlayerId))
            {
                ModRuntime.Log?.Msg("[WorldPickup] Claim ignored — peer gone id=" + id.ToString("X16")
                    + " by " + claim.ClaimerPlayerId);
                return;
            }

            // HasPeer was checked on entry and the grant goes out in this same call: no orphan window.
            if (_net.PickupSync.HostClaim(id, claim.ClaimerPlayerId, (Items.itemlist)claim.ItemEnum, claim.Count,
                    hostNative: false))
                return;
            ModRuntime.Log?.Msg("[WorldPickup] Claim denied id=" + id.ToString("X16") + " by " + claim.ClaimerPlayerId);
            // Inspect-path claimers already ran native pickUp: tell them to take the item back.
            SendWorldPickupDeny(claim.ClaimerPlayerId, id, (Items.itemlist)claim.ItemEnum, claim.Count);
        }

        internal void HandleWorldPickupGrant(WorldPickupGrantMessage grant)
        {
            _net.PickupSync.ApplyGrant(grant);
        }
    }
}
