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

        /// <summary>SessionReset: drop the per-frame flag and the highlight reference (the highlighted clone dies with the session).</summary>
        internal static void ResetSession()
        {
            _interactThisFrame = false;
            _highlighted = null;
        }

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
            catch (System.Exception e) { Guard.Swallow(e); }
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
                    try { _highlighted.setInRange(false); } catch (System.Exception e) { Guard.Swallow(e); }
                    try { DroppedItemManager.SetHighlight(_highlighted.gameObject, false); } catch (System.Exception e) { Guard.Swallow(e); }
                }
                _highlighted = drop;
            }
            if (drop == null) return null;

            try { drop.setInRange(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try { DroppedItemManager.SetHighlight(drop.gameObject, true); } catch (System.Exception e) { Guard.Swallow(e); }

            Interaction cur = null;
            try { cur = inst.currentInter; } catch (System.Exception e) { Guard.Swallow(e); }
            bool ours = cur != null && DroppedItemManager.IsDroppedGo(cur.gameObject);
            if (cur == null || ours)
            {
                try { inst.currentInter = drop; } catch (System.Exception e) { Guard.Swallow(e); }
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
            // The game's interact is Rewired CharacterAction.Use (Interactor.Update reads
            // input.Use.WasPressed, Ghidra Interactor.c). Fire1 is the shoot button — never use it,
            // or shooting near a drop auto-takes it.
            try
            {
                var input = PlayerState.input;
                if (input != null && input.Use != null)
                    return input.Use.WasPressed;
            }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[DroppedInteract] input: " + ex.Message); }
            try
            {
                if (Input.GetButtonDown("Interact")) return true;
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }

            Transform t = null;
            try { t = __instance.transform; } catch { return true; }
            if (t == null) return true;

            var drop = DroppedItemManager.NearbyInteraction(t.position, 1.8f);
            if (drop == null) return true;

            Interaction cur = null;
            try { cur = __instance.currentInter; } catch (System.Exception e) { Guard.Swallow(e); }
            if (cur != null && !DroppedItemManager.IsDroppedGo(cur.gameObject))
                return true;

            try { __instance.currentInter = drop; } catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
