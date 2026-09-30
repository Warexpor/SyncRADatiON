// Scene-scoped registry: WorldId -> components. Rebuilt on every scene load.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Sync
{
    public static class WorldRegistry
    {
        private static readonly Dictionary<ulong, EnemyController> Enemies = new Dictionary<ulong, EnemyController>();
        // Reverse map enemy -> WorldId (local GetInstanceID key, never sent). Hit-time lookup must not
        // recompute WorldId from the hierarchy: sibling order shifts when spawns/dupes are added.
        private static readonly Dictionary<int, ulong> EnemyIds = new Dictionary<int, ulong>();
        private static readonly Dictionary<ulong, Doorway_Double> DoubleDoors = new Dictionary<ulong, Doorway_Double>();
        private static readonly Dictionary<ulong, ConnectedDoors> ConnectedDoorMap = new Dictionary<ulong, ConnectedDoors>();
        private static readonly Dictionary<ulong, EventSlidingDoor> SlidingDoors = new Dictionary<ulong, EventSlidingDoor>();
        private static string _sceneName = "";

        public static string SceneName => _sceneName;
        public static int EnemyCount => Enemies.Count;
        public static int DoorCount => DoubleDoors.Count + ConnectedDoorMap.Count + SlidingDoors.Count;

        private static float _lastRebuildAt = -999f;
        private static string _lastRebuildScene = "";

        /// <summary>
        /// Rebuild only when the registry is older than maxAgeSeconds or the active scene changed.
        /// A scene load used to rebuild 3-4x (ModRuntime, OnSceneChanged, handshake, dump).
        /// </summary>
        public static void RebuildIfStale(float maxAgeSeconds = 2f)
        {
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch { }
            float now = Time.realtimeSinceStartup;
            if (scene == _lastRebuildScene && now - _lastRebuildAt < maxAgeSeconds)
                return;
            Rebuild();
        }

        static void Register<T>(Dictionary<ulong, T> map, ulong id, T c, string what) where T : Component
        {
            T existing;
            Component existingComp;
            // Component-typed compare: Unity fake-null does not survive a generic T == null.
            if (!map.TryGetValue(id, out existing) || (existingComp = existing) == null)
            {
                map[id] = c;
                return;
            }
            WorldLookup.NoteDuplicate(id, what, c, existingComp);
            if (WorldLookup.PreferOver(c, existingComp))
                map[id] = c;
        }

        public static void Rebuild()
        {
            WorldLookup.Invalidate();
            Enemies.Clear();
            EnemyIds.Clear();
            DoubleDoors.Clear();
            ConnectedDoorMap.Clear();
            SlidingDoors.Clear();

            _sceneName = SceneManager.GetActiveScene().name ?? "";
            _lastRebuildScene = _sceneName;
            _lastRebuildAt = Time.realtimeSinceStartup;

            try
            {
                var enemies = WorldLookup.All<EnemyController>();
                if (enemies != null)
                {
                    for (int i = 0; i < enemies.Length; i++)
                    {
                        var e = enemies[i];
                        if (e == null || e.gameObject == null) continue;
                        if (SyncRADation.Cheats.EntitySpawner.IsTemplateObject(e.gameObject)) continue;
                        ulong id = WorldId.FromGameObject(e.gameObject);
                        if (id == 0) continue;
                        Register(Enemies, id, e, "EnemyController");
                        EnemyController winner;
                        if (Enemies.TryGetValue(id, out winner) && winner == e)
                            EnemyIds[e.GetInstanceID()] = id;
                    }
                }

                var doubles = WorldLookup.All<Doorway_Double>();
                if (doubles != null)
                {
                    for (int i = 0; i < doubles.Length; i++)
                    {
                        var d = doubles[i];
                        if (d == null) continue;
                        ulong id = WorldId.FromGameObject(d.gameObject);
                        if (id != 0) Register(DoubleDoors, id, d, "Doorway_Double");
                    }
                }

                var connected = WorldLookup.All<ConnectedDoors>();
                if (connected != null)
                {
                    for (int i = 0; i < connected.Length; i++)
                    {
                        var c = connected[i];
                        if (c == null) continue;
                        ulong id = WorldId.FromGameObject(c.gameObject);
                        if (id != 0) Register(ConnectedDoorMap, id, c, "ConnectedDoors");
                    }
                }

                var sliding = WorldLookup.All<EventSlidingDoor>();
                if (sliding != null)
                {
                    for (int i = 0; i < sliding.Length; i++)
                    {
                        var s = sliding[i];
                        if (s == null) continue;
                        ulong id = WorldId.FromGameObject(s.gameObject);
                        if (id != 0) Register(SlidingDoors, id, s, "EventSlidingDoor");
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
            WorldLookup.Invalidate();
            Enemies.Clear();
            EnemyIds.Clear();
            DoubleDoors.Clear();
            ConnectedDoorMap.Clear();
            SlidingDoors.Clear();
            _sceneName = "";
            _lastRebuildScene = "";
        }

        public static void RegisterEnemy(ulong id, EnemyController enemy)
        {
            if (id == 0 || enemy == null) return;
            Enemies[id] = enemy;
            EnemyIds[enemy.GetInstanceID()] = id;
        }

        /// <summary>WorldId cached at Rebuild/Register time. False for enemies the registry never saw.</summary>
        public static bool TryGetEnemyId(EnemyController enemy, out ulong id)
        {
            id = 0;
            if (enemy == null) return false;
            try { return EnemyIds.TryGetValue(enemy.GetInstanceID(), out id); }
            catch { return false; }
        }

        public static bool TryGetEnemy(ulong id, out EnemyController enemy) => Enemies.TryGetValue(id, out enemy);
        public static bool TryGetDoubleDoor(ulong id, out Doorway_Double door) => DoubleDoors.TryGetValue(id, out door);
        public static bool TryGetConnectedDoor(ulong id, out ConnectedDoors door) => ConnectedDoorMap.TryGetValue(id, out door);
        public static bool TryGetSlidingDoor(ulong id, out EventSlidingDoor door) => SlidingDoors.TryGetValue(id, out door);

        /// <summary>Live map — iterate with foreach (struct enumerator, no per-call iterator garbage).</summary>
        public static Dictionary<ulong, EnemyController> AllEnemies() => Enemies;

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
