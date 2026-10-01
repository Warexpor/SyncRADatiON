// Penrose airlock: the PEN_Titles cinematic is personal (each Elster plays her own), and PEN_Wreck <-> PEN_Hole is a
// per-player split: whoever finishes the airlock loads PEN_Hole alone, the others stay on the wreck. Neither follows.
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    internal static class AirlockCinematic
    {
        const string Wreck = "PEN_Wreck";
        const string Hole = "PEN_Hole";
        // A local cinematic that just began holds peer scene requests for this long (PEN_Titles.started lags a frame).
        const float LocalCinematicHold = 3f;

        // Keycard UseItems this peer unlocked itself (its own cinematic).
        static readonly System.Collections.Generic.HashSet<ulong> _localUnlock = new System.Collections.Generic.HashSet<ulong>();
        static bool _localCinematic;
        static float _cinematicAt;

        public static void Reset()
        {
            _localUnlock.Clear();
            _localCinematic = false;
            _cinematicAt = 0f;
        }

        static ulong Id(UseItemInteraction u) => u != null ? WorldId.FromGameObject(u.gameObject) : 0;

        static string LocalScene()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception e) { Guard.Swallow(e); return ""; }
        }

        internal static PEN_Titles[] AllTitles() => WorldLookup.All<PEN_Titles>();

        public static void NoteLocalUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            if (id != 0) _localUnlock.Add(id);
        }

        /// <summary>
        /// A peer unlocked this keycard: that unlock is theirs, so it never makes this peer's PEN_Hole load personal.
        /// Nothing is recorded (IsLocalUnlock only tracks this peer's own unlocks).
        /// </summary>
        public static void NoteRemoteUnlock(UseItemInteraction u) { }

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
                    _localCinematic = true;
                    _cinematicAt = Time.unscaledTime;
                    PlaytestLog.Event("Story", "local PEN_Titles cinematic");
                    return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        /// <summary>A PEN_Hole load from this peer's own airlock: it runs locally, nobody follows.</summary>
        public static bool IsPersonalChapterLoad(string scene)
        {
            if (!string.Equals(scene, Hole, System.StringComparison.Ordinal)) return false;
            if (DeferFollowWhileAirlockPresent()) return true;
            try
            {
                var all = AllTitles();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && IsLocalUnlock(all[i].keyCardEvent)) return true;
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

        /// <summary>
        /// Client on one side of the wreck / hole split while the host is on the other: the host has none of this
        /// scene's cutscenes / EventZones / MultiConditions (its request apply finds nothing, and presentations are
        /// dropped while the scenes differ), so they run natively here, like solo. Flags they write still forward.
        /// </summary>
        public static bool ClientSplitFromHost()
        {
            if (!NetGate.Client) return false;
            var net = LanNetworkManager.Instance;
            return net != null && IsWreckHoleSplit(LocalScene(), net.HostSceneName);
        }

        /// <summary>
        /// The host's scene is the other side of the wreck / hole split: do not follow it. Wreck <-> hole is per-Elster
        /// (an observer still on the wreck must not be pulled into the hole when the host skips the airlock); a host
        /// leaving Penrose (LOV etc.) is followed.
        /// </summary>
        public static bool ShouldIgnoreHostFollow(string hostScene) => IsWreckHoleSplit(LocalScene(), hostScene);

        public static bool DeferFollowWhileAirlockPresent()
        {
            if (PenTitlesStarted()) return true;
            if (!_localCinematic) return false;
            if (Time.unscaledTime - _cinematicAt < LocalCinematicHold) return true;
            _localCinematic = false;
            return false;
        }

        static bool PenTitlesStarted()
        {
            try
            {
                var all = AllTitles();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].started) return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }
    }
}
