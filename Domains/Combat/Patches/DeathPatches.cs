// Co-op death: native game over is replaced by downed/revive/party-wipe while a party is live.
// Solo / no party: every prefix returns true and the game behaves like vanilla.
using HarmonyLib;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Players;
using SyncRADation.Sync;

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
            return !NetworkDamageSystem.ShouldSuppressNativeGameOver();
        }
    }

    [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.HurtElster))]
    public static class PlayerStateHurtElsterPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(out bool __state)
        {
            __state = false;
            if (!NetworkDamageSystem.PartyLive) return true;
            if (NetworkDamageSystem.IsDead) return false; // downed: nothing can hurt us
            __state = NetworkDamageSystem.HurtGateOpen();
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (__state) NetworkDamageSystem.OnNativeHurt();
        }
    }

    [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.HurtElsterHug))]
    public static class PlayerStateHurtElsterHugPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(out bool __state)
        {
            __state = false;
            if (!NetworkDamageSystem.PartyLive) return true;
            if (NetworkDamageSystem.IsDead) return false;
            __state = true;
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (__state) NetworkDamageSystem.OnNativeHurt();
        }
    }
}
