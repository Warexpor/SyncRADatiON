// Host-authoritative InventoryManager.boxItems: the whole box goes out as one blob whenever its signature changes.
using System.Collections.Generic;
using SyncRADation.ItemSystem;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    public sealed class StorageBoxSyncService
    {
        private float _timer;
        private bool _needSend = true;
        private string _lastSig = "";
        private readonly List<ItemBag.Stack> _boxScratch = new List<ItemBag.Stack>(16);
        private readonly List<StorageBoxItem> _readScratch = new List<StorageBoxItem>(16);
        private StorageBoxItem[] _itemsScratch = System.Array.Empty<StorageBoxItem>();

        public void RequestSend() => _needSend = true;
        public void Reset()
        {
            _needSend = true;
            _lastSig = "";
            _timer = 0f;
        }

        public void TickHost(LanNetworkManager net)
        {
            if (net == null || !NetGate.Host) return;
            _timer += UnityEngine.Mathf.Min(UnityEngine.Time.unscaledDeltaTime, 0.1f);
            if (_timer < 0.5f && !_needSend) return;
            _timer = 0f;
            _needSend = false;
            SendNow(net);
        }

        /// <summary>
        /// Host: push a box change to every peer now instead of on the next tick. Also the pre-unicast flush: a
        /// change still waiting for its broadcast must reach everyone before a join dump records it as sent.
        /// </summary>
        public void FlushDiffNow()
        {
            var net = LanNetworkManager.Instance;
            if (!NetGate.Host) return;
            RequestSend();
            SendNow(net);
        }

        public void SendNow(LanNetworkManager net)
        {
            ClampUniqueKeyStacks();
            var items = ReadBox();
            string sig = Signature(items);
            // A unicast dump goes to one peer: always send, never record it as the box everyone has.
            if (!net.UnicastActive)
            {
                if (sig == _lastSig) return;
                _lastSig = sig;
            }
            PlaytestLog.Event("StorageBox", "send items=" + items.Length + (net.UnicastActive ? " (unicast)" : ""));
            net.InventoryHandlers.SendStorageBoxBlob(items);
        }

        /// <summary>
        /// Key/Object must stay count 1 in the shared box. A put-race or EnsureInBag
        /// re-seed can inflate the dict; clamp before LWW blob so peers never see stacks.
        /// </summary>
        public void ClampUniqueKeyStacks()
        {
            if (!NetGate.HostRole) return;
            var box = ItemBag.Box(_boxScratch);
            for (int i = 0; i < box.Count; i++)
            {
                var item = box[i].Item;
                if (item == null || box[i].Count <= 1 || !PartyKeyRing.IsKeyOrObject(item)) continue;
                try
                {
                    // Re-write as single via clear-and-box under apply gate.
                    int have = ItemBag.BoxStock(item);
                    if (have <= 1) continue;
                    NetGate.BeginApply();
                    try
                    {
                        for (int u = 0; u < have; u++)
                            InventoryManager.unboxItem(item);
                        InventoryManager.boxItem(item, 1);
                    }
                    finally { NetGate.EndApply(); }
                    PlaytestLog.Event("StorageBox", "clamp unique " + item._item + " was=" + have);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
        }

        public void Apply(StorageBoxBlobMessage msg)
        {
            if (NetGate.HostRole) return;
            if (msg.Items == null) return;

            var pending = new List<KeyValuePair<AnItem, int>>();
            try
            {
                for (int i = 0; i < msg.Items.Length; i++)
                {
                    var e = msg.Items[i];
                    var item = InventoryManager.getItem((Items.itemlist)e.ItemEnum);
                    if (item == null || e.Count <= 0) continue;
                    pending.Add(new KeyValuePair<AnItem, int>(item, e.Count));
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[StorageBox] Apply build: " + ex.Message);
                return;
            }

            NetGate.BeginApply();
            try
            {
                var dict = InventoryManager.boxItems;
                if (dict != null) dict.Clear();
                PlaytestLog.Event("StorageBox", "apply items=" + pending.Count);
                for (int i = 0; i < pending.Count; i++)
                {
                    var pair = pending[i];
                    try { InventoryManager.boxItem(pair.Key, pair.Value); }
                    catch
                    {
                        try { InventoryManager.storeItem(pair.Key, pair.Value); } catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[StorageBox] Apply: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }
        }

        private StorageBoxItem[] ReadBox()
        {
            _readScratch.Clear();
            var box = ItemBag.Box(_boxScratch);
            for (int i = 0; i < box.Count; i++)
            {
                var item = box[i].Item;
                if (item == null || box[i].Count <= 0) continue;
                try { _readScratch.Add(new StorageBoxItem { ItemEnum = (ushort)item._item, Count = box[i].Count }); }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            int n = _readScratch.Count;
            if (_itemsScratch.Length != n)
                _itemsScratch = n == 0 ? System.Array.Empty<StorageBoxItem>() : new StorageBoxItem[n];
            for (int i = 0; i < n; i++)
                _itemsScratch[i] = _readScratch[i];
            return _itemsScratch;
        }

        private static string Signature(StorageBoxItem[] items)
        {
            if (items == null || items.Length == 0) return "";
            var sb = new System.Text.StringBuilder(items.Length * 8);
            for (int i = 0; i < items.Length; i++)
            {
                sb.Append(items[i].ItemEnum);
                sb.Append(':');
                sb.Append(items[i].Count);
                sb.Append(',');
            }
            return sb.ToString();
        }
    }
}
