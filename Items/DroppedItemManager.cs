using System.Collections.Generic;
using System.IO;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.ItemSystem
{
    public static class DroppedItemManager
    {
        private static readonly Dictionary<int, GameObject> _worldItems = new Dictionary<int, GameObject>();

        private static string SavePath => Path.Combine(Application.persistentDataPath, "SyncRADation_drops.txt");

        public static GameObject SpawnLocalItem(Items.itemlist item, int count, int netID, Vector3 pos)
        {
            var go = new GameObject("DroppedItem_" + netID);
            go.transform.position = pos;
            go.tag = "Item";

            var wi = go.AddComponent<WorldItem>();
            wi.Setup(item, count, netID);

            _worldItems[netID] = go;
            return go;
        }

        public static void DespawnItem(int netID)
        {
            if (_worldItems.TryGetValue(netID, out var go))
            {
                Object.Destroy(go);
                _worldItems.Remove(netID);
            }
        }

        public static void ClearAll()
        {
            SaveToFile();
            foreach (var go in _worldItems.Values)
            {
                if (go != null) Object.Destroy(go);
            }
            _worldItems.Clear();
        }

        public static GameObject GetItem(int netID)
        {
            _worldItems.TryGetValue(netID, out var go);
            return go;
        }

        public static void SaveToFile()
        {
            try
            {
                var lines = new List<string>();
                foreach (var kvp in _worldItems)
                {
                    var wi = kvp.Value?.GetComponent<WorldItem>();
                    if (wi == null) continue;
                    var pos = kvp.Value.transform.position;
                    lines.Add(string.Format("{0}|{1}|{2}|{3:F2}|{4:F2}|{5:F2}",
                        kvp.Key, (int)wi.ItemEnum, wi.Count,
                        pos.x, pos.y, pos.z));
                }
                File.WriteAllLines(SavePath, lines);
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
                if (!File.Exists(SavePath)) return;
                var lines = File.ReadAllLines(SavePath);
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

                    if (_worldItems.ContainsKey(netID)) continue; // already exists
                    var pos = new Vector3(px, py, pz);
                    SpawnLocalItem((Items.itemlist)itemVal, count, netID, pos);
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
                if (File.Exists(SavePath))
                    File.Delete(SavePath);
            }
            catch { }
        }
    }
}
