using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Config;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>Avatar pose / bones / vitals send + apply.</summary>
    internal sealed class AvatarNetHandlers
    {
        private readonly LanNetworkManager _net;

        private const int SequencedMtuFallback = 1020;
        private const int BonePoseHeaderBytes = 1 + 4 + 2 + 2 + 2; // type + sender + total + start + count
        private bool _loggedOversizedPose;
        private bool _loggedSendFail;
        private bool _loggedRelayFail;
        private bool _loggedVitalFail;
        private Vector3 _lastSentPosition;
        private float _lastSentTime;
        // Edge cues read with the last built pose; SendPlayerState sends them reliably ahead of it.
        private AvatarCue _pendingCues;

        // Per-send reuse: LiteNetLib copies the payload inside Send, so one writer serves every packet of a frame.
        private readonly NetDataWriter _poseWriter = new NetDataWriter();
        private readonly NetDataWriter _eventWriter = new NetDataWriter();
        private readonly NetDataWriter _relayWriter = new NetDataWriter();

        internal AvatarNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ResetSendState()
        {
            _lastSentPosition = Vector3.zero;
            _lastSentTime = 0f;
            _pendingCues = AvatarCue.None;
        }

        internal void SendLocalVital()
        {
            int hp = 100;
            byte gameState = 0, charState = 0;
            bool dead = false;
            try
            {
                hp = PlayerState.hp;
                gameState = (byte)PlayerState.gameState;
                charState = (byte)PlayerState.charState;
                dead = PlayerState.charState == PlayerState.charStates.dead || hp <= 0;
            }
            catch (System.Exception ex)
            {
                if (!_loggedVitalFail)
                {
                    _loggedVitalFail = true;
                    ModRuntime.Log?.Warning("[Net] vital read failed: " + ex.Message);
                }
            }
            // Co-op: our own downed flag is authoritative (native hp regen / charState writes must not flip it).
            if (NetworkDamageSystem.PartyLive)
                dead = NetworkDamageSystem.IsDead;

            var msg = new PlayerVitalMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                Hp = hp,
                MaxHp = 100,
                GameState = gameState,
                CharState = charState,
                Dead = dead
            };
            _eventWriter.Reset();
            _eventWriter.Put((byte)NetMessageType.PlayerVital);
            msg.Serialize(_eventWriter);
            _net.BroadcastRaw(_eventWriter, DeliveryMethod.ReliableOrdered);
        }

        internal PlayerStateMessage BuildPlayerStateMessage(GameObject player)
        {
            var pos = player.transform.position;
            float now = Time.unscaledTime;
            float dt = _lastSentTime > 0f ? Mathf.Max(0.016f, now - _lastSentTime) : PluginInfo.SendInterval;
            _lastSentTime = now;
            Vector3 vel = (pos - _lastSentPosition) / dt;
            // A room-to-room door moves Elster hundreds of units in one frame: that "velocity" made the
            // receiver's Hermite fling the proxy ~100 units back and forth after every door.
            if (vel.sqrMagnitude > PluginInfo.MaxProxySpeed * PluginInfo.MaxProxySpeed) vel = Vector3.zero;
            _lastSentPosition = pos;

            var msg = new PlayerStateMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                VelX = vel.x,
                VelY = vel.y
            };
            _pendingCues |= SourceAnimReader.Read(player, ref msg);
            return msg;
        }

        /// <summary>
        /// Edge cues (shot / reload / hurt): the sequenced 30 Hz pose drops packets by design, so they ride a small
        /// reliable message instead.
        /// </summary>
        private void SendOneShot(AvatarCue cues)
        {
            var msg = new AvatarOneShotMessage { SenderPlayerId = _net.LocalPlayerId, Cues = cues };
            _eventWriter.Reset();
            _eventWriter.Put((byte)NetMessageType.AvatarOneShot);
            msg.Serialize(_eventWriter);
            _net.BroadcastRaw(_eventWriter, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleAvatarOneShot(AvatarOneShotMessage msg, int peerId)
        {
            // Host: identity is the LiteNetLib peer map (same rule as PlayerState).
            if (_net.Role == NetworkRole.Host && peerId >= 0)
                msg.SenderPlayerId = peerId;
            int senderId = msg.SenderPlayerId;
            if (senderId == _net.LocalPlayerId) return;

            if (_net.Role == NetworkRole.Host)
            {
                _relayWriter.Reset();
                _relayWriter.Put((byte)NetMessageType.AvatarOneShot);
                msg.Serialize(_relayWriter);
                RelayToSenderScene(_relayWriter, DeliveryMethod.ReliableOrdered, senderId);
            }

            if (SenderElsewhere(senderId)) return;
            _net.ProxyManager.GetProxy(senderId)?.ApplyOneShot(msg.Cues);
        }

        internal void SendPlayerState(PlayerStateMessage msg)
        {
            if (_pendingCues != AvatarCue.None)
            {
                SendOneShot(_pendingCues);
                _pendingCues = AvatarCue.None;
            }
            // msg.BoneRotations is SourceAnimReader's reused buffer: everything below serializes it synchronously.
            int mtu = SequencedMtu();
            float[] bones = msg.BoneRotations;
            _poseWriter.Reset();
            _poseWriter.Put((byte)NetMessageType.PlayerState);
            msg.Serialize(_poseWriter);
            if (_poseWriter.Length > mtu && bones != null && bones.Length >= 3)
            {
                // Rare: bones do not fit inline. Pose alone, bones as BonePose chunks right after it.
                msg.BoneRotations = null;
                _poseWriter.Reset();
                _poseWriter.Put((byte)NetMessageType.PlayerState);
                msg.Serialize(_poseWriter);
                SendSequenced(_poseWriter, mtu);
                SendBoneChunks(msg.SenderPlayerId, bones, mtu);
                return;
            }
            SendSequenced(_poseWriter, mtu);
        }

        void SendBoneChunks(int senderId, float[] eulers, int mtu)
        {
            int totalBones = eulers.Length / 3;
            int maxBones = (mtu - BonePoseHeaderBytes) / 6;
            if (maxBones < 1) return;
            for (int start = 0; start < totalBones; start += maxBones)
            {
                int count = System.Math.Min(maxBones, totalBones - start);
                _poseWriter.Reset();
                _poseWriter.Put((byte)NetMessageType.BonePose);
                BonePoseMessage.Write(_poseWriter, senderId, (ushort)totalBones, (ushort)start, eulers, start, count);
                SendSequenced(_poseWriter, mtu);
            }
        }

        /// <summary>Smallest single-packet Sequenced size across connected peers (each send goes to all of them).</summary>
        int SequencedMtu()
        {
            int mtu = int.MaxValue;
            foreach (var peer in _net.ConnectedPeers())
            {
                if (peer == null || peer.ConnectionState != ConnectionState.Connected) continue;
                int m = peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced);
                if (m < mtu) mtu = m;
            }
            return mtu == int.MaxValue ? SequencedMtuFallback : mtu;
        }

        void SendSequenced(NetDataWriter writer, int mtu)
        {
            if (writer.Length > mtu)
            {
                if (!_loggedOversizedPose)
                {
                    _loggedOversizedPose = true;
                    ModRuntime.Log?.Error("[Net] sequenced packet " + writer.Length + " bytes exceeds MTU " + mtu + " — dropped");
                }
                return;
            }
            foreach (var peer in _net.ConnectedPeers())
            {
                if (peer == null || peer.ConnectionState != ConnectionState.Connected) continue;
                try { peer.Send(writer, DeliveryMethod.Sequenced); }
                catch (System.Exception ex)
                {
                    if (!_loggedSendFail)
                    {
                        _loggedSendFail = true;
                        ModRuntime.Log?.Error("[Net] sequenced send failed: " + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// The sender is known to be in another scene than this peer (wreck / hole split, a peer mid-follow). Unknown
        /// or loading scenes count as "same": the pose is applied as before.
        /// </summary>
        bool SenderElsewhere(int senderId)
        {
            return ScenesDiffer(_net.SceneOf(senderId), _net.LocalSceneName);
        }

        static bool ScenesDiffer(string a, string b)
        {
            if (SceneFollowService.IsTransient(a) || SceneFollowService.IsTransient(b)) return false;
            return !string.Equals(a, b, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Host: forward a peer's avatar traffic to every other ready peer that is not known to be in another scene
        /// (clients only learn the host's scene, so the host filters for them). Sent on the same channel the
        /// sender used (Events), before any local early-out: a host that is loading or has no player still relays.
        /// </summary>
        void RelayToSenderScene(NetDataWriter writer, DeliveryMethod method, int senderId)
        {
            string from = _net.SceneOf(senderId);
            var ids = _net.GetRemotePlayerIds();
            for (int i = 0; i < ids.Length; i++)
            {
                int pid = ids[i];
                if (pid == senderId) continue;
                if (ScenesDiffer(from, _net.SceneOf(pid))) continue;
                if (!_net.TryGetPeer(pid, out var peer) || peer.ConnectionState != ConnectionState.Connected) continue;
                try { peer.Send(writer, method); }
                catch (System.Exception ex)
                {
                    if (!_loggedRelayFail)
                    {
                        _loggedRelayFail = true;
                        ModRuntime.Log?.Error("[Net] avatar relay (" + method + ") failed: " + ex.Message);
                    }
                }
            }
        }

        internal void HandlePlayerVital(PlayerVitalMessage msg, int senderId)
        {
            // Host always relays (even when no local proxy yet, any scene): party vitals are scene-independent.
            if (_net.Role == NetworkRole.Host)
            {
                _relayWriter.Reset();
                _relayWriter.Put((byte)NetMessageType.PlayerVital);
                msg.Serialize(_relayWriter);
                _net.RelayRaw(_relayWriter, DeliveryMethod.ReliableOrdered, senderId);
            }

            if (msg.SenderPlayerId != _net.LocalPlayerId)
            {
                PartyVitals.NoteVital(msg.SenderPlayerId, msg.Dead);
                _net.ProxyManager.GetProxy(msg.SenderPlayerId)?.SetVital(msg.Dead);
            }
        }

        internal void HandlePlayerState(PlayerStateMessage state, int peerId)
        {
            // Host: identity is the LiteNetLib peer map, not the wire field.
            // Client: all packets come from peer 0 (host); keep the stamped SenderPlayerId.
            if (_net.Role == NetworkRole.Host && peerId >= 0)
                state.SenderPlayerId = peerId;
            int senderId = state.SenderPlayerId;
            if (senderId == _net.LocalPlayerId) return;

            // Relay first: nothing about the host's own state (loading, no player, other scene) may stop it.
            if (_net.Role == NetworkRole.Host)
            {
                _relayWriter.Reset();
                _relayWriter.Put((byte)NetMessageType.PlayerState);
                state.Serialize(_relayWriter);
                RelayToSenderScene(_relayWriter, DeliveryMethod.Sequenced, senderId);
            }

            // Host reads these for revive placement ("next to the nearest living teammate").
            PartyVitals.NotePos(senderId, new Vector3(state.PosX, state.PosY, state.PosZ));

            // Per sender: a peer in another scene has no body here (its coordinates belong to that scene).
            if (SenderElsewhere(senderId))
            {
                if (_net.ProxyManager.HasProxy(senderId))
                {
                    PlaytestLog.Event("Proxy", "p" + senderId + " in '" + _net.SceneOf(senderId)
                        + "', here '" + _net.LocalSceneName + "' - proxy removed");
                    _net.ProxyManager.DestroyProxy(senderId);
                }
                return;
            }

            if (!_net.ProxyManager.HasProxy(senderId))
            {
                if (SceneFollowService.LocalIsTransient()) return;
                GameObject source = PlayerState.player;
                if (source == null)
                    return;
                _net.ProxyManager.CreateProxy(senderId, source);
            }

            _net.ProxyManager.ApplyState(senderId, state);
            _net.NoteAvatarStateReceived();
        }

        internal void HandleBonePose(BonePoseMessage msg, int peerId)
        {
            if (_net.Role == NetworkRole.Host && peerId >= 0)
                msg.SenderPlayerId = peerId;
            int senderId = msg.SenderPlayerId;
            if (senderId == _net.LocalPlayerId) return;

            if (_net.Role == NetworkRole.Host)
            {
                _relayWriter.Reset();
                _relayWriter.Put((byte)NetMessageType.BonePose);
                msg.Serialize(_relayWriter);
                RelayToSenderScene(_relayWriter, DeliveryMethod.Sequenced, senderId);
            }

            if (SenderElsewhere(senderId)) return;
            _net.ProxyManager.GetProxy(senderId)?.Pose.OnBoneChunk(msg.TotalBones, msg.StartBone, msg.Eulers);
        }
    }
}
