// Cutscene Start / Skip / Proceed + pause-during-cutscene skip remap.
// Preserve Start vs Proceed asymmetry as-is (do not unify in this peel).
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(CutsceneManager), nameof(CutsceneManager.Skip))]
    public static class CutsceneSkipMethodPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CutsceneManager __instance)
        {
            if (__instance == null) return true;
            if (NetGate.IsApplying)
            {
                try { if (__instance.completed) return false; } catch (System.Exception e) { Guard.Swallow(e); }
                try { if (__instance.cutscene == null) return false; } catch (System.Exception e) { Guard.Swallow(e); }
                return true;
            }
            if (!NetGate.Party) return true;
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            InteractionSyncService.RememberSkip(id);
            bool running = true;
            try { running = __instance.cutscene != null && !__instance.completed; } catch (System.Exception e) { Guard.Swallow(e); }
            if (NetGate.Host)
            {
                // Host skipped natively in its own cutscene: it counted any END effects of the skip events.
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneSkip, id, 0, StoryWire.HostCounted);
                return running;
            }
            // Wreck / hole split: the host has no such cutscene; the skip stays local like the start did.
            if (!AirlockCinematic.ClientSplitFromHost())
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
            if (!NetGate.Party)
            {
                // A host with nobody connected stays vanilla (no dedupe, no skip checks) but records the start so a
                // peer joining mid-cutscene gets it replayed.
                if (NetGate.Host && !NetGate.IsApplying && __instance != null)
                {
                    try
                    {
                        if (!LocalInspect.AirlockCinematic(__instance.gameObject))
                            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart,
                                WorldId.FromGameObject(__instance.gameObject), 0, "");
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
                return true;
            }
            if (NetGate.IsApplying)
            {
                // A client replaying the host's DetermineEnding runs Finale.determineEnding natively, which starts the
                // ending cutscene; the host's CutsceneStart for it usually arrived first and is already running.
                if (StorySyncService.InEndingApply && __instance != null)
                {
                    try
                    {
                        ulong eid = WorldId.FromGameObject(__instance.gameObject);
                        if (InteractionSyncService.StartedHere(__instance) || InteractionSyncService.StartedRecently(eid))
                            return false;
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
                return true;
            }
            if (__instance == null) return true;
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return true;
            try { if (__instance.completed) return false; } catch (System.Exception e) { Guard.Swallow(e); }
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (InteractionSyncService.WasSkipped(id)) return false;
            // Wreck / hole split (per-player scenes): the host is not in this scene, so asking it would never start the
            // cutscene (the hole's end loads LOV_Reeducation). Run it here like solo.
            if (AirlockCinematic.ClientSplitFromHost()) return true;
            // Host runs native + broadcasts. A client does not start it from here: it asks the host, and the
            // host's CutsceneStart presentation replay starts it on the requester like every other peer.
            // The client must NOT stamp "started" here (RememberStart): that made the replay look like a
            // duplicate, so the requester never played it and repeatable cutscenes stayed dead.
            if (NetGate.Host)
            {
                // A client request (ApplyCutscene) may have started it inside the dedupe window: a second native
                // StartCutscene has no already-started guard and would run a second coroutine. The earlier start
                // already broadcast, so just swallow this one.
                if (!InteractionSyncService.RememberStart(id))
                {
                    PlaytestLog.Event("Story", "host StartCutscene dup id=" + id.ToString("X16"));
                    return false;
                }
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart, id, 0, "");
                return true;
            }
            if (InteractionSyncService.ShouldRequestStart(id))
                LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.CutsceneStart);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(CutsceneManager __instance)
        {
            if (__instance == null || !NetGate.Party) return;
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return;
            try
            {
                if (!__instance.unskippable)
                    CutsceneSkippingUI.skippableCutscene = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.TogglePauseAnywhere))]
    public static class PauseDuringCutscenePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!NetGate.Party) return true;
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
                        try { started = t.started; } catch (System.Exception e) { Guard.Swallow(e); }
                        if (!started) continue;
                        try { CutsceneSkippingUI.skippableCutscene = false; } catch (System.Exception e) { Guard.Swallow(e); }
                        AirlockCinematic.ArmTitlesSkip(t);
                        return false;
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            // No PEN_HoleSnowblind / PEN_CodeRoomEnd branch: SkippableCutscene has no Update / OnEnable (only Check /
            // continousCheck), so "arming" one did nothing, and any not-done skipper in PEN_Hole blocked the pause menu.
            bool inCut = false;
            try { inCut = PlayerState.cutscene || PlayerState.gameState == PlayerState.gameStates.cutscene; } catch (System.Exception e) { Guard.Swallow(e); }
            try { inCut = inCut || CutsceneSkippingUI.skippableCutscene; } catch (System.Exception e) { Guard.Swallow(e); }

            // One scene-cached scan (not Update) — detect running + pick skip target. Only a manager whose coroutine is
            // live counts (CutsceneManager.Cutscene sets completed after its last cut): an unplayed one also has
            // skipper.done == false, and skipping it stamped it skipped (30 s start block) while the real one played on.
            CutsceneManager target = null;
            var managers = WorldLookup.All<CutsceneManager>();
            if (managers != null)
            {
                for (int i = 0; i < managers.Length; i++)
                {
                    var c = managers[i];
                    if (c == null) continue;
                    try
                    {
                        if (c.cutscene == null || c.completed) continue;
                        inCut = true;
                        if (target == null && !LocalInspect.AirlockCinematic(c.gameObject))
                            target = c;
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            if (!inCut) return true;
            if (target == null) return true;
            try { target.Skip(); } catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }
    }

    [HarmonyPatch(typeof(CutsceneCut), nameof(CutsceneCut.Proceed))]
    public static class CutsceneProceedPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CutsceneCut __instance)
        {
            // CutsceneCut.Proceed shares RVA 0x516A00 with END_Boss.StartBattle and XmlSerializationWriter.TopLevelElement
            // (script.json, identical code folding): the detour also fires for those, with a foreign `this`.
            if (!Il2CppRealType.Is<CutsceneCut>(__instance)) return true;
            if (NetGate.IsApplying || !NetGate.Party) return true;
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return true;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneProceed, id, 0, StoryWire.HostCounted);
                return true;
            }
            // Wreck / hole split: the host has no such cut, a request would leave this cutscene stuck.
            if (AirlockCinematic.ClientSplitFromHost()) return true;
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.CutsceneProceed);
            return false;
        }
    }
}
