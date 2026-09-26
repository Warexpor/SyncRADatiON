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
        private bool _scanned;
        private readonly List<WorldPickupEntry> _tickList = new List<WorldPickupEntry>(32);

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
            catch { }
            try
            {
                if (p._itemEnum != Items.itemlist.None)
                {
                    if (p._item == null)
                    {
                        try { p._item = InventoryManager.getItem(p._itemEnum); } catch { }
                    }
                    return p._itemEnum;
                }
            }
            catch { }
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
                catch { }
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
            try { id = WorldId.FromGameObject(p.gameObject); } catch { }
            if (id != 0 && _claimed.Contains(id)) return true;
            try
            {
                if (p._item != null && _claimedItems.Contains((ushort)p._item._item))
                    return UniqueWorldItem(p);
            }
            catch { }
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
            catch { }
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
            try { p.triggered = true; } catch { }
            try
            {
                var it = p.GetComponent<Interaction>();
                if (it != null)
                {
                    it.triggered = true;
                    it.enabled = false;
                }
            }
            catch { }
            try
            {
                if (p == null || p.gameObject == null) return;
                if (!p.dontDestroyOnPickup)
                    p.gameObject.SetActive(false);
                else
                    p.enabled = false;
            }
            catch { }
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
            try { p.triggered = false; } catch { }
            try { p.enabled = true; } catch { }
            try
            {
                var it = p.GetComponent<Interaction>();
                if (it != null)
                {
                    it.triggered = false;
                    it.enabled = true;
                }
            }
            catch { }
            try { p.gameObject.SetActive(true); } catch { }
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
                try { picks = root.GetComponentsInChildren<ItemPickup>(true); } catch { }
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
                            HidePickup(p);
                            continue;
                        }
                        if (IsClaimedPickup(p))
                        {
                            if (id != 0) _claimed.Add(id);
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
                    try { if (p.slave) continue; } catch { }
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
                catch { }
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
                catch { }
            }
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
                if (p != null)
                {
                    HidePickup(p);
                    try
                    {
                        if (p._item != null)
                            NoteClaimedItem(p._item._item);
                    }
                    catch { }
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
                        try { item = InventoryManager.getItem(kind); } catch { }
                    }
                    if ((item == null || kind == Items.itemlist.None) && p != null)
                    {
                        kind = ResolveItem(p);
                        if (kind != Items.itemlist.None)
                        {
                            try { item = InventoryManager.getItem(kind); } catch { }
                        }
                        if (item == null)
                        {
                            try { item = p._item; } catch { }
                        }
                    }
                    if (item == null || kind == Items.itemlist.None)
                    {
                        ModRuntime.Log?.Warning("[WorldPickup] Grant unknown item " + msg.ItemEnum);
                        return;
                    }
                    // hasItem includes the party key ring — that skipped bag AddItem so
                    // keys worked on doors but never appeared in the 6-slot UI.
                    if (!PartyKeyRing.InLocalBag(item))
                        InventoryManager.AddItem(item, msg.Count > 0 ? msg.Count : 1);
                    PartyKeyRing.Note(item);
                    PartyKeyRing.BindUseDialogue(item);

                    // Client Prefix blocks ItemPickup.pickUp, so onPickup never ran.
                    // Host already Invoked via native pickUp — only needed on grant path.
                    // Decompile: ItemPickup.onPickup UnityEvent (dump.cs ~484299).
                    InvokeOnPickup(p);
                }
                finally
                {
                    NetGate.EndApply();
                }

                if (p != null)
                {
                    HidePickup(p);
                    try { if (p._item != null) PartyKeyRing.Note(p._item); } catch { }
                }

                ModRuntime.Log?.Msg("[WorldPickup] Granted " + (Items.itemlist)msg.ItemEnum + " x" + msg.Count);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] Grant failed: " + ex.Message);
            }
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
