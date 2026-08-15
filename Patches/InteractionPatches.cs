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
            UseItemInteractionPatch.OnSceneChanged();
            AirlockCinematic.Reset();
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
            if (LocalInspect.LockWorld(__instance.gameObject)) return true;
            try
            {
                if (__instance.inter != null && LocalInspect.LockWorld(__instance.inter.gameObject))
                    return true;
            }
            catch { }
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
            if (LocalInspect.LockWorld(__instance.gameObject)) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (!_fired.Add(id)) return;
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventZoneFire, id, 0, "");
        }
    }

    internal static class LocalInspect
    {
        public static bool Dialogue(Dialogue d)
        {
            if (d == null) return true;
            try
            {
                if (PlayerState.eventScreen) return true;
                var gs = PlayerState.gameState;
                if (gs == PlayerState.gameStates.eventScreen || gs == PlayerState.gameStates.book)
                    return true;
            }
            catch { }
            try
            {
                if (DialoguerFlavor((int)d._dialogue)) return true;
            }
            catch { }
            return UnderEventCamera(d.gameObject) || LockFlavor(d);
        }

        public static bool DialoguerFlavor(int id)
        {
            // Pickup / lock / one-liner flavor. DialoguerPatches IL-skip, so these
            // also have to be filtered on ApplyPresentation / InteractionRequest.
            switch (id)
            {
                case 0:  // noDialogue
                case 6:  // Pickup_dialogue
                case 17: // Pickup_cantCarry
                case 20: // GenericOneLine
                case 21: // openDoorDialogue
                case 22: // lockedDoorDialogue
                case 23: // useItemDialogue
                case 24: // GenericQuestion
                case 25: // Pickup_dialogue_long
                case 26: // Pickup_noSlots
                case 27: // GenericQuestionFollowup
                    return true;
                default:
                    return false;
            }
        }

        static bool LockFlavor(Dialogue d)
        {
            if (d == null) return false;
            Transform t = d.gameObject != null ? d.gameObject.transform : null;
            while (t != null)
            {
                try
                {
                    if (t.GetComponent<InteractiveLockSingle>() != null) return true;
                    if (t.GetComponent<InteractiveLock>() != null) return true;
                    if (t.GetComponent<ConnectedDoors>() != null) return true;
                    if (t.GetComponent<AutoTraverseDoor>() != null) return true;
                    if (t.GetComponent<UseItemInteraction>() != null) return true;
                    if (t.GetComponent<useItemPuzzleHint>() != null) return true;
                }
                catch { }
                t = t.parent;
            }
            return false;
        }

        static bool UnderEventCamera(GameObject go)
        {
            Transform t = go != null ? go.transform : null;
            while (t != null)
            {
                try
                {
                    if (t.GetComponent<EventScreen3DCam>() != null) return true;
                    if (t.GetComponent<EventScreenInteraction>() != null) return true;
                    if (t.GetComponent<EventOnlyRoom>() != null) return true;
                    if (t.GetComponent<EventScreen>() != null) return true;
                    if (t.GetComponent<ZoomInPoint>() != null) return true;
                    if (t.GetComponent<ItemPickup>() != null) return true;
                    if (t.GetComponent<ObservationDialogue>() != null) return true;
                    if (t.GetComponent<ObservationChoice>() != null) return true;
                    if (t.GetComponent<PEN_Airlock>() != null) return true;
                    if (t.GetComponent<PenroseAirlockNew>() != null) return true;
                    if (t.GetComponent<PenroseAirlock>() != null) return true;
                    if (t.GetComponent<PEN_Titles>() != null) return true;
                    if (t.GetComponent<AirlockInside>() != null) return true;
                    if (t.GetComponent<AirlockDoorLoadZone>() != null) return true;
                }
                catch { }
                t = t.parent;
            }
            return false;
        }

        public static bool Cinematic(GameObject go)
        {
            return UnderEventCamera(go);
        }

        public static bool LockWorld(GameObject go)
        {
            Transform t = go != null ? go.transform : null;
            while (t != null)
            {
                try
                {
                    if (t.GetComponent<InteractiveLockSingle>() != null) return true;
                    if (t.GetComponent<InteractiveLock>() != null) return true;
                    if (t.GetComponent<ConnectedDoors>() != null) return true;
                    if (t.GetComponent<AutoTraverseDoor>() != null) return true;
                    if (t.GetComponent<useItemPuzzleHint>() != null) return true;
                }
                catch { }
                t = t.parent;
            }
            try
            {
                if (go != null)
                {
                    var d = go.GetComponent<Dialogue>();
                    if (d != null && Dialogue(d)) return true;
                }
            }
            catch { }
            return false;
        }
    }

    internal static class AirlockCinematic
    {
        static readonly System.Collections.Generic.HashSet<ulong> _localUnlock
            = new System.Collections.Generic.HashSet<ulong>();
        static readonly System.Collections.Generic.HashSet<ulong> _remoteUnlock
            = new System.Collections.Generic.HashSet<ulong>();

        static string _personalScene;

        public static void Reset()
        {
            _localUnlock.Clear();
            _remoteUnlock.Clear();
        }

        public static void NotePersonalLoad(string scene)
        {
            if (!string.IsNullOrEmpty(scene))
                _personalScene = scene;
        }

        public static void NoteLocalUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            if (id == 0) return;
            _localUnlock.Add(id);
            _remoteUnlock.Remove(id);
        }

        public static void NoteRemoteUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            if (id == 0 || _localUnlock.Contains(id)) return;
            _remoteUnlock.Add(id);
        }

        public static bool IsRemoteUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            return id != 0 && _remoteUnlock.Contains(id) && !_localUnlock.Contains(id);
        }

        public static bool IsLocalUnlock(UseItemInteraction u)
        {
            ulong id = Id(u);
            return id != 0 && _localUnlock.Contains(id);
        }

        public static bool IsPenTitlesCard(UseItemInteraction x)
        {
            if (x == null) return false;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<PEN_Titles>();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].keyCardEvent == x)
                        return true;
                }
            }
            catch { }
            return false;
        }

        public static bool TryBeginLocal(Interaction inter)
        {
            if (inter == null || !NetGate.Live) return false;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<PEN_Titles>();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    bool match = false;
                    try
                    {
                        if (t.ViewPoint == inter) match = true;
                        else if (t.keyCardEvent != null && t.keyCardEvent.inter == inter) match = true;
                    }
                    catch { }
                    if (!match) continue;
                    if (t.started)
                        return false;
                    if (t.keyCardEvent != null)
                        NoteLocalUnlock(t.keyCardEvent);
                    try { t.started = false; } catch { }
                    PlaytestLog.Event("Story", "local PEN_Titles cinematic");
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsPersonalChapterLoad(string scene)
        {
            if (string.IsNullOrEmpty(scene) || SceneFollowService.IsTransient(scene)) return false;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<PEN_Titles>();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    bool started = false;
                    try { started = t.started; } catch { }
                    if (started || IsLocalUnlock(t.keyCardEvent))
                        return true;
                }
            }
            catch { }
            return false;
        }

        public static bool ShouldIgnoreHostFollow(string hostScene)
        {
            if (DeferFollowWhileAirlockPresent()) return true;
            try
            {
                string local = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
                if (!string.IsNullOrEmpty(_personalScene)
                    && string.Equals(local, _personalScene, System.StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(hostScene)
                    && !string.Equals(hostScene, local, System.StringComparison.Ordinal))
                    return true;
            }
            catch { }
            return false;
        }

        public static bool DeferFollowWhileAirlockPresent()
        {
            try { return UnityEngine.Object.FindObjectOfType<PEN_Titles>() != null; }
            catch { return false; }
        }

        static ulong Id(UseItemInteraction u)
        {
            if (u == null) return 0;
            return WorldId.FromGameObject(u.gameObject);
        }
    }

    [HarmonyPatch(typeof(UseItemInteraction), "Update")]
    public static class UseItemInteractionPatch
    {
        private static readonly System.Collections.Generic.HashSet<ulong> _sent
            = new System.Collections.Generic.HashSet<ulong>();

        public static void OnSceneChanged() => _sent.Clear();

        internal static void OnLocalUnlocked(UseItemInteraction u)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (u == null || !u.unlocked) return;
            ulong id = WorldId.FromGameObject(u.gameObject);
            if (id == 0) return;
            AirlockCinematic.NoteLocalUnlock(u);
            if (!_sent.Add(id)) return;
            if (!NetGate.Client) return;
            PlaytestLog.Event("Interact", "request UseItem (unlocked) id=" + id.ToString("X16"));
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.UseItem);
        }

        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance) => OnLocalUnlocked(__instance);
    }

    [HarmonyPatch(typeof(UseItemInteraction), "dialogueOver")]
    public static class UseItemDialogueOverPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance) => UseItemInteractionPatch.OnLocalUnlocked(__instance);
    }

    [HarmonyPatch(typeof(UseItemInteraction), nameof(UseItemInteraction.onMessageEvent))]
    public static class UseItemMessageEventPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance) => UseItemInteractionPatch.OnLocalUnlocked(__instance);
    }

    [HarmonyPatch(typeof(PEN_Titles), "Update")]
    public static class PenTitlesCinematicPatch
    {
        static ulong _skipLogged;

        [HarmonyPrefix]
        public static bool Prefix(PEN_Titles __instance)
        {
            if (__instance == null || !NetGate.Live) return true;
            if (__instance.keyCardEvent == null || !__instance.keyCardEvent.unlocked) return true;
            if (!AirlockCinematic.IsRemoteUnlock(__instance.keyCardEvent)) return true;
            bool started = false;
            try { started = __instance.started; } catch { }
            if (started) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id != _skipLogged)
            {
                _skipLogged = id;
                PlaytestLog.Event("Story", "hold PEN_Titles until local use");
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Interaction), nameof(Interaction.trigger))]
    public static class AirlockLocalTriggerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Interaction __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            AirlockCinematic.TryBeginLocal(__instance);
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
            PlaytestLog.Event("Interact", "KeypadSubmit " + inst.GetType().Name + " id=" + id.ToString("X16"));
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
            PlaytestLog.Event("Interact", "KeypadSubmit " + inst.GetType().Name + " id=" + id.ToString("X16"));
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.KeypadSubmit);
            var pad = inst as PEN_Codepad;
            if (pad != null)
                LanNetworkManager.Instance.PuzzleSync.Emit(PuzzleType.PEN_Codepad, id, pad);
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
                if (NetGate.Host) return;
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
                if (NetGate.Host) return;
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
                if (NetGate.Host) return;
                ClientKeypad.SubmitIfSolved(__instance, __instance.solved);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PEN_Codepad), "CheckSolution")]
    public static class PenCodepadSolvePatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Codepad __instance)
        {
            if (__instance == null || NetGate.IsApplying || !NetGate.Live) return;
            try
            {
                if (!__instance.solved) return;
                ulong id = WorldId.FromGameObject(__instance.gameObject);
                LanNetworkManager.Instance.PuzzleSync.EmitProgressed(PuzzleType.PEN_Codepad, id);
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
            if (LocalInspect.Cinematic(__instance.gameObject)) return true;
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
            if (LocalInspect.Cinematic(__instance.gameObject)) return true;
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

    // EventScreen / BookScreen are local inspect (notes, photos, keypad overlay, cryo
    // camera). Party-replaying them opens the document on every Elster and freezes
    // whoever wasn't at the interact. World results go through PuzzleSync.

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
            if (LocalInspect.Cinematic(__instance.gameObject)) return true;
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
