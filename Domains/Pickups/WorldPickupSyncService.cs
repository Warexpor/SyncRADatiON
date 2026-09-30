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
            public float Time;
        }

        private struct PendingRevert
        {
            public Items.itemlist Item;
            public int Count;
            public int BagBefore;
            public float Deadline;
            public ItemPickup Prop;
        }

        private readonly Dictionary<ulong, NativePending> _nativePending = new Dictionary<ulong, NativePending>();
        private readonly List<PendingRevert> _reverts = new List<PendingRevert>(2);
        private const float RevertWindow = 4f;

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
                Time = Time.unscaledTime
            };
        }

        /// <summary>
        /// Take back a native bag add after the claim lost (host deny, or claimed while the
        /// yes/no dialogue was open). Safe to call before native release has run.
        /// </summary>
        public void RevertNativeGrant(Items.itemlist item, int count, int bagBefore, ItemPickup prop)
        {
            if (item == Items.itemlist.None) return;
            var r = new PendingRevert
            {
                Item = item,
                Count = count > 0 ? count : 1,
                BagBefore = bagBefore,
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
            RevertNativeGrant(item, count, before, prop);
        }

        bool TryRevert(ref PendingRevert r)
        {
            int have = DroppedItemManager.CountInBag(r.Item);
            int gained = have - r.BagBefore;
            if (gained <= 0) return false;
            int take = gained < r.Count ? gained : r.Count;
            NetGate.BeginApply();
            try
            {
                var an = InventoryManager.getItem(r.Item);
                if (an != null) InventoryManager.RemoveItem(an, take);
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

            NativePending np;
            if (!_nativePending.TryGetValue(id, out np))
            {
                PlaytestLog.Event("Pickup", "deny id=" + id.ToString("X16") + " (no native add to undo)");
                return;
            }
            _nativePending.Remove(id);
            PlaytestLog.Event("Pickup", "deny id=" + id.ToString("X16") + " undo native " + np.Item);
            RevertNativeGrant(np.Item, np.Count, np.BagBefore, p);
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
            _partyOnPickupFired.Clear();
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
            _partyOnPickupFired.Clear();
            _timer = 0f;
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

        /// <summary>Undo HideOnePickup after an orphan claim release (peer gone mid-grant).</summary>
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

            foreach (var kvp in _byId)
            {
                if (kvp.Value == null) continue;
                if (_claimed.Contains(kvp.Key) || IsClaimedPickup(kvp.Value))
                    HidePickup(kvp.Value);
            }
        }

        private void EnsureScanned()
        {
            if (_scanned) return;
            _byId.Clear();
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
                    if (!_byId.ContainsKey(id))
                        _byId[id] = p;
                }
            }
            _scanned = true;
            ModRuntime.Log?.Msg("[WorldPickup] Scanned " + _byId.Count + " ItemPickup (WorldId)");
        }

        public void TickHost(LanNetworkManager net)
        {
            // Runs for every role (LanNetworkManager.Update): client deny reverts live here too.
            TickPendingReverts();
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            if (Config.ModConfig.SyncWorldPickups?.Value != true) return;

            _timer += Mathf.Min(Time.deltaTime, 0.1f);
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
                        triggered = p.triggered || triggered;
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
                if (_claimerOf.TryGetValue(worldId, out who) && who == claimerPlayerId)
                {
                    HideClaimed(null);
                    return true;
                }
                return false;
            }

            ItemPickup p;
            if (!_byId.TryGetValue(worldId, out p) || p == null)
            {
                _scanned = false;
                EnsureScanned();
                _byId.TryGetValue(worldId, out p);
            }

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
            if (!_claimerOf.TryGetValue(worldId, out who) || who != claimerPlayerId)
                return false;
            Items.itemlist noted = Items.itemlist.None;
            _claimedItemOf.TryGetValue(worldId, out noted);
            _claimed.Remove(worldId);
            _claimerOf.Remove(worldId);
            _claimedItemOf.Remove(worldId);
            _partyOnPickupFired.Remove(worldId);
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

        /// <summary>
        /// Host: peer disconnect mid WorldPickupClaim/grant. Key/Object already Noted onto
        /// the party ring — keep claimed+hidden. Ammo/docs/etc. never reached the claimer's
        /// bag if grant could not deliver → release + restore prop + broadcast untriggered.
        /// </summary>
        public int ReleaseOrphanClaimsForPlayer(int claimerPlayerId)
        {
            if (claimerPlayerId < 1) return 0;
            EnsureScanned();
            var release = new System.Collections.Generic.List<ulong>(4);
            foreach (var kvp in _claimerOf)
            {
                if (kvp.Value != claimerPlayerId) continue;
                Items.itemlist noted = Items.itemlist.None;
                _claimedItemOf.TryGetValue(kvp.Key, out noted);
                // Party-ring uniques stay claimed (ring already has them).
                if (noted != Items.itemlist.None && PartyKeyRing.IsKeyOrObject(noted))
                    continue;
                release.Add(kvp.Key);
            }
            if (release.Count == 0) return 0;

            var net = LanNetworkManager.Instance;
            int n = 0;
            for (int i = 0; i < release.Count; i++)
            {
                ulong id = release[i];
                if (!ReleaseClaimIf(id, claimerPlayerId)) continue;
                n++;
                ItemPickup p;
                if (_byId.TryGetValue(id, out p) && p != null)
                    RestorePickup(p);
                if (net != null)
                {
                    net.SendWorldPickupState(new[]
                    {
                        new WorldPickupEntry
                        {
                            WorldId = unchecked((long)id),
                            Triggered = false,
                            Active = true
                        }
                    }, false);
                }
                PlaytestLog.Event("Pickup", "orphan release id=" + id.ToString("X16")
                    + " peer=" + claimerPlayerId);
            }
            return n;
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
            _scanned = false;
            EnsureScanned();

            for (int i = 0; i < msg.Entries.Length; i++)
            {
                var e = msg.Entries[i];
                ulong id = unchecked((ulong)e.WorldId);
                if (id == 0) continue;

                ItemPickup p;
                if (!_byId.TryGetValue(id, out p) || p == null)
                {
                    _scanned = false;
                    EnsureScanned();
                    _byId.TryGetValue(id, out p);
                }
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
                    _claimed.Remove(id);
                    _claimerOf.Remove(id);
                    _claimedItemOf.Remove(id);
                    _partyOnPickupFired.Remove(id);
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
                    if (p != null)
                        RestorePickup(p);
                    PlaytestLog.Verbose("Pickup", "unhide id=" + id.ToString("X16"));
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
                EnsureScanned();
                ItemPickup p;
                _byId.TryGetValue(id, out p);
                if (p == null)
                {
                    _scanned = false;
                    EnsureScanned();
                    _byId.TryGetValue(id, out p);
                }
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
                    if (!nativeAdds)
                        InventoryManager.AddItem(item, msg.Count > 0 ? msg.Count : 1);
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
