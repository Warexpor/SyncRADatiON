// Personal Penrose airlock cinematic — wreck↔hole split, local unlock tracking.
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    internal static class AirlockCinematic
    {
        static readonly System.Collections.Generic.HashSet<ulong> _localUnlock
            = new System.Collections.Generic.HashSet<ulong>();
        static readonly System.Collections.Generic.HashSet<ulong> _remoteUnlock
            = new System.Collections.Generic.HashSet<ulong>();

        static string _personalScene;
        static bool _localCinematic;
        static float _cinematicAt;

        public static void Reset()
        {
            _localUnlock.Clear();
            _remoteUnlock.Clear();
            _localCinematic = false;
            _cinematicAt = 0f;
        }

        public static void NotePersonalLoad(string scene)
        {
            if (!string.IsNullOrEmpty(scene))
                _personalScene = scene;
        }

        public static void NoteLocalUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            if (id == 0) return;
            _localUnlock.Add(id);
            _remoteUnlock.Remove(id);
        }

        public static void NoteRemoteUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            if (id == 0 || _localUnlock.Contains(id)) return;
            _remoteUnlock.Add(id);
        }

        public static bool IsLocalUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            return id != 0 && _localUnlock.Contains(id);
        }

        internal static PEN_Titles[] AllTitles()
        {
            return WorldLookup.All<PEN_Titles>();
        }

        public static void ArmTitlesSkip(PEN_Titles t)
        {
            if (t == null) return;
            // CutsceneSkippingUI is for CutsceneManager. PEN_Titles has its own skipper;
            // arming both made the hold bar fight the titles coroutine.
            try
            {
                if (t.skipper != null)
                    t.skipper.enabled = true;
            }
            catch { }
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
                    if (all[i] != null && all[i].keyCardEvent == x)
                        return true;
                }
            }
            catch { }
            return false;
        }

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
                    bool match = false;
                    try
                    {
                        if (t.keyCardEvent != null && t.keyCardEvent.inter == inter)
                            match = true;
                        else if (t.ViewPoint == inter)
                        {
                            bool unlocked = false;
                            try { unlocked = t.keyCardEvent != null && t.keyCardEvent.unlocked; }
                            catch { }
                            match = unlocked;
                        }
                    }
                    catch { }
                    if (!match) continue;
                    if (t.started)
                        return false;
                    if (t.keyCardEvent != null)
                        NoteLocalUnlock(t.keyCardEvent);
                    _localCinematic = true;
                    _cinematicAt = Time.unscaledTime;
                    PlaytestLog.Event("Story", "local PEN_Titles cinematic");
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsPersonalChapterLoad(string scene)
        {
            if (string.IsNullOrEmpty(scene) || SceneFollowService.IsTransient(scene)) return false;
            if (!string.Equals(scene, "PEN_Hole", System.StringComparison.Ordinal)) return false;
            if (DeferFollowWhileAirlockPresent()) return true;
            try
            {
                var all = AllTitles();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    if (IsLocalUnlock(t.keyCardEvent))
                        return true;
                }
            }
            catch { }
            return false;
        }

        static bool IsWreckOrHole(string scene)
        {
            return string.Equals(scene, "PEN_Wreck", System.StringComparison.Ordinal)
                || string.Equals(scene, "PEN_Hole", System.StringComparison.Ordinal);
        }

        public static bool IsWreckHoleSplit(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            bool aWreck = string.Equals(a, "PEN_Wreck", System.StringComparison.Ordinal);
            bool bWreck = string.Equals(b, "PEN_Wreck", System.StringComparison.Ordinal);
            bool aHole = string.Equals(a, "PEN_Hole", System.StringComparison.Ordinal);
            bool bHole = string.Equals(b, "PEN_Hole", System.StringComparison.Ordinal);
            return (aWreck && bHole) || (aHole && bWreck);
        }

        public static bool ShouldIgnoreHostFollow(string hostScene)
        {
            if (string.IsNullOrEmpty(hostScene)) return false;
            string local = "";
            try { local = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch { }
            // Host left Penrose — follow (LOV etc.). Stale _personalScene=PEN_Hole used to
            // trap the client in the hole after LOV_Reeducation loaded (pause-only freeze).
            if (!IsWreckOrHole(hostScene))
            {
                _personalScene = null;
                return false;
            }
            // Wreck↔hole is per-Elster. Requiring local PEN_Titles meant the observer
            // still on the wreck got SceneFollow when the host skipped the airlock.
            if (IsWreckHoleSplit(local, hostScene))
                return true;
            return false;
        }

        public static bool DeferFollowWhileAirlockPresent()
        {
            if (PenTitlesStarted()) return true;
            if (_localCinematic)
            {
                if (Time.unscaledTime - _cinematicAt < 3f)
                    return true;
                _localCinematic = false;
            }
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
                    if (all[i] != null && all[i].started)
                        return true;
                }
            }
            catch { }
            return false;
        }

        static ulong Id(UseItemInteraction u)
        {
            if (u == null) return 0;
            return WorldId.FromGameObject(u.gameObject);
        }
    }
}
