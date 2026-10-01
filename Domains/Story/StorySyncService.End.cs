// END_Manager tally, the ending, and scripted party cheats.
// END counters: every peer runs the native writers (NPC_Tracker, InteractiveLockSingle, PlayerState heal, Add*); a
// client sends its *delta* since the last applied host values, the host adds it to the one shared tally and commits it.
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed partial class StorySyncService
    {
        // Client: host values from the last applied commit (Circle, Death, Graves, Leave, NPC, doors, healedTimeSegments).
        private bool _endBaseValid;
        private readonly int[] _endBase = new int[7];
        private float _endBaseHealed;
        // Host: END statics signature at the last check (a change needs a commit).
        private int _endSig = int.MinValue;

        // One-shot dedupe stamps: party cheats (goto / sethp) and goToPenny.
        private readonly System.Collections.Generic.Dictionary<string, float> _cheatStamp
            = new System.Collections.Generic.Dictionary<string, float>();
        private bool _endingBroadcast;
        private static bool _endingApply;

        /// <summary>True while a client replays the host's DetermineEnding (Finale.determineEnding, CalculatePlaystyle suppressed).</summary>
        public static bool InEndingApply => _endingApply;

        void ResetEnd()
        {
            _endBaseValid = false;
            _endSig = int.MinValue;
            _cheatStamp.Clear();
            _endingBroadcast = false;
            _endingApply = false;
        }

        /// <summary>A client's END counters not yet sent to the host.</summary>
        struct EndContribution
        {
            public int[] Counts;
            public float Healed;

            public int At(int i) => Counts != null ? Counts[i] : 0;
        }

        // ------------------------------------------------------------------ END counters

        static int EndSignature()
        {
            try
            {
                unchecked
                {
                    int h = END_Manager.Circle;
                    h = h * 31 + END_Manager.Death;
                    h = h * 31 + END_Manager.Graves;
                    h = h * 31 + END_Manager.Leave;
                    h = h * 31 + END_Manager.Ending;
                    h = h * 31 + END_Manager.NPC;
                    h = h * 31 + END_Manager.doors;
                    h = h * 31 + END_Manager.healedTimeSegments;
                    h = h * 31 + (int)(END_Manager.healedTime * 10f);
                    // Not memoryTime: MEM_ChecklistLogic.Update adds deltaTime every frame in MEM, which made every tick
                    // a commit. It still rides each commit the host sends for another reason.
                    return h;
                }
            }
            catch (System.Exception ex) { WarnOnce("EndSignature", ex); }
            return 0;
        }

        static bool ReadEnd(int[] cur, out float healed)
        {
            healed = 0f;
            try
            {
                cur[0] = END_Manager.Circle;
                cur[1] = END_Manager.Death;
                cur[2] = END_Manager.Graves;
                cur[3] = END_Manager.Leave;
                cur[4] = END_Manager.NPC;
                cur[5] = END_Manager.doors;
                cur[6] = END_Manager.healedTimeSegments;
                healed = END_Manager.healedTime;
                return true;
            }
            catch (System.Exception ex) { WarnOnce("ReadEnd", ex); }
            return false;
        }

        /// <summary>
        /// SaveManager.Load / NewGame replaced the live slot and the END statics wholesale (wipe reload / Continue): the
        /// new statics are not a delta, and the host's next full dump is authoritative.
        /// </summary>
        public void OnSlotReplaced()
        {
            _endBaseValid = false;
            if (NetGate.Host) RequestAuthoritativeFull();
        }

        private void CaptureEndBase(int[] cur, float healed)
        {
            for (int i = 0; i < _endBase.Length; i++) _endBase[i] = cur[i];
            _endBaseHealed = healed;
            _endBaseValid = true;
        }

        /// <summary>Re-baseline the END delta to the current statics: writes made so far are not this peer's contribution.</summary>
        internal void RebaseEnd()
        {
            if (!_endBaseValid) return;
            var cur = new int[7];
            float healed;
            if (ReadEnd(cur, out healed)) CaptureEndBase(cur, healed);
        }

        EndContribution UnsentEnd()
        {
            var u = new EndContribution { Counts = new int[7] };
            if (!_endBaseValid) return u;
            var cur = new int[7];
            float healed;
            if (!ReadEnd(cur, out healed)) return u;
            for (int i = 0; i < 7; i++) u.Counts[i] = Mathf.Max(0, cur[i] - _endBase[i]);
            u.Healed = Mathf.Max(0f, healed - _endBaseHealed);
            return u;
        }

        /// <summary>Client: the host's END values become the baseline; unsent local deltas ride on top until flushed.</summary>
        bool ApplyEnd(StoryCommitMessage msg, EndContribution unsent)
        {
            var host = new[]
            {
                msg.EndCircle, msg.EndDeath, msg.EndGraves, msg.EndLeave,
                msg.EndNpc, msg.EndDoors, msg.EndHealedSegments
            };
            try
            {
                END_Manager.Circle = host[0] + unsent.At(0);
                END_Manager.Death = host[1] + unsent.At(1);
                END_Manager.Graves = host[2] + unsent.At(2);
                END_Manager.Leave = host[3] + unsent.At(3);
                END_Manager.NPC = host[4] + unsent.At(4);
                END_Manager.doors = host[5] + unsent.At(5);
                END_Manager.healedTimeSegments = host[6] + unsent.At(6);
                END_Manager.healedTime = msg.EndHealedTime + unsent.Healed;
                END_Manager.Ending = msg.EndingId;
                // memoryTime mirrors the host's clock (clients never contribute it, see FlushEndDelta).
                END_Manager.memoryTime = msg.EndMemoryTime;
                CaptureEndBase(host, msg.EndHealedTime);
                return true;
            }
            catch (System.Exception ex) { WarnOnce("ApplyCommit END", ex); }
            return false;
        }

        /// <summary>Client: send the END counters gained since the last baseline (wire: 7 ints | healedTime | memoryTime).</summary>
        internal void FlushEndDelta(LanNetworkManager net)
        {
            var cur = new int[7];
            float healed;
            if (!ReadEnd(cur, out healed)) return;
            if (!_endBaseValid)
            {
                CaptureEndBase(cur, healed);
                return;
            }
            var sb = new System.Text.StringBuilder(64);
            bool any = false;
            for (int i = 0; i < 7; i++)
            {
                int d = cur[i] - _endBase[i];
                if (d < 0) d = 0; // save reload / reset, not a contribution
                if (d > 0) any = true;
                sb.Append(d).Append('|');
            }
            float dh = healed - _endBaseHealed;
            if (dh < 0f) dh = 0f;
            if (dh > 0.0001f) any = true;
            sb.Append(dh.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            // memoryTime is play time in MEM (MEM_ChecklistLogic.Update, per frame): every peer counts the same minutes,
            // so summing peers multiplied it. The host's own clock is the party's; clients always send 0.
            sb.Append("|0");
            CaptureEndBase(cur, healed);
            if (!any) return;
            PlaytestLog.Event("Story", "send END delta " + sb);
            net.SendInteractionRequest(0, InteractionKind.InspectFlag, 100 + (int)StoryCmd.EndDelta, 0, 0f, 0f, 0f, sb.ToString());
        }

        /// <summary>Host: add one client's END_Manager contribution to the single shared tally.</summary>
        public void HostApplyEndDelta(string text, int senderId)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] p = text.Split('|');
            if (p.Length < 9) return;
            var d = new int[7];
            for (int i = 0; i < 7; i++)
            {
                if (!int.TryParse(p[i], out d[i]) || d[i] < 0 || d[i] > 1000) d[i] = 0;
            }
            // p[8] (memoryTime) is always 0 and ignored: the host's own MEM clock is the party's (FlushEndDelta).
            float dh;
            if (!float.TryParse(p[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dh)) dh = 0f;
            if (dh < 0f || dh > 100000f) dh = 0f;
            try
            {
                END_Manager.Circle += d[0];
                END_Manager.Death += d[1];
                END_Manager.Graves += d[2];
                END_Manager.Leave += d[3];
                END_Manager.NPC += d[4];
                END_Manager.doors += d[5];
                END_Manager.healedTimeSegments += d[6];
                END_Manager.healedTime += dh;
                if (d[0] + d[1] + d[2] + d[3] > 0)
                {
                    NetGate.BeginApply();
                    try { END_Manager.EvaluateEnding(); }
                    finally { NetGate.EndApply(); }
                }
            }
            catch (System.Exception ex) { WarnOnce("HostApplyEndDelta", ex); }
            PlaytestLog.Event("Story", "apply END delta from=" + senderId + " " + text + " -> ending=" + SafeEnding());
            _needSend = true;
        }

        static int SafeEnding()
        {
            try { return END_Manager.Ending; }
            catch (System.Exception ex) { WarnOnce("SafeEnding", ex); }
            return -1;
        }

        // ------------------------------------------------------------------ ending

        static Finale FirstFinale()
        {
            var finales = WorldLookup.All<Finale>();
            if (finales == null) return null;
            for (int i = 0; i < finales.Length; i++)
            {
                if (finales[i] != null) return finales[i];
            }
            return null;
        }

        /// <summary>
        /// Host broadcasts END state + DetermineEnding once so every peer starts the same ending. Must run AFTER the
        /// native Finale.determineEnding: that is where CalculatePlaystyle settles Circle/Death/Ending, and the
        /// commit + Int0 carry those final values (clients suppress their own CalculatePlaystyle).
        /// </summary>
        public bool HostBroadcastEnding(LanNetworkManager net)
        {
            if (_endingBroadcast) return false;
            _endingBroadcast = true;
            // A host with nobody connected still records the ending (a later joiner replays it); nothing to send yet.
            if (net.HasReadyPeers) Send(net, false);
            BroadcastPresentation(StoryCmd.DetermineEnding, 0, SafeEnding(), "");
            return true;
        }

        /// <summary>
        /// Host: a peer asked for the ending after it was already broadcast (it was loading or in another scene, where
        /// presentations are dropped): send that peer alone the full table + final END values and the verdict.
        /// </summary>
        public void ResendEnding(LanNetworkManager net, int playerId)
        {
            int prev = net.BeginUnicast(playerId);
            try
            {
                PlaytestLog.Event("Story", "resend DetermineEnding to p" + playerId);
                Send(net, true);
                BroadcastPresentation(StoryCmd.DetermineEnding, 0, SafeEnding(), "");
            }
            finally { net.EndUnicast(prev); }
        }

        /// <summary>Host: a client reached Finale.determineEnding (its native call is blocked) and asks for the ending.</summary>
        internal void HostDetermineEnding(int requesterId)
        {
            var net = LanNetworkManager.Instance;
            // Finale.determineEnding has a per-object `once`; the broadcast has the same once-per-scene guard, so two
            // players reaching the finale at once start the ending a single time for everyone.
            if (_endingBroadcast)
            {
                // The requester missed that broadcast (loading / not yet in the finale scene: presentations are dropped
                // there) and its own determineEnding is blocked: hand it the ending again, to that peer only. A peer that
                // did get it replays a native no-op (Finale.once) and the ending cutscene start is deduped.
                if (requesterId != net.LocalPlayerId && net.HasPeer(requesterId))
                    ResendEnding(net, requesterId);
                return;
            }
            // Native first: CalculatePlaystyle settles Circle/Death/Ending here; the broadcast then carries them. A host
            // that is not in the finale scene (a client reached it alone) has no Finale: it still settles the ending from
            // its own tally and hands it to everyone, or the requester (acked OK, native call blocked) waits forever.
            var f = FirstFinale();
            NetGate.BeginApply();
            try
            {
                if (f != null) f.determineEnding();
                else END_Manager.CalculatePlaystyle();
            }
            catch (System.Exception ex) { WarnOnce(f != null ? "Host determineEnding" : "Host CalculatePlaystyle", ex); }
            finally { NetGate.EndApply(); }
            HostBroadcastEnding(net);
        }

        /// <summary>Host: a client's Finale.goToPenny. Relayed once per window, then run here.</summary>
        internal void HostGoToPenny()
        {
            if (Throttled("@goToPenny", 5f)) return;
            BroadcastPresentation(StoryCmd.GoToPenny, 0, 0, "");
            var f = FirstFinale();
            if (f == null) return;
            NetGate.BeginApply();
            try { f.goToPenny(); }
            catch (System.Exception ex) { WarnOnce("Host goToPenny", ex); }
            finally { NetGate.EndApply(); }
        }

        // ------------------------------------------------------------------ scripted cheats (goto / sethp)

        static bool IsPartyCheat(string cheat)
        {
            if (string.IsNullOrEmpty(cheat)) return false;
            string c = cheat.Trim().ToLowerInvariant();
            return c.StartsWith("goto ") || c == "sethp" || c.StartsWith("sethp ");
        }

        private bool CheatRecently(string cheat) => Throttled(cheat.Trim().ToLowerInvariant(), 3f);

        /// <summary>A party cheat ran inside a replayed presentation: open the dedupe window without relaying it.</summary>
        public void StampPartyCheat(string cheat)
        {
            if (IsPartyCheat(cheat)) CheatRecently(cheat);
        }

        /// <summary>True when this key already fired inside the window (and stamps it otherwise).</summary>
        public bool Throttled(string key, float seconds)
        {
            float now = Time.unscaledTime;
            float last;
            if (_cheatStamp.TryGetValue(key, out last) && now - last < seconds) return true;
            _cheatStamp[key] = now;
            return false;
        }

        /// <summary>
        /// A scripted cutscene cheat ran on this peer. Host relays it so peers outside the cutscene room are
        /// moved / set too (party gather); a client forwards it to the host which relays to everyone else.
        /// </summary>
        public void OnScriptedCheat(string cheat)
        {
            if (!IsPartyCheat(cheat)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !net.HasReadyPeers) return;
            if (CheatRecently(cheat)) return;
            if (NetGate.HostRole)
            {
                PlaytestLog.Event("Story", "relay party cheat '" + cheat + "' origin=host");
                BroadcastPresentation(StoryCmd.PartyCheat, 0, net.LocalPlayerId, cheat);
                return;
            }
            PlaytestLog.Event("Story", "forward party cheat '" + cheat + "'");
            net.SendInteractionRequest(0, InteractionKind.InspectFlag, 100 + (int)StoryCmd.PartyCheat, 0, 0f, 0f, 0f, cheat);
        }

        /// <summary>Host: a client ran a scripted cheat; run it here too and relay to the others.</summary>
        public void HostPartyCheat(string cheat, int senderId)
        {
            if (!IsPartyCheat(cheat)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !NetGate.HostRole) return;
            if (CheatRecently(cheat)) return;
            RunCheat(cheat);
            PlaytestLog.Event("Story", "relay party cheat '" + cheat + "' origin=" + senderId);
            BroadcastPresentation(StoryCmd.PartyCheat, 0, senderId, cheat);
        }

        static void RunCheat(string cheat)
        {
            NetGate.BeginApply();
            try { global::Cheats.cheat(cheat); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Story] cheat '" + cheat + "': " + ex.Message); }
            finally { NetGate.EndApply(); }
        }
    }
}
