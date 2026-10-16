// Host: a room's enemies run while a remote player is in it, as they do for the host's own room.
using System;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Native rule (Ghidra EnemyManager.c): a room's EnemyManager switches its enemies on in Enter(room), which the game
    /// calls only when the local Elster enters that room, and its CheckIfLeft coroutine puts them back to sleep
    /// (ResetEnemy + SetActive(false)) as soon as PlayerState.currentRoom is another room. The host simulates every
    /// enemy, so a room only a client stood in never had live enemies on the host: the client's puppets stood still
    /// and every client hit was dropped ("wake REFUSED ... disabled by the level", test pilot DET_Detention Kitchen).
    /// Here the host runs that same Enter for a room a remote player is in (and the host is not), with that player as
    /// the native reference player, and EnemyManagerPeerPatches keeps CheckIfLeft from sleeping the room while one is
    /// still there. EnemyManager.inCombat / enemyPresence are the host listener's own and stay as they were.
    /// A manager of the host's own room that comes alive after the host walked in gets the entry it missed
    /// (EnterHostRoom).
    /// Managers that own a Kolibri get the same Enter: it switches the room's enemies and the Kolibri on as the host's
    /// The room itself is held too: native Room.LeaveRoom switches the chunk (floor, walls, colliders) off 2 s after the
    /// host walks out (Room.c delayedDisabling), under the enemy a client is still fighting there, so it walked
    /// through walls and CheckIfLeft's "Enemy is floating, resetting!" teleported it home over and over
    /// (RoomDelayedDisablingPatch keeps it, Tick releases it once no player is left).
    /// own entry would, and the Kolibri's state then rides the host's boss sync (BossSyncService). Skipping them left
    /// every other enemy of such a room asleep on the host, so a client's hits there all missed (test pilot
    /// LAB_Labyrinth Loop Hall STCR: "client hit miss" x12). ARAR nests get the same treatment (ActivateNests).
    /// </summary>
    public static class PeerRoomEnemies
    {
        const float Interval = 0.5f;
        // Next check (unscaled time). Cleared by SessionReset "PeerRoomEnemies" (scene / session).
        static float _nextAt;

        public static void Reset()
        {
            _nextAt = 0f;
            _held.Clear();
            _hostWasIn.Clear();
        }

        // ------------------------------------------------------------------ room chunks kept on for a remote player

        private sealed class Held
        {
            public Room Room;
            public float EmptySince;
        }
        // Rooms (instance id, never sent) whose chunk the mod keeps or switched on for a remote player while the host
        // is elsewhere. Cleared by Reset.
        static readonly System.Collections.Generic.Dictionary<int, Held> _held = new System.Collections.Generic.Dictionary<int, Held>();
        static readonly System.Collections.Generic.List<int> _heldScratch = new System.Collections.Generic.List<int>(4);
        /// <summary>Native delayedDisabling waits this long after the player left.</summary>
        const float ReleaseDelay = 2f;

        /// <summary>The mod owns this room's chunk now: a remote player is in it and the host is not.</summary>
        public static void Hold(Room room)
        {
            if (room == null) return;
            int key = room.GetInstanceID();
            Held h;
            if (_held.TryGetValue(key, out h)) h.EmptySince = 0f;
            else _held[key] = new Held { Room = room };
        }

        /// <summary>Switch a held room's chunk off the native way once nobody has been in it for the native delay.</summary>
        static void ReleaseEmpty(Room hostRoom, PlayerProxyManager pm, int[] remote, float now)
        {
            if (_held.Count == 0) return;
            _heldScratch.Clear();
            foreach (var kvp in _held)
            {
                var h = kvp.Value;
                try
                {
                    // The host's own room (again): native entry / leave owns the chunk from here.
                    if (h.Room == null || h.Room == hostRoom || h.Room.containsElster)
                    {
                        _heldScratch.Add(kvp.Key);
                        continue;
                    }
                    if (PeerIn(h.Room, pm, remote) != null)
                    {
                        h.EmptySince = 0f;
                        continue;
                    }
                    if (h.EmptySince <= 0f)
                    {
                        h.EmptySince = now;
                        continue;
                    }
                    if (now - h.EmptySince < ReleaseDelay) continue;
                    _heldScratch.Add(kvp.Key);
                    if (h.Room.chunk != null && h.Room.chunk.activeSelf)
                    {
                        NetGate.BeginApply();
                        try { h.Room.SetChunkStatus(false); }
                        finally { NetGate.EndApply(); }
                    }
                }
                catch (Exception e)
                {
                    Guard.Swallow("PeerRoomEnemies.Release", e);
                    _heldScratch.Add(kvp.Key);
                }
            }
            for (int i = 0; i < _heldScratch.Count; i++) _held.Remove(_heldScratch[i]);
        }

        // ------------------------------------------------------------------ the host listener's combat statics

        // Managers (instance id) whose last CheckIfLeft step ran with the host in the room. Cleared by Reset.
        static readonly System.Collections.Generic.HashSet<int> _hostWasIn = new System.Collections.Generic.HashSet<int>();

        /// <summary>CheckIfLeft step with the host in this manager's room: native writes inCombat / enemyPresence itself.</summary>
        public static void NoteHostIn(EnemyManager m)
        {
            if (m != null) _hostWasIn.Add(m.GetInstanceID());
        }

        /// <summary>
        /// The host is not in this manager's room. True once, on the first step after it left: native CheckIfLeft would
        /// clear inCombat / enemyPresence there ("Sleeping Enemies."), but the room stays live for the remote player
        /// and PeerView hands the statics back as they were, so the host kept the combat music of a fight it walked
        /// away from (LOV West Corridor, into the Library cutscene).
        /// </summary>
        public static bool TakeHostLeft(EnemyManager m) => m != null && _hostWasIn.Remove(m.GetInstanceID());

        /// <summary>Host tick (EnemySyncService.TickHost): enter every manager whose room holds a remote player.</summary>
        public static void Tick(LanNetworkManager net, PlayerProxyManager pm, int[] remote)
        {
            if (!NetGate.Host || pm == null || remote == null || remote.Length == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextAt) return;
            _nextAt = now + Interval;
            var managers = WorldLookup.All<EnemyManager>();
            if (managers == null) return;
            Room hostRoom = null;
            try { hostRoom = PlayerState.currentRoom; } catch (Exception e) { Guard.Swallow(e); }
            for (int i = 0; i < managers.Length; i++)
            {
                var m = managers[i];
                try
                {
                    // A switched-off manager is story-gated: native Enter would only fail its CheckIfLeft coroutine
                    // ("Coroutine couldn't be started ... 'Enemy Manager' is inactive", test pilot LOV / DET).
                    if (m == null || m.inOperation || !m.gameObject.activeInHierarchy) continue;
                    Room room = m.room;
                    if (room == null) continue;
                    if (room == hostRoom)
                    {
                        EnterHostRoom(m, room);
                        continue;
                    }
                    GameObject peer = PeerIn(room, pm, remote);
                    if (peer == null) continue;
                    Enter(m, room, peer);
                }
                catch (Exception e) { Guard.Swallow("PeerRoomEnemies.Tick", e); }
            }
            ActivateNests(hostRoom, pm, remote);
            ReleaseEmpty(hostRoom, pm, remote, now);
        }

        /// <summary>
        /// AraNest.Activate(room) is the nest's PlayerState.EnteredRoomEvent handler: it marks the nest activated (and
        /// re-Loads a sleeping ARAR) when the local player walks into its room. A remote player's entry never raises
        /// it on the host, so the host runs it for a nest whose room holds one (AraNestPeerPatches keeps it activated
        /// while one is there).
        /// </summary>
        static void ActivateNests(Room hostRoom, PlayerProxyManager pm, int[] remote)
        {
            var nests = WorldLookup.All<AraNest>();
            if (nests == null) return;
            for (int i = 0; i < nests.Length; i++)
            {
                var n = nests[i];
                try
                {
                    if (n == null || n.dead || n.activated || !n.isActiveAndEnabled) continue;
                    Room room = n.room;
                    if (room == null || room == hostRoom) continue;
                    GameObject peer = PeerIn(room, pm, remote);
                    if (peer == null) continue;
                    using (new PeerView(room, peer))
                        n.Activate(room);
                    PlaytestLog.Event("Enemy", "peer room " + room.roomName + ": AraNest " + n.gameObject.name + " activated for the peer");
                }
                catch (Exception e) { Guard.Swallow("PeerRoomEnemies.Nests", e); }
            }
        }

        /// <summary>The proxy root of a remote, non-downed player whose reported room is this one, or null.</summary>
        public static GameObject PeerIn(Room room, PlayerProxyManager pm, int[] remote)
        {
            string name = room != null ? room.roomName ?? "" : "";
            if (name.Length == 0 || pm == null || remote == null) return null;
            for (int i = 0; i < remote.Length; i++)
            {
                int pid = remote[i];
                var proxy = pm.GetProxy(pid);
                if (proxy == null || proxy.GameObject == null || PartyVitals.IsProxyDown(pid, proxy)) continue;
                if (PartyVitals.RoomOf(pid) == name) return proxy.GameObject;
            }
            return null;
        }

        /// <summary>
        /// The host's own room, with a manager that is not in operation: the story switched it on while the host was
        /// already standing there (LOV West Corridor: a client took the pistol in the Overlook Office, which arms the
        /// corridor's EULR). Native Enter only runs from the room entry, which solo always comes after such an event,
        /// so the manager never started CheckIfLeft: it is what sets EnemyManager.inCombat / enemyPresence, and the
        /// host fought beside the client with no combat music and no presence static. Run the entry it missed.
        /// </summary>
        static void EnterHostRoom(EnemyManager m, Room room)
        {
            m.Enter(room);
            PlaytestLog.Event("Enemy", "host room " + room.roomName + ": late EnemyManager.Enter (in operation="
                + m.inOperation + ")");
        }

        static void Enter(EnemyManager m, Room room, GameObject peer)
        {
            // The enemies sit outside the room chunk (Room/Enemy Manager/<enemy>), but the room geometry they walk on is
            // the chunk: wake it the native way first (not a local room entry: no puzzle re-apply sweep).
            if (room.chunk != null && !room.chunk.activeSelf)
            {
                NetGate.BeginApply();
                try { room.SetChunkStatus(true); }
                finally { NetGate.EndApply(); }
            }
            Hold(room);
            using (new PeerView(room, peer))
                m.Enter(room);
            int on = 0;
            var list = m.enemies;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].gameObject.activeInHierarchy) on++;
            PlaytestLog.Event("Enemy", "peer room " + room.roomName + ": " + on + " enemies on (EnemyManager.Enter for the peer)");
        }

        /// <summary>
        /// The native player references an EnemyManager reads (PlayerState.player for its spacing / breathing room,
        /// PlayerState.currentRoom for "still in the room") point at the peer for one call; the host's combat statics
        /// come back as they were. Dispose restores everything.
        /// </summary>
        public struct PeerView : IDisposable
        {
            readonly GameObject _player;
            readonly Room _room;
            readonly bool _inCombat;
            readonly bool _presence;
            readonly bool _active;
            readonly bool _hostLeft;
            readonly bool _swapped;

            /// <summary>No player swap: only the host's combat statics come back as they were.</summary>
            public static PeerView StaticsOnly() => new PeerView(true);

            PeerView(bool staticsOnly)
            {
                _hostLeft = false;
                _swapped = false;
                _player = null;
                _room = null;
                _inCombat = EnemyManager.inCombat;
                _presence = EnemyManager.enemyPresence;
                _active = true;
            }

            /// <param name="hostLeft">The host just walked out of this room: its combat statics end cleared (TakeHostLeft).</param>
            public PeerView(Room room, GameObject peer, bool hostLeft = false)
            {
                _hostLeft = hostLeft;
                _swapped = true;
                _player = PlayerState.player;
                _room = PlayerState.currentRoom;
                _inCombat = EnemyManager.inCombat;
                _presence = EnemyManager.enemyPresence;
                _active = true;
                PlayerState.player = peer;
                PlayerState.currentRoom = room;
            }

            public void Dispose()
            {
                if (!_active) return;
                try
                {
                    if (_swapped)
                    {
                        PlayerState.player = _player;
                        PlayerState.currentRoom = _room;
                    }
                    EnemyManager.inCombat = !_hostLeft && _inCombat;
                    EnemyManager.enemyPresence = !_hostLeft && _presence;
                }
                catch (Exception e) { Guard.Swallow(e); }
            }
        }
    }
}
