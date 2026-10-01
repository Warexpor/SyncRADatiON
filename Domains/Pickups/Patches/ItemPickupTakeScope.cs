// While native ItemPickup.release runs, hasItem/getCount report the real bag, not the party key ring.
// release only adds when getCount(item) < maxNumber (Ghidra ItemPickup.c release): the ring masquerade
// (getCount = 1 for a ring key) made it skip the add for a unique key whose ring broadcast arrived first,
// so the PEN_Wreck cryo card was claimed, put on the ring, and never reached the taker's bag.
using HarmonyLib;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(ItemPickup), "release")]
    public static class ItemPickupTakeScope
    {
        static int _frame = -1;

        /// <summary>True inside native release (same frame as its prefix; self-clears if a postfix is skipped).</summary>
        public static bool Active => _frame >= 0 && _frame == Time.frameCount;

        [HarmonyPrefix]
        public static void Prefix() => _frame = Time.frameCount;

        [HarmonyPostfix]
        public static void Postfix() => _frame = -1;

        internal static void ResetSession() => _frame = -1;
    }
}
