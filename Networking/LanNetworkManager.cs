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

        public NetworkRole Role => _role;
        public bool IsConnected => _handshakeComplete && (_role == NetworkRole.Host || _peers.Count > 0);
        public int LocalPlayerId => _localPlayerId;
        public string StatusText { get; private set; } = "Offline";
        public bool SceneMismatch => _sceneMismatch;
        public string HostSceneName => _hostSceneName;
        public string LocalSceneName => _localSceneName;
        public PlayerProxyManager ProxyManager => _proxyManager;
        public EnemySyncService EnemySync => _enemySync;
        public PuzzleSyncService PuzzleSync => _puzzleSync;
        public BossSyncService BossSync => _bossSync;
        public WorldPickupSyncService PickupSync => _pickupSync;
        public StorySyncService StorySync => _storySync;
        public StorageBoxSyncService StorageSync => _storageSync;
        public GameObject GetLocalPlayer() => _localPlayer;
        public void SetLocalPlayer(GameObject go) { _localPlayer = go; }

        private static Action _connected;
        private static Action _disconnected;

        public static event Action Connected
        {
            add { _connected = (Action)Delegate.Combine(_connected, value); }
            remove { _connected = (Action)Delegate.Remove(_connected, value); }
        }

        public static event Action Disconnected
        {
            add { _disconnected = (Action)Delegate.Combine(_disconnected, value); }
            remove { _disconnected = (Action)Delegate.Remove(_disconnected, value); }
        }

        public LanNetworkManager()
        {
            Instance = this;
            ConstructDomainHandlers();
        }

        /// <summary>Transport: connected peers for sequenced avatar sends.</summary>
        internal System.Collections.Generic.IEnumerable<NetPeer> ConnectedPeers()
        {
            foreach (var kvp in _peers)
                yield return kvp.Value;
        }

        internal bool TryGetPeer(int playerId, out NetPeer peer) => _peers.TryGetValue(playerId, out peer);

        internal bool HasPeer(int playerId) => _peers.ContainsKey(playerId);

        internal bool HandshakeComplete => _handshakeComplete;

        internal bool HasTransport => _net != null;

        /// <summary>Join/resync dump scope. Returns previous unicast id for restore in finally.</summary>
        internal int BeginUnicast(int targetPlayerId)
        {
            int prev = _unicastPlayerId;
            _unicastPlayerId = targetPlayerId;
            return prev;
        }

        internal void EndUnicast(int previousUnicastPlayerId) => _unicastPlayerId = previousUnicastPlayerId;

        internal void NotePeerScene(int playerId, string scene) => _peerScenes[playerId] = scene ?? "";

        internal void NoteLocalSceneForHello(string scene)
        {
            _localSceneName = scene ?? "";
            if (_role == NetworkRole.Host)
                _hostSceneName = _localSceneName;
        }

        internal void SetHostSceneName(string scene) => _hostSceneName = scene ?? "";

        internal void SetLocalSceneName(string scene) => _localSceneName = scene ?? "";

        internal void SetSceneMismatch(bool mismatch) => _sceneMismatch = mismatch;

        internal void SetStatusText(string text) => StatusText = text ?? "";

        internal void NoteAvatarStateReceived() => _lastStateTime = Time.time;

        public void StartHost(int port)
        {
            StopNetwork();
            _role = NetworkRole.Host;
            _localPlayerId = 0;
            _net = new NetManager(this) { UnconnectedMessagesEnabled = true, DisconnectTimeout = 5000 };
            if (!_net.Start(port))
            {
                StatusText = "Failed to bind port " + port;
                _role = NetworkRole.Offline;
                return;
            }
            _sessionPlayerIds.Add(0);
            _handshakeComplete = true;
            StatusText = "Hosting on port " + port;
            ModRuntime.Log?.Msg("[Network] Hosting on port " + port);
        }

        public void ConnectToHost(string address, int port)
        {
            StopNetwork();
            _role = NetworkRole.Client;
            _net = new NetManager(this) { UnconnectedMessagesEnabled = true, DisconnectTimeout = 5000 };
            _net.Start();
            var peer = _net.Connect(address, port, PluginInfo.ConnectionKey);
            // Client initially connects with unknown playerId; host will assign in handshake
            _peers.Clear();
            _peerToId.Clear();
            StatusText = "Connecting to " + address + ":" + port;
            ModRuntime.Log?.Msg("[Network] Connecting to " + address + ":" + port);
        }

        public void StopNetwork()
        {
            _localPlayer = null;
            _proxyManager.DestroyAll();
            DoorSyncService.Reset();
            _puzzleSync.Reset();
            _pickupSync.Reset();
            _storySync.Reset();
            _storageSync.Reset();
            PartyKeyRing.Reset();
            FmodEmitterSync.Reset();
            NetGate.Reset();
            SourceAnimReader.Reset();
            HitchTrace.Reset();
            PlaytestLog.Reset();
            NetworkDamageSystem.Reset();
            DroppedItemManager.ClearAll();
            AvatarHandlers.ResetSendState();
            DroppedItemHandlers.Reset();
            SessionHandlers.Reset();
            // Sticky beyond drop-claims + dump queue: client puppets/boss AI, EventZone
            // once-fired, cutscene skip/start sets, airlock unlocks + personal scene,
            // SceneFollow inflight coalesce, Dialoguer flavor gate.
            try { _enemySync.Reset(); } catch { }
            try { _bossSync.Reset(); } catch { }
            try { Patches.EventZonePatch.OnSceneChanged(); } catch { }
            try { SceneFollowService.Reset(); } catch { }
            try { Patches.DialoguerGate.ClearFlavor(); } catch { }
            _handshakeComplete = false;
            _vitalTimer = 0f;
            _peers.Clear();
            _peerToId.Clear();
            _sessionPlayerIds.Clear();
            _peerScenes.Clear();
            _unicastPlayerId = -1;
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

            if (_role != NetworkRole.Offline)
            {
                var d = _disconnected;
                if (d != null)
                    d();
            }

            _role = NetworkRole.Offline;
            StatusText = "Offline";
            RestoreLocalControl();
        }

        static void RestoreLocalControl()
        {
            try { Time.timeScale = 1f; } catch { }
            try { PlayerState.suspendInput = false; } catch { }
            try { PlayerState.suspendInputCheats = false; } catch { }
            try { PlayerState.animating = false; } catch { }
            try { PlayerState.grappled = false; } catch { }
            try { PlayerState.stunTime = 0f; } catch { }
            try
            {
                var gs = PlayerState.gameState;
                if (gs != PlayerState.gameStates.menu && gs != PlayerState.gameStates.loading)
                    PlayerState.gameState = PlayerState.gameStates.play;
            }
            catch { }
            try
            {
                var cs = PlayerState.charState;
                if (cs == PlayerState.charStates.grabbed
                    || cs == PlayerState.charStates.dead
                    || cs == PlayerState.charStates.animation)
                    PlayerState.charState = PlayerState.charStates.idle;
            }
            catch { }
            ModRuntime.Log?.Msg("[Network] local control restored (offline)");
        }

        public void Update()
        {
            _net?.PollEvents();

            if (!IsConnected || !_handshakeComplete)
                return;

            SessionHandlers.TickPendingDumps();
            DoorSyncService.Tick();
            _enemySync.TickHost(this);
            if (ModConfig.PuzzlesEnabled)
                _puzzleSync.Tick(this);
            _bossSync.TickHost(this);
            _pickupSync.TickHost(this);
            _storySync.TickHost(this);
            _storageSync.TickHost(this);

            // Vitals ~5 Hz for remote damage/death presentation
            if (ModConfig.SyncPlayerVitals?.Value == true)
            {
                _vitalTimer += Mathf.Min(Time.deltaTime, 0.1f);
                if (_vitalTimer >= 0.2f)
                {
                    _vitalTimer = 0f;
                    AvatarHandlers.SendLocalVital();
                }
            }

            // Native Interaction TAKE on cloned ItemPickups. No mod E bind.
            if (Input.GetKeyDown(KeyCode.G))
            {
                try { DroppedItemHandlers.TryDropCurrentItem(); }
                catch (Exception ex) { ModRuntime.Log?.Warning("[Drop] G crashed: " + ex.Message); }
            }

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < PluginInfo.SendInterval)
                return;
            _sendTimer = 0f;

            if (_localPlayer == null && PlayerState.player != null)
            {
                _localPlayer = PlayerState.player;
                PlaytestLog.Verbose("Net", "local player " + _localPlayer.name);
            }

            // Send local player state to ALL connected peers
            GameObject player = _localPlayer;
            if (player == null)
                return;

            var msg = AvatarHandlers.BuildPlayerStateMessage(player);
            AvatarHandlers.SendPlayerState(msg);
            HitchTrace.Send();

            // Host also needs to relay states it received from clients — but that's handled
            // in OnReceive: the host stores the state and re-sends to all other peers
        }

        public void LateUpdate()
        {
            _proxyManager.LateUpdate();
        }

        public void RelayRaw(NetDataWriter writer, DeliveryMethod method, int excludePlayerId)
        {
            if (_role != NetworkRole.Host) return;
            foreach (var kvp in _peers)
            {
                if (kvp.Key == excludePlayerId) continue;
                if (kvp.Key == _localPlayerId) continue;
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                peer.Send(writer, method);
            }
        }

        internal void SendToPlayer(int playerId, NetDataWriter writer, DeliveryMethod method)
        {
            if (_peers.TryGetValue(playerId, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, method);
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
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                peer.Send(writer, method);
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
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                peer.Send(writer, method);
            }
        }

        public int GetPlayerCount()
        {
            if (_sessionPlayerIds.Count > 0)
                return _sessionPlayerIds.Count;
            return 1;
        }

        public IEnumerable<int> GetRemotePlayerIds()
        {
            foreach (int id in _sessionPlayerIds)
            {
                if (id != _localPlayerId)
                    yield return id;
            }
        }

        public NetPeer GetPeer(int playerId)
        {
            _peers.TryGetValue(playerId, out var peer);
            return peer;
        }

        // --- Network events ---

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            int playerId;
            if (_role == NetworkRole.Host)
            {
                playerId = AllocClientId();
                if (playerId < 0)
                {
                    ModRuntime.Log?.Warning("[Network] Rejecting peer: max players " + PluginInfo.MaxPlayers);
                    peer.Disconnect();
                    return;
                }

                _peers[playerId] = peer;
                _peerToId[peer] = playerId;
                RebuildHostSession();
                ModRuntime.Log?.Msg("[Network] Client connected, assigned playerId=" + playerId);

                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.Handshake);
                new HandshakeMessage { ProtocolVersion = PluginInfo.ProtocolVersion, AssignedPlayerId = playerId }.Serialize(w);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
                BroadcastPlayerRoster();
            }
            else
            {
                // Client connected to host
                _peers[0] = peer; // host is playerId 0
                _peerToId[peer] = 0;
                ModRuntime.Log?.Msg("[Network] Connected to host");

                // Send handshake to host
                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.Handshake);
                new HandshakeMessage { ProtocolVersion = PluginInfo.ProtocolVersion, AssignedPlayerId = -1 }.Serialize(w);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (_peerToId.TryGetValue(peer, out int playerId))
            {
                ModRuntime.Log?.Msg("[Network] Player " + playerId + " disconnected: " + disconnectInfo.Reason);
                _proxyManager.DestroyProxy(playerId);
                _peers.Remove(playerId);
                _peerToId.Remove(peer);
                _peerScenes.Remove(playerId);
                if (_role == NetworkRole.Host)
                {
                    SessionHandlers.NotePeerGone(playerId);
                    RebuildHostSession();
                    BroadcastPlayerRoster();
                }
            }

            if (_role != NetworkRole.Host)
            {
                StopNetwork();
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
            if (_role == NetworkRole.Host)
                request.AcceptIfKey(PluginInfo.ConnectionKey);
            else
                request.Reject();
        }

        private void HandleHandshake(HandshakeMessage handshake, int senderId)
        {
            if (handshake.ProtocolVersion != PluginInfo.ProtocolVersion)
            {
                ModRuntime.Log?.Error("[Network] Protocol mismatch: local=" + PluginInfo.ProtocolVersion + " remote=" + handshake.ProtocolVersion);
                _handshakeComplete = false;
                foreach (var kvp in _peers)
                    kvp.Value.Disconnect();
                return;
            }

            if (_role == NetworkRole.Client)
            {
                _localPlayerId = handshake.AssignedPlayerId;
                _sessionPlayerIds.Add(0);
                _sessionPlayerIds.Add(_localPlayerId);
                ModRuntime.Log?.Msg("[Network] Host assigned playerId=" + _localPlayerId);
            }

            _handshakeComplete = true;
            _lastStateTime = Time.time;
            StatusText = _role == NetworkRole.Host
                ? "Clients connected (" + GetPlayerCount() + ")"
                : "Connected to host";
            ModRuntime.Log?.Msg("[Network] Handshake OK, local playerId=" + _localPlayerId);
            WorldRegistry.Rebuild();
            BroadcastSceneHello();

            var connected = _connected;
            if (connected != null) connected();

            if (_role == NetworkRole.Host)
            {
                BroadcastPlayerRoster();
                if (senderId >= 1)
                    SendFullWorldSnapshot(senderId);
            }
            else
            {
                _enemySync.PuppetAllNow();
            }
        }

        private int AllocClientId()
        {
            for (int i = 1; i < PluginInfo.MaxPlayers; i++)
            {
                if (!_peers.ContainsKey(i))
                    return i;
            }
            return -1;
        }

        private void RebuildHostSession()
        {
            _sessionPlayerIds.Clear();
            _sessionPlayerIds.Add(0);
            foreach (var kvp in _peers)
                _sessionPlayerIds.Add(kvp.Key);
        }

        private void BroadcastPlayerRoster()
        {
            if (_role != NetworkRole.Host) return;
            RebuildHostSession();
            var ids = new int[_sessionPlayerIds.Count];
            int i = 0;
            foreach (int id in _sessionPlayerIds)
                ids[i++] = id;
            var msg = new PlayerRosterMessage { PlayerIds = ids };
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

        public void OnSceneChanged()
        {
            _localPlayer = null;
            AvatarHandlers.ResetSendState();
            SourceAnimReader.Reset();
            HitchTrace.Reset();
            _sendTimer = 0f;
            _lastStateTime = 0f;
            _proxyManager.DestroyAll();
            DroppedItemManager.ClearVisuals();
            DroppedItemManager.RespawnCurrentScene();
            WorldRegistry.Rebuild();
            DoorSyncService.RefreshScene();
            FmodEmitterSync.Reset();
            _enemySync.OnSceneChanged();
            _puzzleSync.RefreshScene();
            _bossSync.OnSceneChanged();
            _pickupSync.RefreshScene();
            _storySync.OnSceneChanged();
            Patches.EventZonePatch.OnSceneChanged();
            _sceneMismatch = false;
            try { SceneFollowService.NoteArrived(SceneManager.GetActiveScene().name ?? ""); } catch { }
            if (_handshakeComplete)
            {
                if (SceneFollowService.LocalIsTransient())
                {
                    PlaytestLog.Verbose("Scene", "skip hello/dump (loading)");
                    if (_role == NetworkRole.Host)
                        SessionHandlers.DeferDump(-1);
                    return;
                }
                BroadcastSceneHello();
                if (_role == NetworkRole.Host)
                    SendFullWorldSnapshot();
                else if (AirlockCinematic.ShouldIgnoreHostFollow(_hostSceneName))
                    PlaytestLog.Event("Scene", "skip wreck dump (airlock split)");
                else
                {
                    RequestWorldSnapshot();
                    _enemySync.PuppetAllNow();
                }
            }
        }
    }
}
