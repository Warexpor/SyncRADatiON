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
        private bool _loggedVitalFail;
        private Vector3 _lastSentPosition;
        private float _lastSentTime;

        internal AvatarNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ResetSendState()
        {
            _lastSentPosition = Vector3.zero;
            _lastSentTime = 0f;
        }

        internal void SendLocalVital()
        {
            int hp = 100, maxHp = 100;
            byte gameState = 0, charState = 0;
            bool dead = false;
            try
            {
                hp = PlayerState.hp;
                maxHp = 100;
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
                MaxHp = maxHp,
                GameState = gameState,
                CharState = charState,
                Dead = dead
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.PlayerVital);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal PlayerStateMessage BuildPlayerStateMessage(GameObject player)
        {
            var pos = player.transform.position;
            float dt = PluginInfo.SendInterval;
            if (_lastSentTime > 0f)
                dt = Mathf.Max(0.016f, Time.unscaledTime - _lastSentTime);
            _lastSentTime = Time.unscaledTime;
            Vector3 vel = (pos - _lastSentPosition) / dt;
            // A room-to-room door moves Elster hundreds of units in one frame: that "velocity" made the
            // receiver's Hermite fling the proxy ~100 units back and forth after every door.
            if (vel.sqrMagnitude > PluginInfo.MaxProxySpeed * PluginInfo.MaxProxySpeed) vel = Vector3.zero;
            _lastSentPosition = pos;

            var apc = player.GetComponent<AlternatePlayerController>();
            var pc8 = player.GetComponent<PlayerController8>();

            float rotY;
            byte facing;
            if (apc != null)
            {
                facing = (byte)apc.facing;
                rotY = apc.fAngle;
            }
            else if (pc8 != null)
            {
                facing = (byte)pc8.facing;
                rotY = pc8.fAngle;
            }
            else
            {
                facing = 0;
                rotY = player.transform.eulerAngles.y;
            }

            float forwardAmount = 0f, turnAmount = 0f, aimingTime = -1f;
            try
            {
                var tpc = player.GetComponent<ThirdPersonCharacter>();
                if (tpc != null)
                {
                    var tpcType = typeof(ThirdPersonCharacter);
                    var fwd = tpcType.GetField("m_ForwardAmount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fwd != null) forwardAmount = (float)fwd.GetValue(tpc);
                    var trn = tpcType.GetField("m_TurnAmount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (trn != null) turnAmount = (float)trn.GetValue(tpc);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            var euler = player.transform.eulerAngles;
            byte modelState = 0;
            bool wearHat = false;
            try
            {
                var cmt = player.GetComponentInChildren<CharacterModelType>(true);
                if (cmt != null) modelState = (byte)cmt.modelState;
                wearHat = CharacterModelType.wearHat;
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            var msg = new PlayerStateMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotY = rotY,
                RootY = euler.y,
                RootX = euler.x,
                RootZ = euler.z,
                VelX = vel.x,
                VelY = vel.y,
                Forward = forwardAmount,
                Turn = turnAmount,
                AimingTime = aimingTime,
                CharState = (byte)PlayerState.charState,
                Facing = facing,
                AnimBools = 0,
                ModelState = modelState,
                WearHat = wearHat
            };
            msg.SetFacingWorld(SourceAnimReader.ReadFacingWorldRotation(player));

            SourceAnimReader.ReadFromPlayer(player, ref msg);

            if (PlayerState.aiming) msg.AnimBools |= AnimBools.Aiming;
            if (PlayerState.charState == PlayerState.charStates.run) msg.AnimBools |= AnimBools.Running;

            return msg;
        }

        /// <summary>
        /// One-shot triggers (Fire/Hurt/Die/Reload...) are edge events. The sequenced 30 Hz pose drops
        /// packets by design, so they ride a small reliable message instead and are stripped from the pose.
        /// </summary>
        internal void SendOneShot(AnimTriggers triggers)
        {
            if (triggers == AnimTriggers.None) return;
            var msg = new AvatarOneShotMessage { SenderPlayerId = _net.LocalPlayerId, Triggers = triggers };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.AvatarOneShot);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleAvatarOneShot(AvatarOneShotMessage msg, int peerId)
        {
            // Host: identity is the LiteNetLib peer map (same rule as PlayerState).
            if (_net.Role == NetworkRole.Host && peerId >= 0)
                msg.SenderPlayerId = peerId;
            int senderId = msg.SenderPlayerId;
            if (senderId == _net.LocalPlayerId) return;

            if (!_net.SceneMismatch)
            {
                var proxy = _net.ProxyManager.GetProxy(senderId);
                if (proxy != null)
                {
                    try { proxy.ApplyOneShot(msg.Triggers); }
                    catch (System.Exception ex) { ModRuntime.Log?.Warning("[Proxy] one-shot: " + ex.Message); }
                }
            }

            if (_net.Role == NetworkRole.Host)
            {
                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.AvatarOneShot);
                msg.Serialize(w);
                _net.RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
            }
        }

        internal void SendPlayerState(PlayerStateMessage msg)
        {
            if (msg.AnimTriggers != AnimTriggers.None)
            {
                SendOneShot(msg.AnimTriggers);
                msg.AnimTriggers = AnimTriggers.None;
            }
            float[] bones = msg.BoneRotations;
            msg.BoneRotations = null;
            var poseWriter = new NetDataWriter();
            poseWriter.Put((byte)NetMessageType.PlayerState);
            msg.Serialize(poseWriter);

            int mtu = SequencedMtu();
            if (bones != null && bones.Length >= 3)
            {
                int boneBytes = 4 + bones.Length * 2; // count int + ushorts
                if (poseWriter.Length + boneBytes <= mtu)
                {
                    msg.BoneRotations = bones;
                    poseWriter = new NetDataWriter();
                    poseWriter.Put((byte)NetMessageType.PlayerState);
                    msg.Serialize(poseWriter);
                    bones = null;
                }
            }

            SendSequenced(poseWriter);
            if (bones != null)
                SendBoneChunks(msg.SenderPlayerId, bones, mtu);
        }

        void SendBoneChunks(int senderId, float[] eulers, int mtu)
        {
            int totalBones = eulers.Length / 3;
            if (totalBones <= 0) return;
            int maxPayload = mtu - BonePoseHeaderBytes;
            if (maxPayload < 6) return;
            int maxBones = maxPayload / 6;
            if (maxBones < 1) maxBones = 1;

            ushort start = 0;
            while (start < totalBones)
            {
                int count = totalBones - start;
                if (count > maxBones) count = maxBones;
                var chunk = new float[count * 3];
                System.Array.Copy(eulers, start * 3, chunk, 0, chunk.Length);
                var msg = new BonePoseMessage
                {
                    SenderPlayerId = senderId,
                    TotalBones = (ushort)totalBones,
                    StartBone = start,
                    Eulers = chunk
                };
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.BonePose);
                msg.Serialize(writer);
                SendSequenced(writer);
                start += (ushort)count;
            }
        }

        int SequencedMtu()
        {
            foreach (var peer in _net.ConnectedPeers())
            {
                if (peer != null && peer.ConnectionState == ConnectionState.Connected)
                    return peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced);
            }
            return SequencedMtuFallback;
        }

        void SendSequenced(NetDataWriter writer)
        {
            if (writer.Length > SequencedMtu())
            {
                if (!_loggedOversizedPose)
                {
                    _loggedOversizedPose = true;
                    ModRuntime.Log?.Error("[Net] sequenced packet " + writer.Length + " bytes exceeds MTU — dropped");
                }
                return;
            }
            foreach (var peer in _net.ConnectedPeers())
            {
                if (peer == null || peer.ConnectionState != ConnectionState.Connected) continue;
                try { peer.Send(writer, DeliveryMethod.Sequenced); }
                catch (System.Exception ex)
                {
                    if (!_loggedOversizedPose)
                    {
                        _loggedOversizedPose = true;
                        ModRuntime.Log?.Error("[Net] sequenced send failed: " + ex.Message);
                    }
                }
            }
        }

        internal void HandlePlayerVital(PlayerVitalMessage msg, int senderId)
        {
            if (msg.SenderPlayerId != _net.LocalPlayerId)
            {
                PartyVitals.NoteVital(msg.SenderPlayerId, msg.Dead);
                var proxy = _net.ProxyManager.GetProxy(msg.SenderPlayerId);
                if (proxy != null)
                {
                    try { proxy.SetVital(msg.Dead); }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
            }

            // Host always relays (even when no local proxy yet).
            if (_net.Role == NetworkRole.Host)
            {
                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.PlayerVital);
                msg.Serialize(w);
                _net.RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
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

            // Host reads these for revive placement ("next to the nearest living teammate").
            PartyVitals.NotePos(senderId, new Vector3(state.PosX, state.PosY, state.PosZ));

            if (_net.SceneMismatch)
            {
                _net.ProxyManager.DestroyAll();
                return;
            }

            if (!_net.ProxyManager.HasProxy(senderId))
            {
                if (SceneFollowService.LocalIsTransient()) return;
                GameObject source = PlayerState.player;
                if (source == null)
                    return;
                try
                {
                    if (PlayerState.gameState != PlayerState.gameStates.play
                        && PlayerState.gameState != PlayerState.gameStates.traversing)
                    {
                        // Still allow proxy in most interactive states
                    }
                }
                catch (System.Exception e) { Guard.Swallow(e); }
                _net.ProxyManager.CreateProxy(senderId, source);
            }

            _net.ProxyManager.ApplyState(senderId, state);
            _net.NoteAvatarStateReceived();

            if (_net.Role == NetworkRole.Host)
            {
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.PlayerState);
                state.Serialize(writer);
                _net.RelayRaw(writer, DeliveryMethod.Sequenced, senderId);
            }
        }

        internal void HandleBonePose(BonePoseMessage msg, int peerId)
        {
            if (_net.Role == NetworkRole.Host && peerId >= 0)
                msg.SenderPlayerId = peerId;
            int senderId = msg.SenderPlayerId;
            if (senderId == _net.LocalPlayerId) return;
            if (_net.SceneMismatch) return;

            var proxy = _net.ProxyManager.GetProxy(senderId);
            if (proxy != null && proxy.AnimDriver != null)
                proxy.AnimDriver.ApplyBoneChunk(msg.TotalBones, msg.StartBone, msg.Eulers);

            if (_net.Role == NetworkRole.Host)
            {
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.BonePose);
                msg.Serialize(writer);
                _net.RelayRaw(writer, DeliveryMethod.Sequenced, senderId);
            }
        }
    }
}
