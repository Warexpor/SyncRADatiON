// Cloned drops are often missed by Interactor's physics query.
using HarmonyLib;
using SyncRADation.ItemSystem;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(Interactor), "Update")]
    public static class InteractorDropUpdatePatch
    {
        static bool _interactThisFrame;

        [HarmonyPrefix]
        public static void Prefix(Interactor __instance)
        {
            _interactThisFrame = false;
            FeedCurrent(__instance);
        }

        [HarmonyPostfix]
        public static void Postfix(Interactor __instance)
        {
            if (__instance == null) return;
            if (!InPlay())
            {
                FeedCurrent(__instance);
                return;
            }

            var drop = FeedCurrent(__instance);
            if (drop == null) return;
            if (_interactThisFrame) return;
            if (!InteractPressed()) return;
            try { __instance.Interact(); }
            catch { }
        }

        static Interaction _highlighted;

        static Interaction FeedCurrent(Interactor inst)
        {
            if (inst == null) return null;
            Transform t = null;
            try { t = inst.transform; } catch { return null; }
            if (t == null) return null;

            var drop = DroppedItemManager.NearbyInteraction(t.position, 1.8f);
            if (drop != _highlighted)
            {
                if (_highlighted != null)
                {
                    try { _highlighted.setInRange(false); } catch { }
                    try { DroppedItemManager.SetHighlight(_highlighted.gameObject, false); } catch { }
                }
                _highlighted = drop;
            }
            if (drop == null) return null;

            try { drop.setInRange(true); } catch { }
            try { DroppedItemManager.SetHighlight(drop.gameObject, true); } catch { }

            Interaction cur = null;
            try { cur = inst.currentInter; } catch { }
            bool ours = cur != null && DroppedItemManager.IsDroppedGo(cur.gameObject);
            if (cur == null || ours)
            {
                try { inst.currentInter = drop; } catch { }
            }
            return drop;
        }

        static bool InPlay()
        {
            try
            {
                var gs = PlayerState.gameState;
                return gs == PlayerState.gameStates.play;
            }
            catch { return true; }
        }

        internal static void MarkInteracted() => _interactThisFrame = true;

        internal static bool InteractPressed()
        {
            try
            {
                if (Input.GetButtonDown("Submit")) return true;
                if (Input.GetButtonDown("Fire1")) return true;
                if (Input.GetButtonDown("Interact")) return true;
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    return true;
            }
            catch { }
            return false;
        }
    }

    [HarmonyPatch(typeof(Interactor), nameof(Interactor.Interact))]
    public static class InteractorDropInteractPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Interactor __instance)
        {
            InteractorDropUpdatePatch.MarkInteracted();
            if (__instance == null) return true;

            try
            {
                var gs = PlayerState.gameState;
                if (gs != PlayerState.gameStates.play)
                    return true;
            }
            catch { }

            Transform t = null;
            try { t = __instance.transform; } catch { return true; }
            if (t == null) return true;

            var drop = DroppedItemManager.NearbyInteraction(t.position, 1.8f);
            if (drop == null) return true;

            Interaction cur = null;
            try { cur = __instance.currentInter; } catch { }
            if (cur != null && !DroppedItemManager.IsDroppedGo(cur.gameObject))
                return true;

            try { __instance.currentInter = drop; } catch { }
            return true;
        }
    }

    [HarmonyPatch(typeof(Interaction), nameof(Interaction.trigger))]
    public static class DroppedTriggerArmPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Interaction __instance)
        {
            if (__instance == null) return;
            try
            {
                var p = __instance.GetComponent<ItemPickup>();
                if (p == null) p = __instance.GetComponentInParent<ItemPickup>();
                ItemPickupPatches.ArmDropped(p);
            }
            catch { }
        }

        [HarmonyPostfix]
        public static void Postfix(Interaction __instance)
        {
            if (__instance == null) return;
            try
            {
                var p = __instance.GetComponent<ItemPickup>();
                if (p == null) p = __instance.GetComponentInParent<ItemPickup>();
                ItemPickupPatches.CommitDroppedIfTaken(p);
            }
            catch { }
        }
    }
}
