// Shared storage box: host apply of a client put/take, the client's one-in-flight transaction, and the take grant ack.
// The box contents themselves reach peers as a blob (StorageBoxSyncService).
using System;
using System.Collections.Generic;
using SyncRADation.ItemSystem;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    internal static class StorageService
    {
        const string GrantPrefix = "grant:";
        // persistent: per-call scratch buffer
        static readonly List<ItemBag.Stack> _boxScratch = new List<ItemBag.Stack>(16);

        /// <summary>
        /// Host: a client's StoragePut / StorageTake (Int0 = item enum, Int1 = count). Box only, never the host's own
        /// bag; the sender already adjusted its bag (put reserve / take clamp). A take acks "grant:enum:n".
        /// </summary>
        internal static bool HostApply(InteractionRequestMessage msg, out string reason)
        {
            reason = "";
            bool put = msg.Kind == InteractionKind.StoragePut;
            var item = InventoryManager.getItem((Items.itemlist)msg.Int0);
            if (item == null) return false;
            int n = msg.Int1 > 0 ? msg.Int1 : 1;

            int have = ItemBag.BoxStock(item);
            int enumVal = 0;
            try { enumVal = (int)item._item; } catch (Exception e) { Guard.Swallow(e); }
            bool unique = PartyKeyRing.IsKeyOrObject(item);
            if (unique && n > 1) n = 1;
            var sync = LanNetworkManager.Instance?.StorageSync;

            // Host-authoritative stock. The client already reserved (put) or clamped (take) against
            // its own bag before sending, so put acks carry no consume — only take grants items.
            if (!put)
            {
                if (have <= 0)
                {
                    reason = "empty";
                    PlaytestLog.Event("StorageBox", "take FAIL have=" + have + " need=" + n
                        + " item=" + msg.Int0 + " from=" + msg.SenderPlayerId);
                    // Refresh loser's LWW view so UI does not keep a ghost stack.
                    sync?.FlushDiffNow();
                    return false;
                }
                // Partial take like native retrieveItem (clamps to the box stack).
                if (n > have) n = have;
            }
            else if (unique && have >= 1)
            {
                // Unique Key/Object already boxed (often via PartyKeyRing.EnsureInBag
                // re-seeding a bag copy). Absorb sender bag; do not stack the box.
                PlaytestLog.Event("StorageBox", "put absorb unique have=" + have
                    + " item=" + msg.Int0 + " from=" + msg.SenderPlayerId);
                sync?.FlushDiffNow();
                return true;
            }

            NetGate.BeginApply();
            try
            {
                if (put) InventoryManager.boxItem(item, n);
                else n = TakeFromBox(item, n);
            }
            finally { NetGate.EndApply(); }
            sync?.FlushDiffNow();
            if (put) return true;
            if (n <= 0)
            {
                reason = "empty";
                return false;
            }
            reason = GrantPrefix + enumVal + ":" + n;
            return true;
        }

        /// <summary>
        /// Remove n from the box stack (native unboxItem drops the whole entry, retrieveItem would
        /// AddItem to the host bag — neither is right for a remote taker). Returns units removed.
        /// </summary>
        static int TakeFromBox(AnItem item, int n)
        {
            try
            {
                var dict = InventoryManager.boxItems;
                var all = ItemBag.Read(dict, _boxScratch);
                for (int i = 0; i < all.Count; i++)
                {
                    var k = all[i].Item;
                    if (k == null || k._item != item._item) continue;
                    int cur = all[i].Count;
                    if (cur <= 0) break;
                    int take = n < cur ? n : cur;
                    if (cur - take <= 0) dict.Remove(k);
                    else dict[k] = cur - take;
                    return take;
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[StorageBox] take dict: " + ex.Message);
            }
            int have = ItemBag.BoxStock(item);
            if (have <= 0) return 0;
            // unboxItem removes the whole entry (Ghidra InventoryManager.c unboxItem: boxItems.Remove): only
            // exact when the taker wants the full stack. A partial take that cannot edit the stack fails.
            if (have > n)
            {
                ModRuntime.Log?.Warning("[StorageBox] take " + n + "/" + have + " " + item._item
                    + ": box entry not editable, refused");
                return 0;
            }
            try { InventoryManager.unboxItem(item); } catch { return 0; }
            int removed = have - ItemBag.BoxStock(item);
            return removed > 0 ? removed : 0;
        }

        /// <summary>Client: the host's answer to this peer's StoragePut / StorageTake.</summary>
        internal static void HandleAck(InteractionAckMessage ack)
        {
            StorageTxn.Complete(ack.Kind, ack.Ok, ack.WorldId);
            if (!ack.Ok)
            {
                PlaytestLog.Warn("Interact", "rejected " + ack.Kind
                    + (string.IsNullOrEmpty(ack.Reason) ? "" : ": " + ack.Reason));
                return;
            }
            if (string.IsNullOrEmpty(ack.Reason) || !ack.Reason.StartsWith(GrantPrefix)) return;
            try { ApplyTakeGrant(ack.Reason.Substring(GrantPrefix.Length)); }
            catch (Exception ex) { ModRuntime.Log?.Warning("[Interact] bag ack: " + ex.Message); }
        }

        /// <summary>"enum:n": the host took n out of the box for this peer — add them to the bag.</summary>
        static void ApplyTakeGrant(string rest)
        {
            string[] parts = rest.Split(':');
            int enumVal;
            if (parts.Length < 1 || !int.TryParse(parts[0], out enumVal)) return;
            int count = 1;
            if (parts.Length >= 2) int.TryParse(parts[1], out count);
            if (count < 1) count = 1;
            var kind = (Items.itemlist)enumVal;
            var item = InventoryManager.getItem(kind);
            if (item == null) return;
            int before = ItemBag.CountInBag(kind);
            InventoryManager.AddItem(item, count);
            PartyKeyRing.OfferToHost(item);
            // AddItem silently caps at maxNumber: anything it dropped goes back in the box.
            int gained = ItemBag.CountInBag(kind) - before;
            if (gained < count)
                StorageTxn.ReturnToBox(enumVal, count - (gained > 0 ? gained : 0));
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

        // Transaction id rides InteractionRequest.WorldId (storage has no WorldId of its own) and the
        // host echoes it in the ack, so a stale / late ack or the ack of a "return" put can never
        // complete the wrong in-flight transaction.
        static long _txnCounter;
        static long _txn;
        // Puts whose 6 s timeout already gave the bag copy back: a late OK ack means the host did box it.
        // Keyed by txn id so two consecutive timeouts keep both (a single slot lost the first late OK and
        // left the item in the bag AND the box). Entries expire so a never-acked put cannot pile up.
        struct LatePut
        {
            public Items.itemlist Item;
            public int Count;
            public float Until;
        }
        const float LateWindowSec = 120f;
        static readonly Dictionary<long, LatePut> _late = new Dictionary<long, LatePut>();
        static readonly List<long> _lateScratch = new List<long>(2);

        static long NextTxn() => ++_txnCounter;

        internal static void Reset()
        {
            // Session ended with a put in flight: the host applies a put even after the sender is gone
            // (InteractionSyncService.HandleRequest only rejects DroppedPickup / StorageTake), so the item may
            // already be boxed. Giving the bag copy back would duplicate it; the reserve stays consumed.
            if (_putReserved)
                PlaytestLog.Event("StorageBox", "session end with put in flight " + _item + " x" + _count + " — left to the host box");
            _busy = false;
            _putReserved = false;
            _count = 0;
            _txn = 0;
            _late.Clear();
        }

        /// <summary>Client: start a put / take and send it. False when it must not go out (busy / nothing to put / no room).</summary>
        internal static bool TrySend(bool put, AnItem item, int enumVal, int n)
        {
            if (!TryBegin(put, item, enumVal, ref n)) return false;
            LanNetworkManager.Instance.InteractionHandlers.SendInteractionRequest(unchecked((ulong)_txn),
                put ? InteractionKind.StoragePut : InteractionKind.StorageTake, enumVal, n);
            return true;
        }

        static bool TryBegin(bool put, AnItem item, int enumVal, ref int n)
        {
            if (_busy)
            {
                if (Time.unscaledTime - _since <= TimeoutSec)
                    return false;
                PlaytestLog.Warn("StorageBox", "txn timeout — restoring reserve");
                // Restore gives the reserved put back; remember it so a late OK ack can undo that again.
                if (_putReserved)
                {
                    PurgeLate();
                    _late[_txn] = new LatePut { Item = _item, Count = _count, Until = Time.unscaledTime + LateWindowSec };
                }
                Restore();
            }
            var kind = (Items.itemlist)enumVal;
            int have = ItemBag.CountInBag(kind);
            if (put)
            {
                if (have <= 0) return false;
                if (n > have) n = have;
                AnItem held = PartyKeyRing.FindInBag(item) ?? item;
                NetGate.BeginApply();
                try
                {
                    InventoryManager.RemoveItem(held, n);
                    // storeItem would unequip a stored weapon/tool; the prefix blocked it.
                    if (ItemBag.CountInBag(kind) <= 0)
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
                if (max > 0 && have + n > max) n = max - have;
                if (n <= 0)
                {
                    PlaytestLog.Event("StorageBox", "take blocked — bag stack full item=" + kind);
                    return false;
                }
                if (have <= 0 && !ItemBag.BagHasRoom(kind))
                {
                    PlaytestLog.Event("StorageBox", "take blocked — no free slot item=" + kind);
                    return false;
                }
                _putReserved = false;
            }
            _busy = true;
            _since = Time.unscaledTime;
            _txn = NextTxn();
            return true;
        }

        internal static void Complete(InteractionKind kind, bool ok, long txn)
        {
            LatePut late;
            if (txn != 0 && kind == InteractionKind.StoragePut && _late.TryGetValue(txn, out late))
            {
                // Late ack for a put we already rolled back: if the host boxed it, take the copy out
                // again (otherwise the item exists in the bag and the box).
                _late.Remove(txn);
                if (ok && Time.unscaledTime <= late.Until) RemoveLate(late);
                return;
            }
            if (txn != _txn || !_busy)
            {
                PlaytestLog.Verbose("StorageBox", "ignored stale ack " + kind + " txn=" + txn + " cur=" + _txn);
                return;
            }
            if (kind == InteractionKind.StoragePut && _putReserved && !ok)
                Restore();
            _putReserved = false;
            _busy = false;
            _txn = 0;
        }

        static void PurgeLate()
        {
            if (_late.Count == 0) return;
            _lateScratch.Clear();
            float now = Time.unscaledTime;
            foreach (var kvp in _late)
                if (now > kvp.Value.Until) _lateScratch.Add(kvp.Key);
            for (int i = 0; i < _lateScratch.Count; i++) _late.Remove(_lateScratch[i]);
        }

        static void RemoveLate(LatePut late)
        {
            if (late.Item == Items.itemlist.None) return;
            NetGate.BeginApply();
            try
            {
                var an = InventoryManager.getItem(late.Item);
                if (an != null)
                    InventoryManager.RemoveItem(PartyKeyRing.FindInBag(an) ?? an, late.Count > 0 ? late.Count : 1);
                PlaytestLog.Event("StorageBox", "late put ack — removed restored copy " + late.Item + " x" + late.Count);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[StorageBox] late ack undo: " + ex.Message);
            }
            finally { NetGate.EndApply(); }
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
            _txn = 0;
        }

        /// <summary>Overflow the bag could not hold — host boxes it again (no bag reserve, flagged "return").</summary>
        internal static void ReturnToBox(int enumVal, int n)
        {
            if (n <= 0) return;
            var net = LanNetworkManager.Instance;
            if (!NetGate.Client) return;
            PlaytestLog.Event("StorageBox", "return overflow item=" + enumVal + " x" + n);
            // Own transaction id: its ack must not complete whatever transaction is in flight now.
            net.InteractionHandlers.SendInteractionRequest(unchecked((ulong)NextTxn()), InteractionKind.StoragePut, enumVal, n, text: "return");
        }
    }
}
