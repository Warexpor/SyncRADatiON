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
        // Enemy ids stay stable across rebuilds through the WorldId scene cache: the first id computed for an instance in
        // this scene (scene-load warm, or first lookup) is pinned there, so a later reparent / removed sibling does not move
        // it, and RegisterEnemy re-pins host-authored SR_Spawn_* identities (WorldId.Pin).
        private static readonly Dictionary<ulong, Doorway_Double> DoubleDoors = new Dictionary<ulong, Doorway_Double>();
        private static readonly Dictionary<ulong, ConnectedDoors> ConnectedDoorMap = new Dictionary<ulong, ConnectedDoors>();
        private static readonly Dictionary<ulong, EventSlidingDoor> SlidingDoors = new Dictionary<ulong, EventSlidingDoor>();
        private static string _sceneName = "";

        public static string SceneName => _sceneName;
        /// <summary>
        /// Bumped by every Rebuild. Per-instance id caches (EnvEmit, FmodEmitterSync, PuzzleFx) compare against this and
        /// drop their entries after a rebuild; the recomputed id is the one WorldId pinned at scene load.
        /// </summary>
        public static int Generation { get; private set; }
        public static int EnemyCount => Enemies.Count;
        public static int DoorCount => DoubleDoors.Count + ConnectedDoorMap.Count + SlidingDoors.Count;

        private static float _lastRebuildAt = -999f;
        private static string _lastRebuildScene = "";

        // Registry checksum (Sync/WorldChecksum): per category, computed once at the end of Rebuild from the sorted WorldIds.
        // The id lists are kept (sorted) so a SceneDiff answers with exactly the set the checksum was taken over.
        private static readonly int[] ChecksumCountsArr = new int[WorldChecksum.CategoryCount];
        private static readonly ulong[] ChecksumSumsArr = new ulong[WorldChecksum.CategoryCount];
        private static readonly List<ulong>[] ChecksumIds = NewIdLists();
        private static string _checksumScene = "";

        private static List<ulong>[] NewIdLists()
        {
            var lists = new List<ulong>[WorldChecksum.CategoryCount];
            for (int i = 0; i < lists.Length; i++) lists[i] = new List<ulong>();
            return lists;
        }

        /// <summary>Scene the cached checksum was taken in ("" = none yet).</summary>
        public static string ChecksumScene => _checksumScene;
        /// <summary>
        /// True when a checksum of the current registry exists. Solo play never pays for it: Rebuild only computes it on
        /// a live session, and this getter computes it lazily (once per Rebuild) for a session that started afterwards.
        /// </summary>
        public static bool HasChecksum
        {
            get
            {
                if (_checksumScene.Length == 0 && _sceneName.Length > 0 && NetGate.Live)
                    TryComputeChecksum();
                return _checksumScene.Length > 0;
            }
        }
        /// <summary>Per-category id counts of the last Rebuild. Shared array: copy, do not mutate.</summary>
        public static int[] ChecksumCounts => ChecksumCountsArr;
        /// <summary>Per-category WorldId checksums of the last Rebuild. Shared array: copy, do not mutate.</summary>
        public static ulong[] ChecksumSums => ChecksumSumsArr;

        /// <summary>Sorted WorldIds the checksum of a category was taken over (shared list, read only).</summary>
        public static List<ulong> ChecksumIdsOf(int category) =>
            category >= 0 && category < ChecksumIds.Length ? ChecksumIds[category] : null;

        /// <summary>Hierarchy path of a registered object for a diff log line ("" when it is not registered any more).</summary>
        public static string DescribeId(int category, ulong id)
        {
            try
            {
                Component c = null;
                switch (category)
                {
                    case WorldChecksum.Enemies: EnemyController e; if (Enemies.TryGetValue(id, out e)) c = e; break;
                    case WorldChecksum.DoubleDoors: Doorway_Double d; if (DoubleDoors.TryGetValue(id, out d)) c = d; break;
                    case WorldChecksum.ConnectedDoors: ConnectedDoors cd; if (ConnectedDoorMap.TryGetValue(id, out cd)) c = cd; break;
                    case WorldChecksum.SlidingDoors: EventSlidingDoor s; if (SlidingDoors.TryGetValue(id, out s)) c = s; break;
                }
                if (c != null) return WorldId.GetHierarchyPath(c.transform);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return "";
        }

        static void ComputeChecksum()
        {
            for (int i = 0; i < ChecksumIds.Length; i++) ChecksumIds[i].Clear();
            foreach (var kvp in Enemies)
            {
                // F11 spawns are host-authored per session (SR_Spawn_*): the other peer legitimately lacks them at rebuild time.
                EnemyController e = kvp.Value;
                if (e == null || IsSpawnName(e)) continue;
                ChecksumIds[WorldChecksum.Enemies].Add(kvp.Key);
            }
            foreach (var kvp in DoubleDoors) ChecksumIds[WorldChecksum.DoubleDoors].Add(kvp.Key);
            foreach (var kvp in ConnectedDoorMap) ChecksumIds[WorldChecksum.ConnectedDoors].Add(kvp.Key);
            foreach (var kvp in SlidingDoors) ChecksumIds[WorldChecksum.SlidingDoors].Add(kvp.Key);
            for (int i = 0; i < ChecksumIds.Length; i++)
            {
                ChecksumIds[i].Sort();
                ChecksumCountsArr[i] = ChecksumIds[i].Count;
                ChecksumSumsArr[i] = WorldChecksum.ComputeSorted(ChecksumIds[i]);
            }
            _checksumScene = _sceneName;
        }

        static bool IsSpawnName(Component c)
        {
            try
            {
                string n = c.gameObject.name;
                return n != null && n.StartsWith("SR_Spawn_", System.StringComparison.Ordinal);
            }
            catch (System.Exception ex) { Guard.Swallow(ex); return false; }
        }

        static void TryComputeChecksum()
        {
            try { ComputeChecksum(); }
            catch (System.Exception ex) { ClearChecksum(); ModRuntime.Log?.Warning("[WorldRegistry] checksum failed: " + ex.Message); }
        }

        static void ClearChecksum()
        {
            for (int i = 0; i < ChecksumIds.Length; i++)
            {
                ChecksumIds[i].Clear();
                ChecksumCountsArr[i] = 0;
                ChecksumSumsArr[i] = 0;
            }
            _checksumScene = "";
        }

        /// <summary>
        /// Rebuild only when the registry is older than maxAgeSeconds or the active scene changed.
        /// A scene load used to rebuild 3-4x (ModRuntime, OnSceneChanged, handshake, dump).
        /// </summary>
        public static void RebuildIfStale(float maxAgeSeconds = 2f)
        {
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch (System.Exception e) { Guard.Swallow(e); }
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
            if (existingComp == (Component)c) return;
            WorldLookup.NoteDuplicate(id, what, c, existingComp);
            if (WorldLookup.PreferOver(c, existingComp))
                map[id] = c;
        }

        /// <summary>
        /// rescan=false reuses the current scene scan (scene load: ModRuntime just scanned and pinned ids). A mid-scene
        /// rebuild (handshake / dump) rescans so enemies the game spawned since are registered.
        /// </summary>
        public static void Rebuild(bool rescan = true)
        {
            Generation++;
            if (rescan) WorldLookup.Invalidate();
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
                        ulong id = WorldId.FromGameObject(e.gameObject); // pinned per instance per scene
                        if (id == 0) continue;
                        EnemyController before;
                        Enemies.TryGetValue(id, out before);
                        Register(Enemies, id, e, "EnemyController");
                        EnemyController winner;
                        if (Enemies.TryGetValue(id, out winner) && winner == e)
                        {
                            // A replaced duplicate loses its reverse entry too.
                            if (before != null && before != e) EnemyIds.Remove(before.GetInstanceID());
                            EnemyIds[e.GetInstanceID()] = id;
                        }
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

            // Checksum only for a live session (the gameObject.name marshal per enemy is not free); solo stays vanilla-cost.
            ClearChecksum();
            if (NetGate.Live) TryComputeChecksum();

            ModRuntime.Log?.Msg("[WorldRegistry] scene='" + _sceneName
                + "' enemies=" + Enemies.Count
                + " doubleDoors=" + DoubleDoors.Count
                + " connectedDoors=" + ConnectedDoorMap.Count
                + " slidingDoors=" + SlidingDoors.Count
                + (_checksumScene.Length > 0 ? " checksum=" + WorldChecksum.Combine(ChecksumSumsArr).ToString("X16") : " checksum=-"));

        }

        public static void RegisterEnemy(ulong id, EnemyController enemy)
        {
            if (id == 0 || enemy == null) return;
            EnemyController old;
            if (Enemies.TryGetValue(id, out old) && old != null && old != enemy)
                EnemyIds.Remove(old.GetInstanceID());
            Enemies[id] = enemy;
            EnemyIds[enemy.GetInstanceID()] = id;
            // Registration is the identity (adopted SR_Spawn_* rename, F11 spawn): every WorldId lookup of this object and
            // a later Rebuild must answer the same id, not the hierarchy id pinned at scene load.
            try { WorldId.Pin(enemy.gameObject, id); }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            // Destroyed enemies never unregister: rebuild the reverse map from the live forward map once it
            // has drifted, so it stays bounded by the live enemy set.
            if (EnemyIds.Count > Enemies.Count + 64)
            {
                EnemyIds.Clear();
                foreach (var kvp in Enemies)
                {
                    if (kvp.Value != null) EnemyIds[kvp.Value.GetInstanceID()] = kvp.Key;
                }
            }
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
            catch (System.Exception e) { Guard.Swallow(e); }
            return "";
        }
    }
}
