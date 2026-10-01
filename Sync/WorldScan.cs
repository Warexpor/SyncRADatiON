// One FindObjectsOfType<MonoBehaviour>(true) per scene, bucketed by Il2Cpp class, instead of one full scene
// walk per synced type (~85 walks per scene load across puzzles, doors, enemies, pickups, emitters).
// All<T>() answers from the buckets with the native subclass check, so derived types still match.
// At scene load (BuildAndWarm) the same pass pins the WorldId of every scripted GameObject (WorldId.Warm).
using System;
using System.Collections.Generic;
using UnhollowerBaseLib;
using UnityEngine;

namespace SyncRADation.Sync
{
    internal static class WorldScan
    {
        static readonly Dictionary<IntPtr, List<MonoBehaviour>> _byClass = new Dictionary<IntPtr, List<MonoBehaviour>>();
        // (bucket class, target class) -> is-subclass, so a query costs one interop call per distinct class pair once.
        // Keyed on the pointer pair itself: a packed/xor-ed long can collide and answer for the wrong pair.
        static readonly Dictionary<(IntPtr, IntPtr), bool> _subclass = new Dictionary<(IntPtr, IntPtr), bool>();
        static bool _built;
        static int _objects;

        public static void Invalidate()
        {
            _byClass.Clear();
            _subclass.Clear();
            _built = false;
            _objects = 0;
        }

        /// <summary>Scene load: build the buckets (if needed) and pin every scripted GameObject's WorldId.</summary>
        public static void BuildAndWarm() => EnsureBuilt(true);

        static void EnsureBuilt(bool warm = false)
        {
            if (_built && !warm) return;
            float t0 = Time.realtimeSinceStartup;
            MonoBehaviour[] all = null;
            float scanMs = 0f;
            if (!_built)
            {
                _built = true;
                try { all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true); }
                catch (Exception e) { Guard.Swallow(e); }
                if (all == null) return;
                for (int i = 0; i < all.Length; i++)
                {
                    var mb = all[i];
                    if (mb == null) continue;
                    IntPtr klass;
                    try { klass = IL2CPP.il2cpp_object_get_class(mb.Pointer); }
                    catch (Exception e) { Guard.Swallow(e); continue; }
                    if (klass == IntPtr.Zero) continue;
                    List<MonoBehaviour> list;
                    if (!_byClass.TryGetValue(klass, out list))
                    {
                        list = new List<MonoBehaviour>();
                        _byClass[klass] = list;
                    }
                    list.Add(mb);
                }
                _objects = all.Length;
                scanMs = (Time.realtimeSinceStartup - t0) * 1000f;
            }

            string warmPart = "";
            if (warm)
            {
                float w0 = Time.realtimeSinceStartup;
                int gos = Warm();
                float warmMs = (Time.realtimeSinceStartup - w0) * 1000f;
                warmPart = " warm gos=" + gos + " cached=" + WorldId.CachedCount + " " + warmMs.ToString("F1") + "ms";
            }
            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            if (ms >= 8f) HitchTrace.Cost(warm ? "worldScan+warm" : "worldScan", ms);
            PlaytestLog.Event("World", "scan objects=" + _objects + " classes=" + _byClass.Count + " "
                + scanMs.ToString("F1") + "ms" + warmPart);
        }

        /// <summary>Pin the WorldId of every GameObject that carries a scanned MonoBehaviour. Returns GameObjects visited.</summary>
        static int Warm()
        {
            var pass = new WorldId.WarmPass();
            var seen = new HashSet<IntPtr>();
            foreach (var kv in _byClass)
            {
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    var mb = list[i];
                    if (mb == null) continue;
                    try
                    {
                        GameObject go = mb.gameObject;
                        if (go == null || !seen.Add(go.Pointer)) continue;
                        WorldId.Warm(go, pass);
                    }
                    catch (Exception e) { Guard.Swallow("WorldScan.Warm", e); }
                }
            }
            return seen.Count;
        }

        /// <summary>True when T can be answered from the MonoBehaviour buckets (anything else falls back to a direct scan).</summary>
        public static bool Supports<T>() where T : UnityEngine.Object
        {
            return typeof(MonoBehaviour).IsAssignableFrom(typeof(T))
                && Il2CppClassPointerStore<T>.NativeClassPtr != IntPtr.Zero;
        }

        public static T[] All<T>() where T : UnityEngine.Object
        {
            EnsureBuilt();
            IntPtr target = Il2CppClassPointerStore<T>.NativeClassPtr;
            if (target == IntPtr.Zero || _byClass.Count == 0) return Array.Empty<T>();
            var result = new List<T>();
            foreach (var kv in _byClass)
            {
                if (!IsSubclass(kv.Key, target)) continue;
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    var mb = list[i];
                    if (mb == null) continue;
                    T t = null;
                    try { t = mb.TryCast<T>(); }
                    catch (Exception e) { Guard.Swallow(e); }
                    if (t != null) result.Add(t);
                }
            }
            return result.ToArray();
        }

        static bool IsSubclass(IntPtr klass, IntPtr target)
        {
            if (klass == target) return true;
            var key = (klass, target);
            bool yes;
            if (_subclass.TryGetValue(key, out yes)) return yes;
            try { yes = IL2CPP.il2cpp_class_is_subclass_of(klass, target, false); }
            catch (Exception e) { Guard.Swallow(e); yes = false; }
            _subclass[key] = yes;
            return yes;
        }
    }
}
