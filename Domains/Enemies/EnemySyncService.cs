// Host-authoritative enemy AI + snapshots via WorldId (never GetInstanceID).
using System;
using System.Collections.Generic;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class EnemySyncService
    {
        private float _sendTimer;
        private bool _forceSend;
        private readonly HashSet<ulong> _clientPuppeted = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> _lastAttackTime = new Dictionary<ulong, float>();
        private int _mapMisses;
        private int _mapHits;
        private float _lastDiag;

        private readonly List<EnemySnapshotNet> _snapList = new List<EnemySnapshotNet>(32);
        private BasicEnemy[] _basics = Array.Empty<BasicEnemy>();
        private EnemyCookBase[] _cooks = Array.Empty<EnemyCookBase>();
        private bool _altAiCached;

        public void RequestFullSend() => _forceSend = true;

        public void TickHost(LanNetworkManager net)
        {
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < PluginInfo.EntitySendInterval && !_forceSend) return;
            _sendTimer = 0f;
            _forceSend = false;

            float t0 = Time.realtimeSinceStartup;
            try
            {
            _snapList.Clear();
            var pm = net.ProxyManager;

            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;

                ulong id = kvp.Key;
                Transform et;
                try { et = e.transform; }
                catch { continue; }
                if (et == null) continue;

                Transform nearest = null;
                if (e.state != EnemyController.enemystate.dead)
                {
                    nearest = FindNearestTarget(et.position, net, pm);
                    if (nearest != null && !EnemyVisiblyInChunk(e))
                        WakeForCombat(e);
                }

                Vector3 pos;
                try { pos = et.position; }
                catch { continue; }
                var anim = e.animator;

                int hp = 100;
                int maxHp = 100;
                try
                {
                    if (e.Preset != null) maxHp = e.Preset.HP;
                    if (e.hitbox != null) hp = e.hitbox.HP;
                    else hp = maxHp;
                }
                catch { }

                int animHash = 0;
                float animTime = 0f;
                try
                {
                    if (anim != null)
                    {
                        var si = anim.GetCurrentAnimatorStateInfo(0);
                        animHash = si.fullPathHash;
                        animTime = si.normalizedTime;
                    }
                }
                catch { }

                float rotY = 0f;
                try { rotY = et.eulerAngles.y; } catch { }

                float velX = 0f, velY = 0f, velZ = 0f;
                try
                {
                    if (e.agent != null)
                    {
                        var v = e.agent.velocity;
                        velX = v.x; velY = v.y; velZ = v.z;
                    }
                }
                catch { }

                _snapList.Add(new EnemySnapshotNet
                {
                    WorldId = unchecked((long)id),
                    State = (byte)e.state,
                    HurtState = (byte)e.staggerType,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotY = rotY,
                    VelX = velX,
                    VelY = velY,
                    VelZ = velZ,
                    AnimHash = animHash,
                    AnimTime = animTime,
                    HP = hp,
                    MaxHP = maxHp,
                    Alive = e.state != EnemyController.enemystate.dead,
                    TargetPlayerId = -1
                });

                if (e.state != EnemyController.enemystate.dead && nearest != null)
                {
                    e.playerPos = nearest;
                    try
                    {
                        e.AimTarget = nearest;
                        e.IkTarget = nearest;
                    }
                    catch { }

                    int tid = -1;
                    try
                    {
                        tid = pm.GetPlayerIdByGameObject(nearest.gameObject);
                        if (tid < 0 && nearest.gameObject == net.GetLocalPlayer())
                            tid = net.LocalPlayerId;
                    }
                    catch { }
                    var snap = _snapList[_snapList.Count - 1];
                    snap.TargetPlayerId = (sbyte)Mathf.Clamp(tid, -1, 127);
                    _snapList[_snapList.Count - 1] = snap;
                }

                if (e.state == EnemyController.enemystate.attack && e.playerPos != null)
                {
                    try
                    {
                        int targetPid = pm.GetPlayerIdByGameObject(e.playerPos.gameObject);
                        if (targetPid < 0 && e.playerPos.gameObject == net.GetLocalPlayer())
                            targetPid = net.LocalPlayerId;

                        if (targetPid >= 0 && targetPid != net.LocalPlayerId)
                        {
                            float now = Time.time;
                            float lastAtk;
                            _lastAttackTime.TryGetValue(id, out lastAtk);
                            float cooldown = e.attackCooldown > 0f ? e.attackCooldown : 1.5f;
                            if (now - lastAtk >= cooldown)
                            {
                                _lastAttackTime[id] = now;
                                float dmg = e.Preset != null ? e.Preset.damage : 20f;
                                net.SendEnemyDamage(targetPid, id, dmg, true);
                            }
                        }
                    }
                    catch { }
                }
            }

            if (_snapList.Count > 0)
                net.SendEnemyState(_snapList);

            RetargetAltAi(net, pm);
            }
            finally
            {
                HitchTrace.Cost("enemy", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        void EnsureAltAiCache()
        {
            if (_altAiCached) return;
            _basics = WorldLookup.All<BasicEnemy>() ?? Array.Empty<BasicEnemy>();
            _cooks = WorldLookup.All<EnemyCookBase>() ?? Array.Empty<EnemyCookBase>();
            _altAiCached = true;
        }

        private void RetargetAltAi(LanNetworkManager net, PlayerProxyManager pm)
        {
            EnsureAltAiCache();

            try
            {
                var basics = _basics;
                if (basics != null)
                {
                    for (int i = 0; i < basics.Length; i++)
                    {
                        var b = basics[i];
                        if (b == null) continue;
                        Transform bt;
                        try { bt = b.transform; }
                        catch { continue; }
                        if (bt == null) continue;
                        var n = FindNearestStatic(bt.position, net, pm);
                        if (n != null) b.target = n;
                    }
                }
            }
            catch { }

            try
            {
                var cooks = _cooks;
                if (cooks != null)
                {
                    for (int i = 0; i < cooks.Length; i++)
                    {
                        var c = cooks[i];
                        if (c == null) continue;
                        Transform ct;
                        try { ct = c.transform; }
                        catch { continue; }
                        if (ct == null) continue;
                        var n = FindNearestStatic(ct.position, net, pm);
                        if (n != null) c.target = n;
                    }
                }
            }
            catch { }
        }

        private static Transform FindNearestStatic(Vector3 fromPos, LanNetworkManager net, PlayerProxyManager pm)
        {
            Transform best = null;
            float bestDist = 40f * 40f;
            var localPlayer = net.GetLocalPlayer();
            if (localPlayer != null)
            {
                float d = (localPlayer.transform.position - fromPos).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = localPlayer.transform; }
            }
            foreach (int pid in net.GetRemotePlayerIds())
            {
                var proxy = pm.GetProxy(pid);
                if (proxy == null || proxy.GameObject == null) continue;
                float d = (proxy.GameObject.transform.position - fromPos).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = proxy.GameObject.transform; }
            }
            return best;
        }

        public void OnEnemyStateReceived(EnemyStateMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;
            if (msg.Enemies == null) return;

            for (int i = 0; i < msg.Enemies.Length; i++)
                ApplyEnemyState(msg.Enemies[i]);

            if (_mapMisses > 0 && Time.time - _lastDiag > 5f)
            {
                PlaytestLog.Event("Enemy", "map hits=" + _mapHits + " misses=" + _mapMisses
                    + " puppets=" + _clientPuppeted.Count);
                _lastDiag = Time.time;
                _mapHits = 0;
                _mapMisses = 0;
            }
        }

        private void ApplyEnemyState(EnemySnapshotNet snap)
        {
            ulong id = unchecked((ulong)snap.WorldId);
            if (id == 0) return;

            EnemyController enemy;
            if (!WorldRegistry.TryGetEnemy(id, out enemy) || enemy == null)
            {
                _mapMisses++;
                return;
            }
            _mapHits++;

            try
            {
                // Unity fake-null: destroyed GOs compare equal to null.
                if (enemy == null || enemy.gameObject == null) return;

                // Puppet only after successful map
                if (!_clientPuppeted.Contains(id))
                {
                    Puppet(enemy);
                    _clientPuppeted.Add(id);
                }

                // Skip pose snaps for off-chunk enemies so they do not pop through walls.
                if (!EnemyVisiblyInChunk(enemy))
                {
                    if (enemy == null) return;
                    try { enemy.state = (EnemyController.enemystate)snap.State; } catch { }
                    try { enemy.staggerType = (EnemyController.hurtState)snap.HurtState; } catch { }
                    try
                    {
                        if (enemy != null && enemy.hitbox != null)
                            enemy.hitbox.HP = snap.HP;
                    }
                    catch { }
                    return;
                }

                if (enemy == null || enemy.gameObject == null) return;
                var t = enemy.transform;
                if (t == null) return;
                try
                {
                    t.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
                    var rot = t.eulerAngles;
                    rot.y = snap.RotY;
                    t.eulerAngles = rot;
                }
                catch { return; }

                try { enemy.state = (EnemyController.enemystate)snap.State; } catch { }
                // Decompile EnemyController.staggerType (hurtState) — was on wire unused.
                try { enemy.staggerType = (EnemyController.hurtState)snap.HurtState; } catch { }

                try
                {
                    if (enemy.hitbox != null)
                        enemy.hitbox.HP = snap.HP;
                }
                catch { }
                try
                {
                    if (enemy.debugHP != null)
                        enemy.debugHP.text = snap.HP + "/" + snap.MaxHP;
                }
                catch { }

                var anim = enemy.animator;
                if (anim != null && snap.AnimHash != 0)
                {
                    try
                    {
                        var stateInfo = anim.GetCurrentAnimatorStateInfo(0);
                        if (stateInfo.fullPathHash != snap.AnimHash
                            || Mathf.Abs(stateInfo.normalizedTime - snap.AnimTime) > 0.15f)
                        {
                            anim.Play(snap.AnimHash, 0, snap.AnimTime);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool EnemyVisiblyInChunk(EnemyController enemy)
        {
            // Avoid GetComponentsInChildren every tick (main-thread cliff).
            // Inactive hierarchy ≈ sleeping chunk; renderer-only hide is rare.
            try
            {
                return enemy != null
                    && enemy.gameObject != null
                    && enemy.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        /// <summary>Host applies damage from a remote player (legacy float dmg or native TakeDamage).</summary>
        public bool ApplyDamageOnHost(ulong enemyId, float damage)
        {
            return ApplyLegacyHpDamage(enemyId, damage);
        }

        /// <summary>Preferred: call real EnemyController.TakeDamage chances (from PlayerAttack).</summary>
        public bool ApplyNativeTakeDamageOnHost(ulong enemyId, float fire, float crit, float hurt, bool noSneak)
        {
            EnemyController enemy;
            if (!WorldRegistry.TryGetEnemy(enemyId, out enemy) || enemy == null
                || enemy.gameObject == null)
            {
                PlaytestLog.Event("Enemy", "TakeDamage MISS id=" + enemyId.ToString("X16"));
                return false;
            }

            try
            {
                WakeForCombat(enemy);
                if (enemy == null || enemy.gameObject == null) return false;

                enemy.TakeDamage(fire, crit, hurt, noSneak);
                _forceSend = true;

                PlaytestLog.Event("Enemy", "TakeDamage id=" + enemyId.ToString("X16")
                    + " fire=" + fire.ToString("F2")
                    + " crit=" + crit.ToString("F2")
                    + " hurt=" + hurt.ToString("F2")
                    + " hp=" + (enemy.hitbox != null ? enemy.hitbox.HP.ToString() : "?")
                    + " state=" + enemy.state);
                return true;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[EnemySync] TakeDamage failed: " + ex.Message);
                try { enemy.TakeDamage(); return true; }
                catch { return false; }
            }
        }

        private bool ApplyLegacyHpDamage(ulong enemyId, float damage)
        {
            EnemyController enemy;
            if (!WorldRegistry.TryGetEnemy(enemyId, out enemy) || enemy == null
                || enemy.gameObject == null)
            {
                PlaytestLog.Event("Enemy", "legacy dmg MISS id=" + enemyId.ToString("X16"));
                return false;
            }
            try
            {
                WakeForCombat(enemy);
                if (enemy == null || enemy.gameObject == null) return false;
                if (enemy.hitbox != null)
                {
                    enemy.hitbox.HP -= (int)damage;
                    if (enemy.hitbox.HP <= 0)
                        enemy.state = EnemyController.enemystate.dead;
                }
                else
                    enemy.TakeDamage();
                _forceSend = true;
                return true;
            }
            catch { return false; }
        }

        private Transform FindNearestTarget(Vector3 fromPos, LanNetworkManager net, PlayerProxyManager pm)
        {
            Transform best = null;
            float bestDist = 30f * 30f;

            var localPlayer = net.GetLocalPlayer();
            if (localPlayer != null)
            {
                float d = (localPlayer.transform.position - fromPos).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = localPlayer.transform;
                }
            }

            foreach (int pid in net.GetRemotePlayerIds())
            {
                var proxy = pm.GetProxy(pid);
                if (proxy == null || proxy.GameObject == null) continue;
                float d = (proxy.GameObject.transform.position - fromPos).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = proxy.GameObject.transform;
                }
            }

            return best;
        }

        static void Puppet(EnemyController enemy)
        {
            if (enemy == null) return;
            try { enemy.enabled = false; } catch { }
            try { if (enemy.agent != null) enemy.agent.enabled = false; } catch { }
        }

        /// <summary>
        /// Host: sleeping-chunk enemies have no AI and TakeDamage no-ops.
        /// Wake the parent Room chain when a peer is in that room or a hit arrives.
        /// </summary>
        public static void WakeForCombat(EnemyController enemy)
        {
            if (enemy == null || enemy.gameObject == null) return;
            try
            {
                Transform t = enemy.transform;
                while (t != null)
                {
                    if (!t.gameObject.activeSelf)
                        t.gameObject.SetActive(true);
                    if (t.GetComponent<Room>() != null) break;
                    t = t.parent;
                }
                enemy.enabled = true;
                if (enemy.agent != null) enemy.agent.enabled = true;
                enemy.WakeUp();
            }
            catch { }
        }

        public void PuppetAllNow()
        {
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                ulong id = kvp.Key;
                if (_clientPuppeted.Contains(id)) continue;
                Puppet(e);
                _clientPuppeted.Add(id);
            }
        }

        public void OnSceneChanged()
        {
            _clientPuppeted.Clear();
            _lastAttackTime.Clear();
            _mapHits = 0;
            _mapMisses = 0;
            _sendTimer = 0f;
            _snapList.Clear();
            _altAiCached = false;
            _basics = Array.Empty<BasicEnemy>();
            _cooks = Array.Empty<EnemyCookBase>();
        }

        public void Reset()
        {
            OnSceneChanged();
        }

        // PuzzleStateMessage EnemyManagerState — WorldRegistry scan (never FindObjectsOfType).
        internal static bool TryReadPuzzle(EnemyManager x, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            if (x == null) return false;
            bool combat = EnemyManager.inCombat;
            if (!combat)
            {
                foreach (var kvp in WorldRegistry.AllEnemies())
                {
                    var en = kvp.Value;
                    if (en == null) continue;
                    try
                    {
                        if (en.state == EnemyController.enemystate.attack)
                        {
                            combat = true;
                            break;
                        }
                    }
                    catch { }
                }
            }
            int bits = 0;
            if (combat) bits |= 1;
            if (EnemyManager.enemyPresence) bits |= 2;
            entry = PuzzleDomainUtil.Mk(
                PuzzleType.EnemyManagerState, wid,
                x.cleared, x.inOperation, false, bits, 0, 0, 0, 0);
            return true;
        }

        /// <summary>Host-only global (WorldId 0).</summary>
        internal static PuzzleStateEntry ReadGlobalAlert()
        {
            int alarmVal = 0;
            try { alarmVal = (int)GlobalAlertStatus.currentStatus; } catch { }
            return PuzzleDomainUtil.Mk(
                PuzzleType.GlobalAlertStatus, 0, false, false, false, alarmVal, 0, 0, 0, 0f);
        }

        internal static void ApplyGlobalAlert(PuzzleStateEntry e)
        {
            GlobalAlertStatus.currentStatus = (GlobalAlertStatus.alarm)e.Int0;
        }

        internal static void ApplyPuzzle(EnemyManager x, PuzzleStateEntry e)
        {
            if (x != null) { x.cleared = e.Bool0; x.inOperation = e.Bool1; }
            EnemyManager.inCombat = (e.Int0 & 1) != 0;
            EnemyManager.enemyPresence = (e.Int0 & 2) != 0;
        }
    }
}
