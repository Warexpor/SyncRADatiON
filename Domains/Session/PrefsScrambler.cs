// FileBasedPrefs.DataScrambler in linear time (Ghidra FileBasedPrefs.c): the same XOR with the same key, one buffer.
namespace SyncRADation.Players
{
    public static class PrefsScrambler
    {
        public const string Key = "ALL WATCHED OVER BY MACHINES OF LOVING GRACE";

        /// <summary>
        /// Each char XOR the key's char at index % key length, exactly as native. Native appends every char with
        /// String.Concat (a new string per char): O(n^2), about 2.2 GB of garbage for a 47 KB save file, on every
        /// FileBasedPrefs write and read.
        /// </summary>
        public static string Scramble(string data)
        {
            var buf = new char[data.Length];
            for (int i = 0; i < buf.Length; i++)
                buf[i] = (char)(Key[i % Key.Length] ^ data[i]);
            return new string(buf);
        }
    }
}
