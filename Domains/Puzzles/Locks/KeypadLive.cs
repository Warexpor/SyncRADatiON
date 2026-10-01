// Live shared keypads: Keypad3D (safes, radio code lock), ROT_Keypad, PEN_Codepad (six-wheel cryo codepad).
// Both players type into one shared code; each press replays on the other side through the native coroutine
// (button push / wheel flip, its sound, the red blink on a wrong code), Ghidra Keypad3D.c / ROT_Keypad.c /
// PEN_Codepad.c. Wire (PuzzleStateEntry, merged whole by PuzzleSyncService.IsAtomicIntsType):
//   Keypad3D / ROT_Keypad: Int0..Int2 = code digits, one nibble each (8 per int, 24 max);
//     Int3 = length | key << 8 (last pressed key index + 1, 0 = none) | seq << 13 | wrong << 21.
//   PEN_Codepad: Int0 = six 3-bit wheel values; Int3 = op | seq << 8 (op 1..6 up wheel, 7..12 down, 13 reset,
//     14 submit). seq counts presses per keypad, so a peer replays each press once and never its own.
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class KeypadLive
    {
        const int MaxDigits = 24;
        const int KeyClear = 10, KeySubmit = 11;
        const int OpReset = 13, OpSubmit = 14;

        struct Op { public int Seq; public int Key; public bool Wrong; }

        // Last press per keypad (local or applied), by WorldId.
        static readonly Dictionary<ulong, Op> _op = new Dictionary<ulong, Op>();
        // Last shared code / wheel values, restored when native OnEnable resets the pad on a chunk remount.
        static readonly Dictionary<ulong, string> _code = new Dictionary<ulong, string>();
        static readonly Dictionary<ulong, int> _wheels = new Dictionary<ulong, int>();

        public static void Reset()
        {
            _op.Clear();
            _code.Clear();
            _wheels.Clear();
        }

        /// <summary>A local press: the next read carries it with a new seq.</summary>
        public static void NotePress(ulong id, int key, bool wrong)
        {
            Op o;
            _op.TryGetValue(id, out o);
            o.Seq = (o.Seq + 1) & 0xFF;
            o.Key = key;
            o.Wrong = wrong;
            _op[id] = o;
        }

        /// <summary>Generic last-press field: (key + 1) | seq &lt;&lt; 8 (0 = no press yet).</summary>
        public static int PackOp(ulong id)
        {
            Op o;
            if (!_op.TryGetValue(id, out o) || o.Seq == 0) return 0;
            return ((o.Key + 1) & 0xFF) | ((o.Seq & 0xFF) << 8);
        }

        /// <summary>True (with the key) when a packed op is a press this peer has not replayed yet.</summary>
        public static bool TakeOp(ulong id, int packed, out int key)
        {
            key = (packed & 0xFF) - 1;
            int seq = (packed >> 8) & 0xFF;
            return NewPress(id, seq, key, false) && key >= 0;
        }

        // ---- Keypad3D / ROT_Keypad --------------------------------------------------------------------------

        public static void PackCode(ulong id, string code, out int i0, out int i1, out int i2, out int i3)
        {
            i0 = i1 = i2 = 0;
            int len = 0;
            if (!string.IsNullOrEmpty(code))
            {
                for (int i = 0; i < code.Length && len < MaxDigits; i++)
                {
                    int d = code[i] - '0';
                    if (d < 0 || d > 9) continue;
                    int sh = 4 * (len % 8);
                    if (len < 8) i0 |= d << sh;
                    else if (len < 16) i1 |= d << sh;
                    else i2 |= d << sh;
                    len++;
                }
            }
            Op o;
            _op.TryGetValue(id, out o);
            int key = o.Key >= 0 && o.Key <= 11 && o.Seq != 0 ? o.Key + 1 : 0;
            i3 = len | (key << 8) | ((o.Seq & 0xFF) << 13) | ((o.Wrong ? 1 : 0) << 21);
            _code[id] = code ?? "";
        }

        static string UnpackCode(PuzzleStateEntry e)
        {
            int len = e.Int3 & 0xFF;
            if (len > MaxDigits) len = MaxDigits;
            var sb = new System.Text.StringBuilder(len);
            for (int i = 0; i < len; i++)
            {
                int v = i < 8 ? e.Int0 : i < 16 ? e.Int1 : e.Int2;
                sb.Append((char)('0' + ((v >> (4 * (i % 8))) & 0xF)));
            }
            return sb.ToString();
        }

        /// <summary>True when e carries a press this peer has not replayed yet (and records it).</summary>
        static bool NewPress(ulong id, int seq, int key, bool wrong)
        {
            Op o;
            bool have = _op.TryGetValue(id, out o);
            if (have && o.Seq == seq) return false;
            _op[id] = new Op { Seq = seq, Key = key, Wrong = wrong };
            // Every peer in the scene receives every press, so after the first one this keypad is known. First
            // sight of a later seq (join dump) is state catching up, not a press happening now.
            return seq != 0 && (have || seq == 1);
        }

        public static void ApplyKeypad3D(Keypad3D x, PuzzleStateEntry e)
        {
            if (x == null) return;
            ulong id = unchecked((ulong)e.WorldId);
            string code = UnpackCode(e);
            int key = ((e.Int3 >> 8) & 0x1F) - 1;
            int seq = (e.Int3 >> 13) & 0xFF;
            bool wrong = ((e.Int3 >> 21) & 1) != 0;
            bool press = NewPress(id, seq, key, wrong);
            _code[id] = code;
            try { if (x.code != code) x.code = code; } catch (System.Exception ex) { Guard.Swallow(ex); }
            if (!press || !PuzzleFx.LiveApply || key < 0) return;
            try
            {
                var keys = x.Keys;
                if (keys != null && key < keys.Length && keys[key] != null)
                    PuzzleFx.Run(x, x.pushButton(keys[key].transform));
                if (key == KeySubmit)
                {
                    if (wrong)
                        PuzzleFx.Run(x, x.verify("\u0001")); // never the solution: native red blink + Denied
                    else if (e.Bool0)
                    {
                        if (x.green != null) x.green.enabled = true;
                        PuzzleFx.Press(x, x.greenSFX);
                    }
                }
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            // verify() cleared code; the shared code is what the entry says.
            try { if (x.code != code) x.code = code; } catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        public static void ApplyRotKeypad(ROT_Keypad x, PuzzleStateEntry e)
        {
            if (x == null) return;
            ulong id = unchecked((ulong)e.WorldId);
            string code = UnpackCode(e);
            int key = ((e.Int3 >> 8) & 0x1F) - 1;
            int seq = (e.Int3 >> 13) & 0xFF;
            bool wrong = ((e.Int3 >> 21) & 1) != 0;
            bool press = NewPress(id, seq, key, wrong);
            _code[id] = code;
            try { if (x.code != code) x.code = code; } catch (System.Exception ex) { Guard.Swallow(ex); }
            if (!press || !PuzzleFx.LiveApply || key < 0) return;
            try
            {
                var keys = x.Keys;
                if (keys != null && key < keys.Length && keys[key] != null)
                    PuzzleFx.Run(x, x.pushButton(keys[key].transform));
                if (key == KeySubmit)
                {
                    if (wrong)
                        PuzzleFx.Run(x, x.verify("\u0001")); // native fail sound + red blink
                    else if (e.Bool0)
                    {
                        // The solve itself (onSuccess) runs from ApplyRotKeypad's live edge; this is the light + chime.
                        if (x.green != null) x.green.enabled = true;
                        PuzzleFx.Press(x, x.open);
                    }
                }
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
            try { if (x.code != code) x.code = code; } catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        /// <summary>Native OnEnable cleared the code (chunk remount): put the shared one back.</summary>
        public static void RestoreCode(Component pad, System.Func<string> get, System.Action<string> set)
        {
            if (pad == null || !NetGate.Party) return;
            ulong id = WorldId.FromGameObject(pad.gameObject);
            string code;
            if (id == 0 || !_code.TryGetValue(id, out code) || string.IsNullOrEmpty(code)) return;
            try { if (get() != code) set(code); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        // ---- PEN_Codepad ------------------------------------------------------------------------------------

        public static int PackWheels(PEN_Codepad x, ulong id, out int i3)
        {
            int v = 0;
            try
            {
                var input = x.input;
                if (input != null)
                    for (int i = 0; i < 6 && i < input.Length; i++)
                        v |= (input[i] & 7) << (3 * i);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            Op o;
            _op.TryGetValue(id, out o);
            i3 = (o.Seq != 0 ? o.Key & 0xFF : 0) | ((o.Seq & 0xFF) << 8);
            _wheels[id] = v;
            return v;
        }

        /// <summary>UpdateDigitDiplay(i, up) argument → op (i 0..5 = wheel, 6 = reset, 7 = submit).</summary>
        public static int CodepadOp(int i, bool up)
        {
            if (i >= 0 && i < 6) return up ? 1 + i : 7 + i;
            if (i == 6) return OpReset;
            if (i == 7) return OpSubmit;
            return 0;
        }

        public static void ApplyCodepad(PEN_Codepad x, PuzzleStateEntry e)
        {
            if (x == null) return;
            ulong id = unchecked((ulong)e.WorldId);
            int op = e.Int3 & 0xFF;
            int seq = (e.Int3 >> 8) & 0xFF;
            bool press = NewPress(id, seq, op, false);
            _wheels[id] = e.Int0;
            bool replay = press && PuzzleFx.LiveApply && op != 0;
            try
            {
                var input = x.input;
                if (input == null) return;
                for (int i = 0; i < 6 && i < input.Length; i++)
                {
                    int want = (e.Int0 >> (3 * i)) & 7;
                    bool animated = replay && (op == 1 + i || op == 7 + i);
                    if (input[i] == want && !animated) continue;
                    input[i] = want;
                    // The wheel flip coroutine draws digits[input[i]] at its end; otherwise draw it now.
                    if (!animated && !replay) SetDigit(x, i, want);
                }
                if (!replay)
                    return;
                if (op >= 1 && op <= 12)
                {
                    int w = op <= 6 ? op - 1 : op - 7;
                    PuzzleFx.Run(x, x.UpdateDigitDiplay(w, op <= 6));
                }
                else if (op == OpReset)
                {
                    for (int i = 0; i < 6; i++) PuzzleFx.Run(x, x.ResetDigitDiplay(i));
                    PuzzleFx.Run(x, x.UpdateDigitDiplay(6, true));
                }
                else if (op == OpSubmit)
                {
                    PuzzleFx.Run(x, x.UpdateDigitDiplay(7, true));
                    PuzzleFx.Run(x, x.Buzz(e.Bool0));
                }
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        /// <summary>Native OnEnable zeroed the wheels (chunk remount): put the shared values back.</summary>
        public static void RestoreWheels(PEN_Codepad x)
        {
            if (x == null || !NetGate.Party) return;
            ulong id = WorldId.FromGameObject(x.gameObject);
            int v;
            if (id == 0 || !_wheels.TryGetValue(id, out v) || v == 0) return;
            try
            {
                var input = x.input;
                if (input == null) return;
                for (int i = 0; i < 6 && i < input.Length; i++)
                {
                    int want = (v >> (3 * i)) & 7;
                    input[i] = want;
                    SetDigit(x, i, want);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void SetDigit(PEN_Codepad x, int i, int v)
        {
            try
            {
                var displays = x.displays;
                var digits = x.digits;
                if (displays == null || digits == null || i >= displays.Length || v >= digits.Length) return;
                if (displays[i] != null) displays[i].sprite = digits[v];
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
