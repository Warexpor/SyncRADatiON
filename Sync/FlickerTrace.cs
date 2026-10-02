// Flicker diagnostics: one line per CHANGE (room chunk on/off, proxy visibility / jumps, client enemy
// active / in-chunk / snap jumps / unknown ids). Only with the Diagnostics pref (every entry point returns first
// thing when off); budgeted per tag so a toggle loop shows up as a "flapping" summary instead of flooding Latest.log.
using System.Collections.Generic;
using SyncRADation.Config;
using SyncRADation.Networking;
using SyncRADation.Players;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class FlickerTrace
    {
        const float Window = 5f;
        const int Budget = 25;
        const float ProxyJump = 2f;
        /// <summary>Proxy visibility check (renderers + Camera.main) at ~5 Hz; the jump check stays per frame.</summary>
        const float ProxyVisInterval = 0.2f;
        /// <summary>A proxy without renderers yet (clone still building) is re-fetched at most this often.</summary>
        const float ProxyRendRefetch = 1f;

        sealed class TagBudget
        {
            public float Start;
            public int Used;
            public int Dropped;
        }
        // persistent: per-tag log budget windows (time-based, self-expiring)
        static readonly Dictionary<string, TagBudget> _budgets = new Dictionary<string, TagBudget>();

        static readonly Dictionary<int, bool> _chunkOn = new Dictionary<int, bool>();
        static readonly Dictionary<int, int> _chunkFlips = new Dictionary<int, int>();

        sealed class ProxyVis
        {
            public Renderer[] Rends;
            public int State = -1;
            public Vector3 LastPos;
            public bool HavePos;
            public float NextVisAt;
            public float RendsAt = -999f;
        }
        static readonly Dictionary<int, ProxyVis> _proxy = new Dictionary<int, ProxyVis>();

        // ---- Vertical bob ("jumping model"): peak-to-peak heights over 1 s windows ----
        const float BobWindow = 1f;
        // Elster is ~8 units tall (hips ~4.5 above the root); a walk cycle swings the hips ~0.7 smoothly.
        /// <summary>Root height swing worth a line (she cannot jump).</summary>
        const float BobRoot = 0.4f;
        /// <summary>One-sample hips height step worth a line (a pop, not the smooth walk swing).</summary>
        const float BobHipsStep = 0.35f;
        /// <summary>Root swing beyond this is a door / teleport, not a bob.</summary>
        const float BobTeleport = 5f;

        sealed class Bob
        {
            public float Start = -1f;
            public float RawMin, RawMax, DrawnMin, DrawnMax, HipsMin, HipsMax;
            public float LastHips, HipsStep;

            /// <summary>Adds one sample; at the end of a window returns its summary when anything swung, else null.</summary>
            public string Add(float now, float raw, float drawn, float hips)
            {
                if (Start < 0f || now - Start > BobWindow * 3f)
                {
                    Begin(now, raw, drawn, hips);
                    return null;
                }
                RawMin = Mathf.Min(RawMin, raw); RawMax = Mathf.Max(RawMax, raw);
                DrawnMin = Mathf.Min(DrawnMin, drawn); DrawnMax = Mathf.Max(DrawnMax, drawn);
                HipsMin = Mathf.Min(HipsMin, hips); HipsMax = Mathf.Max(HipsMax, hips);
                HipsStep = Mathf.Max(HipsStep, Mathf.Abs(hips - LastHips));
                LastHips = hips;
                if (now - Start < BobWindow) return null;
                float r = RawMax - RawMin, d = DrawnMax - DrawnMin;
                string line = null;
                bool teleport = r > BobTeleport || d > BobTeleport;
                if (!teleport && (r > BobRoot || d > BobRoot || HipsStep > BobHipsStep))
                    line = "rootZ=" + RawMin.ToString("F2") + ".." + RawMax.ToString("F2")
                        + " drawnZ=" + DrawnMin.ToString("F2") + ".." + DrawnMax.ToString("F2")
                        + " hipsH=" + HipsMin.ToString("F2") + ".." + HipsMax.ToString("F2")
                        + " hipsStep=" + HipsStep.ToString("F2");
                Begin(now, raw, drawn, hips);
                return line;
            }

            void Begin(float now, float raw, float drawn, float hips)
            {
                Start = now;
                RawMin = RawMax = raw;
                DrawnMin = DrawnMax = drawn;
                HipsMin = HipsMax = hips;
                LastHips = hips;
                HipsStep = 0f;
            }
        }
        static readonly Dictionary<int, Bob> _proxyBob = new Dictionary<int, Bob>();
        // persistent: the local player's own window, restarted by its gap check (Bob.Add)
        static readonly Bob _selfBob = new Bob();

        /// <summary>
        /// Receiver, per frame: the sender's root z as received (raw), the proxy root z as drawn, and the proxy hips
        /// height above its root (world up is -Z). Tells a wobbling sender root from a pose (hips) bob.
        /// </summary>
        public static void ProxyBob(int pid, float rawZ, float drawnZ, float hipsH, string mode)
        {
            if (!ModConfig.DiagnosticsOn) return;
            Bob b;
            if (!_proxyBob.TryGetValue(pid, out b))
            {
                b = new Bob();
                _proxyBob[pid] = b;
            }
            string line = b.Add(Time.unscaledTime, rawZ, drawnZ, hipsH);
            if (line != null)
                Emit("Proxy", "bob p" + pid + " " + line + " mode=" + mode);
        }

        /// <summary>Sender, per pose send: the same numbers for the local Elster (raw = drawn = her root z).</summary>
        public static void SelfBob(float rootZ, float hipsH)
        {
            if (!ModConfig.DiagnosticsOn) return;
            string line = _selfBob.Add(Time.unscaledTime, rootZ, rootZ, hipsH);
            if (line != null)
                Emit("Move", "self bob " + line);
        }

        static readonly Dictionary<ulong, int> _enemyState = new Dictionary<ulong, int>();
        static readonly HashSet<ulong> _missSeen = new HashSet<ulong>();
        static readonly HashSet<int> _wakeLogged = new HashSet<int>();

        public static void Reset()
        {
            _chunkOn.Clear();
            _chunkFlips.Clear();
            _proxy.Clear();
            _proxyBob.Clear();
            _enemyState.Clear();
            _missSeen.Clear();
            _wakeLogged.Clear();
        }

        static void Emit(string tag, string msg)
        {
            float now = Time.unscaledTime;
            TagBudget b;
            if (!_budgets.TryGetValue(tag, out b))
            {
                b = new TagBudget { Start = now };
                _budgets[tag] = b;
            }
            if (now - b.Start > Window)
            {
                if (b.Dropped > 0)
                    PlaytestLog.Warn(tag, "flapping: " + b.Dropped + " more changes in " + Window.ToString("F0") + "s not logged");
                b.Start = now;
                b.Used = 0;
                b.Dropped = 0;
            }
            if (b.Used >= Budget) { b.Dropped++; return; }
            b.Used++;
            PlaytestLog.Event(tag, msg);
        }

        static string Here()
        {
            string r = NetworkDamageSystem.CurrentRoomName();
            return string.IsNullOrEmpty(r) ? "?" : r;
        }

        // ---- Rooms ----

        public static void RoomChunk(Room room, bool value)
        {
            if (!ModConfig.DiagnosticsOn || room == null || !NetGate.Live) return;
            int key = room.GetInstanceID(); // local-only bookkeeping, never on the wire
            bool prev;
            bool had = _chunkOn.TryGetValue(key, out prev);
            if (had && prev == value) return;
            _chunkOn[key] = value;
            if (!had && !value) return; // first sight of a room already asleep: not a change
            int flips;
            _chunkFlips.TryGetValue(key, out flips);
            _chunkFlips[key] = ++flips;
            Emit("Room", "chunk " + (value ? "ON " : "OFF ") + Name(room)
                + " by=" + (NetGate.IsApplying ? "mod" : "game")
                + " flips=" + flips + " here=" + Here());
        }

        public static void RoomEnter(Room room)
        {
            if (!ModConfig.DiagnosticsOn || room == null || !NetGate.Live) return;
            Emit("Room", "enter " + Name(room));
        }

        static string Name(Room room)
        {
            try
            {
                if (!string.IsNullOrEmpty(room.roomName)) return room.roomName;
                return room.gameObject.name;
            }
            catch { return "?"; }
        }

        // ---- Player proxies ----

        /// <summary>LateUpdate, after the pose is written. mode = hold / extrap / lerp.</summary>
        public static void Proxy(int pid, GameObject go, string mode)
        {
            if (!ModConfig.DiagnosticsOn || go == null) return;
            ProxyVis v;
            if (!_proxy.TryGetValue(pid, out v))
            {
                v = new ProxyVis();
                _proxy[pid] = v;
            }
            var pos = go.transform.position;
            if (v.HavePos)
            {
                float d = Vector3.Distance(pos, v.LastPos);
                if (d > ProxyJump)
                    Emit("Proxy", "jump p" + pid + " d=" + d.ToString("F1") + " mode=" + mode
                        + " to=" + Fmt(pos) + " peerRoom=" + PeerRoom(pid) + " here=" + Here());
            }
            v.LastPos = pos;
            v.HavePos = true;

            // Everything below is per-renderer interop + Camera.main: ~5 Hz is plenty to catch a hide.
            float now = Time.unscaledTime;
            if (now < v.NextVisAt) return;
            v.NextVisAt = now + ProxyVisInterval;

            bool stale = v.Rends == null || v.Rends.Length == 0 || v.Rends[0] == null;
            if (stale && now - v.RendsAt >= ProxyRendRefetch)
            {
                v.Rends = go.GetComponentsInChildren<Renderer>(true);
                v.RendsAt = now;
            }
            if (v.Rends == null) return;

            bool active = go.activeInHierarchy;
            int enabled = 0, visible = 0;
            for (int i = 0; i < v.Rends.Length; i++)
            {
                var r = v.Rends[i];
                if (r == null) continue;
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                enabled++;
                if (r.isVisible) visible++;
            }
            bool onScreen = OnScreen(pos);
            // Off-screen and culled is normal; only on-screen + not drawn (or a disabled object) is a hide.
            int state = (active ? 1 : 0) | (enabled > 0 ? 2 : 0) | (visible > 0 ? 4 : 0) | (onScreen ? 8 : 0);
            if (state == v.State) return;
            bool first = v.State < 0;
            v.State = state;
            if (first && active && enabled > 0) return;
            Emit("Proxy", "vis p" + pid + " active=" + active + " renderers=" + enabled + "/" + v.Rends.Length
                + " drawn=" + visible + " onScreen=" + onScreen + " mode=" + mode
                + " at=" + Fmt(pos) + " peerRoom=" + PeerRoom(pid) + " here=" + Here());
        }

        public static void ProxyGone(int pid)
        {
            _proxy.Remove(pid);
            _proxyBob.Remove(pid);
        }

        static string PeerRoom(int pid)
        {
            string r = PartyVitals.RoomOf(pid);
            return string.IsNullOrEmpty(r) ? "?" : r;
        }

        static bool OnScreen(Vector3 pos)
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) return false;
                var vp = cam.WorldToViewportPoint(pos);
                return vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;
            }
            catch { return false; }
        }

        static string Fmt(Vector3 p) => "(" + p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1") + ")";

        // ---- Enemies ----

        /// <summary>Client, per applied snapshot: active / in-chunk transitions of a puppet.</summary>
        public static void ClientEnemy(ulong id, EnemyController e, bool snapAlive, byte snapState)
        {
            if (!ModConfig.DiagnosticsOn || e == null) return;
            bool active = false, self = false;
            try { active = e.gameObject.activeInHierarchy; self = e.gameObject.activeSelf; } catch { }
            int state = (active ? 1 : 0) | (self ? 2 : 0) | (snapAlive ? 4 : 0);
            int prev;
            bool had = _enemyState.TryGetValue(id, out prev);
            if (had && prev == state) return;
            _enemyState[id] = state;
            if (!had && active) return; // first sight of a visible enemy: normal
            Emit("Enemy", "client " + id.ToString("X16") + " " + SafeName(e)
                + " active=" + active + " self=" + self + " hostAlive=" + snapAlive + " hostState=" + snapState
                + " room=" + EnemyRoom(e) + " here=" + Here());
        }

        public static void ClientEnemySnapJump(ulong id, EnemyController e, float dist)
        {
            if (!ModConfig.DiagnosticsOn) return;
            Emit("Enemy", "client snap-jump " + id.ToString("X16") + " " + SafeName(e) + " d=" + dist.ToString("F1"));
        }

        public static void ClientEnemyMiss(ulong id, Vector3 hostPos, byte state)
        {
            if (!ModConfig.DiagnosticsOn || !_missSeen.Add(id)) return;
            Emit("Enemy", "client unknown " + id.ToString("X16") + " at=" + Fmt(hostPos) + " hostState=" + state
                + " (host has it, this client does not) here=" + Here());
        }

        /// <summary>Host: one line per enemy instance the first time a wake is attempted.</summary>
        public static void HostWake(EnemyController e, bool ok, string why)
        {
            if (!ModConfig.DiagnosticsOn || e == null) return;
            int key;
            try { key = e.GetInstanceID(); } catch { return; } // local-only bookkeeping
            if (!_wakeLogged.Add(key)) return;
            Emit("Enemy", "wake " + (ok ? "ok " : "REFUSED ") + SafeName(e) + " " + why + " room=" + EnemyRoom(e));
        }

        static string SafeName(EnemyController e)
        {
            try { return e.gameObject.name; } catch { return "?"; }
        }

        static string EnemyRoom(EnemyController e)
        {
            try
            {
                var t = e.transform;
                while (t != null)
                {
                    var r = t.GetComponent<Room>();
                    if (r != null) return Name(r);
                    t = t.parent;
                }
            }
            catch { }
            return "?";
        }
    }
}
