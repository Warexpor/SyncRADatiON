using System;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.Events;

namespace SyncRADation.Networking
{
    /// <summary>
    /// The one rule for a puzzle's "solved" consequence. A live rising edge (was unsolved, a live peer change) for a
    /// player in the puzzle's room runs the native consequence (onSolved / onSuccess: exitEvent, cutscene, one-shots);
    /// every other apply (join dump, held re-snap, repeat, a solve in another room) runs only the idempotent durable
    /// form (door open, object SetActive, POI dim). Read wasSolved BEFORE latching the native flag: the latch made later
    /// re-applies look "already solved" and skip the door. Another room takes the durable form because onSolved handlers
    /// start cutscenes / exitEvent, and CutscenePatches lets StartCutscene through while applying, which yanked a player
    /// in another room into the solver's cutscene.
    /// </summary>
    internal static class PuzzleEdge
    {
        /// <param name="at">The puzzle component (room gate); null = no room gate (the live consequence is world state
        /// every peer needs).</param>
        internal static void Solved(string site, bool wasSolved, bool nowSolved, Action durable, Action onLive = null,
            Component at = null)
        {
            if (!nowSolved) return;
            bool live = !wasSolved && PuzzleSyncService.LiveEdge && onLive != null && (at == null || InRoom(at));
            Run(site, live ? onLive : durable, live ? durable : null);
        }

        /// <summary>Solved with an explicit room test (EventZones use the local player's room chunk).</summary>
        internal static void Solved(string site, bool wasSolved, bool nowSolved, Action durable, Action onLive, bool inRoom)
        {
            if (!nowSolved) return;
            bool live = !wasSolved && PuzzleSyncService.LiveEdge && onLive != null && inRoom;
            Run(site, live ? onLive : durable, live ? durable : null);
        }

        static void Run(string site, Action run, Action fallback)
        {
            if (run == null) return;
            try { run(); }
            catch (Exception ex)
            {
                PuzzleSyncService.WarnOnce("edge-" + site, ex.Message);
                // A failed live consequence must still leave the world unblocked.
                if (fallback == null) return;
                try { fallback(); }
                catch (Exception ex2) { PuzzleSyncService.WarnOnce("edge-durable-" + site, ex2.Message); }
            }
        }

        /// <summary>
        /// The local player is in the puzzle's room (its room-side screen anchor, since zoom-in stages sit away from
        /// their room) or zoomed into its screen.
        /// </summary>
        internal static bool InRoom(Component c)
        {
            if (c == null) return false;
            try
            {
                if (PuzzleFx.Viewing(c)) return true;
                return LocalInspect.InLocalRoom(PuzzleFx.Anchor(c).gameObject);
            }
            catch (Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>UnityEvent.Invoke under NetGate.BeginApply so native handlers do not re-emit.</summary>
        internal static void Invoke(UnityEvent ev)
        {
            if (ev == null) return;
            NetGate.BeginApply();
            try { ev.Invoke(); }
            finally { NetGate.EndApply(); }
        }

        /// <summary>
        /// The durable subset of a UnityEvent's persistent calls (scene data): GameObject.SetActive, MinimapPOIObject.dimPOI,
        /// RES_Paternoster.setPower. Presentation and one-shot calls (Play, exitEvent, StartCutscene, goBack, RecordSplit,
        /// setUnleavable) are skipped: this is what a late joiner / other-room peer needs, as the game's own onLoad events
        /// do (ROT_Keypad.onLoad, MED_MultiLock.onLoadUnlocked are exactly the SetActive / dimPOI part of the live event).
        /// </summary>
        internal static void ReplayDurable(UnityEventBase ev)
        {
            if (ev == null) return;
            var group = ev.m_PersistentCalls;
            var calls = group != null ? group.m_Calls : null;
            if (calls == null) return;
            NetGate.BeginApply();
            try
            {
                for (int i = 0; i < calls.Count; i++)
                {
                    // Each persistent call is its own target: one destroyed target must not block the others.
                    try { ReplayOne(calls[i]); }
                    catch (Exception ex) { PuzzleSyncService.WarnOnce("replay-durable", ex.Message); }
                }
            }
            finally { NetGate.EndApply(); }
        }

        static void ReplayOne(PersistentCall pc)
        {
            if (pc == null || pc.m_Target == null) return;
            switch (pc.m_MethodName)
            {
                case "SetActive":
                {
                    var go = pc.m_Target.TryCast<GameObject>();
                    if (go != null) go.SetActive(pc.m_Arguments.m_BoolArgument);
                    break;
                }
                case "dimPOI":
                {
                    var poi = pc.m_Target.TryCast<MinimapPOIObject>();
                    if (poi != null) poi.dimPOI();
                    break;
                }
                case "setPower":
                {
                    var p = pc.m_Target.TryCast<RES_Paternoster>();
                    if (p != null) p.setPower(pc.m_Arguments.m_BoolArgument);
                    break;
                }
            }
        }
    }
}
