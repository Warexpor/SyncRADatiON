// Shared story. The host commits SProgress / Dialoguer globals / END_Manager to every peer (StoryCommit); clients
// forward their own SProgress writes and END contributions to the host; presentation commands (cutscenes, event zones,
// multi-conditions, the ending) replay natively on peers. This file: the commit pipeline and the client forward.
// StorySyncService.End.cs: END tally, ending, scripted cheats. StorySyncService.Presentation.cs: presentations.
// Cutscene rules: CutsceneSync. Direct slot access: ProgressSlot.
//
// Commits (host -> clients):
//   incremental   - keys written since the last broadcast; Dialoguer globals only when they changed.
//   full          - the whole shared table read from the live slot (join / resync / scene change), globals always.
//   authoritative - the full broadcast after SaveManager.Load / NewGame replaced the slot: clients drop keys it lacks
//                   (also every full inside the window after a party wipe).
// A unicast (join / resync dump, ending re-send) is always full and leaves the party's broadcast bookkeeping alone.
// Per-player keys (StoryWire.IsPerPlayerKey) never ride a commit; END_Manager.memoryTime is the host's clock only.
using System.Collections.Generic;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed partial class StorySyncService
    {
        // ---- host: the shared table (live slot's shared keys at the last full read + every write noted since)
        private readonly Dictionary<string, StoryFlagEntry> _flags = new Dictionary<string, StoryFlagEntry>();
        // Keys written since the last broadcast: an incremental commit carries only these.
        private readonly HashSet<string> _dirty = new HashSet<string>();
        private bool _needSend = true;
        private bool _fullDump = true;
        // The next full broadcast carries the Authoritative bit (SaveManager.Load / NewGame replaced the live slot).
        private bool _authFull;
        private float _timer;
        const float CommitInterval = 0.75f;

        // ---- client: a commit buffered while loading / in a scene the host is not in
        private StoryCommitMessage _pendingCommit;
        private bool _hasPendingCommit;
        // Until this time every full commit replaces local SProgress wholesale (the host reverted to a save after a party
        // wipe and may send a pre-reload dump before the post-reload one; each is exact, so the last wins).
        private float _authoritativeUntil;
        const float AuthoritativeWindow = 30f;
        // A commit that could not be applied completely asks for one fresh snapshot per scene.
        private bool _healRequested;

        // ---- client: SProgress writes forwarded to the host, coalesced per key
        private readonly Dictionary<string, StoryFlagEntry> _outbox = new Dictionary<string, StoryFlagEntry>();
        private readonly Dictionary<string, float> _outboxSentAt = new Dictionary<string, float>();
        private readonly List<string> _flushKeys = new List<string>();
        private float _clientTimer;
        const int OutboxPerFlush = 24;
        private static int _authorDepth;
        private static int _suppressForward;

        // persistent: warn-once set (a recurring fault logs one line per process)
        private static readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>True while a client-applied presentation runs UnityEvents whose SProgress writes must reach the host.</summary>
        public static bool ClientAuthorScope => _authorDepth > 0;
        public static void BeginAuthorScope() => _authorDepth++;
        public static void EndAuthorScope() { if (_authorDepth > 0) _authorDepth--; }

        /// <summary>SaveManager.Save/Load/NewGame (and per-player savers) write per-player state into SProgress: never forward those.</summary>
        public static bool ForwardSuppressed => _suppressForward > 0;
        public static void BeginSuppressForward() => _suppressForward++;
        public static void EndSuppressForward() { if (_suppressForward > 0) _suppressForward--; }

        internal static void WarnOnce(string site, System.Exception ex)
        {
            if (!_warned.Add(site)) return;
            ModRuntime.Log?.Warning("[Story] " + site + ": " + ex.Message);
        }

        public void Reset()
        {
            _flags.Clear();
            _dirty.Clear();
            _needSend = true;
            _fullDump = true;
            _authFull = false;
            _timer = 0f;
            _hasPendingCommit = false;
            _authoritativeUntil = 0f;
            _healRequested = false;
            _outbox.Clear();
            _outboxSentAt.Clear();
            _clientTimer = 0f;
            _authorDepth = 0;
            _suppressForward = 0;
            ResetEnd();
            ResetPresentation();
        }

        public void OnSceneChanged()
        {
            _healRequested = false;
            ResetPresentation();
            RequestFullSend();
        }

        // ------------------------------------------------------------------ host: commit pipeline

        public void RequestFullSend()
        {
            // A join / resync dump unicasts its own full commit: the party does not need another full broadcast after it.
            var net = LanNetworkManager.Instance;
            if (net != null && net.UnicastActive) return;
            _needSend = true;
            _fullDump = true;
        }

        /// <summary>Host: the live slot was replaced wholesale (SaveManager.Load / NewGame): the next full dump is authoritative.</summary>
        public void RequestAuthoritativeFull()
        {
            _authFull = true;
            RequestFullSend();
        }

        /// <summary>Host: flag-less change (END counters, ending id) that still needs a StoryCommit.</summary>
        public void MarkDirty() => _needSend = true;

        public void NoteBool(string key, bool val) => Note(new StoryFlagEntry { Kind = 0, Key = key, BoolVal = val });
        public void NoteInt(string key, int val) => Note(new StoryFlagEntry { Kind = 1, Key = key, IntVal = val });
        public void NoteFloat(string key, float val) => Note(new StoryFlagEntry { Kind = 2, Key = key, FloatVal = val });
        public void NoteString(string key, string val) => Note(new StoryFlagEntry { Kind = 3, Key = key, StringVal = val ?? "" });
        public void NoteVector(string key, Vector3 val) =>
            Note(new StoryFlagEntry { Kind = 4, Key = key, FloatVal = val.x, VecY = val.y, VecZ = val.z });

        private void Note(StoryFlagEntry e)
        {
            if (string.IsNullOrEmpty(e.Key)) return;
            // Host per-player keys (enemy save, radio, minimap, inventory slot, help prompts) never ride the shared commit.
            if (StoryWire.IsPerPlayerKey(e.Key)) return;
            _flags[e.Key] = e;
            _dirty.Add(e.Key);
            _needSend = true;
        }

        public void TickHost(LanNetworkManager net)
        {
            if (net == null || !NetGate.Host) return;
            CutsceneSync.TickDeferred();
            // END_Manager statics are written directly by NPC_Tracker / InteractiveLockSingle / PlayerState.
            int sig = EndSignature();
            if (sig != _endSig)
            {
                _endSig = sig;
                _needSend = true;
            }
            if (!_needSend) return;
            // Unscaled: slow-mo / a paused host must not stall the shared story.
            _timer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_timer < CommitInterval) return;
            _timer = 0f;
            Send(net, _fullDump);
        }

        /// <summary>
        /// Host: broadcast whatever the next tick would send, now (a pending full stays full). Before a unicast dump to
        /// one joiner the others must get writes still waiting for their broadcast: the dump records them as sent.
        /// </summary>
        public void FlushDiffNow()
        {
            var net = LanNetworkManager.Instance;
            if (!NetGate.Host || !_needSend) return;
            _timer = 0f;
            Send(net, _fullDump);
        }

        public void Send(LanNetworkManager net, bool full, bool replayPresentation = false)
        {
            bool unicast = net.UnicastActive;
            if (unicast) full = true;

            StoryFlagEntry[] flags;
            bool authoritative = false;
            if (full)
            {
                // The table mirrors the live slot exactly: after SaveManager.Load (wipe reload), SProgress.Load or
                // ResetProgress, keys the slot no longer has must not be resurrected by stale entries.
                if (!ProgressSlot.ReadShared(_flags))
                {
                    // Never send a partial table as "the whole table" (an authoritative one would drop client keys).
                    _needSend = true;
                    _fullDump = true;
                    return;
                }
                flags = new StoryFlagEntry[_flags.Count];
                _flags.Values.CopyTo(flags, 0);
                if (!unicast)
                {
                    // A broadcast full is the whole table: it satisfies a pending full (and carries its authoritative bit).
                    authoritative = _authFull;
                    _authFull = false;
                    _fullDump = false;
                    _dirty.Clear();
                }
            }
            else
                flags = StoryWire.TakeDirty(_flags, _dirty);
            // An incremental broadcast never satisfies a pending full.
            if (!unicast) _needSend = _fullDump;

            // Dialoguer globals are never sent: every one the game reads is this player's own (the pickup yes/no
            // answer ItemPickup.release reads 0.1 s after the callback, item / key names, MedicationCheck's bag test);
            // the only dialogues with conditional phases are unused prototypes.
            bool replay = replayPresentation && CanReplayPresentation();
            var msg = new StoryCommitMessage
            {
                FullRefresh = full,
                Flags = flags,
                ActiveWorldId = replay ? unchecked((long)_lastWorldId) : 0,
                ActiveStoryCmd = replay ? (byte)_lastCmd : (byte)0,
                Authoritative = authoritative
            };
            try
            {
                msg.EndCircle = END_Manager.Circle;
                msg.EndDeath = END_Manager.Death;
                msg.EndGraves = END_Manager.Graves;
                msg.EndLeave = END_Manager.Leave;
                msg.EndingId = END_Manager.Ending;
                msg.EndNpc = END_Manager.NPC;
                msg.EndHealedTime = END_Manager.healedTime;
                msg.EndHealedSegments = END_Manager.healedTimeSegments;
                msg.EndMemoryTime = END_Manager.memoryTime;
                msg.EndDoors = END_Manager.doors;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            net.StoryHandlers.SendStoryCommit(msg);
            if (full)
                PlaytestLog.Event("Story", "commit full flags=" + flags.Length
                    + " cmd=" + (replay ? _lastCmd.ToString() : "-")
                    + (authoritative ? " authoritative" : ""));
        }

        // ------------------------------------------------------------------ client: commit apply

        /// <summary>
        /// Client: the host reverted the party to a save (wipe reload). The clients never load a slot, so their local
        /// SProgress still holds keys the reverted progress no longer has: full commits replace it wholesale for a while.
        /// A commit buffered from before the wipe is stale and dropped.
        /// </summary>
        public void OnPartyWipe()
        {
            if (!NetGate.ClientRole) return;
            _authoritativeUntil = Time.unscaledTime + AuthoritativeWindow;
            _hasPendingCommit = false;
            _endBaseValid = false;
            PlaytestLog.Event("Story", "party wipe: full commits are authoritative for " + AuthoritativeWindow + "s");
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
            // The Authoritative bit survives the merge: the result is a full table whenever the older commit was one.
            if (old.FullRefresh && old.Authoritative) msg.Authoritative = true;
            if (old.FullRefresh && !msg.FullRefresh)
            {
                msg.FullRefresh = true;
                if (msg.ActiveStoryCmd == 0)
                {
                    msg.ActiveStoryCmd = old.ActiveStoryCmd;
                    msg.ActiveWorldId = old.ActiveWorldId;
                }
            }
            _hasPendingCommit = false;
        }

        public void ApplyCommit(StoryCommitMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || NetGate.HostRole) return;
            bool loading = SceneFollowService.LocalIsTransient();
            if (loading || net.SceneMismatch)
            {
                // Loading or in a scene the host is not in (the wreck / hole split can stay mismatched for a long
                // time): keep the newest commit, merged, and apply it once the scene is up / matches.
                MergePending(ref msg);
                _pendingCommit = msg;
                _hasPendingCommit = true;
                PlaytestLog.Verbose("Story", "buffer commit (" + (loading ? "loading" : "scene mismatch") + ")");
                return;
            }
            // A live commit supersedes the buffered (older) one: fold it in so TickClient never re-applies it on top.
            MergePending(ref msg);

            // The host stamps the bit on the full that follows its SaveManager.Load / NewGame (it survives buffering);
            // the window after a PartyLife wipe stays as a fallback for a dump that predates the bit.
            bool authoritative = msg.FullRefresh && (msg.Authoritative || Time.unscaledTime < _authoritativeUntil);
            if (authoritative)
                PlaytestLog.Event("Story", "authoritative full commit: clearing local SProgress"
                    + (msg.Authoritative ? " (host bit)" : " (wipe window)"));

            // Unsent local END contributions survive the overwrite (client increments between commits).
            EndContribution unsent = authoritative ? default : UnsentEnd();
            int written = 0;
            bool ok = true;
            NetGate.BeginApply();
            if (authoritative) BeginSuppressForward();
            try
            {
                if (authoritative) ok = ProgressSlot.ClearShared();
                ok &= ProgressSlot.Apply(msg.Flags, out written);
                ok &= ApplyEnd(msg, unsent);
                DynamicSupplySync.ReconcileResupply();
            }
            finally
            {
                if (authoritative) EndSuppressForward();
                NetGate.EndApply();
            }

            PlaytestLog.Event("Story", "apply commit full=" + msg.FullRefresh
                + " flags=" + (msg.Flags != null ? msg.Flags.Length : 0) + " written=" + written
                + " cmd=" + (StoryCmd)msg.ActiveStoryCmd);
            if (!ok) HealAfterFailedApply(net);

            if (msg.FullRefresh && msg.ActiveStoryCmd != 0)
                ReplayActivePresentation(msg);
        }

        /// <summary>
        /// Part of a commit did not land (logged where it failed): the local story is now a mix of old and new. Ask the
        /// host for a fresh snapshot once per scene, so the next full commit settles it instead of the mix staying.
        /// </summary>
        void HealAfterFailedApply(LanNetworkManager net)
        {
            ModRuntime.Log?.Warning("[Story] commit applied only in part" + (_healRequested ? "" : "; requesting a resync"));
            if (_healRequested) return;
            _healRequested = true;
            net.SessionHandlers.RequestWorldSnapshot();
        }

        // ------------------------------------------------------------------ client: forward + tick

        public void TickClient(LanNetworkManager net)
        {
            if (net == null || !NetGate.Client) return;
            CutsceneSync.TickDeferred();
            // A blocked / queued scene request that never arrived is re-asked (LanNetworkManager ticks only the host side).
            SceneFollowService.TickClient();
            if (SceneFollowService.LocalIsTransient()) return;

            if (_hasPendingCommit && !net.SceneMismatch)
            {
                var m = _pendingCommit;
                _hasPendingCommit = false;
                PlaytestLog.Event("Story", "apply buffered commit (scene loaded)");
                ApplyCommit(m);
            }

            _clientTimer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_clientTimer < 0.2f) return;
            _clientTimer = 0f;
            // Not gated on a pending commit: under a permanent scene mismatch (wreck / hole split) the pending commit
            // never applies. END deltas are relative to the last *applied* baseline, so flushing stays consistent.
            FlushOutbox(net);
            FlushEndDelta(net);
        }

        /// <summary>
        /// Client: a story-flag write that must be authored on the host (host applies + commits to everyone).
        /// The local write still happens so the client's own logic sees it immediately.
        /// </summary>
        public void ClientForward(StoryFlagEntry e)
        {
            if (string.IsNullOrEmpty(e.Key) || ForwardSuppressed || SameAsLocal(e)) return;
            _outbox[e.Key] = e;
        }

        private void FlushOutbox(LanNetworkManager net)
        {
            if (_outbox.Count == 0) return;
            float now = Time.unscaledTime;
            _flushKeys.Clear();
            foreach (var kvp in _outbox)
            {
                if (_flushKeys.Count >= OutboxPerFlush) break;
                var e = kvp.Value;
                // Floats / vectors can be rewritten every frame by game code: 1 Hz per key.
                float last;
                if ((e.Kind == 2 || e.Kind == 4) && _outboxSentAt.TryGetValue(kvp.Key, out last) && now - last < 1f) continue;
                SendFlag(net, e);
                _outboxSentAt[kvp.Key] = now;
                _flushKeys.Add(kvp.Key);
            }
            for (int i = 0; i < _flushKeys.Count; i++)
                _outbox.Remove(_flushKeys[i]);
        }

        /// <summary>One SProgress write to the host (InspectFlag: Int0 = kind; a string rides as "key\nvalue").</summary>
        internal static void SendFlag(LanNetworkManager net, StoryFlagEntry e)
        {
            switch (e.Kind)
            {
                case 0:
                    net.InteractionHandlers.SendInteractionRequest(0, InteractionKind.InspectFlag, 0, e.BoolVal ? 1 : 0, 0f, 0f, 0f, e.Key);
                    break;
                case 1:
                    net.InteractionHandlers.SendInteractionRequest(0, InteractionKind.InspectFlag, 1, e.IntVal, 0f, 0f, 0f, e.Key);
                    break;
                case 2:
                    net.InteractionHandlers.SendInteractionRequest(0, InteractionKind.InspectFlag, 2, 0, e.FloatVal, 0f, 0f, e.Key);
                    break;
                case 3:
                    net.InteractionHandlers.SendInteractionRequest(0, InteractionKind.InspectFlag, 3, 0, 0f, 0f, 0f,
                        e.Key + "\n" + (e.StringVal ?? ""));
                    break;
                case 4:
                    net.InteractionHandlers.SendInteractionRequest(0, InteractionKind.InspectFlag, 4, 0, e.FloatVal, e.VecY, e.VecZ, e.Key);
                    break;
            }
        }

        /// <summary>The live slot already holds this value. Missing keys never match (sentinel defaults), so first writes always count.</summary>
        internal static bool SameAsLocal(StoryFlagEntry e)
        {
            try
            {
                switch (e.Kind)
                {
                    case 0:
                    {
                        bool a = SProgress.GetBool(e.Key, true);
                        return a == SProgress.GetBool(e.Key, false) && a == e.BoolVal;
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

        private static T FindAlive<T>(ulong worldId) where T : Component =>
            InteractionSyncService.FindAlive<T>(worldId, "Story");
    }
}
