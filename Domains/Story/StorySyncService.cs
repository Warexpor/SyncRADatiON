// Host commits SProgress / Dialoguer / END_Manager; peers apply presentation natives.
using System.Collections.Generic;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class StorySyncService
    {
        private readonly Dictionary<string, StoryFlagEntry> _flags
            = new Dictionary<string, StoryFlagEntry>();
        private bool _needSend = true;
        private bool _fullDump = true;
        // Keys written since the last send: an incremental commit carries only these (full = whole table).
        private readonly HashSet<string> _dirty = new HashSet<string>();
        private string _lastXml = "";
        private float _timer;
        public StoryCmd LastCmd;
        public ulong LastWorldId;
        /// <summary>Dialoguer dialogue id for late-join replay (packed into ActiveWorldId).</summary>
        public int LastInt0;

        // --- Client-only: commit buffered while the local scene is loading -------------------
        private StoryCommitMessage _pendingCommit;
        private bool _hasPendingCommit;
        // Client: until this time every FULL commit replaces local SProgress wholesale (host reverted to a save after a
        // party wipe; the host may send a pre-reload dump before the post-reload one, each is exact so the last wins).
        private float _authoritativeUntil;
        const float AuthoritativeWindow = 30f;

        // --- Client-only: SProgress writes forwarded to the host (coalesced per key) ---------
        private readonly Dictionary<string, StoryFlagEntry> _outbox = new Dictionary<string, StoryFlagEntry>();
        private readonly Dictionary<string, float> _outboxSentAt = new Dictionary<string, float>();
        private readonly List<string> _flushKeys = new List<string>();
        private float _clientTimer;
        private static int _authorDepth;
        private static int _suppressForward;

        // --- END_Manager counters: client sends deltas, host adds them (one shared ending) ----
        private bool _endBaseValid;
        private readonly int[] _endBase = new int[7];
        private float _endBaseHealed, _endBaseMemory;
        private int _endSig = int.MinValue;

        // --- Dialogue sequence (host-authored; clients mirror the last presented step) ---------
        private int _dlgId = -1;
        private int _dlgStep;
        private bool _dlgActive;
        private float _dlgStartAt = -99f;

        // --- Scripted cheat relay / one-shot dedupe (goto / sethp / goToPenny) ----------------
        private readonly Dictionary<string, float> _cheatStamp = new Dictionary<string, float>();
        private bool _endingBroadcast;
        private static bool _endingApply;

        /// <summary>True while a client replays the host's DetermineEnding (Finale.determineEnding, CalculatePlaystyle suppressed).</summary>
        public static bool InEndingApply => _endingApply;

        /// <summary>Host: the ending was already started + broadcast this scene.</summary>
        public bool EndingBroadcasted => _endingBroadcast;

        /// <summary>True while a client-applied presentation runs UnityEvents whose SProgress writes must reach the host.</summary>
        public static bool ClientAuthorScope => _authorDepth > 0;
        public static void BeginAuthorScope() => _authorDepth++;
        public static void EndAuthorScope() { if (_authorDepth > 0) _authorDepth--; }

        /// <summary>SaveManager.Save/Load/NewGame write per-player state into SProgress: never forward those.</summary>
        public static bool ForwardSuppressed => _suppressForward > 0;
        public static void BeginSuppressForward() => _suppressForward++;
        public static void EndSuppressForward() { if (_suppressForward > 0) _suppressForward--; }

        /// <summary>
        /// StopNetwork with a party dialogue on screen: its Continue / End came from the host and will never arrive, and
        /// RestoreLocalControl only flips gameState back to play (the dialogue UI would stay up). Close it locally;
        /// loads are swallowed so an end callback cannot start one.
        /// </summary>
        private static void CloseStickyDialogue()
        {
            try
            {
                var net = LanNetworkManager.Instance;
                if (net == null || net.Role == NetworkRole.Offline) return;
                if (PlayerState.gameState != PlayerState.gameStates.dialogue) return;
                NetGate.BeginApply();
                SceneFollowService.BeginSuppressLoads();
                try { Dialoguer.EndDialogue(); }
                finally
                {
                    SceneFollowService.EndSuppressLoads();
                    NetGate.EndApply();
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        public void Reset()
        {
            CloseStickyDialogue();
            _flags.Clear();
            _dirty.Clear();
            _lastXml = "";
            _authoritativeUntil = 0f;
            _endingApply = false;
            _needSend = true;
            _fullDump = true;
            _timer = 0f;
            LastCmd = StoryCmd.None;
            LastWorldId = 0;
            LastInt0 = 0;
            _hasPendingCommit = false;
            _outbox.Clear();
            _outboxSentAt.Clear();
            _clientTimer = 0f;
            _authorDepth = 0;
            _suppressForward = 0;
            _endBaseValid = false;
            _endSig = int.MinValue;
            _dlgId = -1;
            _dlgStep = 0;
            _dlgActive = false;
            _cheatStamp.Clear();
            _endingBroadcast = false;
        }

        public void RequestFullSend()
        {
            _needSend = true;
            _fullDump = true;
        }

        /// <summary>Host: flag-less change (END counters, ending id) that still needs a StoryCommit.</summary>
        public void MarkDirty() => _needSend = true;

        public void OnSceneChanged()
        {
            LastCmd = StoryCmd.None;
            LastWorldId = 0;
            LastInt0 = 0;
            _endingBroadcast = false;
            _dlgActive = false;
            RequestFullSend();
        }

        public void NoteBool(string key, bool val) => Note(new StoryFlagEntry { Kind = 0, Key = key, BoolVal = val });
        public void NoteInt(string key, int val) => Note(new StoryFlagEntry { Kind = 1, Key = key, IntVal = val });
        public void NoteFloat(string key, float val) => Note(new StoryFlagEntry { Kind = 2, Key = key, FloatVal = val });
        public void NoteString(string key, string val) => Note(new StoryFlagEntry { Kind = 3, Key = key, StringVal = val ?? "" });
        public void NoteVector(string key, Vector3 val) =>
            Note(new StoryFlagEntry { Kind = 4, Key = key, FloatVal = val.x, VecY = val.y, VecZ = val.z });

        private void Note(StoryFlagEntry e)
        {
            if (string.IsNullOrEmpty(e.Key)) return;
            _flags[e.Key] = e;
            _dirty.Add(e.Key);
            _needSend = true;
        }

        /// <summary>
        /// Client: the host reverted the party to a save (wipe reload). The clients never load a slot, so their local
        /// SProgress still holds keys the reverted progress no longer has: full commits replace it wholesale for a while.
        /// A commit buffered from before the wipe is stale and dropped.
        /// </summary>
        public void OnPartyWipe()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Client) return;
            _authoritativeUntil = Time.unscaledTime + AuthoritativeWindow;
            _hasPendingCommit = false;
            _endBaseValid = false;
            PlaytestLog.Event("Story", "party wipe: full commits are authoritative for " + AuthoritativeWindow + "s");
        }

        public void TickHost(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            // END_Manager statics are written directly by NPC_Tracker / InteractiveLockSingle / PlayerState.
            int sig = EndSignature();
            if (sig != _endSig)
            {
                _endSig = sig;
                _needSend = true;
            }
            if (!_needSend) return;
            _timer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_timer < 0.75f) return;
            _timer = 0f;
            bool full = _fullDump;
            _fullDump = false;
            _needSend = false;
            Send(net, full);
        }

        // ------------------------------------------------------------------------------------
        // Client tick: buffered commit, coalesced SProgress forward, END counter deltas.
        // ------------------------------------------------------------------------------------
        public void TickClient(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Client || !net.IsConnected) return;
            // A blocked / queued scene request that never arrived is re-asked (host may have been busy > the queue TTL).
            SceneFollowService.TickClient();
            if (SceneFollowService.LocalIsTransient()) return;

            if (_hasPendingCommit && !net.SceneMismatch)
            {
                var m = _pendingCommit;
                _hasPendingCommit = false;
                PlaytestLog.Event("Story", "apply buffered commit (scene loaded)");
                ApplyCommit(m);
            }

            _clientTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_clientTimer < 0.2f) return;
            _clientTimer = 0f;
            // Not gated on a pending commit: under a permanent scene mismatch (wreck / hole split) the pending commit
            // never applies, and gating here stopped the outbox + END delta flush forever. END deltas are relative to
            // the last *applied* baseline, so flushing while a commit is pending stays consistent.
            FlushOutbox(net);
            FlushEndDelta(net);
        }


        private void FlushOutbox(LanNetworkManager net)
        {
            if (_outbox.Count == 0) return;
            float now = Time.unscaledTime;
            _flushKeys.Clear();
            int sent = 0;
            foreach (var kvp in _outbox)
            {
                if (sent >= 24) break;
                var e = kvp.Value;
                // Floats / vectors can be rewritten every frame by game code: 1 Hz per key.
                if (e.Kind == 2 || e.Kind == 4)
                {
                    float last;
                    if (_outboxSentAt.TryGetValue(kvp.Key, out last) && now - last < 1f) continue;
                }
                SendFlag(net, e);
                _outboxSentAt[kvp.Key] = now;
                _flushKeys.Add(kvp.Key);
                sent++;
            }
            for (int i = 0; i < _flushKeys.Count; i++)
                _outbox.Remove(_flushKeys[i]);
        }

        internal static void SendFlag(LanNetworkManager net, StoryFlagEntry e)
        {
            switch (e.Kind)
            {
                case 0:
                    net.SendInteractionRequest(0, InteractionKind.InspectFlag, 0, e.BoolVal ? 1 : 0, 0f, 0f, 0f, e.Key);
                    break;
                case 1:
                    net.SendInteractionRequest(0, InteractionKind.InspectFlag, 1, e.IntVal, 0f, 0f, 0f, e.Key);
                    break;
                case 2:
                    net.SendInteractionRequest(0, InteractionKind.InspectFlag, 2, 0, e.FloatVal, 0f, 0f, e.Key);
                    break;
                case 3:
                    net.SendInteractionRequest(0, InteractionKind.InspectFlag, 3, 0, 0f, 0f, 0f,
                        e.Key + "\n" + (e.StringVal ?? ""));
                    break;
                case 4:
                    net.SendInteractionRequest(0, InteractionKind.InspectFlag, 4, 0, e.FloatVal, e.VecY, e.VecZ, e.Key);
                    break;
            }
        }

        /// <summary>
        /// Client: a story-flag write that must be authored on the host (host applies + commits to everyone).
        /// Local write still happens so the client's own logic sees it immediately.
        /// </summary>
        public void ClientForward(StoryFlagEntry e)
        {
            if (string.IsNullOrEmpty(e.Key)) return;
            if (ForwardSuppressed) return;
            if (SameAsLocal(e)) return;
            _outbox[e.Key] = e;
        }

        // Missing keys never equal the written value (sentinel defaults), so first writes always forward.
        internal static bool SameAsLocal(StoryFlagEntry e)
        {
            try
            {
                switch (e.Kind)
                {
                    case 0:
                    {
                        bool a = SProgress.GetBool(e.Key, true);
                        bool b = SProgress.GetBool(e.Key, false);
                        return a == b && a == e.BoolVal;
                    }
                    case 1:
                        return SProgress.GetInt(e.Key, int.MinValue) == e.IntVal;
                    case 2:
                    {
                        float c = SProgress.GetFloat(e.Key, float.NaN);
                        return !float.IsNaN(c) && Mathf.Approximately(c, e.FloatVal);
                    }
                    case 3:
                        return string.Equals(SProgress.GetString(e.Key, "\u0001<none>"), e.StringVal ?? "",
                            System.StringComparison.Ordinal);
                    case 4:
                    {
                        var v = SProgress.GetVector(e.Key, new Vector3(float.NaN, float.NaN, float.NaN));
                        return !float.IsNaN(v.x) && Mathf.Approximately(v.x, e.FloatVal)
                            && Mathf.Approximately(v.y, e.VecY) && Mathf.Approximately(v.z, e.VecZ);
                    }
                }
            }
            catch (System.Exception ex) { WarnOnce("SameAsLocal", ex); }
            return false;
        }

        private static readonly HashSet<string> _warned = new HashSet<string>();

        internal static void WarnOnce(string site, System.Exception ex)
        {
            if (!_warned.Add(site)) return;
            ModRuntime.Log?.Warning("[Story] " + site + ": " + ex.Message);
        }

        // --- END_Manager counters ---------------------------------------------------------------

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
                    h = h * 31 + (int)(END_Manager.memoryTime * 10f);
                    return h;
                }
            }
            catch (System.Exception ex) { WarnOnce("EndSignature", ex); }
            return 0;
        }

        static bool ReadEnd(int[] cur, out float healed, out float memory)
        {
            healed = 0f;
            memory = 0f;
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
                memory = END_Manager.memoryTime;
                return true;
            }
            catch (System.Exception ex) { WarnOnce("ReadEnd", ex); }
            return false;
        }

        /// <summary>After SaveManager.Load/NewGame the statics were replaced wholesale: do not treat that as a delta.</summary>
        public void ResetEndBase() => _endBaseValid = false;

        private void CaptureEndBase(int[] cur, float healed, float memory)
        {
            for (int i = 0; i < _endBase.Length; i++) _endBase[i] = cur[i];
            _endBaseHealed = healed;
            _endBaseMemory = memory;
            _endBaseValid = true;
        }

        private void FlushEndDelta(LanNetworkManager net)
        {
            var cur = new int[7];
            float healed, memory;
            if (!ReadEnd(cur, out healed, out memory)) return;
            if (!_endBaseValid)
            {
                CaptureEndBase(cur, healed, memory);
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
            float dm = memory - _endBaseMemory;
            if (dh < 0f) dh = 0f;
            if (dm < 0f) dm = 0f;
            if (dh > 0.0001f || dm > 0.0001f) any = true;
            sb.Append(dh.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
            sb.Append(dm.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            CaptureEndBase(cur, healed, memory);
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
            float dh, dm;
            if (!float.TryParse(p[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dh)) dh = 0f;
            if (!float.TryParse(p[8], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dm)) dm = 0f;
            if (dh < 0f || dh > 100000f) dh = 0f;
            if (dm < 0f || dm > 100000f) dm = 0f;
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
                END_Manager.memoryTime += dm;
                if (d[0] + d[1] + d[2] + d[3] > 0)
                {
                    NetGate.BeginApply();
                    try { END_Manager.EvaluateEnding(); }
                    finally { NetGate.EndApply(); }
                }
            }
            catch (System.Exception ex) { WarnOnce("HostApplyEndDelta", ex); }
            PlaytestLog.Event("Story", "apply END delta from=" + senderId + " " + text
                + " -> ending=" + SafeEnding());
            _needSend = true;
        }

        static int SafeEnding()
        {
            try { return END_Manager.Ending; }
            catch (System.Exception ex) { WarnOnce("SafeEnding", ex); }
            return -1;
        }

        // --- Dialogue sequence ------------------------------------------------------------------

        public int DialogueId => _dlgId;
        public int DialogueStep => _dlgStep;

        public string DialogueTag() => _dlgId + ":" + _dlgStep;

        /// <summary>Host: a story dialogue begins. False = duplicate Start of the same id (two players triggered it).</summary>
        public bool HostDialogueStart(int id)
        {
            float now = Time.unscaledTime;
            if (_dlgActive && _dlgId == id && now - _dlgStartAt < 1.5f)
                return false;
            _dlgId = id;
            _dlgStep = 0;
            _dlgActive = true;
            _dlgStartAt = now;
            return true;
        }

        /// <summary>
        /// Host: validate a Continue/End. reqId &lt; 0 (local press or unknown) is always accepted; otherwise the
        /// request must name the dialogue and step the host is on, so duplicates from the other N-1 players drop.
        /// </summary>
        public bool HostDialogueAdvance(int reqId, int reqStep, bool end)
        {
            bool known = _dlgId >= 0;
            if (reqId >= 0 && known)
            {
                if (!_dlgActive || reqId != _dlgId || reqStep != _dlgStep)
                {
                    PlaytestLog.Event("Story", "drop stale dialogue " + (end ? "End" : "Continue")
                        + " req=" + reqId + ":" + reqStep + " host=" + _dlgId + ":" + _dlgStep
                        + (_dlgActive ? "" : " (ended)"));
                    return false;
                }
            }
            _dlgStep++;
            if (end) _dlgActive = false;
            return true;
        }

        public static bool TryParseTag(string tag, out int id, out int step) =>
            StoryWire.TryParseDialogueTag(tag, out id, out step);

        /// <summary>Client: accept a host dialogue presentation only if it is newer than what this peer already mirrored.</summary>
        private bool ClientDialogueAccept(StoryCmd cmd, string tag)
        {
            int id, step;
            if (!TryParseTag(tag, out id, out step))
            {
                // Untagged = late-join replay of the Start: host step unknown, so stay lenient
                // (requests tagged -1 are always accepted by the host) until a tagged Continue re-syncs.
                if (cmd == StoryCmd.DialoguerStartId)
                {
                    _dlgId = -1;
                    _dlgStep = 0;
                    _dlgActive = true;
                }
                return true;
            }
            if (cmd == StoryCmd.DialoguerStartId)
            {
                _dlgId = id;
                _dlgStep = step;
                _dlgActive = true;
                return true;
            }
            if (_dlgId >= 0 && (id != _dlgId || step <= _dlgStep))
            {
                PlaytestLog.Event("Story", "drop stale " + cmd + " tag=" + tag + " local=" + _dlgId + ":" + _dlgStep);
                return false;
            }
            _dlgId = id;
            _dlgStep = step;
            if (cmd == StoryCmd.DialogueEnd) _dlgActive = false;
            return true;
        }

        // --- Scripted cheats (goto / sethp) -------------------------------------------------------

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
            if (!IsPartyCheat(cheat)) return;
            CheatRecently(cheat);
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
            if (net.Role == NetworkRole.Host)
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
            if (net == null || net.Role != NetworkRole.Host) return;
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

        /// <summary>
        /// Host broadcasts END state + DetermineEnding once so every peer starts the same ending. Must run AFTER the
        /// native Finale.determineEnding: that is where CalculatePlaystyle settles Circle/Death/Ending, and the
        /// commit + Int0 carry those final values (clients suppress their own CalculatePlaystyle).
        /// </summary>
        public bool HostBroadcastEnding(LanNetworkManager net)
        {
            if (_endingBroadcast) return false;
            _endingBroadcast = true;
            Send(net, false);
            BroadcastPresentation(StoryCmd.DetermineEnding, 0, SafeEnding(), "");
            return true;
        }

        public void Send(LanNetworkManager net, bool full, bool replayPresentation = false)
        {
            // Full = the whole live table (join / resync / scene change). Incremental = only keys written since the
            // last send: re-sending every flag to N-1 peers on each 0.75 s commit was pure bandwidth.
            StoryFlagEntry[] arr;
            if (full)
            {
                DumpLiveProgress();
                _dirty.Clear();
                arr = new StoryFlagEntry[_flags.Count];
                int i = 0;
                foreach (var kvp in _flags)
                    arr[i++] = kvp.Value;
            }
            else
            {
                arr = StoryWire.TakeDirty(_flags, _dirty);
            }

            string xml = "";
            try { xml = Dialoguer.GetGlobalVariablesState() ?? ""; } catch (System.Exception e) { Guard.Swallow(e); }
            // Dialoguer globals ride every full commit; an incremental one only when they changed (client ignores "").
            string xmlWire = xml;
            if (!full && string.Equals(xml, _lastXml, System.StringComparison.Ordinal)) xmlWire = "";
            else _lastXml = xml;

            int circle = 0, death = 0, graves = 0, leave = 0, ending = 0;
            int npc = 0, healedSeg = 0, doors = 0;
            float healedTime = 0f, memoryTime = 0f;
            try
            {
                circle = END_Manager.Circle;
                death = END_Manager.Death;
                graves = END_Manager.Graves;
                leave = END_Manager.Leave;
                ending = END_Manager.Ending;
                npc = END_Manager.NPC;
                healedTime = END_Manager.healedTime;
                healedSeg = END_Manager.healedTimeSegments;
                memoryTime = END_Manager.memoryTime;
                doors = END_Manager.doors;
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            byte gs = 0;
            try { gs = (byte)PlayerState.gameState; } catch (System.Exception e) { Guard.Swallow(e); }

            bool replay = replayPresentation && CanReplayPresentation(LastCmd, LastWorldId);
            if (replayPresentation && !replay && LastCmd == StoryCmd.DialoguerStartId)
                PlaytestLog.Event("Story", "skip dialogue replay (host not at the first node of a live dialogue)");
            net.SendStoryCommit(new StoryCommitMessage
            {
                FullRefresh = full,
                DialoguerXml = xmlWire,
                EndCircle = circle,
                EndDeath = death,
                EndGraves = graves,
                EndLeave = leave,
                EndingId = ending,
                EndNpc = npc,
                EndHealedTime = healedTime,
                EndHealedSegments = healedSeg,
                EndMemoryTime = memoryTime,
                EndDoors = doors,
                Flags = arr,
                ActiveGameState = gs,
                ActiveWorldId = replay ? unchecked((long)LastWorldId) : 0,
                ActiveStoryCmd = replay ? (byte)LastCmd : (byte)0
            });
            if (full)
                PlaytestLog.Event("Story", "commit full flags=" + arr.Length
                    + " xml=" + (xml != null ? xml.Length : 0)
                    + " cmd=" + (replay ? LastCmd.ToString() : "-")
                    + " gs=" + gs);
        }

        bool CanReplayPresentation(StoryCmd cmd, ulong id)
        {
            switch (cmd)
            {
                case StoryCmd.None:
                case StoryCmd.CutsceneSkip:
                case StoryCmd.CutsceneProceed:
                case StoryCmd.EventZoneFire:
                case StoryCmd.MultiConditionFire:
                case StoryCmd.DetermineEnding:
                case StoryCmd.DialogueContinue:
                case StoryCmd.DialogueEnd:
                case StoryCmd.GoToPenny:
                case StoryCmd.PartyCheat:
                case StoryCmd.EndDelta:
                case StoryCmd.EndGraves:
                    return false;
                case StoryCmd.DialoguerStartId:
                {
                    // Dialogue id is packed into LastWorldId (wire WorldId is always 0 for Dialoguer). LastCmd is not
                    // cleared by natural ends (DialoguerDialogueManager.endDialogue is not patched), so require the
                    // host to actually be in a dialogue. Dialoguer.StartDialogue enters at node 0 and the branch path
                    // of later nodes is not reproducible, so a late joiner is only brought in while the host has not
                    // advanced yet (step 0); past that the replay is skipped (the next host Continue / End still lands).
                    bool inDialogue = false;
                    try { inDialogue = PlayerState.gameState == PlayerState.gameStates.dialogue; }
                    catch (System.Exception e) { Guard.Swallow(e); }
                    return inDialogue && _dlgActive && _dlgId == unchecked((int)(uint)id) && _dlgStep == 0;
                }
                case StoryCmd.CutsceneStart:
                {
                    var c = WorldLookup.Find<CutsceneManager>(id);
                    if (c == null) return false;
                    try
                    {
                        if (c.gameObject == null) return false;
                        if (c.completed) return false;
                        return c.cutscene != null;
                    }
                    catch { return false; }
                }
                default:
                    return cmd != StoryCmd.None;
            }
        }

        private void DumpLiveProgress()
        {
            try
            {
                var p = SProgress.progress;
                if (p == null) return;
                // The table mirrors the live slot exactly: after SaveManager.Load (wipe reload), SProgress.Load or
                // ResetProgress, keys the loaded progress does not have must not be resurrected by stale entries.
                _flags.Clear();
                DumpBools(p);
                DumpInts(p);
                DumpFloats(p);
                DumpStrings(p);
                DumpVectors(p);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Story] Dump progress: " + ex.Message);
            }
        }

        private void DumpBools(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.boolKeys;
                var vals = p.bools;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (!string.IsNullOrEmpty(k))
                        _flags[k] = new StoryFlagEntry { Kind = 0, Key = k, BoolVal = vals[i] };
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        private void DumpInts(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.intKeys;
                var vals = p.ints;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (!string.IsNullOrEmpty(k))
                        _flags[k] = new StoryFlagEntry { Kind = 1, Key = k, IntVal = vals[i] };
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        private void DumpFloats(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.floatKeys;
                var vals = p.floats;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (!string.IsNullOrEmpty(k))
                        _flags[k] = new StoryFlagEntry { Kind = 2, Key = k, FloatVal = vals[i] };
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        private void DumpStrings(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.stringKeys;
                var vals = p.strings;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (!string.IsNullOrEmpty(k))
                        _flags[k] = new StoryFlagEntry { Kind = 3, Key = k, StringVal = vals[i] ?? "" };
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        private void DumpVectors(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.vectorKeys;
                var vals = p.vectors;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (string.IsNullOrEmpty(k)) continue;
                    var v = vals[i];
                    _flags[k] = new StoryFlagEntry { Kind = 4, Key = k, FloatVal = v.x, VecY = v.y, VecZ = v.z };
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Buffered commits are cumulative: flags union by key (newer wins; incremental commits only carry changed
        /// keys), scalars are the newest, a FULL refresh / replay target already buffered is never lost.
        /// </summary>
        private void MergePending(ref StoryCommitMessage msg)
        {
            if (!_hasPendingCommit) return;
            var old = _pendingCommit;
            // A newer FULL commit already is the whole table (union with an older full would keep keys the host since
            // dropped); an incremental one only adds to whatever was buffered.
            if (!msg.FullRefresh) msg.Flags = StoryWire.MergeFlags(old.Flags, msg.Flags);
            if (old.FullRefresh && !msg.FullRefresh)
            {
                msg.FullRefresh = true;
                if (msg.ActiveStoryCmd == 0)
                {
                    msg.ActiveStoryCmd = old.ActiveStoryCmd;
                    msg.ActiveWorldId = old.ActiveWorldId;
                }
            }
            if (string.IsNullOrEmpty(msg.DialoguerXml)) msg.DialoguerXml = old.DialoguerXml;
            _hasPendingCommit = false;
        }

        public void ApplyCommit(StoryCommitMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;
            if (SceneFollowService.LocalIsTransient() || (net != null && net.SceneMismatch))
            {
                // Loading or in a scene the host is not in (wreck / hole split can stay mismatched for a long
                // time): keep the newest commit, merged, and apply it once the scene is up / matches.
                MergePending(ref msg);
                _pendingCommit = msg;
                _hasPendingCommit = true;
                PlaytestLog.Verbose("Story", "buffer commit (" + (SceneFollowService.LocalIsTransient() ? "loading" : "scene mismatch") + ")");
                return;
            }
            // A live commit supersedes the buffered (older) one: fold it in so TickClient never re-applies it on top.
            MergePending(ref msg);

            // Party wipe: the host reverted to a save, this peer never loaded a slot. FULL commits inside the window
            // after it replace local progress exactly (keys absent on the host are removed, not kept).
            bool authoritative = msg.FullRefresh && Time.unscaledTime < _authoritativeUntil;
            if (authoritative)
                PlaytestLog.Event("Story", "authoritative full commit: clearing local SProgress");

            // Unsent local END contributions survive the overwrite below (client increments between commits).
            var unsent = new int[7];
            float unsentHeal = 0f, unsentMem = 0f;
            if (_endBaseValid && !authoritative)
            {
                var cur = new int[7];
                float ch, cm;
                if (ReadEnd(cur, out ch, out cm))
                {
                    for (int i = 0; i < 7; i++)
                        unsent[i] = Mathf.Max(0, cur[i] - _endBase[i]);
                    unsentHeal = Mathf.Max(0f, ch - _endBaseHealed);
                    unsentMem = Mathf.Max(0f, cm - _endBaseMemory);
                }
            }

            NetGate.BeginApply();
            if (authoritative) BeginSuppressForward();
            try
            {
                if (authoritative) ClearLocalProgress();
                if (msg.Flags != null)
                {
                    for (int i = 0; i < msg.Flags.Length; i++)
                    {
                        var f = msg.Flags[i];
                        if (string.IsNullOrEmpty(f.Key)) continue;
                        _flags[f.Key] = f;
                        try
                        {
                            switch (f.Kind)
                            {
                                case 0: SProgress.SetBool(f.Key, f.BoolVal); break;
                                case 1: SProgress.SetInt(f.Key, f.IntVal); break;
                                case 2: SProgress.SetFloat(f.Key, f.FloatVal); break;
                                case 3: SProgress.SetString(f.Key, f.StringVal); break;
                                case 4: SProgress.SetVector(f.Key, new Vector3(f.FloatVal, f.VecY, f.VecZ)); break;
                            }
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }

                if (!string.IsNullOrEmpty(msg.DialoguerXml))
                {
                    try { Dialoguer.SetGlobalVariablesState(msg.DialoguerXml); } catch (System.Exception e) { Guard.Swallow(e); }
                    try { PartyKeyRing.RestoreUiNames(); } catch (System.Exception e) { Guard.Swallow(e); }
                }

                try
                {
                    // END_Manager is static; wrap each write so one bad field cannot abort the rest.
                    END_Manager.Circle = msg.EndCircle;
                    END_Manager.Death = msg.EndDeath;
                    END_Manager.Graves = msg.EndGraves;
                    END_Manager.Leave = msg.EndLeave;
                    END_Manager.Ending = msg.EndingId;
                    END_Manager.NPC = msg.EndNpc;
                    END_Manager.healedTime = msg.EndHealedTime;
                    END_Manager.healedTimeSegments = msg.EndHealedSegments;
                    END_Manager.memoryTime = msg.EndMemoryTime;
                    END_Manager.doors = msg.EndDoors;

                    // Host values become the baseline; unsent local deltas ride on top until flushed.
                    var hostVals = new[]
                    {
                        msg.EndCircle, msg.EndDeath, msg.EndGraves, msg.EndLeave,
                        msg.EndNpc, msg.EndDoors, msg.EndHealedSegments
                    };
                    CaptureEndBase(hostVals, msg.EndHealedTime, msg.EndMemoryTime);
                    if (unsent[0] > 0) END_Manager.Circle += unsent[0];
                    if (unsent[1] > 0) END_Manager.Death += unsent[1];
                    if (unsent[2] > 0) END_Manager.Graves += unsent[2];
                    if (unsent[3] > 0) END_Manager.Leave += unsent[3];
                    if (unsent[4] > 0) END_Manager.NPC += unsent[4];
                    if (unsent[5] > 0) END_Manager.doors += unsent[5];
                    if (unsent[6] > 0) END_Manager.healedTimeSegments += unsent[6];
                    if (unsentHeal > 0f) END_Manager.healedTime += unsentHeal;
                    if (unsentMem > 0f) END_Manager.memoryTime += unsentMem;
                }
                catch (System.Exception ex) { WarnOnce("ApplyCommit END", ex); }
            }
            finally
            {
                if (authoritative) EndSuppressForward();
                NetGate.EndApply();
            }

            PlaytestLog.Event("Story", "apply commit full=" + msg.FullRefresh
                + " flags=" + (msg.Flags != null ? msg.Flags.Length : 0)
                + " xml=" + (msg.DialoguerXml != null ? msg.DialoguerXml.Length : 0)
                + " cmd=" + (StoryCmd)msg.ActiveStoryCmd);

            if (msg.FullRefresh && msg.ActiveStoryCmd != 0)
            {
                var replay = (StoryCmd)msg.ActiveStoryCmd;
                if (IsLocalInspect(replay))
                    return;
                if (replay == StoryCmd.DialoguerStartId)
                {
                    // ActiveWorldId carries packed dialogue id (presentation WorldId is 0).
                    int dialogueId = (int)msg.ActiveWorldId;
                    PlaytestLog.Event("Story", "late-join Dialoguer replay id=" + dialogueId);
                    // The host only replays while it is at node 0, so the tag is exactly id:0.
                    ApplyPresentation(new StoryPresentationMessage
                    {
                        WorldId = 0,
                        Cmd = StoryCmd.DialoguerStartId,
                        Int0 = dialogueId,
                        Text = StoryWire.DialogueTag(dialogueId, 0)
                    });
                }
                else if (msg.ActiveWorldId != 0)
                {
                    ApplyPresentation(new StoryPresentationMessage
                    {
                        WorldId = msg.ActiveWorldId,
                        Cmd = replay,
                        Int0 = 0,
                        Text = "replay"
                    });
                }
            }
        }

        /// <summary>Empty the live SProgress slot's key / value lists (what a fresh slot starts with); slotID is kept.</summary>
        private static void ClearLocalProgress()
        {
            try
            {
                var p = SProgress.progress;
                if (p == null) return;
                try { p.boolKeys?.Clear(); p.bools?.Clear(); } catch (System.Exception e) { Guard.Swallow(e); }
                try { p.intKeys?.Clear(); p.ints?.Clear(); } catch (System.Exception e) { Guard.Swallow(e); }
                try { p.floatKeys?.Clear(); p.floats?.Clear(); } catch (System.Exception e) { Guard.Swallow(e); }
                try { p.stringKeys?.Clear(); p.strings?.Clear(); } catch (System.Exception e) { Guard.Swallow(e); }
                try { p.vectorKeys?.Clear(); p.vectors?.Clear(); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            catch (System.Exception ex) { WarnOnce("ClearLocalProgress", ex); }
        }

        public void BroadcastPresentation(StoryCmd cmd, ulong worldId, int int0, string text)
        {
            if (IsLocalInspect(cmd)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            NoteActivePresentation(cmd, worldId, int0);
            PlaytestLog.Event("Story", "send " + cmd + " id=" + worldId.ToString("X16") + " i=" + int0
                + (string.IsNullOrEmpty(text) ? "" : " '" + text + "'"));
            net.SendStoryPresentation(new StoryPresentationMessage
            {
                WorldId = unchecked((long)worldId),
                Cmd = cmd,
                Int0 = int0,
                Text = text ?? ""
            });
        }

        void NoteActivePresentation(StoryCmd cmd, ulong worldId, int int0)
        {
            // One-shot relays must not displace the active cutscene/dialogue kept for join replay.
            if (cmd == StoryCmd.PartyCheat || cmd == StoryCmd.GoToPenny) return;
            if (cmd == StoryCmd.DialoguerStartId)
            {
                LastCmd = StoryCmd.DialoguerStartId;
                LastInt0 = int0;
                // Pack dialogue id into LastWorldId (presentation WorldId is always 0 for Dialoguer).
                LastWorldId = unchecked((ulong)(uint)int0);
                return;
            }
            if (cmd == StoryCmd.DialogueContinue)
            {
                // Keep Start as the late-join replay target (orphan Continue softlocks UI).
                if (LastCmd != StoryCmd.DialoguerStartId && LastInt0 != 0)
                {
                    LastCmd = StoryCmd.DialoguerStartId;
                    LastWorldId = unchecked((ulong)(uint)LastInt0);
                }
                return;
            }
            if (cmd == StoryCmd.DialogueEnd)
            {
                LastCmd = StoryCmd.None;
                LastWorldId = 0;
                LastInt0 = 0;
                return;
            }
            LastCmd = cmd;
            LastWorldId = worldId;
            LastInt0 = int0;
        }

        public void ApplyPresentation(StoryPresentationMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;
            // Same guard as ApplyCommit: never start dialogue / cutscenes / cheats into a loading or foreign scene.
            if (SceneFollowService.LocalIsTransient())
            {
                PlaytestLog.Verbose("Story", "skip presentation " + msg.Cmd + " (loading)");
                return;
            }
            if (net != null && net.SceneMismatch)
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
                endSkip = !StoryWire.CountsEndHere(msg.Text, net != null ? net.LocalPlayerId : -1);
                if (endSkip && net != null) FlushEndDelta(net);
            }

            NetGate.BeginApply();
            try
            {
                ulong id = unchecked((ulong)msg.WorldId);
                PlaytestLog.Event("Story", "apply " + msg.Cmd + " id=" + id.ToString("X16") + " i=" + msg.Int0);
                switch (msg.Cmd)
                {
                    case StoryCmd.DialogueStart:
                    {
                        var d = Find<Dialogue>(id);
                        if (d == null || LocalInspect.Dialogue(d))
                            PlaytestLog.Verbose("Story", "skip local inspect DialogueStart");
                        break;
                    }
                    case StoryCmd.DialoguerStartId:
                        if (LocalInspect.DialoguerFlavor(msg.Int0))
                            PlaytestLog.Verbose("Story", "skip flavor DialoguerStartId i=" + msg.Int0);
                        else if (ClientDialogueAccept(msg.Cmd, msg.Text))
                        {
                            // A client-initiated start kept its native callback (the request itself never ran it).
                            var cb = DialoguerGate.TakeCallback(msg.Int0);
                            try
                            {
                                if (cb != null) Dialoguer.StartDialogue(msg.Int0, cb);
                                else Dialoguer.StartDialogue(msg.Int0);
                            }
                            catch (System.Exception ex) { WarnOnce("Dialoguer.StartDialogue", ex); }
                        }
                        break;
                    case StoryCmd.DialogueContinue:
                        if (!ClientDialogueAccept(msg.Cmd, msg.Text)) break;
                        try
                        {
                            if (msg.Int0 != 0) Dialoguer.ContinueDialogue(msg.Int0);
                            else Dialoguer.ContinueDialogue();
                        }
                        catch (System.Exception ex) { WarnOnce("Dialoguer.ContinueDialogue", ex); }
                        break;
                    case StoryCmd.DialogueEnd:
                        if (!ClientDialogueAccept(msg.Cmd, msg.Text)) break;
                        try { Dialoguer.EndDialogue(); }
                        catch (System.Exception ex) { WarnOnce("Dialoguer.EndDialogue", ex); }
                        break;
                    case StoryCmd.CutsceneStart:
                    {
                        var c = FindAlive<CutsceneManager>(id);
                        if (c == null)
                            break;
                        try
                        {
                            if (LocalInspect.AirlockCinematic(c.gameObject))
                            {
                                PlaytestLog.Event("Story", "skip local cinematic CutsceneStart");
                                break;
                            }
                            if (!LocalInspect.InLocalRoom(c.gameObject))
                            {
                                PlaytestLog.Event("Story", "skip other-room CutsceneStart id=" + id.ToString("X16"));
                                break;
                            }
                            try { if (c.completed) break; }
                            catch (System.Exception ex) { WarnOnce("CutsceneStart completed", ex); }
                            // Join / resync replay must not start a cutscene this peer is already running.
                            if (msg.Text == "replay" && InteractionSyncService.StartedHere(c))
                            {
                                PlaytestLog.Event("Story", "CutsceneStart replay: already running id=" + id.ToString("X16"));
                                break;
                            }
                            // Time-windowed, so the requester (who only *asked*) plays it exactly once and a
                            // repeatable cutscene can start again after the window.
                            if (InteractionSyncService.WasSkipped(id)
                                || !InteractionSyncService.RememberStart(id))
                            {
                                PlaytestLog.Event("Story", "CutsceneStart already done id=" + id.ToString("X16"));
                                break;
                            }
                            c.StartCutscene();
                            try
                            {
                                if (!c.unskippable)
                                    CutsceneSkippingUI.skippableCutscene = true;
                            }
                            catch (System.Exception ex) { WarnOnce("CutsceneStart skippable", ex); }
                        }
                        catch (System.Exception ex)
                        {
                            ModRuntime.Log?.Warning("[Story] CutsceneStart: " + ex.Message);
                        }
                        break;
                    }
                    case StoryCmd.CutsceneSkip:
                    {
                        var c = FindAlive<CutsceneManager>(id);
                        if (c == null) break;
                        try
                        {
                            if (LocalInspect.AirlockCinematic(c.gameObject))
                            {
                                PlaytestLog.Event("Story", "skip local cinematic CutsceneSkip");
                                break;
                            }
                        }
                        catch (System.Exception ex) { WarnOnce("CutsceneSkip airlock", ex); }
                        // Gate like Start: only a peer that is in the room AND started this cutscene skips it.
                        if (!InteractionSyncService.SkipApplicable(c, id))
                        {
                            PlaytestLog.Event("Story", "skip other-room / never-started CutsceneSkip id=" + id.ToString("X16"));
                            break;
                        }
                        if (!InteractionSyncService.RememberSkip(id))
                            PlaytestLog.Event("Story", "CutsceneSkip already done id=" + id.ToString("X16"));
                        else
                        {
                            BeginAuthorScope();
                            try { InteractionSyncService.NativeSkip(c); }
                            finally { EndAuthorScope(); }
                        }
                        break;
                    }
                    case StoryCmd.CutsceneProceed:
                    {
                        var cut = FindAlive<CutsceneCut>(id);
                        if (cut != null)
                        {
                            try
                            {
                                if (LocalInspect.InLocalRoom(cut.gameObject))
                                {
                                    BeginAuthorScope();
                                    try { cut.Proceed(); }
                                    finally { EndAuthorScope(); }
                                }
                            }
                            catch (System.Exception ex) { WarnOnce("CutsceneProceed", ex); }
                        }
                        break;
                    }
                    case StoryCmd.EventScreenStart:
                    case StoryCmd.EventScreenExit:
                    case StoryCmd.OpenBookMemory:
                    case StoryCmd.BookOpen:
                        PlaytestLog.Verbose("Story", "skip local inspect " + msg.Cmd);
                        break;
                    case StoryCmd.EventZoneFire:
                    {
                        var z = FindAlive<EventZone>(id);
                        if (z == null) break;
                        try
                        {
                            if (LocalInspect.LockWorld(z.gameObject))
                                PlaytestLog.Event("Story", "skip lock EventZoneFire");
                            else if (LocalInspect.AirlockCinematic(z.gameObject))
                                PlaytestLog.Event("Story", "skip airlock EventZoneFire");
                            else
                            {
                                z.triggered = true;
                                if (LocalInspect.InLocalRoom(z.gameObject))
                                {
                                    // Host may be in another room: flag consequences of this Invoke are forwarded
                                    // to the host (author scope) so the one shared story still gets them.
                                    BeginAuthorScope();
                                    try { if (z.onInRange != null) z.onInRange.Invoke(); }
                                    catch (System.Exception ex) { WarnOnce("EventZone onInRange", ex); }
                                    finally { EndAuthorScope(); }
                                }
                                else
                                    PlaytestLog.Event("Story", "skip other-room EventZoneFire id=" + id.ToString("X16"));
                            }
                        }
                        catch (System.Exception ex)
                        {
                            ModRuntime.Log?.Warning("[Story] EventZoneFire: " + ex.Message);
                        }
                        break;
                    }
                    case StoryCmd.MultiConditionFire:
                    {
                        var m = FindAlive<MultiConditionEvent>(id);
                        if (m == null) break;
                        try
                        {
                            if (!LocalInspect.InLocalRoom(m.gameObject)) break;
                            BeginAuthorScope();
                            try { ReplayMultiCondition(m, msg.Int0 == 1); }
                            finally { EndAuthorScope(); }
                        }
                        catch (System.Exception ex) { WarnOnce("MultiConditionFire", ex); }
                        break;
                    }
                    case StoryCmd.DetermineEnding:
                    {
                        try
                        {
                            // Commit normally precedes this; Int0 is the host's verdict in case it did not.
                            if (msg.Int0 >= 0 && msg.Int0 <= 3) END_Manager.Ending = msg.Int0;
                            ModRuntime.Log?.Msg("[Story] END_Manager.Ending=" + END_Manager.Ending);
                            var finales = WorldLookup.All<Finale>();
                            if (finales != null)
                            {
                                // CalculatePlaystyle is suppressed on clients (it would recompute Circle/Death from this
                                // peer's own GlobalStats); the commit sent right before this already carries the host's
                                // final END values, and CutsceneStart must not start the ending cutscene a second time.
                                _endingApply = true;
                                try
                                {
                                    for (int fi = 0; fi < finales.Length; fi++)
                                    {
                                        if (finales[fi] == null) continue;
                                        finales[fi].determineEnding();
                                        break;
                                    }
                                }
                                finally { _endingApply = false; }
                            }
                        }
                        catch (System.Exception ex) { WarnOnce("DetermineEnding", ex); }
                        break;
                    }
                    case StoryCmd.GoToPenny:
                    {
                        try
                        {
                            var finales = WorldLookup.All<Finale>();
                            if (finales != null)
                            {
                                for (int fi = 0; fi < finales.Length; fi++)
                                {
                                    if (finales[fi] == null) continue;
                                    finales[fi].goToPenny();
                                    break;
                                }
                            }
                        }
                        catch (System.Exception ex) { WarnOnce("GoToPenny", ex); }
                        break;
                    }
                    case StoryCmd.PartyCheat:
                    {
                        int localId = net != null ? net.LocalPlayerId : -1;
                        if (msg.Int0 == localId) break; // this peer ran it itself
                        if (CheatRecently(msg.Text)) break; // in-room replay of the same cutscene already ran it
                        PlaytestLog.Event("Story", "party cheat '" + msg.Text + "'");
                        RunCheat(msg.Text);
                        break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Story] Apply presentation: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
                if (endSkip) RebaseEnd();
            }
        }

        /// <summary>Re-baseline the END delta to the current statics: writes made so far are not this peer's contribution.</summary>
        private void RebaseEnd()
        {
            if (!_endBaseValid) return;
            var cur = new int[7];
            float healed, memory;
            if (ReadEnd(cur, out healed, out memory)) CaptureEndBase(cur, healed, memory);
        }

        /// <summary>
        /// Native TryOnce / TryTrigger invoke OnTryDone themselves (MultiConditionEvent.c: tried &gt;= tries, and
        /// TryOnce only while !triedOnce), which is exactly what the host request path relies on. Never call
        /// OnTryDone on top: ApplyMultiConditionEvent (PuzzleState poll) already fires it on the triedOnce rising
        /// edge, and the two paths latch triedOnce first, so OnTryDone runs exactly once per peer.
        /// </summary>
        private static void ReplayMultiCondition(MultiConditionEvent m, bool trigger)
        {
            try
            {
                if (trigger) m.TryTrigger();
                else m.TryOnce();
            }
            catch (System.Exception ex) { WarnOnce("Multi Try", ex); }
        }

        private static bool IsLocalInspect(StoryCmd cmd)
        {
            return cmd == StoryCmd.EventScreenStart
                || cmd == StoryCmd.EventScreenExit
                || cmd == StoryCmd.OpenBookMemory
                || cmd == StoryCmd.BookOpen;
        }

        private static T Find<T>(ulong worldId) where T : Component =>
            WorldLookup.Find<T>(worldId, "Story");

        // Unity destroyed objects compare equal to null via overloaded ==.
        private static T FindAlive<T>(ulong worldId) where T : Component
        {
            var c = Find<T>(worldId);
            if (c == null) return null;
            try
            {
                if (c.gameObject == null) return null;
            }
            catch { return null; }
            return c;
        }

        // PuzzleStateMessage MultiCondition / SaveRoom / Cutscene / Dialogue ownership.
        internal static bool TryReadPuzzle(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.MultiConditionEvent:
                {
                    var x = (MultiConditionEvent)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.triedOnce, false, false, x.tried, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.SaveRoomEvent:
                {
                    var x = (SaveRoomEvent)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.triggered, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.CutsceneCompleted:
                {
                    var x = (CutsceneManager)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.completed, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DialoguePlayedOnce:
                {
                    var x = (Dialogue)c;
                    // Flavor / EventScreen / lock lines stay local — do not party-sync.
                    if (Sync.LocalInspect.Dialogue(x))
                        return false;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.playedOnce, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        internal static void ApplyMultiConditionEvent(MultiConditionEvent x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Rising-edge OnTryDone: Melon triedOnce / tried / OnTryDone (PascalCase
            // UnityEvent). Native TryOnce early-outs on triedOnce — consequence never
            // recoverable once latched without Invoke. Live StoryCmd.MultiConditionFire
            // already TryOnce/TryTrigger + OnTryDone.Invoke (OK). Late-join FullRefresh
            // previously only latched triedOnce — softlock (Proceed / ProceedDelayed /
            // delayedEvent / SetTrigger never fire). Dig M: mirror ApplyDoorLockEvent
            // 0.5.31 both-path rising-edge: capture was=triedOnce; latch; if !e.Bool0
            // return; if !was → BeginApply + OnTryDone.Invoke(). Live StoryCmd path
            // sets triedOnce first → Apply sees was=true → no double-fire. Protocol 10
            // unchanged (reuse MultiConditionEvent Bool0 triedOnce + Int0 tried).
            bool was = false;
            try { was = x.triedOnce; } catch (System.Exception ex) { Guard.Swallow(ex); }
            x.triedOnce = e.Bool0;
            x.tried = e.Int0;
            if (!e.Bool0) return;
            if (!was)
            {
                NetGate.BeginApply();
                try
                {
                    if (x.OnTryDone != null)
                        x.OnTryDone.Invoke();
                }
                catch (System.Exception ex) { Guard.Swallow(ex); }
                finally { NetGate.EndApply(); }
            }
        }

        internal static void ApplySaveRoomEvent(SaveRoomEvent x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.triggered = e.Bool0;
            if (!e.Bool0) return;
            // Party-wide: once triggered, disable the one-shot interaction on observers.
            try
            {
                if (x.eventInter != null)
                    x.eventInter.enabled = false;
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        internal static void ApplyCutsceneCompleted(CutsceneManager x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                x.completed = true;
        }

        internal static void ApplyDialoguePlayedOnce(Dialogue x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (Sync.LocalInspect.Dialogue(x)) return;
            x.playedOnce = e.Bool0;
        }
    }
}
