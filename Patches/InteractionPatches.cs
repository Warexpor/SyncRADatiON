// Client world interactions become host requests; host still runs native.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(EventZone), "Update")]
    public static class EventZonePatch
    {
        private static readonly System.Collections.Generic.HashSet<ulong> _fired
            = new System.Collections.Generic.HashSet<ulong>();

        public static void OnSceneChanged()
        {
            _fired.Clear();
            _lastRequest.Clear();
            ClientKeypad.OnSceneChanged();
        }

        public static void MarkFired(ulong id)
        {
            if (id != 0) _fired.Add(id);
        }

        private static readonly System.Collections.Generic.Dictionary<ulong, float> _lastRequest
            = new System.Collections.Generic.Dictionary<ulong, float>();

        [HarmonyPrefix]
        public static bool Prefix(EventZone __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null || __instance.triggered) return true;
            if (NetGate.Host) return true;

            try
            {
                if (__instance.inter != null && __instance.inter.inRange)
                {
                    ulong id = WorldId.FromGameObject(__instance.gameObject);
                    float last;
                    if (_lastRequest.TryGetValue(id, out last) && Time.unscaledTime - last < 0.25f)
                        return false;
                    _lastRequest[id] = Time.unscaledTime;
                    LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.EventZone);
                }
            }
            catch { }
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(EventZone __instance)
        {
            if (!NetGate.Host || NetGate.IsApplying || !NetGate.Live) return;
            if (__instance == null || !__instance.triggered) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (!_fired.Add(id)) return;
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventZoneFire, id, 0, "");
        }
    }

    [HarmonyPatch(typeof(Dialogue), "OnTriggerEnter2D")]
    public static class DialogueTriggerPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Dialogue __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            if (NetGate.Host) return true;
            try
            {
                if (!__instance.autoTriggered) return true;
                if (!__instance.repeatable && __instance.playedOnce) return false;
            }
            catch { }
            LanNetworkManager.Instance.SendInteractionRequest(
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.DialogueStart);
            return false;
        }
    }

    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.StartDialogue))]
    public static class DialogueStartPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Dialogue __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            if (NetGate.Host)
            {
                ulong id = WorldId.FromGameObject(__instance.gameObject);
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.DialogueStart, id, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.DialogueStart);
            return false;
        }
    }

    [HarmonyPatch(typeof(UseItemInteraction), "Update")]
    public static class UseItemInteractionPatch
    {
        private static readonly System.Collections.Generic.Dictionary<ulong, float> _lastSend
            = new System.Collections.Generic.Dictionary<ulong, float>();

        [HarmonyPrefix]
        public static bool Prefix(UseItemInteraction __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            if (__instance.unlocked && !__instance.repeatable) return true;
            if (NetGate.Host) return true;

            try
            {
                if (__instance.inter == null || !__instance.inter.inRange) return false;
                ulong id = WorldId.FromGameObject(__instance.gameObject);
                float last;
                if (_lastSend.TryGetValue(id, out last) && Time.unscaledTime - last < 0.25f)
                    return false;
                _lastSend[id] = Time.unscaledTime;
                LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.UseItem);
            }
            catch { }
            return false;
        }
    }

    [HarmonyPatch(typeof(Keypad3D), "openDoor")]
    public static class Keypad3DOpenPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Keypad3D __instance) => ClientKeypad.Submit(__instance);
    }

    static class ClientKeypad
    {
        private static readonly System.Collections.Generic.HashSet<ulong> _sent
            = new System.Collections.Generic.HashSet<ulong>();

        public static void OnSceneChanged() => _sent.Clear();

        public static bool Submit(Component inst)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (inst == null) return true;
            if (NetGate.Host) return true;
            ulong id = WorldId.FromGameObject(inst.gameObject);
            if (id == 0 || !_sent.Add(id)) return false;
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.KeypadSubmit);
            return false;
        }

        public static void SubmitIfSolved(Component inst, bool solved)
        {
            if (!solved) return;
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (inst == null || NetGate.Host) return;
            ulong id = WorldId.FromGameObject(inst.gameObject);
            if (id == 0 || !_sent.Add(id)) return;
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.KeypadSubmit);
        }
    }

    [HarmonyPatch(typeof(Keypad3D), "Update")]
    public static class Keypad3DPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Keypad3D __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live || __instance == null) return;
            try
            {
                if (NetGate.Host)
                {
                    if (__instance.solved)
                        LanNetworkManager.Instance.PuzzleSync.RequestFullSend();
                }
                else
                    ClientKeypad.SubmitIfSolved(__instance, __instance.solved);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(ROT_Keypad), "Update")]
    public static class RotKeypadPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Keypad __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live || __instance == null) return;
            try
            {
                if (NetGate.Host)
                {
                    if (__instance.solved)
                        LanNetworkManager.Instance.PuzzleSync.RequestFullSend();
                }
                else
                    ClientKeypad.SubmitIfSolved(__instance, __instance.solved);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PEN_Codepad), "Update")]
    public static class PenCodepadPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Codepad __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live || __instance == null) return;
            try
            {
                if (NetGate.Host)
                {
                    if (__instance.solved)
                        LanNetworkManager.Instance.PuzzleSync.RequestFullSend();
                }
                else
                    ClientKeypad.SubmitIfSolved(__instance, __instance.solved);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(UseItemMultiInteraction), nameof(UseItemMultiInteraction.ready))]
    public static class UseItemMultiPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(UseItemMultiInteraction __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            if (NetGate.Host) return true;
            LanNetworkManager.Instance.SendInteractionRequest(
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.UseItemMulti);
            return false;
        }
    }

    [HarmonyPatch(typeof(CutsceneManager), nameof(CutsceneManager.Skip))]
    public static class CutsceneSkipMethodPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CutsceneManager __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneSkip, id, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.CutsceneSkip);
            return false;
        }
    }

    [HarmonyPatch(typeof(CutsceneManager), nameof(CutsceneManager.StartCutscene))]
    public static class CutsceneStartPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CutsceneManager __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart, id, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.CutsceneStart);
            return false;
        }
    }

    [HarmonyPatch(typeof(SkippableCutscene), nameof(SkippableCutscene.Check))]
    public static class CutsceneSkipPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(SkippableCutscene __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null || __instance.done) return true;
            if (NetGate.Host) return true;
            // Let local hold UI run; when skipEvent would fire, host still owns Skip via StartCutscene path.
            return true;
        }
    }

    [HarmonyPatch(typeof(EventScreenInteraction), nameof(EventScreenInteraction.startEventInstant))]
    public static class EventScreenStartPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(EventScreenInteraction __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventScreenStart, id, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.EventScreenStart);
            return false;
        }
    }

    [HarmonyPatch(typeof(EventScreenInteraction), nameof(EventScreenInteraction.exitEvent))]
    public static class EventScreenExitPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(EventScreenInteraction __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventScreenExit, id, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.EventScreenExit);
            return false;
        }
    }

    [HarmonyPatch(typeof(MultiConditionEvent), nameof(MultiConditionEvent.TryOnce))]
    public static class MultiConditionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MultiConditionEvent __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            if (NetGate.Host) return true;
            LanNetworkManager.Instance.SendInteractionRequest(
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.MultiCondition);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(MultiConditionEvent __instance)
        {
            if (!NetGate.Host || NetGate.IsApplying || !NetGate.Live) return;
            if (__instance == null) return;
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(
                StoryCmd.MultiConditionFire, WorldId.FromGameObject(__instance.gameObject), 0, "");
        }
    }

    [HarmonyPatch(typeof(MultiConditionEvent), nameof(MultiConditionEvent.TryTrigger))]
    public static class MultiConditionTriggerPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MultiConditionEvent __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            if (NetGate.Host) return true;
            LanNetworkManager.Instance.SendInteractionRequest(
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.MultiCondition);
            return false;
        }
    }

    [HarmonyPatch(typeof(CutsceneCut), nameof(CutsceneCut.Proceed))]
    public static class CutsceneProceedPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CutsceneCut __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneProceed, id, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.CutsceneProceed);
            return false;
        }
    }

    [HarmonyPatch(typeof(BookScreen), nameof(BookScreen.OpenBookMemory))]
    public static class BookMemoryPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BookScreen __instance, Book memo)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            string bookName = "";
            try { if (memo != null) bookName = memo.name; } catch { }
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.OpenBookMemory, 0, 0, bookName);
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.BookMemory, 0, 0, 0f, 0f, 0f, bookName);
            return false;
        }
    }

    [HarmonyPatch(typeof(BookScreen), nameof(BookScreen.OpenBook))]
    public static class BookOpenPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BookScreen __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            string bookName = "";
            try { if (__instance.book != null) bookName = __instance.book.name; } catch { }
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.BookOpen, 0, 0, bookName);
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.BookOpen, 0, 0, 0f, 0f, 0f, bookName);
            return false;
        }
    }

    [HarmonyPatch(typeof(EventScreenInteraction), nameof(EventScreenInteraction.startEventDelayed))]
    public static class EventScreenDelayedPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(EventScreenInteraction __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventScreenStart, id, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.EventScreenStart);
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.fireGun))]
    public static class FireGunWakePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            var player = PlayerState.player;
            if (player == null) return;
            var pos = player.transform.position;
            var msg = new InteractionRequestMessage
            {
                SenderPlayerId = LanNetworkManager.Instance.LocalPlayerId,
                Kind = InteractionKind.Gunshot,
                Float0 = pos.x,
                Float1 = pos.y,
                Float2 = pos.z
            };
            if (NetGate.Host)
                InteractionSyncService.HandleRequest(msg);
            else
                LanNetworkManager.Instance.SendInteractionRequest(
                    0, InteractionKind.Gunshot, 0, 0, pos.x, pos.y, pos.z, "");
        }
    }
}
