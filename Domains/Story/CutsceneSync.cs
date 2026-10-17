// CutsceneManager start / skip and CutsceneCut.Proceed across peers. Dedupe model and its two windows: CutsceneStamps.
//
//   Host-authored start: the host runs StartCutscene natively (its own trigger, or a client's CutsceneStart request)
//     and broadcasts CutsceneStart. A client never starts one from its own trigger: it asks the host once and plays
//     the host's answer like every other peer.
//   Rooms: a start belongs to the room of the player whose trigger started it (the author's room, a Room WorldId on
//     the request and the presentation). CutsceneManagers sit under a scene-level "Cutscenes" root, outside every
//     Room, so the object itself says nothing about where it plays. Only peers in the author's room start it, the
//     host included: a host elsewhere relays a client's start without running it. A client whose trigger fires for a
//     cutscene that is already running elsewhere in the party (it walked in late) gets the start for itself.
//   Observer replay: a CutsceneStart presentation starts the cutscene on every peer in the author's room that is not
//     running it. Everyone else gets the durable form once a watcher's copy completes (`completed` + onGameLoad):
//     the host's through the PuzzleState CutsceneCompleted poll, a client's through CutsceneDone when the host did
//     not watch (the host then settles its own copy and the poll carries it on).
//   Late-join replay: the host's full commit names the cutscene it is running (StorySyncService.Send), and the joiner
//     applies it as a CutsceneStart presentation through the same gates (room = the host's room on the roster).
//   Level intros (onStart) start from CutsceneManager.Start, never StartCutscene: every peer, joiners included, runs
//     its own when it loads the scene, so they are not broadcast.
//   Skip / Proceed: per player. Every peer watches its own copy and skips / proceeds it alone, like solo; nothing goes
//     on the wire. A client's skip events run in the author scope, so flags only it wrote reach the host (the host may
//     still be watching). CutsceneCut.Proceed only sets `proceed` (the cut's events run later in the Cutscene
//     coroutine), so it needs no hook. A scene-ending cutscene's load waits for the peer still watching
//     (SceneFollowService: a follow is held while this peer is in a cutscene; the host holds a client's request).
// A scene the host is not in is run by its client owner (Sync/SceneOwners): it plays the host's part above for the
// peers there (requests reach it through the host). PEN_Titles airlock cinematics (LocalInspect.AirlockCinematic) stay local on every path.
using SyncRADation.Patches;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    internal static class CutsceneSync
    {
        // persistent: holder object only; its stamps are cleared on scene change / session reset by InteractionSyncService.OnSceneChanged
        static readonly CutsceneStamps _stamps = new CutsceneStamps();

        // persistent: cleared with the stamps on scene change / session reset (OnSceneChanged)
        static readonly System.Collections.Generic.List<CutsceneManager> _deferred = new System.Collections.Generic.List<CutsceneManager>();

        // persistent: cleared with the stamps on scene change / session reset (OnSceneChanged)
        static readonly System.Collections.Generic.List<CutsceneManager> _watching = new System.Collections.Generic.List<CutsceneManager>();

        sealed class Live
        {
            public CutsceneManager C;
            public ulong Id;
            public string Name;
            public float At;
        }
        // persistent: log bookkeeping of the cutscenes running on this peer; cleared with the stamps (OnSceneChanged)
        static readonly System.Collections.Generic.List<Live> _live = new System.Collections.Generic.List<Live>();

        static float Now => Time.unscaledTime;

        static string Hex(ulong id) => id.ToString("X16");

        /// <summary>Scene change / session reset (via InteractionSyncService.OnSceneChanged): stamps are per-scene WorldIds.</summary>
        public static void OnSceneChanged()
        {
            _stamps.Clear();
            _deferred.Clear();
            _watching.Clear();
            _live.Clear();
        }

        /// <summary>StartCutscene postfix: a cutscene is running on this peer now (whoever started it). Log only.</summary>
        internal static void NoteStarted(CutsceneManager c, string by = null)
        {
            try
            {
                if (!StartedHere(c)) return;
                for (int i = 0; i < _live.Count; i++)
                    if (_live[i].C == c) return;
                var l = new Live { C = c, Id = WorldId.FromGameObject(c.gameObject), Name = c.gameObject.name, At = Now };
                _live.Add(l);
                PlaytestLog.Event("Cutscene", "start '" + l.Name + "' id=" + Hex(l.Id) + " room='"
                    + NetworkDamageSystem.CurrentRoomName() + "' skippable=" + !c.unskippable
                    + " by=" + (by ?? (NetGate.IsApplying ? "party" : "local trigger")));
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>The cutscenes running on this peer, for the [State] line ("" when none).</summary>
        internal static string LiveNames()
        {
            if (_live.Count == 0) return "";
            if (_live.Count == 1) return _live[0].Name;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _live.Count; i++) sb.Append(i == 0 ? "" : "+").Append(_live[i].Name);
            return sb.ToString();
        }

        static void TickLive()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var l = _live[i];
                try
                {
                    if (l.C != null && StartedHere(l.C)) continue;
                    _live.RemoveAt(i);
                    PlaytestLog.Event("Cutscene", "end '" + l.Name + "' id=" + Hex(l.Id) + " after "
                        + (Now - l.At).ToString("F1") + "s: "
                        + (l.C == null ? "object gone" : l.C.completed ? "completed" : "stopped, not completed")
                        + " room='" + NetworkDamageSystem.CurrentRoomName() + "'");
                }
                catch (System.Exception e)
                {
                    Guard.Swallow(e);
                    if (i < _live.Count) _live.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Does a shared start play for this peer? Yes in the author's room. Without a room on either side (a scene
        /// with no rooms, a late-join replay) the roster's host room decides for a replay, else the cutscene's own
        /// Room ancestor when it has one.
        /// </summary>
        static bool WatchHere(CutsceneManager c, string authorRoom, bool replay)
        {
            if (replay && string.IsNullOrEmpty(authorRoom))
            {
                // The host is watching it, so the host's room is the author's.
                string host = PartyVitals.RoomOf(0) ?? "";
                string mine = NetworkDamageSystem.CurrentRoomName();
                if (host.Length > 0 && mine.Length > 0) return string.Equals(host, mine, System.StringComparison.Ordinal);
            }
            return LocalInspect.InAuthorRoom(c.gameObject, authorRoom);
        }

        /// <summary>
        /// This player has a pickup's yes/no open. Solo, no cutscene can start then (pickUp pauses the game and sets the
        /// cutscene flag); a shared start from another player would replace that dialogue, so its callback and release
        /// never ran: the item was never added, the prop stayed triggered here (taken in this peer's world, there on
        /// everyone else's), and gameState was left wrong (pilot soak, ROT_Rotfront: a client's trigger started
        /// Isa_Death while the host and the other client each had a pickup open).
        /// </summary>
        static bool PickupOpenHere()
        {
            var net = LanNetworkManager.Instance;
            try { return net != null && net.PickupSync != null && net.PickupSync.LocalPickupOpen(); }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>Start a shared cutscene here now, or once this player's open pickup has been answered and released.</summary>
        static void StartOrDefer(CutsceneManager c, ulong id)
        {
            if (PickupOpenHere())
            {
                if (!_deferred.Contains(c)) _deferred.Add(c);
                PlaytestLog.Event("Story", "CutsceneStart waits for the open pickup id=" + Hex(id));
                return;
            }
            NativeStart(c);
        }

        static void NativeStart(CutsceneManager c)
        {
            NetGate.BeginApply();
            try
            {
                c.StartCutscene();
                // The StartCutscene postfix arms the skip UI for a skippable one; an unskippable start disarms it.
                if (c.unskippable) CutsceneSkippingUI.skippableCutscene = false;
            }
            finally { NetGate.EndApply(); }
            if (!StartedHere(c))
            {
                bool active = false, done = false;
                try { active = c.gameObject.activeInHierarchy; done = c.completed; } catch (System.Exception e) { Guard.Swallow(e); }
                PlaytestLog.Event("Cutscene", "start of '" + c.gameObject.name + "' did nothing here (object active=" + active
                    + " completed=" + done + ")");
                return;
            }
            if (NetGate.SceneFollower && !_watching.Contains(c)) _watching.Add(c);
        }

        /// <summary>
        /// Client: a shared cutscene this peer watched is over here (played out or skipped). The host may not have
        /// watched it (other room): tell it, so the party's one copy of the story gets it as completed.
        /// </summary>
        static void TickWatched()
        {
            for (int i = _watching.Count - 1; i >= 0; i--)
            {
                var c = _watching[i];
                try
                {
                    if (c != null && !c.completed && StartedHere(c)) continue;
                    _watching.RemoveAt(i);
                    // Stopped without completing (its object was switched off): nothing to report.
                    if (c == null || !c.completed || !NetGate.SceneFollower) continue;
                    LanNetworkManager.Instance.InteractionHandlers.SendInteractionRequest(
                        WorldId.FromGameObject(c.gameObject), InteractionKind.CutsceneDone);
                }
                catch (System.Exception e)
                {
                    Guard.Swallow(e);
                    if (i < _watching.Count) _watching.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Every frame (host and client story tick): report watched cutscenes that ended, and run the starts held
        /// behind a pickup that has now closed.
        /// </summary>
        internal static void TickDeferred()
        {
            if (_live.Count > 0) TickLive();
            if (_watching.Count > 0) TickWatched();
            if (_deferred.Count == 0 || PickupOpenHere()) return;
            var run = _deferred.ToArray();
            _deferred.Clear();
            foreach (var c in run)
            {
                try
                {
                    if (c == null || c.completed || StartedHere(c)) continue;
                    PlaytestLog.Event("Story", "CutsceneStart after pickup id=" + Hex(WorldId.FromGameObject(c.gameObject)));
                    NativeStart(c);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
        }

        /// <summary>This peer's coroutine for the cutscene is live (started here, not completed).</summary>
        internal static bool StartedHere(CutsceneManager c)
        {
            if (c == null) return false;
            try { return c.cutscene != null && !c.completed; }
            catch (System.Exception ex) { StorySyncService.WarnOnce("Cutscene StartedHere", ex); }
            return false;
        }

        /// <summary>
        /// Host: a completed cutscene's "cut &lt;UniqueId.id&gt;" key, which native code writes only in CutsceneManager.Save
        /// (save room). OnEnable re-reads it, so without it a manager that re-enables (room chunk wake) forgot it had
        /// played and could start again; with it the story commit carries it and every peer's OnEnable runs onGameLoad.
        /// </summary>
        internal static void PersistCompleted(CutsceneManager c)
        {
            try
            {
                var uid = c.GetComponent<UniqueId>();
                if (uid == null || string.IsNullOrEmpty(uid.id)) return;
                string key = "cut " + uid.id;
                if (!SProgress.GetBool(key, false)) SProgress.SetBool(key, true);
            }
            catch (System.Exception ex) { StorySyncService.WarnOnce("Cutscene PersistCompleted", ex); }
        }

        /// <summary>
        /// CutsceneManager.OnDisable postfix: deactivating the object stops its coroutines, but native code never clears
        /// `cutscene` (StartCutscene / Start only assign it), so StartedHere would call a dead cutscene live (pause would
        /// Skip it again and re-fire its end events; the late-join commit would name it). A disabled component on an
        /// active object keeps its coroutines running and stays live.
        /// </summary>
        internal static void OnDisabled(CutsceneManager c)
        {
            try
            {
                if (c == null || c.gameObject.activeInHierarchy) return;
                c.cutscene = null;
                c.enforcer = null;
            }
            catch (System.Exception ex) { StorySyncService.WarnOnce("Cutscene OnDisabled", ex); }
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
                // Native StartCutscene does nothing on a switched-off manager (CutsceneManager.c: activeInHierarchy
                // && !completed). A trigger reaching one (LOV Library before the pistol is taken) must not ask the
                // party to start it: the host stamped and broadcast a start nobody could run.
                if (!c.gameObject.activeInHierarchy) return true;
                ulong id = WorldId.FromGameObject(c.gameObject);
                if (_stamps.Skipped(id, Now)) return false;
                // The scene's owner (the host, or the lowest client in a scene the host is not in) starts and relays it.
                if (NetGate.SceneOwner)
                {
                    // A client request (HostApplyStart) may have started it already; a second native StartCutscene would
                    // run a second coroutine. That start already broadcast.
                    if (StartedHere(c) || _deferred.Contains(c))
                    {
                        PlaytestLog.Event("Story", "host StartCutscene dup id=" + Hex(id));
                        return false;
                    }
                    if (!_stamps.TryStart(id, Now))
                    {
                        // Started moments ago for another room (a client's request the host relayed without
                        // watching): the host's own trigger fired now, so it plays here. That start was broadcast.
                        PlaytestLog.Event("Story", "host joins the running CutsceneStart id=" + Hex(id));
                        return true;
                    }
                    LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart, id, 0,
                        LocalInspect.LocalRoomKey());
                    return true;
                }
                if (!StartedHere(c) && _stamps.TryRequest(id, Now))
                    LanNetworkManager.Instance.InteractionHandlers.SendInteractionRequest(id, InteractionKind.CutsceneStart,
                        text: LocalInspect.LocalRoomKey());
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
                PlaytestLog.Event("Cutscene", "skip '" + c.gameObject.name + "' id=" + Hex(WorldId.FromGameObject(c.gameObject)));
                _stamps.TrySkip(WorldId.FromGameObject(c.gameObject), Now);
                if (!NetGate.Client) return true;
                SkipInAuthorScope(c);
                return false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        // ------------------------------------------------------------------ host: client requests

        /// <summary>
        /// Host: a client's trigger asked for this cutscene from that room. N requesters (or the host's own start)
        /// start it once; the host runs it only when it stands in that room too.
        /// </summary>
        internal static bool HostApplyStart(ulong id, string room, int sender, LanNetworkManager net)
        {
            var c = InteractionSyncService.FindAlive<CutsceneManager>(id, "Interact");
            if (c == null || LocalInspect.AirlockCinematic(c.gameObject)) return true;
            if (c.completed)
            {
                // Already played here: the requester's copy forgot (re-enabled before the "cut" key reached it);
                // the CutsceneCompleted poll settles it, no start goes out.
                PlaytestLog.Event("Interact", "CutsceneStart completed id=" + Hex(id));
                return true;
            }
            if (StartedHere(c) || _deferred.Contains(c) || !_stamps.TryStart(id, Now))
            {
                // Started moments ago (the host is watching it, or another player's request): that broadcast named
                // its author's room and this requester was not in it then. Its own trigger fired now, so it gets the
                // start for its room alone (a peer already running it drops the repeat).
                PlaytestLog.Event("Interact", "CutsceneStart dup id=" + Hex(id) + ", start sent to p" + sender);
                net.StoryHandlers.SendStoryPresentationTo(sender, new StoryPresentationMessage
                {
                    WorldId = unchecked((long)id),
                    Cmd = StoryCmd.CutsceneStart,
                    Text = room ?? ""
                });
                return true;
            }
            if (WatchHere(c, room, false))
                StartOrDefer(c, id);
            else
                PlaytestLog.Event("Interact", "CutsceneStart other-room id=" + Hex(id) + " room=" + room
                    + " here=" + LocalInspect.LocalRoomKey());
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart, id, 0, room ?? "");
            return true;
        }

        /// <summary>
        /// Host: a client's copy of a shared cutscene is over. A host that watched it too completes its own; one that
        /// did not (other room) takes the durable form now, and the CutsceneCompleted poll hands it to everyone else.
        /// </summary>
        internal static bool HostApplyDone(ulong id)
        {
            var c = InteractionSyncService.FindAlive<CutsceneManager>(id, "Interact");
            if (c == null || LocalInspect.AirlockCinematic(c.gameObject)) return true;
            if (c.completed || StartedHere(c) || _deferred.Contains(c)) return true;
            PlaytestLog.Event("Interact", "CutsceneDone durable id=" + Hex(id));
            StoryFlagPuzzleSyncService.ApplyCutscene(c, new PuzzleStateEntry { Bool0 = true });
            return true;
        }

        // ------------------------------------------------------------------ observers: presentations (inside the apply scope)

        /// <summary>Host's CutsceneStart (live or late-join replay): start it here once, in the author's room, unless it runs already.</summary>
        internal static void ApplyStart(ulong id, string room, bool replay)
        {
            var c = InteractionSyncService.FindAlive<CutsceneManager>(id, "Story");
            if (c == null) return;
            if (LocalInspect.AirlockCinematic(c.gameObject))
            {
                PlaytestLog.Event("Story", "skip local cinematic CutsceneStart");
                return;
            }
            if (!WatchHere(c, room, replay))
            {
                PlaytestLog.Event("Story", "skip other-room CutsceneStart id=" + Hex(id) + " room=" + room
                    + " here=" + LocalInspect.LocalRoomKey());
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
            StartOrDefer(c, id);
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
