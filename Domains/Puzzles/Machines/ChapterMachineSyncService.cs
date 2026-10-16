using System;
using SyncRADation.Sync;
using UnhollowerBaseLib;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>Chapter machines: card writer, shutters, magpie, reactor, rings, meat blocker, power, flip switch,
    /// evidence locker, vent, tarot, mural, incinerator, scale, shrine.</summary>
    public sealed class ChapterMachineSyncService
    {
        // ---- reads ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Int0 = nodes[i].connected (≤32, scene: 17 MED_KeyNodeConnection), Int1 = remainingSteps, Int2 = cursor node
        /// (x -2..1, y -1..1, +8 each; bit 16 = valid) so a watching peer sees where it is drawn, Int3 = node count
        /// (an all-off trace still holds). Bool0 = solved, Bool1 = hasCard.
        /// </summary>
        internal static PuzzleStateEntry ReadCardWriter(MED_CardWriter x, long wid)
        {
            int bits, count;
            PackNodes(x.nodes, out bits, out count);
            var n = x.node;
            int cursor = ((Mathf.RoundToInt(n.x) + 8) & 0xFF) | (((Mathf.RoundToInt(n.y) + 8) & 0xFF) << 8) | (1 << 16);
            return Mk(PuzzleType.MED_CardWriter, wid, x.solved, x.hasCard, false, bits, x.remainingSteps, cursor, count, 0);
        }

        internal static PuzzleStateEntry ReadShutters(RES_Shutters x, long wid)
            => Mk(PuzzleType.RES_Shutters, wid, x.unlocked, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadMagpie(ROT_Magpie x, long wid)
            => Mk(PuzzleType.ROT_Magpie, wid, x.opened, false, false, 0, 0, 0, 0, 0);

        /// <summary>
        /// Int0 = positions[0..3] as 4×3-bit (0..4). The derived Dvalue / Dtemp / total are recomputed by native Update
        /// from positions every frame, so only positions sync. Int1 stays 0: the rod-selection cursor (current) is per
        /// player, and syncing it made two players overwrite each other's cursor. Bool1 = valid.
        /// </summary>
        internal static PuzzleStateEntry ReadReaktor(PEN_Reaktor x, long wid)
        {
            int pack = 0;
            var positions = x.positions;
            if (positions != null)
                for (int i = 0; i < positions.Length && i < 4; i++)
                    pack |= (Mathf.Clamp(positions[i], 0, 4) & 7) << (i * 3);
            return Mk(PuzzleType.PEN_Reaktor, wid, x.solved, x.valid, false, pack, 0, 0, 0, 0);
        }

        /// <summary>4×2-bit finger pack (Zeige | Mittel | Ring | Klein; empty / regent / serpent / bride = 0..3).</summary>
        internal static PuzzleStateEntry ReadLabRings(LAB_Rings x, long wid)
        {
            int pack = FingerState(x.Zeige) | (FingerState(x.Mittel) << 2) | (FingerState(x.Ring) << 4) | (FingerState(x.Klein) << 6);
            return Mk(PuzzleType.LAB_Rings, wid, x.solved, false, false, pack, 0, 0, 0, 0);
        }

        static int FingerState(LAB_Rings_Finger f) => f != null ? (int)f.state & 3 : 0;

        internal static PuzzleStateEntry ReadMeatBlocker(ROT_MeatBlocker x, long wid)
            => Mk(PuzzleType.ROT_MeatBlocker, wid, !x.blocked, false, false, x.pickups, x.required, 0, 0, 0);

        internal static PuzzleStateEntry ReadPower(RES_Power x, long wid)
            => Mk(PuzzleType.RES_Power, wid, x.solved, x.powered, false, ResidencyPuzzleSyncService.PackBoolArray(x.states), 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadFlipSwitch(FlipSwitch x, long wid)
            => Mk(PuzzleType.FlipSwitch, wid, x.flipped, false, false, 0, 0, 0, 0, 0);

        /// <summary>Int0/Int1 = states[0..63]; Int2 = last pressed button (KeypadLive op): its push + click replay on peers.</summary>
        internal static PuzzleStateEntry ReadEvidenceLocker(EvidenceLockerLogicPuzzle x, long wid)
        {
            int lo, hi;
            PackBoolBits64(x.states, out lo, out hi);
            return Mk(PuzzleType.EvidenceLockerPuzzle, wid, x.solved, false, false, lo, hi, KeypadLive.PackOp(unchecked((ulong)wid)), 0, 0);
        }

        internal static PuzzleStateEntry ReadVent(MED_VentPuzzle x, long wid)
            => Mk(PuzzleType.MED_VentPuzzle, wid, x.uncovered, false, false, 0, 0, 0, 0, 0);

        /// <summary>
        /// Six slots, one byte each (Int0 = slots 0..3, Int1 = 4..5). Item enums are 0..115 and AirlockKey is 0, so an
        /// empty slot is 0xFF. Int3 = slot count, Bool0 = darkmode, Float0 = FlipSwitchPos.
        /// </summary>
        internal static PuzzleStateEntry ReadTarot(ROT_Tarot x, long wid)
        {
            int lo = 0, hi = 0, n = 0;
            var cards = x.cards;
            if (cards != null)
            {
                n = Mathf.Min(cards.Length, 6);
                for (int i = 0; i < n; i++)
                {
                    int code = TarotEmpty;
                    var card = cards[i];
                    if (card != null)
                    {
                        int id = (int)card._item;
                        if (id >= 0 && id < TarotEmpty) code = id;
                    }
                    if (i < 4) lo |= code << (i * 8);
                    else hi |= code << ((i - 4) * 8);
                }
            }
            return Mk(PuzzleType.ROT_Tarot, wid, x.darkmode, false, false, lo, hi, 0, n, x.FlipSwitchPos);
        }

        /// <summary>Up to 8 moons in Int0..Int3, two per int (Pos8 | State4 | Desired4 each).</summary>
        internal static PuzzleStateEntry ReadMural(ROT_Mural x, long wid)
        {
            int m0, m1, m2, m3;
            PackMuralMoons(x.moons, out m0, out m1, out m2, out m3);
            return Mk(PuzzleType.ROT_Mural, wid, x.finished, x.busy, false, m0, m1, m2, m3, x.MoonTurnSpeed);
        }

        internal static PuzzleStateEntry ReadIncinerator(MED_Incinerator x, long wid)
            => Mk(PuzzleType.MED_Incinerator, wid, x.solved, false, false, x.A, x.B, x.C, 0, 0);

        /// <summary>Bool0 = MultiInteraction gate active, Float0 = weight. content stays personal (independent inventories).</summary>
        internal static PuzzleStateEntry ReadWaage(LAB_Waage x, long wid)
        {
            bool gate = x.MultiInteraction != null && x.MultiInteraction.activeSelf;
            return Mk(PuzzleType.LAB_Waage, wid, gate, false, false, 0, 0, 0, 0, x.weight);
        }

        internal static PuzzleStateEntry ReadShrine(RES_Shrine x, long wid)
            => Mk(PuzzleType.RES_Shrine, wid, x.solved, x.busy, false, x.big, x.mid, x.small, 0, 0);

        // ---- applies --------------------------------------------------------------------------------------------

        /// <summary>
        /// Native TryMasterFlip invokes OnSuccess, then latches solved. Scene data RES PowerLogic.OnSuccess: SetSpeed(2)
        /// on the paternoster engine, setPower(true) ×3, Engine SFX on, dimPOI, Play. Everything but the Play is world
        /// state a late joiner needs (the SetSpeed target is a Rewired RotateAroundAxis this mod cannot call directly),
        /// so the durable form is the event itself, once.
        /// </summary>
        internal static void ApplyPower(RES_Power x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.solved;
            int before = ResidencyPuzzleSyncService.PackBoolArray(x.states);
            x.solved = e.Bool0;
            x.powered = e.Bool1;
            ResidencyPuzzleSyncService.UnpackBoolArray(x.states, e.Int0);
            // Native Update redraws the volt readouts from states every frame. A peer's fuse flip clicks here
            // (Flip plays flipFuseSFX); all fuses dropping at once is TryMasterFlip's wrong-voltage reset.
            if (PuzzleFx.LiveApply && before != e.Int0 && !e.Bool0)
            {
                bool reset = e.Int0 == 0 && (before & (before - 1)) != 0; // two or more fuses down at once
                PuzzleFx.Press(x, reset ? x.failFuseSFX : x.flipFuseSFX);
            }
            if (!e.Bool0) return;
            PuzzleEdge.Solved("RES_Power", was, true,
                durable: () => { if (!was) PuzzleEdge.Invoke(x.OnSuccess); },
                onLive: () => PuzzleEdge.Invoke(x.OnSuccess), at: x);
        }

        internal static void ApplyVent(MED_VentPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.uncovered = e.Bool0;
            if (!e.Bool0) return;
            if (x.cover != null) x.cover.SetActive(false);
            if (x.smallCover != null) x.smallCover.SetActive(false);
            if (x.smallCoverDiscarded != null) x.smallCoverDiscarded.SetActive(true);
            var item = x.itemInter;
            if (item != null)
            {
                PuzzleSyncService.DisableOne(item.inter);
                item.enabled = false;
                item.gameObject.SetActive(false);
            }
            PuzzleSyncService.DisableInteractions(x);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        internal static void ApplyEvidenceLocker(EvidenceLockerLogicPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0;
            UnpackBoolBits64(x.states, e.Int0, e.Int1);
            int button;
            if (KeypadLive.TakeOp(unchecked((ulong)e.WorldId), e.Int2, out button) && PuzzleFx.LiveApply)
            {
                // Native Update: logic(button) then pushButton(key) (key dip + AudioSource click). The lights come
                // from states below; the push animation only shows in the zoom view, the click everywhere near.
                var buttons = x.buttons;
                if (PuzzleFx.Viewing(x) && buttons != null && button < buttons.Length && buttons[button] != null)
                    PuzzleFx.Run(x, x.pushButton(buttons[button].transform));
                else
                    PuzzleFx.Clip(x, x.pressSound);
            }
            // Native Update follows states → lights only while !solved and a remote apply skips it: snap the lights.
            var lights = x.lights;
            var states = x.states;
            if (lights != null && states != null)
            {
                int n = Mathf.Min(lights.Length, states.Length);
                for (int i = 0; i < n; i++)
                    if (lights[i] != null) lights[i].SetActive(states[i]);
            }
            if (!e.Bool0) return;
            SnapEvidenceLockerDoors(x.gameObject);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>The locker doors under this puzzle (Door on + unlocked + LoadState) and its siblings' (unlocked).</summary>
        static void SnapEvidenceLockerDoors(GameObject root)
        {
            var doors = root.GetComponentsInChildren<EvidenceLockerDoor>(true);
            if (doors != null)
            {
                for (int i = 0; i < doors.Length; i++)
                {
                    var d = doors[i];
                    if (d == null) continue;
                    d.done = true;
                    if (d.Door != null)
                    {
                        d.Door.SetActive(true);
                        PuzzleSyncService.UnlockDoorObject(d.Door);
                    }
                    PuzzleSyncService.DisableOne(d.inter);
                    Native("evidence-door-load", d.LoadState);
                }
            }
            var parent = root.transform.parent;
            if (parent == null) return;
            var siblings = parent.GetComponentsInChildren<EvidenceLockerDoor>(true);
            if (siblings == null) return;
            for (int i = 0; i < siblings.Length; i++)
            {
                var d = siblings[i];
                if (d == null) continue;
                d.done = true;
                if (d.Door != null)
                    PuzzleSyncService.UnlockDoorObject(d.Door);
                PuzzleSyncService.DisableOne(d.inter);
            }
        }

        /// <summary>
        /// FlipSwitchPos is not written: native Update eases the switch toward ±45 from darkmode and plays the klick
        /// emitter on the darkmode edge, so the flip animates and clicks here too.
        /// </summary>
        internal static void ApplyTarot(ROT_Tarot x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.darkmode = e.Bool0;
            if (e.Int3 <= 0) return;
            // A slot the native take/place could not finish differs from the entry: re-apply it next tick, or the
            // next poll would author that half-applied slot for everyone.
            if (!ApplyTarotCards(x, e.Int0, e.Int1, e.Int3))
                PuzzleSyncService.RetryApply(e);
        }

        const int TarotEmpty = 0xFF;

        /// <summary>
        /// Native TakeCard / PlaceCard (Ghidra ROT_Tarot.c) are the whole slot presentation: the card pickup on the slot's
        /// pivot (or hidden), the empty-slot placer off (or on), cards[] and the card sound. A peer's live move plays
        /// its sound here; a dump / remount snaps silently. False when a slot's native take/place threw.
        /// </summary>
        static bool ApplyTarotCards(ROT_Tarot x, int lo, int hi, int n)
        {
            var cards = x.cards;
            if (cards == null) return true;
            n = Mathf.Min(n, 6);
            bool ok = true;
            bool live = PuzzleFx.LiveApply;
            for (int i = 0; i < n && i < cards.Length; i++)
            {
                int code = i < 4 ? (lo >> (i * 8)) & 0xFF : (hi >> ((i - 4) * 8)) & 0xFF;
                AnItem item = code != TarotEmpty ? InventoryManager.getItem((Items.itemlist)code) : null;
                AnItem cur = cards[i];
                if (SameItem(cur, item)) continue;
                bool took = false;
                PuzzleFx.Begin(x, silent: !live);
                try
                {
                    if (cur != null) { x.TakeCard(cur); took = true; }
                    if (item != null) x.PlaceCard(i, item);
                }
                catch (Exception e)
                {
                    // Native slot step failed: the other slots still apply and the entry is retried.
                    Guard.Swallow("Puzzle.tarot-slot", e);
                    ok = false;
                    // A take without its place left the slot empty: put the old card back so the slot never shows
                    // a state nobody authored (the retry then places the entry's card).
                    if (took && item != null && cards[i] == null)
                        Native("tarot-restore", () => x.PlaceCard(i, cur));
                }
                finally { PuzzleFx.End(); }
            }
            // Moon readout follows cards[]. Load/place path; no inventory remove.
            Native("tarot-moons", x.SetMoons);
            return ok;
        }

        static bool SameItem(AnItem a, AnItem b)
        {
            if (a == null || b == null) return a == null && b == null;
            return a._item == b._item;
        }

        internal static void ApplyCardWriter(MED_CardWriter x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (e.Int3 > 0)
            {
                bool moved = false;
                if ((e.Int2 & (1 << 16)) != 0)
                {
                    // Native Update moves node and sets nodeIcon.localPosition = node * 3.2 (Ghidra MED_CardWriter.c).
                    var n = new Vector2((e.Int2 & 0xFF) - 8, ((e.Int2 >> 8) & 0xFF) - 8);
                    if (x.node != n)
                    {
                        moved = true;
                        x.node = n;
                        x.nodeIconPos = n * 3.2f;
                        if (x.nodeIcon != null) x.nodeIcon.localPosition = new Vector3(n.x * 3.2f, n.y * 3.2f, 0f);
                    }
                }
                // Each cursor move / burn plays keySFX natively; the peer's play here.
                if (PuzzleFx.LiveApply && (moved || x.remainingSteps != e.Int1))
                    PuzzleFx.Press(x, x.keySFX);
                NetGate.BeginApply();
                try { UnpackNodes(x.nodes, e.Int0, e.Int3); }
                finally { NetGate.EndApply(); }
                x.remainingSteps = e.Int1;
                if (x.remainingText != null)
                    x.remainingText.text = e.Int1.ToString();
            }
            x.solved = e.Bool0;
            x.hasCard = e.Bool1 || e.Bool0;
            if (e.Bool0)
            {
                if (x.insertCard != null) x.insertCard.SetActive(false);
                if (x.insertCardPrompt != null) x.insertCardPrompt.SetActive(false);
                if (x.pickUpBlank != null) x.pickUpBlank.SetActive(true);
                if (x.tinyCard != null) x.tinyCard.SetActive(true);
                PuzzleSyncService.RevealPickups(x.pickUpBlank);
                PuzzleSyncService.RevealPickups(x.gameObject);
            }
            else if (e.Bool1)
            {
                // Mid-insert pose: card present, prompts off (the pickup / tinyCard stay solved-only).
                if (x.Card != null) x.Card.SetActive(true);
                if (x.insertCardPrompt != null) x.insertCardPrompt.SetActive(false);
                if (x.insertCard != null) x.insertCard.SetActive(false);
            }
        }

        /// <summary>≤32 MED_KeyNodeConnection.connected bits; count = valid marker (card writer and key grid).</summary>
        internal static void PackNodes(Il2CppSystem.Collections.Generic.List<MED_KeyNodeConnection> nodes, out int bits, out int count)
        {
            bits = 0;
            count = 0;
            if (nodes == null) return;
            count = Mathf.Min(nodes.Count, 32);
            for (int i = 0; i < count; i++)
            {
                var node = nodes[i];
                if (node != null && node.connected)
                    bits |= 1 << i;
            }
        }

        internal static void UnpackNodes(Il2CppSystem.Collections.Generic.List<MED_KeyNodeConnection> nodes, int bits, int count)
        {
            if (nodes == null) return;
            int n = nodes.Count;
            if (count > 0 && count < n) n = count;
            n = Mathf.Min(n, 32);
            for (int i = 0; i < n; i++)
            {
                var node = nodes[i];
                if (node != null)
                    node.connected = (bits & (1 << i)) != 0;
            }
        }

        /// <summary>
        /// Native Update dims poi once when _lock.locked turns false while unlocked is still false, then latches
        /// unlocked; a remote apply latches unlocked first, so the dim runs here (idempotent: live and dump alike).
        /// </summary>
        internal static void ApplyShutters(RES_Shutters x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            bool was = x.unlocked;
            x.unlocked = true;
            if (x.Shutter != null) x.Shutter.SetActive(false);
            if (x.Handle != null) x.Handle.SetActive(false);
            if (x._lock != null)
                DoorNative.ApplyConnectedDoors(x._lock, false);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            PuzzleEdge.Solved("RES_Shutters", was, true, () =>
            {
                if (was || x.poi == null) return;
                NetGate.BeginApply();
                try { x.poi.dimPOI(); }
                finally { NetGate.EndApply(); }
            });
        }

        internal static void ApplyMagpie(ROT_Magpie x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            x.opened = true;
            if (x.CardPickup != null) x.CardPickup.SetActive(true);
            if (x.BoxObs != null) x.BoxObs.SetActive(false);
            PuzzleSyncService.RevealPickups(x.CardPickup);
            PuzzleSyncService.RevealPickups(x.gameObject);
        }

        /// <summary>
        /// Native Update sets solved and starts win, which invokes onSuccess then unlocks. Scene data (MEM / PEN_Wreck
        /// Reaktor Logic) onSuccess = Popup on, E Door Spot Blocked off, E Door Spot on: all SetActive, so the durable
        /// form replays exactly those. Rods: only positions are written; native Update lerps the Rods from them.
        /// </summary>
        internal static void ApplyReaktor(PEN_Reaktor x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.solved;
            x.valid = e.Bool1;
            var positions = x.positions;
            if (positions != null)
            {
                for (int i = 0; i < positions.Length && i < 4; i++)
                {
                    int v = Mathf.Min((e.Int0 >> (i * 3)) & 7, 4); // native Update clamps 0..4
                    if (positions[i] == v && PuzzleSyncService.ReplayWorld) continue;
                    positions[i] = v;
                }
            }
            if (!e.Bool0) return;
            PuzzleEdge.Solved("PEN_Reaktor", was, true,
                durable: () =>
                {
                    if (was) return;
                    PuzzleEdge.ReplayDurable(x.onSuccess);
                    SnapReaktor(x);
                },
                onLive: () =>
                {
                    PuzzleEdge.Invoke(x.onSuccess);
                    SnapReaktor(x);
                }, at: x);
        }

        static void SnapReaktor(PEN_Reaktor x)
        {
            x.solved = true;
            if (x.doorLock != null)
                DoorNative.ApplyConnectedDoors(x.doorLock, false);
            var singles = x.GetComponentsInChildren<InteractiveLockSingle>(true);
            if (singles != null)
            {
                for (int i = 0; i < singles.Length; i++)
                {
                    if (singles[i] == null) continue;
                    if (singles[i].door != null) singles[i].door.locked = false;
                    DoorNative.ApplyLockPlate(singles[i], false);
                }
            }
            if (x._event != null)
            {
                PuzzleSyncService.DisableInteractions(x._event);
                x._event.enabled = false;
            }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>A pack that completes the solution snaps solved even if Bool0 raced ahead of checkSolution.</summary>
        internal static void ApplyLabRings(LAB_Rings x, PuzzleStateEntry e)
        {
            if (x == null) return;
            int pack = e.Int0 & 0xFF;
            ApplyFinger(x, x.Zeige, pack & 3);
            ApplyFinger(x, x.Mittel, (pack >> 2) & 3);
            ApplyFinger(x, x.Ring, (pack >> 4) & 3);
            ApplyFinger(x, x.Klein, (pack >> 6) & 3);
            bool solved = e.Bool0
                || ((pack & 3) == ((int)x.S_Zeige & 3) && ((pack >> 2) & 3) == ((int)x.S_Mittel & 3)
                    && ((pack >> 4) & 3) == ((int)x.S_Ring & 3) && ((pack >> 6) & 3) == ((int)x.S_Klein & 3));
            if (!solved) return;
            x.solved = true;
            if (x.solvedState != null) x.solvedState.SetActive(true);
            if (x.FakePlate != null) x.FakePlate.SetActive(false);
            if (x.PlatePickup != null) x.PlatePickup.SetActive(true);
            PuzzleSyncService.RevealPickups(x.PlatePickup);
            PuzzleSyncService.RevealPickups(x.gameObject);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>
        /// Native LoadState → loadFinger → setStates; place/take also end in setStates for the Pickup / MultiInter
        /// visuals, with no inventory side effects. A live repeat of the same state is skipped; a dump / remount
        /// always redraws (OnEnable may have reset the pose).
        /// </summary>
        static void ApplyFinger(LAB_Rings mgr, LAB_Rings_Finger finger, int state)
        {
            if (finger == null) return;
            var desired = (LAB_Rings_Finger.rings)(state & 3);
            if (finger.state == desired && PuzzleSyncService.ReplayWorld) return;
            finger.state = desired;
            Native("rings-setstates", () => mgr.setStates(finger));
        }

        /// <summary>
        /// MeatBlocker "Death" seals the bookstore wing that holds NG+ KeyOfSacrifice: the party seal waits while that
        /// key is still obtainable (the picker is gated too; wiki Artifact softlock).
        /// </summary>
        internal static void ApplyMeatBlocker(ROT_MeatBlocker x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (!e.Bool0 && IsDeathBlocker(x) && SacrificeKeyStillAvailable())
            {
                x.pickups = e.Int0;
                PlaytestLog.Event("Puzzle", "hold Death MeatBlocker seal until KeyOfSacrifice");
                return;
            }
            bool unblocked = e.Bool0;
            x.pickups = e.Int0;
            x.blocked = !unblocked;
            SetAll(x.Blockers, !unblocked);
            SetAll(x.UnBlockers, unblocked);
            var locks = x.Locks;
            if (locks == null) return;
            for (int i = 0; i < locks.Length; i++)
                if (locks[i] != null)
                    DoorNative.ApplyConnectedDoors(locks[i], locked: !unblocked);
        }

        internal static bool IsDeathBlocker(ROT_MeatBlocker x)
            => string.Equals(x.ID, "Death", StringComparison.Ordinal);

        internal static bool SacrificeKeyStillAvailable()
        {
            var net = LanNetworkManager.Instance;
            return net != null && net.IsConnected && net.PickupSync.KeyOfSacrificeAvailableUnclaimed();
        }

        static void SetAll(Il2CppReferenceArray<GameObject> gos, bool active)
        {
            if (gos == null) return;
            for (int i = 0; i < gos.Length; i++)
                if (gos[i] != null) gos[i].SetActive(active);
        }

        /// <summary>A live peer flip runs the native Flip (animation + its consequences); dump / re-snap set the flag.</summary>
        internal static void ApplyFlipSwitch(FlipSwitch x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (x.flipped != e.Bool0)
            {
                if (PuzzleSyncService.ReplayWorld)
                {
                    try { x.Flip(); }
                    catch (Exception ex)
                    {
                        Guard.Swallow("Puzzle.flip", ex);
                        x.flipped = e.Bool0;
                    }
                }
                else
                    x.flipped = e.Bool0;
            }
            if (e.Bool0) PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>
        /// Native Update invokes onSolved only when finished was false, then latches it. Scene data ROT_Rotfront
        /// MuralLogic.onSolved: goBack, Blocker Entry off, setUnleavable, StartCutscene UnlockMural, Play. useRing only
        /// toggles RingInter / MissingRing. Live in-room edge = onSolved + useRing; everything else = the SetActive part
        /// of onSolved (Blocker Entry off) + the ring objects, never the cutscene.
        /// </summary>
        internal static void ApplyMural(ROT_Mural x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.finished;
            int m0, m1, m2, m3;
            PackMuralMoons(x.moons, out m0, out m1, out m2, out m3);
            bool turned = m0 != e.Int0 || m1 != e.Int1 || m2 != e.Int2 || m3 != e.Int3;
            x.finished = e.Bool0;
            x.busy = e.Bool1;
            x.MoonTurnSpeed = e.Float0;
            UnpackMuralMoons(x.moons, e.Int0, e.Int1, e.Int2, e.Int3);
            // Native Update eases each moon to its new state; Next/Last's dial click plays here for a peer's turn.
            if (turned && PuzzleFx.LiveApply && !was)
                PuzzleFx.Press(x, x.dialClickSFX);
            if (!e.Bool0) return;
            PuzzleEdge.Solved("ROT_Mural", was, true,
                durable: () =>
                {
                    if (!was) PuzzleEdge.ReplayDurable(x.onSolved);
                    if (x.RingInter != null) x.RingInter.SetActive(false);
                    if (x.MissingRing != null) x.MissingRing.SetActive(true);
                },
                onLive: () =>
                {
                    PuzzleEdge.Invoke(x.onSolved);
                    x.useRing();
                }, at: x);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        static int PackOneMoon(ROT_Mural.Moon m)
            => m == null ? 0 : (m.Pos & 0xFF) | ((m.State & 0xF) << 8) | ((m.DesiredPos & 0xF) << 12);

        static void UnpackOneMoon(ROT_Mural.Moon m, int packed)
        {
            m.Pos = packed & 0xFF;
            m.State = (packed >> 8) & 0xF;
            m.DesiredPos = (packed >> 12) & 0xF;
        }

        static void PackMuralMoons(Il2CppReferenceArray<ROT_Mural.Moon> moons, out int i0, out int i1, out int i2, out int i3)
        {
            i0 = i1 = i2 = i3 = 0;
            if (moons == null) return;
            int Pair(int i) => PackOneMoon(i < moons.Length ? moons[i] : null)
                | (PackOneMoon(i + 1 < moons.Length ? moons[i + 1] : null) << 16);
            i0 = Pair(0);
            i1 = Pair(2);
            i2 = Pair(4);
            i3 = Pair(6);
        }

        static void UnpackMuralMoons(Il2CppReferenceArray<ROT_Mural.Moon> moons, int i0, int i1, int i2, int i3)
        {
            if (moons == null) return;
            int[] packs = { i0, i1, i2, i3 };
            for (int i = 0; i < moons.Length && i < 8; i++)
                if (moons[i] != null)
                    UnpackOneMoon(moons[i], (packs[i >> 1] >> ((i & 1) * 16)) & 0xFFFF);
        }

        static void PackBoolBits64(Il2CppStructArray<bool> arr, out int lo, out int hi)
        {
            lo = 0;
            hi = 0;
            if (arr == null) return;
            for (int i = 0; i < arr.Length && i < 64; i++)
            {
                if (!arr[i]) continue;
                if (i < 32) lo |= 1 << i;
                else hi |= 1 << (i - 32);
            }
        }

        static void UnpackBoolBits64(Il2CppStructArray<bool> arr, int lo, int hi)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length && i < 64; i++)
                arr[i] = i < 32 ? (lo & (1 << i)) != 0 : (hi & (1 << (i - 32))) != 0;
        }

        /// <summary>
        /// Native plusX / minusX (Ghidra MED_Incinerator.c) also set the curve (Yspeed = A/10, Yacc1 = B/10, Yacc2 = C/10)
        /// and turn the knob (Euler(0, (n-10)*-9, 0)); nothing redraws them from A/B/C, so they are written here (the
        /// shutdown below integrates that curve).
        /// </summary>
        internal static void ApplyIncinerator(MED_Incinerator x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool turned = x.A != e.Int0 || x.B != e.Int1 || x.C != e.Int2;
            x.A = e.Int0; x.B = e.Int1; x.C = e.Int2;
            x.Yspeed = x.A / 10f;
            x.Yacc1 = x.B / 10f;
            x.Yacc2 = x.C / 10f;
            if (x.Abutton != null) x.Abutton.localRotation = Quaternion.Euler(0f, (x.A - 10) * -9f, 0f);
            if (x.Bbutton != null) x.Bbutton.localRotation = Quaternion.Euler(0f, (x.B - 10) * -9f, 0f);
            if (x.Cbutton != null) x.Cbutton.localRotation = Quaternion.Euler(0f, (x.C - 10) * -9f, 0f);
            if (turned && PuzzleFx.LiveApply && !e.Bool0)
                PuzzleFx.Press(x, x.TurnDialSFX);
            bool was = x.solved;
            if (!e.Bool0) { x.solved = false; return; }
            // Shutdown is the native lever pull: lever + emitter animation, curve check, hatch, plus SProgress /
            // RecordSplit / goBack and later one-shots that outlive PuzzleFx routing. Only a live edge for a player in
            // the room runs it; a join dump, held re-snap or other-room solve takes the Awake solved pose.
            PuzzleEdge.Solved("MED_Incinerator", was, true,
                durable: () => { if (!was) SnapIncineratorSolved(x); },
                onLive: () =>
                {
                    PuzzleFx.Begin(x);
                    try { x.StartShutdown(); }
                    finally { PuzzleFx.End(); }
                },
                at: x);
            x.solved = true;
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>
        /// Ghidra MED_Incinerator.c Awake, solved branch: shutdown latched, lever down, emitter z 0.5, tiny hatch
        /// -100, hatch z 100, fire / card glow off, success light on. OnEnable sets the solved FMOD parameter.
        /// </summary>
        static void SnapIncineratorSolved(MED_Incinerator x)
        {
            x.shutdown = true;
            x.solved = true;
            if (x.emitter != null)
            {
                var p = x.emitter.localPosition;
                p.z = 0.5f;
                x.emitter.localPosition = p;
            }
            if (x.Lever != null) x.Lever.localRotation = Quaternion.Euler(0f, 0f, 0f);
            if (x.tinyHatch != null) x.tinyHatch.localRotation = Quaternion.Euler(0f, 0f, -100f);
            if (x.tinyLight != null) x.tinyLight.SetActive(false);
            if (x.Fire != null) x.Fire.intensity = 0f;
            if (x.FireFill != null) x.FireFill.intensity = 0f;
            if (x.hatch != null)
            {
                var r = x.hatch.localRotation.eulerAngles;
                x.hatch.localRotation = Quaternion.Euler(r.x, r.y, 100f);
            }
            if (x.cardGlow != null) x.cardGlow.intensity = 0f;
            if (x.successLight != null) x.successLight.SetActive(true);
            Native("incinerator-onenable", x.OnEnable);
        }

        /// <summary>
        /// Native placeItem → delayedMulti → MultiInteraction.SetActive(true); the scene starts it inactive, so the
        /// gate is opened here (the weight alone left late joiners stuck). content stays unsynced (independent inventories).
        /// </summary>
        internal static void ApplyWaage(LAB_Waage x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.weight = e.Float0;
            if (!e.Bool0) return;
            var gate = x.MultiInteraction;
            if (gate != null && !gate.activeSelf)
                gate.SetActive(true);
        }

        /// <summary>
        /// Native CheckSolve early-outs when solved is already true, so a remote apply that latches solved must do what
        /// delayedReactionToSolve does: content on, doors to doorPos 1 (Update: LeftDoor Z = -OpenAngle * doorPos,
        /// RightDoor Z = +OpenAngle * doorPos), onSuccess (scene data: dimPOI ×2, so the durable form is the same).
        /// Unsolved: plates snap to pos * 60° (the Update setWheel lerp target).
        /// </summary>
        internal static void ApplyShrine(RES_Shrine x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.solved;
            x.solved = e.Bool0;
            x.busy = e.Bool1;
            x.big = e.Int0; x.mid = e.Int1; x.small = e.Int2;
            if (!e.Bool0)
            {
                NetGate.BeginApply();
                try
                {
                    x.bigX = e.Int0 * 60f;
                    if (x.outside != null) x.setWheel(x.outside, e.Int0, x.bigX);
                    x.midX = e.Int1 * 60f;
                    if (x.middle != null) x.setWheel(x.middle, e.Int1, x.midX);
                    x.smallX = e.Int2 * 60f;
                    if (x.inside != null) x.setWheel(x.inside, e.Int2, x.smallX);
                }
                finally { NetGate.EndApply(); }
                return;
            }
            PuzzleEdge.Solved("RES_Shrine", was, true,
                durable: () =>
                {
                    if (was) return;
                    PuzzleEdge.ReplayDurable(x.onSuccess);
                    SnapShrineFinalPose(x);
                },
                onLive: () =>
                {
                    PuzzleEdge.Invoke(x.onSuccess);
                    SnapShrineFinalPose(x);
                }, at: x);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        static void SnapShrineFinalPose(RES_Shrine x)
        {
            x.doorPos = 1f;
            float open = x.OpenAngle;
            if (x.LeftDoor != null) x.LeftDoor.localEulerAngles = new Vector3(0f, 0f, -open);
            if (x.RightDoor != null) x.RightDoor.localEulerAngles = new Vector3(0f, 0f, open);
            if (x.content != null) x.content.SetActive(true);
        }
    }
}
