// SIGNALIS door entry points for remote applies (lock flags, plates, native cycle), plus the host's lock-solved checks.
using System.Reflection;
using FMODUnity;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class DoorNative
    {
        // persistent: per-process reflection cache
        private static MethodInfo _slideCycle;
        // persistent: per-process reflection cache
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _slideCycle = typeof(EventSlidingDoor).GetMethod("cycle", f);
        }

        public static void ApplyDoubleDoor(Doorway_Double d, bool open, bool locked)
        {
            if (d == null) return;
            // Flavor / DLC seals keep locked=false on the host. Writing that across
            // still runs native Update/indicator as "you can walk in".
            if (!locked && SealedFace(d.gameObject))
            {
                if (open == d.open)
                    return;
                locked = d.locked;
            }
            d.locked = locked;
            if (locked)
            {
                try
                {
                    var dlc = DoorLockOn(d.gameObject);
                    if (dlc != null)
                        ApplyDoorLockControl(dlc, true);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }

            if (open == d.open)
                return;

            PlaytestLog.Event("Door", "apply " + (open ? "open" : "close") + " " + d.gameObject.name);
            // `open` is the whole state: native Update calls openDoors / closeDoors every frame to lerp the leaves
            // toward it (Ghidra Doorway_Double.c Update).
            try { d.open = open; }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[DoorNative] DoubleDoor: " + ex.Message);
            }

            PlayWorld(open ? d.OpenSFX : d.CloseSFX, d.gameObject);
        }

        /// <summary>A key-less lock on the door's root (Doorway_Double / DoorLockControl / Doorway_simple): never solvable.</summary>
        public static bool IsFlavorSeal(GameObject go)
        {
            if (go == null) return false;
            GameObject root = go;
            try
            {
                for (Transform t = go.transform; t != null; t = t.parent)
                {
                    if (t.GetComponent<Doorway_Double>() != null
                        || t.GetComponent<DoorLockControl>() != null
                        || t.GetComponent<Doorway_simple>() != null)
                    {
                        root = t.gameObject;
                        break;
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return FlavorOn(root);
        }

        static bool SealedFace(GameObject go)
        {
            if (go == null) return false;
            if (IsFlavorSeal(go)) return true;
            try
            {
                var dlc = DoorLockOn(go);
                return dlc != null && dlc.locked;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>
        /// Decompile InteractiveLock.Update copies locked -> door.locked every frame, so a door governed by a
        /// still-locked InteractiveLock cannot legitimately be open/unlocked.
        /// </summary>
        public static bool HasUnsolvedLock(Doorway_Double d)
        {
            bool any; bool solved;
            ScanGoverningLocks(d, out any, out solved);
            if (any && !solved) return true;
            try
            {
                var dlc = DoorLockOn(d.gameObject);
                return dlc != null && dlc.locked;
            }
            catch (System.Exception ex) { Guard.Swallow("Door.DlcRead", ex); return false; }
        }

        /// <summary>True when a key/code lock governs this door and every such lock is solved (host truth).</summary>
        public static bool LockSolvedFor(Doorway_Double d)
        {
            if (d == null) return false;
            bool any; bool solved;
            ScanGoverningLocks(d, out any, out solved);
            return any && solved;
        }

        /// <summary>True when a DoorLockControl governs this door and it is already unlocked (host truth).</summary>
        public static bool DoorLockUnlocked(Doorway_Double d)
        {
            if (d == null) return false;
            try
            {
                var dlc = DoorLockOn(d.gameObject);
                return dlc != null && !dlc.locked;
            }
            catch (System.Exception ex) { Guard.Swallow("Door.DlcUnlocked", ex); return false; }
        }

        static void ScanGoverningLocks(Doorway_Double d, out bool any, out bool solved)
        {
            any = false; solved = true;
            if (d == null) return;
            try
            {
                var locks = WorldLookup.All<InteractiveLock>();
                if (locks == null) return;
                for (int i = 0; i < locks.Length; i++)
                {
                    var l = locks[i];
                    if (l == null || l.door != d) continue;
                    // Key-less locks are flavor seals: never solvable.
                    any = true;
                    if (l.locked || l.key == null) solved = false;
                }
            }
            catch (System.Exception ex) { Guard.Swallow("Door.ScanLocks", ex); }
        }

        static DoorLockControl DoorLockOn(GameObject go)
        {
            if (go == null) return null;
            var dlc = go.GetComponent<DoorLockControl>();
            if (dlc != null) return dlc;
            var kids = go.GetComponentsInChildren<DoorLockControl>(true);
            if (kids != null && kids.Length > 0) return kids[0];
            return null;
        }

        static bool FlavorOn(GameObject go)
        {
            if (go == null) return false;
            try
            {
                var locks = go.GetComponentsInChildren<InteractiveLock>(true);
                if (locks != null)
                {
                    for (int i = 0; i < locks.Length; i++)
                    {
                        var l = locks[i];
                        if (l != null && l.locked && l.key == null) return true;
                    }
                }
                var singles = go.GetComponentsInChildren<InteractiveLockSingle>(true);
                if (singles != null)
                {
                    for (int i = 0; i < singles.Length; i++)
                    {
                        var s = singles[i];
                        if (s != null && IsSealedSingle(s)) return true;
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        /// <summary>
        /// A single whose door can never open: no master (its ConnectedDoors never woke, so native Update forces it
        /// locked) or a locked master nothing can unlock. "No key" alone is not a seal: a plain unlocked connection
        /// (LOV East Corridor -> Aula) has none, and sealing it made the host reject every open (door flapping).
        /// </summary>
        public static bool IsSealedSingle(InteractiveLockSingle s)
        {
            if (s == null) return false;
            try
            {
                var m = s.master;
                return m == null || (m.locked && !HasUnlocker(m));
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>The InteractiveLockSingle driving this door's lock flag (native Update writes door.locked), or null.</summary>
        static InteractiveLockSingle GoverningSingle(Doorway_Double d)
        {
            var singles = WorldLookup.All<InteractiveLockSingle>();
            if (singles == null) return null;
            for (int i = 0; i < singles.Length; i++)
            {
                var s = singles[i];
                if (s != null && s.door == d) return s;
            }
            return null;
        }

        /// <summary>
        /// The door's real lock: a governing single's master when wired (door.locked is only its per-frame mirror,
        /// stale while the room sleeps), the door's own flag otherwise.
        /// </summary>
        public static bool EffectiveLocked(Doorway_Double d)
        {
            if (d == null) return true;
            try
            {
                var s = GoverningSingle(d);
                if (s != null) return s.master == null || s.master.locked;
                return d.locked;
            }
            catch (System.Exception e) { Guard.Swallow(e); return true; }
        }

        public static void ApplyDoorLockControl(DoorLockControl dlc, bool locked)
        {
            if (dlc == null || !locked) return;
            SetDoorLock(dlc, true);
        }

        public static void UnsealDoorLockControl(DoorLockControl dlc)
        {
            if (dlc == null || IsFlavorSeal(dlc.gameObject)) return;
            SetDoorLock(dlc, false);
        }

        /// <summary>Native setLock (indicator + zone), falling back to the raw flag; the door zone follows either way.</summary>
        static void SetDoorLock(DoorLockControl dlc, bool locked)
        {
            try { dlc.setLock(locked); }
            catch
            {
                try { dlc.locked = locked; } catch (System.Exception e) { Guard.Swallow(e); }
            }
            try
            {
                if (dlc.doorZone != null)
                    dlc.doorZone.locked = locked;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void ReassertLockVisuals()
        {
            var lockCtrls = WorldLookup.All<DoorLockControl>();
            if (lockCtrls == null) return;
            for (int i = 0; i < lockCtrls.Length; i++)
            {
                var dlc = lockCtrls[i];
                if (dlc == null) continue;
                try
                {
                    if (dlc.locked)
                        SetDoorLock(dlc, true);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
        }

        /// <summary>
        /// ConnectedDoors = room transition (StartA/StartB → traverseAB fade + teleport).
        /// Network must NEVER call StartA/StartB or set inProgress — that yanks every peer
        /// into the same room. Only lock state is multiplayer-safe.
        /// </summary>
        public static void ApplyConnectedDoors(ConnectedDoors cd, bool locked)
        {
            if (cd == null) return;
            // Scene unload can destroy the door between registry lookup and apply.
            if (!Alive(cd)) return;
            try
            {
                // Awake never ran (inactive, e.g. PEN_Wreck DemoOnly): Unlock / UpdateProperties would wire its
                // singles here only, and the peers then disagreed on that door forever. Keep the flag, wire nothing.
                if (!Wired(cd))
                {
                    cd.locked = locked;
                    return;
                }
                if (!locked)
                {
                    if (!HasUnlocker(cd))
                        return;
                    cd.locked = false;
                    try { cd.Unlock(); } catch (System.Exception e) { Guard.Swallow(e); }
                    // Re-check after Unlock: mid-unload can tear the GO during the native call.
                    if (!Alive(cd)) return;
                    ReleaseTraverseDoor(cd.A);
                    ReleaseTraverseDoor(cd.B);
                    try { cd.UpdateProperties(); } catch (System.Exception e) { Guard.Swallow(e); }
                    SetTraversePlate(cd.A, false);
                    SetTraversePlate(cd.B, false);
                    return;
                }
                cd.locked = true;
                SetTraversePlate(cd.A, true);
                SetTraversePlate(cd.B, true);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[DoorNative] ConnectedDoors lock: " + ex.Message);
            }
        }

        /// <summary>ConnectedDoors.Awake ran: it makes its A/B singles' master point back at it (Ghidra ConnectedDoors.c).</summary>
        static bool Wired(ConnectedDoors cd)
        {
            try
            {
                if (cd.gameObject.activeInHierarchy) return true;
                return SingleMaster(cd.A) == cd || SingleMaster(cd.B) == cd;
            }
            catch { return false; }
        }

        static ConnectedDoors SingleMaster(AutoTraverseDoor atd)
        {
            if (atd == null) return null;
            var s = atd.GetComponent<InteractiveLockSingle>();
            return s != null ? s.master : null;
        }

        static bool Alive(Component c)
        {
            try { return c.gameObject != null; }
            catch { return false; }
        }

        static void ReleaseTraverseDoor(AutoTraverseDoor atd)
        {
            if (atd == null) return;
            try { atd.enabled = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var inters = atd.GetComponentsInChildren<Interaction>(true);
                if (inters == null) return;
                for (int i = 0; i < inters.Length; i++)
                {
                    var it = inters[i];
                    if (it == null) continue;
                    var t = it.type;
                    if (t == Interaction.interType.open || t == Interaction.interType.move)
                    {
                        it.triggered = false;
                        it.enabled = true;
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// A ConnectedDoors the game can unlock (external unlocker, key, key hint, or a backtrack door that opens
        /// from its far side). Others stay locked.
        /// </summary>
        public static bool HasUnlocker(ConnectedDoors cd)
        {
            if (cd == null) return false;
            try { return cd.externalUnlocker || cd.key != null || cd.GiveKeyHint || cd.BacktrackDoor; }
            catch { return false; }
        }

        public static bool TraversePlateActive(InteractiveLockSingle x)
        {
            if (x == null) return false;
            try
            {
                if (x.master != null && (PlateOn(x.master.A) || PlateOn(x.master.B))) return true;
                if (PlateOn(x.GetComponentInParent<AutoTraverseDoor>())) return true;
                if (x.door != null)
                {
                    var dlc = DoorLockOn(x.door.gameObject);
                    if (dlc != null && dlc.locked) return true;
                }
                var own = x.GetComponent<DoorLockControl>();
                return own != null && own.locked;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        public static void ApplyLockPlate(InteractiveLockSingle x, bool on)
        {
            if (x == null) return;
            try
            {
                // Sealed singles and flavor-sealed doors are never unplated.
                if (!on && (IsSealedSingle(x) || (x.door != null && IsFlavorSeal(x.door.gameObject)))) return;
                if (x.master != null)
                {
                    SetTraversePlate(x.master.A, on);
                    SetTraversePlate(x.master.B, on);
                }
                SetTraversePlate(x.GetComponentInParent<AutoTraverseDoor>(), on);
                if (!on || x.door == null) return;
                var dlc = DoorLockOn(x.door.gameObject);
                if (dlc != null)
                    SetDoorLock(dlc, true);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static bool PlateOn(AutoTraverseDoor atd)
        {
            return atd != null && atd.blocker != null && atd.blocker.activeSelf;
        }

        static void SetTraversePlate(AutoTraverseDoor atd, bool on)
        {
            try
            {
                if (atd == null || atd.blocker == null) return;
                atd.blocker.SetActive(on);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Native cycle (Ghidra EventSlidingDoor.c): no-op while moving, else moving=true and an open/close coroutine
        /// that sets opened/moving when it ends. A peer's cycle hook reports (opened = state it moved FROM,
        /// moving = true); a full dump reports the settled state (moving = false). Either way the target is reached
        /// by one native cycle here; opened/moving are never written directly (a forced moving=true without a
        /// running coroutine wedged the door for good, cycle() ignores a moving door).
        /// </summary>
        public static void ApplySlidingDoor(EventSlidingDoor sd, bool opened, bool moving)
        {
            if (sd == null) return;
            Resolve();

            bool target = moving ? !opened : opened;
            // In motion: the running coroutine ends at !opened. Already heading there = nothing to do; the other
            // way cannot be reversed mid-move (native cycle ignores it), the next edge / dump settles it.
            if (sd.moving || sd.opened == target)
                return;

            NetGate.BeginApply();
            try
            {
                if (_slideCycle != null)
                    _slideCycle.Invoke(sd, null);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[DoorNative] SlidingDoor: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }

            PlayWorldPath(target ? sd.openSFX : sd.closeSFX, sd.gameObject);
        }

        static void PlayWorld(StudioEventEmitter emitter, GameObject at)
        {
            if (emitter == null || at == null) return;
            string path = null;
            try { path = emitter.Event; } catch (System.Exception e) { Guard.Swallow(e); }
            if (string.IsNullOrEmpty(path))
            {
                float dummy;
                if (!WorldSfx.TryVolume(at.transform.position, out dummy)) return;
                try { emitter.Play(); } catch (System.Exception e) { Guard.Swallow(e); }
                return;
            }
            PlayWorldPath(path, at);
        }

        static void PlayWorldPath(string path, GameObject at)
        {
            if (string.IsNullOrEmpty(path) || at == null) return;
            float vol;
            if (!WorldSfx.TryVolume(at.transform.position, out vol))
            {
                PlaytestLog.Event("Door", "sfx skip far " + at.name);
                return;
            }
            WorldSfx.Play(path, at.transform);
            if (ModRuntime.VerboseLogging)
                PlaytestLog.Verbose("Door", "sfx " + path + " @ " + at.name + " vol=" + vol.ToString("0.00"));
        }
    }
}
