// Co-op death: native game over is replaced by downed/revive/party-wipe while a party is live.
// Solo / no party: every prefix returns true and the game behaves like vanilla.
using HarmonyLib;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    /// <summary>
    /// A real native load (SaveManager.loading was true on entry; otherwise native Load is a no-op): a hosted
    /// session now runs from that slot's last party save. Reset the party key ring to that save's snapshot,
    /// claimed uniques and floor drops so a wipe reload does not keep state the save never had. Clients keep
    /// their own saves untouched. Solo play: two int writes, nothing else.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Load))]
    public static class SaveManagerLoadPatch
    {
        [HarmonyPrefix]
        public static void Prefix(out bool __state)
        {
            __state = HostReload.LoadWillRun();
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (!__state) return; // native Load returned immediately: nothing was loaded
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client) return;
            try
            {
                bool wipe = HostReload.OnLoadFinished();
                if (!NetGate.Host)
                {
                    PartySaveService.NoteSoloLoad();
                    return;
                }
                PartySaveService.OnHostLoaded();
                HostReload.ResetHostWorldState(net);
                PlaytestLog.Event("PartySave", "host load — ring/claims/floor drops reset" + (wipe ? " (wipe reload)" : ""));
                if (wipe)
                    SessionReset.RunAll(SessionReset.ReasonWipe);
                // The snapshot sent at scene arrival was held back (HostReload.Pending) or pre-dates this reset:
                // send the post-reset world once the host is out of the loading screen.
                net?.SessionHandlers.DeferDump(-1);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] Load postfix failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Host save = party save: stamp a token, snapshot the key ring, tell clients to snapshot their bags.
    /// Solo: remembers the slot in memory only (no token, no file write).
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save))]
    public static class SaveManagerSavePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client) return;
            try
            {
                HostReload.NoteSlotBound();
                if (!NetGate.Host)
                {
                    PartySaveService.NoteSoloSave();
                    return;
                }
                var token = PartySaveService.OnHostSaved();
                net.PartyHandlers.SendPartySave(token, 0);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] Save postfix failed: " + ex.Message);
            }
        }
    }

    /// <summary>New game: the run has no save yet. Forget the previous token; the next level load is the start scene.</summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.NewGame))]
    public static class SaveManagerNewGamePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                PartySaveService.OnNewGame();
                HostReload.NoteNewGame();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] NewGame postfix failed: " + ex.Message);
            }
        }
    }

    /// <summary>Records the level the game loads right after NewGame: where a wipe of a never-saved run restarts.</summary>
    [HarmonyPatch(typeof(AsyncLoader), nameof(AsyncLoader.LoadLevel), new[] { typeof(string) })]
    public static class AsyncLoaderNewGameSceneCapture
    {
        [HarmonyPostfix]
        public static void Postfix(string target)
        {
            try { HostReload.NoteLevelLoad(target); }
            catch (System.Exception ex) { Guard.Swallow("DeathPatches.NewGameScene", ex); }
        }
    }

    /// <summary>No native game-over screen while a party lives: the player is downed instead.</summary>
    [HarmonyPatch(typeof(GameOverHandler), nameof(GameOverHandler.hurt))]
    public static class GameOverHandlerHurtPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            // A failed check must leave the native game over in charge, never swallow the hurt.
            try { return !NetworkDamageSystem.ShouldSuppressNativeGameOver(); }
            catch (System.Exception e) { Guard.Swallow("DeathPatches.GameOver", e); return true; }
        }
    }

    // BlackSleekGuiSubs (native) ends the open dialogue when the local player is hurt. Make a cancelled yes/no
    // prompt read as "no": ItemPickup.release / UseItem answers read Dialoguer global bool 1, which could still
    // hold a stale "yes" from the previous prompt and grant the item to a player who never confirmed it.
    [HarmonyPatch(typeof(BlackSleekGuiSubs), "CancelDialogueOnDamageReceived")]
    public static class CancelDialogueAnswersNoPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            if (!NetworkDamageSystem.PartyLive) return;
            try { Dialoguer.SetGlobalBoolean(1, false); } catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.HurtElster))]
    public static class PlayerStateHurtElsterPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int __0, Vector2 __1, out bool __state)
        {
            __state = false;
            if (!NetworkDamageSystem.PartyLive) return true;
            if (NetworkDamageSystem.IsDead) return false; // downed: nothing can hurt us
            // No safe menus in co-op: a hit in the inventory / pause / book / event screen closes it, then lands.
            if (!MenuHit.Intercept(__0, __1, hug: false)) return false;
            __state = NetworkDamageSystem.HurtGateOpen();
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (!__state) return;
            try { NetworkDamageSystem.OnNativeHurt(); }
            catch (System.Exception e) { Guard.Swallow("DeathPatches.Hurt", e); }
        }
    }

    [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.HurtElsterHug))]
    public static class PlayerStateHurtElsterHugPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int __0, Vector2 __1, out bool __state)
        {
            __state = false;
            if (!NetworkDamageSystem.PartyLive) return true;
            if (NetworkDamageSystem.IsDead) return false;
            // Native Hug has no gameState gate: close an open screen first, then the grab damage lands.
            if (!MenuHit.Intercept(__0, __1, hug: true)) return false;
            __state = true;
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (!__state) return;
            try { NetworkDamageSystem.OnNativeHurt(); }
            catch (System.Exception e) { Guard.Swallow("DeathPatches.Hug", e); }
        }
    }
}
