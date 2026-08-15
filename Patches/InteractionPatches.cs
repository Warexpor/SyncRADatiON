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

        public static bool MarkMultiOnce(ulong id) => id != 0 && _fired.Add(id ^ 0x9E3779B97F4A7C15UL);

        public static bool MarkMultiTrigger(ulong id) => id != 0 && _fired.Add(id ^ 0xC2B2AE3D27D4EB4FUL);

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

            bool inRange = false;
            try { inRange = __instance.inter != null && __instance.inter.inRange; } catch { }
            if (!inRange) return true;

            try
            {
                ulong id = WorldId.FromGameObject(__instance.gameObject);
                if (id != 0 && _fired.Contains(id)) return false;
                float last;
                if (_lastRequest.TryGetValue(id, out last) && Time.unscaledTime - last < 0.25f)
                    return false;
                _lastRequest[id] = Time.unscaledTime;
                LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.EventZone);
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

    internal static class AirlockCinematic
    {
        static readonly System.Collections.Generic.HashSet<ulong> _localUnlock
            = new System.Collections.Generic.HashSet<ulong>();
        static readonly System.Collections.Generic.HashSet<ulong> _remoteUnlock
            = new System.Collections.Generic.HashSet<ulong>();

        static string _personalScene;
        static bool _localCinematic;
        static float _cinematicAt;

        public static void Reset()
        {
            _localUnlock.Clear();
            _remoteUnlock.Clear();
            _localCinematic = false;
            _cinematicAt = 0f;
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
                    _localCinematic = true;
                    _cinematicAt = Time.unscaledTime;
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
            if (DeferFollowWhileAirlockPresent()) return true;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<PEN_Titles>();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    if (IsLocalUnlock(t.keyCardEvent))
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
            if (PenTitlesStarted()) return true;
            if (_localCinematic)
            {
                if (Time.unscaledTime - _cinematicAt < 3f)
                    return true;
                _localCinematic = false;
            }
            return false;
        }

        static bool PenTitlesStarted()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<PEN_Titles>();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].started)
                        return true;
                }
            }
            catch { }
            return false;
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

        public static void NoteSolved(Component inst)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (inst == null) return;
            ulong id = WorldId.FromGameObject(inst.gameObject);
            if (id == 0 || !_sent.Add(id)) return;
            if (NetGate.Client)
            {
                PlaytestLog.Event("Interact", "KeypadSubmit " + inst.GetType().Name + " id=" + id.ToString("X16"));
                LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.KeypadSubmit);
            }
            var pad = inst as PEN_Codepad;
            if (pad != null)
                LanNetworkManager.Instance.PuzzleSync.Emit(PuzzleType.PEN_Codepad, id, pad);
        }
    }

    [HarmonyPatch(typeof(ROT_Keypad), "verify")]
    public static class RotKeypadVerifyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Keypad __instance)
        {
            if (__instance == null || NetGate.IsApplying || !NetGate.Live) return;
            try
            {
                if (!__instance.solved && !__instance.opening) return;
                ClientKeypad.NoteSolved(__instance);
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
                ClientKeypad.NoteSolved(__instance);
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
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.MultiCondition, 0);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(MultiConditionEvent __instance)
        {
            if (!NetGate.Host || NetGate.IsApplying || !NetGate.Live) return;
            if (__instance == null) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0 || !EventZonePatch.MarkMultiOnce(id)) return;
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(
                StoryCmd.MultiConditionFire, id, 0, "");
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
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.MultiCondition, 1);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(MultiConditionEvent __instance)
        {
            if (!NetGate.Host || NetGate.IsApplying || !NetGate.Live) return;
            if (__instance == null) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0 || !EventZonePatch.MarkMultiTrigger(id)) return;
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(
                StoryCmd.MultiConditionFire, id, 1, "");
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
