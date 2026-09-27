// Native solve edges for chapter machines (pump, writer, shutters, pipes, …).
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
        static class EnvEmit
        {
            // Update-postfix edges (Magpie/Shutters/…) — one emit per WorldId until scene refresh.
            static readonly System.Collections.Generic.HashSet<string> _once
                = new System.Collections.Generic.HashSet<string>();

            public static void Progressed(PuzzleType type, Component c) => Send(type, c, true);
            public static void Read(PuzzleType type, Component c) => Send(type, c, false);
            public static void ReadOnce(PuzzleType type, Component c)
            {
                if (c == null || NetGate.IsApplying) return;
                var net = LanNetworkManager.Instance;
                if (net == null || !net.IsConnected) return;
                ulong id = WorldId.FromGameObject(c.gameObject);
                string key = ((byte)type) + "_" + id.ToString("X");
                if (_once.Contains(key)) return;
                Send(type, c, false);
                _once.Add(key);
            }

            public static void ClearOnce() => _once.Clear();

            public static void LabRings(LAB_Rings x)
            {
                if (x == null) return;
                if (x.solved) Progressed(PuzzleType.LAB_Rings, x);
                else Read(PuzzleType.LAB_Rings, x);
            }

            public static void ServiceLock(DET_ServiceLock x)
            {
                if (x == null) return;
                bool ok = false;
                try { ok = x.solved != null && x.solved.solved; } catch { }
                if (ok) Progressed(PuzzleType.DET_ServiceLock, x);
                else Read(PuzzleType.DET_ServiceLock, x);
            }

            public static void Incinerator(MED_Incinerator x)
            {
                if (x == null) return;
                bool ok = false;
                try { ok = x.solved; } catch { }
                if (ok) Progressed(PuzzleType.MED_Incinerator, x);
                else Read(PuzzleType.MED_Incinerator, x);
            }

            public static void MultiLock(Component x)
            {
                if (x == null) return;
                bool ok = false;
                try
                {
                    var med = x as MED_MultiLock;
                    if (med != null) ok = med.unlocked;
                    else
                    {
                        var lab = x as LAB_MultiLock;
                        if (lab != null) ok = lab.unlocked;
                    }
                }
                catch { }
                if (ok) Progressed(PuzzleType.MultiLock, x);
                else Read(PuzzleType.MultiLock, x);
            }

            public static void Pump(MED_Pump x)
            {
                if (x == null) return;
                bool ok = false;
                try { ok = x.solved; } catch { }
                if (ok) Progressed(PuzzleType.MED_Pump, x);
                else Read(PuzzleType.MED_Pump, x);
            }

            public static void Power(RES_Power x)
            {
                if (x == null) return;
                bool ok = false;
                try { ok = x.solved; } catch { }
                if (ok) Progressed(PuzzleType.RES_Power, x);
                else Read(PuzzleType.RES_Power, x);
            }

            public static void Dial(ROT_DialLock x)
            {
                if (x == null) return;
                bool ok = false;
                try { ok = x.solved; } catch { }
                if (ok) Progressed(PuzzleType.DialLock, x);
                else Read(PuzzleType.DialLock, x);
            }

            public static void EvidenceLocker(EvidenceLockerLogicPuzzle x)
            {
                if (x == null) return;
                bool ok = false;
                try { ok = x.solved; } catch { }
                if (ok) Progressed(PuzzleType.EvidenceLockerPuzzle, x);
                else Read(PuzzleType.EvidenceLockerPuzzle, x);
            }

            public static void CardWriter(MED_CardWriter x)
            {
                if (x == null) return;
                bool ok = false;
                try { ok = x.solved; } catch { }
                if (ok) Progressed(PuzzleType.MED_CardWriter, x);
                else Read(PuzzleType.MED_CardWriter, x);
            }

            static void Send(PuzzleType type, Component c, bool progressed)
            {
                if (c == null || NetGate.IsApplying) return;
                var net = LanNetworkManager.Instance;
                if (net == null || !net.IsConnected) return;
                ulong id = WorldId.FromGameObject(c.gameObject);
                if (progressed) net.PuzzleSync.EmitProgressed(type, id);
                else net.PuzzleSync.Emit(type, id, c);
            }
        }

    [HarmonyPatch(typeof(MED_Pump), "checkSolved")]
    public static class MedPumpSolvedPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Pump __instance)
        {
            if (__instance == null || !__instance.solved) return;
            EnvEmit.Progressed(PuzzleType.MED_Pump, __instance);
            try
            {
                if (__instance.flood != null)
                    EnvEmit.Progressed(PuzzleType.MED_FloodedBathroom, __instance.flood);
            }
            catch { }
        }
    }


    // MED_Pump: emit mid-water a/b/c on AB/AC/BA/BC/CA/CB transfers (Int0–Int2),
    // Progressed on solve. Native transfer buttons mutate a/b/c then checkSolved —
    // patch transfers so peers see mid-water without waiting for Tick (Dig Y).
    // Initial a=12,b=0,c=0; IsProgressed holds any departure. Existing checkSolved
    // Progressed + ApplyPump/SnapMedPump retained (0.5.28 onSolved).
    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.AB))]
    public static class MedPumpABPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Pump __instance) => EnvEmit.Pump(__instance);
    }

    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.AC))]
    public static class MedPumpACPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Pump __instance) => EnvEmit.Pump(__instance);
    }

    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.BA))]
    public static class MedPumpBAPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Pump __instance) => EnvEmit.Pump(__instance);
    }

    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.BC))]
    public static class MedPumpBCPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Pump __instance) => EnvEmit.Pump(__instance);
    }

    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.CA))]
    public static class MedPumpCAPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Pump __instance) => EnvEmit.Pump(__instance);
    }

    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.CB))]
    public static class MedPumpCBPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Pump __instance) => EnvEmit.Pump(__instance);
    }

    [HarmonyPatch(typeof(MED_FloodedBathroom), nameof(MED_FloodedBathroom.Drain))]
    public static class MedFloodDrainPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_FloodedBathroom __instance)
        {
            EnvEmit.Progressed(PuzzleType.MED_FloodedBathroom, __instance);
        }
    }

    // MED_CardWriter: emit partial connected pack + remainingSteps on Update while
    // card / writeMode / mid-trace active (Dig AB). Native durable state mutates
    // inside Update (no separate write/trace method) — patch so peers see mid-graph
    // without waiting for Tick. Read mid / Progressed solved. IsProgressed holds
    // Bool0||Bool1||Int3 across remount. eatCard covers insert edge.
    [HarmonyPatch(typeof(MED_CardWriter), "Update")]
    public static class MedCardWriterPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_CardWriter __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                bool active = false;
                try { active = __instance.solved || __instance.hasCard || __instance.writeMode; } catch { }
                if (!active)
                {
                    // Mid-trace with card already inserted may leave writeMode false
                    // briefly — still emit when any node is connected.
                    try
                    {
                        var nodes = __instance.nodes;
                        if (nodes != null)
                        {
                            int n = nodes.Count;
                            for (int i = 0; i < n; i++)
                            {
                                var node = nodes[i];
                                if (node != null && node.connected) { active = true; break; }
                            }
                        }
                    }
                    catch { }
                }
                if (active)
                    EnvEmit.CardWriter(__instance);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MED_CardWriter), nameof(MED_CardWriter.eatCard))]
    public static class MedCardWriterEatCardPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_CardWriter __instance) => EnvEmit.CardWriter(__instance);
    }

    [HarmonyPatch(typeof(ROT_Pipes), nameof(ROT_Pipes.TurnValve))]
    public static class RotPipesPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Pipes __instance)
        {
            EnvEmit.Progressed(PuzzleType.ROT_Pipes, __instance);
        }
    }

    [HarmonyPatch(typeof(ROT_Magpie), "Update")]
    public static class RotMagpiePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Magpie __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                if (__instance.opened)
                    EnvEmit.ReadOnce(PuzzleType.ROT_Magpie, __instance);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PEN_Reaktor), "win")]
    public static class PenReaktorWinPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Reaktor __instance)
        {
            EnvEmit.Progressed(PuzzleType.PEN_Reaktor, __instance);
        }
    }

    [HarmonyPatch(typeof(EXC_Seilbahn), nameof(EXC_Seilbahn.goDown))]
    public static class ExcSeilbahnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(EXC_Seilbahn __instance)
        {
            EnvEmit.Progressed(PuzzleType.EXC_Seilbahn, __instance);
        }
    }

    [HarmonyPatch(typeof(EXC_Hatch), nameof(EXC_Hatch.OpenHatch))]
    public static class ExcHatchPatch
    {
        [HarmonyPostfix]
        public static void Postfix(EXC_Hatch __instance)
        {
            EnvEmit.Progressed(PuzzleType.EXC_Hatch, __instance);
        }
    }

    // LAB_Rings: emit partial finger pack on place/take (Int0), Progressed on solve.
    // checkSolution alone used to gate emit on solved — peers never saw mid-puzzle
    // finger states (Dig O). placeRing/takeRing cover the durable state edges;
    // checkSolution still Progressed when native latches solved.
    [HarmonyPatch(typeof(LAB_Rings), nameof(LAB_Rings.placeRing))]
    public static class LabRingsPlacePatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Rings __instance)
        {
            EnvEmit.LabRings(__instance);
        }
    }

    [HarmonyPatch(typeof(LAB_Rings), nameof(LAB_Rings.takeRing))]
    public static class LabRingsTakePatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Rings __instance)
        {
            EnvEmit.LabRings(__instance);
        }
    }

    [HarmonyPatch(typeof(LAB_Rings), nameof(LAB_Rings.checkSolution))]
    public static class LabRingsPatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Rings __instance)
        {
            if (__instance == null || !__instance.solved) return;
            EnvEmit.Progressed(PuzzleType.LAB_Rings, __instance);
        }
    }

    // DET_ServiceLock: emit partial pinning pack on SetPins (Int0), Progressed on solve.
    // Native FlipButton coroutine mutates pinning[] then SetPins/AdjustCrown — patch
    // SetPins so peers see mid-pin without waiting for Tick (Dig T). Avoid FlipButton
    // itself (coroutine start; pinning not yet updated; anim/SFX side effects).
    [HarmonyPatch(typeof(DET_ServiceLock), nameof(DET_ServiceLock.SetPins))]
    public static class DetServiceLockSetPinsPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DET_ServiceLock __instance)
        {
            EnvEmit.ServiceLock(__instance);
        }
    }

    // MED_Incinerator: emit mid-dial A/B/C on plus/minus (Int0–Int2), Progressed on solve.
    // Native dial buttons mutate A/B/C then SetGuide — patch plus/minus so peers see
    // mid-dial without waiting for Tick (Dig W). Initial A=B=C=10; IsProgressed holds
    // any departure. StartShutdown still Progressed when native latches solved.
    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.plusA))]
    public static class MedIncineratorPlusAPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Incinerator __instance) => EnvEmit.Incinerator(__instance);
    }

    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.plusB))]
    public static class MedIncineratorPlusBPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Incinerator __instance) => EnvEmit.Incinerator(__instance);
    }

    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.plusC))]
    public static class MedIncineratorPlusCPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Incinerator __instance) => EnvEmit.Incinerator(__instance);
    }

    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.minusA))]
    public static class MedIncineratorMinusAPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Incinerator __instance) => EnvEmit.Incinerator(__instance);
    }

    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.minusB))]
    public static class MedIncineratorMinusBPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Incinerator __instance) => EnvEmit.Incinerator(__instance);
    }

    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.minusC))]
    public static class MedIncineratorMinusCPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Incinerator __instance) => EnvEmit.Incinerator(__instance);
    }

    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.StartShutdown))]
    public static class MedIncineratorStartShutdownPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Incinerator __instance) => EnvEmit.Incinerator(__instance);
    }

    // MultiLock: emit mid-key element bits on UnlockKey (Int0), Progressed on full unlock.
    // Native UnlockKey sets Fire/Earth/Water/Air/Gold(/Star) then may latch unlocked —
    // patch so peers see mid-key without waiting for Tick (Dig X). IsProgressed holds
    // Int0!=0 across remount. Existing ApplyMulti onUnlocked paths stay (0.5.29).
    [HarmonyPatch(typeof(MED_MultiLock), nameof(MED_MultiLock.UnlockKey))]
    public static class MedMultiLockUnlockKeyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_MultiLock __instance) => EnvEmit.MultiLock(__instance);
    }

    [HarmonyPatch(typeof(LAB_MultiLock), nameof(LAB_MultiLock.UnlockKey))]
    public static class LabMultiLockUnlockKeyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_MultiLock __instance) => EnvEmit.MultiLock(__instance);
    }

    // DialLock (ROT_DialLock): emit mid-dial A/B/C/D on TurnUp/TurnDown (Int0–Int3),
    // Progressed on solve. Native TurnUp/TurnDown mutate dial ints — patch so peers
    // see mid-dial without waiting for Tick (Dig AA). IsProgressed holds departure
    // from initial 0/0/0/5 across remount. Existing ApplyDial / solved polling stay.
    [HarmonyPatch(typeof(ROT_DialLock), nameof(ROT_DialLock.TurnUp), new System.Type[] { typeof(int) })]
    public static class RotDialLockTurnUpPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_DialLock __instance) => EnvEmit.Dial(__instance);
    }

    [HarmonyPatch(typeof(ROT_DialLock), nameof(ROT_DialLock.TurnDown), new System.Type[] { typeof(int) })]
    public static class RotDialLockTurnDownPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_DialLock __instance) => EnvEmit.Dial(__instance);
    }

    // RES_Power: emit mid-fuse states pack on Flip(int) (Int0), Progressed on solve.
    // Native Flip toggles states[i] then may TryMasterFlip — patch so peers see
    // mid-fuse without waiting for Tick (Dig Z). IsProgressed holds Int0!=0 across
    // remount. Existing ApplyPower OnSuccess rising-edge stays (0.5.27).
    [HarmonyPatch(typeof(RES_Power), nameof(RES_Power.Flip), new System.Type[] { typeof(int) })]
    public static class ResPowerFlipPatch
    {
        [HarmonyPostfix]
        public static void Postfix(RES_Power __instance) => EnvEmit.Power(__instance);
    }

    // EvidenceLocker: emit mid-button states pack on logic(int) (Int0/Int1), Progressed
    // on solve. Native Update polls buttons → logic(i) toggles states[i] + lights;
    // patch so peers see mid-press without waiting for Tick (Dig AD). IsProgressed
    // holds Bool0 || Int0!=0 || Int1!=0 across remount. Existing ApplyEvidenceLocker
    // door snap on Bool0 stays; Apply also snaps lights after unpack.
    [HarmonyPatch(typeof(EvidenceLockerLogicPuzzle), nameof(EvidenceLockerLogicPuzzle.logic), new System.Type[] { typeof(int) })]
    public static class EvidenceLockerLogicPatch
    {
        [HarmonyPostfix]
        public static void Postfix(EvidenceLockerLogicPuzzle __instance) => EnvEmit.EvidenceLocker(__instance);
    }

    [HarmonyPatch(typeof(ROT_MeatBlocker), nameof(ROT_MeatBlocker.pickup))]
    public static class RotMeatPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_MeatBlocker __instance)
        {
            // Hold Death-seal emit while NG+ KeyOfSacrifice is still obtainable —
            // mirrors ApplyMeatBlocker hold (wiki Artifact softlock).
            try
            {
                if (__instance != null && __instance.blocked)
                {
                    var id = __instance.ID;
                    if (id != null && string.Equals(id, "Death", System.StringComparison.Ordinal))
                    {
                        var net = LanNetworkManager.Instance;
                        if (net != null && net.IsConnected
                            && net.PickupSync.KeyOfSacrificeAvailableUnclaimed())
                        {
                            PlaytestLog.Event("Puzzle", "hold Death MeatBlocker emit until KeyOfSacrifice");
                            return;
                        }
                    }
                }
            }
            catch { }
            EnvEmit.Read(PuzzleType.ROT_MeatBlocker, __instance);
        }
    }

    [HarmonyPatch(typeof(RES_Shutters), "Update")]
    public static class ResShuttersPatch
    {
        [HarmonyPostfix]
        public static void Postfix(RES_Shutters __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                if (__instance.unlocked)
                    EnvEmit.ReadOnce(PuzzleType.RES_Shutters, __instance);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(BiodomeDoorLock), "Update")]
    public static class BiodomeLockPatch
    {
        [HarmonyPostfix]
        public static void Postfix(BiodomeDoorLock __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                if (__instance.KeyLevel > 0 || !__instance.hasLock)
                    EnvEmit.ReadOnce(PuzzleType.BiodomeDoorLock, __instance);
            }
            catch { }
        }
    }

    // LAB_Waage: placeItem starts delayedMulti which SetActive(true) on MultiInteraction
    // after a yield — native gate flag is still false at postfix time. EmitProgressed
    // forces Bool0 so peers/late-join ApplyWaage activate the gate now (Dig S).
    [HarmonyPatch(typeof(LAB_Waage), nameof(LAB_Waage.placeItem))]
    public static class LabWaagePlacePatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Waage __instance)
        {
            EnvEmit.Progressed(PuzzleType.LAB_Waage, __instance);
        }
    }

    [HarmonyPatch(typeof(LAB_PatternLock), nameof(LAB_PatternLock.toggleButton))]
    public static class PatternLockTogglePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LAB_PatternLock __instance)
        {
            if (__instance == null) return true;
            try
            {
                if (__instance.solved) return false;
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(LAB_PatternLock __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                if (__instance.solved)
                    EnvEmit.Progressed(PuzzleType.PatternLock, __instance);
            }
            catch { }
        }
    }
}
