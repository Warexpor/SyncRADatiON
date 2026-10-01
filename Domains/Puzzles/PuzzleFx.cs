// Live presentation of a peer's puzzle input: button animations and press sounds replayed here.
// Zoom-in puzzles sit on an event-screen stage away from their room; the room-side EventScreenInteraction is where
// the panel physically is. A player zoomed into that same screen hears the press exactly like their own (native
// 2D one-shot); anyone else hears it from the panel's position with the door falloff, and only in the panel's room.
using System.Collections.Generic;
using FMODUnity;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class PuzzleFx
    {
        // Set only while a fresh peer packet is applied live (not join dump, not held re-apply after a remount):
        // effects are for presses happening now, not for state catching up.
        public static bool LiveApply;

        // Inside Remote(): native one-shots the replayed press plays go through RemoteOneShot instead of playing
        // at the world origin, and the host does not relay them again.
        static int _depth;
        static GameObject _at;
        static bool _viewing;
        static bool _silent;

        public static bool Active => _depth > 0;

        static readonly Dictionary<int, EventScreenInteraction> _screenOf = new Dictionary<int, EventScreenInteraction>();
        static int _gen = -1;

        public static void Reset()
        {
            _screenOf.Clear();
            _depth = 0;
            _at = null;
            LiveApply = false;
        }

        /// <summary>
        /// Begin replaying a peer's press on c. Pair with End() in finally. silent: native one-shots inside are
        /// dropped (a state snap reusing the native method, not a press happening now).
        /// </summary>
        public static void Begin(Component c, bool silent = false)
        {
            // Never throws: the Unity lookups run before the depth is taken, so a caller outside try cannot leave
            // Active stuck on (every later one-shot rerouted).
            if (_depth > 0) { _depth++; return; }
            GameObject at = null;
            bool viewing = false;
            if (c != null)
            {
                try
                {
                    at = Anchor(c).gameObject;
                    viewing = Viewing(c);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            _at = at;
            _viewing = viewing;
            _silent = silent;
            NetGate.BeginApply();
            _depth = 1;
        }

        public static void End()
        {
            if (_depth <= 0) return;
            _depth--;
            if (_depth > 0) return;
            _at = null;
            NetGate.EndApply();
        }

        /// <summary>
        /// Prefix hook for RuntimeManager.PlayOneShot(path, pos) while Active: true = let the native call play (the
        /// local player is looking at this puzzle), false = handled here (3D at the panel, or silent elsewhere).
        /// </summary>
        public static bool RemoteOneShot(string path)
        {
            if (!Active) return true;
            if (_silent) return false;
            if (_viewing) return true;
            PlayAt(path, _at);
            return false;
        }

        /// <summary>Play a press sound for a peer's input on c (sound the native method would not play itself).</summary>
        public static void Press(Component c, string path)
        {
            if (string.IsNullOrEmpty(path) || c == null) return;
            try
            {
                if (Viewing(c))
                {
                    Begin(c);
                    try { RuntimeManager.PlayOneShot(path, Vector3.zero); }
                    finally { End(); }
                    return;
                }
                PlayAt(path, Anchor(c).gameObject);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>Start a native coroutine (button animation) for a peer's press, sounds routed as above.</summary>
        public static void Run(MonoBehaviour owner, Il2CppSystem.Collections.IEnumerator routine)
        {
            if (owner == null || routine == null) return;
            Begin(owner);
            try { owner.StartCoroutine(routine); }
            catch (System.Exception e) { Guard.Swallow(e); }
            finally { End(); }
        }

        /// <summary>Unity AudioSource press sound (evidence locker): on the stage for a viewer, else at the panel.</summary>
        public static void Clip(Component c, AudioSource src)
        {
            if (c == null || src == null) return;
            try
            {
                if (Viewing(c)) { src.Play(); return; }
                var at = Anchor(c);
                if (!FmodEmitterSync.AudibleHere(at.gameObject) || src.clip == null) return;
                float vol;
                if (!WorldSfx.TryVolume(at.position, out vol)) return;
                AudioSource.PlayClipAtPoint(src.clip, at.position, src.volume * vol);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void PlayAt(string path, GameObject at)
        {
            if (string.IsNullOrEmpty(path) || at == null) return;
            if (!FmodEmitterSync.AudibleHere(at)) return;
            WorldSfx.Play(path, at.transform);
        }

        /// <summary>True when the local player is zoomed into the event screen that shows c.</summary>
        public static bool Viewing(Component c)
        {
            try
            {
                if (PlayerState.gameState != PlayerState.gameStates.eventScreen) return false;
                var es = ScreenOf(c);
                return es != null && es.Eventing;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>Room-side transform of a puzzle: its EventScreenInteraction, else the puzzle itself.</summary>
        public static Transform Anchor(Component c)
        {
            var es = ScreenOf(c);
            return es != null ? es.transform : c.transform;
        }

        static EventScreenInteraction ScreenOf(Component c)
        {
            if (c == null) return null;
            int gen = WorldRegistry.Generation;
            if (gen != _gen) { _gen = gen; _screenOf.Clear(); }
            int iid = c.GetInstanceID();
            EventScreenInteraction es;
            if (_screenOf.TryGetValue(iid, out es)) return es;
            es = FindScreen(c);
            _screenOf[iid] = es;
            return es;
        }

        static EventScreenInteraction FindScreen(Component c)
        {
            EventScreenInteraction near = null;
            float best = 25f;
            try
            {
                var all = Object.FindObjectsOfType<EventScreenInteraction>(true);
                if (all == null) return null;
                Transform t = c.transform;
                Vector3 p = t.position;
                for (int i = 0; i < all.Length; i++)
                {
                    var es = all[i];
                    if (es == null) continue;
                    var obj = es.EventObject;
                    if (obj != null && t.IsChildOf(obj.transform)) return es;
                    if (t.IsChildOf(es.transform)) return es;
                    // Stage outside EventObject: the screen whose event camera looks at it.
                    var cam = es.eventCamera;
                    if (cam == null) continue;
                    float d = Vector3.Distance(cam.transform.position, p);
                    if (d < best) { best = d; near = es; }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return near;
        }
    }
}
