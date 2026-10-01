// CutsceneManager start / skip and CutsceneCut.Proceed across peers. Dedupe model and its two windows: CutsceneStamps.
//
//   Host-authored start: the host runs StartCutscene natively (its own trigger, or a client's CutsceneStart request)
//     and broadcasts CutsceneStart. A client never starts one from its own trigger: it asks the host once and plays
//     the host's answer like every other peer.
//   Observer replay: a CutsceneStart presentation starts the cutscene on every in-room peer that is not running it.
//     Peers in another room do not start it (the PuzzleState CutsceneCompleted poll settles `completed` later).
//   Late-join replay: the host's full commit names the cutscene it is running (StorySyncService.Send), and the joiner
//     applies it as a CutsceneStart presentation through the same gates.
//   Skip: the first skip of a cutscene goes party-wide once; only an in-room peer that started it runs the native skip
//     (its UnityEvents count END effects once: StoryWire HostCounted / PlayerCounted).
//   Proceed: CutsceneCut.Proceed is host-authored like a start; each cut proceeds once, no window.
// Wreck / hole split (AirlockCinematic.ClientSplitFromHost): the host has none of the client's cutscenes, they run
// locally like solo. PEN_Titles airlock cinematics (LocalInspect.AirlockCinematic) stay local on every path.
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    internal static class CutsceneSync
    {
        // persistent: holder object only; its stamps are cleared on scene change / session reset by InteractionSyncService.OnSceneChanged
        static readonly CutsceneStamps _stamps = new CutsceneStamps();

        static float Now => Time.unscaledTime;

        static string Hex(ulong id) => id.ToString("X16");

        /// <summary>Scene change / session reset (via InteractionSyncService.OnSceneChanged): stamps are per-scene WorldIds.</summary>
        public static void OnSceneChanged() => _stamps.Clear();

        /// <summary>This peer's coroutine for the cutscene is live (started here, not completed).</summary>
        internal static bool StartedHere(CutsceneManager c)
        {
            if (c == null) return false;
            try { return c.cutscene != null && !c.completed; }
            catch (System.Exception ex) { StorySyncService.WarnOnce("Cutscene StartedHere", ex); }
            return false;
        }

        /// <summary>Host: the cutscene with this id is running here (late-join replay target).</summary>
        internal static bool RunningHere(ulong id) => StartedHere(WorldLookup.Find<CutsceneManager>(id));

        /// <summary>A skip only runs natively on a peer that is in the room and started (or just accepted a start of) the cutscene.</summary>
        static bool SkipApplicable(CutsceneManager c, ulong id)
        {
            return LocalInspect.InLocalRoom(c.gameObject)
                && (StartedHere(c) || _stamps.StartedWithin(id, Now, CutsceneStamps.SkipWindow));
        }

        // ------------------------------------------------------------------ local calls (Harmony prefixes)

        /// <summary>
        /// StartCutscene on a peer in a live party, outside an apply scope. Returns whether the native start runs:
        /// the host starts it and broadcasts, a client asks the host instead.
        /// </summary>
        internal static bool LocalStart(CutsceneManager c)
        {
            try
            {
                if (LocalInspect.AirlockCinematic(c.gameObject)) return true;
                if (c.completed) return false;
                ulong id = WorldId.FromGameObject(c.gameObject);
                if (_stamps.Skipped(id, Now)) return false;
                if (AirlockCinematic.ClientSplitFromHost()) return true;
                if (NetGate.Host)
                {
                    // A client request (HostApplyStart) may have started it already; a second native StartCutscene would
                    // run a second coroutine. That start already broadcast.
                    if (StartedHere(c) || !_stamps.TryStart(id, Now))
                    {
                        PlaytestLog.Event("Story", "host StartCutscene dup id=" + Hex(id));
                        return false;
                    }
                    LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart, id, 0, "");
                    return true;
                }
                if (!StartedHere(c) && _stamps.TryRequest(id, Now))
                    LanNetworkManager.Instance.InteractionHandlers.SendInteractionRequest(id, InteractionKind.CutsceneStart);
                return false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        /// <summary>
        /// A client replaying the host's DetermineEnding runs Finale.determineEnding natively, which starts the ending
        /// cutscene; the host's CutsceneStart for it usually arrived first and is already running.
        /// </summary>
        internal static bool EndingReplayStart(CutsceneManager c)
        {
            try
            {
                ulong id = WorldId.FromGameObject(c.gameObject);
                return !StartedHere(c) && _stamps.TryStart(id, Now);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        /// <summary>A host with nobody connected stays vanilla but records the start, so a peer joining mid-cutscene gets it replayed.</summary>
        internal static void SoloHostStart(CutsceneManager c)
        {
            try
            {
                if (!LocalInspect.AirlockCinematic(c.gameObject))
                    LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart,
                        WorldId.FromGameObject(c.gameObject), 0, "");
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Native Skip on a peer in a live party, outside an apply scope (skip hold, pause menu). Returns whether the
        /// native skip runs: only for a cutscene running here. The first skip goes to the party (host broadcast /
        /// client request); a repeat inside the skip window does not.
        /// </summary>
        internal static bool LocalSkip(CutsceneManager c)
        {
            try
            {
                if (LocalInspect.AirlockCinematic(c.gameObject)) return true;
                ulong id = WorldId.FromGameObject(c.gameObject);
                bool running = StartedHere(c);
                if (!_stamps.TrySkip(id, Now)) return running;
                // Host skipped natively in its own cutscene: it counted any END effects of the skip events.
                if (NetGate.Host)
                    LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneSkip, id, 0, StoryWire.HostCounted);
                // Wreck / hole split: the host has no such cutscene; the skip stays local like the start did.
                else if (!AirlockCinematic.ClientSplitFromHost())
                    LanNetworkManager.Instance.InteractionHandlers.SendInteractionRequest(id, InteractionKind.CutsceneSkip);
                return running;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        /// <summary>CutsceneCut.Proceed on a peer in a live party, outside an apply scope. Returns whether native runs.</summary>
        internal static bool LocalProceed(CutsceneCut cut)
        {
            try
            {
                if (LocalInspect.AirlockCinematic(cut.gameObject)) return true;
                ulong id = WorldId.FromGameObject(cut.gameObject);
                if (NetGate.Host)
                {
                    LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneProceed, id, 0, StoryWire.HostCounted);
                    return true;
                }
                // Wreck / hole split: the host has no such cut, a request would leave this cutscene stuck.
                if (AirlockCinematic.ClientSplitFromHost()) return true;
                LanNetworkManager.Instance.InteractionHandlers.SendInteractionRequest(id, InteractionKind.CutsceneProceed);
                return false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        // ------------------------------------------------------------------ host: client requests

        /// <summary>Host: a client's trigger asked for this cutscene. N requesters (or the host's own start) start it once.</summary>
        internal static bool HostApplyStart(ulong id, LanNetworkManager net)
        {
            var c = InteractionSyncService.FindAlive<CutsceneManager>(id, "Interact");
            if (c == null || LocalInspect.AirlockCinematic(c.gameObject)) return true;
            if (StartedHere(c) || !_stamps.TryStart(id, Now))
            {
                PlaytestLog.Event("Interact", "CutsceneStart dup id=" + Hex(id));
                return true;
            }
            if (LocalInspect.InLocalRoom(c.gameObject))
            {
                NetGate.BeginApply();
                try
                {
                    c.StartCutscene();
                    // The StartCutscene postfix arms the skip UI for a skippable one; an unskippable start disarms it.
                    if (c.unskippable) CutsceneSkippingUI.skippableCutscene = false;
                }
                finally { NetGate.EndApply(); }
            }
            else
                PlaytestLog.Event("Interact", "CutsceneStart other-room id=" + Hex(id));
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart, id, 0, "");
            return true;
        }

        /// <summary>Host: a client skipped. The first skip is broadcast; the host skips natively only where the skip applies.</summary>
        internal static bool HostApplySkip(ulong id, LanNetworkManager net, int senderId)
        {
            var c = InteractionSyncService.FindAlive<CutsceneManager>(id, "Interact");
            if (c != null && LocalInspect.AirlockCinematic(c.gameObject)) return true;
            if (!_stamps.TrySkip(id, Now))
            {
                PlaytestLog.Event("Interact", "CutsceneSkip already done id=" + Hex(id));
                return true;
            }
            bool hostRuns = c != null && SkipApplicable(c, id);
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneSkip, id, 0,
                hostRuns ? StoryWire.HostCounted : StoryWire.PlayerCounted(senderId));
            if (hostRuns) SkipInAuthorScope(c);
            else PlaytestLog.Event("Interact", "CutsceneSkip other-room / never-started id=" + Hex(id));
            return true;
        }

        /// <summary>Host: a client's cut proceeded. The host proceeds natively when it is in the room.</summary>
        internal static bool HostApplyProceed(ulong id, LanNetworkManager net, int senderId)
        {
            var cut = InteractionSyncService.FindAlive<CutsceneCut>(id, "Interact");
            if (cut == null) return false;
            bool hostRan = LocalInspect.InLocalRoom(cut.gameObject);
            if (hostRan)
            {
                NetGate.BeginApply();
                try { cut.Proceed(); }
                finally { NetGate.EndApply(); }
            }
            else
                PlaytestLog.Event("Interact", "CutsceneProceed other-room id=" + Hex(id));
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneProceed, id, 0,
                hostRan ? StoryWire.HostCounted : StoryWire.PlayerCounted(senderId));
            return true;
        }

        // ------------------------------------------------------------------ observers: presentations (inside the apply scope)

        /// <summary>Host's CutsceneStart (live or late-join replay): start it here once, in the room, unless it runs already.</summary>
        internal static void ApplyStart(ulong id, bool replay)
        {
            var c = InteractionSyncService.FindAlive<CutsceneManager>(id, "Story");
            if (c == null) return;
            if (LocalInspect.AirlockCinematic(c.gameObject))
            {
                PlaytestLog.Event("Story", "skip local cinematic CutsceneStart");
                return;
            }
            if (!LocalInspect.InLocalRoom(c.gameObject))
            {
                PlaytestLog.Event("Story", "skip other-room CutsceneStart id=" + Hex(id));
                return;
            }
            if (c.completed) return;
            if (StartedHere(c))
            {
                PlaytestLog.Event("Story", "CutsceneStart already running id=" + Hex(id) + (replay ? " (replay)" : ""));
                return;
            }
            if (!_stamps.TryStart(id, Now))
            {
                PlaytestLog.Event("Story", "CutsceneStart already done id=" + Hex(id));
                return;
            }
            c.StartCutscene();
        }

        /// <summary>Host's CutsceneSkip: once per skip window; the native skip runs only where the skip applies.</summary>
        internal static void ApplySkip(ulong id)
        {
            var c = InteractionSyncService.FindAlive<CutsceneManager>(id, "Story");
            if (c == null) return;
            if (LocalInspect.AirlockCinematic(c.gameObject))
            {
                PlaytestLog.Event("Story", "skip local cinematic CutsceneSkip");
                return;
            }
            if (!_stamps.TrySkip(id, Now))
            {
                PlaytestLog.Event("Story", "CutsceneSkip already done id=" + Hex(id));
                return;
            }
            if (!SkipApplicable(c, id))
            {
                PlaytestLog.Event("Story", "skip other-room / never-started CutsceneSkip id=" + Hex(id));
                return;
            }
            SkipInAuthorScope(c);
        }

        /// <summary>Host's CutsceneProceed: in-room peers proceed the same cut.</summary>
        internal static void ApplyProceed(ulong id)
        {
            var cut = InteractionSyncService.FindAlive<CutsceneCut>(id, "Story");
            if (cut == null || !LocalInspect.InLocalRoom(cut.gameObject)) return;
            StorySyncService.BeginAuthorScope();
            try { cut.Proceed(); }
            finally { StorySyncService.EndAuthorScope(); }
        }

        // Skip events may write flags the host never ran (host in another room): a client authors them on the host.
        static void SkipInAuthorScope(CutsceneManager c)
        {
            StorySyncService.BeginAuthorScope();
            try { NativeSkip(c); }
            finally { StorySyncService.EndAuthorScope(); }
        }

        /// <summary>
        /// Skip a not-completed cutscene here. A running one goes through native Skip (CutsceneManager.c: stop the
        /// coroutine, onCutsceneEndEvent / onCutsceneSkip, completed, endState). One with an accepted start but no
        /// coroutine here gets completed + onCutsceneSkip only.
        /// </summary>
        static void NativeSkip(CutsceneManager c)
        {
            bool settled = false;
            try
            {
                if (c.completed) return;
                settled = true;
                if (c.cutscene == null)
                {
                    c.completed = true;
                    if (c.onCutsceneSkip != null) c.onCutsceneSkip.Invoke();
                    return;
                }
                NetGate.BeginApply();
                try { c.Skip(); }
                catch (System.Exception e)
                {
                    // Native Skip threw part-way (e.g. a null in its scene list): still run the skipper's own event.
                    Guard.Swallow(e);
                    if (c.skipper != null && c.skipper.skipEvent != null) c.skipper.skipEvent.Invoke();
                }
                finally { NetGate.EndApply(); }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            finally
            {
                if (settled) CutsceneSkippingUI.skippableCutscene = false;
            }
        }
    }
}
