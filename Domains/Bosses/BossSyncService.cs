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
        private bool _clientDisabled;

        private const float SendInterval = 1f / 15f;

        private readonly Dictionary<long, (MonoBehaviour comp, BossType type)> _hostToLocal
            = new Dictionary<long, (MonoBehaviour comp, BossType type)>();

        private END_Boss[] _endBosses = Array.Empty<END_Boss>();
        private BOS_Adler[] _adlers = Array.Empty<BOS_Adler>();
        private LAB_ChimeraBoss[] _chimeras = Array.Empty<LAB_ChimeraBoss>();
        private MED_MynahBoss[] _mynahs = Array.Empty<MED_MynahBoss>();
        private bool _bossCacheReady;
        private readonly List<BossSnapshotNet> _tickList = new List<BossSnapshotNet>(8);

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
                    try { b.Elster = n; } catch { }
                    try { b.Target = n; } catch { }
                }
            }
            catch { }

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
                    if (n != null) { try { a.Elster = n; } catch { } }
                }
            }
            catch { }
        }

        static Transform FindNearest(Vector3 fromPos, LanNetworkManager net, PlayerProxyManager pm)
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

        public void TickHost(LanNetworkManager net)
        {
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < SendInterval) return;
            _sendTimer = 0f;

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
                try { if (b.hitbox != null) hp = b.hitbox.HP; } catch { }
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
                    catch { }
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
                    Bool0 = b.inOperation, Bool1 = b.done,
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
                        ApplyEND((END_Boss)comp, snap);
                        break;
                    case BossType.LAB_ChimeraBoss:
                        ApplyLAB((LAB_ChimeraBoss)comp, snap);
                        break;
                    case BossType.MED_MynahBoss:
                        ApplyMED((MED_MynahBoss)comp, snap);
                        break;
                }
            }
            catch { }
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

            try { b.state = (END_Boss.states)snap.StateEnum; } catch { }
            try { b.started = snap.Bool0; } catch { }
            try { b.survival = snap.Bool1; } catch { }
            try { b.hit = snap.Bool2; } catch { }
            try { b.didWideAttack = snap.Bool3; } catch { }
            try { b.deployed = snap.Bool4; } catch { }
            try { b.stage = snap.Int0; } catch { }
            try { b.ammo = snap.Int1; } catch { }
            try { b.cycle = snap.Float0; } catch { }
            try { b.stagger = snap.Float1; } catch { }
            try { b.timer = snap.Float2; } catch { }
            try { b.corrupt = snap.Corrupt; } catch { }
            try
            {
                if (b.hitbox != null)
                    b.hitbox.HP = snap.Hp;
            }
            catch { }

            if (b.animator != null && snap.AnimHash != 0)
            {
                try
                {
                    var si = b.animator.GetCurrentAnimatorStateInfo(0);
                    if (si.shortNameHash != snap.AnimHash || Mathf.Abs(si.normalizedTime - snap.AnimTime) > 0.1f)
                        b.animator.Play(snap.AnimHash, 0, snap.AnimTime);
                }
                catch { }
            }
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
                catch { }
            }

            try { b.inOperation = snap.Bool0; } catch { }
            try { b.done = snap.Bool1; } catch { }
            try { b.remainingBossTime = snap.Float0; } catch { }
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
                catch { }
            }

            try { b.inProgress = snap.Bool0; } catch { }
            try { b.phaseTwo = snap.Bool1; } catch { }
            try { b.phaseThree = snap.Bool2; } catch { }
            try { b.schonfrist = snap.Float0; } catch { }
        }

        private MonoBehaviour FindLocalBossByWorldId(long worldIdLong, out BossType type)
        {
            ulong want = unchecked((ulong)worldIdLong);
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

        private void DisableLocalAI()
        {
            EnsureBossCache();
            int n = 0;
            var ends = _endBosses;
            for (int i = 0; i < ends.Length; i++)
                if (ends[i] != null) { ends[i].enabled = false; n++; }

            var labs = _chimeras;
            for (int i = 0; i < labs.Length; i++)
                if (labs[i] != null) { labs[i].enabled = false; n++; }

            var meds = _mynahs;
            for (int i = 0; i < meds.Length; i++)
                if (meds[i] != null) { meds[i].enabled = false; n++; }

            ModRuntime.Log?.Msg("[BossSync] Disabled " + n + " boss controllers");
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
            _bossCacheReady = false;
            _endBosses = Array.Empty<END_Boss>();
            _adlers = Array.Empty<BOS_Adler>();
            _chimeras = Array.Empty<LAB_ChimeraBoss>();
            _mynahs = Array.Empty<MED_MynahBoss>();
        }

        public void Reset()
        {
            OnSceneChanged();
            _sendTimer = 0f;
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
        }

        internal static void ApplyAdler(BOS_Adler x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.intensity = e.Float0;
            x.progress = e.Float1;
        }
    }
}
