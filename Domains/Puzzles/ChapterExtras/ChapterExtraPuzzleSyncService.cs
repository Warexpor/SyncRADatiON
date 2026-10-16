using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>GunCase, AraNest, LAB_RifleQuest, LOV_Microfiche, MED_Adler_EVdoors: final-pose snaps.</summary>
    public sealed class ChapterExtraPuzzleSyncService
    {
        /// <summary>Opened = its interaction is gone / disabled or the pickup is enabled (the Open coroutine's end).</summary>
        internal static PuzzleStateEntry ReadGunCase(GunCase x, long wid)
        {
            bool opened = x.inter == null || !x.inter.enabled || (x.pickup != null && x.pickup.enabled);
            return Mk(PuzzleType.GunCase, wid, opened, false, false, 0, 0, 0, 0, 0);
        }

        internal static PuzzleStateEntry ReadAraNest(AraNest x, long wid)
            => Mk(PuzzleType.AraNest, wid, x.triggered, x.activated, x.dead, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadRifleQuest(LAB_RifleQuest x, long wid)
            => Mk(PuzzleType.LAB_RifleQuest, wid, x.awake, x.gone, x.rifle, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadMicrofiche(LOV_Microfiche x, long wid)
            => Mk(PuzzleType.LOV_Microfiche, wid, x.hasFiche, x.IsaVisited, x.IsaGone, 0, 0, 0, 0, 0);

        /// <summary>
        /// The durable pose is DoorL / DoorR localPosition (OpenDoors / CloseDoors coroutines lerp X by ±Distance; there
        /// is no open flag): Float0 / Float1 = local X, Bool0 = looks open.
        /// </summary>
        internal static PuzzleStateEntry ReadAdlerEvDoors(MED_Adler_EVdoors x, long wid)
        {
            float lx = x.DoorL != null ? x.DoorL.localPosition.x : 0f;
            float rx = x.DoorR != null ? x.DoorR.localPosition.x : 0f;
            return Mk(PuzzleType.MED_Adler_EVdoors, wid, AdlerLooksOpen(x, lx, rx), false, false, 0, 0, 0, 0, lx, rx);
        }

        internal static bool AdlerLooksOpen(MED_Adler_EVdoors x, float lx, float rx)
            => Mathf.Abs(lx) + Mathf.Abs(rx) >= AdlerDistance(x) * 0.5f;

        internal static float AdlerDistance(MED_Adler_EVdoors x)
        {
            float dist = x.Distance;
            return dist < 0.01f ? 20f : dist;
        }

        /// <summary>The Open coroutine's end (GameAssembly): openBox off, lid Y -115, pickup on.</summary>
        internal static void ApplyGunCase(GunCase x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            PuzzleSyncService.DisableOne(x.inter);
            if (x.openBox != null) x.openBox.enabled = false;
            if (x.pickup != null) x.pickup.enabled = true;
            if (x.lid != null) x.lid.localEulerAngles = new Vector3(0f, -115f, 0f);
            PuzzleSyncService.RevealPickups(x.gameObject);
            PuzzleSyncService.DisableInteractions(x);
        }

        /// <summary>
        /// triggered / dead are the nest's; activated is not applied: native Activate sets it from the local player's
        /// room entry (PlayerState.EnteredRoomEvent) and Update clears it once that player left, so it means "this
        /// peer's player is in the nest's room". Copying the host's value reset every nest a client stood at (activated
        /// false + triggered = Update puts the ARAR back to bed); the host runs Activate for a remote player in the
        /// room itself (PeerRoomEnemies).
        /// </summary>
        internal static void ApplyAraNest(AraNest x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (!Sync.NetGate.Host) SyncRADation.Patches.AraNestUpdatePeerPatch.NoteHost(x, e.Bool0);
            if (e.Bool0)
            {
                if (!x.triggered)
                    Native("aranest-trap", x.TriggerTrap);
                x.triggered = true;
            }
            if (!e.Bool2) return;
            x.dead = true;
            int hash = x.anim_LoadDead;
            PlayEnd(x.Nest, hash);
            PlayEnd(x.Ara, hash);
        }

        /// <summary>Load / WakeUp poses: holders by awake / gone, Isa by gone, rifle vs fake rifle (CheckRifle).</summary>
        internal static void ApplyRifleQuest(LAB_RifleQuest x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool awake = e.Bool0, gone = e.Bool1, rifle = e.Bool2;
            x.awake = awake;
            x.gone = gone;
            x.rifle = rifle;
            SetGo(x.UseItemHolder, !awake && !gone);
            SetGo(x.ObsHolder, awake && !gone);
            SetGo(x.Isa, !gone);
            SetGo(x.Rifle, rifle);
            SetGo(x.FakeRifle, !rifle);
            if (gone || awake)
                PlayEnd(x.anim, x.anim_Done);
        }

        /// <summary>Load / IsaDone: book and item interaction track hasFiche (not the BookScreen UI); Isa objects by visited / gone.</summary>
        internal static void ApplyMicrofiche(LOV_Microfiche x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.hasFiche = e.Bool0;
            x.IsaVisited = e.Bool1;
            x.IsaGone = e.Bool2;
            SetPersistent(x.book, e.Bool0);
            SetPersistent(x.ItemInter, !e.Bool0);
            SetGo(x.Isa, e.Bool1 && !e.Bool2);
            SetGo(x.IsaNote, e.Bool2);
            SetGo(x.IsaCutscene, !e.Bool1 && !e.Bool2);
        }

        /// <summary>
        /// Snap DoorL / DoorR local X from the wire and stop the open/close coroutines, so a late join / remount lands
        /// on the durable pose without a second Distance lerp (calling OpenDoors again would overshoot).
        /// </summary>
        internal static void ApplyAdlerEvDoors(MED_Adler_EVdoors x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.StopAllCoroutines();
            SnapX(x.DoorL, e.Float0);
            SnapX(x.DoorR, e.Float1);
        }

        static void SnapX(Transform door, float localX)
        {
            if (door == null) return;
            var p = door.localPosition;
            p.x = localX;
            door.localPosition = p;
        }

        // PersistentGameObject.setActive writes its UniqueId key to SProgress, Awake / OnEnable only read it (default
        // initialState). When the value they would read already is the target, only the GameObject follows: writing
        // it would give this peer's slot keys the host never wrote (test pilot LOV_Reeducation, story digest). The
        // object's own activeSelf is no guide: inside a chunk that never woke, Awake has not applied the key yet.
        static void SetPersistent(PersistentGameObject p, bool active)
        {
            if (p == null) return;
            bool stored = active;
            try
            {
                var uid = p.GetComponent<UniqueId>();
                if (uid != null && !string.IsNullOrEmpty(uid.id)) stored = SProgress.GetBool(uid.id, p.initialState);
                else stored = !active;
            }
            catch (System.Exception e) { Guard.Swallow(e); stored = !active; }
            if (stored != active) p.setActive(active);
            else if (p.gameObject.activeSelf != active) p.gameObject.SetActive(active);
        }

        static void SetGo(GameObject go, bool active)
        {
            if (go != null) go.SetActive(active);
        }

        /// <summary>Jump an animator to the end of a state (the Load pose).</summary>
        static void PlayEnd(Animator anim, int stateHash)
        {
            if (anim == null || stateHash == 0) return;
            anim.Play(stateHash, 0, 1f);
            anim.Play(stateHash);
        }
    }
}
