using System.Collections.Generic;
using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>Concurrent puzzle edits: the host overlays only the cells the client changed (PuzzleMerge.cs).</summary>
    public class PuzzleMergeTests
    {
        static PuzzleStateEntry E(PuzzleType t, int i0 = 0, int i1 = 0, int i2 = 0, int i3 = 0, bool b0 = false, float f0 = 0f)
            => new PuzzleStateEntry { Type = t, WorldId = 42, Bool0 = b0, Int0 = i0, Int1 = i1, Int2 = i2, Int3 = i3, Float0 = f0 };

        /// <summary>Client edit: its state after the change, masked against what it had before.</summary>
        static PuzzleStateEntry Edit(PuzzleStateEntry before, PuzzleStateEntry after, MergeKind kind, int seq = 7)
        {
            after.Mask = PuzzleMerge.DiffMask(before, after, kind);
            after.Seq = seq;
            return after;
        }

        [Fact]
        public void Bit_cells_merge_per_light()
        {
            // Host has lights 0,1 on; the client only saw none and turned light 2 on.
            var host = E(PuzzleType.PatternLock, i0: 0b0011, i3: 9);
            var inc = Edit(E(PuzzleType.PatternLock, i0: 0b0000, i3: 9), E(PuzzleType.PatternLock, i0: 0b0100, i3: 9), MergeKind.BitCells);
            Assert.Equal(1 << (9 + 2), inc.Mask & ~(8 | 16 | 32 | 64 | 1 | 2 | 4));
            var r = PuzzleMerge.MergeEdit(host, inc, MergeKind.BitCells);
            Assert.Equal(0b0111, r.Int0);
        }

        [Fact]
        public void Bit_cells_clear_does_not_revert_other_cells()
        {
            // Host turned light 3 on meanwhile; the client turned light 0 off.
            var host = E(PuzzleType.MED_KeyGrid, i0: 0b1001, i1: 1);
            var inc = Edit(E(PuzzleType.MED_KeyGrid, i0: 0b0001, i1: 1), E(PuzzleType.MED_KeyGrid, i0: 0b0000, i1: 1), MergeKind.BitCells);
            var r = PuzzleMerge.MergeEdit(host, inc, MergeKind.BitCells);
            Assert.Equal(0b1000, r.Int0);
            Assert.Equal(1, r.Int1);
        }

        static int Slots(int a, int b, int c, int d) => a | (b << 8) | (c << 16) | (d << 24);

        [Fact]
        public void Tarot_merges_per_slot_and_ignores_switch_float()
        {
            var host = E(PuzzleType.ROT_Tarot, i0: Slots(1, 2, 3, 4), i1: Slots(5, 6, 0, 0), i3: 6, f0: 45f);
            var before = E(PuzzleType.ROT_Tarot, i0: Slots(1, 0xFF, 3, 4), i1: Slots(5, 0xFF, 0, 0), i3: 6, f0: -45f);
            var after = E(PuzzleType.ROT_Tarot, i0: Slots(1, 0xFF, 3, 7), i1: Slots(9, 0xFF, 0, 0), i3: 6, f0: 10f);
            var inc = Edit(before, after, MergeKind.Tarot);
            Assert.Equal(0, inc.Mask & 128); // FlipSwitchPos never rides the mask
            var r = PuzzleMerge.MergeEdit(host, inc, MergeKind.Tarot);
            // Slot 3 and slot 4 came from the client; slots 1 and 5 (emptied only in its stale view) keep the host's.
            Assert.Equal(Slots(1, 2, 3, 7), r.Int0);
            Assert.Equal(Slots(9, 6, 0, 0), r.Int1);
            Assert.Equal(45f, r.Float0);
            for (int s = 0; s < PuzzleMerge.TarotSlots; s++)
                Assert.InRange(PuzzleMerge.TarotSlot(r, s), 0, 0xFF);
        }

        [Fact]
        public void Atomic_ints_replace_the_whole_code()
        {
            // Another player typed "55" meanwhile; this client typed a digit onto its own "12": the code is never
            // interleaved, the newest typed code wins whole.
            var host = E(PuzzleType.Keypad3D, i0: 0x55, i1: 0x9, i3: 2);
            var before = E(PuzzleType.Keypad3D, i0: 0x21, i3: 2);
            var after = E(PuzzleType.Keypad3D, i0: 0x321, i3: 3);
            var inc = Edit(before, after, MergeKind.AtomicInts);
            Assert.Equal(8 | 16 | 32 | 64, inc.Mask & (8 | 16 | 32 | 64));
            var r = PuzzleMerge.MergeEdit(host, inc, MergeKind.AtomicInts);
            Assert.Equal(0x321, r.Int0);
            Assert.Equal(0, r.Int1);
            Assert.Equal(3, r.Int3);
        }

        [Fact]
        public void Atomic_ints_untouched_when_only_a_bool_changed()
        {
            var host = E(PuzzleType.EvidenceLockerPuzzle, i0: 5, i1: 6);
            var inc = Edit(E(PuzzleType.EvidenceLockerPuzzle, i0: 1), E(PuzzleType.EvidenceLockerPuzzle, i0: 1, b0: true), MergeKind.AtomicInts);
            var r = PuzzleMerge.MergeEdit(host, inc, MergeKind.AtomicInts);
            Assert.True(r.Bool0);
            Assert.Equal(5, r.Int0);
            Assert.Equal(6, r.Int1);
        }

        [Fact]
        public void Reaktor_rods_merge_per_rod()
        {
            // Rods packed 3 bits each: host [0,4,3,1]; client moved rod 2 from 3 to 2 on a stale [1,4,3,1].
            int Pack(int a, int b, int c, int d) => a | (b << 3) | (c << 6) | (d << 9);
            var host = E(PuzzleType.PEN_Reaktor, i0: Pack(0, 4, 3, 1));
            var inc = Edit(E(PuzzleType.PEN_Reaktor, i0: Pack(1, 4, 3, 1)), E(PuzzleType.PEN_Reaktor, i0: Pack(1, 4, 2, 1)), MergeKind.Rods);
            var r = PuzzleMerge.MergeEdit(host, inc, MergeKind.Rods);
            Assert.Equal(Pack(0, 4, 2, 1), r.Int0);
        }

        [Fact]
        public void Mural_merges_per_moon_half()
        {
            var host = E(PuzzleType.ROT_Mural, i0: 0x0003_0001);
            var inc = Edit(E(PuzzleType.ROT_Mural, i0: 0x0000_0001), E(PuzzleType.ROT_Mural, i0: 0x0000_0002), MergeKind.HalfInts);
            var r = PuzzleMerge.MergeEdit(host, inc, MergeKind.HalfInts);
            Assert.Equal(0x0003_0002, r.Int0);
        }

        [Fact]
        public void Fields_merge_only_changed_fields()
        {
            // DialLock: host turned dial B; the client turned dial A on a stale view.
            var host = E(PuzzleType.DialLock, i0: 0, i1: 3, i3: 5);
            var inc = Edit(E(PuzzleType.DialLock, i3: 5), E(PuzzleType.DialLock, i0: 2, i3: 5), MergeKind.Fields);
            var r = PuzzleMerge.MergeEdit(host, inc, MergeKind.Fields);
            Assert.Equal(2, r.Int0);
            Assert.Equal(3, r.Int1);
        }

        [Fact]
        public void Merge_takes_incoming_seq_and_clears_mask()
        {
            var inc = Edit(E(PuzzleType.DialLock, i0: 1), E(PuzzleType.DialLock, i0: 2), MergeKind.Fields, seq: 11);
            var r = PuzzleMerge.MergeEdit(E(PuzzleType.DialLock), inc, MergeKind.Fields);
            Assert.Equal(11, r.Seq);
            Assert.Equal(0, r.Mask);
        }

        [Fact]
        public void Grow_keeps_both_players_items()
        {
            var cur = E(PuzzleType.MEM_ChecklistLogic, i0: 0b0110);
            var inc = E(PuzzleType.MEM_ChecklistLogic, i0: 0b1000);
            Assert.True(PuzzleMerge.Grow(ref inc, cur));
            Assert.Equal(0b1110, inc.Int0);
            Assert.False(inc.Bool0);
            var done = E(PuzzleType.MEM_ChecklistLogic, i0: 0b1110, b0: true);
            Assert.False(PuzzleMerge.Grow(ref done, cur));
            Assert.True(done.Bool0);
        }

        [Fact]
        public void Cell_merge_kinds_exclude_none_and_grow()
        {
            Assert.False(PuzzleMerge.IsCellMerge(MergeKind.None));
            Assert.False(PuzzleMerge.IsCellMerge(MergeKind.Grow));
            Assert.True(PuzzleMerge.IsCellMerge(MergeKind.Fields));
            Assert.True(PuzzleMerge.IsCellMerge(MergeKind.BitCells));
            Assert.True(PuzzleMerge.IsCellMerge(MergeKind.AtomicInts));
        }

        static List<PuzzleMerge.OwnSent> Sent(params (int code, float at)[] sends)
        {
            var l = new List<PuzzleMerge.OwnSent>();
            foreach (var s in sends)
                PuzzleMerge.NoteOwnSent(l, E(PuzzleType.Keypad3D, i0: s.code), s.at);
            return l;
        }

        [Fact]
        public void Stale_echo_of_an_older_own_press_is_skipped()
        {
            var l = Sent((0x1, 10f), (0x21, 10.2f), (0x321, 10.4f));
            Assert.True(PuzzleMerge.IsStaleOwnEcho(l, E(PuzzleType.Keypad3D, i0: 0x21), 10.6f));
        }

        [Fact]
        public void Echo_of_the_newest_press_or_a_foreign_state_applies()
        {
            var l = Sent((0x1, 10f), (0x21, 10.2f));
            Assert.False(PuzzleMerge.IsStaleOwnEcho(l, E(PuzzleType.Keypad3D, i0: 0x21), 10.3f));
            Assert.False(PuzzleMerge.IsStaleOwnEcho(l, E(PuzzleType.Keypad3D, i0: 0x99), 10.3f));
        }

        [Fact]
        public void Stale_echo_window_expires()
        {
            var l = Sent((0x1, 10f), (0x21, 10.2f));
            Assert.False(PuzzleMerge.IsStaleOwnEcho(l, E(PuzzleType.Keypad3D, i0: 0x1), 10.2f + PuzzleMerge.OwnEchoWindow + 0.1f));
            Assert.False(PuzzleMerge.IsStaleOwnEcho(Sent((0x1, 10f)), E(PuzzleType.Keypad3D, i0: 0x1), 10.1f));
        }

        [Fact]
        public void Own_sent_history_is_bounded()
        {
            var l = new List<PuzzleMerge.OwnSent>();
            for (int i = 0; i < PuzzleMerge.OwnSentKeep + 3; i++)
                PuzzleMerge.NoteOwnSent(l, E(PuzzleType.Keypad3D, i0: i), i * 0.01f);
            Assert.Equal(PuzzleMerge.OwnSentKeep, l.Count);
            Assert.Equal(3, l[0].E.Int0);
        }
    }
}
