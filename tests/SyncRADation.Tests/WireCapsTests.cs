using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using LiteNetLib.Utils;
using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>Writers clamp to the reader caps; readers throw (InvalidDataException) rather than misparse.</summary>
    public class WireCapsTests
    {
        private const int Sentinel = 0x5EEDF00D;

        // Arrays whose reader uses NetWire.ReadCount (int count) - BonePose has its own ushort/skip rule, covered separately.
        public static IEnumerable<object[]> CappedFields() =>
            WireFuzz.ArrayCaps.Keys.Where(k => k != "BonePoseMessage.Eulers").OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

        private static (Type type, FieldInfo field, ArrayCap cap) Resolve(string key)
        {
            string[] p = key.Split('.');
            Type t = RoundTripTests.ByName(p[0]);
            return (t, t.GetField(p[1]), WireFuzz.ArrayCaps[key]);
        }

        /// <summary>Message with the given array field set to exactly n random elements; everything else default/empty.</summary>
        private static object WithCount(Type t, FieldInfo f, ArrayCap cap, int n, Random rng)
        {
            object msg = WireFuzz.Fill(t, rng, FillMode.Empty);
            Type et = f.FieldType.GetElementType();
            var arr = Array.CreateInstance(et, n);
            for (int i = 0; i < n; i++)
            {
                object el = et == typeof(float) ? (object)(float)(rng.NextDouble() * 360.0) : WireFuzz.Fill(et, rng, FillMode.Random);
                arr.SetValue(el, i);
            }
            f.SetValue(msg, arr);
            return msg;
        }

        private static void ResetWarnOnce()
        {
            FieldInfo f = typeof(NetWire).GetField("Warned", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(f); // NetWire.Warned renamed? update this helper
            ((HashSet<string>)f.GetValue(null)).Clear();
            ModRuntime.Log.Warnings.Clear();
        }

        // ------------------------------------------------------------------ writer side: clamp, never misparse

        [Theory, MemberData(nameof(CappedFields))]
        public void Over_cap_writer_clamps_to_cap_and_keeps_the_stream_aligned(string key)
        {
            var (t, f, cap) = Resolve(key);
            ResetWarnOnce();
            object msg = WithCount(t, f, cap, cap.Cap + 17, new Random(3));

            var w = WireFuzz.Write(msg);
            w.Put(Sentinel);
            var r = new NetDataReader(w.CopyData());
            object back = WireFuzz.Read(t, r);

            Array sent = (Array)f.GetValue(msg), got = (Array)f.GetValue(back);
            Assert.Equal(cap.Cap, got.Length);
            for (int i = 0; i < got.Length; i += Math.Max(1, got.Length / 64)) // spot-check that the kept prefix is the FIRST cap elements
            {
                object a = sent.GetValue(i), b = got.GetValue(i);
                if (a is float fa) Assert.InRange(Math.Abs(fa - (float)b), 0f, 0.0056f);
                else Assert.True(WireFuzz.Diff(a, b).Count == 0, key + " element " + i);
            }
            Assert.Equal(Sentinel, r.GetInt());
            Assert.True(r.EndOfData);
            Assert.Contains(ModRuntime.Log.Warnings, m => m.Contains("exceeds wire cap"));
        }

        [Theory, MemberData(nameof(CappedFields))]
        public void Exactly_cap_elements_round_trips_and_cap_plus_one_is_clamped(string key)
        {
            var (t, f, cap) = Resolve(key);
            object atCap = WithCount(t, f, cap, cap.Cap, new Random(4));
            object back = WireFuzz.RoundTrip(atCap, out NetDataReader r, out _);
            Assert.Equal(cap.Cap, ((Array)f.GetValue(back)).Length);
            Assert.True(r.EndOfData);

            object over = WithCount(t, f, cap, cap.Cap + 1, new Random(4));
            back = WireFuzz.RoundTrip(over, out r, out _);
            Assert.Equal(cap.Cap, ((Array)f.GetValue(back)).Length);
            Assert.True(r.EndOfData);
        }

        // ------------------------------------------------------------------ reader side: out-of-range count is a hard error

        [Theory, MemberData(nameof(CappedFields))]
        public void Reader_rejects_count_above_cap_and_negative_count(string key)
        {
            var (t, f, cap) = Resolve(key);
            byte[] empty = WireFuzz.Write(WithCount(t, f, cap, 0, new Random(1))).CopyData();
            byte[] one = WireFuzz.Write(WithCount(t, f, cap, 1, new Random(1))).CopyData();
            int at = Enumerable.Range(0, empty.Length).First(i => i >= one.Length || empty[i] != one[i]); // first byte of the count field
            bool isByteCount = key == "PlayerRosterMessage.PlayerIds" || key.EndsWith(".Stats", StringComparison.Ordinal);

            byte[] tooBig = (byte[])empty.Clone();
            if (isByteCount) tooBig[at] = (byte)(cap.Cap + 1);
            else BitConverter.GetBytes(cap.Cap + 1).CopyTo(tooBig, at);
            Assert.Throws<InvalidDataException>(() => WireFuzz.Read(t, new NetDataReader(tooBig)));

            if (!isByteCount)
            {
                byte[] negative = (byte[])empty.Clone();
                BitConverter.GetBytes(-1).CopyTo(negative, at);
                Assert.Throws<InvalidDataException>(() => WireFuzz.Read(t, new NetDataReader(negative)));

                byte[] huge = (byte[])empty.Clone();
                BitConverter.GetBytes(int.MaxValue).CopyTo(huge, at);
                Assert.Throws<InvalidDataException>(() => WireFuzz.Read(t, new NetDataReader(huge)));
            }

            // the unpatched packets still parse: the offset really was the count field
            WireFuzz.Read(t, new NetDataReader(empty));
            WireFuzz.Read(t, new NetDataReader(one));
        }

        // ------------------------------------------------------------------ strings

        [Fact]
        public void PutString_truncates_to_MaxString_and_does_not_throw()
        {
            ResetWarnOnce();
            string over = new string('a', NetWire.MaxString + 500);
            var w = new NetDataWriter();
            NetWire.PutString(w, over);
            w.Put(Sentinel);
            var r = new NetDataReader(w.CopyData());
            string got = r.GetString();
            Assert.Equal(NetWire.MaxString, got.Length);
            Assert.Equal(over.Substring(0, NetWire.MaxString), got);
            Assert.Equal(Sentinel, r.GetInt());
            Assert.Contains(ModRuntime.Log.Warnings, m => m.Contains("truncated"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(NetWire.MaxString - 1)]
        [InlineData(NetWire.MaxString)]
        public void PutString_keeps_strings_up_to_MaxString_exactly_even_at_3_bytes_per_char(int len)
        {
            string s = new string('€', len); // worst-case UTF-8 width, must stay under LiteNetLib's ushort prefix
            var w = new NetDataWriter();
            NetWire.PutString(w, s);
            Assert.Equal(s, new NetDataReader(w.CopyData()).GetString());
        }

        [Fact]
        public void PutString_null_becomes_empty()
        {
            var w = new NetDataWriter();
            NetWire.PutString(w, null);
            Assert.Equal("", new NetDataReader(w.CopyData()).GetString());
        }

        [Fact]
        public void Party_Room_and_Scene_strings_use_the_capped_writer_and_truncate_instead_of_throwing()
        {
            // PartyLife/PartyRoom write Room (and PartyLife Scene) through NetWire.PutString: an absurdly long value is
            // truncated to MaxString chars instead of throwing OverflowException inside Serialize.
            string tooLong = new string('r', 70000);
            var room = (PartyRoomMessage)WireFuzz.RoundTrip(new PartyRoomMessage { PlayerId = 7, Room = tooLong }, out var r1, out _);
            Assert.Equal(NetWire.MaxString, room.Room.Length);
            Assert.True(r1.EndOfData);

            var life = (PartyLifeMessage)WireFuzz.RoundTrip(new PartyLifeMessage { Room = tooLong, Scene = tooLong, SaveSlot = 3 }, out var r2, out _);
            Assert.Equal(NetWire.MaxString, life.Room.Length);
            Assert.Equal(NetWire.MaxString, life.Scene.Length);
            Assert.Equal(3, life.SaveSlot); // fields after the strings survive
            Assert.True(r2.EndOfData);

            string ok = new string('r', NetWire.MaxString);
            var back = (PartyRoomMessage)WireFuzz.RoundTrip(new PartyRoomMessage { PlayerId = 7, Room = ok }, out var r, out _);
            Assert.Equal(ok, back.Room);
            Assert.True(r.EndOfData);
        }

        // ------------------------------------------------------------------ long strings (DialoguerXml)

        [Fact]
        public void Long_string_round_trips_above_the_64KB_short_string_limit()
        {
            string xml = "<d>" + new string('x', 200_000) + "€é</d>";
            var msg = new StoryCommitMessage { DialoguerXml = xml, EndingId = 9 };
            var back = (StoryCommitMessage)WireFuzz.RoundTrip(msg, out var r, out _);
            Assert.Equal(xml, back.DialoguerXml);
            Assert.Equal(9, back.EndingId);
            Assert.True(r.EndOfData);
        }

        [Fact]
        public void Long_string_at_exactly_MaxLongStringBytes_is_kept_and_one_byte_more_is_dropped_without_desync()
        {
            ResetWarnOnce();
            string atCap = new string('a', NetWire.MaxLongStringBytes);
            var ok = (StoryCommitMessage)WireFuzz.RoundTrip(new StoryCommitMessage { DialoguerXml = atCap, EndCircle = 4 }, out var r1, out _);
            Assert.Equal(NetWire.MaxLongStringBytes, ok.DialoguerXml.Length);
            Assert.Equal(4, ok.EndCircle);
            Assert.True(r1.EndOfData);

            string over = new string('a', NetWire.MaxLongStringBytes + 1);
            var dropped = (StoryCommitMessage)WireFuzz.RoundTrip(new StoryCommitMessage { DialoguerXml = over, EndCircle = 5 }, out var r2, out _);
            Assert.Equal("", dropped.DialoguerXml);
            Assert.Equal(5, dropped.EndCircle); // following fields intact
            Assert.True(r2.EndOfData);
            Assert.Contains(ModRuntime.Log.Warnings, m => m.Contains("long string"));
        }

        [Fact]
        public void Long_string_cap_is_measured_in_utf8_bytes_not_chars()
        {
            string s = new string('€', NetWire.MaxLongStringBytes / 3 + 1); // 3 bytes per char -> just over the cap
            Assert.True(Encoding.UTF8.GetByteCount(s) > NetWire.MaxLongStringBytes);
            var back = (StoryCommitMessage)WireFuzz.RoundTrip(new StoryCommitMessage { DialoguerXml = s }, out _, out _);
            Assert.Equal("", back.DialoguerXml);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(NetWire.MaxLongStringBytes + 1)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void GetLongString_rejects_out_of_range_length(int n)
        {
            var w = new NetDataWriter();
            w.Put(n);
            Assert.Throws<InvalidDataException>(() => NetWire.GetLongString(new NetDataReader(w.CopyData())));
        }

        [Fact]
        public void GetLongString_throws_when_payload_is_shorter_than_its_prefix()
        {
            var w = new NetDataWriter();
            w.Put(1000);
            w.Put(new byte[10], 0, 10);
            Assert.ThrowsAny<Exception>(() => NetWire.GetLongString(new NetDataReader(w.CopyData())));
        }

        // ------------------------------------------------------------------ helpers

        [Fact]
        public void ClampCount_passes_through_up_to_cap_and_warns_once_per_key()
        {
            ResetWarnOnce();
            Assert.Equal(5, NetWire.ClampCount(5, 10, "x"));
            Assert.Equal(10, NetWire.ClampCount(10, 10, "x"));
            Assert.Empty(ModRuntime.Log.Warnings);
            Assert.Equal(10, NetWire.ClampCount(11, 10, "x"));
            Assert.Equal(10, NetWire.ClampCount(9999, 10, "x"));
            Assert.Single(ModRuntime.Log.Warnings);
            Assert.Equal(10, NetWire.ClampCount(11, 10, "y"));
            Assert.Equal(2, ModRuntime.Log.Warnings.Count);
        }

        [Fact]
        public void ReadCount_accepts_0_through_cap_and_rejects_the_rest()
        {
            foreach (int ok in new[] { 0, 1, 10 })
            {
                var w = new NetDataWriter(); w.Put(ok);
                Assert.Equal(ok, NetWire.ReadCount(new NetDataReader(w.CopyData()), 10, "x"));
            }
            foreach (int bad in new[] { -1, 11, int.MinValue, int.MaxValue })
            {
                var w = new NetDataWriter(); w.Put(bad);
                Assert.Throws<InvalidDataException>(() => NetWire.ReadCount(new NetDataReader(w.CopyData()), 10, "x"));
            }
        }

        [Fact]
        public void PlayerRoster_reader_rejects_byte_count_above_MaxRoster()
        {
            var w = new NetDataWriter();
            w.Put((byte)(NetWire.MaxRoster + 1));
            for (int i = 0; i <= NetWire.MaxRoster; i++) w.Put(i);
            Assert.Throws<InvalidDataException>(() => PlayerRosterMessage.Deserialize(new NetDataReader(w.CopyData())));
        }

        // ------------------------------------------------------------------ known defect

        // Writer clamps to the reader's 1..1023 chunk so an oversized chunk cannot leave unread ushorts behind.
        [Fact]
        public void BonePose_over_1023_bones_must_not_desync_the_stream()
        {
            var msg = new BonePoseMessage { SenderPlayerId = 1, TotalBones = 2000, StartBone = 0, Eulers = new float[1024 * 3] };
            var w = WireFuzz.Write(msg);
            w.Put(Sentinel);
            var r = new NetDataReader(w.CopyData());
            BonePoseMessage.Deserialize(r);
            Assert.Equal(Sentinel, r.GetInt());
        }

        [Fact]
        public void BonePose_at_the_1023_bone_limit_round_trips()
        {
            var eulers = new float[1023 * 3];
            for (int i = 0; i < eulers.Length; i++) eulers[i] = (i % 360);
            var back = (BonePoseMessage)WireFuzz.RoundTrip(new BonePoseMessage { SenderPlayerId = 1, TotalBones = 1023, StartBone = 0, Eulers = eulers }, out var r, out _);
            Assert.Equal(1023, back.Count);
            Assert.True(r.EndOfData);
        }
    }
}
