// Host-authoritative enemy AI + snapshots by WorldId; clients run halted puppets of the host's enemies.
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
        private readonly List<EnemySnapshotNet> _snapList = new List<EnemySnapshotNet>(32);

        /// <summary>Client: one host-driven enemy copy (keyed by WorldId; reset per scene).</summary>
        private sealed class Puppet
        {
            public bool Halted;                  // AI + agent switched off (Halt)
            public bool HasPose;                 // From/To valid: TickClientInterp moves the puppet
            public Vector3 From, To;
            public float FromRot, ToRot, Start;
            // Last host-confirmed Hitbox.HP. PlayerAttack lowers the puppet's Hitbox.HP itself before calling
            // TakeDamage (Ghidra PlayerAttack.c), so the drop below this value is the local hit's damage.
            public bool HasHp;
            public int Hp;
            public bool HasAnim;
            public float AnimTime;
            public int ShownHp = int.MinValue, ShownMaxHp;  // what debugHP.text was last set to
        }
        private readonly Dictionary<ulong, Puppet> _puppets = new Dictionary<ulong, Puppet>();
        private const float TeleportDist = 8f;
        private int _mapMisses;
        private int _mapHits;
        private float _lastDiag;

        public void RequestFullSend() => _forceSend = true;

        Puppet PuppetOf(ulong id)
        {
            Puppet p;
            if (!_puppets.TryGetValue(id, out p))
            {
                p = new Puppet();
                _puppets[id] = p;
            }
            return p;
        }

        // ------------------------------------------------------------------ host tick

        public void TickHost(LanNetworkManager net)
        {
            if (net.Role == NetworkRole.Client)
            {
                TickClientInterp();
                return;
            }
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;

            // Join/resync dump (unicast to the joiner): one snapshot now. The broadcast clock and a pending forced
            // send stay untouched, so everybody else still gets theirs on schedule.
            bool dump = net.UnicastActive;
            if (!dump)
            {
                // Enemy/boss weapon hurtboxes vs remote proxies (proxies have no colliders, so the native
                // Hurtbox.OnTriggerEnter never fires for them). Every frame: a swing window can be shorter
                // than the 15 Hz snapshot gate below. It early-outs when there are no remote proxies.
                ClientDamageService.TickHurtboxes(net);

                _sendTimer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                if (_sendTimer < PluginInfo.EntitySendInterval && !_forceSend) return;
                _sendTimer = 0f;
                _forceSend = false;
            }

            float t0 = Time.realtimeSinceStartup;
            try
            {
                SyncHostCache();
                var pm = net.ProxyManager;
                int[] remote = net.GetRemotePlayerIds();
                BuildSnapshots(net, pm, remote);
                if (_snapList.Count > 0)
                    net.SendEnemyState(_snapList);
                if (!dump)
                    RetargetAltAi(net, pm, remote);
            }
            finally
            {
                HitchTrace.Cost("enemy", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        void BuildSnapshots(LanNetworkManager net, PlayerProxyManager pm, int[] remote)
        {
            _snapList.Clear();
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                ulong id = kvp.Key;
                try
                {
                    var et = e.transform;
                    if (et == null) continue;
                    Vector3 pos = et.position;
                    bool alive = e.state != EnemyController.enemystate.dead;

                    Transform nearest = null;
                    int nearestId = -1;
                    if (alive)
                    {
                        // The host's own room chunks are native; only a remote peer standing in a room the host
                        // left asleep needs a wake. A level-disabled enemy is never a target (and is not re-walked
                        // every tick: a refused wake backs off).
                        if (!EnemyVisiblyInChunk(e) && pm != null && !WakeBackedOff(e)
                            && PeerInEnemyRoom(e, pos, pm, remote)
                            && !WakeForCombat(e, wakeAi: true))
                            NoteWakeRefused(e);
                        if (EnemyVisiblyInChunk(e))
                            nearest = Nearest(pos, NearestEnemyRange, net, pm, remote, out nearestId);
                    }

                    var snap = ReadSnapshot(e, et, pos, id);
                    if (nearest != null)
                    {
                        e.playerPos = nearest;
                        e.AimTarget = nearest;
                        e.IkTarget = nearest;
                        snap.TargetPlayerId = (sbyte)Mathf.Clamp(nearestId, -1, 127);
                    }
                    _snapList.Add(snap);
                }
                catch (Exception ex) { Guard.Swallow("EnemySync.Snapshot", ex); }
            }
        }

        static EnemySnapshotNet ReadSnapshot(EnemyController e, Transform et, Vector3 pos, ulong id)
        {
            int maxHp = e.Preset != null ? e.Preset.HP : 100;
            var hb = e.hitbox;
            int hp = hb != null ? hb.HP : maxHp;

            int animHash = 0;
            float animTime = 0f;
            var anim = e.animator;
            if (anim != null)
            {
                var si = anim.GetCurrentAnimatorStateInfo(0);
                animHash = si.fullPathHash;
                animTime = si.normalizedTime;
            }

            Vector3 vel = Vector3.zero;
            if (e.agent != null) vel = e.agent.velocity;

            var state = e.state;
            return new EnemySnapshotNet
            {
                WorldId = unchecked((long)id),
                State = (byte)state,
                HurtState = (byte)e.staggerType,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotY = et.eulerAngles.y,
                VelX = vel.x,
                VelY = vel.y,
                VelZ = vel.z,
                AnimHash = animHash,
                AnimTime = animTime,
                HP = hp,
                MaxHP = maxHp,
                Alive = state != EnemyController.enemystate.dead,
                TargetPlayerId = -1
            };
        }

        const float NearestEnemyRange = 30f;
        const float NearestAltAiRange = 40f;

        /// <summary>Nearest live target (local Elster unless dead, non-downed remote proxies) within range, with its player id.</summary>
        static Transform Nearest(Vector3 fromPos, float range, LanNetworkManager net, PlayerProxyManager pm, int[] remote,
            out int playerId)
        {
            Transform best = null;
            playerId = -1;
            float bestDist = range * range;
            var localPlayer = net.GetLocalPlayer();
            if (localPlayer != null && !NetworkDamageSystem.IsDead)
            {
                float d = (localPlayer.transform.position - fromPos).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = localPlayer.transform;
                    playerId = net.LocalPlayerId;
                }
            }
            if (pm == null) return best;
            for (int i = 0; i < remote.Length; i++)
            {
                int pid = remote[i];
                var proxy = pm.GetProxy(pid);
                if (proxy == null || proxy.GameObject == null) continue;
                if (PartyVitals.IsProxyDown(pid, proxy)) continue; // downed peers are not targets
                var pt = proxy.GameObject.transform;
                float d = (pt.position - fromPos).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = pt;
                    playerId = pid;
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ host per-scene cache (one generation check)

        // Host: enemy (local instance id, never sent) -> its Room and a refused-wake backoff. Rooms do not change under
        // a sleeping enemy. Rebuilt with the registry, like the alt-AI arrays (spawns / scene changes are picked up).
        private struct HostEnemy
        {
            public bool RoomKnown;
            public Room Room;
            public string RoomName;
            public float WakeRefusedUntil;
        }
        static readonly Dictionary<int, HostEnemy> _hostEnemies = new Dictionary<int, HostEnemy>();
        static int _hostGeneration = -1;
        static BasicEnemy[] _basics = Array.Empty<BasicEnemy>();
        static EnemyCookBase[] _cooks = Array.Empty<EnemyCookBase>();
        const float WakeRetrySeconds = 2f;

        static void SyncHostCache()
        {
            int g = WorldRegistry.Generation;
            if (g == _hostGeneration) return;
            _hostGeneration = g;
            _hostEnemies.Clear();
            _basics = WorldLookup.All<BasicEnemy>() ?? Array.Empty<BasicEnemy>();
            _cooks = WorldLookup.All<EnemyCookBase>() ?? Array.Empty<EnemyCookBase>();
        }

        static void ClearHostCache()
        {
            _hostEnemies.Clear();
            _hostGeneration = -1;
            _basics = Array.Empty<BasicEnemy>();
            _cooks = Array.Empty<EnemyCookBase>();
        }

        static HostEnemy HostEnemyOf(EnemyController enemy, out int key)
        {
            SyncHostCache();
            key = enemy.GetInstanceID();
            HostEnemy he;
            _hostEnemies.TryGetValue(key, out he);
            return he;
        }

        static EnemyRoomInfo RoomOfEnemy(EnemyController enemy)
        {
            int key;
            HostEnemy he;
            try { he = HostEnemyOf(enemy, out key); }
            catch (Exception e) { Guard.Swallow(e); return default(EnemyRoomInfo); }
            if (!he.RoomKnown)
            {
                he.RoomKnown = true;
                he.RoomName = "";
                try
                {
                    for (var t = enemy.transform; t != null; t = t.parent)
                    {
                        var r = t.GetComponent<Room>();
                        if (r == null) continue;
                        he.Room = r;
                        he.RoomName = r.roomName ?? "";
                        break;
                    }
                }
                catch (Exception e) { Guard.Swallow(e); }
                _hostEnemies[key] = he;
            }
            return new EnemyRoomInfo { Room = he.Room, Name = he.RoomName };
        }

        struct EnemyRoomInfo
        {
            public Room Room;
            public string Name;
        }

        static bool WakeBackedOff(EnemyController e)
        {
            try
            {
                int key;
                var he = HostEnemyOf(e, out key);
                return Time.unscaledTime < he.WakeRefusedUntil;
            }
            catch (Exception ex) { Guard.Swallow(ex); return false; }
        }

        static void NoteWakeRefused(EnemyController e)
        {
            try
            {
                int key;
                var he = HostEnemyOf(e, out key);
                he.WakeRefusedUntil = Time.unscaledTime + WakeRetrySeconds;
                _hostEnemies[key] = he;
            }
            catch (Exception ex) { Guard.Swallow(ex); }
        }

        private void RetargetAltAi(LanNetworkManager net, PlayerProxyManager pm, int[] remote)
        {
            int unused;
            var basics = _basics;
            for (int i = 0; i < basics.Length; i++)
            {
                var b = basics[i];
                if (b == null) continue;
                try
                {
                    var n = Nearest(b.transform.position, NearestAltAiRange, net, pm, remote, out unused);
                    if (n != null) b.target = n;
                }
                catch (Exception e) { Guard.Swallow("EnemySync.RetargetBasic", e); }
            }
            var cooks = _cooks;
            for (int i = 0; i < cooks.Length; i++)
            {
                var c = cooks[i];
                if (c == null) continue;
                try
                {
                    var n = Nearest(c.transform.position, NearestAltAiRange, net, pm, remote, out unused);
                    if (n != null) c.target = n;
                }
                catch (Exception e) { Guard.Swallow("EnemySync.RetargetCook", e); }
            }
        }

        /// <summary>A remote, non-downed peer whose reported room is this enemy's room (and within 30 units).</summary>
        static bool PeerInEnemyRoom(EnemyController enemy, Vector3 pos, PlayerProxyManager pm, int[] remote)
        {
            string roomName = null;
            for (int i = 0; i < remote.Length; i++)
            {
                int pid = remote[i];
                var proxy = pm.GetProxy(pid);
                if (proxy == null || proxy.GameObject == null) continue;
                // Cheap distance first: the room lookup only runs for an enemy a peer is actually near.
                if ((proxy.GameObject.transform.position - pos).sqrMagnitude >= 30f * 30f) continue;
                if (PartyVitals.IsProxyDown(pid, proxy)) continue;
                if (roomName == null)
                {
                    roomName = RoomOfEnemy(enemy).Name ?? "";
                    if (roomName.Length == 0) return false;
                }
                if (PartyVitals.RoomOf(pid) == roomName) return true;
            }
            return false;
        }

        private static bool EnemyVisiblyInChunk(EnemyController enemy)
        {
            // Inactive hierarchy = sleeping chunk (no GetComponentsInChildren renderer walk per tick).
            try
            {
                return enemy != null
                    && enemy.gameObject != null
                    && enemy.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        /// <summary>
        /// Host: sleeping-chunk enemies have no AI and TakeDamage no-ops. Wake one when a peer is in its room
        /// or a hit arrives — but only by native <c>Room.SetChunkStatus(true)</c>, and only when the room's
        /// chunk / instantChunk / Cell is the sole reason it is inactive. An enemy (or any container) switched
        /// off by the level itself stays off: PEN_Wreck keeps real EULR/STCR managers disabled for the
        /// prologue, and force-activating every inactive parent spawned them (plus their event objects) on
        /// the host. Returns false when the enemy is not awake afterwards.
        /// </summary>
        public static bool WakeForCombat(EnemyController enemy, bool wakeAi = false)
        {
            if (enemy == null || enemy.gameObject == null) return false;
            try
            {
                if (!enemy.gameObject.activeInHierarchy)
                {
                    Room room;
                    if (!OnlyChunkAsleep(enemy, out room))
                    {
                        FlickerTrace.HostWake(enemy, false, "disabled by the level (not a sleeping chunk)");
                        return false;
                    }
                    NetGate.BeginApply(); // RoomChunkPuzzlePatch: not a local room entry, no re-apply sweep
                    try { room.SetChunkStatus(true); }
                    finally { NetGate.EndApply(); }
                    if (!enemy.gameObject.activeInHierarchy)
                    {
                        FlickerTrace.HostWake(enemy, false, "still inactive after chunk on");
                        return false;
                    }
                    FlickerTrace.HostWake(enemy, true, wakeAi ? "chunk on (peer in room)" : "chunk on (combat)");
                }
                enemy.enabled = true;
                if (enemy.agent != null) enemy.agent.enabled = true;
                // EnemyController.WakeUp is a no-op without host LOS and, WITH it, also shakes the host
                // screen / rumbles / plays WakeSFX (Ghidra EnemyController.c). Only the chunk-wake tick
                // (a peer is in a sleeping enemy's room) asks for it; combat side effects never do.
                if (wakeAi) enemy.WakeUp();
                return true;
            }
            catch (Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>True when every inactive object between the enemy and its Room is that room's chunk object.</summary>
        static bool OnlyChunkAsleep(EnemyController enemy, out Room room)
        {
            room = RoomOfEnemy(enemy).Room;
            var inactive = _inactiveScratch;
            inactive.Clear();
            if (room == null) return false;
            Transform roomT = room.transform;
            Transform t = enemy.transform;
            while (t != null && t != roomT)
            {
                if (!t.gameObject.activeSelf) inactive.Add(t.gameObject);
                t = t.parent;
            }
            if (t == null || inactive.Count == 0) return false; // reparented out of the cached room
            if (!room.gameObject.activeInHierarchy) return false;
            GameObject chunk = room.chunk, instant = room.instantChunk, cell = room.Cell;
            for (int i = 0; i < inactive.Count; i++)
            {
                var go = inactive[i];
                if (go != chunk && go != instant && go != cell) return false;
            }
            return true;
        }
        // persistent: per-call scratch buffer
        static readonly List<GameObject> _inactiveScratch = new List<GameObject>(4);

        // ------------------------------------------------------------------ host: client requests

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
                if (!WakeForCombat(enemy)) return; // switched off by the level, not a sleeping chunk
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
            catch (Exception ex)
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
            enemy.tracking = false;
            enemy.attacking = false;
            enemy.woken = true;
            var net = LanNetworkManager.Instance;
            var proxy = net != null && net.ProxyManager != null ? net.ProxyManager.GetProxy(fromPlayer) : null;
            if (proxy != null && proxy.GameObject != null)
            {
                var pp = proxy.GameObject.transform.position;
                enemy.targetPosition = new Vector2(pp.x, pp.y);
            }
            if (enemy.state == EnemyController.enemystate.sleep)
                enemy.state = EnemyController.enemystate.pursuit;
        }

        /// <summary>
        /// Host: a client's hit. Lower the real Hitbox.HP by the damage the client's PlayerAttack computed (native
        /// order: HP first, then TakeDamage, which reads HP &lt; 1 for the kill), then run native TakeDamage.
        /// </summary>
        public bool ApplyNativeTakeDamageOnHost(ulong enemyId, float damage, float fire, float crit, float hurt, bool noSneak)
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
                if (!WakeForCombat(enemy)) return false;

                int dmg = 0;
                var hb = enemy.hitbox;
                if (hb != null && enemy.state != EnemyController.enemystate.dead
                    && !float.IsNaN(damage) && damage > 0f)
                {
                    int maxHp = enemy.Preset != null ? enemy.Preset.HP : hb.HP;
                    dmg = Mathf.Clamp(Mathf.RoundToInt(damage), 0, Mathf.Max(0, maxHp));
                    hb.HP -= dmg;
                }

                enemy.TakeDamage(fire, crit, hurt, noSneak);
                _forceSend = true;

                PlaytestLog.Event("Enemy", "TakeDamage id=" + enemyId.ToString("X16")
                    + " dmg=" + dmg
                    + " fire=" + fire.ToString("F2")
                    + " crit=" + crit.ToString("F2")
                    + " hurt=" + hurt.ToString("F2")
                    + " hp=" + (hb != null ? hb.HP.ToString() : "?")
                    + " state=" + enemy.state);
                return true;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[EnemySync] TakeDamage failed: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ client

        public void OnEnemyStateReceived(EnemyStateMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;
            if (msg.Enemies == null) return;
            // Still loading / in another scene (menu, follow load, personal wreck/hole scene): none of the host's
            // enemies exist here. Drop the stream instead of resolving every id to a miss; the next snapshot after
            // the load (and the join dump) carries the full state.
            if (net != null && (net.SceneMismatch || SceneFollowService.LocalIsTransient())) return;

            for (int i = 0; i < msg.Enemies.Length; i++)
                ApplyEnemyState(msg.Enemies[i]);

            if (_mapMisses > 0 && Time.time - _lastDiag > 5f)
            {
                PlaytestLog.Event("Enemy", "map hits=" + _mapHits + " misses=" + _mapMisses
                    + " puppets=" + _puppets.Count);
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
                try { FlickerTrace.ClientEnemyMiss(id, new Vector3(snap.PosX, snap.PosY, snap.PosZ), snap.State); }
                catch (Exception e) { Guard.Swallow(e); }
                return;
            }
            _mapHits++;

            var p = PuppetOf(id);
            try
            {
                if (enemy.gameObject == null) return;
                FlickerTrace.ClientEnemy(id, enemy, snap.Alive, snap.State);

                if (!p.Halted)
                {
                    Halt(enemy);
                    p.Halted = true;
                }
                p.Hp = snap.HP;
                p.HasHp = true;

                var state = (EnemyController.enemystate)snap.State;
                var stagger = (EnemyController.hurtState)snap.HurtState;
                bool dead = !snap.Alive || state == EnemyController.enemystate.dead;

                // Off-chunk: no pose snap (it would pop through walls), but the dead/downed hurtbox gate still
                // applies (join dump / sleeping chunk).
                if (!EnemyVisiblyInChunk(enemy))
                {
                    enemy.state = state;
                    enemy.staggerType = stagger;
                    if (enemy.hitbox != null) enemy.hitbox.HP = snap.HP;
                    ApplyHurtboxActive(enemy, dead, stagger);
                    return;
                }

                var t = enemy.transform;
                if (t == null) return;
                SetPoseTarget(id, p, enemy, t, snap);

                enemy.state = state;
                enemy.staggerType = stagger;
                ApplyHurtboxActive(enemy, dead, stagger);
                ApplyStompPrompt(enemy, dead, stagger);
                if (enemy.hitbox != null) enemy.hitbox.HP = snap.HP;
                if (p.ShownHp != snap.HP || p.ShownMaxHp != snap.MaxHP)
                {
                    var dbg = enemy.debugHP;
                    if (dbg != null) dbg.text = snap.HP + "/" + snap.MaxHP;
                    p.ShownHp = snap.HP;
                    p.ShownMaxHp = snap.MaxHP;
                }
                ApplyAnim(p, enemy, snap);
            }
            catch (Exception ex)
            {
                // A half-applied snapshot is redone in full by the next one (15 Hz): halt again and snap the pose then.
                p.Halted = false;
                p.HasPose = false;
                Guard.Swallow("EnemySync.Apply", ex);
            }
        }

        /// <summary>
        /// Smooth toward the snapshot instead of teleporting (15 Hz feed): TickClientInterp moves the puppet each
        /// frame. First sight / big jump / freshly woken chunk snaps.
        /// </summary>
        static void SetPoseTarget(ulong id, Puppet p, EnemyController enemy, Transform t, EnemySnapshotNet snap)
        {
            var target = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
            var cur = t.position;
            if (!p.HasPose || (cur - target).sqrMagnitude > TeleportDist * TeleportDist)
            {
                if (p.HasPose) FlickerTrace.ClientEnemySnapJump(id, enemy, Vector3.Distance(cur, target));
                t.position = target;
                var rot0 = t.eulerAngles;
                rot0.y = snap.RotY;
                t.eulerAngles = rot0;
                p.From = target;
                p.FromRot = snap.RotY;
            }
            else
            {
                p.From = cur;
                p.FromRot = t.eulerAngles.y;
            }
            p.To = target;
            p.ToRot = snap.RotY;
            p.Start = Time.time;
            p.HasPose = true;
        }

        static void ApplyAnim(Puppet p, EnemyController enemy, EnemySnapshotNet snap)
        {
            var anim = enemy.animator;
            if (anim == null || snap.AnimHash == 0) return;
            // Re-Play only when the host moved to a different state (comparing normalizedTime restarted short
            // one-shot clips every snapshot). normalizedTime only grows within a state, so a sharp drop with the
            // same hash is the host restarting that clip (a repeated attack): replay it too.
            var stateInfo = anim.GetCurrentAnimatorStateInfo(0);
            bool restarted = p.HasAnim && snap.AnimTime < p.AnimTime - 0.5f;
            p.HasAnim = true;
            p.AnimTime = snap.AnimTime;
            if (stateInfo.fullPathHash != snap.AnimHash || restarted)
                anim.Play(snap.AnimHash, 0, snap.AnimTime);
        }

        /// <summary>
        /// Mirror EnemyController.UpdateDataBlock's hitbox GO toggles (a puppet's AI is off, so it never runs):
        /// hurtbox inactive when dead; downedHitbox only for critical/fire while alive. Never KillSilent.
        /// </summary>
        static void ApplyHurtboxActive(EnemyController enemy, bool dead, EnemyController.hurtState stagger)
        {
            if (enemy.hurtbox != null)
                enemy.hurtbox.SetActive(!dead);
            if (enemy.downedHitbox != null)
            {
                bool downed = !dead
                    && (stagger == EnemyController.hurtState.critical
                        || stagger == EnemyController.hurtState.fire);
                enemy.downedHitbox.SetActive(downed);
            }
        }

        /// <summary>
        /// Puppet clients never run the Critical coroutine that enables the stomp prompt
        /// (EnemyController.Critical -> stompInter.SetActive(true), Ghidra EnemyController.c), so a client
        /// could never finish a downed enemy. Mirror it from the host's critical stagger state.
        /// </summary>
        static void ApplyStompPrompt(EnemyController enemy, bool dead, EnemyController.hurtState stagger)
        {
            var si = enemy.stompInter;
            if (si == null) return;
            var go = si.gameObject;
            if (go == null) return;
            bool want = !dead && stagger == EnemyController.hurtState.critical;
            if (!want)
            {
                if (go.activeSelf) go.SetActive(false);
                return;
            }
            // Never re-arm a prompt this client already triggered (host Kill is on the way).
            if (si.triggered) return;
            if (!go.activeSelf) go.SetActive(true);
            // Native keeps the prompt on the enemy's AimPoint (x, y, 0).
            var aim = enemy.AimPoint;
            if (aim != null)
            {
                var ap = aim.position;
                go.transform.position = new Vector3(ap.x, ap.y, 0f);
            }
        }

        /// <summary>Client per-frame: advance puppet poses toward the latest snapshot.</summary>
        private void TickClientInterp()
        {
            if (_puppets.Count == 0) return;
            float now = Time.time;
            // 1.5x the send interval: a snapshot that lands late no longer leaves the puppet standing still.
            float dur = PluginInfo.EntitySendInterval * 1.5f;
            foreach (var kvp in _puppets)
            {
                var p = kvp.Value;
                if (!p.HasPose) continue;
                EnemyController e;
                if (!WorldRegistry.TryGetEnemy(kvp.Key, out e) || e == null)
                {
                    p.HasPose = false;
                    continue;
                }
                float k = dur > 0f ? Mathf.Clamp01((now - p.Start) / dur) : 1f;
                try
                {
                    var tr = e.transform;
                    if (tr == null || !e.gameObject.activeInHierarchy) continue;
                    tr.position = Vector3.Lerp(p.From, p.To, k);
                    var rot = tr.eulerAngles;
                    rot.y = Mathf.LerpAngle(p.FromRot, p.ToRot, k);
                    tr.eulerAngles = rot;
                }
                catch (Exception ex)
                {
                    p.HasPose = false;
                    Guard.Swallow("EnemySync.Interp", ex);
                }
            }
        }

        /// <summary>
        /// Client, inside the TakeDamage prefix: the HP the local hit took off the puppet (PlayerAttack / Kolibri
        /// feedback lower Hitbox.HP before TakeDamage). Restores the last host-confirmed HP so the next local hit
        /// measures only its own damage; the host applies the returned amount. 0 when nothing was taken.
        /// </summary>
        public int TakeLocalHitDelta(ulong id, EnemyController enemy)
        {
            if (enemy == null) return 0;
            try
            {
                var hb = enemy.hitbox;
                if (hb == null) return 0;
                int cur = hb.HP;
                int confirmed;
                Puppet p;
                if (_puppets.TryGetValue(id, out p) && p.HasHp)
                    confirmed = p.Hp;
                else
                {
                    // No snapshot yet: the puppet still has its native spawn HP.
                    if (enemy.Preset == null) return 0;
                    confirmed = enemy.Preset.HP;
                }
                if (cur >= confirmed) return 0;
                hb.HP = confirmed;
                return confirmed - cur;
            }
            catch (Exception ex) { Guard.Swallow(ex); return 0; }
        }

        static void Halt(EnemyController enemy)
        {
            enemy.enabled = false;
            if (enemy.agent != null) enemy.agent.enabled = false;
        }

        /// <summary>Client: halt every registered enemy now (connect / scene arrival, before the first snapshot).</summary>
        public void PuppetAllNow()
        {
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                var p = PuppetOf(kvp.Key);
                if (p.Halted) continue;
                try
                {
                    Halt(e);
                    p.Halted = true;
                }
                catch (Exception ex) { Guard.Swallow("EnemySync.Halt", ex); }
            }
        }

        /// <summary>Offline / StopNetwork: re-enable AI that the puppets disabled.</summary>
        public void UnpuppetAllNow()
        {
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                try
                {
                    e.enabled = true;
                    if (e.agent != null) e.agent.enabled = true;
                }
                catch (Exception ex) { Guard.Swallow("EnemySync.Unhalt", ex); }
            }
            _puppets.Clear();
        }

        public void OnSceneChanged()
        {
            _puppets.Clear();
            _mapHits = 0;
            _mapMisses = 0;
            _sendTimer = 0f;
            _snapList.Clear();
            ClearHostCache();
        }

        public void Reset()
        {
            UnpuppetAllNow();
            OnSceneChanged();
        }

        // ------------------------------------------------------------------ PuzzleState (EnemyManager / global alert)

        /// <summary>EnemyManagerState from the registry (never FindObjectsOfType).</summary>
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
