// Party key ring — held use pick, InteractItem args, display names, reject None adds, craft notes, hasItem/getCount masquerade.
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

    // ---- crafted / granted uniques: the AddItem result goes on the ring, consumed ingredients come off it

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.AddItem), typeof(AnItem), typeof(int))]
    public static class InventoryAddItemCountPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AnItem item) => NoteCraftedKey(item);

        /// <summary>Notes craft/grant <b>result</b> on AddItem. Ingredients: see CombineRecipesCraftPatch.</summary>
        internal static void NoteCraftedKey(AnItem item)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (!PartyKeyRing.IsKeyOrObject(item)) return;
            PartyKeyRing.OfferToHost(item);
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.AddItem), typeof(AnItem))]
    public static class InventoryAddItemPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AnItem item) => InventoryAddItemCountPatch.NoteCraftedKey(item);
    }

    /// <summary>
    /// CombineRecipes.combine success → PartyKeyRing.Remove ingredients (NoteCraftedKey
    /// only Offers the result). Mirrors ConsumeKey / DetachDroppedKey ring drops.
    /// </summary>
    [HarmonyPatch(typeof(CombineRecipes), nameof(CombineRecipes.combine))]
    public static class CombineRecipesCraftPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AnItem itemA, AnItem itemB, AnItem __result)
        {
            if (__result == null || NetGate.IsApplying || !NetGate.Live) return;
            try { PartyKeyRing.ConsumeCraftIngredients(itemA, itemB); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[KeyRing] craft remove: " + ex.Message); }
        }
    }

    // ---- ring masquerade: a key another peer holds answers hasItem / getCount on this peer.
    // Off inside native ItemPickup.release (ItemPickupTakeScope: release only adds when getCount < maxNumber)
    // and inside SaveManager.Save (it saves every hasItem item with its getCount into this player's bag).

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.hasItem), new[] { typeof(AnItem) })]
    public static class InventoryHasItemPatch
    {
        internal static bool Masquerading => NetGate.Live && !ItemPickupTakeScope.Active && !PartyKeyRing.MasqueradeOff;

        [HarmonyPostfix]
        public static void Postfix(AnItem item, ref bool __result)
        {
            if (__result || item == null || !Masquerading) return;
            if (PartyKeyRing.Has(item))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.hasItem), new[] { typeof(Items.itemlist) })]
    public static class InventoryHasItemEnumPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Items.itemlist item, ref bool __result)
        {
            if (__result || !InventoryHasItemPatch.Masquerading) return;
            if (PartyKeyRing.Has(item))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.getCount), new[] { typeof(AnItem) })]
    public static class InventoryGetCountPatch
    {
        static bool _counting;

        // Item types with no other bag instance, valid for one frame at one bag entry count. A miss can only
        // turn into a hit by adding a new bag entry (Count changes), so the cache never hides a real stack.
        // EquipmentSlots.Update alone calls getCount 3x a frame; each miss walked the whole bag.
        static readonly System.Collections.Generic.HashSet<int> _noOtherInstance = new System.Collections.Generic.HashSet<int>();
        static int _missFrame = -1;
        static int _missBagCount = -1;

        /// <summary>SessionReset: re-entrancy flag (cleared in case an exception path ever left it set) and the miss cache.</summary>
        internal static void ResetSession()
        {
            _counting = false;
            _noOtherInstance.Clear();
            _missFrame = -1;
            _missBagCount = -1;
        }

        static AnItem OtherBagInstance(AnItem item)
        {
            int frame = Time.frameCount;
            int bagCount = -1;
            try { var d = InventoryManager.elsterItems; bagCount = d != null ? d.Count : 0; }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (frame != _missFrame || bagCount != _missBagCount)
            {
                _noOtherInstance.Clear();
                _missFrame = frame;
                _missBagCount = bagCount;
            }
            if (bagCount == 0) return null;
            int kind;
            try { kind = (int)item._item; } catch { return PartyKeyRing.FindInBag(item); }
            if (_noOtherInstance.Contains(kind)) return null;
            var held = PartyKeyRing.FindInBag(item);
            if (held == null) _noOtherInstance.Add(kind);
            return held;
        }

        [HarmonyPostfix]
        public static void Postfix(AnItem item, ref int __result)
        {
            if (__result > 0 || item == null || _counting || !InventoryHasItemPatch.Masquerading) return;
            var held = OtherBagInstance(item);
            if (held != null && held != item)
            {
                _counting = true;
                try { __result = InventoryManager.getCount(held); }
                catch { __result = 1; }
                finally { _counting = false; }
                return;
            }
            if (PartyKeyRing.Has(item))
                __result = 1;
        }
    }

    // SaveManager.Save writes every hasItem item with its getCount into the saved bag (Ghidra SaveManager.c Save):
    // the masquerade is off for its duration so other peers' ring keys are not saved as this player's items.
    // (Story's SaveManagerSaveScopePatch suppresses the SProgress forward on the same call.)
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save))]
    public static class SaveManagerKeyRingScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => PartyKeyRing.SuspendMasquerade();
        [HarmonyFinalizer] public static void Finalizer() => PartyKeyRing.ResumeMasquerade();
    }

    // InventoryThumbs.ItemViewHandling (Ghidra InventoryThumbs.c): while the pickup item view shows a key, hasItem(queuedItem)
    // picks images[currentItems.IndexOf(queuedItem)]. The ring notes a picked key before native release adds it to the bag,
    // so the masquerade answered true with the key not yet in currentItems: IndexOf -1, ArgumentOutOfRangeException each
    // frame (test pilot LOV_Reeducation ObservationKey). The thumbs are this player's own bag: no masquerade.
    [HarmonyPatch(typeof(InventoryThumbs), nameof(InventoryThumbs.ItemViewHandling))]
    public static class InventoryThumbsItemViewScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => PartyKeyRing.SuspendMasquerade();
        [HarmonyFinalizer] public static void Finalizer() => PartyKeyRing.ResumeMasquerade();
    }
}
