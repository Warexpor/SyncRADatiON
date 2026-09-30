// Reflection-driven message fuzzing: discovers every wire struct, fills it with random data, round-trips
// it through NetDataWriter/NetDataReader and compares field by field. A new message or a new field is
// covered automatically; a new ARRAY field must be registered in ArrayCaps or the suite fails loudly.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using LiteNetLib.Utils;
using SyncRADation.Networking;

namespace SyncRADation.Tests
{
    internal enum FillMode
    {
        Random,   // random scalars, small random arrays, mixed-length strings
        Max,      // arrays at wire cap, top-level strings at NetWire.MaxString, extreme ints
        Empty,    // zero-length arrays, empty strings, zero scalars
        Default,  // default(T): null strings / null arrays
    }

    internal readonly struct ArrayCap
    {
        public readonly int Cap;        // max element count the writer/reader accept
        public readonly int Multiple;   // element count must be a multiple of this (BonePose Eulers)
        public readonly bool Angles;    // floats are quantized 0..360 angles (ushort) -> tolerance compare
        public ArrayCap(int cap, int multiple = 1, bool angles = false) { Cap = cap; Multiple = multiple; Angles = angles; }
    }

    internal static class WireFuzz
    {
        /// <summary>Every array-typed field on a wire struct. Key: "TypeName.Field".</summary>
        public static readonly Dictionary<string, ArrayCap> ArrayCaps = new Dictionary<string, ArrayCap>
        {
            ["SceneHelloMessage.Stats"] = new ArrayCap(NetWire.MaxWorldCategories),
            ["SceneFollowMessage.Stats"] = new ArrayCap(NetWire.MaxWorldCategories),
            ["SceneDiffMessage.Ids"] = new ArrayCap(NetWire.MaxSceneDiffIds),
            ["PlayerRosterMessage.PlayerIds"] = new ArrayCap(NetWire.MaxRoster),
            ["WorldPickupStateMessage.Entries"] = new ArrayCap(NetWire.MaxPickupEntries),
            ["PlayerStateMessage.BoneRotations"] = new ArrayCap(NetWire.MaxBones, 1, true),
            // BonePose reader accepts 1..1023 bones, each 3 euler angles.
            ["BonePoseMessage.Eulers"] = new ArrayCap(1023 * 3, 3, true),
            ["EnemyStateMessage.Enemies"] = new ArrayCap(NetWire.MaxEnemies),
            ["PuzzleStateMessage.Entries"] = new ArrayCap(NetWire.MaxPuzzleEntries),
            ["BossStateMessage.Bosses"] = new ArrayCap(NetWire.MaxBosses),
            ["StoryCommitMessage.Flags"] = new ArrayCap(NetWire.MaxStoryFlags),
            ["StorageBoxBlobMessage.Items"] = new ArrayCap(NetWire.MaxStorageItems),
            ["PartyKeyRingMessage.ItemEnums"] = new ArrayCap(NetWire.MaxKeyRing),
        };

        public static Type[] AllWireTypes() =>
            typeof(NetWire).Assembly.GetTypes()
                .Where(t => t.IsValueType && !t.IsEnum && t.Namespace == "SyncRADation.Networking" && IsWireType(t))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();

        /// <summary>Top-level messages (the ones carried in packets), i.e. named *Message.</summary>
        public static Type[] AllMessageTypes() =>
            AllWireTypes().Where(t => t.Name.EndsWith("Message", StringComparison.Ordinal)).ToArray();

