// Host-authoritative END (Falke) / LAB Chimera / MED Mynah boss snapshots by WorldId; clients halt their copies.
// Kolibri / Adler ride PuzzleState (TryReadPuzzle / Apply* below) and are held by KolibriAdlerAuthPatches.
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

        /// <summary>Send on the next tick; inside a join dump, the tick sends the unicast snapshot right away.</summary>
        public void RequestFullSend() => _forceSend = true;

        // Scene boss cache: the controllers, their WorldIds (parallel arrays, taken once from the WorldId cache) and
        // the reverse map the snapshot apply resolves a wire id with.
        private struct BossRef
        {
            public MonoBehaviour Comp;
            public BossType Type;
        }
        private END_Boss[] _endBosses = Array.Empty<END_Boss>();
        private LAB_ChimeraBoss[] _chimeras = Array.Empty<LAB_ChimeraBoss>();
        private MED_MynahBoss[] _mynahs = Array.Empty<MED_MynahBoss>();
        private ulong[] _endIds = Array.Empty<ulong>();
        private ulong[] _chimeraIds = Array.Empty<ulong>();
        private ulong[] _mynahIds = Array.Empty<ulong>();
        private readonly Dictionary<long, BossRef> _byId = new Dictionary<long, BossRef>();
        private bool _bossCacheReady;
        private readonly List<BossSnapshotNet> _tickList = new List<BossSnapshotNet>(8);
        // Client: last host-confirmed Falke HP per WorldId. PlayerAttack mutates Hitbox.HP directly
        // (Ghidra PlayerAttack.c: Hitbox.HP -= dmg, no method to hook), so a drop below this value is
        // a local hit that must be forwarded to the host and rolled back.
        private readonly Dictionary<long, int> _lastHp = new Dictionary<long, int>();
        // Host: who got PickupSpears[idx] (first taker wins).
        private readonly Dictionary<int, int> _spearTaker = new Dictionary<int, int>();

        void EnsureBossCache()
        {
            if (_bossCacheReady) return;
            _endBosses = WorldLookup.All<END_Boss>() ?? Array.Empty<END_Boss>();
            _chimeras = WorldLookup.All<LAB_ChimeraBoss>() ?? Array.Empty<LAB_ChimeraBoss>();
            _mynahs = WorldLookup.All<MED_MynahBoss>() ?? Array.Empty<MED_MynahBoss>();
            _byId.Clear();
            _endIds = Index(_endBosses, BossType.END_Boss);
            _chimeraIds = Index(_chimeras, BossType.LAB_ChimeraBoss);
            _mynahIds = Index(_mynahs, BossType.MED_MynahBoss);
            _bossCacheReady = true;
        }

        ulong[] Index<T>(T[] bosses, BossType type) where T : MonoBehaviour
        {
            var ids = new ulong[bosses.Length];
            for (int i = 0; i < bosses.Length; i++)
            {
                try
                {
                    MonoBehaviour b = bosses[i];
                    if (b == null) continue;
                    ulong id = WorldId.FromGameObject(b.gameObject);
                    ids[i] = id;
                    long key = unchecked((long)id);
                    if (id != 0 && !_byId.ContainsKey(key)) _byId[key] = new BossRef { Comp = b, Type = type };
                }
                catch (Exception e) { Guard.Swallow(e); }
            }
            return ids;
        }

        /// <summary>WorldId of a boss controller (the scene-load WorldId cache, no hierarchy walk).</summary>
        public ulong BossId(MonoBehaviour b) => b == null ? 0UL : WorldId.FromGameObject(b.gameObject);

        bool BossCacheEmpty() => _endBosses.Length == 0 && _chimeras.Length == 0 && _mynahs.Length == 0;

        private MonoBehaviour FindLocalBoss(long wid, out BossType type)
        {
            EnsureBossCache();
            BossRef r;
            if (_byId.TryGetValue(wid, out r) && r.Comp != null)
            {
                type = r.Type;
                return r.Comp;
            }
            // A cache taken during a transient / partial load sticks empty: refresh only then, so other-scene
            // WorldId misses do not rescan.
            if (BossCacheEmpty())
            {
                _bossCacheReady = false;
                EnsureBossCache();
                if (_clientDisabled)
                    DisableLocalAI();
                if (_byId.TryGetValue(wid, out r) && r.Comp != null)
                {
                    type = r.Type;
                    return r.Comp;
                }
            }
            type = BossType.END_Boss;
            return null;
        }

        // ------------------------------------------------------------------ host tick

        public void TickHost(LanNetworkManager net)
        {
            if (NetGate.ClientRole)
            {
                TickClient(net);
                return;
            }
            if (!NetGate.Host) return;

            // Join/resync dump (unicast to the joiner): one snapshot now; the broadcast clock and a pending forced
            // send stay untouched so everybody else still gets theirs.
            if (!net.UnicastActive)
            {
                _sendTimer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                if (_sendTimer < SendInterval && !_forceSend) return;
                _sendTimer = 0f;
                _forceSend = false;
            }

            float t0 = Time.realtimeSinceStartup;
            try
            {
                EnsureBossCache();
                _tickList.Clear();
                BossSnapshotNet snap;
                for (int i = 0; i < _endBosses.Length; i++)
                {
                    var b = _endBosses[i];
                    if (b == null) continue;
                    try { if (ReadEnd(b, (short)i, _endIds[i], out snap)) _tickList.Add(snap); }
                    catch (Exception e) { Guard.Swallow("BossSync.SnapshotEND", e); }
                }
                for (int i = 0; i < _chimeras.Length; i++)
                {
                    var b = _chimeras[i];
                    if (b == null) continue;
                    try { if (ReadChimera(b, (short)i, _chimeraIds[i], out snap)) _tickList.Add(snap); }
                    catch (Exception e) { Guard.Swallow("BossSync.SnapshotLAB", e); }
                }
                for (int i = 0; i < _mynahs.Length; i++)
                {
                    var b = _mynahs[i];
                    if (b == null) continue;
                    try { if (ReadMynah(b, (short)i, _mynahIds[i], out snap)) _tickList.Add(snap); }
                    catch (Exception e) { Guard.Swallow("BossSync.SnapshotMED", e); }
                }
                if (_tickList.Count > 0)
                    net.BossHandlers.SendBossState(_tickList);
            }
            finally
            {
                HitchTrace.Cost("boss", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        static bool ReadEnd(END_Boss b, short index, ulong id, out BossSnapshotNet snap)
        {
            snap = default;
            var t = b.transform;
            if (t == null) return false;
            int hp = b.hitbox != null ? b.hitbox.HP : 0;
            int animHash = 0;
            float animTime = 0f;
            var anim = b.animator;
            if (anim != null)
            {
                var si = anim.GetCurrentAnimatorStateInfo(0);
                animHash = si.shortNameHash;
                animTime = si.normalizedTime;
            }
            var pos = t.position;
            var state = b.state;
            snap = new BossSnapshotNet
            {
                Index = index,
                BossType = (byte)BossType.END_Boss,
                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                RotY = t.eulerAngles.y,
                WorldId = unchecked((long)id),
                Alive = state != END_Boss.states.dead,
                StateEnum = (byte)state,
                Bool0 = b.started, Bool1 = b.survival, Bool2 = b.hit,
                Bool3 = b.didWideAttack, Bool4 = b.deployed,
                Int0 = b.stage, Int1 = b.ammo,
                Float0 = b.cycle, Float1 = b.stagger, Float2 = b.timer,
                AnimHash = animHash,
                AnimTime = animTime,
                Hp = hp,
                Corrupt = b.corrupt
            };
            return true;
        }

        static bool ReadChimera(LAB_ChimeraBoss b, short index, ulong id, out BossSnapshotNet snap)
        {
            snap = default;
            if (b.gameObject == null) return false;
            var body = b.Chimera != null && b.Chimera.gameObject != null ? b.Chimera.transform : b.transform;
            if (body == null) return false;
            var pos = body.position;
            snap = new BossSnapshotNet
            {
                Index = index,
                BossType = (byte)BossType.LAB_ChimeraBoss,
                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                RotY = body.eulerAngles.y,
                WorldId = unchecked((long)id),
                Alive = b.inOperation && !b.done,
                StateEnum = 0,
                Bool0 = b.inOperation, Bool1 = b.done, Bool2 = b.isaUp,
                Float0 = b.remainingBossTime
            };
            return true;
        }

        static bool ReadMynah(MED_MynahBoss b, short index, ulong id, out BossSnapshotNet snap)
        {
            snap = default;
            if (b.gameObject == null) return false;
            var body = b.Mynah != null && b.Mynah.gameObject != null ? b.Mynah.transform : b.transform;
            if (body == null) return false;
            var pos = body.position;
            snap = new BossSnapshotNet
            {
                Index = index,
                BossType = (byte)BossType.MED_MynahBoss,
                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                RotY = body.eulerAngles.y,
                WorldId = unchecked((long)id),
                Alive = b.inProgress,
                StateEnum = 0,
                Bool0 = b.inProgress, Bool1 = b.phaseTwo, Bool2 = b.phaseThree,
                Float0 = b.schonfrist
            };
            return true;
        }

        // ------------------------------------------------------------------ host: client requests

        /// <summary>
        /// Host: a client's boss request. Damage lowers the real Hitbox.HP (END_Boss.Update reacts to the
        /// change exactly as for a local hit); Stab/takeSpear run the native methods on the host sim.
        /// </summary>
        public void ApplyHitOnHost(BossHitMessage msg, int senderId)
        {
            BossType type;
            var b = FindLocalBoss(msg.WorldId, out type) as END_Boss;
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

        /// <summary>Host: who got PickupSpears[idx] (first taker wins; later calls keep the first).</summary>
        public void NoteSpearTaker(int idx, int playerId)
        {
            if (!_spearTaker.ContainsKey(idx)) _spearTaker[idx] = playerId;
        }

        /// <summary>The stab consumed SpearItem on the host bag only: retire it from the ring and every peer's bag.</summary>
        static void RevokeSpear(END_Boss b)
        {
            try
            {
                var item = b.SpearItem;
                if (item != null) PartyKeyRing.RevokeConsumed(item._item);
            }
            catch (Exception ex) { Guard.Swallow("BossSync.StabRevoke", ex); }
        }

        /// <summary>Tell a peer that lost the spear race to drop the copy its local UnityEvent added.</summary>
        static void AckConsumeSpear(END_Boss b, long wid, int senderId)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || senderId == net.LocalPlayerId) return;
            try
            {
                var item = b.SpearItem;
                if (item == null) return;
                net.InteractionHandlers.SendInteractionAck(senderId, wid, InteractionKind.UseItem, true,
                    "consume:" + (int)item._item + ":1");
                PlaytestLog.Event("Boss", "spear race lost p" + senderId + " -> consume ack");
            }
            catch (Exception ex) { Guard.Swallow("BossSync.SpearAck", ex); }
        }

        // ------------------------------------------------------------------ client

        /// <summary>Client per-frame: forward local hits on halted Falke copies to the host.</summary>
        private void TickClient(LanNetworkManager net)
        {
            if (!_clientDisabled || net.SceneMismatch || _lastHp.Count == 0) return;
            for (int i = 0; i < _endBosses.Length; i++)
                ForwardLocalBossDamage(net, unchecked((long)_endIds[i]), _endBosses[i]);
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
            catch (Exception ex) { Guard.Swallow("BossSync.ForwardDamage", ex); }
        }

        /// <summary>Client: host presentation event (Falke spear taken, Chimera rifle shot).</summary>
        public void ApplyBossEventOnClient(BossHitMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || NetGate.HostRole || net.SceneMismatch) return;
            if (SceneFollowService.LocalIsTransient()) return;
            BossType type;
            var comp = FindLocalBoss(msg.WorldId, out type);
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
            catch (Exception ex) { Guard.Swallow("BossSync.ClientEvent", ex); }
        }

        public void OnBossStateReceived(BossStateMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (NetGate.HostRole) return;
            if (msg.Bosses == null) return;
            // Join dump / SceneFollow mid-load: applying now would cache an empty boss set and stick
            // _clientDisabled, so later scene bosses would keep local AI and miss phase snaps.
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
            long wid = snap.WorldId;
            BossType type;
            var comp = FindLocalBoss(wid, out type);
            if (comp == null || (byte)type != snap.BossType) return;
            // Every snapshot carries the whole state (15 Hz): a failed apply is redone in full by the next one.
            try
            {
                if (comp.gameObject == null) return;
                switch (type)
                {
                    case BossType.END_Boss:
                    {
                        var b = (END_Boss)comp;
                        // A hit landed since the last snap would be overwritten below: forward it first.
                        var net = LanNetworkManager.Instance;
                        if (net != null) ForwardLocalBossDamage(net, wid, b);
                        ApplyEND(b, snap);
                        _lastHp[wid] = snap.Hp;
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
            catch (Exception e)
            {
                switch (type)
                {
                    case BossType.END_Boss: Guard.Swallow("BossSync.ApplyEND", e); break;
                    case BossType.LAB_ChimeraBoss: Guard.Swallow("BossSync.ApplyLAB", e); break;
                    default: Guard.Swallow("BossSync.ApplyMED", e); break;
                }
            }
        }

        private static void ApplyEND(END_Boss b, BossSnapshotNet snap)
        {
            var t = b.transform;
            if (t == null) return;
            t.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
            var rot = t.eulerAngles;
            rot.y = snap.RotY;
            t.eulerAngles = rot;

            b.state = (END_Boss.states)snap.StateEnum;
            b.started = snap.Bool0;
            b.survival = snap.Bool1;
            b.hit = snap.Bool2;
            b.didWideAttack = snap.Bool3;
            b.deployed = snap.Bool4;
            b.ammo = snap.Int1;
            b.cycle = snap.Float0;
            b.stagger = snap.Float1;
            b.timer = snap.Float2;
            if (b.hitbox != null)
                b.hitbox.HP = snap.Hp;

            var anim = b.animator;
            if (anim != null && snap.AnimHash != 0)
            {
                var si = anim.GetCurrentAnimatorStateInfo(0);
                if (si.shortNameHash != snap.AnimHash || Mathf.Abs(si.normalizedTime - snap.AnimTime) > 0.1f)
                    anim.Play(snap.AnimHash, 0, snap.AnimTime);
            }

            // Client AI is halted, so Start / Stabbed / Update no longer drive arena doors, invuln shields or the
            // corrupt mesh. Mirror them from the snap values (Ghidra END_Boss.c Start, <Stabbed>d__129.MoveNext,
            // Update); stage / corrupt are written there too.
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
            var go = b.StabInteraction;
            if (go == null) return;
            // Native CheckDowned enables StabInteraction only when hasItem(SpearItem) (ring-aware);
            // `deployed` alone lets the boss go down but never shows the prompt.
            bool want = b.state == END_Boss.states.downed
                && b.SpearItem != null && InventoryManager.hasItem(b.SpearItem);
            if (go.activeSelf != want) go.SetActive(want);
        }

        /// <summary>
        /// Decompile: Arenas[i] active iff i equals stage; HeadSpears[i] active iff i below stage;
        /// FloatShields when stage at least 3 (wiki phase 4); FloatShields2 when stage at least 5 (phase 6);
        /// CorruptedMesh/NormalMesh follow corrupt (Update). BodySpears follow ammo/deployed via
        /// native SetBodySpearStates after those fields were snapped.
        /// </summary>
        private static void SnapFalkePresentation(END_Boss b, int stage, bool corrupt)
        {
            if (stage < 0) stage = 0;
            if (stage > 6) stage = 6;
            b.stage = stage;
            b.corrupt = corrupt;

            var arenas = b.Arenas;
            if (arenas != null)
            {
                int n = Mathf.Min(arenas.Length, 6);
                for (int i = 0; i < n; i++)
                {
                    if (arenas[i] != null)
                        arenas[i].SetActive(i == stage);
                }
            }

            var spears = b.HeadSpears;
            if (spears != null)
            {
                int n = Mathf.Min(spears.Length, 6);
                for (int i = 0; i < n; i++)
                {
                    if (spears[i] != null)
                        spears[i].SetActive(i < stage);
                }
            }

            if (b.FloatShields != null) b.FloatShields.SetActive(stage >= 3);
            if (b.FloatShields2 != null) b.FloatShields2.SetActive(stage >= 5);
            if (b.CorruptedMesh != null) b.CorruptedMesh.SetActive(corrupt);
            if (b.NormalMesh != null) b.NormalMesh.SetActive(!corrupt);

            // Stabbed / DeploySpears also refresh BodySpears from ammo/deployed (already snapped).
            b.SetBodySpearStates();
        }

        private static void ApplyLAB(LAB_ChimeraBoss b, BossSnapshotNet snap)
        {
            var body = b.Chimera;
            if (body != null && body.gameObject != null)
            {
                var bt = body.transform;
                bt.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
                var rot = bt.eulerAngles;
                rot.y = snap.RotY;
                bt.eulerAngles = rot;
            }

            b.inOperation = snap.Bool0;
            b.done = snap.Bool1;
            b.remainingBossTime = snap.Float0;
            // The Bossfight coroutine is host-only: mirror the Isa stand-up (GetUp trigger) on the edge.
            if (snap.Bool2 && !b.isaUp)
            {
                b.isaUp = true;
                if (b.IsaAnim != null) b.IsaAnim.SetTrigger(b.anim_GetUp);
            }
            else if (!snap.Bool2 && b.isaUp)
                b.isaUp = false;
        }

        private static void ApplyMED(MED_MynahBoss b, BossSnapshotNet snap)
        {
            var body = b.Mynah;
            if (body != null && body.gameObject != null)
            {
                var bt = body.transform;
                bt.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
                var rot = bt.eulerAngles;
                rot.y = snap.RotY;
                bt.eulerAngles = rot;
            }

            b.inProgress = snap.Bool0;
            b.phaseTwo = snap.Bool1;
            b.phaseThree = snap.Bool2;
            b.schonfrist = snap.Float0;
        }

        private void DisableLocalAI()
        {
            EnsureBossCache();
            int n = 0;
            for (int i = 0; i < _endBosses.Length; i++)
                if (HaltBossController(_endBosses[i])) n++;
            for (int i = 0; i < _chimeras.Length; i++)
                if (HaltBossController(_chimeras[i], keepEnabled: true)) n++;
            for (int i = 0; i < _mynahs.Length; i++)
                if (HaltBossController(_mynahs[i])) n++;
            ModRuntime.Log?.Msg("[BossSync] Disabled " + n + " boss controllers");
        }

        /// <summary>
        /// enabled=false stops Update/LateUpdate but not running coroutines (Bossfight / Stabbed / Airstrike):
        /// StopAllCoroutines first so mid-fight clients cannot keep advancing phase/nests locally while the host snaps.
        /// keepEnabled (LAB Chimera): LateUpdate is the only thing besides Start/Bossfight and it plays the
        /// Isa rifle muzzle flash / projectile / SFX off the private gunShot flag, which the host relays
        /// (BossHit ChimeraShot). Disabling the component would leave clients with a silent, flash-less Isa
        /// (Ghidra LAB_ChimeraBoss.c LateUpdate). Coroutines still stop so only the host runs the fight.
        /// </summary>
        private static bool HaltBossController(MonoBehaviour b, bool keepEnabled = false)
        {
            if (b == null) return false;
            try
            {
                b.StopAllCoroutines();
                if (!keepEnabled) b.enabled = false;
                return true;
            }
            catch (Exception e) { Guard.Swallow("BossSync.Halt", e); return false; }
        }

        public static void EnableLocalAI()
        {
            Enable(WorldLookup.All<END_Boss>());
            Enable(WorldLookup.All<LAB_ChimeraBoss>());
            Enable(WorldLookup.All<MED_MynahBoss>());
        }

        static void Enable<T>(T[] bosses) where T : MonoBehaviour
        {
            if (bosses == null) return;
            for (int i = 0; i < bosses.Length; i++)
            {
                MonoBehaviour b = bosses[i];
                if (b != null) b.enabled = true;
            }
        }

        /// <summary>SessionReset (also after a wipe reload in the same scene): the spears are back on the floor, so a
        /// stale first taker must not suppress the next race's duplicate-spear ack.</summary>
        public void ResetSession()
        {
            _spearTaker.Clear();
        }

        public void OnSceneChanged()
        {
            _clientDisabled = false;
            _lastHp.Clear();
            _spearTaker.Clear();
            _bossCacheReady = false;
            _byId.Clear();
            _endBosses = Array.Empty<END_Boss>();
            _chimeras = Array.Empty<LAB_ChimeraBoss>();
            _mynahs = Array.Empty<MED_MynahBoss>();
            _endIds = Array.Empty<ulong>();
            _chimeraIds = Array.Empty<ulong>();
            _mynahIds = Array.Empty<ulong>();
        }

        public void Reset()
        {
            // StopNetwork may leave client-disabled bosses offline with AI off.
            try { EnableLocalAI(); } catch (Exception e) { Guard.Swallow(e); }
            OnSceneChanged();
            _sendTimer = 0f;
            _forceSend = false;
        }

        // ------------------------------------------------------------------ PuzzleState (Kolibri / Adler)

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
            // Client Update recomputes glitch from local Elster/radio: hold the host snap.
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
