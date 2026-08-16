// Stable cross-process entity identity. NEVER use Unity GetInstanceID() across peers.
using System;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Sync
{
    public static class WorldId
    {
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
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;

            if (sceneName != null)
            {
                for (int i = 0; i < sceneName.Length; i++)
                {
                    hash ^= sceneName[i];
                    hash *= prime;
                }
            }

            hash ^= 0;
            hash *= prime;

            if (hierarchyPath != null)
            {
                for (int i = 0; i < hierarchyPath.Length; i++)
                {
                    hash ^= hierarchyPath[i];
                    hash *= prime;
                }
            }

            return hash;
        }

        public static ulong FromTransform(Transform t)
        {
            if (t == null) return 0;
            try
            {
                string n = t.name;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("SR_Spawn_", StringComparison.Ordinal))
                    return Compute("spawn", n);
            }
            catch { }
            string scene = "";
            try
            {
                var go = t.gameObject;
                if (go != null)
                    scene = go.scene.name ?? "";
            }
            catch { }
            if (string.IsNullOrEmpty(scene))
            {
                try { scene = SceneManager.GetActiveScene().name ?? ""; } catch { }
            }
            return Compute(scene, GetHierarchyPath(t));
        }

        public static ulong FromGameObject(GameObject go)
        {
            return go == null ? 0UL : FromTransform(go.transform);
        }

        public static string DebugLabel(ulong id, Transform t)
        {
            string path = t != null ? GetHierarchyPath(t) : "?";
            return id.ToString("X16") + " " + path;
        }
    }
}
