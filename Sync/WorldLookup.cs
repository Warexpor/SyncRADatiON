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

        /// <summary>Drop the cached scan of one type (a component was instantiated after the scene scan).</summary>
        public static void Invalidate<T>() where T : UnityEngine.Object
        {
            Type t = typeof(T);
            _allByType.Remove(t);
            _idByType.Remove(t);
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
                    if (id == 0) continue;
                    Component existing;
                    if (map.TryGetValue(id, out existing) && existing != null)
                    {
                        NoteDuplicate(id, typeof(T).Name, c, existing);
                        if (!PreferOver(c, existing)) continue;
                    }
                    map[id] = c;
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldLookup] BuildIdMap<" + typeof(T).Name + "> failed: " + ex.Message);
            }
            return map;
        }

        static readonly HashSet<ulong> _loggedDuplicateIds = new HashSet<ulong>();

        /// <summary>Log a WorldId collision once per id (Rebuild runs several times per scene load).</summary>
        public static void NoteDuplicate(ulong id, string what, Component candidate, Component existing)
        {
            if (_loggedDuplicateIds.Count > 1024) _loggedDuplicateIds.Clear();
            if (!_loggedDuplicateIds.Add(id)) return;
            string a = "?", b = "?";
            try { a = WorldId.GetHierarchyPath(candidate.transform); } catch (Exception e) { Guard.Swallow(e); }
            try { b = WorldId.GetHierarchyPath(existing.transform); } catch (Exception e) { Guard.Swallow(e); }
            ModRuntime.Log?.Warning("[WorldLookup] duplicate WorldId " + id.ToString("X16") + " (" + what
                + "): '" + a + "' vs '" + b + "' — keeping the stable-order winner");
        }

        /// <summary>
        /// Deterministic duplicate resolution so host and clients bind the same object no matter what
        /// order FindObjectsOfType returns: lower hierarchy path, then lower component index, then lower position.
        /// </summary>
        public static bool PreferOver(Component candidate, Component current)
        {
            if (candidate == null) return false;
            if (current == null) return true;
            try
            {
                int c = string.CompareOrdinal(WorldId.GetHierarchyPath(candidate.transform),
                    WorldId.GetHierarchyPath(current.transform));
                if (c != 0) return c < 0;

                if (candidate.gameObject == current.gameObject)
                {
                    var all = candidate.GetComponents<Component>(); // serialized order is identical on every peer
                    int ci = -1, ei = -1;
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] == candidate) ci = i;
                        if (all[i] == current) ei = i;
                    }
                    if (ci != ei) return ci >= 0 && (ei < 0 || ci < ei);
                }

                string sa = candidate.gameObject.scene.name ?? "";
                string sb = current.gameObject.scene.name ?? "";
                c = string.CompareOrdinal(sa, sb);
                if (c != 0) return c < 0;

                // Same scene, path and component slot: nothing stable separates them (position can differ per
                // peer once things move), so keep the incumbent and say so.
                PlaytestLog.Warn("World", "PreferOver tie on '" + WorldId.GetHierarchyPath(candidate.transform)
                    + "' — keeping the first registered");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldLookup] PreferOver failed: " + ex.Message);
            }
            return false;
        }
    }
}
