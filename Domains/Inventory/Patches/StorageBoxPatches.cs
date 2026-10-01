// Shared storage box gate: the host mutates its box natively and pushes the blob; a client sends a put/take request.
using HarmonyLib;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch]
    public static class StorageBoxInventoryPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.storeItem), new[] { typeof(AnItem), typeof(int) })]
        public static bool PrefixStore(AnItem item, int number) => GateBox(item, number, true);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.retrieveItem), new[] { typeof(AnItem), typeof(int) })]
        public static bool PrefixRetrieve(AnItem item, int number) => GateBox(item, number, false);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.boxItem), new[] { typeof(AnItem), typeof(int) })]
        public static bool PrefixBox(AnItem item, int number) => GateBox(item, number, true);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.unboxItem), new[] { typeof(AnItem) })]
        public static bool PrefixUnbox(AnItem item) => GateBox(item, 1, false);

        // No-count overloads — storage UI can call these and would bypass the int gates.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.storeItem), new[] { typeof(AnItem) })]
        public static bool PrefixStore1(AnItem item) => GateBox(item, 1, true);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.retrieveItem), new[] { typeof(AnItem) })]
        public static bool PrefixRetrieve1(AnItem item) => GateBox(item, 1, false);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.boxItem), new[] { typeof(AnItem) })]
        public static bool PrefixBox1(AnItem item) => GateBox(item, 1, true);

        private static bool GateBox(AnItem item, int number, bool put)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (item == null) return true;
            int n = number > 0 ? number : 1;
            bool unique = PartyKeyRing.IsKeyOrObject(item);
            if (unique && n > 1) n = 1;
            if (NetGate.Host)
            {
                // Host put of unique already in box: absorb bag copy, do not stack.
                if (put && unique && ItemBag.BoxStock(item) >= 1)
                {
                    try { InventoryManager.RemoveItem(item, n); } catch (System.Exception e) { Guard.Swallow(e); }
                    PlaytestLog.Event("StorageBox", "host put absorb unique item=" + SafeEnum(item));
                    FlushHostBoxBlob();
                    return false;
                }
                // Prefix only flags; Postfix pushes blob after native mutates host box.
                LanNetworkManager.Instance.StorageSync.RequestSend();
                return true;
            }
            int enumVal = SafeEnum(item);
            if (enumVal < 0) return false;
            // One storage transaction in flight per client: a double press before the ack would
            // otherwise box twice but only remove once. Put reserves the bag copy up front.
            StorageTxn.TrySend(put, item, enumVal, n);
            return false;
        }

        static int SafeEnum(AnItem item)
        {
            try { return (int)item._item; } catch { return -1; }
        }

        static void FlushHostBoxBlob()
        {
            if (NetGate.IsApplying || !NetGate.Live || !NetGate.Host) return;
            try { LanNetworkManager.Instance?.StorageSync.FlushDiffNow(); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.storeItem), new[] { typeof(AnItem) })]
        public static void PostStore1(AnItem item) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.retrieveItem), new[] { typeof(AnItem) })]
        public static void PostRetrieve1(AnItem item) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.boxItem), new[] { typeof(AnItem) })]
        public static void PostBox1(AnItem item) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.storeItem), new[] { typeof(AnItem), typeof(int) })]
        public static void PostStore(AnItem item, int number) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.retrieveItem), new[] { typeof(AnItem), typeof(int) })]
        public static void PostRetrieve(AnItem item, int number) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.boxItem), new[] { typeof(AnItem), typeof(int) })]
        public static void PostBox(AnItem item, int number) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.unboxItem), new[] { typeof(AnItem) })]
        public static void PostUnbox(AnItem item) => FlushHostBoxBlob();
    }
}
