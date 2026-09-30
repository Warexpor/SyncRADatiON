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
    /// Host (or offline solo host) load: the session now runs from that slot's last party save.
    /// Reset the party key ring to that save's snapshot, claimed uniques and floor drops so a
    /// wipe reload does not keep state the save never had. Clients keep their own saves untouched.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Load))]
    public static class SaveManagerLoadPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client) return;
            try
            {
                PartySaveService.OnHostLoaded();
                if (!NetGate.Host) return;
                PartyKeyRing.Import(PartySaveService.RingForCurrent());
                PartyKeyRing.Broadcast();
                DroppedItemManager.ClearAll();
                ItemPickupPatches.ResetDropClaims();
                net.PickupSync.Reset();
                PlaytestLog.Event("PartySave", "host load — ring/claims/floor drops reset");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] Load postfix failed: " + ex.Message);
            }
        }
    }

    /// <summary>Host save = party save: stamp a token, snapshot the key ring, tell clients to snapshot their bags.</summary>
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
                var token = PartySaveService.OnHostSaved();
                if (NetGate.Host)
                    net.PartyHandlers.SendPartySave(token, 0);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] Save postfix failed: " + ex.Message);
            }
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
