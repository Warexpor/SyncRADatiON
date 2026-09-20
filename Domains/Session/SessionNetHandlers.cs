using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Cheats;
using SyncRADation.Sync;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    /// <summary>Join/resync snapshot request + full world dump (preserves _unicastPlayerId).</summary>
    internal sealed class SessionNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal SessionNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>Host: dump full world. targetPlayerId &gt;= 0 unicasts (join / client resync); -1 = all peers.</summary>
        internal void SendFullWorldSnapshot(int targetPlayerId = -1)
        {
            if (_net.Role != NetworkRole.Host || !_net.HandshakeComplete) return;
            if (SceneFollowService.LocalIsTransient())
            {
                PlaytestLog.Event("Scene", "skip dump (loading)");
                return;
            }
            ModRuntime.Log?.Msg("[Network] Sending full world snapshot"
                + (targetPlayerId >= 0 ? " to player " + targetPlayerId : " to all peers"));
            int prevUnicast = _net.BeginUnicast(targetPlayerId);
            try
            {
                WorldRegistry.Rebuild();
                DoorSyncService.ForceFullSend();
                _net.PuzzleSync.RequestFullSend();
                _net.PuzzleSync.Tick(_net);
                _net.PickupSync.RequestFullSend();
                _net.PickupSync.TickHost(_net);
                EntitySpawner.DumpLiveSpawns(_net);
                _net.EnemySync.RequestFullSend();
                _net.EnemySync.TickHost(_net);
                _net.BossSync.TickHost(_net);
                _net.StorySync.RequestFullSend();
                _net.StorySync.Send(_net, true, replayPresentation: true);
                _net.StorageSync.RequestSend();
                _net.StorageSync.SendNow(_net);
                PartyKeyRing.Broadcast();
                _net.DroppedItemHandlers.DumpDroppedItems();
                FmodEmitterSync.DumpPlaying();
                if (!SceneFollowService.IsTransient(SceneManager.GetActiveScene().name ?? ""))
                    _net.SendSceneFollow(SceneManager.GetActiveScene().name ?? "", false);
                _net.BroadcastSceneHello();
            }
            finally
            {
                _net.EndUnicast(prevUnicast);
            }
        }

        internal void RequestWorldSnapshot()
        {
            if (_net.Role != NetworkRole.Client || !_net.HandshakeComplete) return;
            var msg = new SnapshotRequestMessage { SenderPlayerId = _net.LocalPlayerId };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.SnapshotRequest);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
            ModRuntime.Log?.Msg("[Network] Snapshot request sent to host");
        }

        internal void HandleSnapshotRequest(SnapshotRequestMessage req, int senderId)
        {
            if (_net.Role != NetworkRole.Host) return;
            int target = req.SenderPlayerId;
            if (target < 1 || !_net.HasPeer(target))
                target = senderId;
            ModRuntime.Log?.Msg("[Network] Snapshot requested by player " + target);
            SendFullWorldSnapshot(target);
        }
    }
}
