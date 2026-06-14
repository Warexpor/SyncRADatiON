// SyncRADation � host-authoritative enemy AI+snapshot (15Hz), client disable+apply, aggro on nearest player/proxy
using System.Collections.Generic;
using SyncRADation.Players;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class EnemySyncService
    {
        private float _sendTimer;
        private bool _clientAiDisabled;

        // Client-side: host InstanceID → local EnemyController lookup
        private readonly Dictionary<int, EnemyController> _hostToLocal = new Dictionary<int, EnemyController>();

        // Host-side: last known attack state per enemy for damage detection
        private readonly Dictionary<int, float> _lastAttackTime = new Dictionary<int, float>();

        private const float SendInterval = 1f / 15f;
        private const float AggroRadius = 30f;

        public void TickHost(LanNetworkManager net)
        {
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < SendInterval) return;
            _sendTimer = 0f;

            var enemies = GameObject.FindObjectsOfType<EnemyController>();
            if (enemies == null || enemies.Length == 0) return;

            int count = enemies.Length;
            var snaps = new EnemySnapshotNet[count];
            var pm = net.ProxyManager;

            for (int i = 0; i < count; i++)
            {
                var e = enemies[i];
                if (e == null) continue;

                int instanceID = e.gameObject.GetInstanceID();
                var pos = e.transform.position;
                var anim = e.animator;

                snaps[i] = new EnemySnapshotNet
                {
                    Index = (short)i,
                    State = (byte)e.state,
                    HurtState = (byte)e.staggerType,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotY = e.transform.eulerAngles.y,
                    VelX = e.agent != null ? e.agent.velocity.x : 0f,
                    VelY = e.agent != null ? e.agent.velocity.y : 0f,
                    VelZ = e.agent != null ? e.agent.velocity.z : 0f,
                    AnimHash = anim != null ? anim.GetCurrentAnimatorStateInfo(0).shortNameHash : 0,
                    AnimTime = anim != null ? anim.GetCurrentAnimatorStateInfo(0).normalizedTime : 0f,
                    HP = e.hitbox != null ? e.hitbox.HP : (e.Preset != null ? e.Preset.HP : 100),
                    MaxHP = e.Preset != null ? e.Preset.HP : 100,
                    HostInstanceID = instanceID,
                    Alive = e.state != EnemyController.enemystate.dead
                };

                // Override playerPos for aggro on nearest player/proxy
                if (e.state == EnemyController.enemystate.pursuit || e.state == EnemyController.enemystate.attack)
                {
                    Transform nearest = FindNearestTarget(e.transform.position, net, pm);
                    if (nearest != null)
                        e.playerPos = nearest;
                }

                // Detect enemy attacking proxy → send damage
                if (e.state == EnemyController.enemystate.attack && e.playerPos != null)
                {
                    int targetPid = pm.GetPlayerIdByGameObject(e.playerPos.gameObject);
                    if (targetPid >= 0)
                    {
                        float now = Time.time;
                        float lastAtk;
                        _lastAttackTime.TryGetValue(instanceID, out lastAtk);
                        float cooldown = e.attackCooldown > 0f ? e.attackCooldown : 1.5f;
                        if (now - lastAtk >= cooldown)
                        {
                            _lastAttackTime[instanceID] = now;
                            float dmg = e.Preset != null ? e.Preset.damage : 20f;
                            net.SendEnemyDamage(targetPid, instanceID, dmg, true);
                        }
                    }
                }
            }

            net.SendEnemyState(snaps);
        }

        public void TickClient(LanNetworkManager net)
        {
            if (net.Role == NetworkRole.Host) return;
            // Client-side is driven entirely by received messages
        }

        public void OnEnemyStateReceived(EnemyStateMessage msg)
        {
            if (!_clientAiDisabled)
            {
                DisableLocalAI();
                _clientAiDisabled = true;
            }

            for (int i = 0; i < msg.Enemies.Length; i++)
            {
                var snap = msg.Enemies[i];
                ApplyEnemyState(snap);
            }
        }

        private void ApplyEnemyState(EnemySnapshotNet snap)
        {
            EnemyController enemy;
            if (!_hostToLocal.TryGetValue(snap.HostInstanceID, out enemy) || enemy == null)
            {
                enemy = FindLocalEnemyByHostID(snap.HostInstanceID);
                if (enemy == null) return;
                _hostToLocal[snap.HostInstanceID] = enemy;
            }

            if (enemy == null) return;

            // Position/rotation
            enemy.transform.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
            var rot = enemy.transform.eulerAngles;
            rot.y = snap.RotY;
            enemy.transform.eulerAngles = rot;

            // State
            enemy.state = (EnemyController.enemystate)snap.State;

            // Velocity (AIBase.velocity is read-only; client AI is disabled so position is set via transform)

            // HP — apply to hitbox + debug text
            if (enemy.hitbox != null)
                enemy.hitbox.HP = snap.HP;
            if (enemy.debugHP != null)
                enemy.debugHP.text = snap.HP + "/" + snap.MaxHP;

            // Animator
            if (enemy.animator != null && snap.AnimHash != 0)
            {
                try
                {
                    var stateInfo = enemy.animator.GetCurrentAnimatorStateInfo(0);
                    if (stateInfo.shortNameHash != snap.AnimHash || Mathf.Abs(stateInfo.normalizedTime - snap.AnimTime) > 0.1f)
                    {
                        // Crossfade to sync animation state
                        foreach (var clip in enemy.animator.runtimeAnimatorController.animationClips)
                        {
                            int hash = Animator.StringToHash(clip.name);
                            if (hash == snap.AnimHash)
                            {
                                enemy.animator.Play(clip.name, 0, snap.AnimTime);
                                break;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private Transform FindNearestTarget(Vector3 fromPos, LanNetworkManager net, PlayerProxyManager pm)
        {
            Transform best = null;
            float bestDist = AggroRadius * AggroRadius;

            // Host player
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

            // Proxies
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

        private EnemyController FindLocalEnemyByHostID(int hostInstanceID)
        {
            var all = GameObject.FindObjectsOfType<EnemyController>();
            foreach (var e in all)
            {
                if (e != null && e.gameObject.GetInstanceID() == hostInstanceID)
                    return e;
            }
            // Fallback: match by name (relevant for scenes where InstanceIDs match)
            return null;
        }

        private static void DisableLocalAI()
        {
            var all = GameObject.FindObjectsOfType<EnemyController>();
            foreach (var e in all)
            {
                if (e != null)
                {
                    e.enabled = false;
                    if (e.agent != null) e.agent.enabled = false;
                }
            }
            ModRuntime.Log?.Msg("[EnemySync] Disabled " + all.Length + " local EnemyControllers");
        }

        public static void EnableLocalAI()
        {
            var all = GameObject.FindObjectsOfType<EnemyController>();
            foreach (var e in all)
            {
                if (e != null)
                {
                    e.enabled = true;
                    if (e.agent != null) e.agent.enabled = true;
                }
            }
        }

        public void OnSceneChanged()
        {
            _clientAiDisabled = false;
            _hostToLocal.Clear();
            _lastAttackTime.Clear();
        }

        public void Reset()
        {
            _clientAiDisabled = false;
            _hostToLocal.Clear();
            _lastAttackTime.Clear();
            _sendTimer = 0f;
        }
    }
}
