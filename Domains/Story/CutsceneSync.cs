// CutsceneManager start / skip and CutsceneCut.Proceed across peers. Dedupe model and its two windows: CutsceneStamps.
//
//   Host-authored start: the host runs StartCutscene natively (its own trigger, or a client's CutsceneStart request)
//     and broadcasts CutsceneStart. A client never starts one from its own trigger: it asks the host once and plays
//     the host's answer like every other peer.
//   Observer replay: a CutsceneStart presentation starts the cutscene on every in-room peer that is not running it.
//     Peers in another room do not start it (the PuzzleState CutsceneCompleted poll settles `completed` later).
//   Late-join replay: the host's full commit names the cutscene it is running (StorySyncService.Send), and the joiner
//     applies it as a CutsceneStart presentation through the same gates.
//   Skip / Proceed: per player. Every peer watches its own copy and skips / proceeds it alone, like solo; nothing goes
//     on the wire. A client's skip / proceed events run in the author scope, so flags only it wrote reach the host
//     (the host may still be watching). A scene-ending cutscene's load waits for the peer still watching
//     (SceneFollowService: a follow is held while this peer is in a cutscene; the host holds a client's request).
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
        /// Native Skip on a peer in a live party, outside an apply scope (skip hold, pause menu). Per player: only this
        /// peer's running copy skips. Returns whether the native skip runs here (a client runs it itself, in the author
        /// scope, and returns false).
        /// </summary>
        internal static bool LocalSkip(CutsceneManager c)
        {
            try
            {
                if (LocalInspect.AirlockCinematic(c.gameObject)) return true;
                // Already over here, or never started (a stray skipper firing late): nothing to skip.
                if (c.completed || !StartedHere(c)) return false;
                // Blocks a re-start of this cutscene on this peer (CutsceneStamps skip window).
                _stamps.TrySkip(WorldId.FromGameObject(c.gameObject), Now);
                if (!NetGate.Client) return true;
                SkipInAuthorScope(c);
                return false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        /// <summary>
        /// CutsceneCut.Proceed on a peer in a live party, outside an apply scope. Per player: the host proceeds natively,
        /// a client proceeds here in the author scope (returns false: already done).
        /// </summary>
        internal static bool LocalProceed(CutsceneCut cut)
        {
            try
            {
                if (LocalInspect.AirlockCinematic(cut.gameObject) || !NetGate.Client) return true;
                StorySyncService.BeginAuthorScope();
                NetGate.BeginApply();
                try { cut.Proceed(); }
                finally
                {
                    NetGate.EndApply();
                    StorySyncService.EndAuthorScope();
                }
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

        // Client skip: its events may write flags the host never ran (the host is still watching, or in another room):
        // authored on the host. Their END_Manager writes are the host's to count (it plays the same cutscene): the
        // client's own pending delta goes first, then it re-baselines, so the skip is never added to the tally twice.
        static void SkipInAuthorScope(CutsceneManager c)
        {
            var net = LanNetworkManager.Instance;
            var story = net != null ? net.StorySync : null;
            story?.FlushEndDelta(net);
            StorySyncService.BeginAuthorScope();
            try { NativeSkip(c); }
            finally
            {
                StorySyncService.EndAuthorScope();
                story?.RebaseEnd();
            }
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
                if (settled)
                {
                    CutsceneSkippingUI.skippableCutscene = false;
                    // Native Skip never ends the skipper's continousCheck (only its own hold or OnDisable sets
                    // done): left polling, a later Cancel hold fired skipEvent on this finished cutscene.
                    try { if (c.skipper != null) c.skipper.done = true; } catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
        }
    }
}
