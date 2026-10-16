// Diagnostics trace of native bag adds (pickup gain measurement, test pilot).
using HarmonyLib;
using SyncRADation.Config;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    /// <summary>
    /// InventoryManager.AddItemToMax (Ghidra InventoryManager.c) is native ItemPickup.release's bag add: it returns
    /// what did not fit. Logged with the bag count around it so a pickup's measured gain can be checked against it.
    /// </summary>
    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.AddItemToMax))]
    public static class InventoryAddItemToMaxTracePatch
    {
        [HarmonyPrefix]
        public static void Prefix(AnItem item, out int __state)
        {
            __state = -1;
            if (!ModConfig.DiagnosticsOn || !NetGate.Live || item == null) return;
            try { __state = ItemBag.CountInBag(item._item); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        [HarmonyPostfix]
        public static void Postfix(AnItem item, int number, int __result, int __state)
        {
            if (__state < 0 || item == null) return;
            try
            {
                PlaytestLog.Event("Bag", "AddItemToMax " + item._item + " x" + number + " left=" + __result
                    + " max=" + item.maxNumber + " bag " + __state + "->" + ItemBag.CountInBag(item._item));
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
