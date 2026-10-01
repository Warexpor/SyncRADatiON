// Host apply of a client's UseItem / UseItemMulti, and the ConsumesKey ring revoke shared with host-local unlocks.
using System.Reflection;
using SyncRADation.Patches;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    public static partial class InteractionSyncService
    {
        private static bool ApplyUseItem(ulong id, int senderId, out string consumeReason)
        {
            consumeReason = "";
            var u = Find<UseItemInteraction>(id);
            if (u == null)
            {
                PlaytestLog.Event("Interact", "UseItem other-scene id=" + id.ToString("X16") + " from=" + senderId);
                return true;
            }
            // Already unlocked (the host's own Dialoguer path unlocked it first): a ConsumesKey key is still revoked.
            if (u.unlocked && !u.repeatable)
            {
                TryRevokeUseItemKey(u);
                return true;
            }

            AnItem key = u.key;
            if (key != null && !PartyKeyRing.LocalOrRingHas(key) && !TrustKey(key, senderId, "UseItem")) return false;

            // Do not latch unlocked here: SnapUseItemWorld's rising edge needs wasUnlocked=false so the host Invokes
            // onSuccessful (Disk InsertDisk* / Tarot PlaceCard*). The snap sets unlocked + doors.
            PuzzleSyncService.TryUnlockDoors(u.gameObject);
            try
            {
                PuzzleSyncService.SnapUseItemWorld(u);
                LanNetworkManager.Instance.PuzzleSync.Emit(PuzzleType.UseItemInteraction, id, u);
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            if (UnlockInteractiveLocks(u, key))
            {
                bool hostHad = PartyKeyRing.InLocalBag(key);
                ConsumeKey(key);
                if (!hostHad) consumeReason = "consume:" + (int)key._item;
            }
            else
                PartyKeyRing.Note(key);
            PartyKeyRing.Broadcast();
            return true;
        }

        private static bool ApplyUseItemMulti(ulong id, int senderId, out string consumeReason)
        {
            consumeReason = "";
            var m = Find<UseItemMultiInteraction>(id);
            if (m == null) return false;
            var ready = typeof(UseItemMultiInteraction).GetMethod("ready",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ready == null) return false;

            var list = m.Interactions;
            int n = list != null ? list.Count : 0;
            for (int i = 0; i < n; i++)
            {
                var u = list[i];
                AnItem key = u != null ? u.key : null;
                if (key == null || PartyKeyRing.LocalOrRingHas(key)) continue;
                if (!TrustKey(key, senderId, "UseItemMulti")) return false;
            }

            NetGate.BeginApply();
            try { ready.Invoke(m, null); }
            finally { NetGate.EndApply(); }

            // ready() unlocked natively; whatever consume step fails, the ring broadcast below still goes out.
            var parts = new System.Collections.Generic.List<string>();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var u = list[i];
                    AnItem key = u != null ? u.key : null;
                    if (key == null) continue;
                    if (UnlockInteractiveLocks(u, key))
                    {
                        bool hostHad = PartyKeyRing.InLocalBag(key);
                        ConsumeKey(key);
                        if (!hostHad) parts.Add((int)key._item + ":1");
                    }
                    else
                        PartyKeyRing.Note(key);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (parts.Count > 0)
                consumeReason = "consume:" + string.Join("|", parts);
            PartyKeyRing.Broadcast();
            return true;
        }

        /// <summary>
        /// A client used a key the host holds neither in its bag nor on the party ring (the ring update is still in
        /// flight): trust the requester, never the host itself, and note the key on the ring.
        /// </summary>
        static bool TrustKey(AnItem key, int senderId, string what)
        {
            var net = LanNetworkManager.Instance;
            int localId = net != null ? net.LocalPlayerId : 0;
            if (senderId == localId || senderId < 0) return false;
            try { PartyKeyRing.Note(key._item); }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
            PlaytestLog.Event("KeyRing", "trust " + what + " from=" + senderId + " " + key._item);
            return true;
        }

        private static void ConsumeKey(AnItem key)
        {
            // Ring drop + bag copy strip on all peers via CraftRevokeSentinel fan-out.
            try { PartyKeyRing.RevokeConsumed(key._item); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Host-local UseItem unlock (Dialoguer onMessageEvent / dialogueOver) never sends an InteractionRequest, only
        /// clients do: run the same ConsumesKey ring revoke ApplyUseItem would, so every peer's bag copy drops.
        /// Idempotent with ApplyUseItem / CraftRevokeSentinel.
        /// </summary>
        public static void HostRevokeIfConsumed(UseItemInteraction u)
        {
            if (u != null) TryRevokeUseItemKey(u);
        }

        /// <summary>Host-local UseItemMulti.ready() runs natively without ApplyUseItemMulti: revoke each ConsumesKey part.</summary>
        public static void HostRevokeUseItemMulti(UseItemMultiInteraction m)
        {
            if (m == null) return;
            try
            {
                var list = m.Interactions;
                if (list == null) return;
                bool any = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var u = list[i];
                    if (u != null && TryRevokeUseItemKey(u)) any = true;
                }
                if (any) PartyKeyRing.Broadcast();
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <returns>true if a ConsumesKey revoke ran</returns>
        static bool TryRevokeUseItemKey(UseItemInteraction u)
        {
            try
            {
                AnItem key = u.key;
                if (key == null || !UnlockInteractiveLocks(u, key)) return false;
                ConsumeKey(key);
                PartyKeyRing.Broadcast();
                return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        /// <summary>Unlock the locks this UseItem opens; true when one of them consumes the key.</summary>
        static bool UnlockInteractiveLocks(UseItemInteraction u, AnItem key)
        {
            if (u == null) return false;
            bool consumes = UnlockLock(u, key);
            consumes |= UnlockLockSingle(u, key);
            consumes |= ConnectedDoorsConsume(u, key);
            return consumes;
        }

        static bool UnlockLock(UseItemInteraction u, AnItem key)
        {
            try
            {
                var lockComp = u.GetComponent<InteractiveLock>() ?? u.GetComponentInChildren<InteractiveLock>(true);
                if (lockComp == null) return false;
                lockComp.locked = false;
                return lockComp.ConsumesKey && key != null;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        static bool UnlockLockSingle(UseItemInteraction u, AnItem key)
        {
            bool consumes = false;
            try
            {
                var single = u.GetComponent<InteractiveLockSingle>()
                    ?? u.GetComponentInChildren<InteractiveLockSingle>(true)
                    ?? u.GetComponentInParent<InteractiveLockSingle>();
                if (single == null) return false;
                consumes = single.ConsumesKey && key != null;
                var master = single.master;
                bool allow = master == null || DoorNative.HasUnlocker(master);
                if (master != null && allow) master.locked = false;
                if (single.door != null && allow) single.door.locked = false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return consumes;
        }

        /// <summary>
        /// ConnectedDoors.ConsumesKey: native Unlock() (DoorNative.ApplyConnectedDoors) only copies the flag + key onto
        /// the AutoTraverseDoor InteractiveLockSingle siblings, it never RemoveItem. The UseItem is often a CD descendant
        /// while those sit on the A/B siblings, so GetComponentInParent/Children from the UseItem never sees them (the
        /// same ring / bag copy softlock as InteractiveLock.ConsumesKey). The key enum must match, so default
        /// ConsumesKey=true templates with a null key do not false-revoke.
        /// </summary>
        static bool ConnectedDoorsConsume(UseItemInteraction u, AnItem key)
        {
            if (key == null) return false;
            try
            {
                var cd = PuzzleDomainUtil.FindInParents<ConnectedDoors>(u.gameObject);
                if (cd == null) return false;
                Items.itemlist want = key._item;
                if (cd.ConsumesKey && cd.key != null && cd.key._item == want) return true;
                var singles = cd.GetComponentsInChildren<InteractiveLockSingle>(true);
                if (singles == null) return false;
                for (int i = 0; i < singles.Length; i++)
                {
                    var s = singles[i];
                    if (s != null && s.ConsumesKey && s.key != null && s.key._item == want) return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }
    }
}
