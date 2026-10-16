// A peer's RES_LibraryPC robot move, played here as a glide at the native movementSpeed. The robot sends its
// position when a move starts, on the 0.5 s poll while it slides and when it stops (LibraryPcMovePatch /
// LibraryPcUpdatePatch); without the glide it would jump between those samples.
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class LibraryRobotGlide
    {
        struct Glide { public RES_LibraryPC Pc; public Vector2 To; }

        static readonly Dictionary<int, Glide> _active = new Dictionary<int, Glide>();
        static readonly List<int> _done = new List<int>();

        public static void To(RES_LibraryPC pc, Vector2 to)
        {
            if (pc == null) return;
            try
            {
                // A far jump (remount / desync) snaps instead of crossing the maze.
                if ((pc.robotPos - to).sqrMagnitude > 64f) { pc.robotPos = to; Stop(pc); return; }
            }
            catch (System.Exception e) { Guard.Swallow(e); return; }
            _active[pc.GetInstanceID()] = new Glide { Pc = pc, To = to };
        }

        public static void Stop(RES_LibraryPC pc)
        {
            if (pc != null) _active.Remove(pc.GetInstanceID());
        }

        public static void Reset() => _active.Clear();

        public static void Tick()
        {
            if (_active.Count == 0) return;
            _done.Clear();
            float dt = Time.unscaledDeltaTime;
            foreach (var kv in _active)
            {
                var g = kv.Value;
                try
                {
                    if (g.Pc == null || g.Pc.moving) { _done.Add(kv.Key); continue; } // local player drives it
                    float speed = g.Pc.movementSpeed > 0f ? g.Pc.movementSpeed : 4f;
                    var p = Vector2.MoveTowards(g.Pc.robotPos, g.To, speed * dt);
                    g.Pc.robotPos = p;
                    if ((p - g.To).sqrMagnitude < 0.0001f) _done.Add(kv.Key);
                }
                catch (System.Exception e) { Guard.Swallow(e); _done.Add(kv.Key); }
            }
            for (int i = 0; i < _done.Count; i++) _active.Remove(_done[i]);
        }
    }
}
