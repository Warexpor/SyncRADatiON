// Host-authoritative boss sync via WorldId
using System;
using System.Collections.Generic;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class BossSyncService
    {
        private float _sendTimer;
        private bool _forceSend;
        private bool _clientDisabled;

        private const float SendInterval = 1f / 15f;

        /// <summary>Join/resync dump: bypass send timer so mid-phase state is in the unicast snapshot.</summary>
        public void RequestFullSend() => _forceSend = true;

        private readonly Dictionary<long, (MonoBehaviour comp, BossType type)> _hostToLocal
            = new Dictionary<long, (MonoBehaviour comp, BossType type)>();

        private END_Boss[] _endBosses = Array.Empty<END_Boss>();
        private BOS_Adler[] _adlers = Array.Empty<BOS_Adler>();
        private LAB_ChimeraBoss[] _chimeras = Array.Empty<LAB_ChimeraBoss>();
        private MED_MynahBoss[] _mynahs = Array.Empty<MED_MynahBoss>();
        private bool _bossCacheReady;
        private readonly List<BossSnapshotNet> _tickList = new List<BossSnapshotNet>(8);
        // Client: last host-confirmed Falke HP per WorldId. PlayerAttack mutates Hitbox.HP directly
        // (Ghidra PlayerAttack.c: Hitbox.HP -= dmg, no method to hook), so a drop below this value is
        // a local hit that must be forwarded to the host and rolled back.
        private readonly Dictionary<long, int> _lastHp = new Dictionary<long, int>();
        private readonly HashSet<string> _warned = new HashSet<string>();

        void WarnOnce(string key, Exception ex)
        {
            if (_warned.Add(key))
                ModRuntime.Log?.Warning("[BossSync] " + key + ": " + ex.Message);
        }

        void EnsureBossCache()
        {
            if (_bossCacheReady) return;
            _endBosses = WorldLookup.All<END_Boss>() ?? Array.Empty<END_Boss>();
            _adlers = WorldLookup.All<BOS_Adler>() ?? Array.Empty<BOS_Adler>();
            _chimeras = WorldLookup.All<LAB_ChimeraBoss>() ?? Array.Empty<LAB_ChimeraBoss>();
            _mynahs = WorldLookup.All<MED_MynahBoss>() ?? Array.Empty<MED_MynahBoss>();
            _bossCacheReady = true;
        }

        /// <summary>Host: retarget END/Adler Elster refs without per-tick FindObjectsOfType.</summary>
        public void RetargetElsters(LanNetworkManager net, PlayerProxyManager pm)
        {
            if (net == null || pm == null) return;
            EnsureBossCache();
            try
            {
                var ends = _endBosses;
                for (int i = 0; i < ends.Length; i++)
                {
                    var b = ends[i];
                    if (b == null) continue;
                    Transform bt;
                    try { bt = b.transform; }
                    catch { continue; }
                    if (bt == null) continue;
                    var n = FindNearest(bt.position, net, pm);
                    if (n == null) continue;
                    try { b.Elster = n; } catch (Exception e) { Guard.Swallow(e); }
                    try { b.Target = n; } catch (Exception e) { Guard.Swallow(e); }
                }
            }
            catch (Exception e) { Guard.Swallow(e); }

            try
            {
                var adlers = _adlers;
                for (int i = 0; i < adlers.Length; i++)
                {
                    var a = adlers[i];
                    if (a == null) continue;
                    Transform at;
                    try { at = a.transform; }
                    catch { continue; }
                    if (at == null) continue;
                    var n = FindNearest(at.position, net, pm);
                    if (n != null) { try { a.Elster = n; } catch (Exception e) { Guard.Swallow(e); } }
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        static Transform FindNearest(Vector3 fromPos, LanNetworkManager net, PlayerProxyManager pm)
        {
            Transform best = null;
            float bestDist = 40f * 40f;
            var localPlayer = net.GetLocalPlayer();
            if (localPlayer != null && !NetworkDamageSystem.IsDead)
            {
                float d = (localPlayer.transform.position - fromPos).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = localPlayer.transform; }
            }
            foreach (int pid in net.GetRemotePlayerIds())
            {
                var proxy = pm.GetProxy(pid);
                if (proxy == null || proxy.GameObject == null) continue;
                if (PartyVitals.IsProxyDown(pid, proxy)) continue; // downed peers are not targets
                float d = (proxy.GameObject.transform.position - fromPos).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = proxy.GameObject.transform; }
            }
            return best;
        }

        /// <summary>Client per-frame: forward local hits on puppeted bosses to the host.</summary>
        private void TickClient(LanNetworkManager net)
        {
            if (!_clientDisabled || net.SceneMismatch || _hostToLocal.Count == 0) return;
            foreach (var kvp in _hostToLocal)
            {
                if (kvp.Value.type != BossType.END_Boss) continue;
                ForwardLocalBossDamage(net, kvp.Key, kvp.Value.comp as END_Boss);
            }
        }

        private void ForwardLocalBossDamage(LanNetworkManager net, long wid, END_Boss b)
        {
            if (b == null) return;
            int last;
            if (!_lastHp.TryGetValue(wid, out last)) return;
            try
            {
                var hb = b.hitbox;
                if (hb == null) return;
                int cur = hb.HP;
                if (cur >= last) return;
                hb.HP = last;
                net.BossHandlers.SendBossHitToHost(wid, BossHitKind.Damage, last - cur);
            }
            catch (Exception ex) { WarnOnce("forward boss damage", ex); }
        }

        /// <summary>
        /// Host: a client's boss request. Damage lowers the real Hitbox.HP (END_Boss.Update reacts to the
        /// change exactly as for a local hit); Stab/takeSpear run the native methods on the host sim.
        /// </summary>
        public void ApplyHitOnHost(BossHitMessage msg, int senderId)
        {
            BossType type;
            var comp = FindLocalBossByWorldId(msg.WorldId, out type);
            var b = comp as END_Boss;
            if (b == null || b.gameObject == null)
            {
                PlaytestLog.Event("Boss", "hit MISS " + msg.Kind + " wid=" + msg.WorldId.ToString("X16") + " from=" + senderId);
                return;
            }
            bool stabbed = false, dupSpear = false;
            try
            {
                NetGate.BeginApply();
                try
                {
                    switch (msg.Kind)
                    {
                        case BossHitKind.Damage:
                            if (msg.Amount > 0 && msg.Amount <= 2000 && b.hitbox != null && b.state != END_Boss.states.dead)
                                b.hitbox.HP -= msg.Amount;
                            break;
                        case BossHitKind.Stab:
                            if (b.state == END_Boss.states.downed)
                            {
                                b.Stab();
                                stabbed = true;
                            }
                            break;
                        case BossHitKind.TakeSpear:
                        {
                            var spears = b.PickupSpears;
                            int idx = msg.Amount;
                            if (spears != null && idx >= 0 && idx < spears.Length && spears[idx] != null)
                            {
                                if (spears[idx].activeSelf)
                                {
                                    NoteSpearTaker(idx, senderId);
                                    b.takeSpear(spears[idx]); // host postfix relays the hide to every client
                                }
                                else
                                {
                                    // Lost the race: this peer's own UnityEvent entry already added the spear
                                    // to its bag, but the host gave it to the first taker. Take it back.
                                    int taker;
                                    if (!_spearTaker.TryGetValue(idx, out taker) || taker != senderId)
                                        dupSpear = true;
                                }
                            }
                            break;
                        }
                    }
                }
                finally { NetGate.EndApply(); }
                if (stabbed) RevokeSpear(b);
                if (dupSpear) AckConsumeSpear(b, msg.WorldId, senderId);
                _forceSend = true;
                PlaytestLog.Event("Boss", "hit " + msg.Kind + " amt=" + msg.Amount + " from=" + senderId);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[BossSync] host hit " + msg.Kind + ": " + ex.Message);
            }
        }

        private readonly Dictionary<int, int> _spearTaker = new Dictionary<int, int>();

        /// <summary>Host: who got PickupSpears[idx] (first taker wins; later calls keep the first).</summary>
        public void NoteSpearTaker(int idx, int playerId)
        {
            if (!_spearTaker.ContainsKey(idx)) _spearTaker[idx] = playerId;
        }

        /// <summary>The stab consumed SpearItem on the host bag only: retire it from the ring and every peer's bag.</summary>
        void RevokeSpear(END_Boss b)
        {
            try
            {
                var item = b != null ? b.SpearItem : null;
                if (item != null) PartyKeyRing.RevokeConsumed(item._item);
            }
            catch (Exception ex) { WarnOnce("stab revoke", ex); }
        }

        /// <summary>Tell a peer that lost the spear race to drop the copy its local UnityEvent added.</summary>
        void AckConsumeSpear(END_Boss b, long wid, int senderId)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || senderId == net.LocalPlayerId) return;
            try
            {
                var item = b != null ? b.SpearItem : null;
                if (item == null) return;
                net.SendInteractionAck(senderId, wid, InteractionKind.UseItem, true,
                    "consume:" + (int)item._item + ":1");
                PlaytestLog.Event("Boss", "spear race lost p" + senderId + " -> consume ack");
            }
            catch (Exception ex) { WarnOnce("spear consume ack", ex); }
        }

        /// <summary>Client: host presentation event (Falke spear taken, Chimera rifle shot).</summary>
        public void ApplyBossEventOnClient(BossHitMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role == NetworkRole.Host || net.SceneMismatch) return;
            if (SceneFollowService.LocalIsTransient()) return;
            BossType type;
            var comp = FindLocalBossByWorldId(msg.WorldId, out type);
            if (comp == null) return;
            try
            {
                switch (msg.Kind)
                {
                    case BossHitKind.TakeSpear:
                    {
                        var b = comp as END_Boss;
                        var spears = b != null ? b.PickupSpears : null;
                        int idx = msg.Amount;
                        if (spears != null && idx >= 0 && idx < spears.Length && spears[idx] != null)
                            spears[idx].SetActive(false);
                        break;
                    }
                    case BossHitKind.ChimeraShot:
                    {
                        var lab = comp as LAB_ChimeraBoss;
                        if (lab != null) lab.gunShot = true; // native LateUpdate plays flash/projectile/SFX
                        break;
                    }
                }
            }
            catch (Exception ex) { WarnOnce("client boss event " + msg.Kind, ex); }
        }

        public void TickHost(LanNetworkManager net)
        {
            if (net.Role == NetworkRole.Client)
            {
                TickClient(net);
                return;
            }
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < SendInterval && !_forceSend) return;
            _sendTimer = 0f;
            _forceSend = false;

            float t0 = Time.realtimeSinceStartup;
            try
            {
            EnsureBossCache();
            _tickList.Clear();
            var list = _tickList;

            var endBosses = _endBosses;
            for (int i = 0; i < endBosses.Length; i++)
            {
                var b = endBosses[i];
                if (b == null) continue;
                Transform t;
                try { t = b.transform; }
                catch { continue; }
                if (t == null) continue;
                var anim = b.animator;
                int hp = 0;
                try { if (b.hitbox != null) hp = b.hitbox.HP; } catch (Exception e) { Guard.Swallow(e); }
                int animHash = 0;
                float animTime = 0f;
                if (anim != null)
                {
                    try
                    {
                        var si = anim.GetCurrentAnimatorStateInfo(0);
                        animHash = si.shortNameHash;
                        animTime = si.normalizedTime;
                    }
                    catch (Exception e) { Guard.Swallow(e); }
                }
                list.Add(new BossSnapshotNet
                {
                    Index = (short)i,
                    BossType = (byte)BossType.END_Boss,
                    PosX = t.position.x, PosY = t.position.y, PosZ = t.position.z,
                    RotY = t.eulerAngles.y,
                    WorldId = unchecked((long)Sync.WorldId.FromGameObject(b.gameObject)),
                    Alive = b.state != END_Boss.states.dead,
                    StateEnum = (byte)b.state,
                    Bool0 = b.started, Bool1 = b.survival, Bool2 = b.hit,
                    Bool3 = b.didWideAttack, Bool4 = b.deployed,
                    Int0 = b.stage, Int1 = b.ammo,
                    Float0 = b.cycle, Float1 = b.stagger, Float2 = b.timer,
                    AnimHash = animHash,
                    AnimTime = animTime,
                    Hp = hp,
                    Corrupt = b.corrupt
                });
            }

            var labs = _chimeras;
            for (int i = 0; i < labs.Length; i++)
            {
                var b = labs[i];
                if (b == null || b.gameObject == null) continue;
                Transform targetT = null;
                try
                {
                    if (b.Chimera != null && b.Chimera.gameObject != null)
                        targetT = b.Chimera.transform;
                    else
                        targetT = b.transform;
                }
                catch { continue; }
                if (targetT == null) continue;
                list.Add(new BossSnapshotNet
                {
                    Index = (short)i,
                    BossType = (byte)BossType.LAB_ChimeraBoss,
                    PosX = targetT.position.x, PosY = targetT.position.y, PosZ = targetT.position.z,
                    RotY = targetT.eulerAngles.y,
                    WorldId = unchecked((long)Sync.WorldId.FromGameObject(b.gameObject)),
                    Alive = b.inOperation && !b.done,
                    StateEnum = 0,
                    Bool0 = b.inOperation, Bool1 = b.done, Bool2 = b.isaUp,
                    Float0 = b.remainingBossTime
                });
            }

            var medBosses = _mynahs;
            for (int i = 0; i < medBosses.Length; i++)
            {
                var b = medBosses[i];
                if (b == null || b.gameObject == null) continue;
                Transform targetT = null;
                try
                {
                    if (b.Mynah != null && b.Mynah.gameObject != null)
                        targetT = b.Mynah.transform;
                    else
                        targetT = b.transform;
                }
                catch { continue; }
                if (targetT == null) continue;
                list.Add(new BossSnapshotNet
                {
                    Index = (short)i,
                    BossType = (byte)BossType.MED_MynahBoss,
                    PosX = targetT.position.x, PosY = targetT.position.y, PosZ = targetT.position.z,
                    RotY = targetT.eulerAngles.y,
                    WorldId = unchecked((long)Sync.WorldId.FromGameObject(b.gameObject)),
                    Alive = b.inProgress,
                    StateEnum = 0,
                    Bool0 = b.inProgress, Bool1 = b.phaseTwo, Bool2 = b.phaseThree,
                    Float0 = b.schonfrist
                });
            }

            if (list.Count > 0)
                net.SendBossState(list);
            }
            finally
            {
                HitchTrace.Cost("boss", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        public void OnBossStateReceived(BossStateMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;
            if (msg.Bosses == null) return;
            // Join dump / SceneFollow mid-load: applying now empties EnsureBossCache and
            // sticks _clientDisabled — later scene bosses keep local AI + miss phase snaps.
            if (SceneFollowService.LocalIsTransient()) return;
            if (net != null && net.SceneMismatch) return;

            if (!_clientDisabled)
            {
                DisableLocalAI();
                _clientDisabled = true;
            }

            for (int i = 0; i < msg.Bosses.Length; i++)
                ApplyBossState(msg.Bosses[i]);
        }

        private void ApplyBossState(BossSnapshotNet snap)
        {
            long hostID = snap.WorldId;

            MonoBehaviour comp;
            BossType type;
            if (_hostToLocal.TryGetValue(hostID, out var existing))
            {
                comp = existing.comp;
                type = existing.type;
                if (comp == null)
                {
                    _hostToLocal.Remove(hostID);
                    comp = FindLocalBossByWorldId(snap.WorldId, out type);
                    if (comp == null) return;
                    _hostToLocal[hostID] = (comp, type);
                }
            }
            else
            {
                comp = FindLocalBossByWorldId(snap.WorldId, out type);
                if (comp == null) return;
                _hostToLocal[hostID] = (comp, type);
            }

            try
            {
                if (comp == null || comp.gameObject == null) return;
                switch ((BossType)snap.BossType)
                {
                    case BossType.END_Boss:
                    {
                        // A hit landed since the last snap would be overwritten below: forward it first.
                        var net = LanNetworkManager.Instance;
                        if (net != null) ForwardLocalBossDamage(net, hostID, (END_Boss)comp);
                        ApplyEND((END_Boss)comp, snap);
                        _lastHp[hostID] = snap.Hp;
                        break;
                    }
                    case BossType.LAB_ChimeraBoss:
                        ApplyLAB((LAB_ChimeraBoss)comp, snap);
                        break;
                    case BossType.MED_MynahBoss:
                        ApplyMED((MED_MynahBoss)comp, snap);
                        break;
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        private static void ApplyEND(END_Boss b, BossSnapshotNet snap)
        {
            if (b == null || b.gameObject == null) return;
            var t = b.transform;
            if (t == null) return;
            try
            {
                t.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
                var rot = t.eulerAngles;
                rot.y = snap.RotY;
                t.eulerAngles = rot;
            }
            catch { return; }

            try { b.state = (END_Boss.states)snap.StateEnum; } catch (Exception e) { Guard.Swallow(e); }
            try { b.started = snap.Bool0; } catch (Exception e) { Guard.Swallow(e); }
            try { b.survival = snap.Bool1; } catch (Exception e) { Guard.Swallow(e); }
            try { b.hit = snap.Bool2; } catch (Exception e) { Guard.Swallow(e); }
            try { b.didWideAttack = snap.Bool3; } catch (Exception e) { Guard.Swallow(e); }
            try { b.deployed = snap.Bool4; } catch (Exception e) { Guard.Swallow(e); }
            try { b.stage = snap.Int0; } catch (Exception e) { Guard.Swallow(e); }
            try { b.ammo = snap.Int1; } catch (Exception e) { Guard.Swallow(e); }
            try { b.cycle = snap.Float0; } catch (Exception e) { Guard.Swallow(e); }
            try { b.stagger = snap.Float1; } catch (Exception e) { Guard.Swallow(e); }
            try { b.timer = snap.Float2; } catch (Exception e) { Guard.Swallow(e); }
            try { b.corrupt = snap.Corrupt; } catch (Exception e) { Guard.Swallow(e); }
            try
            {
                if (b.hitbox != null)
                    b.hitbox.HP = snap.Hp;
            }
            catch (Exception e) { Guard.Swallow(e); }

            if (b.animator != null && snap.AnimHash != 0)
            {
                try
                {
                    var si = b.animator.GetCurrentAnimatorStateInfo(0);
                    if (si.shortNameHash != snap.AnimHash || Mathf.Abs(si.normalizedTime - snap.AnimTime) > 0.1f)
                        b.animator.Play(snap.AnimHash, 0, snap.AnimTime);
                }
                catch (Exception e) { Guard.Swallow(e); }
            }

            // Client AI is disabled — Start/Stabbed/Update no longer drive arena doors,
            // invuln shields, or corrupt mesh. Mirror host presentation from the snap
            // values (not re-read fields) so a failed stage/corrupt write or mid-apply
            // corrupt toggle cannot leave arenas/shields/meshes on a stale combo.
            // (GameAssembly END_Boss.Start + <Stabbed>d__129.MoveNext + Update).
            SnapFalkePresentation(b, snap.Int0, snap.Corrupt);
            SnapStabPrompt(b);
        }

        /// <summary>
        /// Falke downed: native CheckDowned (host only, controller halted here) activates StabInteraction
        /// when the party holds the spear. Mirror it so a client can press the prompt; the press is
        /// forwarded to the host (BossActionPatches) which runs Stab().
        /// </summary>
        private static void SnapStabPrompt(END_Boss b)
        {
            try
            {
                var go = b.StabInteraction;
                if (go == null) return;
                bool want = false;
                if (b.state == END_Boss.states.downed)
                {
                    // Native CheckDowned enables StabInteraction only when hasItem(SpearItem) (ring-aware);
                    // `deployed` alone lets the boss go down but never shows the prompt.
                    try { want = b.SpearItem != null && InventoryManager.hasItem(b.SpearItem); }
                    catch (Exception ex) { Guard.Swallow(ex); want = false; }
                }
                if (go.activeSelf != want) go.SetActive(want);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[BossSync] stab prompt: " + ex.Message);
            }
        }

        /// <summary>
        /// Decompile: Arenas[i] active iff i equals stage; HeadSpears[i] active iff i below stage;
        /// FloatShields when stage at least 3 (wiki phase 4); FloatShields2 when stage at least 5 (phase 6);
        /// CorruptedMesh/NormalMesh follow corrupt (Update). BodySpears follow ammo/deployed via
        /// native SetBodySpearStates after those fields were snapped.
        /// </summary>
        private static void SnapFalkePresentation(END_Boss b, int stage, bool corrupt)
        {
            if (b == null) return;
            if (stage < 0) stage = 0;
            if (stage > 6) stage = 6;

            // Keep field mirror aligned with the presentation we just chose (stage regress /
            // corrupt toggle from a partial field write cannot desync GO active flags).
            try { b.stage = stage; } catch (Exception e) { Guard.Swallow(e); }
            try { b.corrupt = corrupt; } catch (Exception e) { Guard.Swallow(e); }

            try
            {
                var arenas = b.Arenas;
                if (arenas != null)
                {
                    int n = arenas.Length;
                    if (n > 6) n = 6;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            if (arenas[i] != null)
                                arenas[i].SetActive(i == stage);
                        }
                        catch (Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (Exception e) { Guard.Swallow(e); }

            try
            {
                var spears = b.HeadSpears;
                if (spears != null)
                {
                    int n = spears.Length;
                    if (n > 6) n = 6;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            if (spears[i] != null)
                                spears[i].SetActive(i < stage);
                        }
                        catch (Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (Exception e) { Guard.Swallow(e); }

            try
            {
                if (b.FloatShields != null)
                    b.FloatShields.SetActive(stage >= 3);
            }
            catch (Exception e) { Guard.Swallow(e); }
            try
            {
                if (b.FloatShields2 != null)
                    b.FloatShields2.SetActive(stage >= 5);
            }
            catch (Exception e) { Guard.Swallow(e); }

            try
            {
                if (b.CorruptedMesh != null)
                    b.CorruptedMesh.SetActive(corrupt);
            }
            catch (Exception e) { Guard.Swallow(e); }
            try
            {
                if (b.NormalMesh != null)
                    b.NormalMesh.SetActive(!corrupt);
            }
            catch (Exception e) { Guard.Swallow(e); }

            // Stabbed / DeploySpears also refresh BodySpears from ammo/deployed (already snapped).
            try { b.SetBodySpearStates(); } catch (Exception e) { Guard.Swallow(e); }
        }

        private static void ApplyLAB(LAB_ChimeraBoss b, BossSnapshotNet snap)
        {
            if (b == null || b.gameObject == null) return;
            if (b.Chimera != null && b.Chimera.gameObject != null)
            {
                try
                {
                    b.Chimera.transform.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
                    var rot = b.Chimera.transform.eulerAngles;
                    rot.y = snap.RotY;
                    b.Chimera.transform.eulerAngles = rot;
                }
                catch (Exception e) { Guard.Swallow(e); }
            }

            try { b.inOperation = snap.Bool0; } catch (Exception e) { Guard.Swallow(e); }
            try { b.done = snap.Bool1; } catch (Exception e) { Guard.Swallow(e); }
            try { b.remainingBossTime = snap.Float0; } catch (Exception e) { Guard.Swallow(e); }
            // Bossfight coroutine is host-only: mirror the Isa stand-up (GetUp trigger) on the edge.
            try
            {
                if (snap.Bool2 && !b.isaUp)
                {
                    b.isaUp = true;
                    if (b.IsaAnim != null) b.IsaAnim.SetTrigger(b.anim_GetUp);
                }
                else if (!snap.Bool2 && b.isaUp)
                    b.isaUp = false;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[BossSync] chimera isa: " + ex.Message);
            }
        }

        private static void ApplyMED(MED_MynahBoss b, BossSnapshotNet snap)
        {
            if (b == null || b.gameObject == null) return;
            if (b.Mynah != null && b.Mynah.gameObject != null)
            {
                try
                {
                    b.Mynah.transform.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
                    var rot = b.Mynah.transform.eulerAngles;
                    rot.y = snap.RotY;
                    b.Mynah.transform.eulerAngles = rot;
                }
                catch (Exception e) { Guard.Swallow(e); }
            }

            try { b.inProgress = snap.Bool0; } catch (Exception e) { Guard.Swallow(e); }
            try { b.phaseTwo = snap.Bool1; } catch (Exception e) { Guard.Swallow(e); }
            try { b.phaseThree = snap.Bool2; } catch (Exception e) { Guard.Swallow(e); }
            try { b.schonfrist = snap.Float0; } catch (Exception e) { Guard.Swallow(e); }
        }

        private MonoBehaviour FindLocalBossByWorldId(long worldIdLong, out BossType type)
        {
            ulong want = unchecked((ulong)worldIdLong);
            var hit = FindInBossCache(want, out type);
            if (hit != null) return hit;

            // Premature Ensure during transient/partial load sticks an empty cache —
            // only refresh when empty so other-scene WorldId misses do not FindObjects spam.
            if (_bossCacheReady && BossCacheEmpty())
            {
                _bossCacheReady = false;
                EnsureBossCache();
                if (_clientDisabled)
                    DisableLocalAI();
                hit = FindInBossCache(want, out type);
                if (hit != null) return hit;
            }

            type = BossType.END_Boss;
            return null;
        }

        private MonoBehaviour FindInBossCache(ulong want, out BossType type)
        {
            EnsureBossCache();

            var ends = _endBosses;
            for (int i = 0; i < ends.Length; i++)
            {
                var e = ends[i];
                if (e != null && Sync.WorldId.FromGameObject(e.gameObject) == want)
                {
                    type = BossType.END_Boss;
                    return e;
                }
            }

            var labs = _chimeras;
            for (int i = 0; i < labs.Length; i++)
            {
                var l = labs[i];
                if (l != null && Sync.WorldId.FromGameObject(l.gameObject) == want)
                {
                    type = BossType.LAB_ChimeraBoss;
                    return l;
                }
            }

            var meds = _mynahs;
            for (int i = 0; i < meds.Length; i++)
            {
                var m = meds[i];
                if (m != null && Sync.WorldId.FromGameObject(m.gameObject) == want)
                {
                    type = BossType.MED_MynahBoss;
                    return m;
                }
            }

            type = BossType.END_Boss;
            return null;
        }

        bool BossCacheEmpty()
        {
            return (_endBosses == null || _endBosses.Length == 0)
                && (_chimeras == null || _chimeras.Length == 0)
                && (_mynahs == null || _mynahs.Length == 0);
        }

        private void DisableLocalAI()
        {
            EnsureBossCache();
            int n = 0;
            var ends = _endBosses;
            for (int i = 0; i < ends.Length; i++)
                if (HaltBossController(ends[i])) n++;

            var labs = _chimeras;
            for (int i = 0; i < labs.Length; i++)
                if (HaltBossController(labs[i], keepEnabled: true)) n++;

            var meds = _mynahs;
            for (int i = 0; i < meds.Length; i++)
                if (HaltBossController(meds[i])) n++;

            ModRuntime.Log?.Msg("[BossSync] Disabled " + n + " boss controllers");
        }

        /// <summary>
        /// enabled=false stops Update/LateUpdate but not running coroutines (Bossfight /
        /// Stabbed / Airstrike). StopAllCoroutines first so mid-fight clients cannot keep
        /// advancing phase/nests locally while host snaps.
        /// </summary>
        /// <summary>
        /// keepEnabled (LAB Chimera): LateUpdate is the only thing besides Start/Bossfight and it plays the
        /// Isa rifle muzzle flash / projectile / SFX off the private gunShot flag, which the host relays
        /// (BossHit ChimeraShot). Disabling the component would leave clients with a silent, flash-less Isa
        /// (Ghidra LAB_ChimeraBoss.c LateUpdate). Coroutines still stop so only the host runs the fight.
        /// </summary>
        private static bool HaltBossController(MonoBehaviour b, bool keepEnabled = false)
        {
            if (b == null) return false;
            try { b.StopAllCoroutines(); } catch (Exception e) { Guard.Swallow(e); }
            if (keepEnabled) return true;
            try { b.enabled = false; } catch { return false; }
            return true;
        }

        public static void EnableLocalAI()
        {
            var ends = WorldLookup.All<END_Boss>();
            if (ends != null)
                for (int i = 0; i < ends.Length; i++)
                    if (ends[i] != null) ends[i].enabled = true;

            var labs = WorldLookup.All<LAB_ChimeraBoss>();
            if (labs != null)
                for (int i = 0; i < labs.Length; i++)
                    if (labs[i] != null) labs[i].enabled = true;

            var meds = WorldLookup.All<MED_MynahBoss>();
            if (meds != null)
                for (int i = 0; i < meds.Length; i++)
                    if (meds[i] != null) meds[i].enabled = true;
        }

        public void OnSceneChanged()
        {
            _clientDisabled = false;
            _hostToLocal.Clear();
            _lastHp.Clear();
            _spearTaker.Clear();
            _bossCacheReady = false;
            _endBosses = Array.Empty<END_Boss>();
            _adlers = Array.Empty<BOS_Adler>();
            _chimeras = Array.Empty<LAB_ChimeraBoss>();
            _mynahs = Array.Empty<MED_MynahBoss>();
            SyncRADation.Patches.KolibriAdlerAuthPatches.Clear();
        }

        public void Reset()
        {
            // StopNetwork may leave client-disabled bosses offline with AI off.
            try { EnableLocalAI(); } catch (Exception e) { Guard.Swallow(e); }
            OnSceneChanged();
            _sendTimer = 0f;
            _forceSend = false;
        }

        // PuzzleStateMessage ownership for Kolibri/Adler intensity (wire stays PuzzleType).
        internal static bool TryReadPuzzle(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.KolibriManager:
                {
                    var x = (KolibriManager)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.dead, false, false,
                        x.frequency, 0, 0, 0, x.intensity, x.radioIntensity);
                    return true;
                }
                case PuzzleType.BOS_Adler:
                {
                    var x = (BOS_Adler)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, false, false, false,
                        0, 0, 0, 0, x.intensity, x.progress);
                    return true;
                }
                default:
                    return false;
            }
        }

        internal static void ApplyKolibri(KolibriManager x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.dead = e.Bool0;
            x.frequency = e.Int0;
            x.intensity = e.Float0;
            x.radioIntensity = e.Float1;
            // Client Update recomputes glitch from local Elster/radio — hold host snaps.
            SyncRADation.Patches.KolibriAdlerAuthPatches.HoldKolibri(e.Bool0, e.Int0, e.Float0, e.Float1);
        }

        internal static void ApplyAdler(BOS_Adler x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.intensity = e.Float0;
            x.progress = e.Float1;
            SyncRADation.Patches.KolibriAdlerAuthPatches.HoldAdler(e.Float0, e.Float1);
        }
    }
}
