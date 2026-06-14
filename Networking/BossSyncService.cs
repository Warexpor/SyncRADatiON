// SyncRADation — host-authoritative boss sync: END_Boss, Chimera, Mynah (15Hz snapshot, client disable+apply)
using System.Collections.Generic;
using SyncRADation.Players;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class BossSyncService
    {
        private float _sendTimer;
        private bool _clientDisabled;

        private const float SendInterval = 1f / 15f;

        private readonly Dictionary<int, (MonoBehaviour comp, BossType type)> _hostToLocal
            = new Dictionary<int, (MonoBehaviour comp, BossType type)>();

        public void TickHost(LanNetworkManager net)
        {
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < SendInterval) return;
            _sendTimer = 0f;

            var list = new List<BossSnapshotNet>();

            var endBosses = GameObject.FindObjectsOfType<END_Boss>();
            for (int i = 0; i < endBosses.Length; i++)
            {
                var b = endBosses[i];
                if (b == null) continue;
                var t = b.transform;
                var anim = b.animator;
                list.Add(new BossSnapshotNet
                {
                    Index = (short)i,
                    BossType = (byte)BossType.END_Boss,
                    PosX = t.position.x, PosY = t.position.y, PosZ = t.position.z,
                    RotY = t.eulerAngles.y,
                    HostInstanceID = b.gameObject.GetInstanceID(),
                    Alive = b.state != END_Boss.states.dead,
                    StateEnum = (byte)b.state,
                    Bool0 = b.started, Bool1 = b.survival, Bool2 = b.hit,
                    Bool3 = b.didWideAttack, Bool4 = b.deployed,
                    Int0 = b.stage, Int1 = b.ammo,
                    Float0 = b.cycle, Float1 = b.stagger, Float2 = b.timer,
                    AnimHash = anim != null ? anim.GetCurrentAnimatorStateInfo(0).shortNameHash : 0,
                    AnimTime = anim != null ? anim.GetCurrentAnimatorStateInfo(0).normalizedTime : 0f
                });
            }

            var labBosses = GameObject.FindObjectsOfType<LAB_ChimeraBoss>();
            for (int i = 0; i < labBosses.Length; i++)
            {
                var b = labBosses[i];
                if (b == null) continue;
                Transform targetT = b.Chimera != null ? b.Chimera.transform : b.transform;
                list.Add(new BossSnapshotNet
                {
                    Index = (short)i,
                    BossType = (byte)BossType.LAB_ChimeraBoss,
                    PosX = targetT.position.x, PosY = targetT.position.y, PosZ = targetT.position.z,
                    RotY = targetT.eulerAngles.y,
                    HostInstanceID = b.gameObject.GetInstanceID(),
                    Alive = b.inOperation && !b.done,
                    StateEnum = 0,
                    Bool0 = b.inOperation, Bool1 = b.done,
                    Float0 = b.remainingBossTime
                });
            }

            var medBosses = GameObject.FindObjectsOfType<MED_MynahBoss>();
            for (int i = 0; i < medBosses.Length; i++)
            {
                var b = medBosses[i];
                if (b == null) continue;
                Transform targetT = b.Mynah != null ? b.Mynah.transform : b.transform;
                list.Add(new BossSnapshotNet
                {
                    Index = (short)i,
                    BossType = (byte)BossType.MED_MynahBoss,
                    PosX = targetT.position.x, PosY = targetT.position.y, PosZ = targetT.position.z,
                    RotY = targetT.eulerAngles.y,
                    HostInstanceID = b.gameObject.GetInstanceID(),
                    Alive = b.inProgress,
                    StateEnum = 0,
                    Bool0 = b.inProgress, Bool1 = b.phaseTwo, Bool2 = b.phaseThree,
                    Float0 = b.schonfrist
                });
            }

            if (list.Count > 0)
                net.SendBossState(list.ToArray());
        }

        public void TickClient(LanNetworkManager net)
        {
            if (net.Role == NetworkRole.Host) return;
        }

        public void OnBossStateReceived(BossStateMessage msg)
        {
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
            int hostID = snap.HostInstanceID;

            MonoBehaviour comp;
            BossType type;
            if (_hostToLocal.TryGetValue(hostID, out var existing))
            {
                comp = existing.comp;
                type = existing.type;
                if (comp == null)
                {
                    _hostToLocal.Remove(hostID);
                    comp = FindLocalBossByHostID(hostID, out type);
                    if (comp == null) return;
                    _hostToLocal[hostID] = (comp, type);
                }
            }
            else
            {
                comp = FindLocalBossByHostID(hostID, out type);
                if (comp == null) return;
                _hostToLocal[hostID] = (comp, type);
            }

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

        private static void ApplyEND(END_Boss b, BossSnapshotNet snap)
        {
            b.transform.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
            var rot = b.transform.eulerAngles;
            rot.y = snap.RotY;
            b.transform.eulerAngles = rot;

            b.state = (END_Boss.states)snap.StateEnum;
            b.started = snap.Bool0;
            b.survival = snap.Bool1;
            b.hit = snap.Bool2;
            b.didWideAttack = snap.Bool3;
            b.deployed = snap.Bool4;
            b.stage = snap.Int0;
            b.ammo = snap.Int1;
            b.cycle = snap.Float0;
            b.stagger = snap.Float1;
            b.timer = snap.Float2;

            if (b.animator != null && snap.AnimHash != 0)
            {
                try
                {
                    var si = b.animator.GetCurrentAnimatorStateInfo(0);
                    if (si.shortNameHash != snap.AnimHash || Mathf.Abs(si.normalizedTime - snap.AnimTime) > 0.1f)
                    {
                        foreach (var clip in b.animator.runtimeAnimatorController.animationClips)
                        {
                            if (Animator.StringToHash(clip.name) == snap.AnimHash)
                            {
                                b.animator.Play(clip.name, 0, snap.AnimTime);
                                break;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static void ApplyLAB(LAB_ChimeraBoss b, BossSnapshotNet snap)
        {
            if (b.Chimera != null)
            {
                b.Chimera.transform.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
                var rot = b.Chimera.transform.eulerAngles;
                rot.y = snap.RotY;
                b.Chimera.transform.eulerAngles = rot;
            }

            b.inOperation = snap.Bool0;
            b.done = snap.Bool1;
            b.remainingBossTime = snap.Float0;
        }

        private static void ApplyMED(MED_MynahBoss b, BossSnapshotNet snap)
        {
            if (b.Mynah != null)
            {
                b.Mynah.transform.position = new Vector3(snap.PosX, snap.PosY, snap.PosZ);
                var rot = b.Mynah.transform.eulerAngles;
                rot.y = snap.RotY;
                b.Mynah.transform.eulerAngles = rot;
            }

            b.inProgress = snap.Bool0;
            b.phaseTwo = snap.Bool1;
            b.phaseThree = snap.Bool2;
            b.schonfrist = snap.Float0;
        }

        private MonoBehaviour FindLocalBossByHostID(int hostInstanceID, out BossType type)
        {
            var ends = GameObject.FindObjectsOfType<END_Boss>();
            foreach (var e in ends)
            {
                if (e != null && e.gameObject.GetInstanceID() == hostInstanceID)
                {
                    type = BossType.END_Boss;
                    return e;
                }
            }

            var labs = GameObject.FindObjectsOfType<LAB_ChimeraBoss>();
            foreach (var l in labs)
            {
                if (l != null && l.gameObject.GetInstanceID() == hostInstanceID)
                {
                    type = BossType.LAB_ChimeraBoss;
                    return l;
                }
            }

            var meds = GameObject.FindObjectsOfType<MED_MynahBoss>();
            foreach (var m in meds)
            {
                if (m != null && m.gameObject.GetInstanceID() == hostInstanceID)
                {
                    type = BossType.MED_MynahBoss;
                    return m;
                }
            }

            var all = GameObject.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in all)
            {
                if (mb != null && mb.gameObject.GetInstanceID() == hostInstanceID)
                {
                    if (mb is END_Boss) { type = BossType.END_Boss; return mb; }
                    if (mb is LAB_ChimeraBoss) { type = BossType.LAB_ChimeraBoss; return mb; }
                    if (mb is MED_MynahBoss) { type = BossType.MED_MynahBoss; return mb; }
                }
            }

            type = BossType.END_Boss;
            return null;
        }

        private void DisableLocalAI()
        {
            var ends = GameObject.FindObjectsOfType<END_Boss>();
            foreach (var e in ends)
                if (e != null) e.enabled = false;

            var labs = GameObject.FindObjectsOfType<LAB_ChimeraBoss>();
            foreach (var l in labs)
                if (l != null) l.enabled = false;

            var meds = GameObject.FindObjectsOfType<MED_MynahBoss>();
            foreach (var m in meds)
                if (m != null) m.enabled = false;

            ModRuntime.Log?.Msg("[BossSync] Disabled " + (ends.Length + labs.Length + meds.Length) + " boss controllers");
        }

        public static void EnableLocalAI()
        {
            var ends = GameObject.FindObjectsOfType<END_Boss>();
            foreach (var e in ends)
                if (e != null) e.enabled = true;

            var labs = GameObject.FindObjectsOfType<LAB_ChimeraBoss>();
            foreach (var l in labs)
                if (l != null) l.enabled = true;

            var meds = GameObject.FindObjectsOfType<MED_MynahBoss>();
            foreach (var m in meds)
                if (m != null) m.enabled = true;
        }

        public void OnSceneChanged()
        {
            _clientDisabled = false;
            _hostToLocal.Clear();
        }

        public void Reset()
        {
            _clientDisabled = false;
            _hostToLocal.Clear();
            _sendTimer = 0f;
        }
    }
}
