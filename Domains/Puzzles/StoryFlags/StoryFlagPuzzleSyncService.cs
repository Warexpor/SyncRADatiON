using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Host-authored story flags on the PuzzleState poll: MultiConditionEvent tried count, SaveRoomEvent, cutscene
    /// completed, Dialogue playedOnce. Never client-emitted; the presentation itself stays with Story.
    /// </summary>
    public sealed class StoryFlagPuzzleSyncService
    {
        internal static PuzzleStateEntry ReadMultiCondition(MultiConditionEvent x, long wid)
            => Mk(PuzzleType.MultiConditionEvent, wid, x.triedOnce, false, false, x.tried, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadSaveRoom(SaveRoomEvent x, long wid)
            => Mk(PuzzleType.SaveRoomEvent, wid, x.triggered, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadCutscene(CutsceneManager x, long wid)
        {
            if (x.completed && NetGate.Host) CutsceneSync.PersistCompleted(x);
            return Mk(PuzzleType.CutsceneCompleted, wid, x.completed, false, false, 0, 0, 0, 0, 0);
        }

        /// <summary>Flavor / EventScreen / lock lines stay local and are not party-synced.</summary>
        internal static bool TryReadDialogue(Dialogue x, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            if (LocalInspect.Dialogue(x)) return false;
            entry = Mk(PuzzleType.DialoguePlayedOnce, wid, x.playedOnce, false, false, 0, 0, 0, 0, 0);
            return true;
        }

        /// <summary>
        /// Rising edge on the host's count (Bool0 triedOnce, Int0 tried). Native TryOnce / TryTrigger invoke OnTryDone
        /// on the call where tried reaches tries (MultiConditionEvent.c), and a latched triedOnce / count never re-fires
        /// it, so a peer that missed the live call (late join, other room) fires it here when the host's tried crosses
        /// tries past the local count. A live MultiConditionFire replay already moved the local count, so it never
        /// fires twice.
        /// </summary>
        internal static void ApplyMultiCondition(MultiConditionEvent x, PuzzleStateEntry e)
        {
            if (x == null) return;
            int was = x.tried;
            int need = Mathf.Max(1, x.tries);
            x.triedOnce = e.Bool0;
            x.tried = e.Int0;
            if (was >= need || e.Int0 < need) return;
            // The host counted any END writes of OnTryDone when it ran natively: a client sends its own pending
            // delta first and re-baselines after, so the replay is never added to the host's tally again.
            var net = LanNetworkManager.Instance;
            var story = net != null && NetGate.ClientRole ? net.StorySync : null;
            story?.FlushEndDelta(net);
            Native("multicondition-done", () => PuzzleEdge.Invoke(x.OnTryDone));
            story?.RebaseEnd();
        }

        /// <summary>Party-wide: once triggered, the one-shot interaction is off on observers too.</summary>
        internal static void ApplySaveRoom(SaveRoomEvent x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.triggered = e.Bool0;
            if (e.Bool0 && x.eventInter != null)
                x.eventInter.enabled = false;
        }

        /// <summary>
        /// Per-player cutscenes: the host finishing (or skipping) its copy first must not mark this peer's copy, still
        /// playing, completed (its skip would then be refused); its own coroutine sets completed at the end. A copy that
        /// never ran here (other room, late join) gets what the native load does for a completed cutscene
        /// (CutsceneManager.OnEnable / Load: completed from SProgress "cut &lt;id&gt;", then onGameLoad, the "already
        /// happened" state: SetActive, LoadState, setLevel, stopInstant ...).
        /// </summary>
        internal static void ApplyCutscene(CutsceneManager x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0 || x.completed || CutsceneSync.StartedHere(x)) return;
            x.completed = true;
            if (x.onGameLoad != null) Native("cutscene-onGameLoad", () => x.onGameLoad.Invoke());
        }

        internal static void ApplyDialogue(Dialogue x, PuzzleStateEntry e)
        {
            if (x == null || LocalInspect.Dialogue(x)) return;
            x.playedOnce = e.Bool0;
        }
    }
}
