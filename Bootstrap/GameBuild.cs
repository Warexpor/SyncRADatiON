// Fingerprint of the SIGNALIS build this process runs, exchanged in the handshake so two players on
// different game patches (or a cracked / modded GameAssembly) are refused with a clear reason instead of
// desyncing quietly. Deterministic across two installs of the same build: no mtime, no install path.
//   hash = FNV-1a over  Application.version | Application.unityVersion | GameAssembly.dll length
//                       | first 1 MB | last 1 MB of GameAssembly.dll
// Computed once at boot (~2 MB read). Hash 0 = unknown (file missing / unreadable): the check is skipped.
using System;
using System.IO;
using UnityEngine;

namespace SyncRADation
{
    public static class GameBuild
    {
        private const int Window = 1024 * 1024;

        // Boot-time constants (computed once in ModRuntime.Start): persistent by definition.
        /// <summary>0 when the build could not be fingerprinted (handshake then skips the comparison).</summary>
        // persistent: per-process game build fingerprint
        public static uint Hash { get; private set; }

        /// <summary>Short label for logs / F2 / reject reasons, e.g. "1.0.3 / Unity 2021.3.12f1 #1A2B3C4D".</summary>
        // persistent: per-process game build fingerprint
        public static string Label { get; private set; } = "unknown";

        /// <summary>Label without the hash (what goes on the wire for display).</summary>
        // persistent: per-process game build fingerprint
        public static string Version { get; private set; } = "unknown";

        public static void Compute()
        {
            try
            {
                string appVersion = SafeVersion(() => Application.version);
                string unity = SafeVersion(() => Application.unityVersion);
                Version = appVersion + " / Unity " + unity;

                uint h = 2166136261u;
                h = Mix(h, appVersion);
                h = Mix(h, unity);

                string path = FindGameAssembly();
                bool hashed = false;
                if (path != null)
                {
                    hashed = HashFile(ref h, path);
                }
                else
                {
                    ModRuntime.Log?.Warning("[GameBuild] GameAssembly.dll not found - build unknown, handshake check skipped");
                }
                if (!hashed)
                {
                    // A hash over the version strings alone would never match a peer that did hash the file
                    // (and reject a good peer): unknown means "skip the comparison" (Hash == 0).
                    Hash = 0;
                    Label = Version + " (unhashed)";
                    return;
                }
                if (h == 0) h = 1;
                Hash = h;
                Label = Version + " #" + h.ToString("X8");
            }
            catch (Exception ex)
            {
                Hash = 0;
                Label = "unknown";
                Guard.Swallow("GameBuild.Compute", ex);
            }
        }

        private static string SafeVersion(Func<string> read)
        {
            try
            {
                string s = read();
                return string.IsNullOrEmpty(s) ? "?" : s;
            }
            catch (Exception ex)
            {
                Guard.Swallow("GameBuild.Version", ex);
                return "?";
            }
        }

        private static string FindGameAssembly()
        {
            try
            {
                string data = Application.dataPath;
                if (!string.IsNullOrEmpty(data))
                {
                    string root = Path.GetDirectoryName(data.TrimEnd('/', '\\'));
                    if (!string.IsNullOrEmpty(root))
                    {
                        string p = Path.Combine(root, "GameAssembly.dll");
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch (Exception ex) { Guard.Swallow("GameBuild.FindDataPath", ex); }
            try
            {
                string p = Path.Combine(MelonLoader.MelonUtils.BaseDirectory, "GameAssembly.dll");
                if (File.Exists(p)) return p;
            }
            catch (Exception ex) { Guard.Swallow("GameBuild.FindBaseDir", ex); }
            return null;
        }

        /// <summary>True when the whole head/tail read succeeded and <paramref name="h"/> covers the file.</summary>
        private static bool HashFile(ref uint h, string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    h = Mix(h, (int)(len & 0xFFFFFFFF));
                    h = Mix(h, (int)(len >> 32));
                    var buf = new byte[64 * 1024];
                    h = HashRange(h, fs, 0, Math.Min(len, Window), buf);
                    if (len > Window)
                    {
                        long tail = Math.Min(len - Window, Window);
                        h = HashRange(h, fs, len - tail, tail, buf);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[GameBuild] could not read " + path + ": " + ex.Message);
                Guard.Swallow("GameBuild.HashFile", ex);
                return false;
            }
        }

        private static uint HashRange(uint h, FileStream fs, long offset, long count, byte[] buf)
        {
            fs.Seek(offset, SeekOrigin.Begin);
            long left = count;
            while (left > 0)
            {
                int want = (int)Math.Min(buf.Length, left);
                int got = fs.Read(buf, 0, want);
                if (got <= 0) break;
                for (int i = 0; i < got; i++)
                {
                    h ^= buf[i];
                    h *= 16777619u;
                }
                left -= got;
            }
            return h;
        }

        private static uint Mix(uint h, string s)
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
