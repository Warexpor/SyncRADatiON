using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Protocol 10 chapter extras: GunCase, AraNest, LAB_RifleQuest, LOV_Microfiche.
    /// Magpie-style final-pose snaps; host + client emit; late-join via full dump.
    /// </summary>
    public sealed class ChapterExtraPuzzleSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.GunCase:
                {
                    var x = (GunCase)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, IsGunCaseOpened(x), false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.AraNest:
                {
                    var x = (AraNest)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.triggered, x.activated, x.dead, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.LAB_RifleQuest:
                {
                    var x = (LAB_RifleQuest)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.awake, x.gone, x.rifle, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.LOV_Microfiche:
                {
                    var x = (LOV_Microfiche)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.hasFiche, x.IsaVisited, x.IsaGone, 0, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyGunCase(GunCase x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            SnapGunCase(x);
        }

        public static void ApplyAraNest(AraNest x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.activated = e.Bool1; } catch { }

            if (e.Bool0)
            {
                bool already = false;
                try { already = x.triggered; } catch { }
                if (!already)
                {
                    try { x.TriggerTrap(); } catch { }
                }
                try { x.triggered = true; } catch { }
            }

            if (e.Bool2)
                SnapAraNestDead(x);
        }

        public static void ApplyRifleQuest(LAB_RifleQuest x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.awake = e.Bool0; } catch { }
            try { x.gone = e.Bool1; } catch { }
            try { x.rifle = e.Bool2; } catch { }

            bool awake = e.Bool0;
            bool gone = e.Bool1;
            bool rifle = e.Bool2;

            // Load/WakeUp: UseItemHolder off + ObsHolder on when awake; gone clears Isa + both holders.
            SetGo(x.UseItemHolder, !awake && !gone);
            SetGo(x.ObsHolder, awake && !gone);
            SetGo(x.Isa, !gone);
            // CheckRifle: rifle GO vs FakeRifle
            SetGo(x.Rifle, rifle);
            SetGo(x.FakeRifle, !rifle);

            if (gone || awake)
                TryPlayAnim(x.anim, SafeAnimHash(() => x.anim_Done));
        }

        public static void ApplyMicrofiche(LOV_Microfiche x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.hasFiche = e.Bool0; } catch { }
            try { x.IsaVisited = e.Bool1; } catch { }
            try { x.IsaGone = e.Bool2; } catch { }

            // Load: book/ItemInter track hasFiche (not BookScreen UI).
            try
            {
                if (x.book != null)
                    x.book.setActive(e.Bool0);
            }
            catch { }
            try
            {
                if (x.ItemInter != null)
                    x.ItemInter.setActive(!e.Bool0);
            }
            catch { }

            // Load/IsaDone: Isa = visited && !gone; IsaNote = gone; IsaCutscene = !visited && !gone.
            SetGo(x.Isa, e.Bool1 && !e.Bool2);
            SetGo(x.IsaNote, e.Bool2);
            SetGo(x.IsaCutscene, !e.Bool1 && !e.Bool2);
        }

        static bool IsGunCaseOpened(GunCase x)
        {
            if (x == null) return false;
            try
            {
                if (x.inter == null || !x.inter.enabled)
                    return true;
            }
            catch { }
            try
            {
                if (x.pickup != null && x.pickup.enabled)
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Open coroutine (GameAssembly): disables openBox, lerps lid localEuler Y toward
        /// -115, then enables pickup. Snap final open without starting the coroutine.
        /// </summary>
        static void SnapGunCase(GunCase x)
        {
            if (x == null) return;
            try
            {
                if (x.inter != null)
                    PuzzleSyncService.DisableOne(x.inter);
            }
            catch { }
            try
            {
                if (x.openBox != null)
                    x.openBox.enabled = false;
            }
            catch { }
            try
            {
                if (x.pickup != null)
                    x.pickup.enabled = true;
            }
            catch { }
            try
            {
                if (x.lid != null)
                {
                    var e = x.lid.localEulerAngles;
                    e.x = 0f;
                    e.y = -115f;
                    e.z = 0f;
                    x.lid.localEulerAngles = e;
                }
            }
            catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
            try { PuzzleSyncService.DisableInteractions(x); } catch { }
        }

        static void SnapAraNestDead(AraNest x)
        {
            if (x == null) return;
            try { x.dead = true; } catch { }
            int hash = SafeAnimHash(() => x.anim_LoadDead);
            TryPlayAnim(x.Nest, hash);
            TryPlayAnim(x.Ara, hash);
        }

        static void SetGo(GameObject go, bool active)
        {
            if (go == null) return;
            try { go.SetActive(active); } catch { }
        }

        static int SafeAnimHash(System.Func<int> read)
        {
            try { return read(); } catch { return 0; }
        }

        static void TryPlayAnim(Animator anim, int stateHash)
        {
            if (anim == null || stateHash == 0) return;
            try { anim.Play(stateHash, 0, 1f); } catch { }
            try { anim.Play(stateHash); } catch { }
        }
    }
}
