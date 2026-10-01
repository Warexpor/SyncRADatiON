using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Cheats;
using SyncRADation.Sync;
using UnityEngine;
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
        /// <summary>Per-peer rate limit: one snapshot per peer per interval; extra requests coalesce into one later dump.</summary>
        private const float MinDumpIntervalSeconds = 2f;
        private const int MaxDumpFailures = 5;
        private readonly Dictionary<int, float> _lastDumpAt = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _rateDeferred = new Dictionary<int, float>();
        private readonly Dictionary<int, int> _dumpFailures = new Dictionary<int, int>();
        private readonly List<int> _dueScratch = new List<int>(4);

        internal SessionNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void Reset()
        {
            _pendingDumpTargets.Clear();
            _pendingDumpAll = false;
            _lastDumpAt.Clear();
            _rateDeferred.Clear();
            _dumpFailures.Clear();
            LastSnapshotRequestAt = -999f;
        }

        internal void NotePeerGone(int playerId)
        {
            if (playerId >= 1)
            {
                _pendingDumpTargets.Remove(playerId);
                _lastDumpAt.Remove(playerId);
                _rateDeferred.Remove(playerId);
                _dumpFailures.Remove(playerId);
            }
            // Recycled player id + restarted index counter must not collide with its old floor drops.
            try { _net.DroppedItemHandlers.RehomeDropsOf(playerId); }
            catch (System.Exception ex) { Guard.Swallow(ex); }
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
            FlushRateDeferred();
            if (!_pendingDumpAll && _pendingDumpTargets.Count == 0) return;
            if (SceneFollowService.LocalIsTransient()) return;
            // A wipe reload has not reset the host's world yet: a dump now would carry the pre-wipe puzzle memory,
            // floor drops and claims. HostReload.OnLoadFinished / OnSceneArrived (and Abort) queue the post-reset one.
            if (HostReload.Pending) return;

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

        /// <summary>Re-queue requests that arrived inside the rate-limit window once it elapses.</summary>
        void FlushRateDeferred()
        {
            if (_rateDeferred.Count == 0) return;
            float now = Time.unscaledTime;
            _dueScratch.Clear();
            foreach (var kvp in _rateDeferred)
            {
                if (now >= kvp.Value)
                    _dueScratch.Add(kvp.Key);
            }
            for (int i = 0; i < _dueScratch.Count; i++)
            {
                int pid = _dueScratch[i];
                _rateDeferred.Remove(pid);
                if (_net.HasPeer(pid))
                    SendFullWorldSnapshot(pid);
            }
        }

        /// <summary>Host: dump full world. targetPlayerId &gt;= 0 unicasts (join / client resync); -1 = all peers.</summary>
        internal void SendFullWorldSnapshot(int targetPlayerId = -1)
        {
            if (_net.Role != NetworkRole.Host || !_net.HandshakeComplete) return;
            if (SceneFollowService.LocalIsTransient() || HostReload.Pending)
            {
                DeferDump(targetPlayerId);
                return;
            }
            if (targetPlayerId < 0 && !_net.HasReadyPeers) return;
            if (targetPlayerId >= 0 && !_net.HasPeer(targetPlayerId)) return;
            try
            {
                SendFullWorldSnapshotCore(targetPlayerId);
                NoteDumpSent(targetPlayerId);
            }
            catch (System.Exception ex)
            {
                // A throwing domain must not wedge the whole dump / tick: retry with backoff, give up after a few tries.
                int key = targetPlayerId;
                int fails;
                _dumpFailures.TryGetValue(key, out fails);
                fails++;
                _dumpFailures[key] = fails;
                if (fails >= MaxDumpFailures)
                {
                    _dumpFailures.Remove(key);
                    ModRuntime.Log?.Error("[Network] full world snapshot (" + (targetPlayerId >= 0 ? "p" + targetPlayerId : "all")
                        + ") failed " + fails + "x, giving up: " + ex);
                    return;
                }
                ModRuntime.Log?.Warning("[Network] full world snapshot (" + (targetPlayerId >= 0 ? "p" + targetPlayerId : "all")
                    + ") failed (" + fails + "/" + MaxDumpFailures + "), retry: " + ex.Message);
                if (targetPlayerId >= 0)
                    _rateDeferred[targetPlayerId] = Time.unscaledTime + fails;
                else
                    DeferDump(-1);
            }
        }

        void NoteDumpSent(int targetPlayerId)
        {
            float now = Time.unscaledTime;
            if (targetPlayerId >= 0)
            {
                _lastDumpAt[targetPlayerId] = now;
                _dumpFailures.Remove(targetPlayerId);
                return;
            }
            _dumpFailures.Remove(-1);
            foreach (int pid in _net.GetRemotePlayerIds())
                _lastDumpAt[pid] = now;
        }

        void SendFullWorldSnapshotCore(int targetPlayerId)
        {
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
                WorldRegistry.RebuildIfStale();
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
                // Reset clears the change signature: an unchanged box must still reach a late joiner.
                _net.StorageSync.Reset();
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

        /// <summary>Client: unscaled time of the last snapshot request (-999 = none). Lets the WorldId divergence path skip a redundant one.</summary>
        internal float LastSnapshotRequestAt { get; private set; } = -999f;

        internal void RequestWorldSnapshot()
        {
            if (_net.Role != NetworkRole.Client || !_net.HandshakeComplete) return;
            LastSnapshotRequestAt = Time.unscaledTime;
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
            // Only the requester gets the dump; the wire SenderPlayerId is ignored.
            int target = senderId;
            if (target < 1 || !_net.HasPeer(target)) return;

            float now = Time.unscaledTime;
            float last;
            if (_lastDumpAt.TryGetValue(target, out last) && now - last < MinDumpIntervalSeconds)
            {
                // Coalesce: one deferred dump at the end of the window instead of a dump per request.
                if (!_rateDeferred.ContainsKey(target))
                    PlaytestLog.Event("Network", "snapshot request from p" + target + " rate-limited");
                _rateDeferred[target] = last + MinDumpIntervalSeconds;
                return;
            }
            ModRuntime.Log?.Msg("[Network] Snapshot requested by player " + target);
            SendFullWorldSnapshot(target);
        }
    }
}
