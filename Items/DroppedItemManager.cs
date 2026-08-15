// Dropped-item props in the 3D top-down world (no custom IL2CPP MonoBehaviour).
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.ItemSystem
{
    public static class DroppedItemManager
    {
        public static int NearbyID = -1;

        private struct Drop
        {
            public GameObject Go;
            public Items.itemlist Item;
            public int Count;
            public int Key;
        }

        private static readonly Dictionary<int, Drop> _worldItems = new Dictionary<int, Drop>();

        public static GameObject SpawnLocalItem(Items.itemlist item, int count, int netID, Vector3 pos)
        {
            var go = new GameObject("DroppedItem_" + netID);
            go.transform.position = pos;
            try { go.tag = "Item"; } catch { }

            try
            {
                var itemData = InventoryManager.getItem(item);
                if (itemData != null && itemData.worldSprite != null)
                {
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = itemData.worldSprite;
                    sr.sortingOrder = 20;
                }
            }
            catch { }

            Object.DontDestroyOnLoad(go);
            _worldItems[netID] = new Drop { Go = go, Item = item, Count = count, Key = netID };
            return go;
        }

        public static void TickNearby()
        {
            var player = PlayerState.player;
            if (player == null)
            {
                NearbyID = -1;
                return;
            }
            Vector3 p = player.transform.position;
            int nearest = -1;
            float best = 1.4f;
            foreach (var kvp in _worldItems)
            {
                var go = kvp.Value.Go;
                if (go == null) continue;
                float dist = Vector3.Distance(p, go.transform.position);
                if (dist < best)
                {
                    best = dist;
                    nearest = kvp.Key;
                }
            }
            NearbyID = nearest;
        }

        public static void DespawnItem(int netID)
        {
            Drop drop;
            if (_worldItems.TryGetValue(netID, out drop))
            {
                if (drop.Go != null) Object.Destroy(drop.Go);
                _worldItems.Remove(netID);
            }
            if (NearbyID == netID)
                NearbyID = -1;
        }

        public static void ClearAll()
        {
            foreach (var drop in _worldItems.Values)
            {
                if (drop.Go != null) Object.Destroy(drop.Go);
            }
            _worldItems.Clear();
            NearbyID = -1;
        }

        public static GameObject GetItem(int netID)
        {
            Drop drop;
            if (_worldItems.TryGetValue(netID, out drop))
                return drop.Go;
            return null;
        }

        public static bool TryGet(int netID, out Items.itemlist item, out int count)
        {
            Drop drop;
            if (_worldItems.TryGetValue(netID, out drop) && drop.Go != null)
            {
                item = drop.Item;
                count = drop.Count;
                return true;
            }
            item = Items.itemlist.None;
            count = 0;
            return false;
        }
    }
}
