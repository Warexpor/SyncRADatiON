using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    /// <summary>Host-authoritative world ItemPickup claim / grant / hide wire.</summary>
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

        internal void SendWorldPickupClaim(ulong worldId, Items.itemlist item = Items.itemlist.None, int count = 1)
        {
            var msg = new WorldPickupClaimMessage
            {
                ClaimerPlayerId = _net.LocalPlayerId,
                WorldId = unchecked((long)worldId),
                ItemEnum = (ushort)item,
                Count = count > 0 ? count : 1
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.WorldPickupClaim);
            msg.Serialize(writer);
            // Client → host only
            if (_net.TryGetPeer(0, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

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
            if (_net.TryGetPeer(targetPlayerId, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            // Host also grants if target is host
            if (targetPlayerId == _net.LocalPlayerId)
                _net.PickupSync.ApplyGrant(msg);
        }

        internal void SendWorldPickupDeny(int targetPlayerId, ulong worldId, Items.itemlist item, int count)
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
            if (_net.TryGetPeer(targetPlayerId, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleWorldPickupDeny(WorldPickupDenyMessage deny)
        {
            _net.PickupSync.ApplyDeny(deny);
        }

        internal void HandleWorldPickupState(WorldPickupStateMessage pickMsg)
        {
            if (_net.Role == NetworkRole.Host)
                return;
            _net.PickupSync.ApplyHide(pickMsg);
        }

        internal void HandleWorldPickupClaim(WorldPickupClaimMessage claim)
        {
            if (_net.Role != NetworkRole.Host)
                return;

            // Peer disconnected before host handled the claim — do not hide the prop.
            if (claim.ClaimerPlayerId != _net.LocalPlayerId && !_net.HasPeer(claim.ClaimerPlayerId))
            {
                ModRuntime.Log?.Msg("[WorldPickup] Claim ignored — peer gone id="
                    + unchecked((ulong)claim.WorldId).ToString("X16")
                    + " by " + claim.ClaimerPlayerId);
                return;
            }

            ulong id = unchecked((ulong)claim.WorldId);
            Items.itemlist item;
            int count;
            if (!_net.PickupSync.TryClaimOnHost(id, claim.ClaimerPlayerId, out item, out count, hideNow: true,
                    hintItem: (Items.itemlist)claim.ItemEnum, hintCount: claim.Count))
            {
                ModRuntime.Log?.Msg("[WorldPickup] Claim denied id=" + id.ToString("X16")
                    + " by " + claim.ClaimerPlayerId);
                // Inspect-path claimers already ran native pickUp: tell them to take the item back.
                SendWorldPickupDeny(claim.ClaimerPlayerId, id, item, count);
                return;
            }

            ModRuntime.Log?.Msg("[WorldPickup] Claim OK id=" + id.ToString("X16")
                + " item=" + item + " x" + count + " → player " + claim.ClaimerPlayerId);

            PartyKeyRing.Note(item);
            PartyKeyRing.Broadcast();

            if (claim.ClaimerPlayerId == _net.LocalPlayerId)
            {
                try
                {
                    var an = InventoryManager.getItem(item);
                    if (an != null) InventoryManager.AddItem(an, count > 0 ? count : 1);
                }
                catch { }
            }
            else if (item != Items.itemlist.None)
            {
                if (!_net.HasPeer(claim.ClaimerPlayerId))
                {
                    // Claim reserved then peer dropped before grant — Key/Object stay on ring;
                    // ammo/docs must not stay hidden with nobody holding them.
                    if (!PartyKeyRing.IsKeyOrObject(item))
                    {
                        int rolled = _net.PickupSync.ReleaseOrphanClaimsForPlayer(claim.ClaimerPlayerId);
                        ModRuntime.Log?.Msg("[WorldPickup] Claim rolled back — peer gone mid-grant id="
                            + id.ToString("X16") + " released=" + rolled);
                        return;
                    }
                }
                else
                    SendWorldPickupGrant(claim.ClaimerPlayerId, id, item, count > 0 ? count : 1);
            }
            else
                ModRuntime.Log?.Warning("[WorldPickup] Claim OK but item None id=" + id.ToString("X16"));

            _net.PickupSync.BroadcastTriggered(id, true);
            _net.PickupSync.HideClaimed(null);
        }

        internal void HandleWorldPickupGrant(WorldPickupGrantMessage grant)
        {
            _net.PickupSync.ApplyGrant(grant);
        }
    }
}
