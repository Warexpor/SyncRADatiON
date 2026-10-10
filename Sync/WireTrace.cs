// [Wire]: one line per message handed to / taken from the network, with its decoded fields. The domain lines say what
// a peer did with a message; this says that it left and that it arrived, for every type, also the ones no domain logs.
// The pose / snapshot streams (WireTrace.Streams) are left to the [Net] line. A burst (a join dump) is capped per type
// per second and the next line of that type carries the dropped count.
// The fields come from the message struct itself (<NetMessageType>Message.Deserialize + its public fields), found by
// reflection once per type: a new message needs nothing here.
using System;
using System.Collections;
using System.Reflection;
using System.Text;
using LiteNetLib.Utils;
using SyncRADation.Config;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class WireTrace
    {
        const int PerSecond = 10;
        const int MaxLine = 340;
        const int MaxArrayShown = 3;

        sealed class Codec
        {
            public MethodInfo Deserialize;
            public FieldInfo[] Fields;
        }

        // persistent: reflection cache, one entry per message type
        static readonly Codec[] _codecs = new Codec[256];
        // persistent: reflection cache flags
        static readonly bool[] _codecTried = new bool[256];
        // persistent: reflection cache of nested entry structs
        static readonly System.Collections.Generic.Dictionary<Type, FieldInfo[]> _fieldsOf =
            new System.Collections.Generic.Dictionary<Type, FieldInfo[]>();
        // persistent: constant table
        static readonly bool[] Streams = BuildStreams();

        // Burst cap: [dir * 256 + type]. Cleared by Reset.
        static readonly float[] _windowAt = new float[512];
        static readonly int[] _inWindow = new int[512];
        static readonly int[] _dropped = new int[512];

        // A broadcast hands the same writer to every peer in one frame: one line.
        static NetDataWriter _lastTx;
        static int _lastTxFrame = -1;
        static int _lastTxLength;

        static readonly NetDataReader _peek = new NetDataReader();
        static readonly StringBuilder _sb = new StringBuilder(MaxLine + 64);

        static bool[] BuildStreams()
        {
            var s = new bool[256];
            s[(int)NetMessageType.PlayerState] = true;
            s[(int)NetMessageType.BonePose] = true;
            s[(int)NetMessageType.EnemyState] = true;
            s[(int)NetMessageType.BossState] = true;
            s[(int)NetMessageType.PlayerVital] = true;
            s[(int)NetMessageType.WorldDigest] = true;
            return s;
        }

        public static void Reset()
        {
            Array.Clear(_windowAt, 0, 512);
            Array.Clear(_inWindow, 0, 512);
            Array.Clear(_dropped, 0, 512);
            _lastTx = null;
            _lastTxFrame = -1;
        }

        /// <summary>A packet handed to a peer (the writer's first byte is its NetMessageType).</summary>
        public static void Tx(NetDataWriter w)
        {
            if (!ModConfig.WireTraceOn || w == null || w.Length == 0) return;
            byte type = w.Data[0];
            if (Streams[type]) return;
            try
            {
                int frame = Time.frameCount;
                if (ReferenceEquals(w, _lastTx) && frame == _lastTxFrame && w.Length == _lastTxLength) return;
                _lastTx = w;
                _lastTxFrame = frame;
                _lastTxLength = w.Length;
                int dropped;
                if (!Admit(type, out dropped)) return;
                _peek.SetSource(w.Data, 1, w.Length);
                Write("tx", type, w.Length, -1, dropped);
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        /// <summary>A packet from a handshaken peer, before its handler runs; the reader is left where it was.</summary>
        public static void Rx(byte type, NetDataReader reader, int fromPlayer)
        {
            if (!ModConfig.WireTraceOn || reader == null || Streams[type]) return;
            try
            {
                int dropped;
                if (!Admit(256 + type, out dropped)) return;
                _peek.SetSource(reader.RawData, reader.Position, reader.RawDataSize);
                Write("rx", type, reader.AvailableBytes + 1, fromPlayer, dropped);
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        static bool Admit(int slot, out int dropped)
        {
            dropped = 0;
            float now = Time.unscaledTime;
            if (now - _windowAt[slot] >= 1f)
            {
                _windowAt[slot] = now;
                _inWindow[slot] = 0;
            }
            if (_inWindow[slot] >= PerSecond)
            {
                _dropped[slot]++;
                return false;
            }
            _inWindow[slot]++;
            dropped = _dropped[slot];
            _dropped[slot] = 0;
            return true;
        }

        static void Write(string dir, byte type, int bytes, int fromPlayer, int dropped)
        {
            _sb.Length = 0;
            _sb.Append(dir);
            if (fromPlayer >= 0) _sb.Append(" p").Append(fromPlayer);
            _sb.Append(' ').Append((NetMessageType)type).Append(' ').Append(bytes).Append('B');
            var codec = CodecOf(type);
            if (codec != null)
            {
                object msg = null;
                try { msg = codec.Deserialize.Invoke(null, new object[] { _peek }); }
                catch (Exception) { _sb.Append(" (undecodable)"); }
                if (msg != null)
                {
                    _sb.Append(' ');
                    Fields(msg, codec.Fields, 0);
                }
            }
            if (_sb.Length > MaxLine)
            {
                _sb.Length = MaxLine;
                _sb.Append("...");
            }
            if (dropped > 0) _sb.Append(" (+").Append(dropped).Append(" suppressed)");
            // Straight to the sink: two equal messages are two lines, never a collapsed repeat.
            ModRuntime.Log?.Msg("[Wire] " + _sb);
        }

        static Codec CodecOf(byte type)
        {
            if (_codecTried[type]) return _codecs[type];
            _codecTried[type] = true;
            try
            {
                string name = ((NetMessageType)type).ToString() + "Message";
                Type t = typeof(NetMessageType).Assembly.GetType("SyncRADation.Networking." + name, false);
                if (t == null) return null;
                MethodInfo de = t.GetMethod(nameof(HandshakeMessage.Deserialize), BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(NetDataReader) }, null);
                if (de == null) return null;
                _codecs[type] = new Codec { Deserialize = de, Fields = FieldsOf(t) };
            }
            catch (Exception e) { Guard.Swallow(e); }
            return _codecs[type];
        }

        static FieldInfo[] FieldsOf(Type t)
        {
            FieldInfo[] f;
            if (_fieldsOf.TryGetValue(t, out f)) return f;
            f = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            _fieldsOf[t] = f;
            return f;
        }

        static void Fields(object o, FieldInfo[] fields, int depth)
        {
            for (int i = 0; i < fields.Length && _sb.Length <= MaxLine; i++)
            {
                if (i > 0) _sb.Append(' ');
                _sb.Append(fields[i].Name).Append('=');
                object v = fields[i].GetValue(o);
                // A WorldId rides the wire as a long: print it the way every other line does.
                if (v is long && fields[i].Name.EndsWith("Id", StringComparison.Ordinal))
                    _sb.Append(unchecked((ulong)(long)v).ToString("X16"));
                else
                    Value(v, depth);
            }
        }

        static void Value(object v, int depth)
        {
            if (v == null) { _sb.Append('-'); return; }
            if (v is ulong) { _sb.Append(((ulong)v).ToString("X16")); return; }
            if (v is float) { _sb.Append(((float)v).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)); return; }
            var s = v as string;
            if (s != null)
            {
                _sb.Append('\'').Append(s.Length > 48 ? s.Substring(0, 48) + "..." : s).Append('\'');
                return;
            }
            var bytes = v as byte[];
            if (bytes != null) { _sb.Append('[').Append(bytes.Length).Append("B]"); return; }
            var list = v as IList;
            if (list != null)
            {
                _sb.Append('[').Append(list.Count).Append(']');
                if (list.Count == 0 || depth > 0) return;
                _sb.Append('{');
                for (int i = 0; i < list.Count && i < MaxArrayShown && _sb.Length <= MaxLine; i++)
                {
                    if (i > 0) _sb.Append(" | ");
                    Value(list[i], depth + 1);
                }
                if (list.Count > MaxArrayShown) _sb.Append(" | ...");
                _sb.Append('}');
                return;
            }
            Type t = v.GetType();
            if (t.IsPrimitive || t.IsEnum) { _sb.Append(v); return; }
            if (t.IsValueType && depth < 2 && t.Namespace == "SyncRADation.Networking")
            {
                _sb.Append('(');
                Fields(v, FieldsOf(t), depth + 1);
                _sb.Append(')');
                return;
            }
            _sb.Append(v);
        }
    }
}
