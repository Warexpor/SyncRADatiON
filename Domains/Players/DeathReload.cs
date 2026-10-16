// Co-op death reload: a player who dies (host or client) reloads the scene it died in on its own and starts at its
// last save point there, or at the scene's entrance when it never saved in this scene. Nothing is read from a save
// slot: the story, doors, puzzles, pickups and enemies are the party's live world (SProgress as it stands now; the
// scene owner's dump for anyone else there), so a death never puts one peer's world behind the others'. The bag is
// kept as it was at death (no item the world no longer has comes back, none it still lacks is lost).
//
// Native reference (Ghidra LoadingManager.c Start): with SaveManager.loading set, Room.Awake skips the authored start
// room and CutsceneManager.Start skips the level intro; LoadingManager.Start runs SaveManager.Load, puts the player at
// the SavePoint named by the save's "RoomName" (LeaveRoom, position, EnterRoom, gameState 0) and clears loading. The
// LoadingManager prefix here does the placement for the armed room and never lets native Load read a slot.
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Players
{
    public static class DeathReload
    {
        /// <summary>Death animation and sound land before the reload starts.</summary>
        const float DeathDelay = 3.5f;
        /// <summary>Armed placement that never ran (no LoadingManager.Start after arrival): give up and clear the flag.</summary>
        const float PlaceTimeout = 5f;
        /// <summary>A reload that never reached the loading screen (load refused / threw): get up where the player lies.</summary>
        const float LoadTimeout = 30f;

        // This player's last save (or loaded save): scene and SaveManager.SaveRoomName. Per process: the save outlives
        // the session it was made in.
        // persistent: this player's last save point outlives the session
        static string _saveScene = "";
        // persistent: this player's last save point outlives the session
        static string _saveRoom = "";

        // The reload under way.
        static bool _pending;
        static float _diedAt;
        static bool _issued;
        static float _issuedAt;
        static bool _sawTransient;
        static string _scene = "";
        static string _room = "";
        static bool _armed;
        static bool _placed;
        static float _arrivedAt = -1f;

        public static bool Pending => _pending;

        public static void Reset()
        {
            if (_armed && !_placed) ClearLoadingFlag();
            _pending = false;
            _issued = false;
            _sawTransient = false;
            _scene = "";
            _room = "";
            _armed = false;
            _placed = false;
            _arrivedAt = -1f;
        }

        // ------------------------------------------------------------------ save point tracking

        /// <summary>SaveManager.Save / Load postfix (any role): the save room this player now restarts at in that scene.</summary>
        public static void NoteSave(string what)
        {
            try
            {
                string room = SaveManager.SaveRoomName ?? "";
                if (string.IsNullOrEmpty(room)) room = SProgress.GetString("RoomName", "") ?? "";
                string scene = ActiveScene();
                if (SceneFollowService.IsTransient(scene) || string.IsNullOrEmpty(room)) return;
                _saveScene = scene;
                _saveRoom = room;
                PlaytestLog.Event("Damage", what + ": restart point '" + scene + "' room '" + room + "'");
            }
            catch (System.Exception e) { Guard.Swallow("DeathReload.NoteSave", e); }
        }

        /// <summary>SaveManager.NewGame: the run has no save yet, nothing to restart at.</summary>
        public static void NoteNewGame()
        {
            _saveScene = "";
            _saveRoom = "";
        }

        // ------------------------------------------------------------------ death

        /// <summary>The player just died (NetworkDamageSystem.Die): the reload starts after the death has landed.</summary>
        public static void OnDied()
        {
            Reset();
            _pending = true;
            _diedAt = Time.unscaledTime;
        }

        /// <summary>Every frame while a death is being handled. True once the player is back (alive, placed).</summary>
        public static bool Tick()
        {
            if (!_pending) return false;
            float now = Time.unscaledTime;
            if (!_issued)
            {
                if (now - _diedAt < DeathDelay) return false;
                // A load of its own already under way (a door it walked through as it died): wait for it to land.
                if (SceneFollowService.LocalIsTransient()) return false;
                Issue();
                return false;
            }
            bool transient = SceneFollowService.LocalIsTransient();
            if (transient)
            {
                _sawTransient = true;
                _arrivedAt = -1f;
                return false;
            }
            if (!_sawTransient)
            {
                if (now - _issuedAt < LoadTimeout) return false;
                PlaytestLog.Warn("Damage", "death reload of '" + _scene + "' never started - getting up here");
                Finish();
                return true;
            }
            if (PlayerState.player == null) return false;
            if (_arrivedAt < 0f) _arrivedAt = now;
            if (_armed && !_placed)
            {
                if (now - _arrivedAt < PlaceTimeout) return false;
                PlaytestLog.Warn("Damage", "death reload: no LoadingManager placed the player in '" + ActiveScene() + "'");
                ClearLoadingFlag();
            }
            Finish();
            return true;
        }

        static void Issue()
        {
            _issued = true;
            _issuedAt = Time.unscaledTime;
            _scene = ActiveScene();
            _room = PickRoom(_scene);
            PlaytestLog.Event("Damage", "death: reload '" + _scene + "' "
                + (_room.Length > 0 ? "at save room '" + _room + "'" : "at its entrance (no save here)"));
            try
            {
                DroppedItemRegistry.RestorePlayForLoad();
                if (_room.Length > 0)
                {
                    SaveManager.loading = true;
                    _armed = true;
                }
                // Not inside an apply scope: SceneLoadGate sees an own load (the host announces it and keeps the scene's
                // ownership through the load, SceneOwners.HostRecompute uses the pending target).
                AsyncLoader.LoadLevel(_scene);
            }
            catch (System.Exception ex)
            {
                PlaytestLog.Warn("Damage", "death reload of '" + _scene + "' failed: " + ex.Message);
                if (_armed) ClearLoadingFlag();
                _armed = false;
                _sawTransient = false;
                _issuedAt = Time.unscaledTime - LoadTimeout; // next tick gets up in place
            }
        }

        /// <summary>The save room to restart at: this player's last save if it was made in this scene and the scene has it.</summary>
        static string PickRoom(string scene)
        {
            if (!string.Equals(scene, _saveScene, System.StringComparison.Ordinal) || string.IsNullOrEmpty(_saveRoom)) return "";
            var lm = FindManager();
            var points = lm != null ? lm.savePoints : null;
            if (points == null) return "";
            for (int i = 0; i < points.Count; i++)
            {
                var sp = points[i];
                if (sp != null && string.Equals(sp.SaveRoomName, _saveRoom, System.StringComparison.Ordinal)) return _saveRoom;
            }
            return "";
        }

        static void Finish()
        {
            _pending = false;
            _issued = false;
            _armed = false;
            _placed = false;
            _sawTransient = false;
            _arrivedAt = -1f;
            NetworkDamageSystem.OnReloaded();
        }

        // ------------------------------------------------------------------ LoadingManager.Start

        /// <summary>
        /// LoadingManager.Start prefix in a party. The armed death reload is placed at its save room; a client never lets
        /// native SaveManager.Load read its own slot (the world is the party's). A host's own load (title Continue
        /// while hosting ends the session before it gets here) keeps the native path. Returns whether native Start runs.
        /// </summary>
        internal static bool OnLoadingManagerStart(LoadingManager lm)
        {
            if (!NetGate.Party) return true;
            bool loading;
            try { loading = SaveManager.loading; }
            catch (System.Exception e) { Guard.Swallow(e); return true; }
            if (!loading) return true; // native returns at once
            string here = ActiveScene();
            if (_armed && !_placed && string.Equals(here, _scene, System.StringComparison.Ordinal))
            {
                _placed = true;
                PlaceAt(lm, _room);
                return false;
            }
            if (!NetGate.ClientRole) return true;
            PlaytestLog.Warn("Damage", "client LoadingManager with loading set in '" + here + "': skipped own-slot Load");
            ClearLoadingFlag();
            return false;
        }

        static LoadingManager FindManager()
        {
            var all = WorldLookup.All<LoadingManager>();
            return all != null && all.Length > 0 ? all[0] : null;
        }

        /// <summary>LoadingManager.Start after SaveManager.Load: the named SavePoint, else the first one.</summary>
        static void PlaceAt(LoadingManager lm, string room)
        {
            try
            {
                var points = lm != null ? lm.savePoints : null;
                if (points == null || points.Count == 0)
                {
                    PlaytestLog.Warn("Damage", "death reload: no save points here, staying at the entrance");
                    return;
                }
                SavePoint target = points[0];
                for (int i = 0; i < points.Count; i++)
                {
                    var sp = points[i];
                    if (sp != null && string.Equals(sp.SaveRoomName, room, System.StringComparison.Ordinal)) target = sp;
                }
                var cur = PlayerState.currentRoom;
                if (cur != null) cur.LeaveRoom();
                var player = lm.Player != null ? lm.Player : (PlayerState.player != null ? PlayerState.player.transform : null);
                if (target != null && target.TargetPosition != null && player != null)
                {
                    player.position = target.TargetPosition.position;
                    if (target.room != null) target.room.EnterRoom();
                }
                PlayerState.gameState = PlayerState.gameStates.play;
                PlaytestLog.Event("Damage", "death reload: placed at save room '" + (target != null ? target.SaveRoomName : "") + "'");
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            finally { ClearLoadingFlag(); }
        }

        static void ClearLoadingFlag()
        {
            try { SaveManager.loading = false; }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static string ActiveScene()
        {
            try { return SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception e) { Guard.Swallow(e); return ""; }
        }
    }
}

namespace SyncRADation.Patches
{
    [HarmonyLib.HarmonyPatch(typeof(LoadingManager), "Start")]
    public static class LoadingManagerDeathReloadPatch
    {
        [HarmonyLib.HarmonyPrefix]
        public static bool Prefix(LoadingManager __instance) => Players.DeathReload.OnLoadingManagerStart(__instance);
    }
}
