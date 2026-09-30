// Party key ring binds — held use pick, InteractItem args, display names, reject None adds.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(Interactor), nameof(Interactor.Interact))]
    public static class InteractorHeldUsePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Interactor __instance)
        {
            if (__instance == null || !NetGate.Live) return;
            AnItem held = null;
            try { held = InventoryManager.CurrentItem; } catch (System.Exception e) { Guard.Swallow(e); }
            if (held == null) return;
            Items.itemlist want;
            try { want = held._item; } catch { return; }
            if (want == Items.itemlist.None) return;

            Interaction pick = FromList(__instance, want);
            if (pick == null) pick = FromWorld(want);
            if (pick == null) return;
            try { __instance.currentInter = pick; } catch (System.Exception e) { Guard.Swallow(e); }
        }

        static Interaction FromList(Interactor inst, Items.itemlist want)
        {
            try
            {
                var list = inst.interactions;
                if (list == null) return null;
                for (int i = 0; i < list.Count; i++)
                {
                    var inter = MatchUse(list[i], want);
                    if (inter != null) return inter;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return null;
        }

        static Interaction FromWorld(Items.itemlist want)
        {
            try
            {
                var all = SyncRADation.Sync.WorldLookup.All<UseItemInteraction>();
                if (all == null) return null;
                for (int i = 0; i < all.Length; i++)
                {
                    var u = all[i];
                    if (u == null) continue;
                    bool inRange = false;
                    try { inRange = u.inter != null && u.inter.inRange; } catch (System.Exception e) { Guard.Swallow(e); }
                    if (!inRange) continue;
                    if (KeyIs(u, want)) return u.inter;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return null;
        }

        static Interaction MatchUse(Interaction inter, Items.itemlist want)
        {
            if (inter == null) return null;
            UseItemInteraction u = null;
            try { u = inter.GetComponent<UseItemInteraction>(); } catch (System.Exception e) { Guard.Swallow(e); }
            if (u == null)
            {
                try { u = inter.GetComponentInParent<UseItemInteraction>(); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (u == null || !KeyIs(u, want)) return null;
            return inter;
        }

        static bool KeyIs(UseItemInteraction u, Items.itemlist want)
        {
            if (u == null) return false;
            try { if (u.unlocked && !u.repeatable) return false; } catch (System.Exception e) { Guard.Swallow(e); }
            AnItem key = null;
            try { key = u.key; } catch (System.Exception e) { Guard.Swallow(e); }
            if (key == null) return false;
            try { return key._item == want; } catch { return false; }
        }
    }

    [HarmonyPatch(typeof(Interactor), nameof(Interactor.InteractItem))]
    public static class InteractorInteractItemPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ref AnItem item)
        {
            if (!NetGate.Live) return;
            PartyKeyRing.BindHeldArg(ref item);
        }
    }

    [HarmonyPatch(typeof(EventScreen3DCam), nameof(EventScreen3DCam.InteractItem))]
    public static class EventScreenInteractItemPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ref AnItem item)
        {
            if (!NetGate.Live) return;
            PartyKeyRing.BindHeldArg(ref item);
        }
    }

    [HarmonyPatch(typeof(InventoryBase), "useItem")]
    public static class InventoryUseItemPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ref AnItem Item)
        {
            if (!NetGate.Live) return;
            PartyKeyRing.BindHeldArg(ref Item);
        }
    }

    [HarmonyPatch(typeof(UseItemInteraction), nameof(UseItemInteraction.StartDialogue))]
    public static class UseItemDialogueNamePatch
    {
        [HarmonyPrefix]
        public static void Prefix(UseItemInteraction __instance)
        {
            Bind(__instance);
        }

        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance)
        {
            Bind(__instance);
        }

        internal static void Bind(UseItemInteraction u)
        {
            if (!NetGate.Live) return;
            AnItem key = null;
            if (u != null)
            {
                try { key = u.key; } catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (key == null)
            {
                try { key = UseItemInteraction.currentUseItem; } catch (System.Exception e) { Guard.Swallow(e); }
            }
            PartyKeyRing.BindUseDialogue(key);
        }
    }

    [HarmonyPatch(typeof(AnItem), nameof(AnItem.localizedName))]
    public static class ItemLocalizedNamePatch
    {
        static bool _resolving;

        /// <summary>SessionReset: re-entrancy flag.</summary>
        internal static void ResetSession() => _resolving = false;

        [HarmonyPrefix]
        public static bool Prefix(AnItem __instance, ref string __result)
        {
            if (_resolving || __instance == null || !NetGate.Live) return true;
            var cat = PartyKeyRing.CatalogOf(__instance);
            if (cat == null) return true;
            _resolving = true;
            try
            {
                __result = cat.localizedName();
                return PartyKeyRing.BadLoc(__result);
            }
            catch
            {
                return true;
            }
            finally { _resolving = false; }
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.getName))]
    public static class InventoryGetNamePatch
    {
        static bool _resolving;

        /// <summary>SessionReset: re-entrancy flag.</summary>
        internal static void ResetSession() => _resolving = false;

        [HarmonyPrefix]
        public static bool Prefix(AnItem item, ref string __result)
        {
            if (_resolving || item == null || !NetGate.Live) return true;
            var cat = PartyKeyRing.CatalogOf(item);
            if (cat == null) return true;
            _resolving = true;
            try
            {
                __result = InventoryManager.getName(cat);
                return PartyKeyRing.BadLoc(__result);
            }
            catch
            {
                return true;
            }
            finally { _resolving = false; }
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.AddItem), typeof(AnItem), typeof(int))]
    public static class InventoryAddItemNonePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(AnItem item)
        {
            if (!NetGate.Live) return true;
            if (item == null) return false;
            try
            {
                if (item._item == Items.itemlist.None) return false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.AddItem), typeof(AnItem))]
    public static class InventoryAddItemNoneNoCountPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(AnItem item)
        {
            return InventoryAddItemNonePatch.Prefix(item);
        }
    }
}
