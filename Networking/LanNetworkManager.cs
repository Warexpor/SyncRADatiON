// SyncRADation � LiteNetLib host/client, N-peer management, message routing+relay, state building
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Config;
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
using SyncRADation.Players;
using SyncRADation.Sync;
// NetworkDamageSystem lives in Players
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    public sealed partial class LanNetworkManager : INetEventListener
    {
        // persistent: the process-lifetime manager (created once by ModRuntime.EnsureRunning)
        public static LanNetworkManager Instance { get; private set; }

        private NetManager _net;
        private NetworkRole _role = NetworkRole.Offline;
        private int _localPlayerId;
        private int _unicastPlayerId = -1; // >=0: join/resync dump goes only to this peer
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>(); // playerId -> peer
        private readonly Dictionary<NetPeer, int> _peerToId = new Dictionary<NetPeer, int>(); // peer -> playerId
        private readonly HashSet<int> _sessionPlayerIds = new HashSet<int>(); // host+clients; clients fill from roster
        private readonly PlayerProxyManager _proxyManager = new PlayerProxyManager();
        private readonly EnemySyncService _enemySync = new EnemySyncService();
        private readonly PuzzleSyncService _puzzleSync = new PuzzleSyncService();
        private readonly BossSyncService _bossSync = new BossSyncService();
        private readonly WorldPickupSyncService _pickupSync = new WorldPickupSyncService();
        private readonly StorySyncService _storySync = new StorySyncService();
        private readonly StorageBoxSyncService _storageSync = new StorageBoxSyncService();
        private GameObject _localPlayer;
        private float _sendTimer;
        private float _vitalTimer;
        private float _lastStateTime;

        private bool _handshakeComplete;
        private readonly Dictionary<int, string> _peerScenes = new Dictionary<int, string>();
        private string _hostSceneName = "";
        private string _localSceneName = "";
        private bool _sceneMismatch;
        private readonly List<int> _stalePeerIds = new List<int>(8);
        /// <summary>Handshaken player ids (host: clients that passed Handshake; client: {0} once host handshake is OK).</summary>
        private readonly HashSet<int> _readyPeers = new HashSet<int>();
        private readonly Dictionary<int, float> _peerConnectedAt = new Dictionary<int, float>();
        private readonly List<int> _timedOutPeers = new List<int>(4);
        private readonly Dictionary<string, int> _tickFailures = new Dictionary<string, int>();
        private readonly Dictionary<byte, int> _dispatchFailures = new Dictionary<byte, int>();
        private readonly HashSet<string> _loggedGateDrops = new HashSet<string>();
        private int _lastAssignedId; // round-robin cursor for AllocClientId (0 = none yet)
        private float _connectStartedAt;
        private bool _stopPending;
        private string _stopReason = "";
        private const float ConnectTimeoutSeconds = 12f;
        private const float HandshakeTimeoutSeconds = 10f;
        private const float VitalInterval = 0.2f;

        public NetworkRole Role => _role;
        public bool IsConnected => _handshakeComplete && (_role == NetworkRole.Host || _peers.Count > 0);
        /// <summary>True when at least one remote peer finished the handshake (host: a client; client: the host).</summary>
        public bool HasReadyPeers => _handshakeComplete && _readyPeers.Count > 0;
        public int LocalPlayerId => _localPlayerId;
        public string StatusText { get; private set; } = "Offline";
        public bool SceneMismatch => _sceneMismatch;
        public string HostSceneName => _hostSceneName;
        public PlayerProxyManager ProxyManager => _proxyManager;
        public EnemySyncService EnemySync => _enemySync;
        public PuzzleSyncService PuzzleSync => _puzzleSync;
        public BossSyncService BossSync => _bossSync;
        public WorldPickupSyncService PickupSync => _pickupSync;
        public StorySyncService StorySync => _storySync;
        public StorageBoxSyncService StorageSync => _storageSync;
        public GameObject GetLocalPlayer() => _localPlayer;
        public void SetLocalPlayer(GameObject go) { _localPlayer = go; }

        public LanNetworkManager()
        {
            Instance = this;
            ConstructDomainHandlers();
        }

        private NetPeer[] _connectedPeersCache = Array.Empty<NetPeer>();
        private bool _connectedPeersDirty = true;

        /// <summary>
        /// Transport: handshaken peers for sequenced avatar sends. A cached array (rebuilt only when a peer connects,
        /// handshakes, leaves or the session stops): called at 30 Hz per send, so no iterator / list garbage.
        /// Safe to iterate while the roster mutates (a rebuild swaps in a new array).
        /// </summary>
        internal NetPeer[] ConnectedPeers()
        {
            if (_connectedPeersDirty)
            {
                var list = new List<NetPeer>(_readyPeers.Count);
                foreach (var kvp in _peers)
                {
                    if (_readyPeers.Contains(kvp.Key))
                        list.Add(kvp.Value);
                }
                _connectedPeersCache = list.ToArray();
                _connectedPeersDirty = false;
            }
            return _connectedPeersCache;
        }

        /// <summary>Peer for a handshaken player id (pre-handshake peers are never returned).</summary>
        internal bool TryGetPeer(int playerId, out NetPeer peer)
        {
            peer = null;
            return _readyPeers.Contains(playerId) && _peers.TryGetValue(playerId, out peer);
        }

        internal bool HasPeer(int playerId) => _readyPeers.Contains(playerId) && _peers.ContainsKey(playerId);

        /// <summary>Session ids (host + clients), ascending. For UI.</summary>
        public List<int> GetSessionPlayerIdsSorted()
        {
            var ids = new List<int>(_sessionPlayerIds);
            ids.Sort();
            return ids;
        }

        /// <summary>Round-trip ms to a remote peer (0 when unknown).</summary>
        public int GetPeerPing(int playerId)
        {
            NetPeer peer;
            return _peers.TryGetValue(playerId, out peer) && peer != null ? peer.Ping : 0;
        }

        /// <summary>Bytes available in one Sequenced packet (continuous state chunking). Fallback when no peer.</summary>
        internal int StatePacketBudget()
        {
            foreach (var peer in ConnectedPeers())
            {
                if (peer != null && peer.ConnectionState == ConnectionState.Connected)
                    return peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced) - 8;
            }
            return 1000;
        }

        internal bool HandshakeComplete => _handshakeComplete;

        internal bool HasTransport => _net != null;

        /// <summary>
        /// True inside a join/resync dump scope (BeginUnicast): every Broadcast* goes to one peer only. A full-send path
        /// that records "already sent" state must not record it here, or the other clients never get those changes.
        /// </summary>
        public bool UnicastActive => _unicastPlayerId >= 0;

        /// <summary>Join/resync dump scope. Returns previous unicast id for restore in finally.</summary>
        internal int BeginUnicast(int targetPlayerId)
        {
            int prev = _unicastPlayerId;
            _unicastPlayerId = targetPlayerId;
            return prev;
        }

        internal void EndUnicast(int previousUnicastPlayerId) => _unicastPlayerId = previousUnicastPlayerId;

        internal void NotePeerScene(int playerId, string scene) => _peerScenes[playerId] = scene ?? "";

        /// <summary>Host: peer reported the same active scene as the host (unknown/transient = assume yes).</summary>
        internal bool PeerInHostScene(int playerId)
        {
            string theirs;
            if (!_peerScenes.TryGetValue(playerId, out theirs) || string.IsNullOrEmpty(theirs)) return true;
            string mine = SceneManager.GetActiveScene().name ?? "";
            return string.IsNullOrEmpty(mine) || string.Equals(theirs, mine, StringComparison.Ordinal);
        }

        internal void NoteLocalSceneForHello(string scene)
        {
            _localSceneName = scene ?? "";
            if (_role == NetworkRole.Host)
                _hostSceneName = _localSceneName;
        }

        internal void SetHostSceneName(string scene) => _hostSceneName = scene ?? "";

        internal void SetLocalSceneName(string scene) => _localSceneName = scene ?? "";
        /// <summary>This peer's scene as last announced (per-sender proxy scene check).</summary>
        public string LocalSceneName => _localSceneName;

        internal void SetSceneMismatch(bool mismatch) => _sceneMismatch = mismatch;

        internal void SetStatusText(string text) => StatusText = text ?? "";

        internal void NoteAvatarStateReceived() => _lastStateTime = Time.unscaledTime;

        /// <summary>Two channels (events 0 / continuous state 1). AutoRecycle: handlers only read inside the callback.</summary>
        private NetManager NewNetManager()
        {
            return new NetManager(this)
            {
                UnconnectedMessagesEnabled = true,
                DisconnectTimeout = 5000,
                ChannelsCount = NetChannels.Count,
                AutoRecycle = true
            };
        }

        private void AbortNetwork(string status)
        {
            StopNetwork();
            StatusText = status;
            ModRuntime.Log?.Warning("[Network] " + status);
        }

        public void StartHost(int port)
        {
            // StopNetwork already ran every SessionReset clear (a second RunAll here was pure duplicate work).
            StopNetwork();
            if (port < 1 || port > 65535)
            {
                StatusText = "Invalid port " + port;
                ModRuntime.Log?.Warning("[Network] " + StatusText);
                return;
            }
            try
            {
                _role = NetworkRole.Host;
                _localPlayerId = 0;
                _net = NewNetManager();
                if (!_net.Start(port))
                {
                    _net = null;
                    _role = NetworkRole.Offline;
                    StatusText = "Failed to bind port " + port;
                    ModRuntime.Log?.Warning("[Network] " + StatusText);
                    return;
                }
            }
            catch (Exception ex)
            {
                _net = null;
                _role = NetworkRole.Offline;
                StatusText = "Host failed: " + ex.Message;
                ModRuntime.Log?.Error("[Network] StartHost failed: " + ex);
                return;
            }
            _sessionPlayerIds.Add(0);
            _handshakeComplete = true;
            try { PartySaveService.OnSessionStart(); }
            catch (Exception ex) { Guard.Swallow("PartySave.OnSessionStart", ex); }
            StatusText = "Hosting on port " + port + " (0/" + (PluginInfo.MaxPlayers - 1) + " clients)";
            ModRuntime.Log?.Msg("[Network] Hosting on port " + port + " maxPlayers=" + PluginInfo.MaxPlayers
                + " schema=" + NetSchema.Hash.ToString("X8"));
        }

        public void ConnectToHost(string address, int port)
        {
            StopNetwork(); // runs every SessionReset clear
            address = (address ?? "").Trim();
            if (address.Length == 0 || port < 1 || port > 65535)
            {
                StatusText = "Invalid address or port";
                ModRuntime.Log?.Warning("[Network] Connect refused: address='" + address + "' port=" + port);
                return;
            }
            try
            {
                _role = NetworkRole.Client;
                _net = NewNetManager();
                if (!_net.Start())
                {
                    AbortNetwork("Connect failed: could not open UDP socket");
                    return;
                }
                var peer = _net.Connect(address, port, PluginInfo.ConnectionKey);
                if (peer == null)
                {
                    AbortNetwork("Connect failed: bad address " + address + ":" + port);
                    return;
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Error("[Network] ConnectToHost failed: " + ex);
                AbortNetwork("Connect failed: " + ex.Message);
                return;
            }
            // Client initially connects with unknown playerId; host will assign in handshake
            _peers.Clear();
            _peerToId.Clear();
            _connectedPeersDirty = true;
            _connectStartedAt = Time.realtimeSinceStartup;
            StatusText = "Connecting to " + address + ":" + port;
            ModRuntime.Log?.Msg("[Network] Connecting to " + address + ":" + port);
        }

        /// <summary>Defer StopNetwork to Update (after PollEvents) so leftover events in the batch cannot hit a torn-down session.</summary>
        private void RequestStop(string reason)
        {
            if (_stopPending) return;
            _stopPending = true;
            _stopReason = reason ?? "";
        }

        public void StopNetwork()
        {
            string stopReason = _stopReason;
            _stopPending = false;
            _stopReason = "";
            _connectStartedAt = 0f;
            bool wasLive = _handshakeComplete;
            _readyPeers.Clear();
            _connectedPeersDirty = true;
            _peerConnectedAt.Clear();
            _loggedGateDrops.Clear();
            _localPlayer = null;
            // Every Session + Connection step (proxies first, then handler state, doors, puzzles, pickups, story, storage,
            // key ring, FMOD, NetGate, traces, damage, floor drops, scene diff, enemies, bosses, EventZone, SceneFollow,
            // Dialoguer) in Bootstrap/SessionResetRegistrations.cs order, each isolated.
            SessionReset.RunAll(SessionReset.ReasonStop);
            _handshakeComplete = false;
            _vitalTimer = 0f;
            _peers.Clear();
            _peerToId.Clear();
            _connectedPeersDirty = true;
            _sessionPlayerIds.Clear();
            _peerScenes.Clear();
            _unicastPlayerId = -1;
            _lastAssignedId = 0;
            _sendTimer = 0f;
            _sceneMismatch = false;
            _hostSceneName = "";
            _localSceneName = "";
            _localPlayerId = 0;

            if (_net != null)
            {
                _net.Stop();
                _net = null;
            }

            _role = NetworkRole.Offline;
            StatusText = string.IsNullOrEmpty(stopReason) ? "Offline" : "Offline - " + stopReason;
            // Only undo session effects when a session was actually live: StartHost / ConnectToHost call StopNetwork
            // first, and forcing gameState = play / timeScale = 1 there would un-pause a menu or inventory the player
            // opened (F2 is used from the pause menu).
            if (wasLive)
                RestoreLocalControl();
        }

        static void RestoreLocalControl()
        {
            try { Time.timeScale = 1f; } catch (Exception e) { Guard.Swallow(e); }
            try { PlayerState.suspendInput = false; } catch (Exception e) { Guard.Swallow(e); }
            try { PlayerState.suspendInputCheats = false; } catch (Exception e) { Guard.Swallow(e); }
            try { PlayerState.animating = false; } catch (Exception e) { Guard.Swallow(e); }
            try { PlayerState.grappled = false; } catch (Exception e) { Guard.Swallow(e); }
            try { PlayerState.stunTime = 0f; } catch (Exception e) { Guard.Swallow(e); }
            try
            {
                var gs = PlayerState.gameState;
                if (gs != PlayerState.gameStates.menu && gs != PlayerState.gameStates.loading)
                    PlayerState.gameState = PlayerState.gameStates.play;
            }
            catch (Exception e) { Guard.Swallow(e); }
            try
            {
                var cs = PlayerState.charState;
                if (cs == PlayerState.charStates.grabbed
                    || cs == PlayerState.charStates.dead
                    || cs == PlayerState.charStates.animation)
                    PlayerState.charState = PlayerState.charStates.idle;
            }
            catch (Exception e) { Guard.Swallow(e); }
            ModRuntime.Log?.Msg("[Network] local control restored (offline)");
        }

        public void Update()
        {
            if (_net != null)
            {
                long tPoll = HitchTrace.Begin();
                try { _net.PollEvents(); }
                catch (Exception ex) { TickFailed("poll", ex); }
                HitchTrace.End("pollEvents", tPoll);
            }

            if (_stopPending)
            {
                string reason = _stopReason;
                StopNetwork();
                StatusText = string.IsNullOrEmpty(reason) ? "Offline" : "Offline - " + reason;
                return;
            }

            long tTimeouts = HitchTrace.Begin();
            TickConnectTimeout();
            TickHandshakeTimeouts();
            HitchTrace.End("netTimeouts", tTimeouts);

            if (!IsConnected || !_handshakeComplete)
                return;

            // Native Interaction TAKE on cloned ItemPickups. No mod E bind.
            if (Input.GetKeyDown(KeyCode.G))
            {
                try { DroppedItemHandlers.TryDropCurrentItem(); }
                catch (Exception ex) { ModRuntime.Log?.Warning("[Drop] G crashed: " + ex.Message); }
            }

            // Solo host (no handshaken peer yet): nothing to sync, leave the game vanilla.
            if (!HasReadyPeers)
                return;

            // Each domain tick is isolated: one throwing must not skip the others this frame.
            long tTicks = HitchTrace.Begin();
            try { SessionHandlers.TickPendingDumps(); } catch (Exception ex) { TickFailed("dumps", ex); }
            try { DoorSyncService.Tick(); } catch (Exception ex) { TickFailed("door", ex); }
            try { _enemySync.TickHost(this); } catch (Exception ex) { TickFailed("enemy", ex); }
            if (ModConfig.PuzzlesEnabled)
            {
                try { _puzzleSync.Tick(this); } catch (Exception ex) { TickFailed("puzzle", ex); }
            }
            try { _bossSync.TickHost(this); } catch (Exception ex) { TickFailed("boss", ex); }
            try { _pickupSync.TickHost(this); } catch (Exception ex) { TickFailed("pickup", ex); }
            try { _storySync.TickHost(this); } catch (Exception ex) { TickFailed("story", ex); }
            try { _storySync.TickClient(this); } catch (Exception ex) { TickFailed("story-client", ex); }
            if (_role == NetworkRole.Host)
            {
                try { SceneFollowService.Tick(); } catch (Exception ex) { TickFailed("scenefollow", ex); }
            }
            try { _storageSync.TickHost(this); } catch (Exception ex) { TickFailed("storage", ex); }
            HitchTrace.End("domainTicks", tTicks);

            // Vitals ~5 Hz for remote damage/death presentation (host's toggle while connected as a client)
            if (ModConfig.PlayerVitalsEnabled)
            {
                _vitalTimer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                if (_vitalTimer >= VitalInterval)
                {
                    // Carry the remainder (clamped) so the cadence matches the interval instead of drifting low.
                    _vitalTimer = Mathf.Min(_vitalTimer - VitalInterval, VitalInterval);
                    long tVital = HitchTrace.Begin();
                    try { AvatarHandlers.SendLocalVital(); } catch (Exception ex) { TickFailed("vital", ex); }
                    HitchTrace.End("sendVital", tVital);
                }
            }

            _sendTimer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_sendTimer < PluginInfo.SendInterval)
                return;
            // Keep the remainder: resetting to 0 dropped the overshoot every send, so pose ran below the configured
            // rate (e.g. ~24-27 Hz at 60 fps for a 30 Hz interval). Clamped so a long frame cannot queue a burst.
            _sendTimer = Mathf.Min(_sendTimer - PluginInfo.SendInterval, PluginInfo.SendInterval);

            if (_localPlayer == null && PlayerState.player != null)
            {
                _localPlayer = PlayerState.player;
                PlaytestLog.Verbose("Net", "local player " + _localPlayer.name);
            }

            // Send local player state to ALL connected peers
            GameObject player = _localPlayer;
            if (player == null)
                return;

            long tAvatar = HitchTrace.Begin();
            try
            {
                var msg = AvatarHandlers.BuildPlayerStateMessage(player);
                AvatarHandlers.SendPlayerState(msg);
                HitchTrace.Send();
            }
            catch (Exception ex) { TickFailed("avatar", ex); }
            HitchTrace.End("sendAvatar", tAvatar);
            // Client states are relayed by the host on receipt (AvatarNetHandlers.HandlePlayerState / HandleBonePose).
        }

        /// <summary>Throttled per-domain failure log: first occurrence in full, then every 300th.</summary>
        private void TickFailed(string name, Exception ex)
        {
            int n;
            _tickFailures.TryGetValue(name, out n);
            n++;
            _tickFailures[name] = n;
            if (n == 1 || n % 300 == 0)
                ModRuntime.Log?.Error("[Network] tick '" + name + "' threw (x" + n + "): " + ex);
        }

        private void TickConnectTimeout()
        {
            if (_role != NetworkRole.Client || _handshakeComplete || _connectStartedAt <= 0f)
                return;
            if (Time.realtimeSinceStartup - _connectStartedAt < ConnectTimeoutSeconds)
                return;
            AbortNetwork("Connect timed out (no host answer / handshake)");
        }

        /// <summary>Host: a peer that never completes the handshake (old mod build, junk sender) is dropped.</summary>
        private void TickHandshakeTimeouts()
        {
            if (_role != NetworkRole.Host || _peerConnectedAt.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            _timedOutPeers.Clear();
            foreach (var kvp in _peerConnectedAt)
            {
                if (now - kvp.Value >= HandshakeTimeoutSeconds)
                    _timedOutPeers.Add(kvp.Key);
            }
            for (int i = 0; i < _timedOutPeers.Count; i++)
            {
                int pid = _timedOutPeers[i];
                _peerConnectedAt.Remove(pid);
                NetPeer peer;
                if (_peers.TryGetValue(pid, out peer) && !_readyPeers.Contains(pid))
                {
                    ModRuntime.Log?.Warning("[Network] Player " + pid + " handshake timed out — disconnecting");
                    RejectPeer(peer, "Handshake timed out (mod build mismatch?)");
                }
            }
        }

        /// <summary>Disconnect one peer with a reason string the remote shows in its UI (DisconnectInfo.AdditionalData).</summary>
        private void RejectPeer(NetPeer peer, string reason)
        {
            try
            {
                var w = new NetDataWriter();
                w.Put(reason ?? "");
                peer.Disconnect(w);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Network] reject send failed: " + ex.Message);
                try { peer.Disconnect(); }
                catch (Exception ex2) { ModRuntime.Log?.Warning("[Network] disconnect failed: " + ex2.Message); }
            }
        }

        public void LateUpdate()
        {
            _proxyManager.LateUpdate();
        }

        private bool SendPeer(NetPeer peer, NetDataWriter writer, DeliveryMethod method, byte channel)
        {
            if (peer == null || peer.ConnectionState != ConnectionState.Connected) return false;
            try
            {
                peer.Send(writer, channel, method);
                return true;
            }
            catch (Exception ex)
            {
                // e.g. TooBigPacketException on a Sequenced send: log once per method/channel, keep the rest of the fan-out.
                string key = "send:" + method + ":" + channel;
                if (_loggedGateDrops.Add(key))
                    ModRuntime.Log?.Error("[Network] send failed (" + method + " ch" + channel + ", " + writer.Length + " bytes): " + ex.Message);
                return false;
            }
        }

        /// <summary>Host → every handshaken peer except excludePlayerId. Pre-handshake peers never receive relays.</summary>
        public void RelayRaw(NetDataWriter writer, DeliveryMethod method, int excludePlayerId)
        {
            if (_role != NetworkRole.Host) return;
            foreach (var kvp in _peers)
            {
                if (kvp.Key == excludePlayerId) continue;
                if (kvp.Key == _localPlayerId) continue;
                if (!_readyPeers.Contains(kvp.Key)) continue;
                SendPeer(kvp.Value, writer, method, NetChannels.Events);
            }
        }

        internal void SendToPlayer(int playerId, NetDataWriter writer, DeliveryMethod method)
        {
            NetPeer peer;
            if (_readyPeers.Contains(playerId) && _peers.TryGetValue(playerId, out peer))
                SendPeer(peer, writer, method, NetChannels.Events);
        }

        internal void BroadcastRaw(NetDataWriter writer, DeliveryMethod method)
        {
            if (_unicastPlayerId >= 0)
            {
                SendToPlayer(_unicastPlayerId, writer, method);
                return;
            }
            foreach (var kvp in _peers)
            {
                if (!_readyPeers.Contains(kvp.Key)) continue;
                SendPeer(kvp.Value, writer, method, NetChannels.Events);
            }
        }

        /// <summary>
        /// Continuous state snapshots (enemy / boss): Sequenced on the State channel so a stall or loss there
        /// never blocks the ReliableOrdered event stream. Payload must fit one packet (StatePacketBudget).
        /// </summary>
        internal void BroadcastState(NetDataWriter writer)
        {
            if (_unicastPlayerId >= 0)
            {
                NetPeer target;
                if (_readyPeers.Contains(_unicastPlayerId) && _peers.TryGetValue(_unicastPlayerId, out target))
                    SendPeer(target, writer, DeliveryMethod.Sequenced, NetChannels.State);
                return;
            }
            foreach (var kvp in _peers)
            {
                if (!_readyPeers.Contains(kvp.Key)) continue;
                SendPeer(kvp.Value, writer, DeliveryMethod.Sequenced, NetChannels.State);
            }
        }

        internal void BroadcastRawExcept(NetDataWriter writer, DeliveryMethod method, int exceptPlayerId)
        {
            if (_unicastPlayerId >= 0)
            {
                if (_unicastPlayerId != exceptPlayerId)
                    SendToPlayer(_unicastPlayerId, writer, method);
                return;
            }
            foreach (var kvp in _peers)
            {
                if (kvp.Key == exceptPlayerId) continue;
                if (!_readyPeers.Contains(kvp.Key)) continue;
                SendPeer(kvp.Value, writer, method, NetChannels.Events);
            }
        }

        public int GetPlayerCount()
        {
            if (_sessionPlayerIds.Count > 0)
                return _sessionPlayerIds.Count;
            return 1;
        }

        private int[] _remoteIdsCache = Array.Empty<int>();
        private int _remoteIdsSig = int.MinValue;

        /// <summary>
        /// Remote session ids as a cached array (called per enemy/boss per frame — no iterator garbage).
        /// Rebuilt when the roster or local id changes; safe to iterate while the roster mutates.
        /// </summary>
        public int[] GetRemotePlayerIds()
        {
            int sig = _localPlayerId * 31 + _sessionPlayerIds.Count;
            foreach (int id in _sessionPlayerIds)
                sig = unchecked(sig * 16777619 ^ id);
            if (sig != _remoteIdsSig)
            {
                var ids = new int[_sessionPlayerIds.Count];
                int n = 0;
                foreach (int id in _sessionPlayerIds)
                {
                    if (id != _localPlayerId)
                        ids[n++] = id;
                }
                if (n != ids.Length)
                    Array.Resize(ref ids, n);
                _remoteIdsCache = ids;
                _remoteIdsSig = sig;
            }
            return _remoteIdsCache;
        }

        // --- Network events ---

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            if (_stopPending || _net == null) return;
            int playerId;
            if (_role == NetworkRole.Host)
            {
                playerId = AllocClientId();
                if (playerId < 0)
                {
                    ModRuntime.Log?.Warning("[Network] Rejecting peer: max players " + PluginInfo.MaxPlayers);
                    RejectPeer(peer, "Server full (" + PluginInfo.MaxPlayers + " players max)");
                    return;
                }

                // Not in the session/roster until its Handshake passes (pre-handshake gating).
                _peers[playerId] = peer;
                _connectedPeersDirty = true;
                _peerToId[peer] = playerId;
                _peerConnectedAt[playerId] = Time.realtimeSinceStartup;
                ModRuntime.Log?.Msg("[Network] Peer connected, assigned playerId=" + playerId + " (awaiting handshake)");

                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.Handshake);
                new HandshakeMessage
                {
                    ProtocolVersion = PluginInfo.ProtocolVersion,
                    AssignedPlayerId = playerId,
                    SchemaHash = NetSchema.Hash,
                    ModVersion = NetSchema.ModVersion,
                    GameBuildHash = GameBuild.Hash,
                    GameBuild = GameBuild.Version
                }.Serialize(w);
                SendPeer(peer, w, DeliveryMethod.ReliableOrdered, NetChannels.Events);
            }
            else
            {
                // Client connected to host
                _peers[0] = peer; // host is playerId 0
                _connectedPeersDirty = true;
                _peerToId[peer] = 0;
                ModRuntime.Log?.Msg("[Network] Connected to host");

                // Send handshake to host
                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.Handshake);
                new HandshakeMessage
                {
                    ProtocolVersion = PluginInfo.ProtocolVersion,
                    AssignedPlayerId = -1,
                    SchemaHash = NetSchema.Hash,
                    ModVersion = NetSchema.ModVersion,
                    GameBuildHash = GameBuild.Hash,
                    GameBuild = GameBuild.Version
                }.Serialize(w);
                SendPeer(peer, w, DeliveryMethod.ReliableOrdered, NetChannels.Events);
            }
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            // Events queued behind a stop request belong to a dead session.
            if (_stopPending || _role == NetworkRole.Offline) return;

            string remoteReason = "";
            try
            {
                var extra = disconnectInfo.AdditionalData;
                if (extra != null && !extra.IsNull && extra.AvailableBytes > 0)
                    remoteReason = extra.GetString();
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Network] disconnect reason unreadable: " + ex.Message);
            }

            if (_peerToId.TryGetValue(peer, out int playerId))
            {
                ModRuntime.Log?.Msg("[Network] Player " + playerId + " disconnected: " + disconnectInfo.Reason
                    + (remoteReason.Length > 0 ? " (" + remoteReason + ")" : ""));
                bool wasReady = _readyPeers.Remove(playerId);
                _connectedPeersDirty = true;
                _peerConnectedAt.Remove(playerId);
                _proxyManager.DestroyProxy(playerId);
                _peers.Remove(playerId);
                _peerToId.Remove(peer);
                _peerScenes.Remove(playerId);
                if (_role == NetworkRole.Host)
                {
                    SessionHandlers.NotePeerGone(playerId);
                    SceneHandlers.NotePeerGone(playerId);
                    RebuildHostSession();
                    if (wasReady)
                        BroadcastPlayerRoster();
                    StatusText = "Hosting (" + (GetPlayerCount() - 1) + "/" + (PluginInfo.MaxPlayers - 1) + " clients)";
                }
            }

            if (_role != NetworkRole.Host)
            {
                // Deferred: StopNetwork tears down _net while PollEvents is still iterating this batch.
                string reason = remoteReason.Length > 0 ? remoteReason
                    : (_handshakeComplete ? "Host disconnected" : "Connection failed") + " (" + disconnectInfo.Reason + ")";
                RequestStop(reason);
            }
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            ModRuntime.Log?.Error("[Network] Error: " + socketError);
            StatusText = "Error: " + socketError;
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (_role == NetworkRole.Host && !_stopPending)
                request.AcceptIfKey(PluginInfo.ConnectionKey);
            else
                request.Reject();
        }

        /// <summary>Null when compatible, otherwise a one-line reason naming both sides.</summary>
        private static string DescribeHandshakeMismatch(HandshakeMessage hs, bool remoteIsHost)
        {
            string remote = remoteIsHost ? "host" : "client";
            if (hs.ProtocolVersion != PluginInfo.ProtocolVersion)
                return "Protocol mismatch: " + remote + " v" + hs.ProtocolVersion + " vs local v" + PluginInfo.ProtocolVersion;
            if (hs.SchemaHash != NetSchema.Hash)
                return "Mod build mismatch: " + remote + " " + (hs.ModVersion ?? "?") + " (#" + hs.SchemaHash.ToString("X8")
                    + ") vs local " + NetSchema.ModVersion + " (#" + NetSchema.Hash.ToString("X8") + ")";
            // Hash 0 = that side could not fingerprint its game files: nothing to compare.
            if (hs.GameBuildHash != 0 && GameBuild.Hash != 0 && hs.GameBuildHash != GameBuild.Hash)
                return "Game build mismatch: " + remote + " " + (hs.GameBuild ?? "?") + " (#" + hs.GameBuildHash.ToString("X8")
                    + ") vs local " + GameBuild.Version + " (#" + GameBuild.Hash.ToString("X8") + ") - both players need the same SIGNALIS version";
            return null;
        }

        private void HandleHandshake(HandshakeMessage handshake, int senderId)
        {
            string mismatch = DescribeHandshakeMismatch(handshake, _role == NetworkRole.Client);
            if (mismatch != null)
            {
                ModRuntime.Log?.Error("[Network] " + mismatch);
                if (_role == NetworkRole.Host)
                {
                    // Only the offending peer is dropped; the host session and other clients are untouched.
                    NetPeer bad;
                    if (senderId >= 1 && _peers.TryGetValue(senderId, out bad))
                        RejectPeer(bad, mismatch);
                }
                else
                {
                    RequestStop(mismatch);
                }
                return;
            }

            if (_role == NetworkRole.Host)
            {
                if (senderId < 1) return;
                bool newlyReady = _readyPeers.Add(senderId);
                if (newlyReady) _connectedPeersDirty = true;
                if (!newlyReady)
                {
                    // A repeated Handshake must not retrigger roster/dump work.
                    if (_loggedGateDrops.Add("hs:" + senderId))
                        ModRuntime.Log?.Warning("[Network] duplicate Handshake from player " + senderId + " ignored");
                    return;
                }
                _peerConnectedAt.Remove(senderId);
                RebuildHostSession();
                _lastStateTime = Time.unscaledTime;
                StatusText = "Hosting (" + (GetPlayerCount() - 1) + "/" + (PluginInfo.MaxPlayers - 1) + " clients)";
                ModRuntime.Log?.Msg("[Network] Handshake OK, player " + senderId + " ready (" + GetPlayerCount() + " players)");
                WorldRegistry.RebuildIfStale();
                SceneHandlers.BroadcastSceneHello();
                BroadcastPlayerRoster();
                SessionHandlers.SendFullWorldSnapshot(senderId);
                PartySaveService.SendJoinToken(this, senderId);
                return;
            }

            // Client: only the host (peer 0) may complete the handshake, exactly once.
            if (senderId != 0) return;
            if (_handshakeComplete)
            {
                if (_loggedGateDrops.Add("hs:host"))
                    ModRuntime.Log?.Warning("[Network] duplicate host Handshake ignored");
                return;
            }
            int assigned = handshake.AssignedPlayerId;
            if (assigned < 1 || assigned >= ModConfig.HardMaxPlayers)
            {
                ModRuntime.Log?.Error("[Network] Host assigned invalid playerId=" + assigned);
                RequestStop("Host sent an invalid player id (" + assigned + ")");
                return;
            }
            _localPlayerId = assigned;
            _sessionPlayerIds.Add(0);
            _sessionPlayerIds.Add(_localPlayerId);
            ModRuntime.Log?.Msg("[Network] Host assigned playerId=" + _localPlayerId);

            _readyPeers.Add(0);
            _connectedPeersDirty = true;
            _handshakeComplete = true;
            _connectStartedAt = 0f;
            _lastStateTime = Time.unscaledTime;
            StatusText = "Connected to host";
            ModRuntime.Log?.Msg("[Network] Handshake OK, local playerId=" + _localPlayerId);
            WorldRegistry.RebuildIfStale();
            SceneHandlers.BroadcastSceneHello();
            _enemySync.PuppetAllNow();
        }

        private int AllocClientId()
        {
            // Round-robin over 1..MaxPlayers-1 (skipping live peers): a leaver's id is reused as late as possible,
            // so ids baked into dropped-item keys / proxies / peer maps are not recycled while stale state may linger.
            int slots = PluginInfo.MaxPlayers - 1;
            for (int step = 1; step <= slots; step++)
            {
                int id = ((_lastAssignedId - 1 + step) % slots) + 1;
                if (!_peers.ContainsKey(id))
                {
                    _lastAssignedId = id;
                    return id;
                }
            }
            return -1;
        }

        /// <summary>Host session = host + handshaken clients (a connected-but-unverified peer is not a player yet).</summary>
        private void RebuildHostSession()
        {
            _sessionPlayerIds.Clear();
            _sessionPlayerIds.Add(0);
            foreach (int id in _readyPeers)
                _sessionPlayerIds.Add(id);
        }

        private void BroadcastPlayerRoster()
        {
            if (_role != NetworkRole.Host) return;
            RebuildHostSession();
            var ids = new int[_sessionPlayerIds.Count];
            int i = 0;
            foreach (int id in _sessionPlayerIds)
                ids[i++] = id;
            // The host's sync toggles ride every roster: clients use them for the session (ModConfig.*Enabled).
            var msg = new PlayerRosterMessage { PlayerIds = ids, HostFlags = ModConfig.LocalSyncFlags };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.PlayerRoster);
            msg.Serialize(writer);
            int prev = _unicastPlayerId;
            _unicastPlayerId = -1;
            try { BroadcastRaw(writer, DeliveryMethod.ReliableOrdered); }
            finally { _unicastPlayerId = prev; }
            ModRuntime.Log?.Msg("[Network] Roster sent (" + ids.Length + " players)");
        }

        private void HandlePlayerRoster(PlayerRosterMessage msg)
        {
            if (_role == NetworkRole.Host) return;
            ModConfig.ApplyHostSyncFlags(msg.HostFlags);
            _sessionPlayerIds.Clear();
            if (msg.PlayerIds != null)
            {
                for (int i = 0; i < msg.PlayerIds.Length; i++)
                    _sessionPlayerIds.Add(msg.PlayerIds[i]);
            }
            _sessionPlayerIds.Add(0);
            if (_localPlayerId > 0)
                _sessionPlayerIds.Add(_localPlayerId);

            _stalePeerIds.Clear();
            foreach (int pid in _proxyManager.GetProxyPlayerIds())
            {
                if (!_sessionPlayerIds.Contains(pid))
                    _stalePeerIds.Add(pid);
            }
            for (int i = 0; i < _stalePeerIds.Count; i++)
                _proxyManager.DestroyProxy(_stalePeerIds[i]);

            StatusText = "Connected (" + GetPlayerCount() + " players)";
            ModRuntime.Log?.Msg("[Network] Roster applied, players=" + GetPlayerCount());
        }

        /// <summary>
        /// Scene load (ModRuntime.OnSceneChanged, after the WorldId scan and registry rebuild): this manager's own per-scene
        /// fields, then every Scene-scope SessionReset step (Bootstrap/SessionResetRegistrations.cs order), then the scene
        /// hello / world dump for a live session.
        /// </summary>
        public void OnSceneChanged(string scene)
        {
            _localPlayer = null;
            _sendTimer = 0f;
            _lastStateTime = 0f;
            _sceneMismatch = false;
            SessionReset.RunScene(scene ?? "");
            if (_handshakeComplete)
            {
                if (SceneFollowService.LocalIsTransient())
                {
                    PlaytestLog.Verbose("Scene", "skip hello/dump (loading)");
                    if (_role == NetworkRole.Host && HasReadyPeers)
                        SessionHandlers.DeferDump(-1);
                    return;
                }
                SceneHandlers.BroadcastSceneHello();
                if (_role == NetworkRole.Host)
                {
                    if (HasReadyPeers)
                        SessionHandlers.SendFullWorldSnapshot();
                }
                else if (AirlockCinematic.ShouldIgnoreHostFollow(_hostSceneName))
                    PlaytestLog.Event("Scene", "skip wreck dump (airlock split)");
                else
                {
                    SessionHandlers.RequestWorldSnapshot();
                    _enemySync.PuppetAllNow();
                }
            }
        }
    }
}
