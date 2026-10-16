// Scene follow: every chapter / level load is personal (each player travels on their own). The host still announces
// its loads (SceneFollow) and its scene (SceneHello); a client only acts on them right after it connects (it goes to
// the host's scene once) or from a title-flow scene. A party wipe reloads everyone (NetworkDamageSystem.FollowWipeReload).
// A client in a scene the host is not in runs that scene like solo (ClientAwayFromHost); its SProgress writes still
// reach the host, which is how vanilla keeps a scene's progress across loads.
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    public static class SceneFollowService
    {
        // Non-level scenes (Pregame): MainMenu / DeadMenu / EndCredits.
        public const string MainMenuScene = "MainMenu";
        public const string DeadMenuScene = "DeadMenu";
        public const string EndCreditsScene = "EndCredits";

        // A load this peer started (or a follow / wipe reload is running) to this scene, and when.
        static string _pending;
        static float _pendingAt;
        const float InflightWindow = 12f;

        // Client: a fresh connection still goes to the host's scene once (cleared on arrival there or on this
        // player's own level load). Set again by Reset on every network stop.
        static bool _joinFollow = true;

        // Client: a join follow that arrived while this player was watching a cutscene: it runs when the cutscene ends,
        // capped in case it never does.
        static string _heldFollow;
        static float _heldAt;
        const float HoldForCutsceneMax = 180f;

        // Stuck-loading watch (TickLoadingWatch): when gameState loading was first seen with the player up.
        static float _loadingSince = -1f;
        const float StuckLoadingAfter = 4f;

        // Scope: loads are swallowed (even inside IsApplying). Used while a follow tears down a dialogue whose end
        // callbacks could otherwise start a load of their own on the follower.
        static int _suppressLoads;

        // persistent: build-settings scene names by build index, fixed for the process
        static string[] _buildNames;

        public static void Reset()
        {
            _pending = null;
            _pendingAt = 0f;
            _joinFollow = true;
            _heldFollow = null;
            _heldAt = 0f;
            _suppressLoads = 0;
            _loadingSince = -1f;
        }

        public static bool LoadsSuppressed => _suppressLoads > 0;
        public static void BeginSuppressLoads() => _suppressLoads++;
        public static void EndSuppressLoads() { if (_suppressLoads > 0) _suppressLoads--; }

        // ------------------------------------------------------------------ scene names

        static string ActiveScene()
        {
            try { return SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "active scene: " + ex.Message); return ""; }
        }

        public static bool IsTransient(string sceneName)
        {
            return string.IsNullOrEmpty(sceneName) || string.Equals(sceneName, "LoadingScreen", System.StringComparison.Ordinal);
        }

        public static bool LocalIsTransient() => IsTransient(ActiveScene());

        /// <summary>
        /// "MainMenu" (PauseMenu.quit -> quitToScene) or "MainMenu2" (CreditsEnd).
        /// </summary>
        public static bool IsMainMenu(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) && sceneName.StartsWith(MainMenuScene, System.StringComparison.Ordinal);
        }

        public static bool IsMenuScene(string sceneName)
        {
            return IsMainMenu(sceneName)
                || string.Equals(sceneName, DeadMenuScene, System.StringComparison.Ordinal)
                || IsEndCredits(sceneName)
                || string.Equals(sceneName, "Credits", System.StringComparison.Ordinal);
        }

        /// <summary>EndCredits, and EndCredits2 (RedRoomFinale / SanctuaryFinale load it; not in build settings).</summary>
        public static bool IsEndCredits(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) && sceneName.StartsWith(EndCreditsScene, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// A load that leaves the game for the title flow: MainMenu (PauseMenu.quit -> quitToScene("MainMenu") ->
        /// NewApplication.LoadLevel; CreditsEnd → MainMenu2), StartupPC (PlatformManager.RestartGame, AsyncLoader's
        /// LoadLevel(0) fallback) or Profile Select. Never a follow target: it is one player leaving.
        /// </summary>
        public static bool IsLeaveGame(string sceneName)
        {
            return IsMainMenu(sceneName)
                || string.Equals(sceneName, "StartupPC", System.StringComparison.Ordinal)
                || string.Equals(sceneName, "Profile Select", System.StringComparison.Ordinal);
        }

        static string[] BuildNames()
        {
            if (_buildNames != null) return _buildNames;
            try
            {
                var names = new string[SceneManager.sceneCountInBuildSettings];
                for (int i = 0; i < names.Length; i++)
                    names[i] = SceneFileName(SceneUtility.GetScenePathByBuildIndex(i));
                _buildNames = names;
            }
            catch (System.Exception e) { Guard.Swallow(e); return new string[0]; }
            return _buildNames;
        }

        static string SceneFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int start = Mathf.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1;
            int dot = path.LastIndexOf('.');
            return dot <= start ? path.Substring(start) : path.Substring(start, dot - start);
        }

        public static string ResolveLevelName(int index)
        {
            var names = BuildNames();
            string named = index >= 0 && index < names.Length ? names[index] : "";
            if (!IsTransient(named)) return named;
            try
            {
                string stored = AsyncLoader.targetLevelString;
                if (!IsTransient(stored)) return stored;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return named;
        }

        // ------------------------------------------------------------------ load bookkeeping

        public static bool AlreadyGoingTo(string sceneName)
        {
            // Only our own NoteGoingTo window counts: AsyncLoader.targetLevelString keeps the last native target after a
            // network reset.
            return !IsTransient(sceneName)
                && string.Equals(_pending, sceneName, System.StringComparison.Ordinal)
                && Time.unscaledTime - _pendingAt < InflightWindow;
        }

        public static void NoteGoingTo(string sceneName)
        {
            if (IsTransient(sceneName)) return;
            _pending = sceneName;
            _pendingAt = Time.unscaledTime;
        }

        public static void NoteArrived(string sceneName)
        {
            if (IsTransient(sceneName)) return;
            if (string.Equals(_pending, sceneName, System.StringComparison.Ordinal))
                _pending = null;
        }

        /// <summary>This player is watching a cutscene (native gameState cutscene): a join follow waits for it.</summary>
        public static bool InLocalCutscene()
        {
            try { return PlayerState.player != null && PlayerState.gameState == PlayerState.gameStates.cutscene; }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>A join follow to this scene waits for this player's cutscene.</summary>
        public static bool HoldsForCutscene(string sceneName)
        {
            return InLocalCutscene() && !IsMainMenu(sceneName)
                && !string.Equals(ActiveScene(), sceneName, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Client: go to the host's scene. Only a fresh connection (the joiner's own save position is not the party's
        /// world) or a client sitting in a title-flow scene does this; every other level load is personal.
        /// The wreck / hole split stays apart even on join: each Elster does her own airlock.
        /// </summary>
        public static bool MustFollowHost(string hostScene)
        {
            if (!NetGate.ClientRole || IsTransient(hostScene)) return false;
            string here = ActiveScene();
            if (AirlockCinematic.IsWreckHoleSplit(here, hostScene)) return false;
            return _joinFollow || IsLeaveGame(here);
        }

        /// <summary>Client: this player is in the host's scene (the join follow is done).</summary>
        public static void NoteWithHost()
        {
            if (!_joinFollow) return;
            _joinFollow = false;
            PlaytestLog.Event("Scene", "joined the host's scene: chapter loads are personal from here");
        }

        /// <summary>Client: this player's own level load (door, cutscene, F7). It goes alone and ends any join follow.</summary>
        public static void NoteOwnLoad(string sceneName)
        {
            _joinFollow = false;
            _heldFollow = null;
            NoteGoingTo(sceneName);
        }

        /// <summary>Client in a title-flow scene (MainMenu, StartupPC, Profile Select): its level loads are the host's.</summary>
        public static bool LocalInTitleFlow() => IsLeaveGame(ActiveScene());

        /// <summary>
        /// Client in a scene the host is not in: that scene runs here like solo (cutscenes, EventZones,
        /// MultiConditions, enemies), its SProgress writes still forward to the host.
        /// </summary>
        public static bool ClientAwayFromHost()
        {
            if (!NetGate.Client) return false;
            var net = LanNetworkManager.Instance;
            return net != null && net.SceneMismatch;
        }

        /// <summary>Client tick: run a held join follow once this player's cutscene is over.</summary>
        public static void TickClient()
        {
            if (string.IsNullOrEmpty(_heldFollow)) return;
            if (InLocalCutscene() && Time.unscaledTime - _heldAt <= HoldForCutsceneMax) return;
            string held = _heldFollow;
            _heldFollow = null;
            PlaytestLog.Event("Scene", "cutscene over, follow '" + held + "'");
            Apply(held);
        }

        /// <summary>
        /// Every peer in a session: a level whose only way out of gameState loading is its intro cutscene (no
        /// ForceGameStateOnStart, e.g. LOV_Reeducation) leaves the player frozen when it is entered with that intro
        /// already completed in this peer's SProgress (CutsceneManager.Start: onStart, !SaveManager.loading,
        /// !completed). A follow or an F7 jump can do that; restore play after a grace.
        /// </summary>
        public static void TickLoadingWatch()
        {
            bool stuck = false;
            if (NetGate.Live && !LocalIsTransient())
            {
                try
                {
                    stuck = PlayerState.player != null && PlayerState.gameState == PlayerState.gameStates.loading
                        && !SaveManager.loading && !PlayerState.cutscene;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (!stuck)
            {
                _loadingSince = -1f;
                return;
            }
            float now = Time.unscaledTime;
            if (_loadingSince < 0f) _loadingSince = now;
            if (now - _loadingSince < StuckLoadingAfter) return;
            _loadingSince = -1f;
            PlaytestLog.Warn("Scene", "gameState stuck at loading in '" + ActiveScene() + "' (no intro cutscene ran): play restored");
            try { PlayerState.gameState = PlayerState.gameStates.play; }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        // ------------------------------------------------------------------ follower

        public static void Apply(string sceneName)
        {
            if (IsTransient(sceneName)) return;
            string cur = ActiveScene();
            if (string.Equals(cur, sceneName, System.StringComparison.Ordinal))
            {
                _pending = null;
                return;
            }
            if (AlreadyGoingTo(sceneName)) return;

            NoteGoingTo(sceneName);
            PlaytestLog.Event("Scene", "follow load '" + sceneName + "' (was '" + cur + "')");
            // An open inventory / menu / dialogue, or a cutscene whose coroutine dies with the unloaded scene, would leave
            // gameState / PlayerState.cutscene / dialogue sticky on the follower: restore play first, like a disconnect.
            try { DroppedItemRegistry.RestorePlayForLoad(); }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "RestorePlayForLoad: " + ex.Message); }
            NetGate.BeginApply();
            try { LoadFirstAvailable(sceneName); }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "load failed: " + ex.Message); }
            finally { NetGate.EndApply(); }
        }

        // Fallback chain: AsyncLoader (loading screen), then the scene's SceneHelper, NewApplication, and plain SceneManager.
        static void LoadFirstAvailable(string sceneName)
        {
            try { AsyncLoader.LoadLevel(sceneName); return; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var helpers = WorldLookup.All<SceneHelper>();
                if (helpers != null && helpers.Length > 0 && helpers[0] != null)
                {
                    helpers[0].LoadScene(sceneName);
                    return;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { NewApplication.LoadLevel(sceneName); return; } catch (System.Exception e) { Guard.Swallow(e); }
            SceneManager.LoadScene(sceneName);
        }

        public static void HandleMessage(SceneFollowMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            // Requests are a wire value only: a client's level load is its own.
            if (msg.IsRequest || NetGate.HostRole) return;
            if (!MustFollowHost(msg.SceneName)) return;
            FollowHost(msg.SceneName);
        }

        /// <summary>
        /// Client: go to the host's scene now (join follow), or once this player's cutscene ends. Both sources, the
        /// SceneFollow message and the SceneHello scene compare, go through here so neither loads past the hold.
        /// </summary>
        public static void FollowHost(string sceneName)
        {
            if (HoldsForCutscene(sceneName))
            {
                if (string.IsNullOrEmpty(_heldFollow)) _heldAt = Time.unscaledTime;
                if (!string.Equals(_heldFollow, sceneName, System.StringComparison.Ordinal))
                    PlaytestLog.Event("Scene", "hold follow '" + sceneName + "' until this cutscene ends");
                _heldFollow = sceneName;
                return;
            }
            Apply(sceneName);
        }
    }
}
