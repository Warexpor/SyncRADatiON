// Scene owners: who runs each occupied scene's world (enemies, bosses, doors, puzzles, scene cutscenes / zones, world
// sounds). The host in its own scene; in a scene the host is not in, the lowest player id there. The host sends every
// ready peer's scene (PeerScenes) and each peer derives the owners from that one table, so all agree. Story, storage,
// the key ring, pickups, party life and saves stay the host's wherever it is.
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Sync
{
    public static class SceneOwners
    {
        // Every peer: player id -> scene ("" = loading / unknown), as the host last sent it (the host: as it last sent).
        static readonly Dictionary<int, string> _scenes = new Dictionary<int, string>();
        // Host: signature of the table last sent.
        static string _sentSig = "";
        // Every peer: this peer's scene and its owner as last acted on (handoff edges), and the cached verdict.
        static string _seenScene = "";
        static int _seenOwner = -1;
        static bool _localOwns;
        // Host: when the table was last rebuilt by the tick (events rebuild it at once).
        static float _hostTickAt;

        public static void Reset()
        {
            _scenes.Clear();
            _sentSig = "";
            _seenScene = "";
            _seenOwner = -1;
            _localOwns = false;
            _hostTickAt = 0f;
        }

        /// <summary>This client owns the world of the scene it is in (cached; Tick / table updates refresh it).</summary>
        public static bool LocalOwns => _localOwns;

        static string ActiveScene()
        {
            try { return SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception e) { Guard.Swallow(e); return ""; }
        }

        /// <summary>A scene with no world to run: loading, or a title / menu / credits scene (a player leaving or arriving).</summary>
        static bool Unowned(string scene) =>
            SceneFollowService.IsTransient(scene) || SceneFollowService.IsLeaveGame(scene) || SceneFollowService.IsMenuScene(scene);

        /// <summary>The scene a peer is in, "" when unknown or loading.</summary>
        public static string SceneOf(int playerId)
        {
            string s;
            return _scenes.TryGetValue(playerId, out s) ? s ?? "" : "";
        }

        /// <summary>Owner of a scene: the host when it is there, else the lowest id there; -1 for none / unknown.</summary>
        public static int OwnerOf(string scene)
        {
            if (Unowned(scene)) return -1;
            int best = -1;
            foreach (var kvp in _scenes)
            {
                if (!string.Equals(kvp.Value, scene, System.StringComparison.Ordinal)) continue;
                if (kvp.Key == 0) return 0;
                if (best < 0 || kvp.Key < best) best = kvp.Key;
            }
            return best;
        }

        /// <summary>Owner of the scene this player is in (-1 unknown).</summary>
        public static int OwnerOfPlayer(int playerId) => OwnerOf(SceneOf(playerId));

        /// <summary>Some player other than `me` is in this known scene.</summary>
        public static bool AnyOtherIn(string scene, int me)
        {
            if (string.IsNullOrEmpty(scene)) return false;
            foreach (var kvp in _scenes)
            {
                if (kvp.Key != me && string.Equals(kvp.Value, scene, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Two players are in the same known scene.</summary>
        public static bool SameScene(int a, int b)
        {
            string sa = SceneOf(a);
            return sa.Length > 0 && string.Equals(sa, SceneOf(b), System.StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ host

        /// <summary>
        /// Host: rebuild the table from its own scene (its pending load target while it loads, so the scene it is
        /// going to does not change hands twice) and every ready peer's hello; send it when it changed.
        /// </summary>
        internal static void HostRecompute(LanNetworkManager net)
        {
            if (net == null || !NetGate.HostRole) return;
            string mine = ActiveScene();
            if (SceneFollowService.IsTransient(mine)) mine = SceneFollowService.PendingTarget ?? "";
            _scenes.Clear();
            _scenes[0] = mine;
            foreach (int pid in net.GetRemotePlayerIds())
            {
                if (!net.HasPeer(pid)) continue;
                string s = net.PeerSceneName(pid);
                _scenes[pid] = SceneFollowService.IsTransient(s) ? "" : s;
            }
            var ids = new List<int>(_scenes.Keys);
            ids.Sort();
            var sb = new System.Text.StringBuilder();
            var scenes = new string[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                scenes[i] = _scenes[ids[i]];
                sb.Append(ids[i]).Append('=').Append(scenes[i]).Append(';');
            }
            string sig = sb.ToString();
            RefreshLocal(net);
            if (sig == _sentSig || !net.HasReadyPeers) return;
            _sentSig = sig;
            PlaytestLog.Event("Owners", "scenes " + sig);
            var msg = new PeerScenesMessage { PlayerIds = ids.ToArray(), Scenes = scenes };
            var w = new NetDataWriter();
            w.Put((byte)NetMessageType.PeerScenes);
            msg.Serialize(w);
            net.BroadcastRaw(w, DeliveryMethod.ReliableOrdered);
        }

        // ------------------------------------------------------------------ client

        internal static void HandlePeerScenes(LanNetworkManager net, PeerScenesMessage msg, int senderId)
        {
            if (net == null || NetGate.HostRole || senderId != 0) return;
            _scenes.Clear();
            int n = msg.PlayerIds != null ? msg.PlayerIds.Length : 0;
            for (int i = 0; i < n && msg.Scenes != null && i < msg.Scenes.Length; i++)
                _scenes[msg.PlayerIds[i]] = msg.Scenes[i] ?? "";
            RefreshLocal(net);
        }

        // ------------------------------------------------------------------ every peer

        /// <summary>Every frame: this peer's scene owner edge (the host also rebuilds its table once a second).</summary>
        internal static void Tick(LanNetworkManager net)
        {
            if (NetGate.HostRole && Time.unscaledTime - _hostTickAt >= 1f)
            {
                _hostTickAt = Time.unscaledTime;
                HostRecompute(net);
                return;
            }
            RefreshLocal(net);
        }

        static void RefreshLocal(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected)
            {
                _localOwns = false;
                return;
            }
            string scene = ActiveScene();
            if (Unowned(scene))
            {
                _localOwns = false;
                _seenScene = "";
                _seenOwner = -1;
                return;
            }
            int owner;
            if (NetGate.HostRole) owner = net.LocalPlayerId;
            else
            {
                // This client counts as in its active scene even before the table says so (its hello is on the way),
                // so it is never left without an owner: the host if it is here, else the lowest id of the others
                // known here and this one.
                owner = OwnerOf(scene);
                if (owner != 0 && (owner < 0 || net.LocalPlayerId < owner)) owner = net.LocalPlayerId;
            }
            _localOwns = !NetGate.HostRole && owner == net.LocalPlayerId;
            if (string.Equals(scene, _seenScene, System.StringComparison.Ordinal) && owner == _seenOwner) return;
            bool sameScene = string.Equals(scene, _seenScene, System.StringComparison.Ordinal);
            int was = _seenOwner;
            _seenScene = scene;
            _seenOwner = owner;
            if (NetGate.HostRole) return;
            OnOwnerChanged(net, scene, sameScene ? was : -1, owner);
        }

        /// <summary>
        /// This client's scene changed hands (or it just learnt who owns the scene it arrived in). Owner: its
        /// enemies run their own AI again and it sends the scene's world to the others there. Follower of a new owner:
        /// its enemies become puppets and it asks for the owner's world (the host forwards the request).
        /// </summary>
        static void OnOwnerChanged(LanNetworkManager net, string scene, int was, int owner)
        {
            bool mine = owner == net.LocalPlayerId;
            PlaytestLog.Event("Owners", "'" + scene + "' owner " + (was < 0 ? "?" : "p" + was) + " -> p" + owner
                + (mine ? " (this player runs it)" : ""));
            try
            {
                if (mine)
                {
                    net.EnemySync.UnpuppetAllNow();
                    net.BossSync.OnBecameOwner();
                    net.PuzzleSync.OnBecameOwner();
                    net.SessionHandlers.SendSceneWorldDump(-1);
                }
                else
                {
                    net.EnemySync.PuppetAllNow();
                    net.BossSync.OnBecameFollower();
                    net.PuzzleSync.OnBecameFollower();
                    // A fresh arrival already asked for the world on its scene change; a handoff in place has not.
                    if (was >= 0) net.SessionHandlers.RequestWorldSnapshot();
                }
            }
            catch (System.Exception ex) { PlaytestLog.Warn("Owners", "handoff: " + ex.Message); }
        }
    }
}
