// Dropped-item props in the 3D top-down world (no custom IL2CPP MonoBehaviour).
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.ItemSystem
{
    public static class WorldItem
    {
        public static int NearbyID = -1;
    }

    public static class DroppedItemManager
    {
        private struct Drop
        {
            public GameObject Go;
            public Items.itemlist Item;
            public int Count;
            public int Key;
        }

        private static readonly Dictionary<int, Drop> _worldItems = new Dictionary<int, Drop>();

        private static string SavePath => System.IO.Path.Combine(Application.persistentDataPath, "SyncRADation_drops.txt");

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
                WorldItem.NearbyID = -1;
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
            WorldItem.NearbyID = nearest;
        }

        public static void DespawnItem(int netID)
        {
            Drop drop;
            if (_worldItems.TryGetValue(netID, out drop))
            {
                if (drop.Go != null) Object.Destroy(drop.Go);
                _worldItems.Remove(netID);
            }
            if (WorldItem.NearbyID == netID)
                WorldItem.NearbyID = -1;
        }

        public static void ClearAll()
        {
            SaveToFile();
            foreach (var drop in _worldItems.Values)
            {
                if (drop.Go != null) Object.Destroy(drop.Go);
            }
            _worldItems.Clear();
            WorldItem.NearbyID = -1;
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

        public static void SaveToFile()
        {
            try
            {
                var lines = new List<string>();
                foreach (var kvp in _worldItems)
                {
                    if (kvp.Value.Go == null) continue;
                    var pos = kvp.Value.Go.transform.position;
                    lines.Add(string.Format("{0}|{1}|{2}|{3:F2}|{4:F2}|{5:F2}",
                        kvp.Key, (int)kvp.Value.Item, kvp.Value.Count,
                        pos.x, pos.y, pos.z));
                }
                System.IO.File.WriteAllLines(SavePath, lines);
                ModRuntime.Log?.Msg("[Drops] Saved " + lines.Count + " items to " + SavePath);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Drops] Save failed: " + ex.Message);
            }
        }

        public static void LoadFromFile()
        {
            try
            {
                if (!System.IO.File.Exists(SavePath)) return;
                var lines = System.IO.File.ReadAllLines(SavePath);
                int restored = 0;
                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    if (parts.Length < 6) continue;
                    if (!int.TryParse(parts[0], out int netID)) continue;
                    if (!int.TryParse(parts[1], out int itemVal)) continue;
                    if (!int.TryParse(parts[2], out int count)) continue;
                    if (!float.TryParse(parts[3], out float px)) continue;
                    if (!float.TryParse(parts[4], out float py)) continue;
                    if (!float.TryParse(parts[5], out float pz)) continue;
                    if (_worldItems.ContainsKey(netID)) continue;
                    SpawnLocalItem((Items.itemlist)itemVal, count, netID, new Vector3(px, py, pz));
                    restored++;
                }
                ModRuntime.Log?.Msg("[Drops] Restored " + restored + " items from save");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Drops] Load failed: " + ex.Message);
            }
        }

        public static void DeleteSave()
        {
            try
            {
                if (System.IO.File.Exists(SavePath))
                    System.IO.File.Delete(SavePath);
            }
            catch { }
        }
    }
}
