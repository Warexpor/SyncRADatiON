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
            InteractionSyncService.OnSceneChanged();
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
            // Host still runs native (keycard slot EventZones). Client skips so
            // airlock titles stay local and are not remoted as a party EventZone.
            if (LocalInspect.AirlockCinematic(__instance.gameObject))
                return NetGate.Host;
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
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return;
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

        public static bool ShouldHoldTitles(PEN_Titles t)
        {
            if (t == null) return false;
            try { if (t.started) return false; } catch { return false; }
            UseItemInteraction card = null;
            try { card = t.keyCardEvent; } catch { }
            if (card == null) return false;
            try { if (!card.unlocked) return false; } catch { return false; }
            return !IsLocalUnlock(card);
        }

        public static bool ShouldHoldGo(GameObject go)
        {
            if (go == null) return false;
            Transform t = go.transform;
            while (t != null)
            {
                try
                {
                    var titles = t.GetComponent<PEN_Titles>();
                    if (titles != null && ShouldHoldTitles(titles)) return true;
                }
                catch { }
                t = t.parent;
            }
            return false;
        }

        internal static PEN_Titles[] AllTitles()
        {
            try { return Object.FindObjectsOfType<PEN_Titles>(); }
            catch { return null; }
        }

        public static void KeepTitlesPrompt(PEN_Titles t)
        {
            if (t == null) return;
            try
            {
                if (t.keyCardEvent != null)
                    KeepUsePrompt(t.keyCardEvent);
            }
            catch { }
            try
            {
                var vp = t.ViewPoint;
                if (vp != null)
                {
                    vp.triggered = false;
                    vp.enabled = true;
                }
            }
            catch { }
        }

        public static void KeepUsePrompt(UseItemInteraction u)
        {
            if (u == null) return;
            try
            {
                if (u.inter != null)
                {
                    u.inter.triggered = false;
                    u.inter.enabled = true;
                }
            }
            catch { }
        }

        public static void ArmTitlesSkip(PEN_Titles t)
        {
            if (t == null) return;
            // CutsceneSkippingUI is for CutsceneManager. PEN_Titles has its own skipper;
            // arming both made the hold bar fight the titles coroutine.
            try
            {
                if (t.skipper != null)
                    t.skipper.enabled = true;
            }
            catch { }
        }

        public static bool IsPenTitlesCard(UseItemInteraction x)
        {
            if (x == null) return false;
            try
            {
                var all = AllTitles();
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
                var all = AllTitles();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    bool match = false;
                    try
                    {
                        if (t.keyCardEvent != null && t.keyCardEvent.inter == inter)
                            match = true;
                        else if (t.ViewPoint == inter)
                        {
                            bool unlocked = false;
                            try { unlocked = t.keyCardEvent != null && t.keyCardEvent.unlocked; }
                            catch { }
                            match = unlocked;
                        }
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
            if (!string.Equals(scene, "PEN_Hole", System.StringComparison.Ordinal)) return false;
            if (DeferFollowWhileAirlockPresent()) return true;
            try
            {
                var all = AllTitles();
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

        static bool IsWreckOrHole(string scene)
        {
            return string.Equals(scene, "PEN_Wreck", System.StringComparison.Ordinal)
                || string.Equals(scene, "PEN_Hole", System.StringComparison.Ordinal);
        }

        public static bool IsWreckHoleSplit(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            bool aWreck = string.Equals(a, "PEN_Wreck", System.StringComparison.Ordinal);
            bool bWreck = string.Equals(b, "PEN_Wreck", System.StringComparison.Ordinal);
            bool aHole = string.Equals(a, "PEN_Hole", System.StringComparison.Ordinal);
            bool bHole = string.Equals(b, "PEN_Hole", System.StringComparison.Ordinal);
            return (aWreck && bHole) || (aHole && bWreck);
        }

        public static bool ShouldIgnoreHostFollow(string hostScene)
        {
            if (string.IsNullOrEmpty(hostScene)) return false;
            string local = "";
            try { local = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch { }
            // Host left Penrose — follow (LOV etc.). Stale _personalScene=PEN_Hole used to
            // trap the client in the hole after LOV_Reeducation loaded (pause-only freeze).
            if (!IsWreckOrHole(hostScene))
            {
                _personalScene = null;
                return false;
            }
            // Wreck↔hole is per-Elster. Requiring local PEN_Titles meant the observer
            // still on the wreck got SceneFollow when the host skipped the airlock.
            if (IsWreckHoleSplit(local, hostScene))
                return true;
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
                var all = AllTitles();
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

        internal static void OnLocalUnlocked(UseItemInteraction u, bool fromUpdate)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (u == null || !u.unlocked) return;
            ulong id = WorldId.FromGameObject(u.gameObject);
            if (id == 0) return;
            if (!fromUpdate || !AirlockCinematic.IsPenTitlesCard(u))
                AirlockCinematic.NoteLocalUnlock(u);
            if (!_sent.Add(id)) return;
            if (!NetGate.Client) return;
            PlaytestLog.Event("Interact", "request UseItem (unlocked) id=" + id.ToString("X16"));
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.UseItem);
        }

        [HarmonyPrefix]
        public static bool Prefix(UseItemInteraction __instance)
        {
            if (!NetGate.Live || __instance == null) return true;
            try
            {
                if (__instance.inter != null && __instance.inter.inRange)
                {
                    __instance.key = PartyKeyRing.BindSceneKey(__instance.key);
                    if (InteractorDropUpdatePatch.InteractPressed())
                    {
                        string cur = "";
                        try
                        {
                            var c = InventoryManager.CurrentItem;
                            cur = c != null ? c._item.ToString() : "none";
                        }
                        catch { cur = "?"; }
                        PlaytestLog.Event("Interact", "UseItem press " + __instance.gameObject.name
                            + " key=" + (__instance.key != null ? __instance.key._item.ToString() : "null")
                            + " current=" + cur
                            + " unlocked=" + __instance.unlocked);
                    }
                }
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance) => OnLocalUnlocked(__instance, true);
    }

    [HarmonyPatch(typeof(Interactor), nameof(Interactor.Interact))]
    public static class InteractorHeldUsePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Interactor __instance)
        {
            if (__instance == null || !NetGate.Live) return;
            AnItem held = null;
            try { held = InventoryManager.CurrentItem; } catch { }
            if (held == null) return;
            Items.itemlist want;
            try { want = held._item; } catch { return; }
            if (want == Items.itemlist.None) return;

            Interaction pick = FromList(__instance, want);
            if (pick == null) pick = FromWorld(want);
            if (pick == null) return;
            try { __instance.currentInter = pick; } catch { }
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
            catch { }
            return null;
        }

        static Interaction FromWorld(Items.itemlist want)
        {
            try
            {
                var all = Object.FindObjectsOfType<UseItemInteraction>();
                if (all == null) return null;
                for (int i = 0; i < all.Length; i++)
                {
                    var u = all[i];
                    if (u == null) continue;
                    bool inRange = false;
                    try { inRange = u.inter != null && u.inter.inRange; } catch { }
                    if (!inRange) continue;
                    if (KeyIs(u, want)) return u.inter;
                }
            }
            catch { }
            return null;
        }

        static Interaction MatchUse(Interaction inter, Items.itemlist want)
        {
            if (inter == null) return null;
            UseItemInteraction u = null;
            try { u = inter.GetComponent<UseItemInteraction>(); } catch { }
            if (u == null)
            {
                try { u = inter.GetComponentInParent<UseItemInteraction>(); } catch { }
            }
            if (u == null || !KeyIs(u, want)) return null;
            return inter;
        }

        static bool KeyIs(UseItemInteraction u, Items.itemlist want)
        {
            if (u == null) return false;
            try { if (u.unlocked && !u.repeatable) return false; } catch { }
            AnItem key = null;
            try { key = u.key; } catch { }
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
            AnItem key = null;
            if (u != null)
            {
                try { key = u.key; } catch { }
            }
            if (key == null)
            {
                try { key = UseItemInteraction.currentUseItem; } catch { }
            }
            PartyKeyRing.BindUseDialogue(key);
        }
    }

    [HarmonyPatch(typeof(AnItem), nameof(AnItem.localizedName))]
    public static class ItemLocalizedNamePatch
    {
        static bool _resolving;

        [HarmonyPrefix]
        public static bool Prefix(AnItem __instance, ref string __result)
        {
            if (_resolving || __instance == null) return true;
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

        [HarmonyPrefix]
        public static bool Prefix(AnItem item, ref string __result)
        {
            if (_resolving || item == null) return true;
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
            catch { }
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

    [HarmonyPatch(typeof(UseItemInteraction), "dialogueOver")]
    public static class UseItemDialogueOverPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance) => UseItemInteractionPatch.OnLocalUnlocked(__instance, false);
    }

    [HarmonyPatch(typeof(UseItemInteraction), nameof(UseItemInteraction.onMessageEvent))]
    public static class UseItemMessageEventPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance) => UseItemInteractionPatch.OnLocalUnlocked(__instance, false);
    }

    [HarmonyPatch(typeof(PEN_Titles), "Update")]
    public static class PenTitlesCinematicPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Titles __instance)
        {
            if (__instance == null || !NetGate.Live) return;
            try
            {
                if (__instance.started)
                {
                    CutsceneSkippingUI.skippableCutscene = false;
                    AirlockCinematic.ArmTitlesSkip(__instance);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PEN_Titles), "Skip")]
    public static class PenTitlesSkipPatch
    {
        [HarmonyPrefix]
        public static void Prefix(PEN_Titles __instance)
        {
            if (__instance == null || !NetGate.Live) return;
            AirlockCinematic.ArmTitlesSkip(__instance);
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
            if (__instance == null) return true;
            if (NetGate.IsApplying)
            {
                try { if (__instance.completed) return false; } catch { }
                try { if (__instance.cutscene == null) return false; } catch { }
                return true;
            }
            if (!NetGate.Live) return true;
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            InteractionSyncService.RememberSkip(id);
            bool running = true;
            try { running = __instance.cutscene != null && !__instance.completed; } catch { }
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneSkip, id, 0, "");
                return running;
            }
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.CutsceneSkip);
            return running;
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
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return true;
            try { if (__instance.completed) return false; } catch { }
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (InteractionSyncService.WasSkipped(id)) return false;
            InteractionSyncService.RememberStart(id);
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart, id, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.CutsceneStart);
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(CutsceneManager __instance)
        {
            if (__instance == null || !NetGate.Live) return;
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return;
            try
            {
                if (!__instance.unskippable)
                    CutsceneSkippingUI.skippableCutscene = true;
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.TogglePauseAnywhere))]
    public static class PauseDuringCutscenePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!NetGate.Live) return true;
            try
            {
                var all = AirlockCinematic.AllTitles();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var t = all[i];
                        if (t == null) continue;
                        bool started = false;
                        try { started = t.started; } catch { }
                        if (!started) continue;
                        try { CutsceneSkippingUI.skippableCutscene = false; } catch { }
                        AirlockCinematic.ArmTitlesSkip(t);
                        return false;
                    }
                }
            }
            catch { }
            if (ArmWorldSkippers())
            {
                try { CutsceneSkippingUI.skippableCutscene = true; } catch { }
                return false;
            }
            bool inCut = false;
            try { inCut = PlayerState.cutscene || PlayerState.gameState == PlayerState.gameStates.cutscene; } catch { }
            try { inCut = inCut || CutsceneSkippingUI.skippableCutscene; } catch { }
            if (!inCut)
            {
                try
                {
                    var all = UnityEngine.Object.FindObjectsOfType<CutsceneManager>();
                    if (all != null)
                    {
                        for (int i = 0; i < all.Length; i++)
                        {
                            var c = all[i];
                            if (c == null) continue;
                            try
                            {
                                if (c.cutscene != null && !c.completed)
                                {
                                    inCut = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            if (!inCut) return true;

            CutsceneManager target = null;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<CutsceneManager>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var c = all[i];
                        if (c == null) continue;
                        try
                        {
                            if (LocalInspect.AirlockCinematic(c.gameObject)) continue;
                            if (c.cutscene != null || (c.skipper != null && !c.skipper.done))
                            {
                                target = c;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            if (target == null) return true;
            try { target.Skip(); } catch { }
            return false;
        }

        static bool ArmWorldSkippers()
        {
            bool armed = false;
            try
            {
                var holes = UnityEngine.Object.FindObjectsOfType<PEN_HoleSnowblind>();
                if (holes != null)
                {
                    for (int i = 0; i < holes.Length; i++)
                    {
                        var h = holes[i];
                        if (h == null) continue;
                        armed |= ArmSkipper(h.skipper) | ArmSkipper(h.skipper2);
                    }
                }
            }
            catch { }
            try
            {
                var ends = UnityEngine.Object.FindObjectsOfType<PEN_CodeRoomEnd>();
                if (ends != null)
                {
                    for (int i = 0; i < ends.Length; i++)
                    {
                        var e = ends[i];
                        if (e == null) continue;
                        armed |= ArmSkipper(e.skipper);
                    }
                }
            }
            catch { }
            return armed;
        }

        static bool ArmSkipper(SkippableCutscene s)
        {
            if (s == null) return false;
            try
            {
                bool done = false;
                try { done = s.done; } catch { }
                if (done) return false;
                s.enabled = true;
                return true;
            }
            catch { return false; }
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
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return true;
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
