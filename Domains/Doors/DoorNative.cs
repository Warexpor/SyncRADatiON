// Invoke SIGNALIS door entry points (private methods via reflection when needed).
using System.Reflection;
using FMODUnity;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class DoorNative
    {
        private static MethodInfo _doubleOpen;
        private static MethodInfo _doubleClose;
        private static MethodInfo _slideCycle;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _doubleOpen = typeof(Doorway_Double).GetMethod("openDoors", f);
            _doubleClose = typeof(Doorway_Double).GetMethod("closeDoors", f);
            _slideCycle = typeof(EventSlidingDoor).GetMethod("cycle", f);
        }

        public static void ApplyDoubleDoor(Doorway_Double d, bool open, bool locked)
        {
            if (d == null) return;
            Resolve();
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

            bool wasOpen = d.open;
            if (open == wasOpen)
                return;

            PlaytestLog.Event("Door", "apply " + (open ? "open" : "close") + " " + d.gameObject.name);
            NetGate.BeginApply();
            try
            {
                if (open)
                {
                    if (_doubleOpen != null)
                        _doubleOpen.Invoke(d, null);
                }
                else
                {
                    if (_doubleClose != null)
                        _doubleClose.Invoke(d, null);
                }
                d.open = open;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[DoorNative] DoubleDoor: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }

            PlayWorld(open ? d.OpenSFX : d.CloseSFX, d.gameObject);
        }

        public static bool IsFlavorSeal(GameObject go)
        {
            if (go == null) return false;
            GameObject root = go;
            try
            {
                Transform t = go.transform;
                while (t != null)
                {
                    try
                    {
                        if (t.GetComponent<Doorway_Double>() != null
                            || t.GetComponent<DoorLockControl>() != null
                            || t.GetComponent<Doorway_simple>() != null)
                        {
                            root = t.gameObject;
                            break;
                        }
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                    t = t.parent;
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
                if (dlc != null && dlc.locked) return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
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
                if (dlc != null && dlc.locked) return true;
            }
            catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-dlc-read", ex.Message); }
            return false;
        }

        /// <summary>True when a key/code lock governs this door and every such lock is solved (host truth).</summary>
        public static bool LockSolvedFor(Doorway_Double d)
        {
            if (d == null) return false;
            bool any; bool solved;
            ScanGoverningLocks(d, out any, out solved);
            return any && solved;
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
            catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-scan-locks", ex.Message); }
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
                        if (l == null) continue;
                        try
                        {
                            if (l.locked && l.key == null) return true;
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var singles = go.GetComponentsInChildren<InteractiveLockSingle>(true);
                if (singles != null)
                {
                    for (int i = 0; i < singles.Length; i++)
                    {
                        var s = singles[i];
                        if (s == null) continue;
                        try
                        {
                            if (s.key == null) return true;
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        public static void ApplyDoorLockControl(DoorLockControl dlc, bool locked)
        {
            if (dlc == null || !locked) return;
            try { dlc.setLock(true); }
            catch
            {
                try { dlc.locked = true; } catch (System.Exception e) { Guard.Swallow(e); }
            }
            try
            {
                if (dlc.doorZone != null)
                    dlc.doorZone.locked = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void UnsealDoorLockControl(DoorLockControl dlc)
        {
            if (dlc == null || IsFlavorSeal(dlc.gameObject)) return;
            try { dlc.setLock(false); }
            catch
            {
                try { dlc.locked = false; } catch (System.Exception e) { Guard.Swallow(e); }
            }
            try
            {
                if (dlc.doorZone != null)
                    dlc.doorZone.locked = false;
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
                        ApplyDoorLockControl(dlc, true);
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
            try
            {
                if (cd.gameObject == null) return;
            }
            catch { return; }
            try
            {
                if (!locked)
                {
                    if (!HasUnlocker(cd))
                        return;
                    cd.locked = false;
                    try { cd.Unlock(); } catch (System.Exception e) { Guard.Swallow(e); }
                    // Re-check after Unlock — mid-unload can tear the GO during native call.
                    try { if (cd.gameObject == null) return; } catch { return; }
                    ReleaseTraverse(cd);
                    try { cd.UpdateProperties(); } catch (System.Exception e) { Guard.Swallow(e); }
                    EnsurePlates(cd, false);
                    return;
                }
                cd.locked = true;
                EnsurePlates(cd, true);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[DoorNative] ConnectedDoors lock: " + ex.Message);
            }
        }

        static void ReleaseTraverse(ConnectedDoors cd)
        {
            ReleaseTraverseDoor(cd.A);
            ReleaseTraverseDoor(cd.B);
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
                    try
                    {
                        var t = it.type;
                        if (t == Interaction.interType.open || t == Interaction.interType.move)
                        {
                            it.triggered = false;
                            it.enabled = true;
                        }
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static bool HasUnlocker(ConnectedDoors cd)
        {
            if (cd == null) return false;
            try
            {
                if (cd.externalUnlocker) return true;
                if (cd.key != null) return true;
                if (cd.GiveKeyHint) return true;
            }
            catch { return false; }
            return false;
        }

        public static bool AllowUnlock(ConnectedDoors cd)
        {
            return HasUnlocker(cd);
        }

        public static bool TraversePlateActive(InteractiveLockSingle x)
        {
            if (x == null) return false;
            try
            {
                if (x.master != null)
                {
                    if (PlateOn(x.master.A) || PlateOn(x.master.B)) return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var atd = x.GetComponentInParent<AutoTraverseDoor>();
                if (PlateOn(atd)) return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.door != null)
                {
                    var dlc = DoorLockOn(x.door.gameObject);
                    if (dlc != null && dlc.locked) return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var dlc2 = x.GetComponent<DoorLockControl>();
                if (dlc2 != null && dlc2.locked) return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        public static void ApplyLockPlate(InteractiveLockSingle x, bool on)
        {
            if (x == null) return;
            if (!on)
            {
                try
                {
                    if (x.key == null) return;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
                try
                {
                    if (x.door != null && IsFlavorSeal(x.door.gameObject)) return;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            try
            {
                if (x.master != null)
                {
                    SetTraversePlate(x.master.A, on);
                    SetTraversePlate(x.master.B, on);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { SetTraversePlate(x.GetComponentInParent<AutoTraverseDoor>(), on); } catch (System.Exception e) { Guard.Swallow(e); }
            if (!on) return;
            try
            {
                if (x.door != null)
                {
                    var dlc = DoorLockOn(x.door.gameObject);
                    if (dlc != null)
                        ApplyDoorLockControl(dlc, true);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void EnsurePlates(ConnectedDoors cd, bool on)
        {
            if (cd == null) return;
            try { SetTraversePlate(cd.A, on); } catch (System.Exception e) { Guard.Swallow(e); }
            try { SetTraversePlate(cd.B, on); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        static bool PlateOn(AutoTraverseDoor atd)
        {
            return atd != null && atd.blocker != null && atd.blocker.activeSelf;
        }

        static void SetTraversePlate(AutoTraverseDoor atd, bool on)
        {
            if (atd == null || atd.blocker == null) return;
            try { atd.blocker.SetActive(on); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void ApplySlidingDoor(EventSlidingDoor sd, bool opened, bool moving)
        {
            if (sd == null) return;
            Resolve();

            bool was = sd.opened;
            if (opened == was && !moving)
                return;

            NetGate.BeginApply();
            try
            {
                if (opened != was && _slideCycle != null)
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

            sd.opened = opened;
            sd.moving = moving;
            if (opened != was)
                PlayWorldPath(opened ? sd.openSFX : sd.closeSFX, sd.gameObject);
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
            PlaytestLog.Verbose("Door", "sfx " + path + " @ " + at.name + " vol=" + vol.ToString("0.00"));
        }
    }
}
