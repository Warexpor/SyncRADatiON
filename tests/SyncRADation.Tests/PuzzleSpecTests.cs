using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>
    /// Source scan of Domains/Puzzles/PuzzleSpecs.cs (the rows bind Il2Cpp game types, so the file is not compiled
    /// here): every PuzzleType has exactly one PuzzleTypeSpec row or is listed as retired, and the live shared
    /// screens keep their merge kind.
    /// </summary>
    public class PuzzleSpecTests
    {
        static readonly Lazy<string> Source = new Lazy<string>(() =>
        {
            string d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "SyncRADation.csproj"))) d = Path.GetDirectoryName(d);
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d, "Domains", "Puzzles", "PuzzleSpecs.cs"));
        });

        static readonly Regex RowStart = new Regex(@"\b(?:Row<[\w.]+>|TryRow<[\w.]+>|Global)\(PuzzleType\.(\w+),");

        /// <summary>Row name → its text (up to the next row).</summary>
        static Dictionary<string, string> Rows(out List<string> names)
        {
            string src = Source.Value;
            var ms = RowStart.Matches(src).Cast<Match>().ToList();
            names = ms.Select(m => m.Groups[1].Value).ToList();
            var rows = new Dictionary<string, string>();
            for (int i = 0; i < ms.Count; i++)
            {
                int end = i + 1 < ms.Count ? ms[i + 1].Index : src.Length;
                rows[ms[i].Groups[1].Value] = src.Substring(ms[i].Index, end - ms[i].Index);
            }
            return rows;
        }

        static List<string> Retired()
        {
            var m = Regex.Match(Source.Value, @"Retired\s*=\s*\{(.*?)\};", RegexOptions.Singleline);
            Assert.True(m.Success, "PuzzleSpecs.Retired not found");
            return Regex.Matches(m.Groups[1].Value, @"PuzzleType\.(\w+)").Cast<Match>().Select(x => x.Groups[1].Value).ToList();
        }

        [Fact]
        public void Every_puzzle_type_has_one_spec_row_or_is_retired()
        {
            List<string> names;
            Rows(out names);
            var retired = Retired();
            Assert.Equal(names.Count, names.Distinct().Count());
            foreach (PuzzleType t in Enum.GetValues(typeof(PuzzleType)))
            {
                string n = t.ToString();
                bool row = names.Contains(n), dead = retired.Contains(n);
                Assert.True(row ^ dead, n + (row ? " is both a row and retired" : " has no PuzzleTypeSpec row and is not retired"));
            }
            foreach (string n in names.Concat(retired))
                Assert.True(Enum.IsDefined(typeof(PuzzleType), n), n + " is not a PuzzleType");
        }

        [Fact]
        public void Interaction_triggered_stays_retired()
        {
            Assert.Contains(nameof(PuzzleType.InteractionTriggered), Retired());
        }

        [Theory]
        [InlineData(nameof(PuzzleType.PatternLock), nameof(MergeKind.BitCells))]
        [InlineData(nameof(PuzzleType.MED_KeyGrid), nameof(MergeKind.BitCells))]
        [InlineData(nameof(PuzzleType.ROT_Tarot), nameof(MergeKind.Tarot))]
        [InlineData(nameof(PuzzleType.PEN_Codepad), nameof(MergeKind.AtomicInts))]
        [InlineData(nameof(PuzzleType.Keypad3D), nameof(MergeKind.AtomicInts))]
        [InlineData(nameof(PuzzleType.ROT_Keypad), nameof(MergeKind.AtomicInts))]
        [InlineData(nameof(PuzzleType.EvidenceLockerPuzzle), nameof(MergeKind.AtomicInts))]
        [InlineData(nameof(PuzzleType.PEN_Reaktor), nameof(MergeKind.Rods))]
        [InlineData(nameof(PuzzleType.ROT_Mural), nameof(MergeKind.HalfInts))]
        [InlineData(nameof(PuzzleType.DialLock), nameof(MergeKind.Fields))]
        [InlineData(nameof(PuzzleType.MED_Incinerator), nameof(MergeKind.Fields))]
        [InlineData(nameof(PuzzleType.RES_Shrine), nameof(MergeKind.Fields))]
        [InlineData(nameof(PuzzleType.ROT_RadioAlignment), nameof(MergeKind.Fields))]
        [InlineData(nameof(PuzzleType.DET_RadioCodeLock), nameof(MergeKind.Fields))]
        [InlineData(nameof(PuzzleType.MEM_ChecklistLogic), nameof(MergeKind.Grow))]
        public void Shared_screens_keep_their_merge_kind(string type, string kind)
        {
            List<string> names;
            var rows = Rows(out names);
            Assert.True(rows.ContainsKey(type), type);
            Assert.Contains("MergeKind." + kind, rows[type]);
        }

        [Theory]
        [InlineData(nameof(PuzzleType.InteractiveLock))]
        [InlineData(nameof(PuzzleType.UseItemInteraction))]
        [InlineData(nameof(PuzzleType.MultiLock))]
        public void Whole_entry_types_do_not_merge(string type)
        {
            List<string> names;
            var rows = Rows(out names);
            Assert.DoesNotContain("MergeKind.", rows[type]);
        }

        [Theory]
        [InlineData(nameof(PuzzleType.GlobalAlertStatus))]
        [InlineData(nameof(PuzzleType.EventZoneTriggered))]
        [InlineData(nameof(PuzzleType.MultiConditionEvent))]
        [InlineData(nameof(PuzzleType.CutsceneCompleted))]
        [InlineData(nameof(PuzzleType.DialoguePlayedOnce))]
        [InlineData(nameof(PuzzleType.EnemyManagerState))]
        [InlineData(nameof(PuzzleType.KolibriManager))]
        public void Host_authored_types_are_never_client_emitted(string type)
        {
            List<string> names;
            var rows = Rows(out names);
            Assert.Contains("SpecFlags.None", rows[type]);
            Assert.DoesNotContain("Emit", rows[type].Replace("ClientSendsIf", ""));
        }
    }
}
