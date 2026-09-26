using System.Collections.Generic;
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
        /// <summary>Per-joiner dump targets deferred while host is on LoadingScreen.</summary>
        private readonly HashSet<int> _pendingDumpTargets = new HashSet<int>();
        /// <summary>Broadcast dump deferred (OnSceneChanged / resync-all while transient).</summary>
        private bool _pendingDumpAll;

        internal SessionNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void Reset()
        {
            _pendingDumpTargets.Clear();
            _pendingDumpAll = false;
        }

        internal void NotePeerGone(int playerId)
        {
            if (playerId >= 1)
                _pendingDumpTargets.Remove(playerId);
            // Mid-claim WorldPickupGrant softlock: ammo/docs claimed then peer gone.
            try
            {
                int n = _net.PickupSync.ReleaseOrphanClaimsForPlayer(playerId);
                if (n > 0)
                    PlaytestLog.Event("Pickup", "peer gone orphan releases=" + n + " p" + playerId);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Pickup] orphan release: " + ex.Message);
            }
        }

        /// <summary>
        /// Remember a dump that could not send while host was transient.
        /// targetPlayerId &gt;= 0 unicasts; -1 = all peers when load finishes.
        /// </summary>
        internal void DeferDump(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;
            if (targetPlayerId >= 0)
            {
                _pendingDumpTargets.Add(targetPlayerId);
                PlaytestLog.Event("Scene", "defer dump p" + targetPlayerId);
            }
            else
            {
                _pendingDumpAll = true;
                PlaytestLog.Event("Scene", "defer dump all");
            }
        }

        /// <summary>Host tick: flush deferred dumps once active scene is non-transient.</summary>
        internal void TickPendingDumps()
        {
            if (_net.Role != NetworkRole.Host || !_net.HandshakeComplete) return;
            if (!_pendingDumpAll && _pendingDumpTargets.Count == 0) return;
            if (SceneFollowService.LocalIsTransient()) return;

            bool all = _pendingDumpAll;
            int[] targets = null;
            if (!all && _pendingDumpTargets.Count > 0)
            {
                targets = new int[_pendingDumpTargets.Count];
                _pendingDumpTargets.CopyTo(targets);
            }
            // Clear before send so a nested Tick cannot double-flush; re-Defer on failure.
            _pendingDumpAll = false;
            _pendingDumpTargets.Clear();

            if (all)
            {
                PlaytestLog.Event("Scene", "flush deferred dump all");
                try { SendFullWorldSnapshot(-1); }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.Warning("[Scene] deferred dump all failed: " + ex.Message);
                    DeferDump(-1);
                }
                return;
            }

            if (targets == null) return;
            for (int i = 0; i < targets.Length; i++)
            {
                int pid = targets[i];
                if (!_net.HasPeer(pid)) continue;
                PlaytestLog.Event("Scene", "flush deferred dump p" + pid);
                try { SendFullWorldSnapshot(pid); }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.Warning("[Scene] deferred dump p" + pid + " failed: " + ex.Message);
                    DeferDump(pid);
                }
            }
        }

        /// <summary>Host: dump full world. targetPlayerId &gt;= 0 unicasts (join / client resync); -1 = all peers.</summary>
        internal void SendFullWorldSnapshot(int targetPlayerId = -1)
        {
            if (_net.Role != NetworkRole.Host || !_net.HandshakeComplete) return;
            if (SceneFollowService.LocalIsTransient())
            {
                DeferDump(targetPlayerId);
                return;
            }
            // Successful send clears matching pending entries for this scope.
            if (targetPlayerId < 0)
            {
                _pendingDumpAll = false;
                _pendingDumpTargets.Clear();
            }
            else
                _pendingDumpTargets.Remove(targetPlayerId);

            ModRuntime.Log?.Msg("[Network] Sending full world snapshot"
                + (targetPlayerId >= 0 ? " to player " + targetPlayerId : " to all peers"));
            int prevUnicast = _net.BeginUnicast(targetPlayerId);
            try
            {
                WorldRegistry.Rebuild();
                DoorSyncService.ForceFullSend();
                _net.PuzzleSync.ForceFullSend();
                _net.PuzzleSync.Tick(_net);
                _net.PickupSync.RequestFullSend();
                _net.PickupSync.TickHost(_net);
                EntitySpawner.DumpLiveSpawns(_net);
                _net.EnemySync.RequestFullSend();
                _net.EnemySync.TickHost(_net);
                _net.BossSync.RequestFullSend();
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
