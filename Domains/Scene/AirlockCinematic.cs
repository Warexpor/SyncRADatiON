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
        static bool _localCinematic;
        static float _cinematicAt;

        public static void Reset()
        {
            _localUnlock.Clear();
            _localCinematic = false;
            _cinematicAt = 0f;
        }

        public static void NoteLocalUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            if (id == 0) return;
            _localUnlock.Add(id);
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
                    if (all[i] != null && all[i].keyCardEvent == x)
                        return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
                            catch (System.Exception e) { Guard.Swallow(e); }
                            match = unlocked;
                        }
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
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

        /// <summary>
        /// Client on one side of the wreck / hole split while the host is on the other: the host has none of this
        /// scene's cutscenes / EventZones / MultiConditions (its request apply finds nothing, and presentations are
        /// dropped while the scenes differ), so they run natively here, like solo. Flags they write still forward.
        /// </summary>
        public static bool ClientSplitFromHost()
        {
            if (!NetGate.Client) return false;
            var net = LanNetworkManager.Instance;
            if (net == null) return false;
            string local = "";
            try { local = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception e) { Guard.Swallow(e); }
            return IsWreckHoleSplit(local, net.HostSceneName);
        }

        public static bool ShouldIgnoreHostFollow(string hostScene)
        {
            if (string.IsNullOrEmpty(hostScene)) return false;
            string local = "";
            try { local = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception e) { Guard.Swallow(e); }
            // Host left Penrose — follow (LOV etc.).
            if (!IsWreckOrHole(hostScene)) return false;
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
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        static ulong Id(UseItemInteraction u)
        {
            if (u == null) return 0;
            return WorldId.FromGameObject(u.gameObject);
        }
    }
}
