using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>InteractiveLock*, Keypad3D/ROT_Keypad, Dial, Number, Multi, DoorLock*, Biodome, DET_ServiceLock read/apply.</summary>
    public sealed class LockSyncService
    {
        internal static PuzzleStateEntry ReadInteractive(InteractiveLock x, long wid)
            => Mk(PuzzleType.InteractiveLock, wid, x.locked, false, false, 0, 0, 0, 0, 0);

        /// <summary>Bool1 = traverse plate (or a flavor seal's permanent plate).</summary>
        internal static PuzzleStateEntry ReadInteractiveSingle(InteractiveLockSingle x, long wid)
        {
            bool locked = x.door != null && x.door.locked;
            bool plate = DoorNative.TraversePlateActive(x);
            if (!plate && x.key == null && (x.door == null || !x.door.open))
                plate = DoorNative.IsFlavorSeal(x.gameObject) || (x.door != null && DoorNative.IsFlavorSeal(x.door.gameObject));
            return Mk(PuzzleType.InteractiveLockSingle, wid, locked, plate, false, 0, 0, 0, 0, 0);
        }

        // Keypads: native blocked (the 0.3 s button-push lockout) is per player and not on the wire: syncing it
        // could leave the other player's keypad deaf. Ints: the shared code + last press (KeypadLive).
        internal static PuzzleStateEntry ReadKeypad3D(Keypad3D x, long wid)
        {
            int i0, i1, i2, i3;
            KeypadLive.PackCode(unchecked((ulong)wid), x.code, out i0, out i1, out i2, out i3);
            return Mk(PuzzleType.Keypad3D, wid, x.solved || x.opening, x.opening, false, i0, i1, i2, i3, 0);
        }

        internal static PuzzleStateEntry ReadRotKeypad(ROT_Keypad x, long wid)
        {
            int i0, i1, i2, i3;
            KeypadLive.PackCode(unchecked((ulong)wid), x.code, out i0, out i1, out i2, out i3);
            return Mk(PuzzleType.ROT_Keypad, wid, x.solved || x.opening, x.opening, false, i0, i1, i2, i3, 0);
        }

        internal static PuzzleStateEntry ReadDial(ROT_DialLock x, long wid)
            => Mk(PuzzleType.DialLock, wid, x.solved, false, false, x.A, x.B, x.C, x.D, 0);

        internal static PuzzleStateEntry ReadNumber(NumberLockNew x, long wid)
            => Mk(PuzzleType.NumberLockNew, wid, x.locked, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadDoorLockPuzzle(DoorLockPuzzle x, long wid)
            => Mk(PuzzleType.DoorLockPuzzle, wid, x.locked, false, false, 0, 0, 0, 0, 0);

        /// <summary>Decompile MED/LAB_MultiLock element bools → Int0 bits.</summary>
        internal static bool TryReadMulti(Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            var med = c as MED_MultiLock;
            if (med != null)
            {
                entry = Mk(PuzzleType.MultiLock, wid, med.unlocked, false, false, PackMedBits(med), 0, 0, 0, 0);
                return true;
            }
            var lab = c as LAB_MultiLock;
            if (lab == null) return false;
            entry = Mk(PuzzleType.MultiLock, wid, lab.unlocked, false, false, PackLabBits(lab), 0, 0, 0, 0);
            return true;
        }

        internal static PuzzleStateEntry ReadDoorLockEvent(DoorLockEventInteraction x, long wid)
            => Mk(PuzzleType.DoorLockEventInteraction, wid, x.done, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadBiodome(BiodomeDoorLock x, long wid)
            => Mk(PuzzleType.BiodomeDoorLock, wid, !x.hasLock, false, false, x.KeyLevel, 0, 0, 0, 0);

        /// <summary>Int0 = 4×3-bit pinning pack (0..precision).</summary>
        internal static PuzzleStateEntry ReadServiceLock(DET_ServiceLock x, long wid)
        {
            bool ok = x.solved != null && x.solved.solved;
            return Mk(PuzzleType.DET_ServiceLock, wid, ok, false, false, PackServiceLockPins(x), 0, 0, 0, 0);
        }

        /// <summary>
        /// Decompile InteractiveLock: Update copies locked -> door.locked every frame and delayedOpen only sets
        /// door.open (door visuals ride DoorState). Durable consequence = unlocked door, on every apply.
        /// </summary>
        internal static void ApplyInteractive(InteractiveLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.locked;
            x.locked = e.Bool0;
            PuzzleEdge.Solved("InteractiveLock", !was, !e.Bool0, () =>
            {
                if (x.door != null)
                    PuzzleSyncService.UnlockDoorObject(x.door.gameObject);
                PuzzleSyncService.TryUnlockDoors(x.gameObject);
            });
        }

        /// <summary>Flags + plate always snap (incl. the dump).</summary>
        internal static void ApplyInteractiveSingle(InteractiveLockSingle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (DoorNative.IsFlavorSeal(x.gameObject) || (x.door != null && DoorNative.IsFlavorSeal(x.door.gameObject)))
            {
                if (x.door != null) x.door.locked = true;
                return;
            }
            if (e.Bool0)
            {
                if (x.door != null) x.door.locked = true;
                if (e.Bool1)
                    DoorNative.ApplyLockPlate(x, true);
                return;
            }
            if (x.door != null)
            {
                x.door.locked = false;
                PuzzleSyncService.UnlockDoorObject(x.door.gameObject);
            }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            DoorNative.ApplyLockPlate(x, false);
        }

        /// <summary>
        /// Decompile Keypad3D: the wheel turn sets opening and openDoor (an IEnumerator, so a bare call never runs)
        /// lerps Door to Euler(0,-100,0). Peers snap that pose (what LoadState does) on every apply.
        /// </summary>
        internal static void ApplyKeypad3D(Keypad3D x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool wasOpening = x.opening;
            if (!x.solved || e.Bool0) KeypadLive.ApplyKeypad3D(x, e);
            x.solved = e.Bool0;
            x.opening = e.Bool1;
            if (!e.Bool0) return;
            if (e.Bool1)
                PuzzleEdge.Solved("Keypad3D", wasOpening, true, () =>
                {
                    if (x.Door != null) x.Door.localRotation = Quaternion.Euler(0f, -100f, 0f);
                });
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>
        /// onSuccess (scene data ROT_Rotfront KeypadLogic): Door Connection (35) Unlock + exitEvent + Event SetActive
        /// + dimPOI; native LoadState fires onLoad, the same minus exitEvent. TryUnlockDoors (FindInParents) misses
        /// that door, which sits under a separate DoorConnections tree. A remounted room resets solved, so a held
        /// re-snap is a !was edge again and takes onLoad.
        /// </summary>
        internal static void ApplyRotKeypad(ROT_Keypad x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.solved;
            if (!x.solved || e.Bool0) KeypadLive.ApplyRotKeypad(x, e);
            x.solved = e.Bool0;
            x.opening = e.Bool1;
            if (!e.Bool0) return;
            PuzzleEdge.Solved("ROT_Keypad", was, true,
                durable: () => { if (!was) PuzzleEdge.Invoke(x.onLoad); },
                onLive: () => PuzzleEdge.Invoke(x.onSuccess), at: x);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        internal static void ApplyDial(ROT_DialLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0;
            x.A = e.Int0; x.B = e.Int1; x.C = e.Int2; x.D = e.Int3;
            if (x.a != null) x.a.localEulerAngles = new Vector3(0, e.Int0 * 36, 0);
            if (x.b != null) x.b.localEulerAngles = new Vector3(0, e.Int1 * 36, 0);
            if (x.c != null) x.c.localEulerAngles = new Vector3(0, e.Int2 * 36, 0);
            if (x.d != null) x.d.localEulerAngles = new Vector3(0, e.Int3 * 36, 0);
            if (!e.Bool0) return;
            if (x.lockObject != null) x.lockObject.SetActive(false);
            if (x.Door != null) PuzzleSyncService.UnlockDoorObject(x.Door.gameObject);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            PuzzleSyncService.DisableInteractions(x);
        }

        internal static void ApplyNumber(NumberLockNew x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.locked = e.Bool0;
            if (!e.Bool0) UnlockDoor(x.gameObject, x.door, x.doorInt);
        }

        internal static void ApplyDoorLockPuzzle(DoorLockPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.locked = e.Bool0;
            if (!e.Bool0) UnlockDoor(x.gameObject, x.door, x.doorInt);
        }

        static void UnlockDoor(GameObject self, Doorway_Double door, Interaction doorInt)
        {
            if (door != null)
            {
                door.locked = false;
                PuzzleSyncService.UnlockDoorObject(door.gameObject);
            }
            if (doorInt != null) PuzzleSyncService.DisableOne(doorInt);
            PuzzleSyncService.TryUnlockDoors(self);
        }

        /// <summary>
        /// Native UnlockKey (live) invokes onUnlocked then onUnlockedLate; OnEnable (load) invokes onLoadUnlocked when
        /// unlocked. Scene data: onUnlocked = dimPOI + exitEvent (+ Play on LAB), onUnlockedLate = onLoadUnlocked =
        /// SetActive Inter on / EventInter off. Live in-room edge = both live events; everything else = onLoadUnlocked once.
        /// onEarthKeyUnlocked (a mid-key dimPOI) is not replayed.
        /// </summary>
        internal static void ApplyMulti(Component c, PuzzleStateEntry e)
        {
            var med = c as MED_MultiLock;
            if (med != null)
            {
                bool was = med.unlocked;
                med.unlocked = e.Bool0;
                UnpackMedBits(med, e.Int0);
                PaintMed(med);
                if (!e.Bool0) return;
                PuzzleEdge.Solved("MED_MultiLock", was, true,
                    durable: () => { if (!was) PuzzleEdge.Invoke(med.onLoadUnlocked); },
                    onLive: () =>
                    {
                        PuzzleEdge.Invoke(med.onUnlocked);
                        PuzzleEdge.Invoke(med.onUnlockedLate);
                    }, at: med);
                PuzzleSyncService.TryUnlockDoors(med.gameObject);
                return;
            }
            var lab = c as LAB_MultiLock;
            if (lab == null) return;
            bool labWas = lab.unlocked;
            lab.unlocked = e.Bool0;
            int before = PackLabBits(lab);
            UnpackLabBits(lab, e.Int0);
            PaintLab(lab);
            // Native LAB insertKey plays insertPlate for the plate going in (MED's card sound is a scene emitter on
            // the item interaction, relayed by the emitter sync).
            if (PuzzleFx.LiveApply && (e.Int0 & ~before) != 0)
                PuzzleFx.Press(lab, lab.insertPlate);
            if (!e.Bool0) return;
            PuzzleEdge.Solved("LAB_MultiLock", labWas, true,
                durable: () => { if (!labWas) PuzzleEdge.Invoke(lab.onLoadUnlocked); },
                onLive: () =>
                {
                    PuzzleEdge.Invoke(lab.onUnlocked);
                    PuzzleEdge.Invoke(lab.onUnlockedLate);
                }, at: lab);
            PuzzleSyncService.TryUnlockDoors(lab.gameObject);
        }

        // Neither class has an Update: the cards, slot icons and lights are painted only by OnEnable (once, from the
        // save) and the insertKey coroutine, which MED runs as a cutscene (gameState 4 → 7) and so cannot be
        // replayed on a player who is not in the screen. Same paint as native OnEnable (Ghidra MED_MultiLock.c /
        // LAB_MultiLock.c): card shown + slid in (x 0), _s on, _i off, lights green when set; else red, _i on.
        static void PaintMed(MED_MultiLock x)
        {
            PaintMedOne(x, x.Fire, x.FireCard, x.FireCard_s, x.FireCard_i, x.FireLight, x.FireLight_s, x.FireLight_L);
            PaintMedOne(x, x.Earth, x.EarthCard, x.EarthCard_s, x.EarthCard_i, x.EarthLight, x.EarthLight_s, x.EarthLight_L);
            PaintMedOne(x, x.Water, x.WaterCard, x.WaterCard_s, x.WaterCard_i, x.WaterLight, x.WaterLight_s, x.WaterLight_L);
            PaintMedOne(x, x.Air, x.AirCard, x.AirCard_s, x.AirCard_i, x.AirLight, x.AirLight_s, x.AirLight_L);
            PaintMedOne(x, x.Gold, x.GoldCard, x.GoldCard_s, x.GoldCard_i, x.GoldLight, x.GoldLight_s, x.GoldLight_L);
        }

        static void PaintMedOne(MED_MultiLock x, bool on, Transform card, GameObject s, GameObject i,
            SpriteRenderer light, SpriteRenderer lightS, Light lightL)
        {
            if (card != null)
            {
                card.gameObject.SetActive(on);
                if (on) { var p = card.localPosition; p.x = 0f; card.localPosition = p; }
            }
            if (s != null) s.SetActive(on);
            if (i != null) i.SetActive(!on);
            Color col = on ? x.green : x.red;
            if (light != null) light.color = col;
            if (lightS != null) lightS.color = col;
            if (lightL != null) lightL.color = col;
        }

        static void PaintLab(LAB_MultiLock x)
        {
            PaintLabOne(x.Fire, x.FireCard, x.FireCard_s, x.FireCard_i);
            PaintLabOne(x.Earth, x.EarthCard, x.EarthCard_s, x.EarthCard_i);
            PaintLabOne(x.Water, x.WaterCard, x.WaterCard_s, x.WaterCard_i);
            PaintLabOne(x.Air, x.AirCard, x.AirCard_s, x.AirCard_i);
            PaintLabOne(x.Gold, x.GoldCard, x.GoldCard_s, x.GoldCard_i);
            PaintLabOne(x.Star, x.StarCard, x.StarCard_s, x.StarCard_i);
        }

        static void PaintLabOne(bool on, GameObject card, GameObject s, GameObject i)
        {
            if (card != null) card.SetActive(on);
            if (s != null) s.SetActive(on);
            if (i != null) i.SetActive(!on);
        }

        // Decompile MED/LAB_MultiLock: Fire=1 Earth=2 Water=4 Air=8 Gold=16 Star=32 (LAB).
        static int PackMedBits(MED_MultiLock x)
            => (x.Fire ? 1 : 0) | (x.Earth ? 2 : 0) | (x.Water ? 4 : 0) | (x.Air ? 8 : 0) | (x.Gold ? 16 : 0);

        static int PackLabBits(LAB_MultiLock x)
            => (x.Fire ? 1 : 0) | (x.Earth ? 2 : 0) | (x.Water ? 4 : 0) | (x.Air ? 8 : 0) | (x.Gold ? 16 : 0)
                | (x.Star ? 32 : 0);

        static void UnpackMedBits(MED_MultiLock x, int bits)
        {
            x.Fire = (bits & 1) != 0;
            x.Earth = (bits & 2) != 0;
            x.Water = (bits & 4) != 0;
            x.Air = (bits & 8) != 0;
            x.Gold = (bits & 16) != 0;
        }

        static void UnpackLabBits(LAB_MultiLock x, int bits)
        {
            x.Fire = (bits & 1) != 0;
            x.Earth = (bits & 2) != 0;
            x.Water = (bits & 4) != 0;
            x.Air = (bits & 8) != 0;
            x.Gold = (bits & 16) != 0;
            x.Star = (bits & 32) != 0;
        }

        /// <summary>
        /// onSolved (scene data DET CellBlockCorridor) = DoorLockEvent SetActive(false). The native solved path then
        /// reveals Door after the lock UI dismisses: Door on + unlocked on every apply (late joiners kept it inactive).
        /// </summary>
        internal static void ApplyDoorLockEvent(DoorLockEventInteraction x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.done;
            x.done = e.Bool0;
            if (!e.Bool0) return;
            PuzzleEdge.Solved("DoorLockEvent", was, true,
                durable: () => { if (!was) PuzzleEdge.ReplayDurable(x.onSolved); },
                onLive: () => PuzzleEdge.Invoke(x.onSolved), at: x);
            if (x.Door != null)
            {
                x.Door.SetActive(true);
                PuzzleSyncService.UnlockDoorObject(x.Door);
            }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        internal static void ApplyBiodome(BiodomeDoorLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.KeyLevel = e.Int0;
            x.hasLock = !e.Bool0;
            Native("biodome-sprites", x.setSprites);
            if (!e.Bool0) return;
            if (x.door != null) x.door.locked = false;
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>Partial packs apply while unsolved too, so peers / late joiners see mid-pin.</summary>
        internal static void ApplyServiceLock(DET_ServiceLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            ApplyServiceLockPins(x, e.Int0);
            if (e.Bool0)
                SnapServiceLock(x);
        }

        /// <summary>Pin0 bits0-2, Pin1 3-5, Pin2 6-8, Pin3 9-11 (values 0..precision, live precision = 6).</summary>
        static int PackServiceLockPins(DET_ServiceLock x)
        {
            int prec = Mathf.Clamp(x.precision, 0, 7); // 3-bit field
            var pins = x.pinning;
            if (pins == null) return 0;
            int pack = 0;
            int n = Mathf.Min(pins.Length, 4);
            for (int i = 0; i < n; i++)
                pack |= (Mathf.Clamp(pins[i], 0, prec) & 7) << (i * 3);
            return pack;
        }

        /// <summary>
        /// Native FlipButton ends in SetPins + AdjustCrown for the pin / crown visuals: call those directly (the
        /// FlipButton coroutine has SFX / anim side effects). A dump / held re-snap refreshes the visuals even when
        /// the values match (a remount OnEnable may leave wrong transforms).
        /// </summary>
        static void ApplyServiceLockPins(DET_ServiceLock x, int pack)
        {
            int prec = Mathf.Max(0, x.precision);
            var pins = x.pinning;
            if (pins == null) return;
            int n = Mathf.Min(pins.Length, 4);
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                int v = Mathf.Min((pack >> (i * 3)) & 7, prec);
                if (pins[i] == v && PuzzleSyncService.ReplayWorld) continue;
                pins[i] = v;
                changed = true;
            }
            if (!changed && PuzzleSyncService.ReplayWorld) return;
            Native("service-pins", x.SetPins);
            Native("service-crown", x.AdjustCrown);
        }

        static void SnapServiceLock(DET_ServiceLock x)
        {
            if (x.solved != null)
                x.solved.solved = true;
            PuzzleSyncService.DisableInteractions(x);
            DisableAll(x.Buttons);
            DisableAll(x.CounterButtons);
            PuzzleSyncService.DisableOne(x.TestButton);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        internal static void DisableAll(UnhollowerBaseLib.Il2CppReferenceArray<Interaction> its)
        {
            if (its == null) return;
            for (int i = 0; i < its.Length; i++)
                PuzzleSyncService.DisableOne(its[i]);
        }
    }
}
