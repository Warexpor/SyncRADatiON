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
            _inside.Clear();
            _seen.Clear();
            _colliders.Clear();
            _noClosestPoint.Clear();
            _swingTime.Clear();
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
                if (!net.PeerInHostScene(pid)) continue;
                if (PartyVitals.IsDown(pid)) continue; // downed peers are not targets (0.5.57)
                var proxy = pm.GetProxy(pid);
                if (proxy == null || proxy.GameObject == null || proxy.LastDead) continue;
                try
                {
                    _targets.Add(new Target { Pid = pid, Pos = proxy.GameObject.transform.position });
                }
                catch (System.Exception ex) { WarnOnce("proxy position", ex); }
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
            if (!CollectTargets(net))
            {
                if (_inside.Count > 0) _inside.Clear();
                return;
            }

            var all = WorldLookup.All<Hurtbox>();
            if (all == null || all.Length == 0) return;

            float now = Time.time;
            _seen.Clear();
            for (int i = 0; i < all.Length; i++)
            {
                var h = all[i];
                if (h == null) continue;
                GameObject go;
                int hid;
                try
                {
                    go = h.gameObject;
                    if (go == null || !go.activeInHierarchy || !h.enabled) continue;
                    // Player melee/stomp boxes (canDamageEnemies) are the local Elster's weapons.
                    if (h.canDamageEnemies) continue;
                    if (h.damage <= 0) continue;
                    hid = h.GetInstanceID();
                }
                catch { continue; }

                Collider col;
                if (!_colliders.TryGetValue(hid, out col) || col == null)
                {
                    try { col = go.GetComponent<Collider>(); }
                catch (System.Exception ex) { WarnOnce("hurtbox collider", ex); col = null; }
                    _colliders[hid] = col;
                }
                if (col == null) continue;
                try { if (!col.enabled) continue; } catch { continue; }

                for (int t = 0; t < _targets.Count; t++)
                {
                    var tg = _targets[t];
                    if (!Overlaps(col, hid, tg.Pos)) continue;

                    long key = (long)hid * 256L + tg.Pid;
                    _seen.Add(key);
                    float next;
                    bool wasInside = _inside.TryGetValue(key, out next);
                    if (wasInside && now < next) continue;

                    float pulse = 2f;
                    int dmg = 0;
                    try
                    {
                        pulse = h.pulseTime > 0.25f ? h.pulseTime : 0.5f;
                        dmg = h.damage;
                    }
                    catch (System.Exception ex) { WarnOnce("hurtbox fields", ex); }
                    _inside[key] = now + pulse;
                    net.SendEnemyDamage(tg.Pid, 0, dmg, true);
                    PlaytestLog.Verbose("Damage", "hurtbox " + go.name + " dmg=" + dmg + " -> p" + tg.Pid);
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

        static bool Overlaps(Collider col, int hid, Vector3 feet)
        {
            try
            {
                // Proxy root sits at the feet; test roughly chest height so low/flat volumes still connect.
                var mid = feet + new Vector3(0f, 0.9f, 0f);
                var b = col.bounds;
                b.Expand(0.4f);
                if (!b.Contains(mid) && !b.Contains(feet)) return false;
                if (_noClosestPoint.Contains(hid)) return true;
                try
                {
                    var cp = col.ClosestPoint(mid);
                    if ((cp - mid).sqrMagnitude <= 0.6f * 0.6f) return true;
                    var cf = col.ClosestPoint(feet);
                    return (cf - feet).sqrMagnitude <= 0.6f * 0.6f;
                }
                catch
                {
                    // Non-convex MeshCollider cannot answer ClosestPoint: fall back to its bounds.
                    _noClosestPoint.Add(hid);
                    return true;
                }
            }
            catch { return false; }
        }

        // --- Weapon-less enemy melee (EnemyController.Hit direct branch) ---------------------------

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
            if (e == null) return;
            try
            {
                if (e.WeaponHurtbox != null) return;
                if (e.state == EnemyController.enemystate.dead) return;
                if (e.staggerType != EnemyController.hurtState.none) return;
            }
            catch { return; }

            if (!CollectTargets(net)) return;

            float range = 2.5f;
            float angle = 0f;
            float dmg = 20f;
            try
            {
                var preset = e.Preset;
                if (preset != null)
                {
                    if (preset.attackRange > 0f) range = preset.attackRange;
                    angle = preset.attackAngle;
                    dmg = preset.damage;
                }
            }
            catch (System.Exception ex) { WarnOnce("enemy preset", ex); }

            Transform et;
            try { et = e.transform; } catch { return; }
            if (et == null) return;
            var ep = et.position;
            var fwd = et.forward;
            fwd.y = 0f;

            ulong id;
            WorldRegistry.TryGetEnemyId(e, out id);
            float now = Time.time;
            for (int i = 0; i < _targets.Count; i++)
            {
                var tg = _targets[i];
                var d = tg.Pos - ep;
                if (Mathf.Abs(d.y) > 3f) continue;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist > range + RangeSlack) continue;
                if (angle > 0f && dist > 0.1f && fwd.sqrMagnitude > 0.0001f)
                {
                    if (Vector3.Angle(fwd, d) > angle + AngleSlack) continue;
                }
                long key = (long)id * 256L + tg.Pid;
                float last;
                if (_swingTime.TryGetValue(key, out last) && now - last < MinSwingGap) continue;
                _swingTime[key] = now;
                net.SendEnemyDamage(tg.Pid, id, dmg, true);
                PlaytestLog.Verbose("Damage", "swing " + et.name + " dmg=" + dmg.ToString("F0") + " -> p" + tg.Pid);
            }
        }
    }
}
