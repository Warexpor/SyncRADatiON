using UnhollowerBaseLib;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Chapter machine snap/read/apply batch (card writer, shutters, magpie, reactor, rings, etc.).
    /// </summary>
    public sealed class ChapterMachineSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.MED_CardWriter:
                {
                    // Dig AB: pack nodes[i].connected → Int0; remainingSteps → Int1;
                    // Int3 = node count (valid marker so all-zero mid-state is retained).
                    // Bool0=solved, Bool1=hasCard retained. AssetStudio MED_CardWriter:
                    // 17 MED_KeyNodeConnection nodes (not KeyGrid's 17 GridSprites — same
                    // SO type; pack ≤32). maxSteps/remainingSteps initial 8.
                    var x = (MED_CardWriter)c;
                    int bits = 0, count = 0, steps = 0;
                    try { PackCardWriterNodes(x, out bits, out count); } catch { }
                    try { steps = x.remainingSteps; } catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, x.hasCard, false, bits, steps, 0, count, 0);
                    return true;
                }
                case PuzzleType.RES_Shutters:
                {
                    var x = (RES_Shutters)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.unlocked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ROT_Magpie:
                {
                    var x = (ROT_Magpie)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.opened, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.PEN_Reaktor:
                {
                    var x = (PEN_Reaktor)c;
                    // Dig AG: durable mid-state is positions[4] (0..4) + current.
                    // Pack positions[0..3] → Int0 as 4×3-bit (mirror DET_ServiceLock);
                    // Int1=current; Bool0=solved; Bool1=valid. Int2/Int3 unused.
                    // Derived Dvalue/Dtemp/total/values recomputed by native Update from
                    // positions — packing them alone cannot stick. Initial AssetStudio
                    // positions [0,4,3,1] → Int0=736≠0 so IsProgressed holds from load.
                    int pack = 0, cur = 0;
                    try { pack = PackReaktorPositions(x); } catch { }
                    try { cur = x.current; } catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, x.valid, false, pack, cur, 0, 0, 0);
                    return true;
                }
                case PuzzleType.LAB_Rings:
                {
                    var x = (LAB_Rings)c;
                    // 4×2-bit finger pack in Int0 (Zeige|Mittel|Ring|Klein); empty/regent/serpent/bride=0..3.
                    int pack = 0;
                    try { pack = PackLabRingFingers(x); } catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, pack, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ROT_MeatBlocker:
                {
                    var x = (ROT_MeatBlocker)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, !x.blocked, false, false, x.pickups, x.required, 0, 0, 0);
                    return true;
                }
                case PuzzleType.RES_Power:
                {
                    var x = (RES_Power)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, x.powered, false, PackBoolBits(x.states), 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.FlipSwitch:
                {
                    var x = (FlipSwitch)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.flipped, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.EvidenceLockerPuzzle:
                {
                    var x = (EvidenceLockerLogicPuzzle)c;
                    int lo, hi;
                    PackBoolBits64(x.states, out lo, out hi);
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, lo, hi, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MED_VentPuzzle:
                {
                    var x = (MED_VentPuzzle)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.uncovered, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ROT_Tarot:
                {
                    var x = (ROT_Tarot)c;
                    // Six tarot slots. Item enums are 0..115; empty slot is 0xFF
                    // (AirlockKey is 0, so 0 cannot mean empty). Int0 = slots 0–3,
                    // Int1 = slots 4–5, Int3 = slot count. darkmode stays Bool0.
                    int lo = 0, hi = 0, n = 0;
                    try { PackTarotCards(x, out lo, out hi, out n); } catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.darkmode, false, false, lo, hi, 0, n, x.FlipSwitchPos);
                    return true;
                }
                case PuzzleType.ROT_Mural:
                {
                    var x = (ROT_Mural)c;
                    // Pack up to 8 moons into Int0–Int3 (2 moons / int: Pos8|State4|Desired4 each).
                    int m0 = 0, m1 = 0, m2 = 0, m3 = 0;
                    try { PackMuralMoons(x.moons, out m0, out m1, out m2, out m3); } catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.finished, x.busy, false, m0, m1, m2, m3, x.MoonTurnSpeed);
                    return true;
                }
                case PuzzleType.MED_Incinerator:
                {
                    var x = (MED_Incinerator)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, x.A, x.B, x.C, 0, 0);
                    return true;
                }
                case PuzzleType.LAB_Waage:
                {
                    var x = (LAB_Waage)c;
                    // Dig S: gate-active Bool0 + weight Float0. content stays personal
                    // (independent inventories — never wire AnItem). Melon MultiInteraction
                    // (GameObject @0x100) / weight (@0xD0) / content (@0x108).
                    bool gate = false;
                    try
                    {
                        gate = x.MultiInteraction != null && x.MultiInteraction.activeSelf;
                    }
                    catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, gate, false, false, 0, 0, 0, 0, x.weight);
                    return true;
                }
                case PuzzleType.RES_Shrine:
                {
                    var x = (RES_Shrine)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, x.busy, false, x.big, x.mid, x.small, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyPower(RES_Power x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Rising-edge OnSuccess: native TryMasterFlip (RVA 0x4B5FB0) Invokes
            // OnSuccess @ ~0x4B60D4 when TopVolt==0x320 && BotVolt==0xE6, then
            // latches solved=1 @ +0x91. Asset OnSuccess → SetSpeed (Paternoster)
            // + setPower×3 + SetActive + dimPOI + Play. Prior Apply flags-only
            // → peer residency power softlock / paternoster not driven. Melon
            // field OnSuccess (PascalCase). No onLoad — Dig J #2 skipped
            // FullRefresh; late-join / remount then left setPower targets and
            // Paternoster SetSpeed un-driven (RES_Paternoster Bool0 alone is
            // not the same wire as OnSuccess's SetSpeed(2) + three setPower).
            // Both-path like PEN_Reaktor Dig Q: idempotent SetActive/setPower/
            // dimPOI / SetSpeed final-pose.
            bool was = x.solved;
            x.solved = e.Bool0;
            x.powered = e.Bool1;
            UnpackBoolBits(x.states, e.Int0);
            if (!e.Bool0) return;
            if (!was)
            {
                NetGate.BeginApply();
                try
                {
                    if (x.OnSuccess != null)
                        x.OnSuccess.Invoke();
                }
                catch { }
                finally { NetGate.EndApply(); }
            }
        }

        public static void ApplyVent(MED_VentPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.uncovered = e.Bool0;
            if (!e.Bool0) return;
            try { if (x.cover != null) x.cover.SetActive(false); } catch { }
            try { if (x.smallCover != null) x.smallCover.SetActive(false); } catch { }
            try { if (x.smallCoverDiscarded != null) x.smallCoverDiscarded.SetActive(true); } catch { }
            try
            {
                if (x.itemInter != null)
                {
                    try
                    {
                        if (x.itemInter.inter != null)
                            PuzzleSyncService.DisableOne(x.itemInter.inter);
                    }
                    catch { }
                    try { x.itemInter.enabled = false; } catch { }
                    try { x.itemInter.gameObject.SetActive(false); } catch { }
                }
            }
            catch { }
            try { PuzzleSyncService.DisableInteractions(x); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyEvidenceLocker(EvidenceLockerLogicPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0;
            UnpackBoolBits64(x.states, e.Int0, e.Int1);
            // Dig AD: snap lights to unpacked states so remount / late-join mid-hold
            // shows button lights without waiting for native Update (!solved path).
            // Native Update follows states→lights when !solved; remote Apply skips Update.
            NetGate.BeginApply();
            try
            {
                var lights = x.lights;
                var states = x.states;
                if (lights != null && states != null)
                {
                    int n = lights.Length;
                    if (states.Length < n) n = states.Length;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            if (lights[i] != null)
                                lights[i].SetActive(states[i]);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally { NetGate.EndApply(); }
            if (!e.Bool0) return;
            SnapEvidenceLockerDoors(x.gameObject);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        static void SnapEvidenceLockerDoors(GameObject root)
        {
            if (root == null) return;
            try
            {
                var doors = root.GetComponentsInChildren<EvidenceLockerDoor>(true);
                if (doors == null) return;
                for (int i = 0; i < doors.Length; i++)
                {
                    var d = doors[i];
                    if (d == null) continue;
                    try { d.done = true; } catch { }
                    try
                    {
                        if (d.Door != null)
                        {
                            d.Door.SetActive(true);
                            PuzzleSyncService.UnlockDoorObject(d.Door);
                        }
                    }
                    catch { }
                    try
                    {
                        if (d.inter != null)
                            PuzzleSyncService.DisableOne(d.inter);
                    }
                    catch { }
                    try { d.LoadState(); } catch { }
                }
            }
            catch { }
            // Sibling doors under the same room chunk.
            try
            {
                var parent = root.transform.parent;
                if (parent == null) return;
                var siblings = parent.GetComponentsInChildren<EvidenceLockerDoor>(true);
                if (siblings == null) return;
                for (int i = 0; i < siblings.Length; i++)
                {
                    var d = siblings[i];
                    if (d == null) continue;
                    try { d.done = true; } catch { }
                    try
                    {
                        if (d.Door != null)
                            PuzzleSyncService.UnlockDoorObject(d.Door);
                    }
                    catch { }
                    try
                    {
                        if (d.inter != null)
                            PuzzleSyncService.DisableOne(d.inter);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static void ApplyTarot(ROT_Tarot x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.darkmode = e.Bool0;
            x.FlipSwitchPos = e.Float0;
            if (e.Int3 <= 0) return;
            ApplyTarotCards(x, e.Int0, e.Int1, e.Int3);
        }

        const int TarotEmpty = 0xFF;

        static void PackTarotCards(ROT_Tarot x, out int lo, out int hi, out int n)
        {
            lo = 0;
            hi = 0;
            n = 0;
            if (x == null) return;
            var cards = x.cards;
            if (cards == null) return;
            n = cards.Length;
            if (n > 6) n = 6;
            for (int i = 0; i < n; i++)
            {
                int code = TarotEmpty;
                try
                {
                    var card = cards[i];
                    if (card != null)
                    {
                        int id = (int)card._item;
                        if (id >= 0 && id < TarotEmpty) code = id;
                    }
                }
                catch { }
                if (i < 4) lo |= code << (i * 8);
                else hi |= code << ((i - 4) * 8);
            }
        }

        static void ApplyTarotCards(ROT_Tarot x, int lo, int hi, int n)
        {
            if (x == null || n <= 0) return;
            if (n > 6) n = 6;
            Il2CppReferenceArray<AnItem> cards = null;
            Il2CppReferenceArray<GameObject> placers = null;
            try { cards = x.cards; } catch { }
            try { placers = x.Placers; } catch { }
            for (int i = 0; i < n; i++)
            {
                int code = i < 4 ? (lo >> (i * 8)) & 0xFF : (hi >> ((i - 4) * 8)) & 0xFF;
                AnItem item = null;
                if (code != TarotEmpty)
                {
                    try { item = InventoryManager.getItem((Items.itemlist)code); } catch { }
                }
                try
                {
                    if (cards != null && i < cards.Length)
                        cards[i] = item;
                }
                catch { }
                try
                {
                    if (placers != null && i < placers.Length && placers[i] != null)
                        placers[i].SetActive(item != null);
                }
                catch { }
            }
            // Moon readout follows cards[]. Load/place path; no inventory remove.
            try { x.SetMoons(); } catch { }
        }

        public static void ApplyCardWriter(MED_CardWriter x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Dig AB: Bool0 solved / Bool1 hasCard; Int0 connected pack; Int1 remainingSteps;
            // Int3 count marker gates node+steps apply so all-zero packs still refresh.
            // Snap card pose without UI/cinematic until solved (existing SnapCardWriter).
            if (e.Int3 > 0)
            {
                ApplyCardWriterNodes(x, e.Int0, e.Int3);
                try { x.remainingSteps = e.Int1; } catch { }
                try
                {
                    if (x.remainingText != null)
                        x.remainingText.text = e.Int1.ToString();
                }
                catch { }
            }
            SnapCardWriter(x, e.Bool0, e.Bool1);
            // Mid-insert pose: card present, prompts off — no pickup reveal / tinyCard
            // (those stay solved-only inside SnapCardWriter).
            if (!e.Bool0 && e.Bool1)
            {
                try { if (x.Card != null) x.Card.SetActive(true); } catch { }
                try { if (x.insertCardPrompt != null) x.insertCardPrompt.SetActive(false); } catch { }
                try { if (x.insertCard != null) x.insertCard.SetActive(false); } catch { }
            }
        }

        /// <summary>Pack ≤32 MED_KeyNodeConnection.connected bits; count = valid marker.</summary>
        static void PackCardWriterNodes(MED_CardWriter x, out int bits, out int count)
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
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>Unpack Int0 into nodes[i].connected under NetGate.</summary>
        static void ApplyCardWriterNodes(MED_CardWriter x, int bits, int count)
        {
            if (x == null) return;
            NetGate.BeginApply();
            try
            {
                try
                {
                    var nodes = x.nodes;
                    if (nodes == null) return;
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
                        catch { }
                    }
                }
                catch { }
            }
            finally { NetGate.EndApply(); }
        }

        public static void ApplyShutters(RES_Shutters x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Dig AC: Melon unlocked / poi / _lock / Handle / Shutter.
            // Native Update (~0x4B73C0) dims poi once when _lock.locked becomes false
            // while unlocked still false, then latches unlocked. Prior SnapShutters set
            // unlocked + shutter/handle + ConnectedDoors — never called poi.dimPOI.
            // Remote Apply sets unlocked=true first → native edge skipped forever.
            // Call dimPOI idempotently under NetGate on BOTH live MutateWorld AND
            // FullRefresh (MusicBox / Shrine dimPOI ships; no separate onLoad).
            // Protocol 10 unchanged (reuse RES_Shutters Bool0 unlocked).
            bool was = false;
            try { was = x.unlocked; } catch { }
            if (!e.Bool0) return;
            SnapShutters(x);
            if (!was)
            {
                NetGate.BeginApply();
                try
                {
                    try
                    {
                        if (x.poi != null)
                            x.poi.dimPOI();
                    }
                    catch { }
                }
                catch { }
                finally { NetGate.EndApply(); }
            }
        }

        public static void ApplyMagpie(ROT_Magpie x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapMagpie(x);
        }

        public static void ApplyReaktor(PEN_Reaktor x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Rising-edge onSuccess (Dig Q): Melon solved / doorLock / onSuccess / _event.
            // Native Update sets solved then starts win; win.MoveNext Invokes onSuccess
            // then unlocks. Prior ApplyReaktor applied mid fields + SnapReaktor (solved,
            // unlock doorLock/plates, disable _event) — never Invoked onSuccess. Asset
            // onSuccess → Popup.SetActive(true); E Door Spot Blocked.SetActive(false);
            // E Door Spot.SetActive(true). Peers/late-join: red blocked marker stays,
            // green success marker/popup missing (door unlock already covered by Snap).
            // Mirror RES_Shrine / DoorLockEvent rising-edge: Invoke on BOTH live
            // MutateWorld AND FullRefresh (idempotent SetActive final-pose; no separate
            // onLoad). Protocol 10 unchanged (reuse PEN_Reaktor Bool0 solved).
            // Dig AG: wire Int0 = positions[0..3] 4×3-bit pack; Int1 = current.
            // Derived Dvalue/Dtemp/total are NOT written — native Update recomputes
            // them from positions every frame. Prefer letting Update lerp Rods.
            bool was = false;
            try { was = x.solved; } catch { }
            try { x.valid = e.Bool1; } catch { }
            int pack = SanitizeReaktorPack(e.Int0);
            ApplyReaktorPositions(x, pack);
            try { x.current = e.Int1; } catch { }
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
                    catch { }
                    SnapReaktor(x);
                }
                catch { }
                finally { NetGate.EndApply(); }
            }
        }

        /// <summary>Pos0 bits0-2, Pos1 3-5, Pos2 6-8, Pos3 9-11 (values 0..4).</summary>
        static int PackReaktorPositions(PEN_Reaktor x)
        {
            int pack = 0;
            try
            {
                var positions = x.positions;
                if (positions == null) return 0;
                int n = positions.Length;
                if (n > 4) n = 4;
                for (int i = 0; i < n; i++)
                {
                    int v = positions[i];
                    if (v < 0) v = 0;
                    if (v > 4) v = 4;
                    pack |= (v & 7) << (i * 3);
                }
            }
            catch { }
            return pack;
        }

        static int SanitizeReaktorPack(int packed)
        {
            // 3-bit mask per rod — clamp to 0..4 (native Update Clamp).
            int p0 = packed & 7;
            int p1 = (packed >> 3) & 7;
            int p2 = (packed >> 6) & 7;
            int p3 = (packed >> 9) & 7;
            if (p0 > 4) p0 = 4;
            if (p1 > 4) p1 = 4;
            if (p2 > 4) p2 = 4;
            if (p3 > 4) p3 = 4;
            return p0 | (p1 << 3) | (p2 << 6) | (p3 << 9);
        }

        static void ApplyReaktorPositions(PEN_Reaktor x, int pack)
        {
            if (x == null) return;
            try
            {
                var positions = x.positions;
                if (positions == null) return;
                int n = positions.Length;
                if (n > 4) n = 4;
                for (int i = 0; i < n; i++)
                {
                    int v = (pack >> (i * 3)) & 7;
                    if (v > 4) v = 4;
                    int cur = 0;
                    try { cur = positions[i]; } catch { }
                    if (cur == v && PuzzleSyncService.MutateWorld) continue;
                    try { positions[i] = v; } catch { }
                }
                // Prefer native Update lerp of Rods from positions (Dig AG) —
                // no Rods pose snap under NetGate.
            }
            catch { }
        }

        public static void ApplyLabRings(LAB_Rings x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Protocol 10: Int0 = 4×2-bit finger states; Bool0 = solved (plate snap).
            // Sanitize each nibble to 0..3 (empty/regent/serpent/bride). Host also
            // treats pack==S_* as solved so a partial emit that completes the
            // solution still snaps even if Bool0 raced ahead of checkSolution.
            int pack = SanitizeLabRingPack(e.Int0);
            ApplyLabRingFingers(x, pack);
            bool solved = e.Bool0;
            if (!solved)
            {
                try { solved = LabRingPackMatchesSolution(x, pack); } catch { }
            }
            if (solved)
                SnapLabRings(x);
        }

        /// <summary>Zeige bits0-1, Mittel 2-3, Ring 4-5, Klein 6-7.</summary>
        static int PackLabRingFingers(LAB_Rings x)
        {
            int z = 0, m = 0, r = 0, k = 0;
            try { if (x.Zeige != null) z = (int)x.Zeige.state & 3; } catch { }
            try { if (x.Mittel != null) m = (int)x.Mittel.state & 3; } catch { }
            try { if (x.Ring != null) r = (int)x.Ring.state & 3; } catch { }
            try { if (x.Klein != null) k = (int)x.Klein.state & 3; } catch { }
            return z | (m << 2) | (r << 4) | (k << 6);
        }

        static int SanitizeLabRingPack(int packed)
        {
            // 2-bit mask per finger — illegal high bits dropped; values always 0..3.
            int z = packed & 3;
            int m = (packed >> 2) & 3;
            int r = (packed >> 4) & 3;
            int k = (packed >> 6) & 3;
            return z | (m << 2) | (r << 4) | (k << 6);
        }

        static bool LabRingPackMatchesSolution(LAB_Rings x, int pack)
        {
            int z = pack & 3, m = (pack >> 2) & 3, r = (pack >> 4) & 3, k = (pack >> 6) & 3;
            return z == ((int)x.S_Zeige & 3)
                && m == ((int)x.S_Mittel & 3)
                && r == ((int)x.S_Ring & 3)
                && k == ((int)x.S_Klein & 3);
        }

        static void ApplyLabRingFingers(LAB_Rings x, int pack)
        {
            ApplyOneLabFinger(x, x.Zeige, pack & 3);
            ApplyOneLabFinger(x, x.Mittel, (pack >> 2) & 3);
            ApplyOneLabFinger(x, x.Ring, (pack >> 4) & 3);
            ApplyOneLabFinger(x, x.Klein, (pack >> 6) & 3);
        }

        static void ApplyOneLabFinger(LAB_Rings mgr, LAB_Rings_Finger finger, int state)
        {
            if (finger == null) return;
            var desired = (LAB_Rings_Finger.rings)(state & 3);
            // Rising-edge / idempotent: skip setStates when already matching (live
            // rebroadcast). FullRefresh (!MutateWorld) still forces visuals so a
            // remount / late-join OnEnable empty pose is corrected even if state
            // coincidentally matches after LoadState.
            bool same = false;
            try { same = finger.state == desired; } catch { }
            if (same && PuzzleSyncService.MutateWorld) return;
            try { finger.state = desired; } catch { }
            // Native LoadState → loadFinger(string) → setStates; place/take also
            // end in setStates for Pickup*/MultiInter visuals. Prefer setStates
            // (no inventory side-effects) over placeRing/takeRing.
            try { if (mgr != null) mgr.setStates(finger); } catch { }
        }

        public static void ApplyMeatBlocker(ROT_MeatBlocker x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // MeatBlocker ID "Death" seals the bookstore wing that holds NG+ KeyOfSacrifice.
            // Hold party seal apply while that key is still obtainable (picker is gated too).
            if (!e.Bool0 && IsDeathMeatBlocker(x) && SacrificeKeyStillAvailable())
            {
                try { x.pickups = e.Int0; } catch { }
                PlaytestLog.Event("Puzzle", "hold Death MeatBlocker seal until KeyOfSacrifice");
                return;
            }
            SnapMeatBlocker(x, e.Bool0, e.Int0);
        }

        static bool IsDeathMeatBlocker(ROT_MeatBlocker x)
        {
            if (x == null) return false;
            try
            {
                var id = x.ID;
                return id != null && string.Equals(id, "Death", System.StringComparison.Ordinal);
            }
            catch { return false; }
        }

        static bool SacrificeKeyStillAvailable()
        {
            try
            {
                var net = LanNetworkManager.Instance;
                if (net == null || !net.IsConnected) return false;
                return net.PickupSync.KeyOfSacrificeAvailableUnclaimed();
            }
            catch { return false; }
        }

        public static void SnapCardWriter(MED_CardWriter x, bool solved, bool hasCard)
        {
            if (x == null) return;
            try { x.solved = solved; } catch { }
            try { x.hasCard = hasCard || solved; } catch { }
            if (!solved) return;
            try { if (x.insertCard != null) x.insertCard.SetActive(false); } catch { }
            try { if (x.insertCardPrompt != null) x.insertCardPrompt.SetActive(false); } catch { }
            try { if (x.pickUpBlank != null) x.pickUpBlank.SetActive(true); } catch { }
            try { if (x.tinyCard != null) x.tinyCard.SetActive(true); } catch { }
            try { PuzzleSyncService.RevealPickups(x.pickUpBlank); } catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
        }

        public static void SnapShutters(RES_Shutters x)
        {
            if (x == null) return;
            try { x.unlocked = true; } catch { }
            try { if (x.Shutter != null) x.Shutter.SetActive(false); } catch { }
            try { if (x.Handle != null) x.Handle.SetActive(false); } catch { }
            try
            {
                if (x._lock != null)
                    DoorNative.ApplyConnectedDoors(x._lock, false);
            }
            catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapMagpie(ROT_Magpie x)
        {
            if (x == null) return;
            try { x.opened = true; } catch { }
            try { if (x.CardPickup != null) x.CardPickup.SetActive(true); } catch { }
            try { if (x.BoxObs != null) x.BoxObs.SetActive(false); } catch { }
            try { PuzzleSyncService.RevealPickups(x.CardPickup); } catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
        }

        public static void SnapReaktor(PEN_Reaktor x)
        {
            if (x == null) return;
            try { x.solved = true; } catch { }
            try
            {
                if (x.doorLock != null)
                    DoorNative.ApplyConnectedDoors(x.doorLock, false);
            }
            catch { }
            try
            {
                var singles = x.GetComponentsInChildren<InteractiveLockSingle>(true);
                if (singles != null)
                {
                    for (int i = 0; i < singles.Length; i++)
                    {
                        if (singles[i] == null) continue;
                        try { if (singles[i].door != null) singles[i].door.locked = false; } catch { }
                        DoorNative.ApplyLockPlate(singles[i], false);
                    }
                }
            }
            catch { }
            try
            {
                if (x._event != null)
                {
                    PuzzleSyncService.DisableInteractions(x._event);
                    try { x._event.enabled = false; } catch { }
                }
            }
            catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapLabRings(LAB_Rings x)
        {
            if (x == null) return;
            try { x.solved = true; } catch { }
            try { if (x.solvedState != null) x.solvedState.SetActive(true); } catch { }
            try { if (x.FakePlate != null) x.FakePlate.SetActive(false); } catch { }
            try { if (x.PlatePickup != null) x.PlatePickup.SetActive(true); } catch { }
            try { PuzzleSyncService.RevealPickups(x.PlatePickup); } catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapMeatBlocker(ROT_MeatBlocker x, bool unblocked, int pickups)
        {
            if (x == null) return;
            try { x.pickups = pickups; } catch { }
            try { x.blocked = !unblocked; } catch { }
            try
            {
                var blockers = x.Blockers;
                if (blockers != null)
                {
                    for (int i = 0; i < blockers.Length; i++)
                    {
                        try
                        {
                            if (blockers[i] != null)
                                blockers[i].SetActive(!unblocked);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            try
            {
                var open = x.UnBlockers;
                if (open != null)
                {
                    for (int i = 0; i < open.Length; i++)
                    {
                        try
                        {
                            if (open[i] != null)
                                open[i].SetActive(unblocked);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            try
            {
                var locks = x.Locks;
                if (locks != null)
                {
                    for (int i = 0; i < locks.Length; i++)
                    {
                        try
                        {
                            if (locks[i] != null)
                                DoorNative.ApplyConnectedDoors(locks[i], locked: !unblocked);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void ApplyFlipSwitch(FlipSwitch x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            if (x.flipped != e.Bool0)
            {
                if (mutateWorld)
                {
                    try { x.Flip(); }
                    catch { x.flipped = e.Bool0; }
                }
                else
                    x.flipped = e.Bool0;
            }
            if (e.Bool0) PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyMural(ROT_Mural x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Rising-edge onSolved: native Update only Invokes when finished was false,
            // then latches finished=1. Asset onSolved → goBack / SetActive(false) Blocker
            // Entry / setUnleavable / StartCutscene / FMOD. useRing only toggles
            // RingInter/MissingRing — does NOT fire onSolved. Live MutateWorld:
            // BeginApply + onSolved (CutsceneStartPatch allows StartCutscene while
            // IsApplying) + useRing. FullRefresh / remount: do NOT Invoke onSolved
            // (StartCutscene / goBack / EventScreen-adjacent presentation replay).
            // Instead snap Blocker Entry SetActive(false) + useRing so the door
            // blocker clears without replaying the mural cutscene (Dig AJ).
            bool was = x.finished;
            x.finished = e.Bool0; x.busy = e.Bool1;
            try { x.MoonTurnSpeed = e.Float0; } catch { }
            try { UnpackMuralMoons(x.moons, e.Int0, e.Int1, e.Int2, e.Int3); } catch { }
            if (!e.Bool0) return;
            if (PuzzleSyncService.MutateWorld && !was)
            {
                NetGate.BeginApply();
                try
                {
                    if (x.onSolved != null)
                        x.onSolved.Invoke();
                }
                catch { }
                finally { NetGate.EndApply(); }
            }
            else if (!was)
            {
                SnapMuralBlockerEntry(x);
            }
            try { x.useRing(); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>
        /// ROT_Rotfront has exactly one GameObject named "Blocker Entry" wired from
        /// ROT_Mural.onSolved SetActive(false). FullRefresh cannot Invoke onSolved
        /// (StartCutscene), so clear that blocker by name under the mural root.
        /// </summary>
        static void SnapMuralBlockerEntry(ROT_Mural x)
        {
            if (x == null) return;
            Transform root = null;
            try { root = x.transform; } catch { }
            if (root == null) return;
            try
            {
                var parent = root.parent;
                if (parent != null) root = parent;
            }
            catch { }
            try
            {
                var trs = root.GetComponentsInChildren<Transform>(true);
                if (trs == null) return;
                for (int i = 0; i < trs.Length; i++)
                {
                    var t = trs[i];
                    if (t == null) continue;
                    string n = null;
                    try { n = t.name; } catch { }
                    if (n == null) continue;
                    if (!string.Equals(n, "Blocker Entry", System.StringComparison.Ordinal))
                        continue;
                    try { t.gameObject.SetActive(false); } catch { }
                    return;
                }
            }
            catch { }
        }

        static int PackMoonPair(ROT_Mural.Moon a, ROT_Mural.Moon b)
        {
            return PackOneMoon(a) | (PackOneMoon(b) << 16);
        }

        static int PackOneMoon(ROT_Mural.Moon m)
        {
            if (m == null) return 0;
            int pos = 0, state = 0, desired = 0;
            try { pos = m.Pos & 0xFF; } catch { }
            try { state = m.State & 0xF; } catch { }
            try { desired = m.DesiredPos & 0xF; } catch { }
            return pos | (state << 8) | (desired << 12);
        }

        static void UnpackOneMoon(ROT_Mural.Moon m, int packed)
        {
            if (m == null) return;
            try { m.Pos = packed & 0xFF; } catch { }
            try { m.State = (packed >> 8) & 0xF; } catch { }
            try { m.DesiredPos = (packed >> 12) & 0xF; } catch { }
        }

        static void PackMuralMoons(UnhollowerBaseLib.Il2CppReferenceArray<ROT_Mural.Moon> moons,
            out int i0, out int i1, out int i2, out int i3)
        {
            i0 = i1 = i2 = i3 = 0;
            if (moons == null) return;
            ROT_Mural.Moon At(int i)
            {
                try { return i < moons.Length ? moons[i] : null; }
                catch { return null; }
            }
            i0 = PackMoonPair(At(0), At(1));
            i1 = PackMoonPair(At(2), At(3));
            i2 = PackMoonPair(At(4), At(5));
            i3 = PackMoonPair(At(6), At(7));
        }

        static void UnpackMuralMoons(UnhollowerBaseLib.Il2CppReferenceArray<ROT_Mural.Moon> moons,
            int i0, int i1, int i2, int i3)
        {
            if (moons == null) return;
            void Slot(int i, int packed)
            {
                try
                {
                    if (i < moons.Length && moons[i] != null)
                        UnpackOneMoon(moons[i], packed);
                }
                catch { }
            }
            Slot(0, i0 & 0xFFFF); Slot(1, (i0 >> 16) & 0xFFFF);
            Slot(2, i1 & 0xFFFF); Slot(3, (i1 >> 16) & 0xFFFF);
            Slot(4, i2 & 0xFFFF); Slot(5, (i2 >> 16) & 0xFFFF);
            Slot(6, i3 & 0xFFFF); Slot(7, (i3 >> 16) & 0xFFFF);
        }

        static int PackBoolBits(UnhollowerBaseLib.Il2CppStructArray<bool> arr)
        {
            return ResidencyPuzzleSyncService.PackBoolArray(arr);
        }

        static void UnpackBoolBits(UnhollowerBaseLib.Il2CppStructArray<bool> arr, int bits)
        {
            ResidencyPuzzleSyncService.UnpackBoolArray(arr, bits);
        }

        static void PackBoolBits64(UnhollowerBaseLib.Il2CppStructArray<bool> arr, out int lo, out int hi)
        {
            lo = 0;
            hi = 0;
            if (arr == null) return;
            try
            {
                int n = arr.Length;
                for (int i = 0; i < n && i < 32; i++)
                {
                    try { if (arr[i]) lo |= 1 << i; } catch { }
                }
                for (int i = 32; i < n && i < 64; i++)
                {
                    try { if (arr[i]) hi |= 1 << (i - 32); } catch { }
                }
            }
            catch { }
        }

        static void UnpackBoolBits64(UnhollowerBaseLib.Il2CppStructArray<bool> arr, int lo, int hi)
        {
            if (arr == null) return;
            try
            {
                int n = arr.Length;
                for (int i = 0; i < n && i < 32; i++)
                {
                    try { arr[i] = (lo & (1 << i)) != 0; } catch { }
                }
                for (int i = 32; i < n && i < 64; i++)
                {
                    try { arr[i] = (hi & (1 << (i - 32))) != 0; } catch { }
                }
            }
            catch { }
        }

        public static void ApplyIncinerator(MED_Incinerator x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0; x.A = e.Int0; x.B = e.Int1; x.C = e.Int2;
            if (!e.Bool0) return;
            try { x.StartShutdown(); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyWaage(LAB_Waage x, PuzzleStateEntry e)
        {
            // Dig S: weight pose + MultiInteraction gate. Native placeItem → delayedMulti
            // → MultiInteraction.SetActive(true). Scene starts MultiInteraction inactive.
            // Prior Apply weight-only → peers/late-join get scale pose but gameplay gate
            // stays inactive (softlock). content (AnItem @0x108) stays unsynced —
            // independent inventories (AGENTS). Protocol 10 reuse Bool0 gate + Float0 weight.
            if (x == null) return;
            x.weight = e.Float0;
            if (!e.Bool0) return;
            try
            {
                var gate = x.MultiInteraction;
                if (gate != null && !gate.activeSelf)
                    gate.SetActive(true);
            }
            catch { }
        }

        public static void ApplyShrine(RES_Shrine x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Rising-edge final-pose (Dig P): Melon solved / busy / big / mid / small /
            // onSuccess / LeftDoor / RightDoor / OpenAngle / doorPos / content.
            // Native CheckSolve (~0x4B6AF0) early-outs when solved already true —
            // never starts delayedReactionToSolve.MoveNext (~0x6EEAA0): latch solved,
            // content.SetActive(true), MoveTowards doorPos→1 (Update applies
            // LeftDoor Z=-OpenAngle*doorPos / RightDoor Z=+OpenAngle*doorPos),
            // onSuccess.Invoke, StartCoroutine(release) (busy clear). Prior Apply
            // latched solved then CheckSolve → peers miss doors/content/dimPOI;
            // sticky solved unrecovered. Asset onSuccess → MinimapPOIObject.dimPOI ×2
            // (safe both-path like DoorLockEvent/MultiCondition). LoadState when
            // solved also snaps doorPos=1 + content (no onSuccess). Mirror
            // RadioStationTutorial / Magpie final-pose + DoorLockEvent rising-edge
            // Invoke. Protocol 10 unchanged (reuse RES_Shrine Bool0/Bool1/Int0..2).
            bool was = false;
            try { was = x.solved; } catch { }
            x.solved = e.Bool0; x.busy = e.Bool1;
            x.big = e.Int0; x.mid = e.Int1; x.small = e.Int2;
            // Dig AE: mid plate visual snap when !Bool0 so remount / late-join show
            // unsolved big/mid/small without waiting for Update setWheel lerp
            // (pos*60°; Melon setWheel Private→Public). Bool0 final-pose path unchanged.
            if (!e.Bool0)
            {
                NetGate.BeginApply();
                try { SnapShrineMidWheels(x, e.Int0, e.Int1, e.Int2); }
                catch { }
                finally { NetGate.EndApply(); }
                return;
            }
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
                    catch { }
                    SnapShrineFinalPose(x);
                }
                catch { }
                finally { NetGate.EndApply(); }
            }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>
        /// Dig AE mid-hold visual: force bigX/midX/smallX = pos*60 and setWheel so
        /// remount shows plate pose immediately (Update also lerps the same formula).
        /// </summary>
        static void SnapShrineMidWheels(RES_Shrine x, int big, int mid, int small)
        {
            if (x == null) return;
            const float step = 60f;
            try
            {
                float t = big * step;
                x.bigX = t;
                if (x.outside != null)
                    x.setWheel(x.outside, big, t);
            }
            catch { }
            try
            {
                float t = mid * step;
                x.midX = t;
                if (x.middle != null)
                    x.setWheel(x.middle, mid, t);
            }
            catch { }
            try
            {
                float t = small * step;
                x.smallX = t;
                if (x.inside != null)
                    x.setWheel(x.inside, small, t);
            }
            catch { }
        }

        /// <summary>
        /// delayedReaction / LoadState final pose: doorPos=1, LeftDoor Z=-OpenAngle,
        /// RightDoor Z=+OpenAngle, content active. Does not start release coroutine.
        /// </summary>
        static void SnapShrineFinalPose(RES_Shrine x)
        {
            if (x == null) return;
            try { x.doorPos = 1f; } catch { }
            float open = 0f;
            try { open = x.OpenAngle; } catch { }
            try
            {
                if (x.LeftDoor != null)
                {
                    var e = x.LeftDoor.localEulerAngles;
                    e.x = 0f;
                    e.y = 0f;
                    e.z = -open;
                    x.LeftDoor.localEulerAngles = e;
                }
            }
            catch { }
            try
            {
                if (x.RightDoor != null)
                {
                    var e = x.RightDoor.localEulerAngles;
                    e.x = 0f;
                    e.y = 0f;
                    e.z = open;
                    x.RightDoor.localEulerAngles = e;
                }
            }
            catch { }
            try
            {
                if (x.content != null)
                    x.content.SetActive(true);
            }
            catch { }
        }

        // Radio peel — façade for any leftover callers.
        public static void ApplyRadioAlignment(ROT_RadioAlignment x, PuzzleStateEntry e)
            => RadioPuzzleSyncService.ApplyAlignment(x, e);

        public static void ApplyRadioCode(DET_RadioCodeLock x, PuzzleStateEntry e)
            => RadioPuzzleSyncService.ApplyCode(x, e);
    }
}
