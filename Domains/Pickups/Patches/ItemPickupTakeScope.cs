// While native ItemPickup.release runs, getCount reports the real bag (no other-instance mapping).
// release only adds when getCount(item) < maxNumber (Ghidra ItemPickup.c release): any count the mod adds on top
// makes it skip the add (the PEN_Wreck cryo card was claimed and never reached the taker's bag).
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
