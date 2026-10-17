// Gunshot wake — scene owner HandleRequest / follower emits Gunshot at player pos.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.fireGun))]
    public static class FireGunWakePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            // A native WakeUp inside a swapped enemy Update (EnemyTargetPatch): no player fired, nothing to wake.
            if (EnemyTargetPatch.Swapped) return;
            var player = PlayerState.player;
            if (player == null) return;
            var pos = player.transform.position;
            var msg = new InteractionRequestMessage
            {
                SenderPlayerId = LanNetworkManager.Instance.LocalPlayerId,
                Kind = InteractionKind.Gunshot,
                Float0 = pos.x,
                Float1 = pos.y,
                Float2 = pos.z
            };
            // The scene's owner wakes its enemies at once; a follower asks the owner (a client owner through the host).
            if (NetGate.SceneOwner)
                InteractionSyncService.HandleRequest(msg);
            else
                LanNetworkManager.Instance.InteractionHandlers.SendInteractionRequest(
                    0, InteractionKind.Gunshot, 0, 0, pos.x, pos.y, pos.z, "");
        }
    }
}
