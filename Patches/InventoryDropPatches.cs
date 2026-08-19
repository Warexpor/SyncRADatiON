// Native inventory item menu: add DROP next to DESTROY (text list, no new sprites).
using HarmonyLib;
using SyncRADation.Networking;
using UnhollowerBaseLib;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(InventoryBase), "ToggleInteractMenu")]
    public static class InventoryDropMenuPatch
    {
        public const string DropLabel = "DROP";

        [HarmonyPostfix]
        public static void Postfix(InventoryBase __instance)
        {
            if (__instance == null) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            try { if (!__instance.intMenuOn) return; } catch { return; }
            try { if (InventoryBase.storeMode) return; } catch { }

            Il2CppStringArray old = null;
            try { old = __instance.intChoices; } catch { return; }
            if (old == null) return;

            int n = old.Length;
            for (int i = 0; i < n; i++)
            {
                if (old[i] == DropLabel) return;
            }

            var neu = new Il2CppStringArray(n + 1);
            for (int i = 0; i < n; i++)
                neu[i] = old[i];
            neu[n] = DropLabel;
            __instance.intChoices = neu;

            try
            {
                var tmp = __instance.IntMenuText;
                if (tmp != null && tmp.text != null && tmp.text.IndexOf(DropLabel) < 0)
                    tmp.text = tmp.text + "\n" + DropLabel;
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(InventoryBase), "InteractMenu")]
    public static class InventoryDropSelectPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(InventoryBase __instance)
        {
            if (__instance == null) return true;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return true;
            try { if (!__instance.intMenuOn) return true; } catch { return true; }

            Il2CppStringArray choices = null;
            try { choices = __instance.intChoices; } catch { return true; }
            if (choices == null) return true;

            int idx;
            try { idx = __instance.intMenuChoice; } catch { return true; }
            if (idx < 0 || idx >= choices.Length) return true;
            if (choices[idx] != InventoryDropMenuPatch.DropLabel) return true;
            if (!ConfirmPressed()) return true;

            AnItem item = null;
            try { item = __instance.intItem; } catch { }
            net.TryDropItem(item);
            return false;
        }

        static bool ConfirmPressed()
        {
            try
            {
                if (Input.GetButtonDown("Submit")) return true;
                if (Input.GetButtonDown("Fire1")) return true;
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    return true;
            }
            catch { }
            return false;
        }
    }
}
