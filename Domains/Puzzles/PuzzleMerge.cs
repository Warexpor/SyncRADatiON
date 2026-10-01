// Pure merge rules for concurrent puzzle edits (no Unity objects, no service state): linked into the test project.
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Networking
{
    internal static class PuzzleMerge
    {
        // Types where the host merges only the cells a client actually changed (see PuzzleStateEntry.Mask).
        internal static bool IsMergeType(PuzzleType t)
        {
            switch (t)
            {
                case PuzzleType.DialLock:
                case PuzzleType.MED_Incinerator:
                case PuzzleType.RES_Shrine:
                case PuzzleType.PEN_Reaktor:
                case PuzzleType.ROT_Mural:
                case PuzzleType.ROT_RadioAlignment:
                case PuzzleType.DET_RadioCodeLock:
                // Live shared puzzles: two players in the same screen see each other's input as it happens.
                case PuzzleType.PatternLock:
                case PuzzleType.MED_KeyGrid:
                case PuzzleType.ROT_Tarot:
                case PuzzleType.EvidenceLockerPuzzle:
                case PuzzleType.Keypad3D:
                case PuzzleType.ROT_Keypad:
                case PuzzleType.PEN_Codepad:
                    return true;
                default:
                    return false;
            }
        }

        // One bit of Int0 per cell (pattern lights, key-grid nodes): mask bit 9+i = cell i changed.
        internal const int BitCells = 23;
        internal static bool IsBitCellType(PuzzleType t) => t == PuzzleType.PatternLock || t == PuzzleType.MED_KeyGrid;

        // Tarot: one byte per card slot (Int0 slots 0..3, Int1 slots 4..5): mask bit 9+slot = slot changed.
        internal const int TarotSlots = 6;

        // Input that only makes sense whole (a typed code, the evidence locker's coupled lights + last press):
        // any Int change replaces all four, so two concurrent entries never interleave into a code nobody typed.
        internal static bool IsAtomicIntsType(PuzzleType t)
            => t == PuzzleType.EvidenceLockerPuzzle || t == PuzzleType.Keypad3D
            || t == PuzzleType.ROT_Keypad || t == PuzzleType.PEN_Codepad;

        // Mural packs two 16-bit moons per Int: merge at half granularity.
        internal static bool IsHalfMergeType(PuzzleType t) => t == PuzzleType.ROT_Mural;

        // Reaktor packs four 3-bit rod positions in Int0: mask bits 16..19 = rod r changed. The selection
        // cursor (PEN_Reaktor.current) is per player and not on the wire (Int1 stays 0).
        internal const int ReaktorRodBits = 4;
        internal static bool IsRodMergeType(PuzzleType t) => t == PuzzleType.PEN_Reaktor;

        internal static int DiffMask(PuzzleStateEntry a, PuzzleStateEntry b)
        {
            int m = 0;
            if (a.Bool0 != b.Bool0) m |= 1;
            if (a.Bool1 != b.Bool1) m |= 2;
            if (a.Bool2 != b.Bool2) m |= 4;
            if (a.Int0 != b.Int0) m |= 8;
            if (a.Int1 != b.Int1) m |= 16;
            if (a.Int2 != b.Int2) m |= 32;
            if (a.Int3 != b.Int3) m |= 64;
            if (!Mathf.Approximately(a.Float0, b.Float0)) m |= 128;
            if (!Mathf.Approximately(a.Float1, b.Float1)) m |= 256;
            if (IsRodMergeType(b.Type))
            {
                int x = a.Int0 ^ b.Int0;
                for (int r = 0; r < ReaktorRodBits; r++)
                    if (((x >> (r * 3)) & 7) != 0) m |= 1 << (16 + r);
            }
            if (IsHalfMergeType(b.Type))
            {
                for (int k = 0; k < 4; k++)
                {
                    int x = GetInt(a, k) ^ GetInt(b, k);
                    if ((x & 0xFFFF) != 0) m |= 1 << (16 + 2 * k);
                    if ((x & unchecked((int)0xFFFF0000)) != 0) m |= 1 << (17 + 2 * k);
                }
            }
            if (IsBitCellType(b.Type))
            {
                int x = a.Int0 ^ b.Int0;
                for (int i = 0; i < BitCells; i++)
                    if (((x >> i) & 1) != 0) m |= 1 << (9 + i);
            }
            if (b.Type == PuzzleType.ROT_Tarot)
            {
                for (int s = 0; s < TarotSlots; s++)
                    if (TarotSlot(a, s) != TarotSlot(b, s)) m |= 1 << (9 + s);
                m &= ~128; // FlipSwitchPos is the switch animation native Update drives from darkmode
            }
            if (IsAtomicIntsType(b.Type) && (m & (8 | 16 | 32 | 64)) != 0)
                m |= 8 | 16 | 32 | 64;
            return m;
        }

        internal static int TarotSlot(PuzzleStateEntry e, int s)
            => s < 4 ? (e.Int0 >> (8 * s)) & 0xFF : (e.Int1 >> (8 * (s - 4))) & 0xFF;

        internal static void SetTarotSlot(ref PuzzleStateEntry e, int s, int v)
        {
            if (s < 4) e.Int0 = (e.Int0 & ~(0xFF << (8 * s))) | ((v & 0xFF) << (8 * s));
            else e.Int1 = (e.Int1 & ~(0xFF << (8 * (s - 4)))) | ((v & 0xFF) << (8 * (s - 4)));
        }

        internal static int GetInt(PuzzleStateEntry e, int k)
        {
            switch (k)
            {
                case 0: return e.Int0;
                case 1: return e.Int1;
                case 2: return e.Int2;
                default: return e.Int3;
            }
        }

        internal static void SetInt(ref PuzzleStateEntry e, int k, int v)
        {
            switch (k)
            {
                case 0: e.Int0 = v; break;
                case 1: e.Int1 = v; break;
                case 2: e.Int2 = v; break;
                default: e.Int3 = v; break;
            }
        }

        /// <summary>Overlay only the cells named by inc.Mask onto the host's current state.</summary>
        internal static PuzzleStateEntry MergeEdit(PuzzleStateEntry cur, PuzzleStateEntry inc)
        {
            int m = inc.Mask;
            var r = cur;
            if ((m & 1) != 0) r.Bool0 = inc.Bool0;
            if ((m & 2) != 0) r.Bool1 = inc.Bool1;
            if ((m & 4) != 0) r.Bool2 = inc.Bool2;
            bool half = IsHalfMergeType(inc.Type);
            bool rods = IsRodMergeType(inc.Type);
            bool cells = IsBitCellType(inc.Type);
            bool tarot = inc.Type == PuzzleType.ROT_Tarot;
            for (int k = 0; k < 4; k++)
            {
                if ((m & (8 << k)) == 0) continue;
                if (cells && k == 0)
                {
                    int cv = cur.Int0;
                    for (int i = 0; i < BitCells; i++)
                    {
                        if ((m & (1 << (9 + i))) == 0) continue;
                        cv = (cv & ~(1 << i)) | (inc.Int0 & (1 << i));
                    }
                    r.Int0 = cv;
                    continue;
                }
                if (tarot && k <= 1)
                {
                    int s0 = k == 0 ? 0 : 4, s1 = k == 0 ? 4 : TarotSlots;
                    for (int s = s0; s < s1; s++)
                        if ((m & (1 << (9 + s))) != 0) SetTarotSlot(ref r, s, TarotSlot(inc, s));
                    continue;
                }
                if (rods && k == 0)
                {
                    int rv = cur.Int0;
                    for (int rr = 0; rr < ReaktorRodBits; rr++)
                    {
                        if ((m & (1 << (16 + rr))) == 0) continue;
                        int sh = rr * 3;
                        rv = (rv & ~(7 << sh)) | (inc.Int0 & (7 << sh));
                    }
                    r.Int0 = rv;
                    continue;
                }
                if (!half)
                {
                    SetInt(ref r, k, GetInt(inc, k));
                    continue;
                }
                int v = GetInt(cur, k);
                int n = GetInt(inc, k);
                if ((m & (1 << (16 + 2 * k))) != 0) v = (v & unchecked((int)0xFFFF0000)) | (n & 0xFFFF);
                if ((m & (1 << (17 + 2 * k))) != 0) v = (v & 0xFFFF) | (n & unchecked((int)0xFFFF0000));
                SetInt(ref r, k, v);
            }
            if ((m & 128) != 0) r.Float0 = inc.Float0;
            if ((m & 256) != 0) r.Float1 = inc.Float1;
            r.Seq = inc.Seq;
            r.Mask = 0;
            return r;
        }

        internal static bool SameCells(PuzzleStateEntry a, PuzzleStateEntry b)
        {
            return a.Bool0 == b.Bool0 && a.Bool1 == b.Bool1 && a.Bool2 == b.Bool2
                && a.Int0 == b.Int0 && a.Int1 == b.Int1 && a.Int2 == b.Int2 && a.Int3 == b.Int3
                && Mathf.Approximately(a.Float0, b.Float0) && Mathf.Approximately(a.Float1, b.Float1);
        }

        // Client: the last few states this client sent per merge-type key. The host relays a merged edit to
        // everyone, the author included, so fast input (keypad digits, pattern presses) gets its own older states
        // echoed back after newer local presses.
        internal const int OwnSentKeep = 6;
        internal const float OwnEchoWindow = 2f;
        internal struct OwnSent { public PuzzleStateEntry E; public float At; }

        internal static void NoteOwnSent(List<OwnSent> l, PuzzleStateEntry e, float now)
        {
            if (l.Count >= OwnSentKeep) l.RemoveAt(0);
            l.Add(new OwnSent { E = e, At = now });
        }

        /// <summary>e equals a state this client sent recently but has since moved past (oldest first in l).</summary>
        internal static bool IsStaleOwnEcho(List<OwnSent> l, PuzzleStateEntry e, float now)
        {
            if (l == null || l.Count < 2) return false;
            if (now - l[l.Count - 1].At > OwnEchoWindow) return false;
            if (SameCells(l[l.Count - 1].E, e)) return false; // the newest: applying it is a no-op anyway
            for (int i = l.Count - 2; i >= 0; i--)
            {
                if (now - l[i].At > OwnEchoWindow) break;
                if (SameCells(l[i].E, e)) return true;
            }
            return false;
        }
    }
}
