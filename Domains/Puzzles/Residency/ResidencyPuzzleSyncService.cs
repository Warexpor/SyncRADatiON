using SyncRADation.Sync;
using UnhollowerBaseLib;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Protocol-9 residency / key / drawer puzzles (RES_MusicBox..OpenableDrawer).
    /// Magpie-style final-pose snaps; MED_KeyGrid / ArianePhotoCode are static globals.
    /// </summary>
    public sealed class ResidencyPuzzleSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.RES_MusicBox:
                {
                    var x = (RES_MusicBox)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.opened, x.hasCassette, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.RES_LibraryPC:
                {
                    var x = (RES_LibraryPC)c;
                    // Dig AJ: mid-maze robotPos (AssetStudio initial 9,5; Victory 10,1).
                    // Float0/Float1 = x/y. Bool0=solved. Bool1 marks a valid robot pack
                    // so spawn (9,5) still holds across remount (Float alone can be 0).
                    float rx = 0f, ry = 0f;
                    try
                    {
                        var p = x.robotPos;
                        rx = p.x;
                        ry = p.y;
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, true, false, 0, 0, 0, 0, rx, ry);
                    return true;
                }
                case PuzzleType.RES_Paternoster:
                {
                    var x = (RES_Paternoster)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.powered, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DET_ServiceLock_Key:
                {
                    var x = (DET_ServiceLock_Key)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.Open, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.SafeDoorSmall:
                {
                    var x = (SafeDoorSmall)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.open, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MultiKeyLock:
                {
                    var x = (MultiKeyLock)c;
                    // Derive unlocked from keys[] — never call checkLock() on the poll path
                    // (Apply calls it when unlocking).
                    bool unlocked = false;
                    try
                    {
                        var keys = x.keys;
                        if (keys != null && keys.Length > 0)
                        {
                            unlocked = true;
                            for (int i = 0; i < keys.Length; i++)
                            {
                                if (!keys[i]) { unlocked = false; break; }
                            }
                        }
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                    entry = PuzzleDomainUtil.Mk(type, wid, unlocked, false, false, PackBoolArray(x.keys), 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.OpenableDrawer:
                {
                    var x = (OpenableDrawer)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.open, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MED_KeyGrid:
                {
                    // Dig V: pack nodes[i].connected → Int0; Int1 = node count (valid
                    // marker so all-zero mid-state is retained). Bool0 = static solved.
                    // Instance registered for scan; WorldId-0 global poll is authority.
                    var x = (MED_KeyGrid)c;
                    int bits = 0, count = 0;
                    try { PackKeyGridNodes(x, out bits, out count); } catch (System.Exception e) { Guard.Swallow(e); }
                    entry = PuzzleDomainUtil.Mk(type, wid, MED_KeyGrid.solved, false, false, bits, count, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ArianePhotoCode:
                {
                    entry = PuzzleDomainUtil.Mk(type, wid, ArianePhotoCode.code != 0, false, false, ArianePhotoCode.code, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>Host-only global (WorldId 0) — mirrors RadioManagerState.</summary>
        public static PuzzleStateEntry ReadKeyGridGlobal()
        {
            // Dig V: extend payload with 17× connected bits (Int0) + count marker (Int1).
            // Keep WorldId 0 poll / client-emit path; solved stays Bool0.
            bool solved = false;
            try { solved = MED_KeyGrid.solved; } catch (System.Exception e) { Guard.Swallow(e); }
            int bits = 0, count = 0;
            try
            {
                var grids = WorldLookup.All<MED_KeyGrid>();
                if (grids != null)
                {
                    for (int g = 0; g < grids.Length; g++)
                    {
                        if (grids[g] == null) continue;
                        PackKeyGridNodes(grids[g], out bits, out count);
                        if (count > 0) break;
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return PuzzleDomainUtil.Mk(PuzzleType.MED_KeyGrid, 0, solved, false, false, bits, count, 0, 0, 0f);
        }

        /// <summary>Host-only global (WorldId 0) — static ArianePhotoCode.code.</summary>
        public static PuzzleStateEntry ReadArianePhotoCodeGlobal()
        {
            int code = 0;
            try { code = ArianePhotoCode.code; } catch (System.Exception e) { Guard.Swallow(e); }
            return PuzzleDomainUtil.Mk(PuzzleType.ArianePhotoCode, 0, code != 0, false, false, code, 0, 0, 0, 0f);
        }

        public static void ApplyMusicBox(RES_MusicBox x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Rising-edge onSuccess (Dig R): Melon opened / hasCassette / onSuccess.
            // Native Update (~0x4B4ED0) only fires onSuccess on opened false→true;
            // after Apply latches opened, native retry permanently skipped. Asset
            // onSuccess → MinimapPOIObject.dimPOI. Prior ApplyMusicBox set hasCassette
            // then SnapMusicBox (latch opened + force hasCassette=true + pose) — never
            // Invoked onSuccess. Late-join FullRefresh same gap; LoadState does not
            // invoke. Mirror PEN_Reaktor / RES_Shrine rising-edge: Invoke on BOTH live
            // MutateWorld AND FullRefresh (idempotent dimPOI; no separate onLoad).
            // Protocol 10 unchanged (reuse RES_MusicBox Bool0=opened, Bool1=hasCassette).
            bool was = false;
            try { was = x.opened; } catch (System.Exception ex) { Guard.Swallow(ex); }
            try { x.hasCassette = e.Bool1; } catch (System.Exception ex) { Guard.Swallow(ex); }
            if (!e.Bool0) return;
            if (!was)
            {
                NetGate.BeginApply();
                try
                {
                    try
                    {
                        if (x.onSuccess != null)
                            x.onSuccess.Invoke();
                    }
                    catch (System.Exception ex) { Guard.Swallow(ex); }
                    SnapMusicBox(x);
                    // Reassert wire cassette — Snap must not own hasCassette.
                    try { x.hasCassette = e.Bool1; } catch (System.Exception ex) { Guard.Swallow(ex); }
                }
                catch (System.Exception ex) { Guard.Swallow(ex); }
                finally { NetGate.EndApply(); }
            }
        }

        public static void ApplyLibraryPc(RES_LibraryPC x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Mid robotPos always (live + FullRefresh). Bool1 = pack valid. A peer's live move glides at the
            // native movementSpeed (native Move* coroutines set gameState 4/7, so they cannot be replayed on a
            // player outside the screen); native Update draws RobotX/RobotY from robotPos every frame.
            if (e.Bool1)
            {
                var to = new Vector2(e.Float0, e.Float1);
                if (PuzzleFx.LiveApply && !e.Bool0) LibraryRobotGlide.To(x, to);
                else
                {
                    LibraryRobotGlide.Stop(x);
                    try { x.robotPos = to; } catch (System.Exception ex) { Guard.Swallow(ex); }
                }
            }
            if (!e.Bool0) return;
            bool was = false;
            try { was = x.solved; } catch (System.Exception ex) { Guard.Swallow(ex); }
            SnapLibraryPc(x);
            // Asset onSuccess → Play (FMOD one-shot). Rising-edge both paths like MusicBox.
            if (!was)
            {
                NetGate.BeginApply();
                try
                {
                    if (x.onSuccess != null)
                        x.onSuccess.Invoke();
                }
                catch (System.Exception ex) { Guard.Swallow(ex); }
                finally { NetGate.EndApply(); }
            }
        }

        public static void ApplyPaternoster(RES_Paternoster x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.setPower(e.Bool0); }
            catch
            {
                try { x.powered = e.Bool0; } catch (System.Exception ex) { Guard.Swallow(ex); }
            }
        }

        public static void ApplyKeyGrid(PuzzleStateEntry e)
        {
            // Dig V: Bool0 → static solved; Int0 → nodes[i].connected; Int1 count
            // marker gates node apply so all-zero packs still refresh visuals.
            // nodesObjects SetActive mirrors native Update under NetGate.
            try { MED_KeyGrid.solved = e.Bool0; } catch (System.Exception ex) { Guard.Swallow(ex); }
            if (e.Int1 <= 0) return;
            MED_KeyGrid[] grids = null;
            try { grids = WorldLookup.All<MED_KeyGrid>(); } catch (System.Exception ex) { Guard.Swallow(ex); }
            if (grids == null) return;
            for (int g = 0; g < grids.Length; g++)
            {
                var x = grids[g];
                if (x == null) continue;
                ApplyKeyGridNodes(x, e.Int0, e.Int1);
            }
        }

        /// <summary>Pack ≤32 MED_KeyNodeConnection.connected bits; count = valid marker.</summary>
        static void PackKeyGridNodes(MED_KeyGrid x, out int bits, out int count)
        {
            bits = 0;
            count = 0;
            if (x == null) return;
            try
            {
                var nodes = x.nodes;
                if (nodes == null) return;
                int n = nodes.Count;
                if (n > 32) n = 32;
                count = n;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var node = nodes[i];
                        if (node != null && node.connected)
                            bits |= 1 << i;
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>Unpack Int0 into connected + nodesObjects active (NetGate).</summary>
        static void ApplyKeyGridNodes(MED_KeyGrid x, int bits, int count)
        {
            if (x == null) return;
            NetGate.BeginApply();
            try
            {
                try
                {
                    var nodes = x.nodes;
                    if (nodes != null)
                    {
                        int n = nodes.Count;
                        if (count > 0 && count < n) n = count;
                        if (n > 32) n = 32;
                        for (int i = 0; i < n; i++)
                        {
                            try
                            {
                                var node = nodes[i];
                                if (node != null)
                                    node.connected = (bits & (1 << i)) != 0;
                            }
                            catch (System.Exception e) { Guard.Swallow(e); }
                        }
                    }
                }
                catch (System.Exception e) { Guard.Swallow(e); }
                try
                {
                    var objs = x.nodesObjects;
                    if (objs != null)
                    {
                        int n = objs.Count;
                        if (count > 0 && count < n) n = count;
                        if (n > 32) n = 32;
                        for (int i = 0; i < n; i++)
                        {
                            try
                            {
                                var go = objs[i];
                                if (go != null)
                                    go.SetActive((bits & (1 << i)) != 0);
                            }
                            catch (System.Exception e) { Guard.Swallow(e); }
                        }
                    }
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            finally { NetGate.EndApply(); }
        }

        public static void ApplyArianePhotoCode(PuzzleStateEntry e)
        {
            try { ArianePhotoCode.code = e.Int0; } catch (System.Exception ex) { Guard.Swallow(ex); }
            try { ArianePhotoCode.LoadState(); } catch (System.Exception ex) { Guard.Swallow(ex); }
            // Reassert after LoadState in case it reloads from local save.
            try { ArianePhotoCode.code = e.Int0; } catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        public static void ApplyServiceLockKey(DET_ServiceLock_Key x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapServiceLockKey(x);
        }

        public static void ApplySafeDoorSmall(SafeDoorSmall x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            // Rising-edge onSolved: Asset → dimPOI. Snap opens keys/keypad/doors but
            // never Invoked onSolved (Dig AJ, mirror MusicBox Dig R both-path).
            bool was = false;
            try { was = x.open; } catch (System.Exception ex) { Guard.Swallow(ex); }
            SnapSafeDoorSmall(x);
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
        }

        public static void ApplyMultiKeyLock(MultiKeyLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            UnpackBoolArray(x.keys, e.Int0);
            if (!e.Bool0) return;
            try { x.checkLock(); } catch (System.Exception ex) { Guard.Swallow(ex); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            try { PuzzleSyncService.DisableInteractions(x); } catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        public static void ApplyOpenableDrawer(OpenableDrawer x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (x.open == e.Bool0) return;
            try { x.open = e.Bool0; } catch (System.Exception ex) { Guard.Swallow(ex); }
            PoseDrawer(x, e.Bool0);
        }

        public static void SnapMusicBox(RES_MusicBox x)
        {
            if (x == null) return;
            try { x.opened = true; } catch (System.Exception e) { Guard.Swallow(e); }
            // hasCassette owned by ApplyMusicBox from wire Bool1 — do not force true.
            try { if (x.CardPickup != null) x.CardPickup.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.BoxObs != null) x.BoxObs.SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.euleLid != null)
                {
                    var e = x.euleLid.localEulerAngles;
                    e.z = x.lidOpen;
                    x.euleLid.localEulerAngles = e;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.RevealPickups(x.CardPickup); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.DisableInteractions(x); } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.tapeInteraction != null) x.tapeInteraction.SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void SnapLibraryPc(RES_LibraryPC x)
        {
            if (x == null) return;
            try { x.solved = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Pickup != null) x.Pickup.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Tome != null) x.Tome.gameObject.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.RevealPickups(x.Pickup); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.DisableInteractions(x); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void SnapServiceLockKey(DET_ServiceLock_Key x)
        {
            if (x == null) return;
            try { x.Open = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.KeyObj != null) x.KeyObj.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.Key != null)
                {
                    var e = x.Key.localEulerAngles;
                    e.z = x.keyTargetRot;
                    x.Key.localEulerAngles = e;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.Door != null)
                {
                    var e = x.Door.localEulerAngles;
                    e.y = x.doorTargetRot;
                    x.Door.localEulerAngles = e;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.lockInteraction != null)
                    x.lockInteraction.SetActive(false);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.DisableInteractions(x); } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.inter != null) PuzzleSyncService.DisableOne(x.inter); } catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapSafeDoorSmall(SafeDoorSmall x)
        {
            if (x == null) return;
            try { x.open = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.key != null) x.key.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.smallKey != null) x.smallKey.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.RevealPickups(x.key); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.RevealPickups(x.smallKey); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.keypad != null)
                {
                    x.keypad.solved = true;
                    PuzzleSyncService.DisableInteractions(x.keypad);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        static void PoseDrawer(OpenableDrawer x, bool open)
        {
            try
            {
                if (x.drawer != null && open)
                    x.drawer.localPosition = x.openPos;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var content = x.Content;
                if (content != null)
                {
                    for (int i = 0; i < content.Count; i++)
                    {
                        try
                        {
                            if (content[i] != null)
                                content[i].SetActive(open);
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (open)
            {
                try { PuzzleSyncService.RevealPickups(x.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            }
        }

        internal static int PackBoolArray(Il2CppStructArray<bool> arr)
        {
            int bits = 0;
            if (arr == null) return bits;
            try
            {
                int n = arr.Length;
                if (n > 32) n = 32;
                for (int i = 0; i < n; i++)
                {
                    try { if (arr[i]) bits |= 1 << i; } catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return bits;
        }

        internal static void UnpackBoolArray(Il2CppStructArray<bool> arr, int bits)
        {
            if (arr == null) return;
            try
            {
                int n = arr.Length;
                if (n > 32) n = 32;
                for (int i = 0; i < n; i++)
                {
                    try { arr[i] = (bits & (1 << i)) != 0; } catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
