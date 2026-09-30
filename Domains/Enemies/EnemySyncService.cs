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
        /// <summary>Client puppet pose smoothing target per enemy (snapshots arrive at 15 Hz).</summary>
        private struct PoseInterp
        {
            public Vector3 From;
            public Vector3 To;
            public float FromRot;
            public float ToRot;
            public float Start;
        }
        private readonly Dictionary<ulong, PoseInterp> _interp = new Dictionary<ulong, PoseInterp>();
        private readonly Dictionary<ulong, float> _lastAnimTime = new Dictionary<ulong, float>();
        private readonly List<ulong> _interpDead = new List<ulong>(4);
        private const float TeleportDist = 8f;
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
            if (net.Role == NetworkRole.Client)
            {
                TickClientInterp();
                return;
            }
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;

            // Enemy/boss weapon hurtboxes vs remote proxies (proxies have no colliders, so the native
            // Hurtbox.OnTriggerEnter never fires for them). Every frame — a swing window can be shorter
            // than the 15 Hz snapshot gate below — and it early-outs when there are no remote proxies.
            ClientDamageService.TickHurtboxes(net);

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
                        WakeForCombat(e, wakeAi: true);
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
                catch (Exception ex) { Guard.Swallow(ex); }

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
                catch (Exception ex) { Guard.Swallow(ex); }

                float rotY = 0f;
                try { rotY = et.eulerAngles.y; } catch (Exception ex) { Guard.Swallow(ex); }

                float velX = 0f, velY = 0f, velZ = 0f;
                try
                {
                    if (e.agent != null)
                    {
                        var v = e.agent.velocity;
                        velX = v.x; velY = v.y; velZ = v.z;
                    }
                }
                catch (Exception ex) { Guard.Swallow(ex); }

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
                    catch (Exception ex) { Guard.Swallow(ex); }

                    int tid = -1;
                    try
                    {
                        tid = pm.GetPlayerIdByGameObject(nearest.gameObject);
                        if (tid < 0 && nearest.gameObject == net.GetLocalPlayer())
                            tid = net.LocalPlayerId;
                    }
                    catch (Exception ex) { Guard.Swallow(ex); }
                    var snap = _snapList[_snapList.Count - 1];
                    snap.TargetPlayerId = (sbyte)Mathf.Clamp(tid, -1, 127);
                    _snapList[_snapList.Count - 1] = snap;
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
            catch (Exception e) { Guard.Swallow(e); }

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
            catch (Exception e) { Guard.Swallow(e); }
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

        /// <summary>
        /// Match GameAssembly EnemyController.UpdateDataBlock hitbox GO toggles.
        /// Peer Puppet disables AI so UpdateDataBlock never runs — Apply must mirror:
        /// hurtbox inactive when dead; downedHitbox only for critical/fire while alive.
        /// Do not call KillSilent.
        /// </summary>
        static void ApplyHurtboxActive(EnemyController enemy, bool dead, EnemyController.hurtState stagger)
        {
            try
            {
                if (enemy.hurtbox != null)
                    enemy.hurtbox.SetActive(!dead);
            }
            catch (Exception e) { Guard.Swallow(e); }
            try
            {
                if (enemy.downedHitbox != null)
                {
                    bool downed = !dead
                        && (stagger == EnemyController.hurtState.critical
                            || stagger == EnemyController.hurtState.fire);
                    enemy.downedHitbox.SetActive(downed);
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Puppet clients never run the Critical coroutine that enables the stomp prompt
        /// (EnemyController.Critical -> stompInter.SetActive(true), Ghidra EnemyController.c), so a client
        /// could never finish a downed enemy. Mirror it from the host's critical stagger state.
        /// </summary>
        static void ApplyStompPrompt(EnemyController enemy, bool dead, EnemyController.hurtState stagger)
        {
            try
            {
                var si = enemy.stompInter;
                if (si == null) return;
                var go = si.gameObject;
                if (go == null) return;
                bool want = !dead && stagger == EnemyController.hurtState.critical;
                if (want)
                {
                    // Never re-arm a prompt this client already triggered (host Kill is on the way).
                    if (si.triggered) return;
                    if (!go.activeSelf) go.SetActive(true);
                    try
                    {
                        // Native keeps the prompt on the enemy's AimPoint (x, y, 0).
                        var aim = enemy.AimPoint;
                        if (aim != null)
                        {
                            var ap = aim.position;
                            go.transform.position = new Vector3(ap.x, ap.y, 0f);
                        }
                    }
                    catch (System.Exception ex) { WarnStompOnce(ex); }
                }
                else if (go.activeSelf)
                    go.SetActive(false);
            }
            catch (System.Exception ex) { WarnStompOnce(ex); }
        }

        static bool _stompWarned;

        static void WarnStompOnce(System.Exception ex)
        {
            if (_stompWarned) return;
            _stompWarned = true;
            ModRuntime.Log?.Warning("[EnemySync] stomp prompt: " + ex.Message);
        }

        /// <summary>Client per-frame: advance puppet poses toward the latest snapshot.</summary>
        private void TickClientInterp()
        {
            if (_interp.Count == 0) return;
            float now = Time.time;
            // 1.5x the send interval: a snapshot that lands late no longer leaves the puppet standing still.
            float dur = PluginInfo.EntitySendInterval * 1.5f;
            _interpDead.Clear();
            foreach (var kvp in _interp)
            {
                EnemyController e;
                if (!WorldRegistry.TryGetEnemy(kvp.Key, out e) || e == null)
                {
                    _interpDead.Add(kvp.Key);
                    continue;
                }
                var pi = kvp.Value;
                float k = dur > 0f ? Mathf.Clamp01((now - pi.Start) / dur) : 1f;
                try
                {
                    var tr = e.transform;
                    if (tr == null || !e.gameObject.activeInHierarchy) continue;
                    tr.position = Vector3.Lerp(pi.From, pi.To, k);
                    var rot = tr.eulerAngles;
                    rot.y = Mathf.LerpAngle(pi.FromRot, pi.ToRot, k);
                    tr.eulerAngles = rot;
                }
                catch { _interpDead.Add(kvp.Key); }
            }
            for (int i = 0; i < _interpDead.Count; i++)
                _interp.Remove(_interpDead[i]);
        }

        /// <summary>
        /// Host: side effects a puppeted client triggered on its local copy (stomp Kill, push/knockback,
        /// fusee burn, wake). Runs the native method on the real sim so the result is shared.
        /// </summary>
        public void ApplyActionOnHost(ulong enemyId, EnemyActionKind action, int fromPlayer)
        {
            EnemyController enemy;
            if (!WorldRegistry.TryGetEnemy(enemyId, out enemy) || enemy == null || enemy.gameObject == null)
            {
                PlaytestLog.Event("Enemy", "action MISS " + action + " id=" + enemyId.ToString("X16"));
                return;
            }
            try
            {
                if (action != EnemyActionKind.WakeUp && enemy.state == EnemyController.enemystate.dead) return;
                WakeForCombat(enemy);
                if (enemy == null || enemy.gameObject == null) return;
                NetGate.BeginApply();
                try
                {
                    switch (action)
                    {
                        case EnemyActionKind.Kill: enemy.Kill(); break;
                        case EnemyActionKind.KillSilent: enemy.KillSilent(); break;
                        case EnemyActionKind.Knockback: enemy.Knockback(); break;
                        case EnemyActionKind.GetPushed: enemy.GetPushed(); break;
                        case EnemyActionKind.Burndown:
                            if (enemy.BurnEffect != null) enemy.BurnEffect.Burn();
                            enemy.burndown();
                            break;
                        case EnemyActionKind.WakeUp: WakeFromFlashlight(enemy, fromPlayer); break;
                    }
                }
                finally { NetGate.EndApply(); }
                _forceSend = true;
                PlaytestLog.Event("Enemy", "action " + action + " id=" + enemyId.ToString("X16")
                    + " from=" + fromPlayer + " state=" + enemy.state);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[EnemySync] action " + action + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Client flashlight wake. Native WakeUpFlashlight also fires PlayerState.fireGun + screen shake +
        /// rumble on the HOST (and WakeUp needs host LOS), so set just the AI state it changes:
        /// woken, not tracking/attacking, heading for the waker, pursuit.
        /// </summary>
        static void WakeFromFlashlight(EnemyController enemy, int fromPlayer)
        {
            if (enemy == null) return;
            enemy.tracking = false;
            enemy.attacking = false;
            enemy.woken = true;
            try
            {
                var net = LanNetworkManager.Instance;
                var proxy = net != null && net.ProxyManager != null ? net.ProxyManager.GetProxy(fromPlayer) : null;
                if (proxy != null && proxy.GameObject != null)
                {
                    var pp = proxy.GameObject.transform.position;
                    enemy.targetPosition = new Vector2(pp.x, pp.y);
                }
            }
            catch (Exception ex) { Guard.Swallow(ex); }
            if (enemy.state == EnemyController.enemystate.sleep)
                enemy.state = EnemyController.enemystate.pursuit;
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
                    try { enemy.state = (EnemyController.enemystate)snap.State; } catch (Exception e) { Guard.Swallow(e); }
                    try { enemy.staggerType = (EnemyController.hurtState)snap.HurtState; } catch (Exception e) { Guard.Swallow(e); }
                    try
                    {
                        if (enemy != null && enemy.hitbox != null)
                            enemy.hitbox.HP = snap.HP;
                    }
                    catch (Exception e) { Guard.Swallow(e); }
                    // Off-chunk still needs dead hurtbox gate (join dump / sleeping chunk).
                    {
                        bool dead = !snap.Alive
                            || snap.State == (byte)EnemyController.enemystate.dead;
                        var stagger = (EnemyController.hurtState)snap.HurtState;
                        ApplyHurtboxActive(enemy, dead, stagger);
                    }
                    return;
                }

                if (enemy == null || enemy.gameObject == null) return;
                var t = enemy.transform;
                if (t == null) return;
                try
                {
                    // Smooth toward the snapshot instead of teleporting (15 Hz feed): TickClientInterp
                    // moves the puppet each frame. First sight / big jump / freshly woken chunk snaps.
                    var target = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
                    var cur = t.position;
                    PoseInterp pi;
                    bool have = _interp.TryGetValue(id, out pi);
                    if (!have || (cur - target).sqrMagnitude > TeleportDist * TeleportDist)
                    {
                        t.position = target;
                        var rot0 = t.eulerAngles;
                        rot0.y = snap.RotY;
                        t.eulerAngles = rot0;
                        pi.From = target;
                        pi.FromRot = snap.RotY;
                    }
                    else
                    {
                        pi.From = cur;
                        pi.FromRot = t.eulerAngles.y;
                    }
                    pi.To = target;
                    pi.ToRot = snap.RotY;
                    pi.Start = Time.time;
                    _interp[id] = pi;
                }
                catch { return; }

                try { enemy.state = (EnemyController.enemystate)snap.State; } catch (Exception e) { Guard.Swallow(e); }
                // Decompile EnemyController.staggerType (hurtState) — was on wire unused.
                try { enemy.staggerType = (EnemyController.hurtState)snap.HurtState; } catch (Exception e) { Guard.Swallow(e); }

                // Puppet peers never run UpdateDataBlock — mirror dead/downed hitbox GOs.
                {
                    bool dead = !snap.Alive
                        || snap.State == (byte)EnemyController.enemystate.dead;
                    var stagger = (EnemyController.hurtState)snap.HurtState;
                    ApplyHurtboxActive(enemy, dead, stagger);
                    ApplyStompPrompt(enemy, dead, stagger);
                }

                try
                {
                    if (enemy.hitbox != null)
                        enemy.hitbox.HP = snap.HP;
                }
                catch (Exception e) { Guard.Swallow(e); }
                try
                {
                    if (enemy.debugHP != null)
                        enemy.debugHP.text = snap.HP + "/" + snap.MaxHP;
                }
                catch (Exception e) { Guard.Swallow(e); }

                var anim = enemy.animator;
                if (anim != null && snap.AnimHash != 0)
                {
                    try
                    {
                        // Re-Play only when the host moved to a different state. Comparing
                        // normalizedTime restarted short one-shot clips every snapshot (stutter).
                        var stateInfo = anim.GetCurrentAnimatorStateInfo(0);
                        // normalizedTime only grows within a state, so a sharp drop with the same hash is the
                        // host restarting that clip (a repeated attack): replay it too.
                        float lastT;
                        bool restarted = _lastAnimTime.TryGetValue(id, out lastT) && snap.AnimTime < lastT - 0.5f;
                        _lastAnimTime[id] = snap.AnimTime;
                        if (stateInfo.fullPathHash != snap.AnimHash || restarted)
                            anim.Play(snap.AnimHash, 0, snap.AnimTime);
                    }
                    catch (Exception e) { Guard.Swallow(e); }
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
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
            if (localPlayer != null && !NetworkDamageSystem.IsDead)
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
                if (PartyVitals.IsProxyDown(pid, proxy)) continue; // downed peers are not targets
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
            try { enemy.enabled = false; } catch (Exception e) { Guard.Swallow(e); }
            try { if (enemy.agent != null) enemy.agent.enabled = false; } catch (Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Host: sleeping-chunk enemies have no AI and TakeDamage no-ops.
        /// Wake the parent Room chain when a peer is in that room or a hit arrives.
        /// </summary>
        public static void WakeForCombat(EnemyController enemy, bool wakeAi = false)
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
                // EnemyController.WakeUp is a no-op without host LOS and, WITH it, also shakes the host
                // screen / rumbles / plays WakeSFX (Ghidra EnemyController.c). Only the chunk-wake tick
                // (a peer is near a sleeping enemy) asks for it; combat side effects never do.
                if (wakeAi) enemy.WakeUp();
            }
            catch (Exception e) { Guard.Swallow(e); }
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

        /// <summary>Offline / StopNetwork: re-enable AI that PuppetAllNow disabled.</summary>
        public void UnpuppetAllNow()
        {
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                try { e.enabled = true; } catch (Exception ex) { Guard.Swallow(ex); }
                try { if (e.agent != null) e.agent.enabled = true; } catch (Exception ex) { Guard.Swallow(ex); }
            }
            _clientPuppeted.Clear();
        }

        public void OnSceneChanged()
        {
            _clientPuppeted.Clear();
            _interp.Clear();
            _lastAnimTime.Clear();
            SyncRADation.Patches.EnemySpawnerPatches.ClearAdopted();
            ClientDamageService.OnSceneChanged();
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
            UnpuppetAllNow();
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
                    catch (Exception e) { Guard.Swallow(e); }
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
            try { alarmVal = (int)GlobalAlertStatus.currentStatus; } catch (Exception e) { Guard.Swallow(e); }
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