        public static bool IsWireType(Type t) =>
            t.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(NetDataWriter) }, null) != null &&
            t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(NetDataReader) }, null) != null;

        // ---------------------------------------------------------------- serialize / deserialize

        public static NetDataWriter Write(object msg)
        {
            var w = new NetDataWriter();
            Invoke(msg.GetType().GetMethod("Serialize", new[] { typeof(NetDataWriter) }), msg, w);
            return w;
        }

        public static object Read(Type t, NetDataReader r) =>
            Invoke(t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(NetDataReader) }, null), null, r);

        /// <summary>Serialize then deserialize from an exact-size copy (so over-reads fail instead of reading spare capacity).</summary>
        public static object RoundTrip(object msg, out NetDataReader reader, out int length)
        {
            byte[] data = Write(msg).CopyData();
            length = data.Length;
            reader = new NetDataReader(data);
            return Read(msg.GetType(), reader);
        }

        private static object Invoke(MethodInfo m, object target, object arg)
        {
            try { return m.Invoke(target, new[] { arg }); }
            catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }

        // ---------------------------------------------------------------- filling

        public static object Fill(Type t, Random rng, FillMode mode) => Make(t, rng, mode, t.Name, null, 0);

        private static object Make(Type t, Random rng, FillMode mode, string owner, string field, int depth)
        {
            if (mode == FillMode.Default) return t.IsValueType ? DefaultStruct(t) : null;

            if (t == typeof(string)) return MakeString(rng, mode, depth);
            if (t.IsEnum) return Enum.ToObject(t, MakeIntegral(Enum.GetUnderlyingType(t), rng, mode));
            if (t.IsPrimitive) return t == typeof(bool) ? (object)(mode == FillMode.Empty ? false : rng.Next(2) == 1)
                                   : t == typeof(float) ? MakeFloat(rng, mode, angle: false)
                                   : MakeIntegral(t, rng, mode);

            if (t.IsArray)
            {
                string key = owner + "." + field;
                if (!ArrayCaps.TryGetValue(key, out ArrayCap cap))
                    throw new InvalidOperationException("Array field " + key + " has no entry in WireFuzz.ArrayCaps. Register its wire cap (and add cap tests).");
                Type et = t.GetElementType();
                int n = mode == FillMode.Max ? cap.Cap
                      : mode == FillMode.Empty ? 0
                      : rng.Next(0, Math.Min(cap.Cap, 24) + 1);
                n -= n % cap.Multiple;
                var arr = Array.CreateInstance(et, n);
                for (int i = 0; i < n; i++)
                    arr.SetValue(et == typeof(float) && cap.Angles ? MakeFloat(rng, mode, angle: true) : Make(et, rng, mode, et.Name, null, depth + 1), i);
                return arr;
            }

            if (t.IsValueType)
            {
                object boxed = DefaultStruct(t);
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    f.SetValue(boxed, Make(f.FieldType, rng, mode, t.Name, f.Name, depth));
                return boxed;
            }

            throw new NotSupportedException("WireFuzz cannot fill " + t);
        }

        private static object DefaultStruct(Type t) => Activator.CreateInstance(t);

        private static string MakeString(Random rng, FillMode mode, int depth)
        {
            if (mode == FillMode.Empty) return "";
            if (mode == FillMode.Max) return depth == 0 ? Repeat("Zyé€", NetWire.MaxString) : "max" + rng.Next(1000);
            int kind = rng.Next(10);
            if (kind == 0) return "";
            if (kind == 1 && depth == 0) return Repeat("€", NetWire.MaxString); // 3 UTF-8 bytes per char, worst case
            return RandomText(rng, rng.Next(1, 120));
        }

        private static string Repeat(string unit, int chars)
        {
            var sb = new StringBuilder(chars);
            while (sb.Length < chars) sb.Append(unit);
            sb.Length = chars;
            return sb.ToString();
        }

        public static string RandomText(Random rng, int len)
        {
            var sb = new StringBuilder(len);
            while (sb.Length < len)
            {
                switch (rng.Next(5))
                {
                    case 0: sb.Append((char)rng.Next(0x20, 0x7F)); break;
                    case 1: sb.Append((char)rng.Next(0xC0, 0x250)); break;
                    case 2: sb.Append((char)rng.Next(0x4E00, 0x9FFF)); break;
                    case 3: sb.Append('/').Append('_'); break;
                    default: sb.Append(char.ConvertFromUtf32(rng.Next(0x1F300, 0x1F64F))); break; // surrogate pair
                }
            }
            return sb.ToString(0, len > 0 && char.IsHighSurrogate(sb[len - 1]) ? len - 1 : len);
        }

        private static readonly float[] SpecialFloats =
        {
            0f, -0f, 1f, -1f, float.MaxValue, float.MinValue, float.Epsilon, float.PositiveInfinity, float.NegativeInfinity, float.NaN,
        };

        private static object MakeFloat(Random rng, FillMode mode, bool angle)
        {
            if (mode == FillMode.Empty) return 0f;
            if (angle) return mode == FillMode.Max ? (rng.Next(2) == 0 ? 0f : 360f) : (float)(rng.NextDouble() * 360.0);
            if (mode == FillMode.Max || rng.Next(6) == 0) return SpecialFloats[rng.Next(SpecialFloats.Length)];
            return (float)((rng.NextDouble() - 0.5) * 4000.0);
        }

        private static object MakeIntegral(Type t, Random rng, FillMode mode)
        {
            long raw;
            if (mode == FillMode.Empty) raw = 0;
            else if (mode == FillMode.Max) raw = rng.Next(2) == 0 ? long.MinValue : long.MaxValue; // truncates to min/max-ish bit patterns
            else
            {
                switch (rng.Next(5))
                {
                    case 0: raw = 0; break;
                    case 1: raw = -1; break;
                    case 2: raw = rng.Next(-3, 4); break;
                    default:
                        var buf = new byte[8];
                        rng.NextBytes(buf);
                        raw = BitConverter.ToInt64(buf, 0);
                        break;
                }
            }
            unchecked
            {
                if (t == typeof(byte)) return (byte)raw;
                if (t == typeof(sbyte)) return (sbyte)raw;
                if (t == typeof(short)) return (short)raw;
                if (t == typeof(ushort)) return (ushort)raw;
                if (t == typeof(int)) return (int)raw;
                if (t == typeof(uint)) return (uint)raw;
                if (t == typeof(long)) return raw;
                if (t == typeof(ulong)) return (ulong)raw;
            }
            throw new NotSupportedException("integral " + t);
        }

        // ---------------------------------------------------------------- comparison

        /// <summary>Field-by-field diff. null == "" for strings and null == empty for arrays (the wire cannot tell them apart).</summary>
        public static List<string> Diff(object expected, object actual)
        {
            var diffs = new List<string>();
            Compare(expected, actual, expected.GetType().Name, diffs, false);
            return diffs;
        }

        private static readonly float AngleTolerance = 360f / 65535f * 1.01f;

        private static void Compare(object a, object b, string path, List<string> diffs, bool angle)
        {
            if (a == null && b == null) return;
            if (a is string || b is string)
            {
                if (((string)a ?? "") != ((string)b ?? "")) diffs.Add(path + ": string mismatch (len " + ((string)a ?? "").Length + " vs " + ((string)b ?? "").Length + ")");
                return;
            }
            if (a is Array || b is Array)
            {
                var x = (Array)a; var y = (Array)b;
                int nx = x?.Length ?? 0, ny = y?.Length ?? 0;
                if (nx != ny) { diffs.Add(path + ": length " + nx + " vs " + ny); return; }
                for (int i = 0; i < nx; i++) Compare(x.GetValue(i), y.GetValue(i), path + "[" + i + "]", diffs, angle);
                return;
            }
            Type t = (a ?? b).GetType();
            if (t == typeof(float))
            {
                float fa = (float)a, fb = (float)b;
                bool same = angle ? Math.Abs(fa - fb) <= AngleTolerance : BitConverter.SingleToInt32Bits(fa) == BitConverter.SingleToInt32Bits(fb);
                if (!same) diffs.Add(path + ": " + fa + " vs " + fb);
                return;
            }
            if (t.IsPrimitive || t.IsEnum)
            {
                if (!Equals(a, b)) diffs.Add(path + ": " + a + " vs " + b);
                return;
            }
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                bool ang = f.FieldType == typeof(float[]) && ArrayCaps.TryGetValue(t.Name + "." + f.Name, out ArrayCap c) && c.Angles;
                Compare(f.GetValue(a), f.GetValue(b), path + "." + f.Name, diffs, ang);
            }
        }
    }
}
