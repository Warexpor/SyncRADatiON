using System;
using System.Collections.Generic;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    /// <summary>
    /// SceneFollow + SceneHello send/apply (coordinates with SceneFollowService) and WorldId divergence detection:
    /// SceneHello / SceneFollow carry the sender's WorldRegistry checksum (Sync/WorldChecksum); a mismatch logs one
    /// line, the host unicasts a SceneDiff (its ids of the differing categories) and the client logs what it lacks / has extra.
    /// </summary>
    internal sealed class SceneNetHandlers
    {
        private readonly LanNetworkManager _net;

        /// <summary>Same mismatch re-sent by a peer inside this window is not logged / diffed again.</summary>
        private const float DiffResendSeconds = 10f;
        /// <summary>Ids listed per "missing:" / "extra:" log line.</summary>
        private const int MaxLoggedIds = 20;
        /// <summary>A snapshot request this recent already covers a divergence.</summary>
        private const float SnapshotCoverSeconds = 5f;

        private sealed class PeerDivergence
        {
            public string Sig = "";
            public float At;
            public string Scene = "";
            public int Ids;
        }

        private sealed class DiffAccumulator
        {
            public readonly List<ulong> Ids = new List<ulong>();
            public int Parts;
            public int Received;
            public int HostCount;
            public bool Complete => Parts > 0 && Received >= Parts;
        }

        // Host: per client, last divergence seen (dedupe + F2 status).
        private readonly Dictionary<int, PeerDivergence> _peerDivergence = new Dictionary<int, PeerDivergence>();

        // Client: host checksum last received (hello / follow) and the divergence state for one scene.
        private string _hostStatsScene = "";
        private int[] _hostCounts;
        private ulong[] _hostSums;
        private string _clientSig = "";
        private string _clientDiffScene = "";
        private int _clientDiffIds;
        private readonly Dictionary<int, DiffAccumulator> _diffParts = new Dictionary<int, DiffAccumulator>();
        private uint _diffMask;
        private string _diffScene = "";

        internal SceneNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        internal void Reset()
        {
            _peerDivergence.Clear();
            _hostStatsScene = "";
            _hostCounts = null;
            _hostSums = null;
            _clientSig = "";
            _clientDiffScene = "";
            _clientDiffIds = 0;
            _diffParts.Clear();
            _diffMask = 0;
            _diffScene = "";
        }

        internal void NotePeerGone(int playerId) => _peerDivergence.Remove(playerId);

        // ------------------------------------------------------------------ registry checksum <-> wire

        /// <summary>The cached registry checksum for <paramref name="scene"/>, or an empty array when the registry is not built for it.</summary>
        private static WorldCategoryStat[] LocalStats(string scene)
        {
            if (!WorldRegistry.HasChecksum || !string.Equals(WorldRegistry.ChecksumScene, scene, StringComparison.Ordinal))
                return Array.Empty<WorldCategoryStat>();
            int[] counts = WorldRegistry.ChecksumCounts;
            ulong[] sums = WorldRegistry.ChecksumSums;
            var stats = new WorldCategoryStat[counts.Length];
            for (int i = 0; i < stats.Length; i++)
                stats[i] = new WorldCategoryStat { Count = counts[i], Sum = sums[i] };
            return stats;
        }

        private static void Split(WorldCategoryStat[] stats, out int[] counts, out ulong[] sums)
        {
            int n = stats != null ? stats.Length : 0;
            counts = new int[n];
            sums = new ulong[n];
            for (int i = 0; i < n; i++)
            {
                counts[i] = stats[i].Count;
                sums[i] = stats[i].Sum;
            }
        }

        // ------------------------------------------------------------------ send

        internal void BroadcastSceneHello()
        {
            if (!_net.HasTransport || !_net.HandshakeComplete) return;
            string scene = SceneManager.GetActiveScene().name ?? "";
            _net.NoteLocalSceneForHello(scene);

            var msg = new SceneHelloMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                SceneName = scene,
                RoomName = WorldRegistry.GetLocalRoomName(),
                Stats = LocalStats(scene)
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.SceneHello);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
            ModRuntime.Log?.Msg("[Scene] Hello sent scene='" + msg.SceneName + "' room='" + msg.RoomName + "'");
            // Our registry is fresh after a load: compare it with the host checksum we already hold (hello just went out).
            if (_net.Role == NetworkRole.Client) EvaluateClient(true);
        }

        internal void SendSceneFollow(string sceneName, bool isRequest)
        {
            var msg = new SceneFollowMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                SceneName = sceneName ?? "",
                IsRequest = isRequest,
                // Only when the registry is already built for that scene (a follow sent at load start describes the old scene).
                Stats = isRequest ? Array.Empty<WorldCategoryStat>() : LocalStats(sceneName ?? "")
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

        // ------------------------------------------------------------------ receive

        internal void HandleSceneFollow(SceneFollowMessage msg)
        {
            SceneFollowService.HandleMessage(msg);
            if (_net.Role == NetworkRole.Client && !msg.IsRequest && msg.Stats != null && msg.Stats.Length > 0)
            {
                NoteHostStats(msg.SceneName, msg.Stats);
                EvaluateClient(false);
            }
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

            // WorldId divergence: host compares a client's checksum, a client remembers the host's and self-checks.
            if (_net.Role == NetworkRole.Host)
            {
                if (msg.SenderPlayerId >= 1) CheckPeerChecksum(msg);
            }
            else if (_net.Role == NetworkRole.Client && msg.SenderPlayerId == 0 && msg.Stats != null && msg.Stats.Length > 0)
            {
                NoteHostStats(msg.SceneName, msg.Stats);
                EvaluateClient(false);
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

        // ------------------------------------------------------------------ host: compare + SceneDiff

        private void CheckPeerChecksum(SceneHelloMessage msg)
        {
            int pid = msg.SenderPlayerId;
            if (msg.Stats == null || msg.Stats.Length == 0) return;
            string scene = SceneManager.GetActiveScene().name ?? "";
            if (scene.Length == 0 || SceneFollowService.IsTransient(scene)) return;
            // Peer is elsewhere, or our registry is not built for this scene yet: nothing comparable (the peer re-hellos).
            if (!string.Equals(msg.SceneName, scene, StringComparison.Ordinal)) return;
            if (!WorldRegistry.HasChecksum || !string.Equals(WorldRegistry.ChecksumScene, scene, StringComparison.Ordinal)) return;

            int[] hostCounts = WorldRegistry.ChecksumCounts;
            ulong[] hostSums = WorldRegistry.ChecksumSums;
            int[] peerCounts;
            ulong[] peerSums;
            Split(msg.Stats, out peerCounts, out peerSums);
            uint mask = WorldChecksum.MismatchMask(hostCounts, hostSums, peerCounts, peerSums);

            PeerDivergence state;
            _peerDivergence.TryGetValue(pid, out state);
            if (mask == 0)
            {
                if (state != null)
                {
                    _peerDivergence.Remove(pid);
                    if (state.Ids > 0 && state.Scene == scene)
                        ModRuntime.Log?.Msg("[Scene] WorldId in sync with p" + pid + " in " + scene);
                }
                return;
            }

            string sig = scene + ":" + mask + ":" + WorldChecksum.Combine(hostSums).ToString("X16")
                + ":" + WorldChecksum.Combine(peerSums).ToString("X16");
            float now = Time.unscaledTime;
            if (state != null && state.Sig == sig && now - state.At < DiffResendSeconds) return;

            if (state == null)
            {
                state = new PeerDivergence();
                _peerDivergence[pid] = state;
            }
            state.Sig = sig;
            state.At = now;
            state.Scene = scene;
            // Lower bound from the counts alone (the exact figure is only known to the client once it has the diff).
            state.Ids = WorldChecksum.EstimateDiffIds(hostCounts, peerCounts, mask);
            ModRuntime.Log?.Warning("[Scene] WorldId divergence with p" + pid + " in " + scene + ": "
                + WorldChecksum.Format(hostCounts, peerCounts, mask) + " (host/peer)");
            SendSceneDiff(pid, scene, mask);
        }

        /// <summary>Host → one client: the host's sorted WorldIds of every differing category, chunked and capped.</summary>
        private void SendSceneDiff(int pid, string scene, uint mask)
        {
            for (int cat = 0; cat < WorldChecksum.CategoryCount; cat++)
            {
                if ((mask & (1u << cat)) == 0) continue;
                List<ulong> ids = WorldRegistry.ChecksumIdsOf(cat);
                int total = ids != null ? ids.Count : 0;
                int sendCount = Math.Min(total, NetWire.MaxSceneDiffTotalIds);
                if (total > sendCount)
                    ModRuntime.Log?.Warning("[Scene] SceneDiff " + WorldChecksum.NameOf(cat) + ": " + total
                        + " ids, listing the first " + sendCount);
                int parts = WorldChecksum.ChunkCount(sendCount, NetWire.MaxSceneDiffIds);
                for (int part = 0; part < parts; part++)
                {
                    int from = part * NetWire.MaxSceneDiffIds;
                    int n = Math.Max(0, Math.Min(NetWire.MaxSceneDiffIds, sendCount - from));
                    var slice = new ulong[n];
                    for (int i = 0; i < n; i++) slice[i] = ids[from + i];
                    var diff = new SceneDiffMessage
                    {
                        SenderPlayerId = _net.LocalPlayerId,
                        SceneName = scene,
                        Category = (byte)cat,
                        CategoryMask = (byte)mask,
                        Part = (byte)part,
                        Parts = (byte)parts,
                        HostCount = total,
                        Ids = slice
                    };
                    var writer = new NetDataWriter();
                    writer.Put((byte)NetMessageType.SceneDiff);
                    diff.Serialize(writer);
                    _net.SendToPlayer(pid, writer, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        // ------------------------------------------------------------------ client: self-check + SceneDiff

        private void NoteHostStats(string scene, WorldCategoryStat[] stats)
        {
            _hostStatsScene = scene ?? "";
            Split(stats, out _hostCounts, out _hostSums);
        }

        /// <summary>
        /// Compare our registry with the host checksum for the active scene. On a new mismatch: log once, ask the host for the
        /// diff (re-hello: it carries our checksum) and request one full dump so state still lands on the ids that do match.
        /// <paramref name="helloJustSent"/>: our own hello (with the same checksum) went out a moment ago, and the load path
        /// requests the dump itself.
        /// </summary>
        private void EvaluateClient(bool helloJustSent)
        {
            if (_net.Role != NetworkRole.Client || !_net.HandshakeComplete) return;
            string scene = SceneManager.GetActiveScene().name ?? "";
            if (scene.Length == 0 || SceneFollowService.IsTransient(scene)) return;
            if (_hostCounts == null || _hostSums == null || !string.Equals(_hostStatsScene, scene, StringComparison.Ordinal)) return;
            if (!WorldRegistry.HasChecksum || !string.Equals(WorldRegistry.ChecksumScene, scene, StringComparison.Ordinal)) return;

            int[] counts = WorldRegistry.ChecksumCounts;
            ulong[] sums = WorldRegistry.ChecksumSums;
            uint mask = WorldChecksum.MismatchMask(counts, sums, _hostCounts, _hostSums);
            if (mask == 0)
            {
                if (_clientDiffScene == scene && _clientDiffIds > 0)
                    ModRuntime.Log?.Msg("[Scene] WorldId in sync with host in " + scene);
                _clientSig = "";
                _clientDiffIds = 0;
                return;
            }

            string sig = scene + ":" + mask + ":" + WorldChecksum.Combine(sums).ToString("X16")
                + ":" + WorldChecksum.Combine(_hostSums).ToString("X16");
            if (sig == _clientSig) return;
            _clientSig = sig;
            if (_clientDiffScene != scene)
            {
                _clientDiffScene = scene;
                _clientDiffIds = WorldChecksum.EstimateDiffIds(counts, _hostCounts, mask); // the diff replaces it with the exact figure
            }
            ModRuntime.Log?.Warning("[Scene] WorldId divergence with host in " + scene + ": "
                + WorldChecksum.Format(counts, _hostCounts, mask) + " (local/host)");

            if (!helloJustSent)
            {
                BroadcastSceneHello(); // carries our checksum: the host answers with a SceneDiff
                var session = _net.SessionHandlers;
                if (Time.unscaledTime - session.LastSnapshotRequestAt > SnapshotCoverSeconds)
                    session.RequestWorldSnapshot();
            }
        }

        internal void HandleSceneDiff(SceneDiffMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            string scene = SceneManager.GetActiveScene().name ?? "";
            if (!string.Equals(msg.SceneName, scene, StringComparison.Ordinal))
            {
                PlaytestLog.Verbose("Scene", "SceneDiff for '" + msg.SceneName + "' ignored (local '" + scene + "')");
                return;
            }
            int cat = msg.Category;
            if (cat >= WorldChecksum.CategoryCount || msg.Parts == 0 || msg.Part >= msg.Parts) return;

            if (_diffScene != scene)
            {
                // Scene change: drop what an older episode left behind (Part 0 restarts a category on a host re-send).
                _diffParts.Clear();
                _diffScene = scene;
            }
            DiffAccumulator acc;
            if (msg.Part == 0 || !_diffParts.TryGetValue(cat, out acc))
            {
                acc = new DiffAccumulator();
                _diffParts[cat] = acc;
            }
            acc.Ids.AddRange(msg.Ids ?? Array.Empty<ulong>());
            acc.Parts = msg.Parts;
            acc.Received++;
            acc.HostCount = msg.HostCount;
            _diffMask = msg.CategoryMask;

            for (int c = 0; c < WorldChecksum.CategoryCount; c++)
            {
                if ((_diffMask & (1u << c)) == 0) continue;
                DiffAccumulator a;
                if (!_diffParts.TryGetValue(c, out a) || !a.Complete) return;
            }
            ReportDiff(scene);
            _diffParts.Clear();
            _diffMask = 0;
        }

        private void ReportDiff(string scene)
        {
            var missingText = new StringBuilder();
            var extraText = new StringBuilder();
            int missingTotal = 0, extraTotal = 0, missingShown = 0, extraShown = 0;
            for (int cat = 0; cat < WorldChecksum.CategoryCount; cat++)
            {
                if ((_diffMask & (1u << cat)) == 0) continue;
                DiffAccumulator acc;
                if (!_diffParts.TryGetValue(cat, out acc)) continue;
                List<ulong> local = WorldRegistry.ChecksumIdsOf(cat);
                var missing = new List<ulong>();
                var extra = new List<ulong>();
                int mTotal, eTotal;
                WorldChecksum.Diff(local, acc.Ids, MaxLoggedIds, missing, extra, out mTotal, out eTotal);
                bool truncated = acc.HostCount > acc.Ids.Count;
                if (truncated)
                {
                    // The host listed only a prefix: what we "have extra" cannot be judged.
                    extra.Clear();
                    eTotal = 0;
                }
                missingTotal += mTotal;
                extraTotal += eTotal;
                string name = WorldChecksum.NameOf(cat);
                if (mTotal > 0)
                {
                    missingText.Append(' ').Append(name).Append('(').Append(mTotal).Append(truncated ? "+" : "").Append("):");
                    for (int i = 0; i < missing.Count && missingShown < MaxLoggedIds; i++, missingShown++)
                        missingText.Append(' ').Append(missing[i].ToString("X16"));
                }
                if (eTotal > 0)
                {
                    extraText.Append(' ').Append(name).Append('(').Append(eTotal).Append("):");
                    for (int i = 0; i < extra.Count && extraShown < MaxLoggedIds; i++, extraShown++)
                    {
                        string path = WorldRegistry.DescribeId(cat, extra[i]);
                        extraText.Append(' ').Append(path.Length > 0 ? path : extra[i].ToString("X16"));
                    }
                }
            }
            if (missingTotal > 0)
                ModRuntime.Log?.Warning("[Scene] missing:" + missingText + (missingTotal > missingShown ? " (+" + (missingTotal - missingShown) + " more)" : ""));
            if (extraTotal > 0)
                ModRuntime.Log?.Warning("[Scene] extra:" + extraText + (extraTotal > extraShown ? " (+" + (extraTotal - extraShown) + " more)" : ""));
            _clientDiffScene = scene;
            _clientDiffIds = missingTotal + extraTotal;
        }

        /// <summary>F2 status: "World: in sync" or "World: N ids differ" (host: lower bound from the counts, worst peer).</summary>
        internal string WorldSyncStatus()
        {
            string scene = SceneManager.GetActiveScene().name ?? "";
            int ids = 0;
            string who = "";
            if (_net.Role == NetworkRole.Client)
            {
                if (_clientDiffScene == scene) ids = _clientDiffIds;
            }
            else if (_net.Role == NetworkRole.Host)
            {
                foreach (var kvp in _peerDivergence)
                {
                    if (kvp.Value.Scene != scene || kvp.Value.Ids <= ids) continue;
                    ids = kvp.Value.Ids;
                    who = " (p" + kvp.Key + ")";
                }
            }
            return ids > 0 ? "World: " + ids + " ids differ" + who : "World: in sync";
        }
    }
}
