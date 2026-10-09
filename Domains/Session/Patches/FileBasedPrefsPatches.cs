// FileBasedPrefs.DataScrambler replaced by the linear PrefsScrambler (same output).
using HarmonyLib;
using SyncRADation.Players;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    /// <summary>
    /// Native DataScrambler (Ghidra FileBasedPrefs.c) builds its result one String.Concat per char, and every
    /// FileBasedPrefs write (WriteToSaveFile) and read (GetSaveFile) runs it over the whole save file. A played save is
    /// ~47 KB: about 2.2 GB of garbage per call, and GlobalStats.SaveAchs writes once per achievement key from a
    /// cutscene's end event. Test pilot clients froze there for seconds and grew from 5 to 13 GB until killed (stall
    /// watch native frames: CutsceneManager Cutscene MoveNext &lt; UnityEvent.Invoke &lt; GlobalStats.SaveAchs &lt;
    /// FileBasedPrefs.SetBool &lt; WriteToSaveFile &lt; DataScrambler), on the intro skip, RES_School Isa_School, the
    /// LAB_Emptiness end and a join's first load. Same XOR, same key, one buffer: the file on disk is byte-identical.
    /// Applies offline too: it is the game's own function made linear, no sync involved.
    /// </summary>
    [HarmonyPatch(typeof(FileBasedPrefs), nameof(FileBasedPrefs.DataScrambler))]
    public static class FileBasedPrefsScramblerPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string data, ref string __result)
        {
            // Native throws on null: let it.
            if (data == null) return true;
            try
            {
                __result = PrefsScrambler.Scramble(data);
                return false;
            }
            catch (System.Exception e)
            {
                Guard.Swallow(e);
                return true;
            }
        }
    }
}
