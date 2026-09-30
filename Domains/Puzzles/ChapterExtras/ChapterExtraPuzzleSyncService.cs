using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Protocol 10–11 chapter extras: GunCase, AraNest, LAB_RifleQuest, LOV_Microfiche,
    /// MED_Adler_EVdoors. Magpie-style final-pose snaps; host + client emit; late-join dump.
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
                case PuzzleType.MED_Adler_EVdoors:
                {
                    var x = (MED_Adler_EVdoors)c;
                    float lx = 0f, rx = 0f;
                    try { if (x.DoorL != null) lx = x.DoorL.localPosition.x; } catch (System.Exception e) { Guard.Swallow(e); }
                    try { if (x.DoorR != null) rx = x.DoorR.localPosition.x; } catch (System.Exception e) { Guard.Swallow(e); }
                    float dist = 20f;
                    try { dist = x.Distance; } catch (System.Exception e) { Guard.Swallow(e); }
                    if (dist < 0.01f) dist = 20f;
                    // Durable pose is DoorL/DoorR localPosition (OpenDoors/CloseDoors
                    // coroutines lerp X by ±Distance; Melon has no open/solved bool).
                    bool open = Mathf.Abs(lx) + Mathf.Abs(rx) >= dist * 0.5f;
                    entry = PuzzleDomainUtil.Mk(type, wid, open, false, false, 0, 0, 0, 0, lx, rx);
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
            try { x.activated = e.Bool1; } catch (System.Exception ex) { Guard.Swallow(ex); }

            if (e.Bool0)
            {
                bool already = false;
                try { already = x.triggered; } catch (System.Exception ex) { Guard.Swallow(ex); }
                if (!already)
                {
                    try { x.TriggerTrap(); } catch (System.Exception ex) { Guard.Swallow(ex); }
                }
                try { x.triggered = true; } catch (System.Exception ex) { Guard.Swallow(ex); }
            }

            if (e.Bool2)
                SnapAraNestDead(x);
        }

        public static void ApplyRifleQuest(LAB_RifleQuest x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.awake = e.Bool0; } catch (System.Exception ex) { Guard.Swallow(ex); }
            try { x.gone = e.Bool1; } catch (System.Exception ex) { Guard.Swallow(ex); }
            try { x.rifle = e.Bool2; } catch (System.Exception ex) { Guard.Swallow(ex); }

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
            try { x.hasFiche = e.Bool0; } catch (System.Exception ex) { Guard.Swallow(ex); }
            try { x.IsaVisited = e.Bool1; } catch (System.Exception ex) { Guard.Swallow(ex); }
            try { x.IsaGone = e.Bool2; } catch (System.Exception ex) { Guard.Swallow(ex); }

            // Load: book/ItemInter track hasFiche (not BookScreen UI).
            try
            {
                if (x.book != null)
                    x.book.setActive(e.Bool0);
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            try
            {
                if (x.ItemInter != null)
                    x.ItemInter.setActive(!e.Bool0);
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }

            // Load/IsaDone: Isa = visited && !gone; IsaNote = gone; IsaCutscene = !visited && !gone.
            SetGo(x.Isa, e.Bool1 && !e.Bool2);
            SetGo(x.IsaNote, e.Bool2);
            SetGo(x.IsaCutscene, !e.Bool1 && !e.Bool2);
        }

        /// <summary>
        /// Snap DoorL/DoorR local X from the wire (Float0/Float1). Stop open/close
        /// coroutines so late-join / remount land on the durable pose without a
        /// second Distance lerp (calling OpenDoors again would overshoot).
        /// </summary>
        public static void ApplyAdlerEvDoors(MED_Adler_EVdoors x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.StopAllCoroutines(); } catch (System.Exception ex) { Guard.Swallow(ex); }
            SnapAdlerDoorX(x.DoorL, e.Float0);
            SnapAdlerDoorX(x.DoorR, e.Float1);
        }

        static void SnapAdlerDoorX(Transform door, float localX)
        {
            if (door == null) return;
            try
            {
                var p = door.localPosition;
                p.x = localX;
                door.localPosition = p;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static bool IsGunCaseOpened(GunCase x)
        {
            if (x == null) return false;
            try
            {
                if (x.inter == null || !x.inter.enabled)
                    return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.pickup != null && x.pickup.enabled)
                    return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.openBox != null)
                    x.openBox.enabled = false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.pickup != null)
                    x.pickup.enabled = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.DisableInteractions(x); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void SnapAraNestDead(AraNest x)
        {
            if (x == null) return;
            try { x.dead = true; } catch (System.Exception e) { Guard.Swallow(e); }
            int hash = SafeAnimHash(() => x.anim_LoadDead);
            TryPlayAnim(x.Nest, hash);
            TryPlayAnim(x.Ara, hash);
        }

        static void SetGo(GameObject go, bool active)
        {
            if (go == null) return;
            try { go.SetActive(active); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        static int SafeAnimHash(System.Func<int> read)
        {
            try { return read(); } catch { return 0; }
        }

        static void TryPlayAnim(Animator anim, int stateHash)
        {
            if (anim == null || stateHash == 0) return;
            try { anim.Play(stateHash, 0, 1f); } catch (System.Exception e) { Guard.Swallow(e); }
            try { anim.Play(stateHash); } catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
