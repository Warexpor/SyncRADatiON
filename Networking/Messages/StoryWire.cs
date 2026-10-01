// Pure story-wire helpers (no Unity types): incremental commit selection, buffered-commit merge,
// dialogue tags and END attribution tags. Linked into tests/SyncRADation.Tests.
using System.Collections.Generic;

namespace SyncRADation.Networking
{
    public static class StoryWire
    {
        /// <summary>Presentation Text: the host ran this event natively in its own room, so it already counted its END effects.</summary>
        public const string HostCounted = "h";

        /// <summary>Presentation Text: host is out of the room; only this player id (the requester) counts END effects.</summary>
        public static string PlayerCounted(int playerId) => "p" + playerId;

        /// <summary>Client: does a replayed presentation with this attribution tag count its END_Manager writes here?</summary>
        public static bool CountsEndHere(string tag, int localPlayerId)
        {
            if (string.IsNullOrEmpty(tag)) return true; // unattributed (legacy / unknown): count like before
            if (tag == HostCounted) return false;
            if (tag.Length > 1 && tag[0] == 'p')
            {
                int id;
                if (int.TryParse(tag.Substring(1), out id)) return id == localPlayerId;
            }
            return true;
        }

        /// <summary>
        /// SProgress keys the game writes per player outside the shared story (Ghidra-verified writers: EnemyController.Save
        /// "enemy &lt;name&gt; hp/rp/pos/rot/ded/burn", RadioManager "RadioFreq", HelpInputPrompts "showHelp", InventoryBase
        /// "InventorySlot", MinimapPOIManager "mPOI&lt;id&gt;", PersistentMinimapManager "MMSet" / "F&lt;n&gt;R &lt;..&gt;",
        /// SaveGameScreenshotMaker "Screenshot"). The host never commits them: they would overwrite each client's own.
        /// </summary>
        public static bool IsPerPlayerKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.StartsWith("enemy ", System.StringComparison.Ordinal)) return true;
            if (key.StartsWith("mPOI", System.StringComparison.Ordinal)) return true;
            switch (key)
            {
                case "RadioFreq":
                case "showHelp":
                case "InventorySlot":
                case "Screenshot":
                case "MMSet":
                    return true;
            }
            // PersistentMinimapManager.Save: "F" + <int> + "R " + <cells>.
            if (key.Length > 3 && key[0] == 'F' && char.IsDigit(key[1]))
            {
                int i = 1;
                while (i < key.Length && char.IsDigit(key[i])) i++;
                if (i + 1 < key.Length && key[i] == 'R' && key[i + 1] == ' ') return true;
            }
            return false;
        }

        public static string DialogueTag(int id, int step) => id + ":" + step;

        public static bool TryParseDialogueTag(string tag, out int id, out int step)
        {
            id = -1;
            step = 0;
            if (string.IsNullOrEmpty(tag)) return false;
            int c = tag.IndexOf(':');
            if (c <= 0) return false;
            return int.TryParse(tag.Substring(0, c), out id) && int.TryParse(tag.Substring(c + 1), out step);
        }

        /// <summary>
        /// Incremental commit body: only the keys written since the last send (still present in the flag table).
        /// A full commit sends the whole table instead. Clears <paramref name="dirty"/>.
        /// </summary>
        public static StoryFlagEntry[] TakeDirty(Dictionary<string, StoryFlagEntry> flags, HashSet<string> dirty)
        {
            var list = new List<StoryFlagEntry>(dirty.Count);
            foreach (var k in dirty)
            {
                StoryFlagEntry e;
                if (flags.TryGetValue(k, out e)) list.Add(e);
            }
            dirty.Clear();
            return list.ToArray();
        }

        /// <summary>Buffered commits are cumulative: union by key, the newer entry wins. Order: older keys first, then new ones.</summary>
        public static StoryFlagEntry[] MergeFlags(StoryFlagEntry[] older, StoryFlagEntry[] newer)
        {
            if (older == null || older.Length == 0) return newer;
            if (newer == null || newer.Length == 0) return older;
            var index = new Dictionary<string, int>(older.Length + newer.Length);
            var list = new List<StoryFlagEntry>(older.Length + newer.Length);
            for (int i = 0; i < older.Length; i++)
            {
                var k = older[i].Key ?? "";
                int at;
                if (index.TryGetValue(k, out at)) list[at] = older[i];
                else { index[k] = list.Count; list.Add(older[i]); }
            }
            for (int i = 0; i < newer.Length; i++)
            {
                var k = newer[i].Key ?? "";
                int at;
                if (index.TryGetValue(k, out at)) list[at] = newer[i];
                else { index[k] = list.Count; list.Add(newer[i]); }
            }
            return list.ToArray();
        }
    }
}
