// Penrose airlock: the PEN_Titles cinematic is personal (each Elster plays her own). Its PEN_Hole load is personal like
// every level load (SceneLoadGate); a join follow still keeps the wreck / hole split apart (IsWreckHoleSplit).
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    internal static class AirlockCinematic
    {
        const string Wreck = "PEN_Wreck";
        const string Hole = "PEN_Hole";
        // Keycard UseItems this peer unlocked itself (its own cinematic).
        static readonly System.Collections.Generic.HashSet<ulong> _localUnlock = new System.Collections.Generic.HashSet<ulong>();

        public static void Reset()
        {
            _localUnlock.Clear();
        }

        static ulong Id(UseItemInteraction u) => u != null ? WorldId.FromGameObject(u.gameObject) : 0;

        internal static PEN_Titles[] AllTitles() => WorldLookup.All<PEN_Titles>();

        public static void NoteLocalUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            if (id != 0) _localUnlock.Add(id);
        }

        public static bool IsLocalUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            return id != 0 && _localUnlock.Contains(id);
        }

        public static void ArmTitlesSkip(PEN_Titles t)
        {
            if (t == null) return;
            // CutsceneSkippingUI is for CutsceneManager. PEN_Titles has its own skipper;
            // arming both made the hold bar fight the titles coroutine.
            try
            {
                if (t.skipper != null) t.skipper.enabled = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static bool IsPenTitlesCard(UseItemInteraction x)
        {
            if (x == null) return false;
            try
            {
                var all = AllTitles();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].keyCardEvent == x) return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        /// <summary>
        /// Interaction.trigger on this peer: the keycard slot of a PEN_Titles (or its view point once that card is
        /// unlocked) begins this peer's own airlock cinematic.
        /// </summary>
        public static bool TryBeginLocal(Interaction inter)
        {
            if (inter == null || !NetGate.Live) return false;
            try
            {
                var all = AllTitles();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    var card = t.keyCardEvent;
                    bool match = card != null && (card.inter == inter || (t.ViewPoint == inter && card.unlocked));
                    if (!match) continue;
                    if (t.started) return false;
                    NoteLocalUnlock(card);
                    PlaytestLog.Event("Story", "local PEN_Titles cinematic");
                    return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        public static bool IsWreckHoleSplit(string a, string b)
        {
            bool aWreck = string.Equals(a, Wreck, System.StringComparison.Ordinal);
            bool bWreck = string.Equals(b, Wreck, System.StringComparison.Ordinal);
            bool aHole = string.Equals(a, Hole, System.StringComparison.Ordinal);
            bool bHole = string.Equals(b, Hole, System.StringComparison.Ordinal);
            return (aWreck && bHole) || (aHole && bWreck);
        }
    }
}
