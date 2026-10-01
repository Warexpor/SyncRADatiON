// Stable cross-process entity identity. NEVER use Unity GetInstanceID() across peers.
// id = FNV-1a64(scene \0 hierarchy path), path segments name[siblingIndex]. The sibling index shifts when the game
// Destroys an earlier sibling (ItemPickup.release destroys the prop on the taker only), so ids are pinned per scene:
// WorldScan warms every scripted GameObject at scene load (BeginScene + Warm) and FromGameObject answers from that
// cache (a dictionary hit instead of a per-call path walk). Objects first seen later are computed once and cached.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Sync
{
    public static class WorldId
    {
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        const string SpawnPrefix = "SR_Spawn_";
        /// <summary>Same-name reloads keep the cache (dead instance ids are never hit again); this bounds that growth.</summary>
        const int MaxCached = 1 << 17;

        // Scene-scoped (GameObject instance id -> WorldId; local key, never on the wire). Deliberately not session state:
        // ids pinned at load must survive a session that starts later in the same scene. BeginScene clears it.
        static readonly Dictionary<int, ulong> _cache = new Dictionary<int, ulong>(4096);
        static string _cacheScene;

        public static int CachedCount => _cache.Count;

        public static string GetHierarchyPath(Transform t)
        {
            if (t == null) return "";
            var sb = new StringBuilder(128);
            BuildPath(t, sb);
            return sb.ToString();
        }

        private static void BuildPath(Transform t, StringBuilder sb)
        {
            if (t.parent != null)
            {
                BuildPath(t.parent, sb);
                sb.Append('/');
            }

            // Disambiguate same-named siblings: name[siblingIndex]
            int idx = t.GetSiblingIndex();
            sb.Append(t.name);
            sb.Append('[');
            sb.Append(idx);
            sb.Append(']');
        }

        public static ulong Compute(string sceneName, string hierarchyPath)
        {
            // FNV-1a 64-bit over scene\0path
            return Mix(SceneSeed(sceneName), hierarchyPath);
        }

        static ulong SceneSeed(string sceneName)
        {
            ulong hash = Mix(FnvOffset, sceneName);
            hash ^= 0;
            hash *= FnvPrime;
            return hash;
        }

        static ulong Mix(ulong hash, string s)
        {
            if (s == null) return hash;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= s[i];
                hash *= FnvPrime;
            }
            return hash;
        }

        static ulong Mix(ulong hash, char c)
        {
            hash ^= c;
            hash *= FnvPrime;
            return hash;
        }

        /// <summary>Decimal digits of a non-negative int, exactly as StringBuilder.Append(int) writes them.</summary>
        static ulong MixDigits(ulong hash, int v)
        {
            if (v < 0) return Mix(hash, v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (v >= 10) hash = MixDigits(hash, v / 10);
            return Mix(hash, (char)('0' + v % 10));
        }

        /// <summary>The live id from the current hierarchy (no cache).</summary>
        public static ulong ComputeLive(Transform t)
        {
            if (t == null) return 0;
            try
            {
                string n = t.name;
                if (!string.IsNullOrEmpty(n) && n.StartsWith(SpawnPrefix, StringComparison.Ordinal))
                    return Compute("spawn", n);
            }
            catch (Exception e) { Guard.Swallow(e); }
            return Compute(SceneOf(t), GetHierarchyPath(t));
        }

        static string SceneOf(Transform t)
        {
            string scene = "";
            try
            {
                var go = t.gameObject;
                if (go != null)
                    scene = go.scene.name ?? "";
            }
            catch (Exception e) { Guard.Swallow(e); }
            if (string.IsNullOrEmpty(scene))
            {
                try { scene = SceneManager.GetActiveScene().name ?? ""; } catch (Exception e) { Guard.Swallow(e); }
            }
            return scene;
        }

        public static ulong FromTransform(Transform t)
        {
            if (t == null) return 0;
            GameObject go = null;
            try { go = t.gameObject; }
            catch (Exception e) { Guard.Swallow(e); }
            return go != null ? FromGameObject(go) : ComputeLive(t);
        }

        public static ulong FromGameObject(GameObject go)
        {
            if (go == null) return 0UL;
            int key;
            try { key = go.GetInstanceID(); }
            catch (Exception e) { Guard.Swallow(e); return ComputeLive(go.transform); }
            ulong id;
            if (_cache.TryGetValue(key, out id)) return id;
            id = ComputeLive(go.transform);
            if (id != 0) _cache[key] = id;
            return id;
        }

        // ------------------------------------------------------------------ scene cache

        /// <summary>Scene load: a different active scene drops every pinned id (same-name reloads keep them, bounded).</summary>
        public static void BeginScene(string activeScene)
        {
            activeScene = activeScene ?? "";
            if (_cacheScene == activeScene && _cache.Count < MaxCached) return;
            _cache.Clear();
            _cacheScene = activeScene;
        }

        /// <summary>
        /// Drop the pinned id of an object whose identity is changed on purpose (renamed / reparented to get a new id);
        /// the next lookup recomputes it from the current hierarchy.
        /// </summary>
        public static void Forget(GameObject go)
        {
            if (go == null) return;
            try { _cache.Remove(go.GetInstanceID()); }
            catch (Exception e) { Guard.Swallow(e); }
        }

        /// <summary>Pin an id chosen by the caller (host-authored SR_Spawn_* identity of an adopted enemy).</summary>
        public static void Pin(GameObject go, ulong id)
        {
            if (go == null || id == 0) return;
            try { _cache[go.GetInstanceID()] = id; }
            catch (Exception e) { Guard.Swallow(e); }
        }

        /// <summary>One warm pass: transform (Il2Cpp object pointer, alive for the pass) -> FNV state of scene\0path.</summary>
        public sealed class WarmPass
        {
            internal readonly Dictionary<IntPtr, ulong> State = new Dictionary<IntPtr, ulong>(4096);
            /// <summary>Ids added to the cache by this pass.</summary>
            public int Added;
        }

        /// <summary>
        /// Pin go's id unless it already has one. Every ancestor is read once per pass (name, sibling index, parent) and
        /// the id equals ComputeLive at this moment.
        /// </summary>
        public static void Warm(GameObject go, WarmPass pass)
        {
            if (go == null || pass == null) return;
            int key = go.GetInstanceID();
            if (_cache.ContainsKey(key)) return;
            Transform t = go.transform;
            if (t == null) return;
            string n = t.name ?? "";
            ulong id = n.StartsWith(SpawnPrefix, StringComparison.Ordinal) ? Compute("spawn", n) : StateOf(t, n, pass);
            if (id == 0) return;
            _cache[key] = id;
            pass.Added++;
        }

        /// <summary>name: t.name when the caller already read it, else null (read here only on a memo miss).</summary>
        static ulong StateOf(Transform t, string name, WarmPass pass)
        {
            IntPtr p = t.Pointer;
            ulong s;
            if (pass.State.TryGetValue(p, out s)) return s;
            Transform parent = t.parent;
            if (parent == null)
                s = SceneSeed(SceneOf(t));
            else
                s = Mix(StateOf(parent, null, pass), '/');
            s = Mix(s, name ?? t.name);
            s = Mix(s, '[');
            s = MixDigits(s, t.GetSiblingIndex());
            s = Mix(s, ']');
            pass.State[p] = s;
            return s;
        }
    }
}
