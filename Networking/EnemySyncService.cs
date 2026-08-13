// Host-authoritative enemy AI + snapshots via WorldId (never GetInstanceID).
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

        public void RequestFullSend() => _forceSend = true;

        public void TickHost(LanNetworkManager net)
        {
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < PluginInfo.EntitySendInterval && !_forceSend) return;
            _sendTimer = 0f;
            _forceSend = false;

            var list = new List<EnemySnapshotNet>(32);
            var pm = net.ProxyManager;

            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;

                ulong id = kvp.Key;
                var pos = e.transform.position;
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

                list.Add(new EnemySnapshotNet
                {
                    WorldId = unchecked((long)id),
                    State = (byte)e.state,
                    HurtState = (byte)e.staggerType,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotY = e.transform.eulerAngles.y,
                    VelX = e.agent != null ? e.agent.velocity.x : 0f,
                    VelY = e.agent != null ? e.agent.velocity.y : 0f,
                    VelZ = e.agent != null ? e.agent.velocity.z : 0f,
                    AnimHash = animHash,
                    AnimTime = animTime,
                    HP = hp,
                    MaxHP = maxHp,
                    Alive = e.state != EnemyController.enemystate.dead,
                    TargetPlayerId = -1
                });

                if (e.state != EnemyController.enemystate.dead)
                {
                    Transform nearest = FindNearestTarget(e.transform.position, net, pm);
                    if (nearest != null)
                    {
                        e.playerPos = nearest;
                        try
                        {
                            e.AimTarget = nearest;
                            e.IkTarget = nearest;
                        }
                        catch { }

                        int tid = pm.GetPlayerIdByGameObject(nearest.gameObject);
                        if (tid < 0 && nearest.gameObject == net.GetLocalPlayer())
                            tid = net.LocalPlayerId;
                        var snap = list[list.Count - 1];
                        snap.TargetPlayerId = (sbyte)Mathf.Clamp(tid, -1, 127);
                        list[list.Count - 1] = snap;
                    }
                }

                if (e.state == EnemyController.enemystate.attack && e.playerPos != null)
                {
                    int targetPid = pm.GetPlayerIdByGameObject(e.playerPos.gameObject);
                    if (targetPid < 0 && e.playerPos.gameObject == net.GetLocalPlayer())
                        targetPid = net.LocalPlayerId;

                    if (targetPid >= 0)
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
            }

            if (list.Count > 0)
                net.SendEnemyState(list.ToArray());

            RetargetAltAi(net, pm);
        }

        private static void RetargetAltAi(LanNetworkManager net, PlayerProxyManager pm)
        {
            try
            {
                var bosses = Object.FindObjectsOfType<END_Boss>();
                if (bosses != null)
                {
                    for (int i = 0; i < bosses.Length; i++)
                    {
                        var b = bosses[i];
                        if (b == null) continue;
                        Transform n = null;
                        // reuse nearest vs boss position
                        n = FindNearestStatic(b.transform.position, net, pm);
                        if (n == null) continue;
                        try { b.Elster = n; } catch { }
                        try { b.Target = n; } catch { }
                    }
                }
            }
            catch { }

            try
            {
                var adlers = Object.FindObjectsOfType<BOS_Adler>();
                if (adlers != null)
                {
                    for (int i = 0; i < adlers.Length; i++)
                    {
                        var a = adlers[i];
                        if (a == null) continue;
                        var n = FindNearestStatic(a.transform.position, net, pm);
                        if (n != null) { try { a.Elster = n; } catch { } }
                    }
                }
            }
            catch { }

            try
            {
                var basics = Object.FindObjectsOfType<BasicEnemy>();
                if (basics != null)
                {
                    for (int i = 0; i < basics.Length; i++)
                    {
                        var b = basics[i];
                        if (b == null) continue;
                        var n = FindNearestStatic(b.transform.position, net, pm);
                        if (n != null) b.target = n;
                    }
                }
            }
            catch { }

            try
            {
                var cooks = Object.FindObjectsOfType<EnemyCookBase>();
                if (cooks != null)
                {
                    for (int i = 0; i < cooks.Length; i++)
                    {
                        var c = cooks[i];
                        if (c == null) continue;
                        var n = FindNearestStatic(c.transform.position, net, pm);
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
            if (msg.Enemies == null) return;

            for (int i = 0; i < msg.Enemies.Length; i++)
                ApplyEnemyState(msg.Enemies[i]);

            if (Time.time - _lastDiag > 5f)
            {
                ModRuntime.Log?.Msg("[EnemySync] map hits=" + _mapHits + " misses=" + _mapMisses
                    + " puppets=" + _clientPuppeted.Count);
                _lastDiag = Time.time;
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

            // Puppet only after successful map
            if (!_clientPuppeted.Contains(id))
            {
                try
                {
                    enemy.enabled = false;
                    if (enemy.agent != null) enemy.agent.enabled = false;
                }
                catch { }
                _clientPuppeted.Add(id);
            }

            // Skip pose snaps for off-chunk enemies so they do not pop through walls.
            if (!EnemyVisiblyInChunk(enemy))
            {
                try { enemy.state = (EnemyController.enemystate)snap.State; } catch { }
                if (enemy.hitbox != null)
                    enemy.hitbox.HP = snap.HP;
                return;
            }

            enemy.transform.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
            var rot = enemy.transform.eulerAngles;
            rot.y = snap.RotY;
            enemy.transform.eulerAngles = rot;

            try { enemy.state = (EnemyController.enemystate)snap.State; } catch { }

            if (enemy.hitbox != null)
                enemy.hitbox.HP = snap.HP;
            if (enemy.debugHP != null)
                enemy.debugHP.text = snap.HP + "/" + snap.MaxHP;

            if (enemy.animator != null && snap.AnimHash != 0)
            {
                try
                {
                    var stateInfo = enemy.animator.GetCurrentAnimatorStateInfo(0);
                    if (stateInfo.fullPathHash != snap.AnimHash
                        || Mathf.Abs(stateInfo.normalizedTime - snap.AnimTime) > 0.15f)
                    {
                        enemy.animator.Play(snap.AnimHash, 0, snap.AnimTime);
                    }
                }
                catch { }
            }
        }

        private static bool EnemyVisiblyInChunk(EnemyController enemy)
        {
            if (enemy == null || enemy.gameObject == null) return false;
            if (!enemy.gameObject.activeInHierarchy) return false;
            try
            {
                var rs = enemy.GetComponentsInChildren<Renderer>(true);
                if (rs == null || rs.Length == 0) return true;
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] != null && rs[i].enabled && rs[i].gameObject.activeInHierarchy)
                        return true;
                }
                return false;
            }
            catch { return true; }
        }

        /// <summary>Host applies damage from a remote player (legacy float dmg or native TakeDamage).</summary>
        public bool ApplyDamageOnHost(ulong enemyId, float damage)
        {
            return ApplyNativeTakeDamageOnHost(enemyId, 0f, 0f, 1f, false)
                || ApplyLegacyHpDamage(enemyId, damage);
        }

        /// <summary>Preferred: call real EnemyController.TakeDamage chances (from PlayerAttack).</summary>
        public bool ApplyNativeTakeDamageOnHost(ulong enemyId, float fire, float crit, float hurt, bool noSneak)
        {
            EnemyController enemy;
            if (!WorldRegistry.TryGetEnemy(enemyId, out enemy) || enemy == null)
                return false;

            try
            {
                // Re-enable briefly if we had puppeted (host should never puppet its own AI).
                if (!enemy.enabled) enemy.enabled = true;
                if (enemy.agent != null && !enemy.agent.enabled) enemy.agent.enabled = true;

                enemy.TakeDamage(fire, crit, hurt, noSneak);
                _forceSend = true;

                ModRuntime.Log?.Msg("[EnemySync] Native TakeDamage id=" + enemyId.ToString("X16")
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
            if (!WorldRegistry.TryGetEnemy(enemyId, out enemy) || enemy == null)
                return false;
            try
            {
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

        public void PuppetAllNow()
        {
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                ulong id = kvp.Key;
                if (_clientPuppeted.Contains(id)) continue;
                try
                {
                    e.enabled = false;
                    if (e.agent != null) e.agent.enabled = false;
                }
                catch { }
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
        }

        public void Reset()
        {
            OnSceneChanged();
        }
    }
}
