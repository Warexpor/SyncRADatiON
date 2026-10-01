// Stand-ins for the MelonLoader-bound mod runtime surface that the pure files touch. Test-only.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SyncRADation
{
    /// <summary>Shape of MelonLogger.Instance as used by NetWire.WarnOnce (ModRuntime.Log?.Warning).</summary>
    public sealed class TestLog
    {
        public readonly List<string> Warnings = new List<string>();
        public void Warning(string msg) { lock (Warnings) Warnings.Add(msg); }
    }

    public static class ModRuntime
    {
        public static TestLog Log = new TestLog();
    }

    /// <summary>Same public API as Sync/Guard.cs (which needs MelonLoader); records instead of logging.</summary>
    public static class Guard
    {
        public static int Swallowed;
        public static int InternalFailures => 0;
        public static void Swallow(string tag, Exception e) { Swallowed++; }
        public static void Swallow(Exception e, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) { Swallowed++; }
    }
}

namespace SyncRADation.Config
{
    /// <summary>PluginInfo.MaxPlayers reads this; only the clamped value is needed.</summary>
    public static class ModConfig
    {
        public static int MaxPlayersClamped => 4;
    }
}
