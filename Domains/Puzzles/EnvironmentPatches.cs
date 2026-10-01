// Native edges of the chapter machines (pump, writer, shutters, pipes, dials, …): each emits the puzzle's state the
// moment the native method changed it, instead of waiting for the 0.5 s poll.
using System.Collections.Generic;
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    static class EnvEmit
    {
        // Update-postfix edges (Magpie / Shutters): one emit per WorldId until the scene refreshes.
        static readonly HashSet<(byte, ulong)> _once = new HashSet<(byte, ulong)>();

        static bool Connected()
        {
            var net = LanNetworkManager.Instance;
            return net != null && net.IsConnected;
        }

        /// <summary>
        /// The one emit for a native edge: solved = the native "done" flag. A solved edge forces Bool0 (solved()/Open()
        /// are coroutines, so the flag the reader sees may still be false); anything else is a plain read + diff.
        /// </summary>
        public static void Edge(PuzzleType type, Component c, bool solved)
        {
            if (c == null || NetGate.IsApplying || !Connected()) return;
            var net = LanNetworkManager.Instance;
            ulong id = WorldId.FromGameObject(c.gameObject);
            if (solved) net.PuzzleSync.EmitProgressed(type, id);
            else net.PuzzleSync.Emit(type, id, c);
        }

        public static void Progressed(PuzzleType type, Component c) => Edge(type, c, true);
        public static void Read(PuzzleType type, Component c) => Edge(type, c, false);

        public static void ReadOnce(PuzzleType type, Component c)
        {
            if (c == null || NetGate.IsApplying || !Connected()) return;
            if (!_once.Add(((byte)type, WorldId.FromGameObject(c.gameObject)))) return;
            Read(type, c);
        }

        // Deferred emits: native input that changes state inside a coroutine (after its first yield).
        struct SoonItem { public float At; public Component C; public System.Action<Component> Fn; }
        static readonly List<SoonItem> _soon = new List<SoonItem>();

        public static void Soon(float delay, Component c, System.Action<Component> fn)
        {
            if (c == null || fn == null || _soon.Count > 64) return;
            _soon.Add(new SoonItem { At = Time.unscaledTime + delay, C = c, Fn = fn });
        }

        public static void TickSoon()
        {
            if (_soon.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = 0; i < _soon.Count; i++)
            {
                var s = _soon[i];
                if (s.At > now) continue;
                _soon.RemoveAt(i--);
                if (s.C == null) continue;
                try { s.Fn(s.C); } catch (System.Exception e) { Guard.Swallow(e); }
            }
        }

        public static void ClearOnce()
        {
            _soon.Clear();
            _once.Clear();
        }

        /// <summary>
        /// MED_Adler_EVdoors has no open flag: OpenDoors / CloseDoors start a lerp of DoorL / DoorR local X by
        /// ±Distance, so at postfix time a read still sees the start pose. Project the end pose and emit that.
        /// </summary>
        public static void AdlerEvDoors(MED_Adler_EVdoors x, bool open)
        {
            if (x == null || NetGate.IsApplying || !Connected()) return;
            float dist = ChapterExtraPuzzleSyncService.AdlerDistance(x);
            float lx = x.DoorL != null ? x.DoorL.localPosition.x : 0f;
            float rx = x.DoorR != null ? x.DoorR.localPosition.x : 0f;
            bool looksOpen = ChapterExtraPuzzleSyncService.AdlerLooksOpen(x, lx, rx);
            if (open && !looksOpen)
            {
                lx -= dist;
                rx += dist;
            }
            else if (!open && looksOpen)
            {
                lx += dist;
                rx -= dist;
            }
            ulong id = WorldId.FromGameObject(x.gameObject);
            if (id == 0) return;
            LanNetworkManager.Instance.PuzzleSync.EmitEntry(PuzzleDomainUtil.Mk(
                PuzzleType.MED_Adler_EVdoors, unchecked((long)id), open, false, false, 0, 0, 0, 0, lx, rx));
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
            if (__instance.flood != null)
                EnvEmit.Progressed(PuzzleType.MED_FloodedBathroom, __instance.flood);
        }
    }

    // Transfer buttons mutate a/b/c then checkSolved: peers see the water move at once (Int0..Int2).
    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.AB))]
    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.AC))]
    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.BA))]
    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.BC))]
    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.CA))]
    [HarmonyPatch(typeof(MED_Pump), nameof(MED_Pump.CB))]
    public static class MedPumpTransferPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Pump __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.MED_Pump, __instance, __instance.solved);
        }
    }

    [HarmonyPatch(typeof(MED_FloodedBathroom), nameof(MED_FloodedBathroom.Drain))]
    public static class MedFloodDrainPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_FloodedBathroom __instance) => EnvEmit.Progressed(PuzzleType.MED_FloodedBathroom, __instance);
    }

    // The card writer's node / step state changes inside native Update (no separate write / trace method).
    [HarmonyPatch(typeof(MED_CardWriter), "Update")]
    public static class MedCardWriterPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_CardWriter __instance)
        {
            // Native Update returns unless gameState == eventScreen (Ghidra MED_CardWriter.c): nothing it writes can
            // change outside the screen, so the per-frame read + diff runs only while someone is in one.
            if (__instance == null || NetGate.IsApplying || PlayerState.gameState != PlayerState.gameStates.eventScreen) return;
            try
            {
                // Mid-trace with the card inserted can leave writeMode false briefly: any connected node counts too.
                bool active = __instance.solved || __instance.hasCard || __instance.writeMode || AnyConnected(__instance.nodes);
                if (active)
                    EnvEmit.Edge(PuzzleType.MED_CardWriter, __instance, __instance.solved);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static bool AnyConnected(Il2CppSystem.Collections.Generic.List<MED_KeyNodeConnection> nodes)
        {
            if (nodes == null) return false;
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i] != null && nodes[i].connected) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(MED_CardWriter), nameof(MED_CardWriter.eatCard))]
    public static class MedCardWriterEatCardPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_CardWriter __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.MED_CardWriter, __instance, __instance.solved);
        }
    }

    [HarmonyPatch(typeof(ROT_Pipes), nameof(ROT_Pipes.TurnValve))]
    public static class RotPipesPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Pipes __instance) => EnvEmit.Progressed(PuzzleType.ROT_Pipes, __instance);
    }

    [HarmonyPatch(typeof(ROT_Magpie), "Update")]
    public static class RotMagpiePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Magpie __instance)
        {
            if (__instance != null && !NetGate.IsApplying && __instance.opened)
                EnvEmit.ReadOnce(PuzzleType.ROT_Magpie, __instance);
        }
    }

    // Native Update moves positions[current] (Clamp 0..4) and derives the readouts from them.
    [HarmonyPatch(typeof(PEN_Reaktor), "Update")]
    public static class PenReaktorUpdatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Reaktor __instance)
        {
            // Native Update moves rods only while its screen is Eventing and gameState == eventScreen (Ghidra
            // PEN_Reaktor.c): skip the per-frame read + diff the rest of the time.
            if (__instance == null || PlayerState.gameState != PlayerState.gameStates.eventScreen) return;
            EnvEmit.Edge(PuzzleType.PEN_Reaktor, __instance, __instance.solved);
        }
    }

    [HarmonyPatch(typeof(PEN_Reaktor), "win")]
    public static class PenReaktorWinPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Reaktor __instance) => EnvEmit.Progressed(PuzzleType.PEN_Reaktor, __instance);
    }

    [HarmonyPatch(typeof(EXC_Seilbahn), nameof(EXC_Seilbahn.goDown))]
    public static class ExcSeilbahnPatch
    {
        // goDown() toggles both ways and its coroutine latches down synchronously: read it (a return trip is
        // down=false; Progressed would force it true).
        [HarmonyPostfix]
        public static void Postfix(EXC_Seilbahn __instance) => EnvEmit.Read(PuzzleType.EXC_Seilbahn, __instance);
    }

    [HarmonyPatch(typeof(EXC_Hatch), nameof(EXC_Hatch.OpenHatch))]
    public static class ExcHatchPatch
    {
        [HarmonyPostfix]
        public static void Postfix(EXC_Hatch __instance) => EnvEmit.Progressed(PuzzleType.EXC_Hatch, __instance);
    }

    // placeRing / takeRing are the finger-state edges (Int0); checkSolution only adds the solved edge.
    [HarmonyPatch(typeof(LAB_Rings), nameof(LAB_Rings.placeRing))]
    [HarmonyPatch(typeof(LAB_Rings), nameof(LAB_Rings.takeRing))]
    public static class LabRingsPlaceTakePatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Rings __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.LAB_Rings, __instance, __instance.solved);
        }
    }

    [HarmonyPatch(typeof(LAB_Rings), nameof(LAB_Rings.checkSolution))]
    public static class LabRingsPatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Rings __instance)
        {
            if (__instance != null && __instance.solved)
                EnvEmit.Progressed(PuzzleType.LAB_Rings, __instance);
        }
    }

    // Interact deactivates the component itself, so the activeOnly poll never sees the edge.
    [HarmonyPatch(typeof(MapRevealInteraction), nameof(MapRevealInteraction.Interact))]
    public static class MapRevealInteractPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MapRevealInteraction __instance) => EnvEmit.Progressed(PuzzleType.MapReveal, __instance);
    }

    // The FlipButton coroutine mutates pinning[] then calls SetPins / AdjustCrown: SetPins is the state edge
    // (FlipButton itself fires before pinning changes and carries anim / SFX).
    [HarmonyPatch(typeof(DET_ServiceLock), nameof(DET_ServiceLock.SetPins))]
    public static class DetServiceLockSetPinsPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DET_ServiceLock __instance)
        {
            if (__instance == null) return;
            var s = __instance.solved;
            EnvEmit.Edge(PuzzleType.DET_ServiceLock, __instance, s != null && s.solved);
        }
    }

    // Dial buttons mutate A/B/C then SetGuide; StartShutdown is the lever (the solved edge).
    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.plusA))]
    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.plusB))]
    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.plusC))]
    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.minusA))]
    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.minusB))]
    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.minusC))]
    [HarmonyPatch(typeof(MED_Incinerator), nameof(MED_Incinerator.StartShutdown))]
    public static class MedIncineratorPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Incinerator __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.MED_Incinerator, __instance, __instance.solved);
        }
    }

    // UnlockKey sets one element (Int0 bits) and may latch unlocked.
    [HarmonyPatch(typeof(MED_MultiLock), nameof(MED_MultiLock.UnlockKey))]
    public static class MedMultiLockUnlockKeyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_MultiLock __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.MultiLock, __instance, __instance.unlocked);
        }
    }

    [HarmonyPatch(typeof(LAB_MultiLock), nameof(LAB_MultiLock.UnlockKey))]
    public static class LabMultiLockUnlockKeyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_MultiLock __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.MultiLock, __instance, __instance.unlocked);
        }
    }

    [HarmonyPatch(typeof(ROT_DialLock), nameof(ROT_DialLock.TurnUp), new System.Type[] { typeof(int) })]
    [HarmonyPatch(typeof(ROT_DialLock), nameof(ROT_DialLock.TurnDown), new System.Type[] { typeof(int) })]
    public static class RotDialLockTurnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_DialLock __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.DialLock, __instance, __instance.solved);
        }
    }

    // Flip(int) toggles states[i] then may TryMasterFlip.
    [HarmonyPatch(typeof(RES_Power), nameof(RES_Power.Flip), new System.Type[] { typeof(int) })]
    public static class ResPowerFlipPatch
    {
        [HarmonyPostfix]
        public static void Postfix(RES_Power __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.RES_Power, __instance, __instance.solved);
        }
    }

    // Native Update polls the buttons → logic(i) toggles states[i] + lights.
    [HarmonyPatch(typeof(EvidenceLockerLogicPuzzle), nameof(EvidenceLockerLogicPuzzle.logic), new System.Type[] { typeof(int) })]
    public static class EvidenceLockerLogicPatch
    {
        [HarmonyPostfix]
        public static void Postfix(EvidenceLockerLogicPuzzle __instance, int __0)
        {
            if (__instance == null || NetGate.IsApplying) return;
            // The pressed button rides with the lights so a peer replays the key push + click (KeypadLive op).
            if (NetGate.Party)
            {
                ulong id = WorldId.FromGameObject(__instance.gameObject);
                if (id != 0) KeypadLive.NotePress(id, __0, false);
            }
            EnvEmit.Edge(PuzzleType.EvidenceLockerPuzzle, __instance, __instance.solved);
        }
    }

    // TurnLeft / TurnRight mutate the plate ints then CheckSolve.
    [HarmonyPatch(typeof(RES_Shrine), nameof(RES_Shrine.TurnLeft), new System.Type[] { typeof(int) })]
    [HarmonyPatch(typeof(RES_Shrine), nameof(RES_Shrine.TurnRight), new System.Type[] { typeof(int) })]
    public static class ResShrineTurnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(RES_Shrine __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.RES_Shrine, __instance, __instance.solved);
        }
    }

    // Next / Last mutate a moon's Pos then may finish.
    [HarmonyPatch(typeof(ROT_Mural), nameof(ROT_Mural.Next), new System.Type[] { typeof(string) })]
    [HarmonyPatch(typeof(ROT_Mural), nameof(ROT_Mural.Last), new System.Type[] { typeof(string) })]
    public static class RotMuralTurnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Mural __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.ROT_Mural, __instance, __instance.finished);
        }
    }

    // MultiKeyLock.Update is deliberately NOT patched: it is the empty stub at RVA 0x2CB6B0 shared with ~3,000 unrelated
    // methods (docs/RVA_FOLDING.md), so a detour would run for all of them every call. Partial key packs (Int0) ride the 0.5 s poll.

    [HarmonyPatch(typeof(RES_MusicBox), nameof(RES_MusicBox.LoadCassette))]
    public static class MusicBoxCassettePatch
    {
        [HarmonyPostfix]
        public static void Postfix(RES_MusicBox __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.RES_MusicBox, __instance, __instance.opened);
        }
    }

    // The maze robot moves mutate robotPos: emit where a move starts.
    [HarmonyPatch(typeof(RES_LibraryPC), nameof(RES_LibraryPC.moveRight))]
    [HarmonyPatch(typeof(RES_LibraryPC), nameof(RES_LibraryPC.moveLeft))]
    [HarmonyPatch(typeof(RES_LibraryPC), nameof(RES_LibraryPC.moveUp))]
    [HarmonyPatch(typeof(RES_LibraryPC), nameof(RES_LibraryPC.moveDown))]
    public static class LibraryPcMovePatch
    {
        [HarmonyPostfix]
        public static void Postfix(RES_LibraryPC __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.RES_LibraryPC, __instance, __instance.solved);
        }
    }

    // The Move* coroutine slides robotPos cell by cell; emit where it stopped (moving true → false) so a peer's
    // glide (LibraryRobotGlide) ends on the right cell instead of the last 0.5 s poll sample.
    [HarmonyPatch(typeof(RES_LibraryPC), "Update")]
    public static class LibraryPcUpdatePatch
    {
        // Instance ids of robots seen moving (local per-frame edge state, never sent).
        static readonly HashSet<int> _wasMoving = new HashSet<int>();

        internal static void Reset() => _wasMoving.Clear();

        [HarmonyPostfix]
        public static void Postfix(RES_LibraryPC __instance)
        {
            if (__instance == null) return;
            int iid = __instance.GetInstanceID();
            if (__instance.moving) { _wasMoving.Add(iid); return; }
            if (!_wasMoving.Remove(iid)) return;
            EnvEmit.Edge(PuzzleType.RES_LibraryPC, __instance, __instance.solved);
        }
    }

    // Host Start() rolls frequency/code/hint: push that roll immediately. A client's Start() then re-applies the
    // held host roll so its local roll does not stick.
    [HarmonyPatch(typeof(DET_RadioCodeLock), "Start")]
    public static class RadioCodeStartPatch
    {
        [HarmonyPostfix]
        public static void Postfix(DET_RadioCodeLock __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (net.Role == NetworkRole.Host)
                EnvEmit.Read(PuzzleType.DET_RadioCodeLock, __instance);
            else
                net.PuzzleSync.QueueReapply();
        }
    }

    [HarmonyPatch(typeof(ROT_MeatBlocker), nameof(ROT_MeatBlocker.pickup))]
    public static class RotMeatPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_MeatBlocker __instance)
        {
            // RVA 0x4BB310 is shared with RegexParser.MoveRight (docs/RVA_FOLDING.md).
            if (!Il2CppRealType.Is<ROT_MeatBlocker>(__instance)) return;
            if (__instance == null) return;
            // The Death seal waits while NG+ KeyOfSacrifice is still obtainable (ApplyMeatBlocker holds it too).
            if (__instance.blocked && ChapterMachineSyncService.IsDeathBlocker(__instance)
                && ChapterMachineSyncService.SacrificeKeyStillAvailable())
            {
                PlaytestLog.Event("Puzzle", "hold Death MeatBlocker emit until KeyOfSacrifice");
                return;
            }
            EnvEmit.Read(PuzzleType.ROT_MeatBlocker, __instance);
        }
    }

    [HarmonyPatch(typeof(RES_Shutters), "Update")]
    public static class ResShuttersPatch
    {
        [HarmonyPostfix]
        public static void Postfix(RES_Shutters __instance)
        {
            if (__instance != null && !NetGate.IsApplying && __instance.unlocked)
                EnvEmit.ReadOnce(PuzzleType.RES_Shutters, __instance);
        }
    }

    [HarmonyPatch(typeof(BiodomeDoorLock), "Update")]
    public static class BiodomeLockPatch
    {
        // Last (KeyLevel, hasLock) seen per instance: Update never writes either (Ghidra BiodomeDoorLock.c), so
        // emit only when one changed (every KeyLevel step, not once) instead of a read + diff every frame. Local
        // per-instance state, never sent.
        static readonly Dictionary<int, int> _last = new Dictionary<int, int>();

        internal static void Reset() => _last.Clear();

        [HarmonyPostfix]
        public static void Postfix(BiodomeDoorLock __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            int level = __instance.KeyLevel;
            bool locked = __instance.hasLock;
            int sig = (level << 1) | (locked ? 1 : 0);
            int iid = __instance.GetInstanceID();
            int prev;
            if (_last.TryGetValue(iid, out prev) && prev == sig) return;
            if (_last.Count > 256) _last.Clear();
            _last[iid] = sig;
            if (level > 0 || !locked)
                EnvEmit.Edge(PuzzleType.BiodomeDoorLock, __instance, !locked);
        }
    }

    // placeItem starts delayedMulti, which turns MultiInteraction on after a yield: the gate flag is still false at
    // postfix time, so Progressed forces Bool0 for peers / late joiners now.
    [HarmonyPatch(typeof(LAB_Waage), nameof(LAB_Waage.placeItem))]
    public static class LabWaagePlacePatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Waage __instance) => EnvEmit.Progressed(PuzzleType.LAB_Waage, __instance);
    }

    // setButtonState is where states[,] actually flips (toggleButton yields first).
    [HarmonyPatch(typeof(LAB_PatternLock), nameof(LAB_PatternLock.setButtonState))]
    public static class PatternLockSetStatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_PatternLock __instance)
        {
            if (__instance != null) EnvEmit.Edge(PuzzleType.PatternLock, __instance, __instance.solved);
        }
    }

    [HarmonyPatch(typeof(ROT_Tarot), nameof(ROT_Tarot.flip))]
    [HarmonyPatch(typeof(ROT_Tarot), nameof(ROT_Tarot.PlaceCardBuyan))]
    [HarmonyPatch(typeof(ROT_Tarot), nameof(ROT_Tarot.PlaceCardVineta))]
    [HarmonyPatch(typeof(ROT_Tarot), nameof(ROT_Tarot.PlaceCardHeimat))]
    [HarmonyPatch(typeof(ROT_Tarot), nameof(ROT_Tarot.PlaceCardKitezh))]
    [HarmonyPatch(typeof(ROT_Tarot), nameof(ROT_Tarot.PlaceCardLeng))]
    [HarmonyPatch(typeof(ROT_Tarot), nameof(ROT_Tarot.PlaceCardRotfront))]
    [HarmonyPatch(typeof(ROT_Tarot), nameof(ROT_Tarot.TakeCard))]
    public static class RotTarotCardPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Tarot __instance)
        {
            // ROT_Tarot.flip (RVA 0x4BFB20) is shared with FlipSwitch.Flip (docs/RVA_FOLDING.md).
            if (!Il2CppRealType.Is<ROT_Tarot>(__instance)) return;
            EnvEmit.Read(PuzzleType.ROT_Tarot, __instance);
        }
    }

    [HarmonyPatch(typeof(LAB_PatternLock), nameof(LAB_PatternLock.toggleButton))]
    public static class PatternLockTogglePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LAB_PatternLock __instance) => __instance == null || !__instance.solved;

        [HarmonyPostfix]
        public static void Postfix(LAB_PatternLock __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            // toggleButton only starts delayed(): the light flips 0.1 s later and checkSolution runs then (Ghidra
            // LAB_PatternLock.c). Emit right after each step so a peer sees the press live, not on the next poll.
            EnvEmit.Soon(0.12f, __instance, Emit);
            EnvEmit.Soon(0.25f, __instance, Emit);
        }

        static void Emit(Component c)
        {
            var x = c as LAB_PatternLock;
            if (x != null) EnvEmit.Edge(PuzzleType.PatternLock, x, x.solved);
        }
    }

    // Cutscene UnityEvents call OpenDoors / CloseDoors (lerp DoorL / DoorR local X by ±Distance; dump.cs TypeDef 9811).
    [HarmonyPatch(typeof(MED_Adler_EVdoors), nameof(MED_Adler_EVdoors.OpenDoors))]
    public static class MedAdlerEvDoorsOpenPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Adler_EVdoors __instance) => EnvEmit.AdlerEvDoors(__instance, open: true);
    }

    [HarmonyPatch(typeof(MED_Adler_EVdoors), nameof(MED_Adler_EVdoors.CloseDoors))]
    public static class MedAdlerEvDoorsClosePatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Adler_EVdoors __instance) => EnvEmit.AdlerEvDoors(__instance, open: false);
    }
}
