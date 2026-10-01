// Host-authoritative world ItemPickup: claim → grant claimer, hide for all peers.
using System.Collections.Generic;
using SyncRADation.ItemSystem;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class WorldPickupSyncService
    {
        private float _timer;
        private const float Interval = 0.4f;
        private bool _needFull = true;
        private readonly Dictionary<ulong, bool> _lastTriggered = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, bool> _lastActive = new Dictionary<ulong, bool>();
        private readonly HashSet<ulong> _claimed = new HashSet<ulong>();
        private readonly HashSet<ushort> _claimedItems = new HashSet<ushort>();
        private readonly HashSet<ushort> _keepItemsScratch = new HashSet<ushort>();
        private readonly Dictionary<ulong, int> _claimerOf = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, Items.itemlist> _claimedItemOf = new Dictionary<ulong, Items.itemlist>();
        private readonly Dictionary<ulong, ItemPickup> _byId = new Dictionary<ulong, ItemPickup>();
        /// <summary>Once-per-WorldId party onPickup Invoke (Dig H). Cleared on scene refresh.</summary>
        private readonly HashSet<ulong> _partyOnPickupFired = new HashSet<ulong>();
        private bool _scanned;
        private readonly List<WorldPickupEntry> _tickList = new List<WorldPickupEntry>(32);

        /// <summary>
        /// Inspect-path claims: native pickUp/release already adds (or is about to add) the item to
        /// this peer's bag. ApplyGrant must skip AddItem for these ids; a host deny must take it back.
        /// Non-inspect claims never appear here (client Prefix blocks native pickUp), so their grant
        /// always AddItems even when the bag already holds a stack (ammo/health/batteries).
        /// </summary>
        private struct NativePending
        {
            public Items.itemlist Item;
            public int Count;
            public int BagBefore;
            /// <summary>Weapon magazine before native release (-1 = not an ammo item with a loaded weapon): MagFill gains.</summary>
            public int MagBefore;
            public float Time;
        }

        private struct PendingRevert
        {
            public Items.itemlist Item;
            public int Count;
            public int BagBefore;
            public int MagBefore;
            public float Deadline;
            public ItemPickup Prop;
        }

        private readonly Dictionary<ulong, NativePending> _nativePending = new Dictionary<ulong, NativePending>();
        private readonly List<PendingRevert> _reverts = new List<PendingRevert>(2);
        private const float RevertWindow = 4f;

        // Native ItemPickup.dialoguerCallback schedules Invoke("release", 0.1) (Ghidra ItemPickup.c).
        // Unity cancels/never runs a pending Invoke once the GameObject is deactivated, so a prop must
        // not be hidden for its own claimer until release has run. Key = WorldId, value = safety expiry.
        private readonly Dictionary<ulong, float> _releasePending = new Dictionary<ulong, float>();
        private readonly HashSet<ulong> _deferredHide = new HashSet<ulong>();
        private readonly List<ulong> _expiredScratch = new List<ulong>(2);
        private const float ReleaseWaitOpen = 120f;
        private const float ReleaseWaitCallback = 3f;

        // Claims whose native release must be measured: what the bag really gained vs the claim count.
        private readonly Dictionary<ulong, NativePending> _gainCheck = new Dictionary<ulong, NativePending>();
        private readonly List<ulong> _extendScratch = new List<ulong>(2);

        // Scan-time list of Key/Object props: the only ones IsClaimedPickup can hide by item enum
        // (a second instance of a claimed unique). HideClaimed(null) walks _claimed + this, not every prop.
        private struct UniqueProp
        {
            public ulong Id;
            public ItemPickup P;
            public ushort Item;
        }
        private readonly List<UniqueProp> _uniqueProps = new List<UniqueProp>(8);
        // A miss in _byId (prop instantiated after the scan) rescans at most this often.
        private float _lastRescanAt = -10f;
        private const float RescanCooldown = 1f;

        /// <summary>
        /// Native release runs ~0.1s after dialoguerCallback. Record the bag count now so a later
        /// deny can tell whether (and how much) native already added.
        /// </summary>
        public void NoteNativeGrantExpected(ulong worldId, Items.itemlist item, int count)
        {
            if (worldId == 0 || item == Items.itemlist.None) return;
            int before = DroppedItemManager.CountInBag(item);
            _nativePending[worldId] = new NativePending
            {
                Item = item,
                Count = count > 0 ? count : 1,
                BagBefore = before,
                MagBefore = MagAmmoOf(item),
                Time = Time.unscaledTime
            };
            ExpectGain(worldId, item, count, before);
        }

        /// <summary>Remember the bag before native release so a partial AddItemToMax can be measured.</summary>
        public void ExpectGain(ulong worldId, Items.itemlist item, int count, int bagBefore = -1)
        {
            if (worldId == 0 || item == Items.itemlist.None) return;
            if (bagBefore < 0) bagBefore = DroppedItemManager.CountInBag(item);
            _gainCheck[worldId] = new NativePending
            {
                Item = item,
                Count = count > 0 ? count : 1,
                BagBefore = bagBefore,
                MagBefore = MagAmmoOf(item),
                Time = Time.unscaledTime
            };
        }

        /// <summary>
        /// Magazine round count of the weapon that fires this ammo, or -1 when it is not ammo / no weapon is held.
        /// Native release takes the MagFill branch when the bag is full: the ammo goes straight into the
        /// magazine and never touches the bag, so a bag-count delta alone reads it as "gained nothing".
        /// </summary>
        static int MagAmmoOf(Items.itemlist kind)
        {
            try
            {
                var an = InventoryManager.getItem(kind);
                if (an == null || an.type != AnItem.AnItemType.Ammo) return -1;
                var w = InventoryManager.getWeaponFromAmmo(an);
                if (w == null || w.parentItem == null || !InventoryManager.hasItem(w.parentItem)) return -1;
                return w.magAmmo;
            }
            catch (System.Exception e) { Guard.Swallow(e); return -1; }
        }

        /// <summary>True when a full bag would still take this ammo into the weapon magazine (native MagFill).</summary>
        public static bool WouldMagFill(Items.itemlist kind)
        {
            try
            {
                var an = InventoryManager.getItem(kind);
                if (an == null || an.type != AnItem.AnItemType.Ammo) return false;
                var settings = PlayerState.settings;
                if (settings == null || !settings.allowMagfilling) return false;
                var w = InventoryManager.getWeaponFromAmmo(an);
                if (w == null || w.parentItem == null || !InventoryManager.hasItem(w.parentItem)) return false;
                return w.magAmmo < w.MagSize;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        static ulong IdOf(ItemPickup p)
        {
            if (p == null) return 0;
            try { return p.gameObject != null ? WorldId.FromGameObject(p.gameObject) : 0UL; }
            catch (System.Exception e) { Guard.Swallow(e); return 0; }
        }

        /// <summary>
        /// Native pickUp/dialoguerCallback is running for this prop on the local peer: its release is
        /// still to come, so HidePickup defers. seconds = safety expiry if release never runs.
        /// </summary>
        public void MarkReleasePending(ItemPickup p, bool callbackSeen)
        {
            ulong id = IdOf(p);
            if (id == 0) return;
            _releasePending[id] = Time.unscaledTime + (callbackSeen ? ReleaseWaitCallback : ReleaseWaitOpen);
        }

        /// <summary>The release Invoke runs on scaled time and the pickup state stays "dialogue" until it ran.</summary>
        static bool ReleaseStillComing()
        {
            try
            {
                if (Time.timeScale <= 0.0001f) return true;
                return PlayerState.gameState == PlayerState.gameStates.dialogue;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        bool IsReleasePending(ulong id)
        {
            float until;
            if (!_releasePending.TryGetValue(id, out until)) return false;
            if (Time.unscaledTime > until)
            {
                // Paused / dialogue still open: release has not had a chance to run, keep waiting.
                if (ReleaseStillComing()) return true;
                _releasePending.Remove(id);
                return false;
            }
            return true;
        }

        /// <summary>pickUp produced no dialogue (refused / threw): no release will follow, so nothing to wait for.</summary>
        public void ClearReleasePending(ItemPickup p)
        {
            ulong id = IdOf(p);
            if (id == 0 || !_releasePending.Remove(id)) return;
            if (_deferredHide.Remove(id) && p != null) HideOnePickup(p);
        }

        /// <summary>True when native pickUp opened its dialogue (state == dialogue), i.e. a release is coming.</summary>
        public static bool DialogueOpenNow()
        {
            try { return PlayerState.gameState == PlayerState.gameStates.dialogue; }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>Native release finished: measure the gain, settle a partial take, then hide.</summary>
        public void OnNativeRelease(ItemPickup p)
        {
            ulong id = IdOf(p);
            if (id == 0) return;
            _releasePending.Remove(id);
            CheckGain(id, p);
            if (_deferredHide.Remove(id))
                HideOnePickup(p);
        }

        void CheckGain(ulong id, ItemPickup p)
        {
            NativePending np;
            if (!_gainCheck.TryGetValue(id, out np)) return;
            _gainCheck.Remove(id);
            int gained = DroppedItemManager.CountInBag(np.Item) - np.BagBefore;
            if (gained < 0) gained = 0;
            // hasItem/getCount are ring-masqueraded outside release, so only the raw bag delta is logged.
            PlaytestLog.Event("Pickup", "native release id=" + id.ToString("X16") + " " + np.Item
                + " gained=" + gained + "/" + np.Count);
            if (np.MagBefore >= 0)
            {
                // Bag full: native MagFill put the ammo straight into the magazine.
                int mag = MagAmmoOf(np.Item) - np.MagBefore;
                if (mag > 0) gained += mag;
            }
            int remainder = np.Count - gained;
            if (remainder <= 0 || np.Item == Items.itemlist.None) return;
            // Key/Object ride the party key ring; they never stack or overflow.
            if (PartyKeyRing.IsKeyOrObject(np.Item)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

            // Native release kept the remainder on the prop (count = remainder + SProgress SetInt, no Destroy;
            // Ghidra ItemPickup.c release) and that SProgress write reaches every peer through story sync.
            // The prop is the only home of the remainder: it is never also spawned on the floor.
            if (net.Role == NetworkRole.Host && !NetGate.Party)
            {
                // Lone host is vanilla: give the claim back so the prop stays, visible and takeable.
                ReleaseClaimIf(id, net.LocalPlayerId);
                PlaytestLog.Verbose("Pickup", "lone host partial " + np.Item + " x" + remainder + " stays on prop");
                return;
            }
            // Party: other peers' live props still show the full count (no count on the wire), so the claim
            // stays for this visit; the prop comes back with the remainder (LoadState) on the next scene load.
            PlaytestLog.Event("Pickup", "partial " + np.Item + " x" + remainder
                + " stays on prop id=" + id.ToString("X16") + " (returns on revisit)");
        }

        /// <summary>
        /// Take back a native bag add after the claim lost (host deny, or claimed while the
        /// yes/no dialogue was open). Safe to call before native release has run.
        /// </summary>
        public void RevertNativeGrant(Items.itemlist item, int count, int bagBefore, ItemPickup prop, int magBefore = -1)
        {
            if (item == Items.itemlist.None) return;
            var r = new PendingRevert
            {
                Item = item,
                Count = count > 0 ? count : 1,
                BagBefore = bagBefore,
                MagBefore = magBefore,
                Deadline = Time.unscaledTime + RevertWindow,
                Prop = prop
            };
            if (TryRevert(ref r)) return;
            _reverts.Add(r);
        }

        /// <summary>Convenience: measure the bag now (callback time, before native release).</summary>
        public void RevertNativeGrantNow(Items.itemlist item, int count, ItemPickup prop)
        {
            int before = DroppedItemManager.CountInBag(item);
            RevertNativeGrant(item, count, before, prop, MagAmmoOf(item));
        }

        bool TryRevert(ref PendingRevert r)
        {
            int have = DroppedItemManager.CountInBag(r.Item);
            int gained = have - r.BagBefore;
            int magGain = 0;
            if (r.MagBefore >= 0)
            {
                // Native MagFill (bag full): the ammo went into the magazine, not the bag.
                int magNow = MagAmmoOf(r.Item);
                if (magNow > r.MagBefore) magGain = magNow - r.MagBefore;
            }
            if (gained <= 0 && magGain <= 0) return false;
            int take = gained < r.Count ? gained : r.Count;
            NetGate.BeginApply();
            try
            {
                var an = InventoryManager.getItem(r.Item);
                // Bag entries may be a different AnItem instance than the catalog one (ring-seeded copies).
                if (take > 0 && an != null) InventoryManager.RemoveItem(PartyKeyRing.FindInBag(an) ?? an, take);
                if (magGain > 0 && an != null)
                {
                    var w = InventoryManager.getWeaponFromAmmo(an);
                    if (w != null)
                    {
                        int undo = magGain < r.Count ? magGain : r.Count;
                        w.magAmmo = System.Math.Max(r.MagBefore, w.magAmmo - undo);
                        take += undo;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] revert native grant: " + ex.Message);
            }
            finally { NetGate.EndApply(); }
            PlaytestLog.Event("Pickup", "reverted native grant " + r.Item + " x" + take + " (claimed by another player)");
            NotifyGone(r.Item, r.Prop);
            return true;
        }

        static void NotifyGone(Items.itemlist item, ItemPickup prop)
        {
            // Native "cannot carry" line is the closest existing message; the item is gone for this peer.
            if (prop == null) return;
            try
            {
                if (prop.gameObject == null) return;
                if (PlayerState.gameState != PlayerState.gameStates.play) return;
                var an = InventoryManager.getItem(item);
                if (an != null) PartyKeyRing.BindUseDialogue(an);
                NetGate.BeginApply();
                try { Dialoguer.StartDialogue((int)prop._cannotDialogue); }
                finally { NetGate.EndApply(); }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] gone message: " + ex.Message);
            }
        }

        /// <summary>Per-frame (both roles): finish deferred reverts once native release has added the item.</summary>
        void TickPendingReverts()
        {
            if (_reverts.Count > 0)
            {
                float now = Time.unscaledTime;
                for (int i = _reverts.Count - 1; i >= 0; i--)
                {
                    var r = _reverts[i];
                    if (TryRevert(ref r) || now > r.Deadline)
                        _reverts.RemoveAt(i);
                }
            }
            if (_nativePending.Count > 0)
            {
                float now = Time.unscaledTime;
                List<ulong> stale = null;
                foreach (var kvp in _nativePending)
                {
                    if (now - kvp.Value.Time > 30f)
                    {
                        if (stale == null) stale = new List<ulong>(2);
                        stale.Add(kvp.Key);
                    }
                }
                if (stale != null)
                    for (int i = 0; i < stale.Count; i++) _nativePending.Remove(stale[i]);
            }
            if (_gainCheck.Count > 0)
            {
                float now = Time.unscaledTime;
                _expiredScratch.Clear();
                foreach (var kvp in _gainCheck)
                    if (now - kvp.Value.Time > 30f) _expiredScratch.Add(kvp.Key);
                for (int i = 0; i < _expiredScratch.Count; i++) _gainCheck.Remove(_expiredScratch[i]);
            }
            if (_releasePending.Count > 0)
            {
                float now = Time.unscaledTime;
                // Paused (timeScale 0) or the pickup dialogue still open: the Invoke("release") has not had a
                // chance to run, so the wait does not run out and a hide never lands under an open dialogue.
                bool waiting = ReleaseStillComing();
                _expiredScratch.Clear();
                _extendScratch.Clear();
                foreach (var kvp in _releasePending)
                {
                    if (now <= kvp.Value) continue;
                    if (waiting) _extendScratch.Add(kvp.Key);
                    else _expiredScratch.Add(kvp.Key);
                }
                for (int i = 0; i < _extendScratch.Count; i++)
                    _releasePending[_extendScratch[i]] = now + ReleaseWaitCallback;
                for (int i = 0; i < _expiredScratch.Count; i++)
                {
                    ulong id = _expiredScratch[i];
                    _releasePending.Remove(id);
                    // Release never ran (prop gone / dialogue aborted): apply the hide we held back.
                    if (_deferredHide.Remove(id))
                    {
                        ItemPickup p;
                        if (_byId.TryGetValue(id, out p) && p != null) HideOnePickup(p);
                    }
                }
            }
        }

        /// <summary>Client: host refused the claim — someone else owns the prop; undo the native add.</summary>
        public void ApplyDeny(WorldPickupDenyMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || msg.TargetPlayerId != net.LocalPlayerId) return;
            ulong id = unchecked((ulong)msg.WorldId);
            EnsureScanned();
            ItemPickup p;
            _byId.TryGetValue(id, out p);
            try { if (p != null && p.gameObject == null) p = null; } catch { p = null; }

            _gainCheck.Remove(id);
            NativePending np;
            if (!_nativePending.TryGetValue(id, out np))
            {
                PlaytestLog.Event("Pickup", "deny id=" + id.ToString("X16") + " (no native add to undo)");
                return;
            }
            _nativePending.Remove(id);
            PlaytestLog.Event("Pickup", "deny id=" + id.ToString("X16") + " undo native " + np.Item);
            RevertNativeGrant(np.Item, np.Count, np.BagBefore, p, np.MagBefore);
        }

        public void RefreshScene()
        {
            _keepItemsScratch.Clear();
            foreach (var item in _claimedItems)
                _keepItemsScratch.Add(item);
            _scanned = false;
            _needFull = true;
            _lastTriggered.Clear();
            _lastActive.Clear();
            _claimed.Clear();
            _claimedItems.Clear();
            _claimerOf.Clear();
            _claimedItemOf.Clear();
            _byId.Clear();
            _uniqueProps.Clear();
            _lastRescanAt = -10f;
            _partyOnPickupFired.Clear();
            // Props of the old scene are gone: their held-back hides / release waits die with them.
            // In-flight claims (_nativePending/_reverts/_gainCheck) keep their own 4-30 s expiry so a
            // grant/deny that lands just after the load still balances the bag.
            _releasePending.Clear();
            _deferredHide.Clear();
            _timer = 0f;
            foreach (var item in _keepItemsScratch)
                _claimedItems.Add(item);
        }

        public void Reset()
        {
            _scanned = false;
            _needFull = true;
            _lastTriggered.Clear();
            _lastActive.Clear();
            _claimed.Clear();
            _claimedItems.Clear();
            _claimerOf.Clear();
            _claimedItemOf.Clear();
            _byId.Clear();
            _uniqueProps.Clear();
            _lastRescanAt = -10f;
            _partyOnPickupFired.Clear();
            ClearPending();
            _timer = 0f;
        }
        void ClearPending()
        {
            _nativePending.Clear();
            _reverts.Clear();
            _releasePending.Clear();
            _deferredHide.Clear();
            _gainCheck.Clear();
        }

        public void RequestFullSend() => _needFull = true;
        public static Items.itemlist ResolveItem(ItemPickup p)
        {
            if (p == null) return Items.itemlist.None;
            try
            {
                if (p._item != null && p._item._item != Items.itemlist.None)
                    return p._item._item;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (p._itemEnum != Items.itemlist.None)
                {
                    if (p._item == null)
                    {
                        try { p._item = InventoryManager.getItem(p._itemEnum); } catch (System.Exception e) { Guard.Swallow(e); }
                    }
                    return p._itemEnum;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return Items.itemlist.None;
        }

        static bool IsLocalHost(int playerId)
        {
            var n = LanNetworkManager.Instance;
            return n != null && n.Role == NetworkRole.Host && playerId == n.LocalPlayerId;
        }

        public bool IsClaimed(ulong worldId) => worldId != 0 && _claimed.Contains(worldId);

        /// <summary>
        /// NG+ Artifact softlock guard: KeyOfSacrifice lives under NGP_only in the
        /// bookstore. Death tarot MeatBlocker ID "Death" seals that wing (wiki).
        /// True while a live unclaimed KeyOfSacrifice world pickup still exists.
        /// </summary>
        public bool KeyOfSacrificeAvailableUnclaimed()
        {
            if (PartyKeyRing.Has(Items.itemlist.KeyOfSacrifice)) return false;
            if (_claimedItems.Contains((ushort)Items.itemlist.KeyOfSacrifice)) return false;
            EnsureScanned();
            foreach (var kvp in _byId)
            {
                var p = kvp.Value;
                if (p == null) continue;
                try
                {
                    if (ResolveItem(p) != Items.itemlist.KeyOfSacrifice) continue;
                    if (p.gameObject != null && p.gameObject.activeInHierarchy)
                        return true;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            return false;
        }

        public bool HoldTarotDeathForSacrifice(Items.itemlist item)
        {
            if (item != Items.itemlist.TarotDeath) return false;
            return KeyOfSacrificeAvailableUnclaimed();
        }


        public bool IsClaimedPickup(ItemPickup p)
        {
            if (p == null) return false;
            if (DroppedItemManager.IsDropped(p)) return false;
            ulong id = 0;
            try { id = WorldId.FromGameObject(p.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            if (id != 0 && _claimed.Contains(id)) return true;
            try
            {
                if (p._item != null && _claimedItems.Contains((ushort)p._item._item))
                    return UniqueWorldItem(p);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        static bool UniqueWorldItem(ItemPickup p)
        {
            if (p == null) return false;
            try
            {
                if (p._item != null)
                {
                    var t = p._item.type;
                    if (t == AnItem.AnItemType.Key || t == AnItem.AnItemType.Object)
                        return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        void NoteClaimedItem(Items.itemlist item)
        {
            if (item == Items.itemlist.None) return;
            if (!PartyKeyRing.IsKeyOrObject(item)) return;
            _claimedItems.Add((ushort)item);
        }

        public void NotifyRevealed()
        {
            HideClaimed(null);
        }

        public void HidePickup(ItemPickup p)
        {
            if (p == null) return;
            if (_releasePending.Count > 0)
            {
                ulong id = IdOf(p);
                if (id != 0 && IsReleasePending(id))
                {
                    // Hiding now would SetActive(false) under the pending Invoke("release").
                    _deferredHide.Add(id);
                    PlaytestLog.Verbose("Pickup", "hide deferred until native release id=" + id.ToString("X16"));
                    return;
                }
            }
            HideOnePickup(p);
        }

        static void HideOnePickup(ItemPickup p)
        {
            if (p == null) return;
            try
            {
                if (p.gameObject == null) return;
            }
            catch { return; }
            try { p.triggered = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var it = p.GetComponent<Interaction>();
                if (it != null)
                {
                    it.triggered = true;
                    it.enabled = false;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (p == null || p.gameObject == null) return;
                if (!p.dontDestroyOnPickup)
                    p.gameObject.SetActive(false);
                else
                    p.enabled = false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>Undo HideOnePickup after the host gave a claim back (declined yes/no, Triggered=false broadcast).</summary>
        static void RestorePickup(ItemPickup p)
        {
            if (p == null) return;
            try
            {
                if (p.gameObject == null) return;
            }
            catch { return; }
            try { p.triggered = false; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.enabled = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var it = p.GetComponent<Interaction>();
                if (it != null)
                {
                    it.triggered = false;
                    it.enabled = true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { p.gameObject.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Re-hide claimed props after a parent chunk/content wakes (SetActive re-enables children).
        /// </summary>
        public void HideClaimed(GameObject root)
        {
            EnsureScanned();
            if (root != null)
            {
                ItemPickup[] picks = null;
                try { picks = root.GetComponentsInChildren<ItemPickup>(true); } catch (System.Exception e) { Guard.Swallow(e); }
                if (picks != null)
                {
                    for (int i = 0; i < picks.Length; i++)
                    {
                        var p = picks[i];
                        if (p == null) continue;
                        if (DroppedItemManager.IsDropped(p)) continue;
                        ulong id = WorldId.FromGameObject(p.gameObject);
                        if (id != 0 && _claimed.Contains(id))
                        {
                            // Dig H: chunk wake HideClaimed used to Hide only — never
                            // EnsurePartyOnPickup. takeSpear / StartOutro / MeatBlocker
                            // onPickup stayed silent if claim/state hide ran before scan.
                            EnsurePartyOnPickup(id, p);
                            HidePickup(p);
                            continue;
                        }
                        if (IsClaimedPickup(p))
                        {
                            if (id != 0) _claimed.Add(id);
                            if (id != 0) EnsurePartyOnPickup(id, p);
                            HidePickup(p);
                        }
                    }
                }
                return;
            }

            // Runs on every state message / door event: only claimed ids and the scan's unique props, ids cached.
            foreach (var id in _claimed)
            {
                ItemPickup p;
                if (!_byId.TryGetValue(id, out p) || p == null) continue;
                if (!AlreadyHidden(p)) HidePickup(p);
            }
            if (_claimedItems.Count == 0) return;
            for (int i = 0; i < _uniqueProps.Count; i++)
            {
                var u = _uniqueProps[i];
                if (u.P == null || _claimed.Contains(u.Id) || !_claimedItems.Contains(u.Item)) continue;
                if (!AlreadyHidden(u.P)) HidePickup(u.P);
            }
        }

        /// <summary>HideOnePickup already ran and nothing re-enabled the prop itself (parent wakes keep activeSelf false).</summary>
        static bool AlreadyHidden(ItemPickup p)
        {
            try
            {
                if (p.gameObject == null) return true;
                return p.dontDestroyOnPickup ? !p.enabled : !p.gameObject.activeSelf;
            }
            catch { return true; }
        }

        private void EnsureScanned()
        {
            if (_scanned) return;
            _byId.Clear();
            _uniqueProps.Clear();
            _lastRescanAt = Time.unscaledTime;
            var all = WorldLookup.All<ItemPickup>();

            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var p = all[i];
                    if (p == null) continue;
                    try { if (p.slave) continue; } catch (System.Exception e) { Guard.Swallow(e); }
                    if (DroppedItemManager.IsDropped(p)) continue;
                    ulong id = WorldId.FromGameObject(p.gameObject);
                    if (id == 0) continue;
                    if (_byId.ContainsKey(id)) continue;
                    _byId[id] = p;
                    if (UniqueWorldItem(p))
                    {
                        try { _uniqueProps.Add(new UniqueProp { Id = id, P = p, Item = (ushort)p._item._item }); }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            _scanned = true;
            ModRuntime.Log?.Msg("[WorldPickup] Scanned " + _byId.Count + " ItemPickup (WorldId)");
        }

        /// <summary>
        /// Prop by WorldId. Ids are cached per instance at scene load, so a miss is a prop instantiated after the
        /// scan (or one in another room/scene): rescan at most once per RescanCooldown, never per entry.
        /// </summary>
        bool TryFind(ulong id, out ItemPickup p)
        {
            EnsureScanned();
            if (_byId.TryGetValue(id, out p) && p != null) return true;
            if (Time.unscaledTime - _lastRescanAt < RescanCooldown) { p = null; return false; }
            _scanned = false;
            EnsureScanned();
            return _byId.TryGetValue(id, out p) && p != null;
        }

        public void TickHost(LanNetworkManager net)
        {
            // Runs for every role (LanNetworkManager.Update): client deny reverts live here too.
            TickPendingReverts();
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            if (Config.ModConfig.SyncWorldPickups?.Value != true) return;

            // Send cadence is wall-clock: slow-mo / timeScale must not stretch the pickup state stream.
            _timer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_timer < Interval && !_needFull) return;
            _timer = 0f;

            float t0 = Time.realtimeSinceStartup;
            try
            {
            EnsureScanned();
            if (_byId.Count == 0) return;

            _tickList.Clear();
            bool full = _needFull;
            _needFull = false;

            foreach (var kvp in _byId)
            {
                var p = kvp.Value;
                ulong id = kvp.Key;
                bool gone = p == null;
                try
                {
                    if (!gone && p.gameObject == null)
                        gone = true;
                }
                catch
                {
                    // Inactive EventObject props can throw; do not treat as claimed.
                    continue;
                }

                bool triggered = gone || _claimed.Contains(id);
                bool active = false;
                if (!gone)
                {
                    try
                    {
                        // Native `triggered` stays true for the whole yes/no dialogue and until release ran.
                        // While this host's own release is pending it is NOT a claim: auto-claiming it here
                        // (without a claimer) would deny the host's own "yes" and strand the prop on a "no".
                        bool nativeTrig = p.triggered && !(_releasePending.Count > 0 && IsReleasePending(id));
                        triggered = nativeTrig || triggered;
                        active = p.gameObject.activeInHierarchy && p.enabled && !triggered;
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (gone || triggered)
                    _claimed.Add(id);

                bool lt, la;
                _lastTriggered.TryGetValue(id, out lt);
                _lastActive.TryGetValue(id, out la);

                if (full || lt != triggered || la != active)
                {
                    _tickList.Add(new WorldPickupEntry
                    {
                        WorldId = unchecked((long)id),
                        Triggered = triggered,
                        Active = active
                    });
                    _lastTriggered[id] = triggered;
                    _lastActive[id] = active;
                }
            }

            if (_tickList.Count == 0) return;
            net.SendWorldPickupState(_tickList, full);
            HideClaimed(null);
            }
            finally
            {
                HitchTrace.Cost("pickup", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        /// <summary>
        /// Host: reserve a pickup for claimer.
        /// hideNow=false when host is about to run native pickUp() itself.
        /// </summary>
        public bool TryClaimOnHost(ulong worldId, int claimerPlayerId, out Items.itemlist itemEnum, out int count,
            bool hideNow = true, Items.itemlist hintItem = Items.itemlist.None, int hintCount = 0)
        {
            itemEnum = hintItem;
            count = hintCount > 0 ? hintCount : 1;
            EnsureScanned();

            if (_claimed.Contains(worldId))
            {
                int who;
                if (_claimerOf.TryGetValue(worldId, out who))
                {
                    if (who == claimerPlayerId)
                    {
                        HideClaimed(null);
                        return true;
                    }
                    return false;
                }
                // Claimed with no claimer on record = the world marked it (native `triggered` seen by TickHost,
                // or the prop is gone). Only the host's own in-flight pickup may take that over.
                if (!IsLocalHost(claimerPlayerId)) return false;
                _claimed.Remove(worldId);
            }

            ItemPickup p;
            TryFind(worldId, out p);

            if (p != null)
            {
                try
                {
                    var resolved = ResolveItem(p);
                    if (resolved != Items.itemlist.None)
                        itemEnum = resolved;
                    count = p.count > 0 ? p.count : count;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            else if (itemEnum == Items.itemlist.None)
            {
                // Other room: still reserve the WorldId so the prop hides when the chunk wakes.
                PlaytestLog.Event("Pickup", "claim without local prop id=" + worldId.ToString("X16")
                    + " hint=" + hintItem);
            }

            if (HoldTarotDeathForSacrifice(itemEnum))
            {
                PlaytestLog.Event("Pickup", "deny TarotDeath — KeyOfSacrifice still available");
                return false;
            }

            _claimed.Add(worldId);
            _claimerOf[worldId] = claimerPlayerId;
            _claimedItemOf[worldId] = itemEnum;
            NoteClaimedItem(itemEnum);

            if (hideNow)
            {
                if (p != null)
                {
                    try
                    {
                        if (p.gameObject == null)
                            p = null;
                    }
                    catch { p = null; }
                }
                if (p != null)
                    HidePickup(p);
                HideClaimed(null);
            }

            return true;
        }

        /// <summary>
        /// Undo a host Prefix reservation when native pickUp refused (nospace / cancel).
        /// Only the same claimer may release — peer claims stay.
        /// </summary>
        public bool ReleaseClaimIf(ulong worldId, int claimerPlayerId)
        {
            if (!_claimed.Contains(worldId)) return false;
            int who;
            if (_claimerOf.TryGetValue(worldId, out who))
            {
                if (who != claimerPlayerId) return false;
            }
            else if (!IsLocalHost(claimerPlayerId))
                return false; // unowned claim: only the host's own pickup may give it back
            Items.itemlist noted = Items.itemlist.None;
            _claimedItemOf.TryGetValue(worldId, out noted);
            _claimed.Remove(worldId);
            _claimerOf.Remove(worldId);
            _claimedItemOf.Remove(worldId);
            _partyOnPickupFired.Remove(worldId);
            _deferredHide.Remove(worldId);
            // Only drop the item-enum mark when no other WorldId still claims that unique.
            if (noted != Items.itemlist.None)
            {
                bool still = false;
                foreach (var kvp in _claimedItemOf)
                {
                    if (kvp.Value == noted) { still = true; break; }
                }
                if (!still)
                    _claimedItems.Remove((ushort)noted);
            }
            PlaytestLog.Event("Pickup", "release claim id=" + worldId.ToString("X16")
                + " by=" + claimerPlayerId + " item=" + noted);
            return true;
        }

        /// <summary>Host declined its own yes/no: free the claim and tell peers to show the prop again.</summary>
        public void ReleaseAndBroadcast(ulong worldId, int claimerPlayerId)
        {
            if (!ReleaseClaimIf(worldId, claimerPlayerId)) return;
            _gainCheck.Remove(worldId);
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            net.SendWorldPickupState(new[]
            {
                new WorldPickupEntry { WorldId = unchecked((long)worldId), Triggered = false, Active = true }
            }, false);
            ItemPickup p;
            if (_byId.TryGetValue(worldId, out p) && p != null) RestorePickup(p);
        }

        public void BroadcastTriggered(ulong worldId, bool triggered)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            _claimed.Add(worldId);
            ItemPickup p;
            if (_byId.TryGetValue(worldId, out p) && p != null)
            {
                try
                {
                    if (p.gameObject == null)
                        p = null;
                }
                catch { p = null; }
            }
            if (p != null)
            {
                try
                {
                    if (p._item != null)
                        NoteClaimedItem(p._item._item);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            // Host does not ApplyHide its own broadcast — party onPickup for host when
            // a client claimed (native pickUp never ran here). Host-native path Notes
            // first so this Ensure is a no-op (Dig H).
            if (triggered)
                EnsurePartyOnPickup(worldId, p);
            net.SendWorldPickupState(new[]
            {
                new WorldPickupEntry
                {
                    WorldId = unchecked((long)worldId),
                    Triggered = triggered,
                    Active = !triggered
                }
            }, false);
        }

        public void ApplyHide(WorldPickupStateMessage msg)
        {
            if (msg.Entries == null) return;
            EnsureScanned();

            for (int i = 0; i < msg.Entries.Length; i++)
            {
                var e = msg.Entries[i];
                ulong id = unchecked((ulong)e.WorldId);
                if (id == 0) continue;

                ItemPickup p;
                TryFind(id, out p);
                if (p != null)
                {
                    try
                    {
                        if (p.gameObject == null)
                            p = null;
                    }
                    catch { p = null; }
                }

                // Host orphan-release / rollback broadcasts Triggered=false — restore prop.
                if (!e.Triggered)
                {
                    Items.itemlist noted = Items.itemlist.None;
                    _claimedItemOf.TryGetValue(id, out noted);
                    // Only undo a hide this peer did. The join dump lists every unclaimed prop as
                    // Triggered=false, and force-activating them revealed props the level keeps off
                    // (PEN_Cryo.contentLateActivated: the cryo key card, live before the pod opened).
                    bool wasHidden = _claimed.Contains(id) || _deferredHide.Contains(id);
                    _claimed.Remove(id);
                    _claimerOf.Remove(id);
                    _claimedItemOf.Remove(id);
                    _partyOnPickupFired.Remove(id);
                    _deferredHide.Remove(id);
                    if (noted != Items.itemlist.None)
                    {
                        bool still = false;
                        foreach (var kvp in _claimedItemOf)
                        {
                            if (kvp.Value == noted) { still = true; break; }
                        }
                        if (!still)
                            _claimedItems.Remove((ushort)noted);
                    }
                    if (p != null && wasHidden)
                    {
                        RestorePickup(p);
                        PlaytestLog.Verbose("Pickup", "unhide id=" + id.ToString("X16"));
                    }
                    continue;
                }

                _claimed.Add(id);
                // Triggered rising (or FullRefresh remount): party onPickup for non-claimers
                // (TakeCard/takeRing/takeSpear/StartOutro/MeatBlocker). Invoke before Hide
                // so persistent GameObject args stay live. HashSet de-dupes Grant+Hide.
                EnsurePartyOnPickup(id, p);
                if (p != null)
                {
                    HidePickup(p);
                    try
                    {
                        if (p._item != null)
                            NoteClaimedItem(p._item._item);
                    }
                    catch (System.Exception ex) { Guard.Swallow(ex); }
                    try
                    {
                        PlaytestLog.Verbose("Pickup", "hide id=" + id.ToString("X16")
                            + " " + (p.gameObject != null ? p.gameObject.name : "?"));
                    }
                    catch
                    {
                        PlaytestLog.Verbose("Pickup", "hide id=" + id.ToString("X16"));
                    }
                }
                else
                    PlaytestLog.Verbose("Pickup", "hide pending id=" + id.ToString("X16"));
            }

            HideClaimed(null);
        }

        /// <summary>Client-side inventory grant after host approved claim.</summary>
        public void ApplyGrant(WorldPickupGrantMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            if (msg.TargetPlayerId != net.LocalPlayerId) return;

            try
            {
                ulong id = unchecked((ulong)msg.WorldId);
                _claimed.Add(id);
                NoteClaimedItem((Items.itemlist)msg.ItemEnum);
                ItemPickup p;
                TryFind(id, out p);
                if (p != null)
                {
                    try
                    {
                        if (p.gameObject == null)
                            p = null;
                    }
                    catch { p = null; }
                }

                NetGate.BeginApply();
                try
                {
                    var kind = (Items.itemlist)msg.ItemEnum;
                    AnItem item = null;
                    if (kind != Items.itemlist.None)
                    {
                        try { item = InventoryManager.getItem(kind); } catch (System.Exception e) { Guard.Swallow(e); }
                    }
                    if ((item == null || kind == Items.itemlist.None) && p != null)
                    {
                        kind = ResolveItem(p);
                        if (kind != Items.itemlist.None)
                        {
                            try { item = InventoryManager.getItem(kind); } catch (System.Exception e) { Guard.Swallow(e); }
                        }
                        if (item == null)
                        {
                            try { item = p._item; } catch (System.Exception e) { Guard.Swallow(e); }
                        }
                    }
                    if (item == null || kind == Items.itemlist.None)
                    {
                        ModRuntime.Log?.Warning("[WorldPickup] Grant unknown item " + msg.ItemEnum);
                        return;
                    }
                    // Inspect-path claims: native pickUp/release already added (or is about to add)
                    // the item — AddItem here would double it. Every other claim (client Prefix blocked
                    // native pickUp) must AddItem even if the bag already holds a stack of it, otherwise
                    // ammo/health/batteries are hidden for all peers and granted to nobody.
                    bool nativeAdds = _nativePending.Remove(id);
                    int grantCount = msg.Count > 0 ? msg.Count : 1;
                    if (!nativeAdds)
                    {
                        // Non-inspect props only (the BOS_Adler spears, count 1; the claim Prefix checked bag room).
                        int before = DroppedItemManager.CountInBag(kind);
                        InventoryManager.AddItem(item, grantCount);
                        // AddItem silently caps at maxNumber: measure what the bag really took.
                        int gained = DroppedItemManager.CountInBag(kind) - before;
                        if (gained < grantCount)
                            PlaytestLog.Warn("Pickup", "grant " + kind + " took " + (gained > 0 ? gained : 0)
                                + "/" + grantCount + " (stack cap)");
                    }
                    else
                    {
                        // Native release owns the add (and its onPickup Invoke): a partial gain stays on the
                        // prop natively (CheckGain), and party onPickup must not fire a second time.
                        NoteOnPickupFired(id);
                    }
                    PartyKeyRing.Note(item);
                    PartyKeyRing.BindUseDialogue(item);

                    // Client Prefix blocks ItemPickup.pickUp, so onPickup never ran.
                    // Host-claim comment was false when client claims — host+non-claimers
                    // now Ensure via ApplyHide/BroadcastTriggered. Claimer uses same
                    // Ensure (HashSet de-dupes if State arrived first). Dig H / 0.5.24.
                    EnsurePartyOnPickup(id, p);
                }
                finally
                {
                    NetGate.EndApply();
                }

                if (p != null)
                {
                    // Deferred while this peer's native release is still pending (inspect claims).
                    HidePickup(p);
                    try { if (p._item != null) PartyKeyRing.Note(p._item); } catch (System.Exception e) { Guard.Swallow(e); }
                }

                ModRuntime.Log?.Msg("[WorldPickup] Granted " + (Items.itemlist)msg.ItemEnum + " x" + msg.Count);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] Grant failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Host-native pickUp already Invoked onPickup — mark so BroadcastTriggered /
        /// ApplyHide Ensure do not double-fire (Dig H).
        /// </summary>
        public void NoteOnPickupFired(ulong worldId)
        {
            if (worldId == 0) return;
            _partyOnPickupFired.Add(worldId);
        }

        /// <summary>
        /// Idempotent party onPickup: once per WorldId until scene refresh / claim release.
        /// Skips when prop not yet scanned (pending other-room) so a later ApplyHide can fire.
        /// </summary>
        public bool EnsurePartyOnPickup(ulong worldId, ItemPickup p)
        {
            if (worldId == 0 || p == null) return false;
            if (_partyOnPickupFired.Contains(worldId)) return false;
            bool ok = false;
            NetGate.BeginApply();
            try
            {
                InvokeOnPickup(p);
                ok = true;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] onPickup Ensure: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }
            // Mark only after a successful Invoke so a multi-call UnityEvent
            // (StartOutro+UnJam) can retry if the first attempt threw (Dig AJ).
            if (ok)
                _partyOnPickupFired.Add(worldId);
            return ok;
        }

        /// <summary>
        /// Fire story/world listeners without re-running pickUp (which would AddItem again).
        /// </summary>
        static void InvokeOnPickup(ItemPickup p)
        {
            if (p == null) return;
            try
            {
                var ev = p.onPickup;
                if (ev != null)
                    ev.Invoke();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] onPickup Invoke: " + ex.Message);
            }
        }
    }
}
