// Scene-scoped registry: WorldId -> components. Rebuilt on every scene load.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Sync
{
    public static class WorldRegistry
    {
        private static readonly Dictionary<ulong, EnemyController> Enemies = new Dictionary<ulong, EnemyController>();
        private static readonly Dictionary<ulong, Doorway_Double> DoubleDoors = new Dictionary<ulong, Doorway_Double>();
        private static readonly Dictionary<ulong, ConnectedDoors> ConnectedDoorMap = new Dictionary<ulong, ConnectedDoors>();
        private static readonly Dictionary<ulong, EventSlidingDoor> SlidingDoors = new Dictionary<ulong, EventSlidingDoor>();
        private static string _sceneName = "";

        public static string SceneName => _sceneName;
        public static int EnemyCount => Enemies.Count;
        public static int DoorCount => DoubleDoors.Count + ConnectedDoorMap.Count + SlidingDoors.Count;

        public static void Rebuild()
        {
            Enemies.Clear();
            DoubleDoors.Clear();
            ConnectedDoorMap.Clear();
            SlidingDoors.Clear();

            _sceneName = SceneManager.GetActiveScene().name ?? "";

            try
            {
                var enemies = FindAll<EnemyController>();
                if (enemies != null)
                {
                    for (int i = 0; i < enemies.Length; i++)
                    {
                        var e = enemies[i];
                        if (e == null) continue;
                        ulong id = WorldId.FromGameObject(e.gameObject);
                        if (id == 0) continue;
                        if (!Enemies.ContainsKey(id))
                            Enemies[id] = e;
                        else
                            ModRuntime.Log?.Warning("[WorldRegistry] Enemy WorldId collision: " + WorldId.DebugLabel(id, e.transform));
                    }
                }

                var doubles = FindAll<Doorway_Double>();
                if (doubles != null)
                {
                    for (int i = 0; i < doubles.Length; i++)
                    {
                        var d = doubles[i];
                        if (d == null) continue;
                        ulong id = WorldId.FromGameObject(d.gameObject);
                        if (id != 0 && !DoubleDoors.ContainsKey(id))
                            DoubleDoors[id] = d;
                        else if (id != 0)
                            ModRuntime.Log?.Warning("[WorldRegistry] Double door WorldId collision: " + WorldId.DebugLabel(id, d.transform));
                    }
                }

                var connected = FindAll<ConnectedDoors>();
                if (connected != null)
                {
                    for (int i = 0; i < connected.Length; i++)
                    {
                        var c = connected[i];
                        if (c == null) continue;
                        ulong id = WorldId.FromGameObject(c.gameObject);
                        if (id != 0 && !ConnectedDoorMap.ContainsKey(id))
                            ConnectedDoorMap[id] = c;
                        else if (id != 0)
                            ModRuntime.Log?.Warning("[WorldRegistry] ConnectedDoor WorldId collision: " + WorldId.DebugLabel(id, c.transform));
                    }
                }

                var sliding = FindAll<EventSlidingDoor>();
                if (sliding != null)
                {
                    for (int i = 0; i < sliding.Length; i++)
                    {
                        var s = sliding[i];
                        if (s == null) continue;
                        ulong id = WorldId.FromGameObject(s.gameObject);
                        if (id != 0 && !SlidingDoors.ContainsKey(id))
                            SlidingDoors[id] = s;
                        else if (id != 0)
                            ModRuntime.Log?.Warning("[WorldRegistry] Sliding door WorldId collision: " + WorldId.DebugLabel(id, s.transform));
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Error("[WorldRegistry] Rebuild failed: " + ex);
            }

            ModRuntime.Log?.Msg("[WorldRegistry] scene='" + _sceneName
                + "' enemies=" + Enemies.Count
                + " doubleDoors=" + DoubleDoors.Count
                + " connectedDoors=" + ConnectedDoorMap.Count
                + " slidingDoors=" + SlidingDoors.Count);
        }

        public static void Clear()
        {
            Enemies.Clear();
            DoubleDoors.Clear();
            ConnectedDoorMap.Clear();
            SlidingDoors.Clear();
            _sceneName = "";
        }

        private static T[] FindAll<T>() where T : Object
        {
            try { return Object.FindObjectsOfType<T>(true); }
            catch { return Object.FindObjectsOfType<T>(); }
        }

        public static bool TryGetEnemy(ulong id, out EnemyController enemy) => Enemies.TryGetValue(id, out enemy);
        public static bool TryGetDoubleDoor(ulong id, out Doorway_Double door) => DoubleDoors.TryGetValue(id, out door);
        public static bool TryGetConnectedDoor(ulong id, out ConnectedDoors door) => ConnectedDoorMap.TryGetValue(id, out door);
        public static bool TryGetSlidingDoor(ulong id, out EventSlidingDoor door) => SlidingDoors.TryGetValue(id, out door);

        public static IEnumerable<KeyValuePair<ulong, EnemyController>> AllEnemies()
        {
            foreach (var kvp in Enemies)
                yield return kvp;
        }

        public static IEnumerable<KeyValuePair<ulong, Doorway_Double>> AllDoubleDoors()
        {
            foreach (var kvp in DoubleDoors)
                yield return kvp;
        }

        public static IEnumerable<KeyValuePair<ulong, ConnectedDoors>> AllConnectedDoors()
        {
            foreach (var kvp in ConnectedDoorMap)
                yield return kvp;
        }

        public static IEnumerable<KeyValuePair<ulong, EventSlidingDoor>> AllSlidingDoors()
        {
            foreach (var kvp in SlidingDoors)
                yield return kvp;
        }

        public static string GetLocalRoomName()
        {
            try
            {
                if (PlayerState.currentRoom != null && !string.IsNullOrEmpty(PlayerState.currentRoom.roomName))
                    return PlayerState.currentRoom.roomName;
                if (!string.IsNullOrEmpty(PlayerState.currentLocation))
                    return PlayerState.currentLocation;
            }
            catch { }
            return "";
        }
    }
}
