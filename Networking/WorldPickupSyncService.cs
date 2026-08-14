// Host-authoritative world ItemPickup: claim → grant claimer, hide for all peers.
using System.Collections.Generic;
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
        private readonly Dictionary<ulong, ItemPickup> _byId = new Dictionary<ulong, ItemPickup>();
        private bool _scanned;

        public void RefreshScene()
        {
            _scanned = false;
            _needFull = true;
            _lastTriggered.Clear();
            _lastActive.Clear();
            _claimed.Clear();
            _byId.Clear();
            _timer = 0f;
        }

        public void Reset() => RefreshScene();
        public void RequestFullSend() => _needFull = true;

        private void EnsureScanned()
        {
            if (_scanned) return;
            _byId.Clear();
            ItemPickup[] all = null;
            try { all = Object.FindObjectsOfType<ItemPickup>(true); }
            catch
            {
                try { all = Object.FindObjectsOfType<ItemPickup>(); } catch { }
            }

            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var p = all[i];
                    if (p == null) continue;
                    try { if (p.slave) continue; } catch { }
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
                if (p == null) continue;
                ulong id = kvp.Key;

                bool triggered = false;
                bool active = false;
                try
                {
                    triggered = p.triggered || _claimed.Contains(id);
                    active = p.gameObject.activeInHierarchy && p.enabled && !triggered;
                }
                catch { continue; }

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
        }

        /// <summary>
        /// Host: reserve a pickup for claimer.
        /// hideNow=false when host is about to run native pickUp() itself.
        /// </summary>
        public bool TryClaimOnHost(ulong worldId, int claimerPlayerId, out Items.itemlist itemEnum, out int count, bool hideNow = true)
        {
            itemEnum = Items.itemlist.None;
            count = 0;
            EnsureScanned();

            if (_claimed.Contains(worldId))
                return false;

            ItemPickup p;
            if (!_byId.TryGetValue(worldId, out p) || p == null)
            {
                _scanned = false;
                EnsureScanned();
                if (!_byId.TryGetValue(worldId, out p) || p == null)
                    return false;
            }

            try
            {
                if (p.triggered) return false;
            }
            catch { }

            try
            {
                if (p._item != null)
                    itemEnum = p._item._item;
                count = p.count > 0 ? p.count : 1;
            }
            catch
            {
                count = 1;
            }

            _claimed.Add(worldId);

            if (hideNow)
            {
                try
                {
                    p.triggered = true;
                    if (!p.dontDestroyOnPickup)
                        p.gameObject.SetActive(false);
                    else
                        p.enabled = false;
                }
                catch { }
            }

            return true;
        }

        public void BroadcastTriggered(ulong worldId, bool triggered)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            _claimed.Add(worldId);
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
                ItemPickup p;
                if (!_byId.TryGetValue(id, out p) || p == null) continue;

                // Inactive room chunks are not picked up. Only Triggered hides the prop.
                if (!e.Triggered) continue;

                _claimed.Add(id);
                try
                {
                    p.triggered = true;
                    if (!p.dontDestroyOnPickup)
                        p.gameObject.SetActive(false);
                    else
                        p.enabled = false;
                    PlaytestLog.Event("Pickup", "hide id=" + id.ToString("X16") + " " + p.gameObject.name);
                }
                catch { }
            }
        }

        /// <summary>Client-side inventory grant after host approved claim.</summary>
        public void ApplyGrant(WorldPickupGrantMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            if (msg.TargetPlayerId != net.LocalPlayerId) return;

            try
            {
                var item = InventoryManager.getItem((Items.itemlist)msg.ItemEnum);
                if (item == null)
                {
                    ModRuntime.Log?.Warning("[WorldPickup] Grant unknown item " + msg.ItemEnum);
                    return;
                }
                InventoryManager.AddItem(item, msg.Count > 0 ? msg.Count : 1);
                PartyKeyRing.Note(item);
                ModRuntime.Log?.Msg("[WorldPickup] Granted " + (Items.itemlist)msg.ItemEnum + " x" + msg.Count);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] Grant failed: " + ex.Message);
            }

            // Hide local prop
            ulong id = unchecked((ulong)msg.WorldId);
            _claimed.Add(id);
            ItemPickup p;
            if (_byId.TryGetValue(id, out p) && p != null)
            {
                try
                {
                    p.triggered = true;
                    if (!p.dontDestroyOnPickup) p.gameObject.SetActive(false);
                }
                catch { }
            }
        }
    }
}
