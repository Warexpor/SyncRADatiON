// One FindObjectsOfType<MonoBehaviour>(true) per scene, bucketed by Il2Cpp class, instead of one full scene
// walk per synced type (~85 walks per scene load across puzzles, doors, enemies, pickups, emitters).
// All<T>() answers from the buckets with the native subclass check, so derived types still match.
using System;
using System.Collections.Generic;
using UnhollowerBaseLib;
using UnityEngine;

namespace SyncRADation.Sync
{
    internal static class WorldScan
    {
        static readonly Dictionary<IntPtr, List<MonoBehaviour>> _byClass = new Dictionary<IntPtr, List<MonoBehaviour>>();
        // (bucket class, target class) -> is-subclass, so a query costs one interop call per distinct class once.
        static readonly Dictionary<long, bool> _subclass = new Dictionary<long, bool>();
        static bool _built;
        static int _objects;

        public static bool Built => _built;

        public static void Invalidate()
        {
            _byClass.Clear();
            _subclass.Clear();
            _built = false;
            _objects = 0;
        }

        static void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            float t0 = Time.realtimeSinceStartup;
            MonoBehaviour[] all = null;
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
            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            if (ms >= 8f) HitchTrace.Cost("worldScan", ms);
            PlaytestLog.Event("World", "scan objects=" + _objects + " classes=" + _byClass.Count + " " + ms.ToString("F1") + "ms");
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
            long key = ((long)klass.ToInt64() * 31) ^ target.ToInt64();
            bool yes;
            if (_subclass.TryGetValue(key, out yes)) return yes;
            try { yes = IL2CPP.il2cpp_class_is_subclass_of(klass, target, false); }
            catch (Exception e) { Guard.Swallow(e); yes = false; }
            _subclass[key] = yes;
            return yes;
        }
    }
}
