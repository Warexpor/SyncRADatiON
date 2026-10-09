using SyncRADation.Players;
using Xunit;

namespace SyncRADation.Tests
{
    public class PrefsScramblerTests
    {
        // Native DataScrambler, transcribed from Ghidra FileBasedPrefs.c: one Concat per char.
        static string Native(string data)
        {
            string s = "";
            for (int i = 0; i < data.Length; i++)
                s = string.Concat(s, ((char)(PrefsScrambler.Key[i % PrefsScrambler.Key.Length] ^ data[i])).ToString());
            return s;
        }

        [Fact]
        public void Matches_native_and_round_trips()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 5000; i++) sb.Append((char)(i * 7919 % 0xD7FF));
            sb.Append("{\"boolData\":[{\"key\":\"ach_lab\",\"value\":true}]}");
            string data = sb.ToString();
            Assert.Equal(Native(data), PrefsScrambler.Scramble(data));
            Assert.Equal(data, PrefsScrambler.Scramble(PrefsScrambler.Scramble(data)));
            Assert.Equal("", PrefsScrambler.Scramble(""));
        }
    }
}
