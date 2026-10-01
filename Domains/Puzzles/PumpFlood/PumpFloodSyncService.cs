using System;
using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>MED_Pump / MED_FloodedBathroom + flood control switches.</summary>
    public sealed class PumpFloodSyncService
    {
        internal static PuzzleStateEntry ReadPump(MED_Pump x, long wid)
            => Mk(PuzzleType.MED_Pump, wid, x.solved, false, false, x.a, x.b, x.c, 0, 0);

        internal static PuzzleStateEntry ReadFlood(MED_FloodedBathroom x, long wid)
        {
            bool drained = x.Ladder != null && x.Ladder.activeSelf;
            return Mk(PuzzleType.MED_FloodedBathroom, wid, drained, false, false, 0, 0, 0, 0, x.level);
        }

        internal static PuzzleStateEntry ReadFloodSwitch(FloodControlSwitch x, long wid)
            => Mk(PuzzleType.FloodControlSwitch, wid, x.state, false, false, 0, 0, 0, 0, 0);

        /// <summary>
        /// Native durable state is code[] + input[] (checkSolution compares them): Int0 = code, Int1 = input, so a peer
        /// never keeps a stale input while the switch poses look right.
        /// </summary>
        internal static PuzzleStateEntry ReadFloodControls(FloodControls x, long wid)
        {
            var code = x.code;
            var input = x.input;
            int codeBits = code != null && code.Length <= 32 ? ResidencyPuzzleSyncService.PackBoolArray(code) : 0;
            int inputBits = input != null && input.Length <= 32 ? ResidencyPuzzleSyncService.PackBoolArray(input) : 0;
            return Mk(PuzzleType.FloodControls, wid, x.done, x.locked, false, codeBits, inputBits, 0, 0, 0);
        }

        /// <summary>
        /// Native checkSolved invokes onSolved (scene data MED PumpLogic: dimPOI, StartCutscene PumpSuccess,
        /// RecordSplit) and drains; onLoad is the dimPOI alone. Live in-room edge = onSolved; everything else = onLoad, once.
        /// </summary>
        internal static void ApplyPump(MED_Pump x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.solved;
            bool moved = x.a != e.Int0 || x.b != e.Int1 || x.c != e.Int2;
            x.a = e.Int0; x.b = e.Int1; x.c = e.Int2;
            // Native Update eases the water bars to a/b/c; the pressure sprite/light colour is only set by
            // checkSolved (Ghidra MED_Pump.c), so paint it here: blue once solved, red otherwise.
            if (x.PressureSprite != null) x.PressureSprite.color = e.Bool0 ? x.Blue : x.Red;
            if (x.PressureLight != null) x.PressureLight.color = e.Bool0 ? x.Blue : x.Red;
            // A peer's transfer: the button click + water rush the native AB/BC/... play.
            if (moved && PuzzleFx.LiveApply && !was)
            {
                PuzzleFx.Press(x, x.buttonSFX);
                PuzzleFx.Press(x, x.waterSFX);
            }
            if (!e.Bool0) return;
            x.solved = true;
            SnapFlood(x.flood, PuzzleSyncService.LiveEdge);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            PuzzleEdge.Solved("MED_Pump", was, true,
                durable: () => { if (!was) PuzzleEdge.Invoke(x.onLoad); },
                onLive: () => PuzzleEdge.Invoke(x.onSolved), at: x);
        }

        internal static void ApplyFlood(MED_FloodedBathroom x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapFlood(x, PuzzleSyncService.LiveEdge);
        }

        /// <summary>Native FloodControlSwitch.Update writes fc.input[index] from state: mirror it (checkSolution reads input).</summary>
        internal static void ApplyFloodSwitch(FloodControlSwitch x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.state = e.Bool0;
            var fc = x.fc;
            var input = fc != null ? fc.input : null;
            int idx = x.index;
            if (input != null && idx >= 0 && idx < input.Length)
                input[idx] = e.Bool0;
            if (e.Bool0) PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>Partial switch progress (input) applies without done; dlc.locked + door unlock are flag snaps (dump too).</summary>
        internal static void ApplyFloodControls(FloodControls x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.done = e.Bool0;
            x.locked = e.Bool1;
            var code = x.code;
            if (code != null && code.Length <= 32)
                ResidencyPuzzleSyncService.UnpackBoolArray(code, e.Int0);
            var input = x.input;
            if (input != null && input.Length <= 32)
                ResidencyPuzzleSyncService.UnpackBoolArray(input, e.Int1);
            if (!e.Bool0) return;
            if (x.dlc != null) x.dlc.locked = false;
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>Live: the native Drain (once per scene); otherwise / on failure the drained end pose.</summary>
        static void SnapFlood(MED_FloodedBathroom x, bool play)
        {
            if (x == null) return;
            if (play && PuzzleSyncService.TryStartWorldAnim(PuzzleType.MED_FloodedBathroom, x.gameObject))
            {
                try { x.Drain(); return; }
                catch (Exception e) { Guard.Swallow("Puzzle.flood-drain", e); }
            }
            PoseFlood(x);
        }

        static void PoseFlood(MED_FloodedBathroom x)
        {
            Native("flood-level", () => x.setLevel(x.endDepth));
            x.level = x.endDepth;
            if (x.waterTrans != null)
            {
                var p = x.waterTrans.localPosition;
                p.y = x.endDepth;
                x.waterTrans.localPosition = p;
            }
            if (x.Ladder != null) x.Ladder.SetActive(true);
            if (x.ObservationFlood != null) x.ObservationFlood.SetActive(false);
        }
    }
}
