using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class GameObjectEntityTracker
    {
        private static readonly List<GameObject> _objects = new List<GameObject>(128);
        private static readonly Dictionary<GameObject, short> _idMap = new Dictionary<GameObject, short>(128);
        private static readonly HashSet<short> _activeIds = new HashSet<short>();
        private static readonly object _lock = new object();
        private static short _nextId = 1;

        public static short GetOrAssignId(GameObject obj)
        {
            if (obj == null) return 0;
            lock (_lock)
            {
                if (_idMap.TryGetValue(obj, out var id))
                    return id;
                id = GetCollisionFreeId();
                if (id == 0) return 0;
                _idMap[obj] = id;
                _activeIds.Add(id);
                if (!_objects.Contains(obj))
                    _objects.Add(obj);
                return id;
            }
        }

        public static void AssignId(GameObject obj, short id)
        {
            if (obj == null) return;
            lock (_lock)
            {
                _idMap[obj] = id;
                _activeIds.Add(id);
                if (!_objects.Contains(obj))
                    _objects.Add(obj);
            }
        }

        public static bool TryGetId(GameObject obj, out short id)
        {
            if (obj == null) { id = 0; return false; }
            lock (_lock) { return _idMap.TryGetValue(obj, out id); }
        }

        public static GameObject FindByStableId(short id)
        {
            lock (_lock)
            {
                for (int i = 0; i < _objects.Count; i++)
                {
                    var obj = _objects[i];
                    if (obj != null && _idMap.TryGetValue(obj, out var sid) && sid == id)
                        return obj;
                }
            }
            return null;
        }

        public static GameObject FindByPositionAndName(Vector3 pos, string name, float radius, HashSet<short> excludeIds = null)
        {
            float radiusSq = radius * radius;
            GameObject best = null;
            float bestDistSq = float.MaxValue;

            lock (_lock)
            {
                for (int i = 0; i < _objects.Count; i++)
                {
                    var obj = _objects[i];
                    if (obj == null) continue;

                    if (excludeIds != null && _idMap.TryGetValue(obj, out var sid) && excludeIds.Contains(sid))
                        continue;

                    string objName = obj.name;
                    if (objName.EndsWith("(Clone)"))
                        objName = objName.Substring(0, objName.Length - 7);
                    if (!string.Equals(objName, name, System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    float dSq = (obj.transform.position - pos).sqrMagnitude;
                    if (dSq < radiusSq && dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        best = obj;
                    }
                }
            }
            return best;
        }

        public static void Add(GameObject obj)
        {
            if (obj == null) return;
            lock (_lock)
            {
                if (!_objects.Contains(obj))
                {
                    _objects.Add(obj);
                    if (!_idMap.ContainsKey(obj))
                    {
                        short id = GetCollisionFreeId();
                        if (id != 0) { _idMap[obj] = id; _activeIds.Add(id); }
                    }
                }
            }
        }

        public static void Remove(GameObject obj)
        {
            if (obj == null) return;
            lock (_lock)
            {
                if (_idMap.TryGetValue(obj, out var id))
                    _activeIds.Remove(id);
                _objects.Remove(obj);
                _idMap.Remove(obj);
            }
        }

        public static GameObject[] GetAll()
        {
            lock (_lock) { return _objects.ToArray(); }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _objects.Clear();
                _idMap.Clear();
                _activeIds.Clear();
                _nextId = 1;
            }
        }

        private static short GetCollisionFreeId()
        {
            short id;
            int safety = 0;
            do { id = _nextId++; if (++safety > short.MaxValue) return 0; }
            while (id == 0 || _activeIds.Contains(id));
            return id;
        }
    }
}
