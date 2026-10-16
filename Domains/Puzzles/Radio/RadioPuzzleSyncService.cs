using FMODUnity;
using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>RadioStationTutorial / ROT_RadioAlignment / DET_RadioCodeLock / RadioManagerState.</summary>
    public sealed class RadioPuzzleSyncService
    {
        internal static PuzzleStateEntry ReadTutorial(RadioStationTutorialPuzzle x, long wid)
            => Mk(PuzzleType.RadioStationTutorial, wid, x.solved, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadAlignment(ROT_RadioAlignment x, long wid)
            => Mk(PuzzleType.ROT_RadioAlignment, wid, x.east, false, false, x.correctAntenna, x.setAntenna, 0, 0, x.QualityE);

        /// <summary>Bool0 = keypad solved (Apply snaps the pad + doors); Int0..2 = frequency / code / hint station.</summary>
        internal static PuzzleStateEntry ReadCode(DET_RadioCodeLock x, long wid)
        {
            bool padSolved = x.keypad != null && (x.keypad.solved || x.keypad.opening);
            return Mk(PuzzleType.DET_RadioCodeLock, wid, padSolved, false, false, x.frequency, x.code, x.hintStation, 0, 0);
        }

        /// <summary>WorldId-0 global: RadioManager.moduleInstalled (Int0 bit 0).</summary>
        internal static PuzzleStateEntry ReadManagerState()
        {
            int radioBools = RadioManager.moduleInstalled ? 1 : 0;
            return Mk(PuzzleType.RadioManagerState, 0, radioBools != 0, false, false, radioBools, 0, 0, 0, 0f);
        }

        /// <summary>
        /// Native Update, when completionTimer reaches completionTime: EndCutscene.trigger, latch solved, StartCoroutine
        /// OpenDoor (PlayOneShot unlockedSFX, lerp Door Z 180 → 20, returnStations on, TutorialStation off). OnEnable
        /// always restores the unsolved pose, and Door is not a Doorway_* (no DoorSync cover). Live in-room edge =
        /// EndCutscene + SFX + end pose; everything else = the end pose only.
        /// </summary>
        internal static void ApplyTutorial(RadioStationTutorialPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.solved;
            x.solved = e.Bool0;
            if (!e.Bool0) return;
            PuzzleEdge.Solved("RadioStationTutorial", was, true,
                durable: () => { if (!was) SnapTutorialFinalPose(x); },
                onLive: () =>
                {
                    NetGate.BeginApply();
                    try
                    {
                        Native("tutorial-cutscene", () => { if (x.EndCutscene != null) x.EndCutscene.trigger(); });
                        if (!string.IsNullOrEmpty(x.unlockedSFX))
                            RuntimeManager.PlayOneShot(x.unlockedSFX, x.transform.position);
                    }
                    finally { NetGate.EndApply(); }
                    SnapTutorialFinalPose(x);
                }, at: x);
        }

        static void SnapTutorialFinalPose(RadioStationTutorialPuzzle x)
        {
            if (x.Door != null) x.Door.localEulerAngles = new Vector3(0f, 0f, 20f);
            if (x.returnStations != null) x.returnStations.SetActive(true);
            if (x.TutorialStation != null) x.TutorialStation.SetActive(false);
        }

        /// <summary>Latch true only: a host false must not wipe a peer who just acquired the module.</summary>
        internal static void ApplyManager(PuzzleStateEntry e)
        {
            if ((e.Int0 & 1) != 0)
                RadioManager.moduleInstalled = true;
        }

        internal static void ApplyAlignment(ROT_RadioAlignment x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.east = e.Bool0;
            x.correctAntenna = e.Int0;
            x.setAntenna = e.Int1;
            x.QualityE = e.Float0;
            Native("alignment-load", x.LoadState);
        }

        /// <summary>
        /// A peer's solve reaching the host must not replace the host-generated code (ApplyingPeerPacket); remount /
        /// host broadcast / late join copy the ints and the keypad solution so Start()'s local roll does not stick.
        /// </summary>
        internal static void ApplyCode(DET_RadioCodeLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (!PuzzleSyncService.ApplyingPeerPacket)
            {
                x.frequency = e.Int0;
                x.code = e.Int1;
                x.hintStation = e.Int2;
                if (x.keypad != null && e.Int1 != 0)
                    x.keypad.solution = e.Int1.ToString();
            }
            if (!e.Bool0) return;
            if (x.keypad != null)
            {
                x.keypad.solved = true;
                x.keypad.opening = true;
            }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            if (x.keypad != null)
                PuzzleSyncService.TryUnlockDoors(x.keypad.gameObject);
        }
    }
}
