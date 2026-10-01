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
            if (ArmWorldSkippers())
            {
                try { CutsceneSkippingUI.skippableCutscene = true; } catch (System.Exception e) { Guard.Swallow(e); }
                return false;
            }
            bool inCut = false;
            try { inCut = PlayerState.cutscene || PlayerState.gameState == PlayerState.gameStates.cutscene; } catch (System.Exception e) { Guard.Swallow(e); }
            try { inCut = inCut || CutsceneSkippingUI.skippableCutscene; } catch (System.Exception e) { Guard.Swallow(e); }

            // One scene-cached scan (not Update) — detect running + pick skip target.
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
                        if (!inCut && c.cutscene != null && !c.completed)
                            inCut = true;
                        if (target == null
                            && !LocalInspect.AirlockCinematic(c.gameObject)
                            && (c.cutscene != null || (c.skipper != null && !c.skipper.done)))
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

        static bool ArmWorldSkippers()
        {
            bool armed = false;
            try
            {
                var holes = WorldLookup.All<PEN_HoleSnowblind>();
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
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var ends = WorldLookup.All<PEN_CodeRoomEnd>();
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
            catch (System.Exception e) { Guard.Swallow(e); }
            return armed;
        }

        static bool ArmSkipper(SkippableCutscene s)
        {
            if (s == null) return false;
            try
            {
                bool done = false;
                try { done = s.done; } catch (System.Exception e) { Guard.Swallow(e); }
                if (done) return false;
                s.enabled = true;
                return true;
            }
            catch { return false; }
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
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.CutsceneProceed);
            return false;
        }
    }
}
