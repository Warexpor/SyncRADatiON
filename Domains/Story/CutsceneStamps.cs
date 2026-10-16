// Pure cutscene dedupe model (no Unity / MelonLoader references: compiled into the unit tests).
//
// One record per cutscene WorldId on each peer: the last start this peer accepted (or its own start request still
// waiting for the host) and the last skip it accepted. Two windows, one meaning each:
//   StartWindow (4 s)  - a start inside it is the same trigger arriving again (N players walking in, host native start
//                        + a client request, relay echo, join replay). A client's own request occupies the window too,
//                        so it asks once, yet the host's CutsceneStart that answers it is still accepted.
//   SkipWindow (30 s)  - this peer's own skip (skips are per player, never sent): every start path stays blocked
//                        for it, since CutsceneManager.Skip sets
//                        completed, but OnEnable reloads completed from SProgress (CutsceneManager.c), so a room
//                        chunk waking up right after a skip would otherwise let a trigger start the cutscene again.
// A cutscene that is running on this peer (CutsceneSync.StartedHere) is always a duplicate start, whatever the stamps
// say: native StartCutscene has no already-running guard and would start a second coroutine.
using System.Collections.Generic;

namespace SyncRADation.Networking
{
    internal sealed class CutsceneStamps
    {
        public const float StartWindow = 4f;
        public const float SkipWindow = 30f;

        struct Rec
        {
            public bool HasStart;
            public float StartAt;
            // The start stamp is this client's own request, not yet answered by the host's CutsceneStart.
            public bool Requested;
            public bool HasSkip;
            public float SkipAt;
        }

        readonly Dictionary<ulong, Rec> _recs = new Dictionary<ulong, Rec>();

        public void Clear() => _recs.Clear();

        static bool Within(bool has, float at, float now, float window) => has && now - at < window;

        /// <summary>Skipped inside the skip window: no start path may start it.</summary>
        public bool Skipped(ulong id, float now)
        {
            Rec r;
            return id != 0 && _recs.TryGetValue(id, out r) && Within(r.HasSkip, r.SkipAt, now, SkipWindow);
        }

        /// <summary>
        /// Accept a start (host native / host on a client request / observer replay / ending replay). False when the
        /// cutscene was skipped inside the skip window or another start was accepted inside the start window; this
        /// peer's own pending request does not count, it is what the host's answer completes. Id 0 always passes.
        /// </summary>
        public bool TryStart(ulong id, float now)
        {
            if (id == 0) return true;
            Rec r;
            _recs.TryGetValue(id, out r);
            if (Within(r.HasSkip, r.SkipAt, now, SkipWindow)) return false;
            if (!r.Requested && Within(r.HasStart, r.StartAt, now, StartWindow)) return false;
            r.HasStart = true;
            r.StartAt = now;
            r.Requested = false;
            _recs[id] = r;
            return true;
        }

        /// <summary>
        /// Client: may it ask the host to start this cutscene? False while skipped or while any start (accepted or
        /// already requested) is inside the start window. A true stamps the request.
        /// </summary>
        public bool TryRequest(ulong id, float now)
        {
            if (id == 0) return true;
            Rec r;
            _recs.TryGetValue(id, out r);
            if (Within(r.HasSkip, r.SkipAt, now, SkipWindow)) return false;
            if (Within(r.HasStart, r.StartAt, now, StartWindow)) return false;
            r.HasStart = true;
            r.StartAt = now;
            r.Requested = true;
            _recs[id] = r;
            return true;
        }

        /// <summary>Accept a skip: false for a repeat inside the skip window. Id 0 always passes.</summary>
        public bool TrySkip(ulong id, float now)
        {
            if (id == 0) return true;
            Rec r;
            _recs.TryGetValue(id, out r);
            if (Within(r.HasSkip, r.SkipAt, now, SkipWindow)) return false;
            r.HasSkip = true;
            r.SkipAt = now;
            _recs[id] = r;
            return true;
        }
    }
}
