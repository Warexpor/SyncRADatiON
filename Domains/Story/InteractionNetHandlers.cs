using System;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

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
            if (ack.Kind == InteractionKind.StoragePut || ack.Kind == InteractionKind.StorageTake)
                StorageTxn.Complete(ack.Kind, ack.Ok);

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
                int before = DroppedItemManager.CountInBag((Items.itemlist)enumVal);
                InventoryManager.AddItem(item, count);
                PartyKeyRing.OfferToHost(item);
                // AddItem silently caps at maxNumber: anything it dropped goes back in the box.
                int gained = DroppedItemManager.CountInBag((Items.itemlist)enumVal) - before;
                if (gained < count)
                    StorageTxn.ReturnToBox(enumVal, count - (gained > 0 ? gained : 0));
            }
        }
    }

    /// <summary>
    /// Client storage box transaction guard. Put reserves (removes) the bag copy before the request so a
    /// double press cannot box twice; a failed/lost ack gives it back. Take clamps to bag room first and
    /// returns any overflow AddItem could not hold to the box.
    /// </summary>
    internal static class StorageTxn
    {
        const float TimeoutSec = 6f;
        static bool _busy;
        static float _since;
        static bool _putReserved;
        static Items.itemlist _item;
        static int _count;

        internal static void Reset()
        {
            // Session ended with a put in flight: give the bag copy back (ack will never arrive).
            if (_putReserved) Restore();
            _busy = false;
            _putReserved = false;
            _count = 0;
        }

        /// <summary>Returns false when the request must not be sent (busy / nothing to put / no room).</summary>
        internal static bool TryBegin(bool put, AnItem item, int enumVal, ref int n)
        {
            if (_busy)
            {
                if (Time.unscaledTime - _since <= TimeoutSec)
                    return false;
                PlaytestLog.Warn("StorageBox", "txn timeout — restoring reserve");
                Restore();
            }
            var kind = (Items.itemlist)enumVal;
            if (put)
            {
                int have = DroppedItemManager.CountInBag(kind);
                if (have <= 0) return false;
                if (n > have) n = have;
                AnItem held = PartyKeyRing.FindInBag(item) ?? item;
                NetGate.BeginApply();
                try
                {
                    InventoryManager.RemoveItem(held, n);
                    // storeItem would unequip a stored weapon/tool; the prefix blocked it.
                    if (DroppedItemManager.CountInBag(kind) <= 0)
                    {
                        var w = InventoryManager.EquippedWeapon;
                        if (w != null && w.parentItem != null && w.parentItem._item == kind)
                            InventoryManager.EquippedWeapon = null;
                        var t = InventoryManager.EquippedTool;
                        if (t != null && t._item == kind)
                            InventoryManager.EquippedTool = null;
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.Warning("[StorageBox] reserve: " + ex.Message);
                    return false;
                }
                finally { NetGate.EndApply(); }
                _putReserved = true;
                _item = kind;
                _count = n;
            }
            else
            {
                int max = item != null ? item.maxNumber : 0;
                int have = DroppedItemManager.CountInBag(kind);
                if (max > 0 && have + n > max) n = max - have;
                if (n <= 0)
                {
                    PlaytestLog.Event("StorageBox", "take blocked — bag stack full item=" + kind);
                    return false;
                }
                var net = LanNetworkManager.Instance;
                if (have <= 0 && net != null && !net.DroppedItemHandlers.HasBagRoom(kind))
                {
                    PlaytestLog.Event("StorageBox", "take blocked — no free slot item=" + kind);
                    return false;
                }
                _putReserved = false;
            }
            _busy = true;
            _since = Time.unscaledTime;
            return true;
        }

        internal static void Complete(InteractionKind kind, bool ok)
        {
            if (kind == InteractionKind.StoragePut && _putReserved && !ok)
                Restore();
            _putReserved = false;
            _busy = false;
        }

        static void Restore()
        {
            if (_putReserved && _item != Items.itemlist.None)
            {
                NetGate.BeginApply();
                try
                {
                    var an = InventoryManager.getItem(_item);
                    if (an != null) InventoryManager.AddItem(an, _count > 0 ? _count : 1);
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.Warning("[StorageBox] restore: " + ex.Message);
                }
                finally { NetGate.EndApply(); }
                PlaytestLog.Event("StorageBox", "put rolled back " + _item + " x" + _count);
            }
            _putReserved = false;
            _busy = false;
        }

        /// <summary>Overflow the bag could not hold — host boxes it again (no bag reserve, flagged "return").</summary>
        internal static void ReturnToBox(int enumVal, int n)
        {
            if (n <= 0) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || net.Role == NetworkRole.Host) return;
            PlaytestLog.Event("StorageBox", "return overflow item=" + enumVal + " x" + n);
            net.SendInteractionRequest(0, InteractionKind.StoragePut, enumVal, n, text: "return");
        }
    }
}
