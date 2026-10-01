// Host-authoritative enemy/boss -> remote-player damage.
// The proxy has no colliders and no MonoBehaviours, so native Hurtbox.OnTriggerEnter and the
// EnemyController.Hit range check only ever hurt the host's Elster. The host evaluates the same
// conditions against each remote proxy and sends EnemyDamage to the victim.
using System.Collections.Generic;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class ClientDamageService
    {
        private struct Target
        {
            public int Pid;
            public Vector3 Pos;
        }

        private static readonly List<Target> _targets = new List<Target>(4);
        private static readonly Dictionary<long, float> _inside = new Dictionary<long, float>();
        private static readonly HashSet<long> _seen = new HashSet<long>();
        private static readonly List<long> _stale = new List<long>(8);
        private static readonly Dictionary<int, Collider> _colliders = new Dictionary<int, Collider>();
        private static readonly HashSet<int> _noClosestPoint = new HashSet<int>();
        private static readonly Dictionary<long, float> _swingTime = new Dictionary<long, float>();

        // Dedicated hurtbox list. WorldLookup.All<Hurtbox> is cached per scene and never sees enemies,
        // projectiles or adopted spawns created after the load, so the host keeps its own list. The scan includes
        // inactive objects, so sleeping room chunks and weapon windows (Hit/EndHit toggling) are already in it; it
        // is redone only when the world changes: a spawn hook (NoteSpawn: enemy spawner, EnemySpawn adoption, host
        // F11, Grenade.OnEnable for the GrenadeExplosion prefab, the one runtime-instantiated Hurtbox prefab), a
        // WorldRegistry rebuild (generation), a scene change, and a slow safety rescan.
        // hid / damage / pulse / collider are cached here: they are read-only for the scan, so the per-frame
        // pass only touches the three live toggles (Hurtbox.enabled, activeInHierarchy, Collider.enabled).
        private struct Hb
        {
            public Hurtbox H;
            public GameObject Go;
            public Collider Col;
            public int Hid;
            public int Damage;
            public float Pulse;
            public bool NoClosest;
        }

        private static readonly List<Hb> _hurtboxes = new List<Hb>(64);
        private static float _nextRefresh;
        private static bool _forceRefresh = true;
        private static int _scanGeneration = int.MinValue;
        private const float SafetyRefreshInterval = 5f;

        /// <summary>An enemy / spawner child / grenade appeared: rescan hurtboxes on the next tick.</summary>
        public static void NoteSpawn() => _forceRefresh = true;

        static void RefreshHurtboxes()
        {
            float now = Time.unscaledTime;
            int gen = WorldRegistry.Generation;
            if (!_forceRefresh && gen == _scanGeneration && now < _nextRefresh) return;
            _forceRefresh = false;
            _scanGeneration = gen;
            _hurtboxes.Clear();
            // Per-collider caches are keyed by local instance ids; projectiles churn them, so cap growth.
            if (_colliders.Count > 512)
            {
                _colliders.Clear();
                _noClosestPoint.Clear();
            }
            _nextRefresh = now + SafetyRefreshInterval;
            try
            {
                var found = Object.FindObjectsOfType<Hurtbox>(true);
                for (int i = 0; i < found.Length; i++)
                {
                    var h = found[i];
                    // Player melee/stomp boxes (canDamageEnemies) are the local Elster's weapons.
                    if (h == null || h.canDamageEnemies || h.damage <= 0) continue;
                    var e = new Hb
                    {
                        H = h,
                        Go = h.gameObject,
                        Damage = h.damage,
                        Hid = h.GetInstanceID(),
                        Pulse = h.pulseTime > 0.25f ? h.pulseTime : 0.5f
                    };
                    if (!_colliders.TryGetValue(e.Hid, out Collider col) || col == null)
                    {
                        col = e.Go.GetComponent<Collider>();
                        _colliders[e.Hid] = col;
                        if (col != null && !SupportsClosestPoint(col))
                            _noClosestPoint.Add(e.Hid);
                    }
                    if (col == null) continue;
                    e.Col = col;
                    e.NoClosest = _noClosestPoint.Contains(e.Hid);
                    _hurtboxes.Add(e);
                }
            }
            catch (System.Exception ex) { WarnOnce("hurtbox scan", ex); }
        }

        static bool SupportsClosestPoint(Collider col)
        {
            // Collider.ClosestPoint only answers for Box/Sphere/Capsule/convex Mesh; anything else logs an
            // error and returns the input point, which would inflate the overlap test.
            try
            {
                var mesh = col.TryCast<MeshCollider>();
                if (mesh != null) return mesh.convex;
                return col.TryCast<BoxCollider>() != null
                    || col.TryCast<SphereCollider>() != null
                    || col.TryCast<CapsuleCollider>() != null;
            }
            catch (System.Exception ex) { WarnOnce("collider type", ex); return false; }
        }

        private static readonly HashSet<string> _warned = new HashSet<string>();

        static void WarnOnce(string key, System.Exception ex)
        {
            if (_warned.Add(key))
                ModRuntime.Log?.Warning("[ClientDamage] " + key + ": " + ex.Message);
        }

        private const float MinSwingGap = 0.35f;
        private const float RangeSlack = 0.35f;
        private const float AngleSlack = 12f;

        public static void OnSceneChanged()
        {
            _hurtboxes.Clear();
            _forceRefresh = true;
            _peerSceneOk.Clear();
            _inside.Clear();
            _seen.Clear();
            _colliders.Clear();
            _noClosestPoint.Clear();
            _swingTime.Clear();
        }

        // PeerInHostScene reads (and allocates) the active scene name on every call; the verdict only
        // changes on a scene change, so cache it per peer for a short window (cleared in OnSceneChanged).
        private struct SceneVerdict { public bool Ok; public float Until; }
        private static readonly Dictionary<int, SceneVerdict> _peerSceneOk = new Dictionary<int, SceneVerdict>();
        private const float SceneVerdictSeconds = 0.5f;

        static bool PeerSameScene(LanNetworkManager net, int pid)
        {
            float now = Time.unscaledTime;
            SceneVerdict v;
            if (_peerSceneOk.TryGetValue(pid, out v) && now < v.Until) return v.Ok;
            v = new SceneVerdict { Ok = net.PeerInHostScene(pid), Until = now + SceneVerdictSeconds };
            _peerSceneOk[pid] = v;
            return v.Ok;
        }

        static bool CollectTargets(LanNetworkManager net)
        {
            _targets.Clear();
            var ids = net.GetRemotePlayerIds();
            if (ids.Length == 0) return false;
            var pm = net.ProxyManager;
            if (pm == null) return false;
            for (int i = 0; i < ids.Length; i++)
            {
                int pid = ids[i];
                if (!PeerSameScene(net, pid)) continue;
                if (PartyVitals.IsDown(pid)) continue; // downed peers are not targets
                var proxy = pm.GetProxy(pid);
                if (proxy == null || proxy.GameObject == null || proxy.LastDead) continue;
                _targets.Add(new Target { Pid = pid, Pos = proxy.GameObject.transform.position });
            }
            return _targets.Count > 0;
        }

        // --- Weapon / hazard hurtboxes (enemy weapon windows, boss attacks, environment) -------------

        /// <summary>
        /// Host: every active non-player Hurtbox overlapping a remote proxy hurts that peer once on
        /// contact and then on the native pulse interval while it stays inside (Hurtbox.OnTriggerEnter /
        /// OnTriggerStay, Ghidra Hurtbox.c). Window state is the hurtbox GO itself, so enemy swing
        /// timing (Hit/EndHit toggling WeaponHurtboxObject) and boss attack phases are honoured.
        /// </summary>
        public static void TickHurtboxes(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            // Solo / lone host: no remote peers, so nothing to hurt — no scans, no list refresh.
            if (net.GetPlayerCount() <= 1)
            {
                if (_inside.Count > 0) _inside.Clear();
                return;
            }
            if (!CollectTargets(net))
            {
                if (_inside.Count > 0) _inside.Clear();
                return;
            }

            RefreshHurtboxes();
            var all = _hurtboxes;
            if (all.Count == 0) return;

            float now = Time.time;
            _seen.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                var hb = all[i];
                // Destroyed since the scan (projectile, despawn): Unity's null check, no interop call.
                if (hb.H == null || hb.Go == null || hb.Col == null) continue;
                if (!hb.H.enabled || !hb.Go.activeInHierarchy || !hb.Col.enabled) continue;
                // Once per hurtbox, not once per target.
                Bounds b = hb.Col.bounds;
                b.Expand(0.4f);

                for (int t = 0; t < _targets.Count; t++)
                {
                    var tg = _targets[t];
                    if (!Overlaps(hb.Col, b, hb.NoClosest, hb.Hid, tg.Pos)) continue;

                    long key = (long)hb.Hid * 256L + tg.Pid;
                    _seen.Add(key);
                    float next;
                    bool wasInside = _inside.TryGetValue(key, out next);
                    if (wasInside && now < next) continue;

                    _inside[key] = now + hb.Pulse;
                    net.SendEnemyDamage(tg.Pid, 0, hb.Damage, true);
                    PlaytestLog.Verbose("Damage", "hurtbox " + hb.Go.name + " dmg=" + hb.Damage + " -> p" + tg.Pid);
                }
            }

            // Left the volume (or it switched off): re-entry counts as a fresh hit.
            if (_inside.Count > 0)
            {
                _stale.Clear();
                foreach (var kvp in _inside)
                {
                    if (!_seen.Contains(kvp.Key)) _stale.Add(kvp.Key);
                }
                for (int i = 0; i < _stale.Count; i++)
                    _inside.Remove(_stale[i]);
            }
        }

        static bool Overlaps(Collider col, Bounds b, bool noClosest, int hid, Vector3 feet)
        {
            // Proxy root sits at the feet; also test roughly chest height so torso-level volumes connect.
            // SIGNALIS walks the XY plane: up is -Z.
            var mid = feet + new Vector3(0f, 0f, -0.9f);
            if (!b.Contains(mid) && !b.Contains(feet)) return false;
            if (noClosest) return true;
            try
            {
                var cp = col.ClosestPoint(mid);
                if ((cp - mid).sqrMagnitude <= 0.6f * 0.6f) return true;
                var cf = col.ClosestPoint(feet);
                return (cf - feet).sqrMagnitude <= 0.6f * 0.6f;
            }
            catch (System.Exception ex)
            {
                // Unexpected collider type: fall back to its bounds from now on.
                WarnOnce("ClosestPoint", ex);
                _noClosestPoint.Add(hid);
                return true;
            }
        }

        // --- Weapon-less enemy melee (EnemyController.Hit direct branch) ---------------------------

        static Transform _hitScratch;

        /// <summary>
        /// Native Hit does playerPos.position = Elster.position before measuring (Ghidra EnemyController.c).
        /// On the host e.playerPos is the nearest remote proxy, which would be teleported onto the host for
        /// a frame. Returns a scratch transform to stand in for playerPos during Hit (null = leave it).
        /// </summary>
        internal static Transform HitScratchFor(EnemyController e)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || e == null || net.GetPlayerCount() <= 1) return null; // lone host: playerPos is the Elster
            try
            {
                var pp = e.playerPos;
                if (pp == null || pp.gameObject == null) return null;
                var pm = net.ProxyManager;
                if (pm == null || pm.GetPlayerIdByGameObject(pp.gameObject) < 0) return null;
                if (_hitScratch == null)
                {
                    var go = new GameObject("SR_HitScratch");
                    Object.DontDestroyOnLoad(go);
                    _hitScratch = go.transform;
                }
                return _hitScratch;
            }
            catch (System.Exception ex) { WarnOnce("hit scratch", ex); return null; }
        }

        /// <summary>
        /// Host, EnemyController.Hit Prefix. Enemies with no WeaponHurtbox hurt the player straight from
        /// Hit(): attackRange + attackAngle + staggerType==none -> HurtElster (Ghidra EnemyController.c).
        /// Apply the same test to each remote proxy at the animation event instead of a fixed timer.
        /// Weapon-hurtbox enemies are covered by <see cref="TickHurtboxes"/>.
        /// </summary>
        public static void OnEnemyHit(EnemyController e)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            if (e == null || net.GetPlayerCount() <= 1) return; // lone host: nothing but native Hit
            // Runs in the EnemyController.Hit prefix: a failure here must never stop the native swing.
            try
            {
                if (e.WeaponHurtbox != null || e.state == EnemyController.enemystate.dead
                    || e.staggerType != EnemyController.hurtState.none) return;
                if (!CollectTargets(net)) return;

                float range = 2.5f, angle = 0f, dmg = 20f;
                var preset = e.Preset;
                if (preset != null)
                {
                    if (preset.attackRange > 0f) range = preset.attackRange;
                    angle = preset.attackAngle;
                    dmg = preset.damage;
                }
                // Native Hit: whiffs when attackAngle <= angle, so an attackAngle of 0 never hits the player.
                if (angle <= 0f) return;

                var et = e.transform;
                var ep = et.position;
                var fwd = et.forward;
                WorldRegistry.TryGetEnemyId(e, out ulong id);
                // Enemies without a registered WorldId must not share one swing-time slot.
                long baseKey = id != 0 ? unchecked((long)id) : -(long)e.GetInstanceID();
                float now = Time.time;
                for (int i = 0; i < _targets.Count; i++)
                {
                    var tg = _targets[i];
                    var d3 = tg.Pos - ep;
                    // Native distance is Vector2.Distance(transform.position, playerPos.position) on (x, y).
                    float dist = Mathf.Sqrt(d3.x * d3.x + d3.y * d3.y);
                    if (dist > range + RangeSlack) continue;
                    // Native angle is Vector3.Angle(forward, toPlayer) in 3D.
                    if (dist > 0.1f && fwd.sqrMagnitude > 0.0001f && Vector3.Angle(fwd, d3) >= angle + AngleSlack) continue;
                    long key = unchecked(baseKey * 256L + tg.Pid);
                    if (_swingTime.TryGetValue(key, out float last) && now - last < MinSwingGap) continue;
                    _swingTime[key] = now;
                    net.SendEnemyDamage(tg.Pid, id, dmg, true);
                    PlaytestLog.Verbose("Damage", "swing " + et.name + " dmg=" + dmg.ToString("F0") + " -> p" + tg.Pid);
                }
            }
            catch (System.Exception ex) { WarnOnce("enemy hit", ex); }
        }
    }
}
