using System;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    /// <summary>SceneFollow + SceneHello send/apply (coordinates with SceneFollowService).</summary>
    internal sealed class SceneNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal SceneNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        internal void BroadcastSceneHello()
        {
            if (!_net.HasTransport || !_net.HandshakeComplete) return;
            string scene = SceneManager.GetActiveScene().name ?? "";
            _net.NoteLocalSceneForHello(scene);

            var msg = new SceneHelloMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                SceneName = scene,
                RoomName = WorldRegistry.GetLocalRoomName()
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.SceneHello);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
            ModRuntime.Log?.Msg("[Scene] Hello sent scene='" + msg.SceneName + "' room='" + msg.RoomName + "'");
        }

        internal void SendSceneFollow(string sceneName, bool isRequest)
        {
            var msg = new SceneFollowMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                SceneName = sceneName ?? "",
                IsRequest = isRequest
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.SceneFollow);
            msg.Serialize(writer);
            if (isRequest)
            {
                if (_net.TryGetPeer(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
            else
                _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleSceneFollow(SceneFollowMessage msg)
        {
            SceneFollowService.HandleMessage(msg);
        }

        internal void HandleSceneHello(SceneHelloMessage msg)
        {
            if (SceneFollowService.IsTransient(msg.SceneName))
            {
                PlaytestLog.Verbose("Scene", "peer " + msg.SenderPlayerId + " still loading");
                return;
            }

            _net.NotePeerScene(msg.SenderPlayerId, msg.SceneName ?? "");
            if (msg.SenderPlayerId == 0 || _net.Role == NetworkRole.Client)
            {
                if (msg.SenderPlayerId == 0)
                    _net.SetHostSceneName(msg.SceneName ?? "");
            }

            string localScene = SceneManager.GetActiveScene().name ?? "";
            _net.SetLocalSceneName(localScene);
            string compareTo = !string.IsNullOrEmpty(_net.HostSceneName) ? _net.HostSceneName : msg.SceneName;
            bool hostTransient = SceneFollowService.IsTransient(compareTo);
            bool localTransient = SceneFollowService.IsTransient(localScene);
            bool mismatch = !hostTransient && !localTransient
                && !string.IsNullOrEmpty(compareTo)
                && !string.IsNullOrEmpty(localScene)
                && !string.Equals(compareTo, localScene, StringComparison.Ordinal);
            _net.SetSceneMismatch(mismatch);

            if (_net.Role == NetworkRole.Client && !hostTransient && !string.IsNullOrEmpty(compareTo)
                && (mismatch || localTransient))
            {
                if (mismatch && AirlockCinematic.ShouldIgnoreHostFollow(compareTo))
                {
                    _net.ProxyManager.DestroyAll();
                    PlaytestLog.Event("Scene", "defer airlock follow host='" + compareTo
                        + "' local='" + localScene + "'");
                    return;
                }
                if (mismatch)
                {
                    _net.SetStatusText("Following host scene '" + compareTo + "'");
                    ModRuntime.Log?.Warning("[Scene] MISMATCH local='" + localScene + "' host='" + compareTo
                        + "' — following host");
                }
                SceneFollowService.Apply(compareTo);
            }
            else if (!mismatch)
            {
                ModRuntime.Log?.Msg("[Scene] Peer " + msg.SenderPlayerId + " scene='" + msg.SceneName
                    + "' room='" + msg.RoomName + "' OK");
            }
        }
    }
}
