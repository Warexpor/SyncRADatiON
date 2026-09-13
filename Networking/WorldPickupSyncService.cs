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
        private readonly Dictionary<ulong, int> _claimerOf = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, ItemPickup> _byId = new Dictionary<ulong, ItemPickup>();
        private bool _scanned;

        public void RefreshScene()
        {
            var keepItems = new HashSet<ushort>(_claimedItems);
            _scanned = false;
            _needFull = true;
            _lastTriggered.Clear();
            _lastActive.Clear();
            _claimed.Clear();
            _claimedItems.Clear();
            _claimerOf.Clear();
            _byId.Clear();
            _timer = 0f;
            foreach (var item in keepItems)
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
                if (!p.dontDestroyOnPickup)
                    p.gameObject.SetActive(false);
                else
                    p.enabled = false;
            }
            catch { }
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

            EnsureScanned();
            if (_byId.Count == 0) return;

            var list = new List<WorldPickupEntry>(32);
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
                    list.Add(new WorldPickupEntry
                    {
                        WorldId = unchecked((long)id),
                        Triggered = triggered,
                        Active = active
                    });
                    _lastTriggered[id] = triggered;
                    _lastActive[id] = active;
                }
            }

            if (list.Count == 0) return;
            net.SendWorldPickupState(list.ToArray(), full);
            HideClaimed(null);
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

            _claimed.Add(worldId);
            _claimerOf[worldId] = claimerPlayerId;
            NoteClaimedItem(itemEnum);

            if (hideNow)
            {
                if (p != null)
                    HidePickup(p);
                HideClaimed(null);
            }

            return true;
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
                if (!e.Triggered) continue;

                ulong id = unchecked((ulong)e.WorldId);
                _claimed.Add(id);
                ItemPickup p;
                if (!_byId.TryGetValue(id, out p) || p == null)
                {
                    _scanned = false;
                    EnsureScanned();
                    _byId.TryGetValue(id, out p);
                }
                if (p != null)
                {
                    HidePickup(p);
                    try
                    {
                        if (p._item != null)
                            NoteClaimedItem(p._item._item);
                    }
                    catch { }
                    PlaytestLog.Verbose("Pickup", "hide id=" + id.ToString("X16") + " " + p.gameObject.name);
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
    }
}
