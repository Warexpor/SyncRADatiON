// Per-peer dead flag + last known position/room. Every peer reads it so enemies / bosses / friendly fire skip a player
// that died and is reloading (NetworkDamageSystem / DeathReload).
using System.Collections.Generic;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class PartyVitals
    {
        private sealed class Entry
        {
            public bool Down;
            public bool HasPos;
            public Vector3 Pos;
            public string Room = "";
        }

        private static readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private static readonly List<int> _scratch = new List<int>(8);

        private static Entry Get(int playerId)
        {
            Entry e;
            if (!_entries.TryGetValue(playerId, out e))
            {
                e = new Entry();
                _entries[playerId] = e;
            }
            return e;
        }

        private static bool IsLocal(int playerId)
        {
            var net = LanNetworkManager.Instance;
            return net != null && playerId == net.LocalPlayerId;
        }

        public static bool IsDown(int playerId)
        {
            if (playerId < 0) return false;
            if (IsLocal(playerId)) return NetworkDamageSystem.IsDead;
            Entry e;
            return _entries.TryGetValue(playerId, out e) && e.Down;
        }

        /// <summary>Downed check for a world transform (enemy/boss target picking).</summary>
        public static bool IsProxyDown(int playerId, RemotePlayerProxy proxy)
        {
            if (IsDown(playerId)) return true;
            return proxy != null && proxy.LastDead;
        }

        public static void NoteVital(int playerId, bool dead)
        {
            if (playerId < 0 || IsLocal(playerId)) return;
            var e = Get(playerId);
            if (dead == e.Down) return;
            e.Down = dead;
            PlaytestLog.Event("Damage", "peer " + playerId + (dead ? " dead (vital)" : " back (vital)"));
        }

        /// <summary>
        /// DeathPolicy(ClientDowned): sent once at the moment of death, ReliableOrdered on the Events channel, so a peer
        /// counts as dead even with periodic vitals off (SyncPlayerVitals). Its first "alive" vital after the reload
        /// clears it.
        /// </summary>
        public static void NoteDown(int playerId)
        {
            if (playerId < 0 || IsLocal(playerId)) return;
            var e = Get(playerId);
            if (e.Down) return;
            e.Down = true;
            PlaytestLog.Event("Damage", "peer " + playerId + " died");
        }

        public static void NotePos(int playerId, Vector3 pos)
        {
            if (playerId < 0 || IsLocal(playerId)) return;
            var e = Get(playerId);
            e.HasPos = true;
            e.Pos = pos;
        }

        public static void NoteRoom(int playerId, string room)
        {
            if (playerId < 0 || IsLocal(playerId)) return;
            var e = Get(playerId);
            room = room ?? "";
            if (e.Room != room)
                PlaytestLog.Event("Room", "p" + playerId + " '" + e.Room + "' -> '" + room + "'");
            e.Room = room;
        }

        public static bool TryGetPos(int playerId, out Vector3 pos)
        {
            Entry e;
            if (_entries.TryGetValue(playerId, out e) && e.HasPos)
            {
                pos = e.Pos;
                return true;
            }
            pos = Vector3.zero;
            return false;
        }

        public static string RoomOf(int playerId)
        {
            Entry e;
            return _entries.TryGetValue(playerId, out e) ? e.Room : "";
        }

        /// <summary>Drop entries for peers that left the session.</summary>
        public static void Prune(LanNetworkManager net)
        {
            if (net == null || _entries.Count == 0) return;
            _scratch.Clear();
            foreach (var kvp in _entries)
            {
                bool present = false;
                foreach (int pid in net.GetRemotePlayerIds())
                {
                    if (pid == kvp.Key) { present = true; break; }
                }
                if (!present) _scratch.Add(kvp.Key);
            }
            for (int i = 0; i < _scratch.Count; i++)
                _entries.Remove(_scratch[i]);
        }

        public static void Reset()
        {
            _entries.Clear();
        }
    }
}
