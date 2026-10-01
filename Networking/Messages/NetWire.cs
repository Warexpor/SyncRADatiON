// SyncRADation — wire helpers: writer/reader caps, guarded strings, handshake schema hash, channels
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    /// <summary>LiteNetLib channel numbers. Events keep ReliableOrdered on 0; continuous snapshots use 1 (Sequenced).</summary>
    public static class NetChannels
    {
        public const byte Events = 0;
        public const byte State = 1;
        public const byte Count = 2;
    }

    /// <summary>Writers clamp to the same caps the readers enforce; readers throw instead of silently misparsing.</summary>
    public static class NetWire
    {
        /// <summary>UTF-16 chars per short string. 16000 * 3 UTF-8 bytes stays under LiteNetLib's ushort prefix.</summary>
        public const int MaxString = 16000;
        public const int MaxLongStringBytes = 4 * 1024 * 1024;

        public const int MaxEnemies = 512;
        public const int MaxBosses = 64;
        public const int MaxStoryFlags = 8192;
        public const int MaxPuzzleEntries = 8192;
        public const int MaxPickupEntries = 8192;
        public const int MaxStorageItems = 512;
        public const int MaxKeyRing = 512;
        public const int MaxRoster = 32;
        public const int MaxBones = 4095;
        /// <summary>WorldId registry categories carried in SceneHello / SceneFollow (WorldChecksum uses 4).</summary>
        public const int MaxWorldCategories = 8;
        /// <summary>WorldIds per SceneDiff chunk (256 * 8 bytes stays well inside one MTU-fragmented reliable packet).</summary>
        public const int MaxSceneDiffIds = 256;
        /// <summary>Most ids the host lists per category across all chunks of one SceneDiff (the rest is reported as truncated).</summary>
        public const int MaxSceneDiffTotalIds = 2048;

        // Warn-once keys: intentionally persistent (a wire warning is not repeated every session).
        // persistent: warn-once set
        private static readonly HashSet<string> Warned = new HashSet<string>();

        internal static void WarnOnce(string key, string msg)
        {
            if (Warned.Count > 256) Warned.Clear();
            if (Warned.Add(key))
                ModRuntime.Log?.Warning("[Wire] " + msg);
        }

        /// <summary>Clamp a writer-side count to the reader cap (warn once per what).</summary>
        public static int ClampCount(int count, int cap, string what)
        {
            if (count <= cap) return count;
            WarnOnce("cap:" + what, what + " count " + count + " exceeds wire cap " + cap + " — clamped");
            return cap;
        }

        /// <summary>Reader-side count: 0..cap, otherwise the packet is corrupt (caught by dispatch).</summary>
        public static int ReadCount(NetDataReader r, int cap, string what)
        {
            int n = r.GetInt();
            if (n < 0 || n > cap)
                throw new InvalidDataException(what + " count " + n + " outside 0.." + cap);
            return n;
        }

        /// <summary>Short string (names, paths, reasons). Over-long input is truncated instead of throwing in LiteNetLib.</summary>
        public static void PutString(NetDataWriter w, string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                w.Put("");
                return;
            }
            if (s.Length > MaxString)
            {
                WarnOnce("str", "string of " + s.Length + " chars truncated to " + MaxString);
                s = s.Substring(0, MaxString);
            }
            w.Put(s);
        }

        /// <summary>Length-prefixed UTF-8 blob for strings that may exceed 64 KB (DialoguerXml).</summary>
        public static void PutLongString(NetDataWriter w, string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                w.Put(0);
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > MaxLongStringBytes)
            {
                WarnOnce("longstr", "long string of " + bytes.Length + " bytes exceeds " + MaxLongStringBytes + " — dropped");
                w.Put(0);
                return;
            }
            w.Put(bytes.Length);
            w.Put(bytes, 0, bytes.Length);
        }

        public static string GetLongString(NetDataReader r)
        {
            int n = r.GetInt();
            if (n < 0 || n > MaxLongStringBytes)
                throw new InvalidDataException("long string length " + n);
            if (n == 0) return "";
            var bytes = new byte[n];
            r.GetBytes(bytes, n);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>Build/schema fingerprint exchanged in the handshake so mismatched builds are rejected even when ProtocolVersion matches.</summary>
    public static class NetSchema
    {
        // Pure computed caches of this dll's schema: persistent by definition.
        // persistent: computed schema hash of this dll
        private static uint _hash;
        // persistent: computed schema hash of this dll
        private static bool _ready;

        public static string ModVersion => PluginInfo.Version;

        /// <summary>
        /// Handshake fingerprint: structural hash (assembly version, protocol, every wire enum id) plus this
        /// module's MVID. The csproj builds deterministically, so the MVID is a content hash of the dll: two
        /// stale/modified dlls of the same version no longer look identical.
        /// </summary>
        public static uint Hash
        {
            get
            {
                if (!_ready)
                {
                    _hash = Mix(StructuralHash, ModuleId);
                    _ready = true;
                }
                return _hash;
            }
        }

        // persistent: computed schema hash of this dll
        private static uint _structural;
        // persistent: computed schema hash of this dll
        private static bool _structuralReady;

        /// <summary>Hash without the module id (what the tests pin: stable across rebuilds).</summary>
        public static uint StructuralHash
        {
            get
            {
                if (!_structuralReady)
                {
                    _structural = Compute();
                    _structuralReady = true;
                }
                return _structural;
            }
        }

        /// <summary>MVID of the dll the schema lives in ("N" format). Never throws.</summary>
        public static string ModuleId
        {
            get
            {
                try { return typeof(NetSchema).Module.ModuleVersionId.ToString("N"); }
                catch { return "no-mvid"; }
            }
        }

        private static uint Compute()
        {
            uint h = 2166136261u;
            string asmVersion = "";
            try { asmVersion = typeof(NetSchema).Assembly.GetName().Version.ToString(); }
            catch { asmVersion = PluginInfo.Version; }
            h = Mix(h, asmVersion);
            h = Mix(h, PluginInfo.ProtocolVersion);
            h = MixEnum(h, typeof(NetMessageType));
            h = MixEnum(h, typeof(InteractionKind));
            h = MixEnum(h, typeof(StoryCmd));
            h = MixEnum(h, typeof(DeathKind));
            h = MixEnum(h, typeof(DoorType));
            h = MixEnum(h, typeof(PuzzleType));
            h = MixEnum(h, typeof(BossType));
            h = MixEnum(h, typeof(EnemyActionKind));
            h = MixEnum(h, typeof(BossHitKind));
            return h;
        }

        private static uint MixEnum(uint h, Type enumType)
        {
            var values = Enum.GetValues(enumType);
            var ids = new List<long>(values.Length);
            foreach (object v in values)
                ids.Add(Convert.ToInt64(v));
            ids.Sort();
            h = Mix(h, enumType.Name);
            for (int i = 0; i < ids.Count; i++)
                h = Mix(h, (int)ids[i]);
            return h;
        }

        internal static uint Mix(uint h, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            return h;
        }

        private static uint Mix(uint h, int v)
        {
            unchecked
            {
                for (int i = 0; i < 4; i++)
                {
                    h ^= (uint)((v >> (i * 8)) & 0xFF);
                    h *= 16777619u;
                }
            }
            return h;
        }
    }
}
