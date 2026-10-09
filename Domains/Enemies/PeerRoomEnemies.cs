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
    /// Managers that own a Kolibri get the same Enter: it switches the room's enemies and the Kolibri on as the host's
    /// own entry would, and the Kolibri's state then rides the host's boss sync (BossSyncService). Skipping them left
    /// every other enemy of such a room asleep on the host, so a client's hits there all missed (test pilot
    /// LAB_Labyrinth Loop Hall STCR: "client hit miss" x12). ARAR nests get the same treatment (ActivateNests).
    /// </summary>
    public static class PeerRoomEnemies
    {
        const float Interval = 0.5f;
        // Next check (unscaled time). Cleared by SessionReset "PeerRoomEnemies" (scene / session).
        static float _nextAt;

        public static void Reset() => _nextAt = 0f;

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
                    if (room == null || room == hostRoom) continue;
                    GameObject peer = PeerIn(room, pm, remote);
                    if (peer == null) continue;
                    Enter(m, room, peer);
                }
                catch (Exception e) { Guard.Swallow("PeerRoomEnemies.Tick", e); }
            }
            ActivateNests(hostRoom, pm, remote);
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

            public PeerView(Room room, GameObject peer)
            {
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
                    PlayerState.player = _player;
                    PlayerState.currentRoom = _room;
                    EnemyManager.inCombat = _inCombat;
                    EnemyManager.enemyPresence = _presence;
                }
                catch (Exception e) { Guard.Swallow(e); }
            }
        }
    }
}
