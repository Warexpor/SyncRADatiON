// Scene world traffic for a scene a client owns (Sync/SceneOwners). The topology is a star, so it goes through the host:
//   - the owner's broadcasts reach the host as ordinary messages; the host sends them on to the peers in that scene
//     (TryHostRoute, owner fan-out) instead of reading them itself;
//   - a follower's requests (door, puzzle, hit, zone, cutscene...) reach the host as ordinary messages; the host
//     forwards them to the owner in a WorldRelay (ToOwner) and the owner handles them as the host would;
//   - the owner's sends to one player, or to everyone but one, go to the host as a WorldRelay (ToPlayer / ToScene).
// The host's own scene-world broadcasts only go to the peers in its scene, so a peer only ever gets the world of the
// scene it is in from that scene's owner.
using System;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Sync;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    public sealed partial class LanNetworkManager
    {
        // Depth of a forwarded follower request being handled here (the owner stamps its sender like the host does).
        private int _relayDepth;

        /// <summary>Dispatch stamps the sender id from the peer map: on the host, and on an owner handling a forwarded request.</summary>
        bool StampsSender => _role == NetworkRole.Host || _relayDepth > 0;

        /// <summary>Host: the scene a peer last announced ("" unknown).</summary>
        internal string PeerSceneName(int playerId)
        {
            string s;
            return _peerScenes.TryGetValue(playerId, out s) ? s ?? "" : "";
        }

        /// <summary>That player is in this peer's scene (host: unknown counts as yes, as PeerInHostScene).</summary>
        internal bool PeerInMyScene(int playerId)
        {
            if (_role == NetworkRole.Host) return PeerInHostScene(playerId);
            string theirs = SceneOwners.SceneOf(playerId);
            if (theirs.Length == 0) return false;
            string mine = SceneManager.GetActiveScene().name ?? "";
            return string.Equals(theirs, mine, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ message classes

        /// <summary>A message that describes one scene's world (the owner sends it to that scene only).</summary>
        static bool IsSceneWorld(byte[] data, int offset, int length)
        {
            if (data == null || length < 1) return false;
            switch ((NetMessageType)data[offset])
            {
            case NetMessageType.DoorState:
            case NetMessageType.EnemyState:
            case NetMessageType.EnemySpawn:
            case NetMessageType.PuzzleState:
            case NetMessageType.BossState:
            case NetMessageType.BossHit:
            case NetMessageType.FmodEmitter:
                return true;
            case NetMessageType.StoryPresentation:
                // WorldId (8) after the type byte, then the command.
                return length > 9 && IsSceneCmd((StoryCmd)data[offset + 9]);
            default:
                return false;
            }
        }

        /// <summary>Presentations that belong to the scene they ran in (the ending and the story requests are the host's).</summary>
        internal static bool IsSceneCmd(StoryCmd cmd)
        {
            switch (cmd)
            {
            case StoryCmd.CutsceneStart:
            case StoryCmd.EventZoneFire:
            case StoryCmd.MultiConditionFire:
            case StoryCmd.PartyCheat:
            case StoryCmd.GoToPenny:
                return true;
            default:
                return false;
            }
        }

        /// <summary>A follower's request its scene's owner answers.</summary>
        static bool IsSceneRequest(byte[] data, int offset, int length)
        {
            if (data == null || length < 1) return false;
            switch ((NetMessageType)data[offset])
            {
            case NetMessageType.DoorState:
            case NetMessageType.EnemyDamage:
            case NetMessageType.EnemyAction:
            case NetMessageType.EnemySpawn:
            case NetMessageType.BossHit:
            case NetMessageType.PuzzleState:
            case NetMessageType.FmodEmitterRequest:
                return true;
            case NetMessageType.InteractionRequest:
            {
                // SenderPlayerId (4) + WorldId (8) after the type byte, then Kind, then Int0.
                if (length < 18) return false;
                var kind = (InteractionKind)data[offset + 13];
                return InteractionSyncService.IsSceneRequest(kind, BitConverter.ToInt32(data, offset + 14));
            }
            default:
                return false;
            }
        }

        // ------------------------------------------------------------------ host: route before dispatch

        /// <summary>
        /// Host, before its own dispatch: a client owner's scene broadcast goes on to the peers in its scene, a
        /// follower's request goes to its client owner. True when the message was routed (not read here).
        /// </summary>
        bool TryHostRoute(NetPacketReader reader, NetMessageType type, int senderId, byte channel, DeliveryMethod method)
        {
            if (_role != NetworkRole.Host || senderId < 1) return false;
            byte[] data = reader.RawData;
            int offset = reader.UserDataOffset;
            int length = reader.UserDataSize;
            if (data == null || length < 1) return false;
            int owner = SceneOwners.OwnerOfPlayer(senderId);
            if (owner < 1) return false; // the host's scene (or unknown): the host reads it
            if (owner == senderId)
            {
                if (!IsSceneWorld(data, offset, length)) return false;
                FanOut(senderId, -1, data, offset, length, channel, method);
                return true;
            }
            if (!IsSceneRequest(data, offset, length)) return false;
            var payload = new byte[length];
            Buffer.BlockCopy(data, offset, payload, 0, length);
            SendRelayTo(owner, new WorldRelayMessage
            {
                Mode = WorldRelayMessage.ToOwner,
                From = senderId,
                Target = owner,
                State = (byte)(channel == NetChannels.State ? 1 : 0),
                Payload = payload
            });
            return true;
        }

        /// <summary>Host: one owner's message to every ready peer in its scene but the owner and exceptId.</summary>
        void FanOut(int ownerId, int exceptId, byte[] data, int offset, int length, byte channel, DeliveryMethod method)
        {
            var w = new NetDataWriter();
            w.Put(data, offset, length);
            foreach (var kvp in _peers)
            {
                int pid = kvp.Key;
                if (pid == ownerId || pid == exceptId || pid == _localPlayerId) continue;
                if (!_readyPeers.Contains(pid) || !SceneOwners.SameScene(pid, ownerId)) continue;
                SendPeer(kvp.Value, w, method, channel);
            }
        }

        /// <summary>Host: hand a message to a scene's client owner as if `from` had sent it to the host.</summary>
        internal void ForwardToOwner(int owner, int from, NetDataWriter payload)
        {
            if (_role != NetworkRole.Host) return;
            SendRelayTo(owner, new WorldRelayMessage
            {
                Mode = WorldRelayMessage.ToOwner,
                From = from,
                Target = owner,
                State = 0,
                Payload = payload.CopyData()
            });
        }

        void SendRelayTo(int playerId, WorldRelayMessage msg)
        {
            NetPeer peer;
            if (!_readyPeers.Contains(playerId) || !_peers.TryGetValue(playerId, out peer)) return;
            var w = new NetDataWriter();
            w.Put((byte)NetMessageType.WorldRelay);
            msg.Serialize(w);
            if (msg.State == 1) SendPeer(peer, w, DeliveryMethod.Sequenced, NetChannels.State);
            else SendPeer(peer, w, DeliveryMethod.ReliableOrdered, NetChannels.Events);
        }

        // ------------------------------------------------------------------ WorldRelay receive

        internal void HandleWorldRelay(WorldRelayMessage msg, int senderId)
        {
            byte[] p = msg.Payload;
            if (p == null || p.Length == 0) return;
            if (_role == NetworkRole.Host)
            {
                // Only the owner of a scene the host is not in sends these, for that scene: its world, and to one
                // player also the hit an enemy landed on them and the answer to their request.
                var inner = (NetMessageType)p[0];
                bool allowed = IsSceneWorld(p, 0, p.Length) || (msg.Mode == WorldRelayMessage.ToPlayer
                    && (inner == NetMessageType.EnemyDamage || inner == NetMessageType.InteractionAck));
                if (senderId < 1 || SceneOwners.OwnerOfPlayer(senderId) != senderId || !allowed)
                {
                    if (_loggedGateDrops.Add("relay:" + senderId + ":" + msg.Mode))
                        ModRuntime.Log?.Warning("[Network] dropped WorldRelay mode " + msg.Mode + " from player " + senderId
                            + " (not the owner of its scene, or not scene world)");
                    return;
                }
                byte ch = msg.State == 1 ? NetChannels.State : NetChannels.Events;
                var dm = msg.State == 1 ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered;
                if (msg.Mode == WorldRelayMessage.ToScene)
                    FanOut(senderId, msg.Target, p, 0, p.Length, ch, dm);
                else if (msg.Mode == WorldRelayMessage.ToPlayer && msg.Target >= 1 && msg.Target != senderId
                         && SceneOwners.SameScene(msg.Target, senderId))
                {
                    NetPeer peer;
                    if (_readyPeers.Contains(msg.Target) && _peers.TryGetValue(msg.Target, out peer))
                    {
                        var w = new NetDataWriter();
                        w.Put(p, 0, p.Length);
                        SendPeer(peer, w, dm, ch);
                    }
                }
                return;
            }
            // Client: the host forwards a follower's request to this owner.
            if (senderId != 0 || msg.Mode != WorldRelayMessage.ToOwner || !SceneOwners.LocalOwns) return;
            if (!IsSceneRequest(p, 0, p.Length) && (NetMessageType)p[0] != NetMessageType.SnapshotRequest) return;
            var reader = new NetDataReader(p);
            var type = (NetMessageType)reader.GetByte();
            _relayDepth++;
            try
            {
                Sync.WireTrace.Rx((byte)type, reader, msg.From);
                if (!(TryDispatchSession(type, reader, msg.From)
                    || TryDispatchPlayers(type, reader, msg.From)
                    || TryDispatchWorld(type, reader, msg.From)
                    || TryDispatchStoryAudio(type, reader, msg.From)))
                    ModRuntime.Log?.Warning("[Network] relayed type " + type + " unhandled");
            }
            finally { _relayDepth--; }
        }

        // ------------------------------------------------------------------ client owner: targeted sends

        /// <summary>Client owner → host: a WorldRelay the host sends on (ToPlayer: target; ToScene: all there but target).</summary>
        void SendRelayViaHost(byte mode, int target, NetDataWriter payload, bool state)
        {
            NetPeer host;
            if (!_peers.TryGetValue(0, out host)) return;
            var data = payload.CopyData();
            var msg = new WorldRelayMessage
            {
                Mode = mode,
                From = _localPlayerId,
                Target = target,
                State = (byte)(state ? 1 : 0),
                Payload = data
            };
            var w = new NetDataWriter();
            w.Put((byte)NetMessageType.WorldRelay);
            msg.Serialize(w);
            if (state) SendPeer(host, w, DeliveryMethod.Sequenced, NetChannels.State);
            else SendPeer(host, w, DeliveryMethod.ReliableOrdered, NetChannels.Events);
        }

        /// <summary>This client owns its scene and the payload is that scene's business to send to someone else.</summary>
        bool ClientOwnerSend => _role == NetworkRole.Client && SceneOwners.LocalOwns;

        /// <summary>
        /// A client owner alone in its scene: its scene world has nobody to go to (a follower arriving asks for the
        /// whole world, SessionNetHandlers.SendSceneWorldDump), so it is not sent to the host at all.
        /// </summary>
        bool ClientOwnerAlone(NetDataWriter writer) =>
            ClientOwnerSend && IsSceneWorld(writer.Data, 0, writer.Length)
            && !SceneOwners.AnyOtherIn(SceneManager.GetActiveScene().name ?? "", _localPlayerId);
    }
}
