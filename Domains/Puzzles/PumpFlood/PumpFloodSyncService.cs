using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// MED_Pump / MED_FloodedBathroom + flood control switches — snap/read/apply.
    /// </summary>
    public sealed class PumpFloodSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.MED_Pump:
                {
                    var x = (MED_Pump)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, x.a, x.b, x.c, 0, 0);
                    return true;
                }
                case PuzzleType.MED_FloodedBathroom:
                {
                    var x = (MED_FloodedBathroom)c;
                    bool drained = x.Ladder != null && x.Ladder.activeSelf;
                    entry = PuzzleDomainUtil.Mk(type, wid, drained, false, false, 0, 0, 0, 0, x.level);
                    return true;
                }
                case PuzzleType.FloodControlSwitch:
                {
                    var x = (FloodControlSwitch)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.state, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.FloodControls:
                {
                    // Dig U: native durable state is code[] + input[] (Melon input@field,
                    // done, locked, code; checkSolution compares input vs code). Prior
                    // TryRead packed code→Int0 only (Int1=0) so peers/late-join kept
                    // stale input while switch poses looked correct → gate unsolved.
                    var x = (FloodControls)c;
                    int codeBits = 0;
                    int inputBits = 0;
                    try
                    {
                        var code = x.code;
                        if (code != null && code.Length <= 32)
                            codeBits = ResidencyPuzzleSyncService.PackBoolArray(code);
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                    try
                    {
                        var input = x.input;
                        if (input != null && input.Length <= 32)
                            inputBits = ResidencyPuzzleSyncService.PackBoolArray(input);
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.done, x.locked, false, codeBits, inputBits, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyPump(MED_Pump x, PuzzleStateEntry e, bool cinematic)
        {
            if (x == null) return;
            // Rising-edge onSolved: native checkSolved Invokes onSolved + transfers/drain.
            // AssetStudio MED_Pump.onSolved → dimPOI + StartCutscene + RecordSplit.
            // Prior SnapMedPump (~94–105) only latched solved + SnapFlood + TryUnlockDoors
            // — never Invoked onSolved → peer drain worked but cutscene/dimPOI/RecordSplit
            // skipped. Melon fields solved / onSolved / onLoad verified (camelCase).
            // onLoad → dimPOI only (no StartCutscene). Mirror ApplyMural live path +
            // ApplyRotKeypad late-join onLoad: MutateWorld && !was → BeginApply +
            // onSolved.Invoke(); !MutateWorld && !was → onLoad.Invoke() (dimPOI soak,
            // skip remount StartCutscene). Keep SnapMedPump drain.
            bool was = false;
            try { was = x.solved; } catch (System.Exception ex) { Guard.Swallow(ex); }
            try { x.a = e.Int0; x.b = e.Int1; x.c = e.Int2; } catch (System.Exception ex) { Guard.Swallow(ex); }
            if (!e.Bool0) return;
            SnapMedPump(x, cinematic);
            // Shared edge rule (PuzzleEdge): live rising edge = onSolved (dimPOI + StartCutscene + RecordSplit);
            // join dump / held re-snap = onLoad (dimPOI only), once. ReapplyHeld used to count as live.
            PuzzleEdge.Solved("MED_Pump", was, true,
                durable: () => { if (!was) LockSyncService.InvokeApplying(x.onLoad); },
                onLive: () => LockSyncService.InvokeApplying(x.onSolved));
        }

        public static void ApplyFlood(MED_FloodedBathroom x, PuzzleStateEntry e, bool cinematic)
        {
            if (x != null && e.Bool0)
                SnapFlood(x, cinematic);
        }

        public static void ApplyFloodSwitch(FloodControlSwitch x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.state = e.Bool0;
            // Dig U: native FloodControlSwitch.Update writes fc.input[index] from state.
            // Pose-only Apply left authoritative FloodControls.input stale → checkSolution
            // fails on peer/late-join even when switch sprites match. Mirror native write.
            try
            {
                var fc = x.fc;
                if (fc != null)
                {
                    var input = fc.input;
                    int idx = x.index;
                    if (input != null && idx >= 0 && idx < input.Length)
                        input[idx] = e.Bool0;
                }
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            if (e.Bool0) PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyFloodControls(FloodControls x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            x.done = e.Bool0;
            x.locked = e.Bool1;
            try
            {
                var code = x.code;
                if (code != null && code.Length <= 32)
                    ResidencyPuzzleSyncService.UnpackBoolArray(code, e.Int0);
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            // Dig U: unpack Int1→input[] (≤32) so partial switch progress survives
            // remount / late-join FullRefresh without requiring Bool0 done.
            try
            {
                var input = x.input;
                if (input != null && input.Length <= 32)
                    ResidencyPuzzleSyncService.UnpackBoolArray(input, e.Int1);
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            if (!e.Bool0) return;
            // dlc.locked is a flag snap — apply on join FullRefresh too.
            try { if (x.dlc != null) x.dlc.locked = false; } catch (System.Exception ex) { Guard.Swallow(ex); }
            // TryUnlockDoors is a lock-flag snap (flavor seals gated by AllowUnlock): join dump needs it too.
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapMedPump(MED_Pump x, bool play)
        {
            if (x == null) return;
            try { x.solved = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.flood != null)
                    SnapFlood(x.flood, play);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapFlood(MED_FloodedBathroom x, bool play)
        {
            if (x == null) return;
            if (play && PuzzleSyncService.TryStartWorldAnim(PuzzleType.MED_FloodedBathroom, x.gameObject))
            {
                try { x.Drain(); }
                catch { PoseFlood(x); }
            }
            else
                PoseFlood(x);
        }

        static void PoseFlood(MED_FloodedBathroom x)
        {
            if (x == null) return;
            try { x.setLevel(x.endDepth); } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.level = x.endDepth; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.waterTrans != null)
                {
                    var p = x.waterTrans.localPosition;
                    p.y = x.endDepth;
                    x.waterTrans.localPosition = p;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Ladder != null) x.Ladder.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.ObservationFlood != null) x.ObservationFlood.SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
