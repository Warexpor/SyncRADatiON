// Story presentations: the host broadcasts what it (or a requesting client) ran, peers replay it natively.
// The last replayable one rides the next full commit so a late joiner starts it too (CanReplayPresentation).
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    public sealed partial class StorySyncService
    {
        // Host: the presentation a late joiner should replay (the running cutscene, or the ending).
        private StoryCmd _lastCmd;
        private ulong _lastWorldId;

        void ResetPresentation()
        {
            _lastCmd = StoryCmd.None;
            _lastWorldId = 0;
            _endingBroadcast = false;
        }

        public void BroadcastPresentation(StoryCmd cmd, ulong worldId, int int0, string text)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            // Bookkeeping (late-join replay target) runs on any live host; only the send needs a ready peer.
            NoteActivePresentation(cmd, worldId);
            if (!net.HasReadyPeers) return;
            PlaytestLog.Event("Story", "send " + cmd + " id=" + worldId.ToString("X16") + " i=" + int0
                + (string.IsNullOrEmpty(text) ? "" : " '" + text + "'"));
            net.StoryHandlers.SendStoryPresentation(new StoryPresentationMessage
            {
                WorldId = unchecked((long)worldId),
                Cmd = cmd,
                Int0 = int0,
                Text = text ?? ""
            });
        }

        void NoteActivePresentation(StoryCmd cmd, ulong worldId)
        {
            // One-shot relays must not displace the active cutscene kept for join replay.
            if (cmd == StoryCmd.PartyCheat || cmd == StoryCmd.GoToPenny) return;
            if (cmd == StoryCmd.DetermineEnding)
            {
                // Replaces the ending cutscene's own CutsceneStart record; the replay re-runs determineEnding instead.
                _lastCmd = StoryCmd.DetermineEnding;
                _lastWorldId = 1; // non-zero marker (the commit replay branch needs one); the verdict rides EndingId
                return;
            }
            _lastCmd = cmd;
            _lastWorldId = worldId;
        }

        /// <summary>Host: the last presentation is still live and worth replaying for a joiner.</summary>
        bool CanReplayPresentation()
        {
            switch (_lastCmd)
            {
                case StoryCmd.DetermineEnding:
                    // The ending was settled and broadcast this scene: a late joiner replays determineEnding from the
                    // commit's final END values (it starts the ending cutscene natively).
                    return _endingBroadcast;
                case StoryCmd.CutsceneStart:
                    return CutsceneSync.RunningHere(_lastWorldId);
                default:
                    // Skip / Proceed / EventZone / MultiCondition are one-shot consequences, already in the commit's flags.
                    return false;
            }
        }

        /// <summary>Client: the full commit just applied names the host's live presentation (late join / resync).</summary>
        void ReplayActivePresentation(StoryCommitMessage msg)
        {
            var cmd = (StoryCmd)msg.ActiveStoryCmd;
            if (cmd == StoryCmd.DetermineEnding)
            {
                // The commit already holds the final END values; Int0 = the verdict.
                PlaytestLog.Event("Story", "late-join DetermineEnding replay ending=" + msg.EndingId);
                ApplyPresentation(new StoryPresentationMessage { Cmd = cmd, Int0 = msg.EndingId, Text = "replay" });
            }
            else if (msg.ActiveWorldId != 0)
                ApplyPresentation(new StoryPresentationMessage { WorldId = msg.ActiveWorldId, Cmd = cmd, Text = "replay" });
        }

        public void ApplyPresentation(StoryPresentationMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || NetGate.HostRole) return;
            // Same guard as ApplyCommit: never start dialogue / cutscenes / cheats into a loading or foreign scene.
            if (SceneFollowService.LocalIsTransient())
            {
                PlaytestLog.Verbose("Story", "skip presentation " + msg.Cmd + " (loading)");
                return;
            }
            // DetermineEnding is the exception: a client can reach the finale while the host is elsewhere (the host
            // then settles the ending without a Finale), and it only runs this peer's own Finale if the scene has one.
            if (net.SceneMismatch && msg.Cmd != StoryCmd.DetermineEnding)
            {
                PlaytestLog.Verbose("Story", "skip presentation " + msg.Cmd + " (scene mismatch)");
                return;
            }

            // Replayed author-scope events (EventZone / MultiCondition / cutscene skip / proceed) can run END_Manager
            // writes on every in-room peer, while the host already counted them natively (or exactly one requester
            // should). The presentation Text says who counts (StoryWire); a peer that does not re-baselines its END
            // delta afterwards so the replay is never sent to the host as a contribution.
            bool endSkip = false;
            if (msg.Cmd == StoryCmd.EventZoneFire || msg.Cmd == StoryCmd.MultiConditionFire
                || msg.Cmd == StoryCmd.CutsceneSkip || msg.Cmd == StoryCmd.CutsceneProceed)
            {
                endSkip = !StoryWire.CountsEndHere(msg.Text, net.LocalPlayerId);
                if (endSkip) FlushEndDelta(net);
            }

            ulong id = unchecked((ulong)msg.WorldId);
            NetGate.BeginApply();
            try
            {
                PlaytestLog.Event("Story", "apply " + msg.Cmd + " id=" + id.ToString("X16") + " i=" + msg.Int0);
                // Dialogue* / EventScreen* / Book* commands are wire values only: dialogues and inspect screens are
                // local to the peer that opened them (DialoguerGate), nothing sends them.
                switch (msg.Cmd)
                {
                    case StoryCmd.CutsceneStart:
                        CutsceneSync.ApplyStart(id, msg.Text == "replay");
                        break;
                    case StoryCmd.CutsceneSkip:
                        CutsceneSync.ApplySkip(id);
                        break;
                    case StoryCmd.CutsceneProceed:
                        CutsceneSync.ApplyProceed(id);
                        break;
                    case StoryCmd.EventZoneFire:
                        ApplyEventZoneFire(id);
                        break;
                    case StoryCmd.MultiConditionFire:
                        // Only TryOnce is relayed (Int0 0); a TryTrigger count arrives through the PuzzleState poll.
                        if (msg.Int0 != 1) ApplyMultiConditionOnce(id);
                        break;
                    case StoryCmd.DetermineEnding:
                        ApplyDetermineEnding(msg.Int0);
                        break;
                    case StoryCmd.GoToPenny:
                    {
                        var f = FirstFinale();
                        if (f != null) f.goToPenny();
                        break;
                    }
                    case StoryCmd.PartyCheat:
                        if (msg.Int0 == net.LocalPlayerId) break; // this peer ran it itself
                        if (CheatRecently(msg.Text)) break; // in-room replay of the same cutscene already ran it
                        PlaytestLog.Event("Story", "party cheat '" + msg.Text + "'");
                        RunCheat(msg.Text);
                        break;
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Story] apply " + msg.Cmd + " id=" + id.ToString("X16") + ": " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
                if (endSkip) RebaseEnd();
            }
        }

        static void ApplyEventZoneFire(ulong id)
        {
            var z = FindAlive<EventZone>(id);
            if (z == null) return;
            if (LocalInspect.LockWorld(z.gameObject))
            {
                PlaytestLog.Event("Story", "skip lock EventZoneFire");
                return;
            }
            if (LocalInspect.AirlockCinematic(z.gameObject))
            {
                PlaytestLog.Event("Story", "skip airlock EventZoneFire");
                return;
            }
            z.triggered = true;
            if (!LocalInspect.InLocalRoom(z.gameObject))
            {
                PlaytestLog.Event("Story", "skip other-room EventZoneFire id=" + id.ToString("X16"));
                return;
            }
            // The host may be in another room: flag consequences of this Invoke are forwarded to the host (author
            // scope) so the one shared story still gets them.
            BeginAuthorScope();
            try { if (z.onInRange != null) z.onInRange.Invoke(); }
            finally { EndAuthorScope(); }
        }

        /// <summary>
        /// Replays the host's TryOnce natively (MultiConditionEvent.c): tried++ and OnTryDone on the call where tried
        /// reaches tries, only while !triedOnce (latched after the invoke), so a second replay of the same event is a
        /// no-op. TryTrigger is not replayed (see MultiConditionTriggerPatch). Never call OnTryDone on top:
        /// ApplyMultiConditionEvent (PuzzleState poll) fires it only when the host's tried crosses tries past the
        /// local count, which this replay already moved.
        /// </summary>
        static void ApplyMultiConditionOnce(ulong id)
        {
            var m = FindAlive<MultiConditionEvent>(id);
            if (m == null || !LocalInspect.InLocalRoom(m.gameObject)) return;
            BeginAuthorScope();
            try { m.TryOnce(); }
            finally { EndAuthorScope(); }
        }

        static void ApplyDetermineEnding(int verdict)
        {
            // The commit normally precedes this; Int0 is the host's verdict in case it did not.
            if (verdict >= 0 && verdict <= 3) END_Manager.Ending = verdict;
            ModRuntime.Log?.Msg("[Story] END_Manager.Ending=" + END_Manager.Ending);
            var f = FirstFinale();
            if (f == null) return;
            // CalculatePlaystyle is suppressed on clients (it would recompute Circle/Death from this peer's own
            // GlobalStats); the commit sent right before this already carries the host's final END values, and the
            // ending cutscene start is deduped against the host's CutsceneStart (CutsceneSync.EndingReplayStart).
            _endingApply = true;
            try { f.determineEnding(); }
            finally { _endingApply = false; }
        }
    }
}
