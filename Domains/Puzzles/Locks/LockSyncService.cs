using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// InteractiveLock*, Keypad3D/ROT_Keypad, Dial, Number, Multi, DoorLock* read/apply.
    /// </summary>
    public sealed class LockSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.InteractiveLock:
                {
                    var x = (InteractiveLock)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.InteractiveLockSingle:
                {
                    var x = (InteractiveLockSingle)c;
                    bool locked = x.door != null && x.door.locked;
                    bool plate = DoorNative.TraversePlateActive(x);
                    try
                    {
                        if (!plate && x.key == null && (x.door == null || !x.door.open))
                            plate = DoorNative.IsFlavorSeal(x.gameObject)
                                || (x.door != null && DoorNative.IsFlavorSeal(x.door.gameObject));
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                    entry = PuzzleDomainUtil.Mk(type, wid, locked, plate, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.Keypad3D:
                {
                    var x = (Keypad3D)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved || x.opening, x.opening, x.blocked, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ROT_Keypad:
                {
                    var x = (ROT_Keypad)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved || x.opening, x.opening, x.blocked, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DialLock:
                {
                    var x = (ROT_DialLock)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, x.A, x.B, x.C, x.D, 0);
                    return true;
                }
                case PuzzleType.NumberLockNew:
                {
                    var x = (NumberLockNew)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DoorLockPuzzle:
                {
                    var x = (DoorLockPuzzle)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MultiLock:
                {
                    // Decompile MED/LAB_MultiLock element bools → Int0 bits (protocol 8 reuses Int0).
                    var med = c as MED_MultiLock;
                    if (med != null)
                    {
                        entry = PuzzleDomainUtil.Mk(type, wid, med.unlocked, false, false, PackMedBits(med), 0, 0, 0, 0);
                        return true;
                    }
                    var lab = c as LAB_MultiLock;
                    if (lab != null)
                    {
                        entry = PuzzleDomainUtil.Mk(type, wid, lab.unlocked, false, false, PackLabBits(lab), 0, 0, 0, 0);
                        return true;
                    }
                    return false;
                }
                case PuzzleType.DoorLockControl:
                    return PuzzleDoorFlagsSyncService.TryRead(type, c, wid, out entry);
                case PuzzleType.DoorLockEventInteraction:
                {
                    var x = (DoorLockEventInteraction)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.done, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.BiodomeDoorLock:
                {
                    var x = (BiodomeDoorLock)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, !x.hasLock, false, false, x.KeyLevel, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DET_ServiceLock:
                {
                    var x = (DET_ServiceLock)c;
                    bool ok = x.solved != null && x.solved.solved;
                    // Protocol 10: Int0 = 4×3-bit pinning pack (0..precision, live precision=6).
                    int pack = 0;
                    try { pack = PackServiceLockPins(x); } catch (System.Exception e) { Guard.Swallow(e); }
                    entry = PuzzleDomainUtil.Mk(type, wid, ok, false, false, pack, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyInteractive(InteractiveLock x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            bool was = x.locked;
            x.locked = e.Bool0;
            // Decompile InteractiveLock: Update copies locked -> door.locked every frame and <delayedOpen> only
            // sets door.open (door visuals ride DoorState). delayedOpen() returns an IEnumerator, so the bare
            // call that used to live here never ran. Durable consequence = unlocked door, on every apply.
            PuzzleEdge.Solved("InteractiveLock", was == false, !e.Bool0, () =>
            {
                if (x.door != null)
                    PuzzleSyncService.UnlockDoorObject(x.door.gameObject);
                PuzzleSyncService.TryUnlockDoors(x.gameObject);
            });
        }

        public static void ApplyInteractiveSingle(InteractiveLockSingle x, PuzzleStateEntry e, bool mutateWorld)
        {
            // Flags + plate always snap (incl. FullRefresh); unlock side-effects stay mutate-gated.
            if (x == null) return;
            try
            {
                if (DoorNative.IsFlavorSeal(x.gameObject)
                    || (x.door != null && DoorNative.IsFlavorSeal(x.door.gameObject)))
                {
                    if (x.door != null) x.door.locked = true;
                    return;
                }
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            if (e.Bool0)
            {
                if (x.door != null) x.door.locked = true;
                if (e.Bool1)
                    DoorNative.ApplyLockPlate(x, true);
            }
            else
            {
                if (x.door != null)
                {
                    try { x.door.locked = false; } catch (System.Exception ex) { Guard.Swallow(ex); }
                    PuzzleSyncService.UnlockDoorObject(x.door.gameObject);
                }
                PuzzleSyncService.TryUnlockDoors(x.gameObject);
                DoorNative.ApplyLockPlate(x, false);
            }
        }

        public static void ApplyKeypad3D(Keypad3D x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            bool wasOpening = x.opening;
            x.solved = e.Bool0; x.opening = e.Bool1; x.blocked = e.Bool2;
            if (!e.Bool0) return;
            // Decompile Keypad3D: the wheel turn sets opening and <openDoor> lerps Door to Euler(0,-100,0)
            // (and locks the local player's input). openDoor() returns an IEnumerator, so the bare call
            // never ran — with opening latched the wheel is dead and the door stayed shut. Peers snap the
            // pose (what LoadState does) on live edge, join dump and held re-apply alike.
            if (e.Bool1)
                PuzzleEdge.Solved("Keypad3D", wasOpening, true, () => SnapKeypad3DDoor(x));
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        static void SnapKeypad3DDoor(Keypad3D x)
        {
            if (x == null || x.Door == null) return;
            x.Door.localRotation = Quaternion.Euler(0f, -100f, 0f);
        }

        public static void ApplyRotKeypad(ROT_Keypad x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            bool was = x.solved;
            x.solved = e.Bool0; x.opening = e.Bool1; x.blocked = e.Bool2;
            if (!e.Bool0) return;
            // Mirror Keypad3D openDoor rising-edge + host ApplyKeypad: onSuccess peels
            // to ConnectedDoors.Unlock on Door Connection (35) under DoorConnections
            // (separate tree from KeypadLogic) + Event.SetActive + exitEvent + dimPOI.
            // TryUnlockDoors = FindInParents ConnectedDoors only — misses that peel.
            // FullRefresh sets _mutateWorld=false; live 0.5.22 only Invoked onSuccess
            // when mutateWorld && !was, then latched solved so ReapplyHeld rising-edge
            // skipped → late joiner kept Door Connection locked. Native LoadState
            // fires onLoad (Unlock + SetActive + dimPOI; omits exitEvent) — mirror that
            // for !mutateWorld && !was. Keep latching solved (do not UseItem-style delay).
            // Shared edge rule (PuzzleEdge): a live rising edge runs onSuccess; join dump AND held re-snap
            // (a remounted room resets solved, so !was again) run the durable onLoad. Before, ReapplyHeld
            // (mutateWorld=true) replayed onSuccess incl. exitEvent on a player who was not in the screen.
            PuzzleEdge.Solved("ROT_Keypad", was, true,
                durable: () => { if (!was) InvokeApplying(x.onLoad); },
                onLive: () => InvokeApplying(x.onSuccess));
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>UnityEvent.Invoke under NetGate.BeginApply so native handlers do not re-emit.</summary>
        internal static void InvokeApplying(UnityEngine.Events.UnityEvent ev)
        {
            if (ev == null) return;
            NetGate.BeginApply();
            try { ev.Invoke(); }
            finally { NetGate.EndApply(); }
        }

        public static void ApplyDial(ROT_DialLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0;
            x.A = e.Int0; x.B = e.Int1; x.C = e.Int2; x.D = e.Int3;
            if (x.a != null) x.a.localEulerAngles = new Vector3(0, e.Int0 * 36, 0);
            if (x.b != null) x.b.localEulerAngles = new Vector3(0, e.Int1 * 36, 0);
            if (x.c != null) x.c.localEulerAngles = new Vector3(0, e.Int2 * 36, 0);
            if (x.d != null) x.d.localEulerAngles = new Vector3(0, e.Int3 * 36, 0);
            if (!e.Bool0) return;
            try
            {
                if (x.lockObject != null)
                    x.lockObject.SetActive(false);
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            try
            {
                if (x.Door != null)
                    PuzzleSyncService.UnlockDoorObject(x.Door.gameObject);
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            try { PuzzleSyncService.DisableInteractions(x); } catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        public static void ApplyNumber(NumberLockNew x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.locked = e.Bool0;
            if (e.Bool0) return;
            try
            {
                if (x.door != null)
                {
                    x.door.locked = false;
                    PuzzleSyncService.UnlockDoorObject(x.door.gameObject);
                }
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            try { if (x.doorInt != null) PuzzleSyncService.DisableOne(x.doorInt); } catch (System.Exception ex) { Guard.Swallow(ex); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyDoorLockPuzzle(DoorLockPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.locked = e.Bool0;
            if (e.Bool0) return;
            try
            {
                if (x.door != null)
                {
                    x.door.locked = false;
                    PuzzleSyncService.UnlockDoorObject(x.door.gameObject);
                }
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            try { if (x.doorInt != null) PuzzleSyncService.DisableOne(x.doorInt); } catch (System.Exception ex) { Guard.Swallow(ex); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyMulti(Component c, PuzzleStateEntry e)
        {
            if (c == null) return;
            // Rising-edge Melon UnityEvents: prior ApplyMulti only latched unlocked +
            // element bits + TryUnlockDoors — never Invoked onUnlocked / onLoadUnlocked.
            // Native UnlockKey (live) Invokes onUnlocked then onUnlockedLate; OnEnable
            // (load) Invokes onLoadUnlocked when unlocked. AssetStudio MED Elemental:
            // onUnlocked → dimPOI + exitEvent; onUnlockedLate/onLoadUnlocked → SetActive
            // Inter/EventInter. LAB TreeLock: onUnlocked → exitEvent + Play; late/load →
            // SetActive Inter/Event. Inter SetActive is ONLY on Late/Load — live peers
            // need both onUnlocked (exitEvent) and onUnlockedLate (Inter) or EventScreen
            // never exitEvents and Inter stays stuck. Mirror ApplyRotKeypad 0.5.25 /
            // ApplyPower 0.5.27 / ApplyPump 0.5.28: MutateWorld&&!was → onUnlocked +
            // onUnlockedLate; !MutateWorld&&!was → onLoadUnlocked. Skip onEarthKeyUnlocked
            // (mid-key dimPOI only; no full-unlock softlock proof).
            var med = c as MED_MultiLock;
            if (med != null)
            {
                bool was = false;
                try { was = med.unlocked; } catch (System.Exception ex) { Guard.Swallow(ex); }
                med.unlocked = e.Bool0;
                UnpackMedBits(med, e.Int0);
                if (!e.Bool0) return;
                // Shared edge rule (PuzzleEdge): live rising edge = onUnlocked + onUnlockedLate; join dump and
                // held re-snap (ReapplyHeld used to count as live) = onLoadUnlocked only, once.
                PuzzleEdge.Solved("MED_MultiLock", was, true,
                    durable: () => { if (!was) InvokeApplying(med.onLoadUnlocked); },
                    onLive: () =>
                    {
                        InvokeApplying(med.onUnlocked);
                        InvokeApplying(med.onUnlockedLate);
                    });
                PuzzleSyncService.TryUnlockDoors(med.gameObject);
                return;
            }
            var lab = c as LAB_MultiLock;
            if (lab != null)
            {
                bool was = false;
                try { was = lab.unlocked; } catch (System.Exception ex) { Guard.Swallow(ex); }
                lab.unlocked = e.Bool0;
                UnpackLabBits(lab, e.Int0);
                if (!e.Bool0) return;
                // Shared edge rule (PuzzleEdge): live rising edge = onUnlocked + onUnlockedLate; join dump and
                // held re-snap (ReapplyHeld used to count as live) = onLoadUnlocked only, once.
                PuzzleEdge.Solved("LAB_MultiLock", was, true,
                    durable: () => { if (!was) InvokeApplying(lab.onLoadUnlocked); },
                    onLive: () =>
                    {
                        InvokeApplying(lab.onUnlocked);
                        InvokeApplying(lab.onUnlockedLate);
                    });
                PuzzleSyncService.TryUnlockDoors(lab.gameObject);
            }
        }

        // Decompile MED/LAB_MultiLock: Fire=1 Earth=2 Water=4 Air=8 Gold=16 Star=32 (LAB).
        static int PackMedBits(MED_MultiLock x)
        {
            int bits = 0;
            if (x == null) return bits;
            try { if (x.Fire) bits |= 1; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Earth) bits |= 2; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Water) bits |= 4; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Air) bits |= 8; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Gold) bits |= 16; } catch (System.Exception e) { Guard.Swallow(e); }
            return bits;
        }

        static int PackLabBits(LAB_MultiLock x)
        {
            int bits = 0;
            if (x == null) return bits;
            try { if (x.Fire) bits |= 1; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Earth) bits |= 2; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Water) bits |= 4; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Air) bits |= 8; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Gold) bits |= 16; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Star) bits |= 32; } catch (System.Exception e) { Guard.Swallow(e); }
            return bits;
        }

        static void UnpackMedBits(MED_MultiLock x, int bits)
        {
            if (x == null) return;
            try { x.Fire = (bits & 1) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.Earth = (bits & 2) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.Water = (bits & 4) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.Air = (bits & 8) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.Gold = (bits & 16) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void UnpackLabBits(LAB_MultiLock x, int bits)
        {
            if (x == null) return;
            try { x.Fire = (bits & 1) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.Earth = (bits & 2) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.Water = (bits & 4) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.Air = (bits & 8) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.Gold = (bits & 16) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.Star = (bits & 32) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void ApplyDoorLockControl(DoorLockControl x, PuzzleStateEntry e, bool mutateWorld)
            => PuzzleDoorFlagsSyncService.ApplyDoorLockControl(x, e, mutateWorld);

        public static void ApplyDoorLockEvent(DoorLockEventInteraction x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Rising-edge onSolved: Melon done + onSolved (no onLoad). Native LoadState
            // and solved path both Invoke onSolved. Asset DET: onSolved → SetActive(false)
            // on DoorLockEvent GO. Dig L: both live + FullRefresh Invoke onSolved.
            // Melon field Door (GameObject) — native solved path reveals the door props
            // after the lock UI dismisses. Dig L deferred Door.SetActive(true); peers /
            // late-join can keep Door inactive after onSolved hides the lock GO.
            // Mirror CryoSyncService Door.SetActive(true): rising-edge activate Door
            // + TryUnlockDoors / UnlockDoorObject (Dig AJ).
            bool was = false;
            try { was = x.done; } catch (System.Exception ex) { Guard.Swallow(ex); }
            x.done = e.Bool0;
            if (!e.Bool0) return;
            if (!was)
            {
                NetGate.BeginApply();
                try
                {
                    if (x.onSolved != null)
                        x.onSolved.Invoke();
                }
                catch (System.Exception ex) { Guard.Swallow(ex); }
                finally { NetGate.EndApply(); }
            }
            try
            {
                if (x.Door != null)
                {
                    x.Door.SetActive(true);
                    PuzzleSyncService.UnlockDoorObject(x.Door);
                }
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            try { PuzzleSyncService.TryUnlockDoors(x.gameObject); } catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        public static void ApplyBiodome(BiodomeDoorLock x, PuzzleStateEntry e)
        {
            if (x != null)
                SnapBiodomeLock(x, e.Bool0, e.Int0);
        }

        public static void ApplyServiceLock(DET_ServiceLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Protocol 10: Int0 = 4×3-bit pinning; Bool0 = solved.
            // Apply partial packs even when unsolved so peers / late-join see mid-pin.
            int pack = SanitizeServiceLockPack(e.Int0);
            ApplyServiceLockPins(x, pack);
            if (e.Bool0)
                SnapServiceLock(x);
        }

        /// <summary>Pin0 bits0-2, Pin1 3-5, Pin2 6-8, Pin3 9-11 (values 0..precision).</summary>
        static int PackServiceLockPins(DET_ServiceLock x)
        {
            int prec = 6;
            try { prec = x.precision; } catch (System.Exception e) { Guard.Swallow(e); }
            if (prec < 0) prec = 0;
            if (prec > 7) prec = 7; // 3-bit field
            int pack = 0;
            try
            {
                var pins = x.pinning;
                if (pins == null) return 0;
                int n = pins.Length;
                if (n > 4) n = 4;
                for (int i = 0; i < n; i++)
                {
                    int v = pins[i];
                    if (v < 0) v = 0;
                    if (v > prec) v = prec;
                    pack |= (v & 7) << (i * 3);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return pack;
        }

        static int SanitizeServiceLockPack(int packed)
        {
            // 3-bit mask per pin — illegal high bits dropped; values always 0..7.
            int p0 = packed & 7;
            int p1 = (packed >> 3) & 7;
            int p2 = (packed >> 6) & 7;
            int p3 = (packed >> 9) & 7;
            return p0 | (p1 << 3) | (p2 << 6) | (p3 << 9);
        }

        static void ApplyServiceLockPins(DET_ServiceLock x, int pack)
        {
            if (x == null) return;
            int prec = 6;
            try { prec = x.precision; } catch (System.Exception e) { Guard.Swallow(e); }
            if (prec < 0) prec = 0;
            try
            {
                var pins = x.pinning;
                if (pins == null) return;
                int n = pins.Length;
                if (n > 4) n = 4;
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    int v = (pack >> (i * 3)) & 7;
                    if (v > prec) v = prec;
                    int cur = 0;
                    try { cur = pins[i]; } catch (System.Exception e) { Guard.Swallow(e); }
                    if (cur == v && PuzzleSyncService.ReplayWorld) continue;
                    try { pins[i] = v; changed = true; } catch (System.Exception e) { Guard.Swallow(e); }
                }
                // Native FlipButton ends in SetPins + AdjustCrown for pin/crown visuals.
                // Call those directly — avoid FlipButton coroutine side effects (SFX/anim).
                // FullRefresh (!MutateWorld) always refreshes visuals even if values match
                // (remount / late-join OnEnable may leave wrong transforms).
                if (!changed && PuzzleSyncService.ReplayWorld) return;
            }
            catch { return; }
            try { x.SetPins(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.AdjustCrown(); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void SnapBiodomeLock(BiodomeDoorLock x, bool unlocked, int keyLevel)
        {
            if (x == null) return;
            try { x.KeyLevel = keyLevel; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.hasLock = !unlocked; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.setSprites(); } catch (System.Exception e) { Guard.Swallow(e); }
            if (!unlocked) return;
            try
            {
                if (x.door != null)
                    x.door.locked = false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapServiceLock(DET_ServiceLock x)
        {
            if (x == null) return;
            try
            {
                if (x.solved != null)
                    x.solved.solved = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.DisableInteractions(x);
            try
            {
                var buttons = x.Buttons;
                if (buttons != null)
                {
                    for (int i = 0; i < buttons.Length; i++)
                        PuzzleSyncService.DisableOne(buttons[i]);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var counter = x.CounterButtons;
                if (counter != null)
                {
                    for (int i = 0; i < counter.Length; i++)
                        PuzzleSyncService.DisableOne(counter[i]);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.DisableOne(x.TestButton); } catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }
    }
}
