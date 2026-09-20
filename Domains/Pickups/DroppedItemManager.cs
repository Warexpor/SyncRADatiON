// Thin façade — dropped-item logic lives in Domains/Pickups (Registry + Spawner).
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.ItemSystem
{
    public static class DroppedItemManager
    {
        public const string NamePrefix = DroppedItemRegistry.NamePrefix;

        public struct Drop
        {
            public GameObject Go;
            public Items.itemlist Item;
            public int Count;
            public int Key;
            public string Scene;
            public Vector3 Pos;

            public static implicit operator Drop(DroppedItemRegistry.Drop d)
            {
                return new Drop
                {
                    Go = d.Go,
                    Item = d.Item,
                    Count = d.Count,
                    Key = d.Key,
                    Scene = d.Scene,
                    Pos = d.Pos
                };
            }
        }

        public static IEnumerable<Drop> All()
        {
            foreach (var d in DroppedItemRegistry.All())
                yield return d;
        }

        public static bool IsDropped(ItemPickup p) => DroppedItemRegistry.IsDropped(p);
        public static bool IsDroppedGo(GameObject go) => DroppedItemRegistry.IsDroppedGo(go);
        public static bool TryKeyOf(ItemPickup p, out int key) => DroppedItemRegistry.TryKeyOf(p, out key);
        public static bool TryKeyOfGo(GameObject go, out int key) => DroppedItemRegistry.TryKeyOfGo(go, out key);

        public static Vector3 FloorDropPos(Transform player) => DroppedItemRegistry.FloorDropPos(player);

        public static GameObject SpawnLocalItem(Items.itemlist item, int count, int netID, Vector3 pos)
            => DroppedItemSpawner.SpawnLocalItem(item, count, netID, pos);

        public static int SanitizeStack(int n, bool unique) => DroppedItemRegistry.SanitizeStack(n, unique);
        public static int CountInBag(Items.itemlist id) => DroppedItemRegistry.CountInBag(id);
        public static Interaction NearbyInteraction(Vector3 pos, float maxDist)
            => DroppedItemRegistry.NearbyInteraction(pos, maxDist);
        public static void SetHighlight(GameObject go, bool on) => DroppedItemRegistry.SetHighlight(go, on);
        public static bool InspectLocked() => DroppedItemRegistry.InspectLocked();
        public static void RestorePlay() => DroppedItemRegistry.RestorePlay();
        public static void HideForClaim(int netID) => DroppedItemRegistry.HideForClaim(netID);
        public static void DespawnWhenIdle(int netID) => DroppedItemRegistry.DespawnWhenIdle(netID);
        public static void TickDeferred() => DroppedItemRegistry.TickDeferred();
        public static void DespawnItem(int netID) => DroppedItemRegistry.DespawnItem(netID);
        public static void ClearVisuals() => DroppedItemRegistry.ClearVisuals();
        public static void RespawnCurrentScene() => DroppedItemRegistry.RespawnCurrentScene();
        public static void ClearAll() => DroppedItemRegistry.ClearAll();
        public static GameObject GetItem(int netID) => DroppedItemRegistry.GetItem(netID);
        public static bool TryGet(int netID, out Items.itemlist item, out int count)
            => DroppedItemRegistry.TryGet(netID, out item, out count);

        public static void RestOnFloor(GameObject go) => DroppedItemSpawner.RestOnFloor(go);
    }
}
