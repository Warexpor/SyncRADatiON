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
            d.locked = locked;

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

        /// <summary>
        /// ConnectedDoors = room transition (StartA/StartB → traverseAB fade + teleport).
        /// Network must NEVER call StartA/StartB or set inProgress — that yanks every peer
        /// into the same room. Only lock state is multiplayer-safe.
        /// </summary>
        public static void ApplyConnectedDoors(ConnectedDoors cd, bool locked)
        {
            if (cd == null) return;
            try
            {
                cd.locked = locked;
                try { cd.UpdateProperties(); } catch { }
                SetTraversePlate(cd.A, locked);
                SetTraversePlate(cd.B, locked);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[DoorNative] ConnectedDoors lock: " + ex.Message);
            }
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
            catch { }
            try
            {
                var atd = x.GetComponentInParent<AutoTraverseDoor>();
                if (PlateOn(atd)) return true;
            }
            catch { }
            return false;
        }

        public static void ApplyLockPlate(InteractiveLockSingle x, bool on)
        {
            if (x == null) return;
            try
            {
                if (x.master != null)
                {
                    SetTraversePlate(x.master.A, on);
                    SetTraversePlate(x.master.B, on);
                }
            }
            catch { }
            try { SetTraversePlate(x.GetComponentInParent<AutoTraverseDoor>(), on); } catch { }
        }

        static bool PlateOn(AutoTraverseDoor atd)
        {
            return atd != null && atd.blocker != null && atd.blocker.activeSelf;
        }

        static void SetTraversePlate(AutoTraverseDoor atd, bool on)
        {
            if (atd == null || atd.blocker == null) return;
            try { atd.blocker.SetActive(on); } catch { }
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
            try { path = emitter.Event; } catch { }
            if (string.IsNullOrEmpty(path))
            {
                float dummy;
                if (!WorldSfx.TryVolume(at.transform.position, out dummy)) return;
                try { emitter.Play(); } catch { }
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
                PlaytestLog.Verbose("Door", "sfx skip far " + at.name);
                return;
            }
            WorldSfx.Play(path, at.transform);
            PlaytestLog.Event("Door", "sfx " + path + " @ " + at.name + " vol=" + vol.ToString("0.00"));
        }
    }
}
