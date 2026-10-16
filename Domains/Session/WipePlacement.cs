// Client side of a party wipe that reloads the host's save: start at the host's save point, like the host.
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    /// <summary>
    /// The host reloads its save with SaveManager.loading set: Room.Awake skips the authored start room,
    /// CutsceneManager.Start skips the level intro, and LoadingManager.Start runs SaveManager.Load, then puts the player
    /// at the SavePoint named by the save's "RoomName" (LeaveRoom, position, EnterRoom, gameState = 0, loading = false;
    /// Ghidra LoadingManager.c Start). A client following that reload never loads a slot of its own, so it used to
    /// arrive at the scene's authored entrance (and replay the intro). Armed by the wipe message, the client loads with
    /// the same loading flag and does LoadingManager's placement with the host's room name, without SaveManager.Load.
    /// </summary>
    public static class WipePlacement
    {
        static string _scene = "";
        static string _room = "";

        public static bool Armed => !string.IsNullOrEmpty(_room);

        /// <summary>loadPending: the reload scene has not loaded yet here (its LoadingManager.Start is still to run).</summary>
        public static void Arm(string scene, string room, bool loadPending)
        {
            if (!loadPending)
            {
                // Already in the reloaded scene (the host's follow beat the wipe message): place now.
                PlaytestLog.Event("Damage", "wipe: already loaded, placing at save room '" + room + "'");
                PlaceAt(FindManager(), room);
                return;
            }
            _scene = scene ?? "";
            _room = room ?? "";
            try { SaveManager.loading = true; }
            catch (System.Exception e) { Guard.Swallow(e); }
            PlaytestLog.Event("Damage", "wipe: load '" + _scene + "' at save room '" + _room + "'");
        }

        /// <summary>Session boundary: an armed load that never arrived must not leave the loading flag behind.</summary>
        public static void Reset()
        {
            if (!Armed) return;
            _scene = "";
            _room = "";
            try { SaveManager.loading = false; }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// LoadingManager.Start prefix on a client in a party. Never lets native SaveManager.Load read this peer's own
        /// slot; an armed wipe reload is placed at the host's save room. Returns whether native Start runs.
        /// </summary>
        internal static bool OnLoadingManagerStart(LoadingManager lm)
        {
            if (!NetGate.ClientRole || !NetGate.Party) return true;
            bool loading;
            try { loading = SaveManager.loading; }
            catch (System.Exception e) { Guard.Swallow(e); return true; }
            if (!loading) return true; // native returns at once
            string here = "";
            try { here = SceneManager.GetActiveScene().name ?? ""; } catch (System.Exception e) { Guard.Swallow(e); }
            if (Armed && string.Equals(here, _scene, System.StringComparison.Ordinal))
            {
                string room = _room;
                _scene = "";
                _room = "";
                PlaceAt(lm, room);
            }
            else
            {
                PlaytestLog.Warn("Damage", "client LoadingManager with loading set in '" + here + "': skipped own-slot Load");
                try { SaveManager.loading = false; } catch (System.Exception e) { Guard.Swallow(e); }
            }
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
                    PlaytestLog.Warn("Damage", "wipe: no save points here, staying at the entrance");
                    return;
                }
                SavePoint target = points[0];
                for (int i = 0; i < points.Count; i++)
                {
                    var sp = points[i];
                    if (sp != null && string.Equals(sp.SaveRoomName, room, System.StringComparison.Ordinal)) target = sp;
                }
                var here = PlayerState.currentRoom;
                if (here != null) here.LeaveRoom();
                var player = lm.Player != null ? lm.Player : (PlayerState.player != null ? PlayerState.player.transform : null);
                if (target != null && target.TargetPosition != null && player != null)
                {
                    player.position = target.TargetPosition.position;
                    if (target.room != null) target.room.EnterRoom();
                }
                PlayerState.gameState = PlayerState.gameStates.play;
                PlaytestLog.Event("Damage", "wipe: placed at save room '" + (target != null ? target.SaveRoomName : "") + "'");
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            finally
            {
                try { SaveManager.loading = false; } catch (System.Exception e) { Guard.Swallow(e); }
            }
        }
    }
}

namespace SyncRADation.Patches
{
    [HarmonyLib.HarmonyPatch(typeof(LoadingManager), "Start")]
    public static class LoadingManagerWipePatch
    {
        [HarmonyLib.HarmonyPrefix]
        public static bool Prefix(LoadingManager __instance) => Networking.WipePlacement.OnLoadingManagerStart(__instance);
    }
}
