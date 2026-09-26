using System;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    /// <summary>Interaction request/ack send + thin host apply / bag ack.</summary>
    internal sealed class InteractionNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal InteractionNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendInteractionRequest(ulong worldId, InteractionKind kind, int int0 = 0, int int1 = 0,
            float f0 = 0f, float f1 = 0f, float f2 = 0f, string text = "")
        {
            var msg = new InteractionRequestMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                WorldId = unchecked((long)worldId),
                Kind = kind,
                Int0 = int0,
                Int1 = int1,
                Float0 = f0,
                Float1 = f1,
                Float2 = f2,
                Text = text ?? ""
            };
            if (kind != InteractionKind.Gunshot)
                PlaytestLog.Event("Interact", "request " + kind + " id=" + worldId.ToString("X16"));
            if (_net.Role == NetworkRole.Host)
            {
                InteractionSyncService.HandleRequest(msg);
                return;
            }
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.InteractionRequest);
            msg.Serialize(writer);
            if (_net.TryGetPeer(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void SendInteractionAck(int targetPlayerId, long worldId, InteractionKind kind, bool ok, string reason)
        {
            var msg = new InteractionAckMessage
            {
                TargetPlayerId = targetPlayerId,
                WorldId = worldId,
                Kind = kind,
                Ok = ok,
                Reason = reason ?? ""
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.InteractionAck);
            msg.Serialize(writer);
            if (targetPlayerId == _net.LocalPlayerId) return;
            if (_net.TryGetPeer(targetPlayerId, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleInteractionRequest(InteractionRequestMessage msg)
        {
            if (_net.Role == NetworkRole.Host)
                InteractionSyncService.HandleRequest(msg);
        }

        internal void HandleInteractionAck(InteractionAckMessage ack)
        {
            if (ack.TargetPlayerId != _net.LocalPlayerId) return;

            if (ack.Kind == InteractionKind.DroppedPickup)
                ItemPickupPatches.NoteDropClaimAck(ack.Ok, ack.WorldId);

            if (!ack.Ok)
            {
                PlaytestLog.Warn("Interact", "rejected " + ack.Kind
                    + (string.IsNullOrEmpty(ack.Reason) ? "" : ": " + ack.Reason));
                return;
            }

            if (string.IsNullOrEmpty(ack.Reason)) return;
            try
            {
                ApplyBagAck(ack.Reason);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Interact] bag ack: " + ex.Message);
            }
        }

        static void ApplyBagAck(string reason)
        {
            bool consume = reason.StartsWith("consume:");
            bool grant = reason.StartsWith("grant:");
            if (!consume && !grant) return;
            string rest = reason.Substring(consume ? 8 : 6);
            string[] groups = rest.Split('|');
            for (int g = 0; g < groups.Length; g++)
                ApplyBagAckPart(consume, groups[g]);
        }

        static void ApplyBagAckPart(bool consume, string rest)
        {
            string[] parts = rest.Split(':');
            if (parts.Length < 1) return;
            int enumVal;
            if (!int.TryParse(parts[0], out enumVal)) return;
            int count = 1;
            if (parts.Length >= 2)
                int.TryParse(parts[1], out count);
            if (count < 1) count = 1;
            var item = InventoryManager.getItem((Items.itemlist)enumVal);
            if (item == null) return;
            if (consume) InventoryManager.RemoveItem(item, count);
            else
            {
                InventoryManager.AddItem(item, count);
                PartyKeyRing.OfferToHost(item);
            }
        }
    }
}
