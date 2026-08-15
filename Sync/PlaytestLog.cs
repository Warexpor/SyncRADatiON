// Sparse playtest traces. Always-on for Chapter 1 diagnosis; VerboseLogging for FMOD oneshots.
namespace SyncRADation.Sync
{
    public static class PlaytestLog
    {
        public static void Event(string tag, string msg)
        {
            ModRuntime.Log?.Msg("[" + tag + "] " + msg);
        }

        public static void Verbose(string tag, string msg)
        {
            if (!ModRuntime.VerboseLogging) return;
            Event(tag, msg);
        }

        public static void Miss(string tag, string what, ulong id)
        {
            ModRuntime.Log?.Warning("[" + tag + "] MISS " + what + " id=" + id.ToString("X16"));
        }
    }
}
