// Shared WorldId component lookup (include inactive — room chunks sleep).
// Scene-scoped All/Find caches avoid FindObjectsOfType on every presentation apply.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Sync
{
    public static class WorldLookup
    {
        static string _sceneName = "";
        static readonly Dictionary<Type, UnityEngine.Object[]> _allByType
            = new Dictionary<Type, UnityEngine.Object[]>();
        static readonly Dictionary<Type, Dictionary<ulong, Component>> _idByType
            = new Dictionary<Type, Dictionary<ulong, Component>>();

        public static void Invalidate()
        {
            _allByType.Clear();
            _idByType.Clear();
            _sceneName = "";
        }

        static void EnsureScene()
        {
            string name = "";
            try { name = SceneManager.GetActiveScene().name ?? ""; }
            catch { name = ""; }
            if (name == _sceneName) return;
            _allByType.Clear();
            _idByType.Clear();
            _sceneName = name;
        }

        public static T[] All<T>() where T : UnityEngine.Object
        {
            EnsureScene();
            Type t = typeof(T);
            UnityEngine.Object[] cached;
            if (_allByType.TryGetValue(t, out cached))
                return (T[])cached;

            T[] found = ScanAll<T>();
            _allByType[t] = found ?? Array.Empty<T>();
            return (T[])_allByType[t];
        }

        static T[] ScanAll<T>() where T : UnityEngine.Object
        {
            try { return UnityEngine.Object.FindObjectsOfType<T>(true); }
            catch
            {
                try { return UnityEngine.Object.FindObjectsOfType<T>(); }
                catch { return null; }
            }
        }

        public static T Find<T>(ulong worldId, string missTag) where T : Component
        {
            var found = Find<T>(worldId);
            if (found == null && worldId != 0)
                PlaytestLog.Miss(missTag, typeof(T).Name, worldId);
            return found;
        }

        public static T Find<T>(ulong worldId) where T : Component
        {
            if (worldId == 0) return null;
            EnsureScene();
            Type t = typeof(T);
            Dictionary<ulong, Component> map;
            if (!_idByType.TryGetValue(t, out map))
            {
                map = BuildIdMap<T>();
                _idByType[t] = map;
            }

            Component c;
            if (map.TryGetValue(worldId, out c))
            {
                if (c != null)
                    return c as T;
                map.Remove(worldId);
            }
            return null;
        }

        static Dictionary<ulong, Component> BuildIdMap<T>() where T : Component
        {
            var map = new Dictionary<ulong, Component>();
            try
            {
                var all = All<T>();
                if (all == null) return map;
                for (int i = 0; i < all.Length; i++)
                {
                    var c = all[i];
                    if (c == null) continue;
                    ulong id = WorldId.FromGameObject(c.gameObject);
                    if (id == 0 || map.ContainsKey(id)) continue;
                    map[id] = c;
                }
            }
            catch { }
            return map;
        }
    }
}
