// Per-peer downed flag + last known position/room. Host reads it for revive + wipe;
// every peer reads it so enemies/bosses/friendly-fire skip downed players.
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
            public float DownAt;
            public float ReviveSentAt = -99f;
            /// <summary>Revived, but no "alive" vital seen yet: the peer may still be loading / not have applied it.</summary>
            public bool AwaitingAck;
            public bool HasPos;
            public Vector3 Pos;
            public string Room = "";
        }

        /// <summary>A stale "dead" vital still in flight must not re-down a peer we just revived.</summary>
        private const float ReviveGrace = 1.5f;
        /// <summary>
        /// A revived peer keeps reporting dead=true until it applies the revive (it defers while loading a scene).
        /// Ignore "dead" from it until it reports alive once, but never longer than this.
        /// </summary>
        private const float AckTimeout = 45f; // longer than a slow (cold-disk Proton) scene load

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
            var net = ModRuntime.Network;
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

        public static float DownAt(int playerId)
        {
            Entry e;
            return _entries.TryGetValue(playerId, out e) ? e.DownAt : 0f;
        }

        public static void NoteVital(int playerId, bool dead)
        {
            if (playerId < 0 || IsLocal(playerId)) return;
            var e = Get(playerId);
            if (!dead && e.AwaitingAck)
                e.AwaitingAck = false;
            if (dead)
            {
                if (!e.Down && !IsAwaitingAck(e) && Time.unscaledTime - e.ReviveSentAt >= ReviveGrace)
                {
                    e.Down = true;
                    e.DownAt = Time.unscaledTime;
                    PlaytestLog.Event("Damage", "peer " + playerId + " downed (vital)");
                }
            }
            else if (e.Down)
            {
                e.Down = false;
                PlaytestLog.Event("Damage", "peer " + playerId + " up (vital)");
            }
        }

        /// <summary>
        /// DeathPolicy(ClientDowned): authoritative, unlike a "dead" vital. It is sent once, at the moment of death,
        /// ReliableOrdered on the Events channel ahead of that death's vitals; the host only revives a peer it has
        /// already seen down, so this message always lands before the revive it could be confused with. One that
        /// arrives after a revive is a new death. Dropping it (ack window / grace) left the peer downed for good when
        /// no periodic vitals repeat it (SyncPlayerVitals off).
        /// </summary>
        public static void NoteDown(int playerId)
        {
            if (playerId < 0 || IsLocal(playerId)) return;
            var e = Get(playerId);
            e.AwaitingAck = false;
            if (e.Down) return;
            e.Down = true;
            e.DownAt = Time.unscaledTime;
            PlaytestLog.Event("Damage", "peer " + playerId + " downed");
        }

        /// <summary>Host marks the revive; clients mark it when the PartyLife message lands.</summary>
        public static void NoteRevived(int playerId)
        {
            if (playerId < 0 || IsLocal(playerId)) return;
            var e = Get(playerId);
            e.Down = false;
            e.ReviveSentAt = Time.unscaledTime;
            e.AwaitingAck = true;
        }

        private static bool IsAwaitingAck(Entry e)
        {
            if (!e.AwaitingAck) return false;
            if (Time.unscaledTime - e.ReviveSentAt < AckTimeout) return true;
            e.AwaitingAck = false; // peer never acked: trust its vitals again
            return false;
        }

        /// <summary>Party wipe: everyone is alive again; ignore stale "dead" vitals still in flight.</summary>
        public static void ReviveAll(LanNetworkManager net)
        {
            if (net == null) return;
            foreach (int pid in net.GetRemotePlayerIds())
                NoteRevived(pid);
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
            Get(playerId).Room = room ?? "";
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

        /// <summary>True when at least one OTHER session player is not downed.</summary>
        public static bool AnyOtherAlive(LanNetworkManager net)
        {
            if (net == null) return false;
            foreach (int pid in net.GetRemotePlayerIds())
            {
                if (!IsDown(pid)) return true;
            }
            return false;
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
