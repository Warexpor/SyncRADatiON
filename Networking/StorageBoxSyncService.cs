// Host-authoritative InventoryManager.boxItems.
using System.Collections.Generic;

namespace SyncRADation.Networking
{
    public sealed class StorageBoxSyncService
    {
        private float _timer;
        private bool _needSend = true;
        private string _lastSig = "";

        public void RequestSend() => _needSend = true;
        public void Reset()
        {
            _needSend = true;
            _lastSig = "";
            _timer = 0f;
        }

        public void TickHost(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            _timer += UnityEngine.Mathf.Min(UnityEngine.Time.deltaTime, 0.1f);
            if (_timer < 0.5f && !_needSend) return;
            _timer = 0f;
            _needSend = false;
            SendNow(net);
        }

        public void SendNow(LanNetworkManager net)
        {
            var items = ReadBox();
            string sig = Signature(items);
            if (sig == _lastSig) return;
            _lastSig = sig;
            net.SendStorageBoxBlob(items);
        }

        public void Apply(StorageBoxBlobMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;

            Sync.NetGate.BeginApply();
            try
            {
                var dict = InventoryManager.boxItems;
                if (dict != null)
                {
                    try { dict.Clear(); } catch { }
                }

                if (msg.Items == null) return;
                for (int i = 0; i < msg.Items.Length; i++)
                {
                    var e = msg.Items[i];
                    var item = InventoryManager.getItem((Items.itemlist)e.ItemEnum);
                    if (item == null || e.Count <= 0) continue;
                    try { InventoryManager.boxItem(item, e.Count); }
                    catch
                    {
                        try { InventoryManager.storeItem(item, e.Count); } catch { }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[StorageBox] Apply: " + ex.Message);
            }
            finally
            {
                Sync.NetGate.EndApply();
            }
        }

        private static StorageBoxItem[] ReadBox()
        {
            var list = new List<StorageBoxItem>(16);
            try
            {
                var dict = InventoryManager.boxItems;
                if (dict == null) return list.ToArray();
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                {
                    var kvp = en.Current;
                    var item = kvp.key;
                    int count = kvp.value;
                    if (item == null || count <= 0) continue;
                    list.Add(new StorageBoxItem
                    {
                        ItemEnum = (ushort)item._item,
                        Count = count
                    });
                }
                en.Dispose();
            }
            catch { }
            return list.ToArray();
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
