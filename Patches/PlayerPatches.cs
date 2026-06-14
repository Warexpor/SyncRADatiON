using HarmonyLib;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(PlayerState), "Awake")]
    public static class PlayerStateAwakePatch
    {
        private static void Postfix()
        {
        }
    }
}
