using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Players;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class LanNetworkManager : INetEventListener
    {
        public static LanNetworkManager Instance { get; private set; }

        private NetManager _net;
        private NetPeer _peer;
        private NetworkRole _role = NetworkRole.Offline;
        private RemotePlayerProxy _remoteProxy;
        private GameObject _localPlayer;
        private GameObject _proxyGameObject;
        private float _sendTimer;
        private Vector3 _lastSentPosition;
        private float _lastStateTime;

        private bool _handshakeComplete;
        private float _lastSendLogTime;
        private float _lastRecvLogTime;
        public NetworkRole Role => _role;
        public bool IsConnected => _peer != null && _peer.ConnectionState == ConnectionState.Connected;
        public string StatusText { get; private set; } = "Offline";
        public RemotePlayerProxy RemoteProxy => _remoteProxy;
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
        }

        public void StartHost(int port)
        {
            StopNetwork();
            _role = NetworkRole.Host;
            _net = new NetManager(this) { UnconnectedMessagesEnabled = true, DisconnectTimeout = 5000 };
            if (!_net.Start(port))
            {
                StatusText = "Failed to bind port " + port;
                _role = NetworkRole.Offline;
                return;
            }
            StatusText = "Hosting on port " + port;
            ModRuntime.Log?.Msg("[Network] Hosting on port " + port);
        }

        public void ConnectToHost(string address, int port)
        {
            StopNetwork();
            _role = NetworkRole.Client;
            _net = new NetManager(this) { UnconnectedMessagesEnabled = true, DisconnectTimeout = 5000 };
            _net.Start();
            _peer = _net.Connect(address, port, "SyncRADation");
            StatusText = "Connecting to " + address + ":" + port;
            ModRuntime.Log?.Msg("[Network] Connecting to " + address + ":" + port);
        }

        public void StopNetwork()
        {
            _localPlayer = null;
            _proxyGameObject = null;
            DestroyRemoteProxy();
            DoorSyncService.Reset();
            SourceAnimReader.Reset();
            _handshakeComplete = false;
            _peer = null;
            _sendTimer = 0f;

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
        }

        public void Update()
        {
            _net?.PollEvents();

            if (!IsConnected || !_handshakeComplete)
                return;

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);

            /* entity sync disabled for alpha - causes visual jitter */
            // if (_role == NetworkRole.Host)
            // {
            //     EntityStateBroadcastService.Tick();
            // }

            DoorSyncService.Tick();

            if (_sendTimer < PluginInfo.SendInterval)
                return;

            _sendTimer = 0f;

            if (_localPlayer == null && PlayerState.player != null)
            {
                _localPlayer = PlayerState.player;
                ModRuntime.Log?.Msg("[DIAG] Initial player: " + _localPlayer.name);
            }

            if (_localPlayer != null && PlayerState.player != null
                && PlayerState.player != _localPlayer && PlayerState.player != _proxyGameObject)
            {
                _localPlayer = PlayerState.player;
                ClientEntityInterpolationService.ResetPlayerState();
                ModRuntime.Log?.Msg("[DIAG] Player changed to: " + _localPlayer.name);
            }

            GameObject player = _localPlayer;
            if (player == null)
                return;

            var pos = player.transform.position;
            Vector3 vel = (pos - _lastSentPosition) / PluginInfo.SendInterval;
            _lastSentPosition = pos;

            var apc = player.GetComponent<AlternatePlayerController>();
            var pc8 = player.GetComponent<PlayerController8>();

            float rotY;
            byte facing;
            if (apc != null)
            {
                facing = (byte)apc.facing;
                rotY = apc.fAngle;
                if (Time.time - _lastSendLogTime > 5f)
                {
                    ModRuntime.Log?.Msg("[SEND] fAngle=" + rotY.ToString("F1") + " pos=" + pos.ToString("F1"));
                    _lastSendLogTime = Time.time;
                }
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
            catch { }

            WeaponType weapon = WeaponType.None;
            try
            {
                var eq = InventoryManager.EquippedWeapon;
                if (eq != null)
                {
                    var pi = eq.parentItem;
                    if (pi != null)
                    {
                switch (pi._item)
                {
                    case Items.itemlist.Pistol: weapon = WeaponType.Pistol; break;
                    case Items.itemlist.Revolver: weapon = WeaponType.Revolver; break;
                    case Items.itemlist.Shotgun: weapon = WeaponType.Shotgun; break;
                    case Items.itemlist.Rifle: weapon = WeaponType.Rifle; break;
                    case Items.itemlist.SMG: weapon = WeaponType.SMG; break;
                    case Items.itemlist.FlareGun: weapon = WeaponType.Flare; break;
                    case Items.itemlist.FlakGun: weapon = WeaponType.CAR; break;
                    case Items.itemlist.Machete: weapon = WeaponType.Melee; break;
                    case Items.itemlist.Taser: weapon = WeaponType.Handgun; break;
                }
                    }
                }
            }
            catch { }

            var msg = new PlayerStateMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotY = rotY,
                RootY = player.transform.eulerAngles.y,
                VelX = vel.x,
                VelZ = vel.z,
                Forward = forwardAmount,
                Turn = turnAmount,
                AimingTime = aimingTime,
                CharState = (byte)PlayerState.charState,
                Facing = facing,
                Weapon = weapon,
                AnimBools = 0
            };

            // Read animator params from source player (fills floats + AnimBools from Animator)
            SourceAnimReader.ReadFromPlayer(player, ref msg);

            // Ensure game state bools override any Animator-derived values
            if (PlayerState.aiming) msg.AnimBools |= AnimBools.Aiming;
            if (PlayerState.shooting) msg.AnimBools |= AnimBools.Shooting;
            if (PlayerState.charState == PlayerState.charStates.run) msg.AnimBools |= AnimBools.Running;
            Send(NetMessageType.PlayerState, w => msg.Serialize(w));

            if (_role == NetworkRole.Host)
                PlayerPositionManager.ReportHostPosition(pos);
        }

        public void LateUpdate()
        {
            if (_remoteProxy != null && _lastStateTime > 0f && Time.time - _lastStateTime > 5f)
            {
                ModRuntime.Log?.Msg("[Net] No state for 5s, destroying proxy");
                DestroyRemoteProxy();
            }

            ClientEntityInterpolationService.TickLateUpdate();
        }

        public void Send(NetMessageType type, Action<NetDataWriter> writeBody, DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (_peer == null) return;
            var writer = new NetDataWriter();
            writer.Put((byte)type);
            writeBody(writer);
            _peer.Send(writer, method);
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            _peer = peer;
            _handshakeComplete = false;
            StatusText = "Peer connected";
            ModRuntime.Log?.Msg("[Network] Peer connected");

            Send(NetMessageType.Handshake, w =>
            {
                new HandshakeMessage { ProtocolVersion = PluginInfo.ProtocolVersion }.Serialize(w);
            });

            if (_role == NetworkRole.Host)
            {
                EntityStateBroadcastService.SetPeer(peer);
            }

            var c = _connected;
            if (c != null)
                c();
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            ModRuntime.Log?.Msg("[Network] Peer disconnected: " + disconnectInfo.Reason);
            StopNetwork();
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            ModRuntime.Log?.Error("[Network] Error: " + socketError);
            StatusText = "Error: " + socketError;
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (!reader.TryGetByte(out byte messageType))
                return;

            var type = (NetMessageType)messageType;

            switch (type)
            {
            case NetMessageType.Handshake:
                HandleHandshake(HandshakeMessage.Deserialize(reader));
                break;
            case NetMessageType.PlayerState:
                HandlePlayerState(PlayerStateMessage.Deserialize(reader));
                break;
            case NetMessageType.DoorState:
                DoorSyncService.HandleMessage(DoorStateMessage.Deserialize(reader));
                break;
            }
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
                request.AcceptIfKey("SyncRADation");
            else
                request.Reject();
        }

        private void HandleHandshake(HandshakeMessage handshake)
        {
            if (handshake.ProtocolVersion != PluginInfo.ProtocolVersion)
            {
                ModRuntime.Log?.Error("[Network] Protocol mismatch: local=" + PluginInfo.ProtocolVersion + " remote=" + handshake.ProtocolVersion);
                _peer?.Disconnect();
                return;
            }
            _handshakeComplete = true;
            StatusText = _role == NetworkRole.Host ? "Client joined" : "Connected to host";
            ModRuntime.Log?.Msg("[Network] Handshake OK");
            _lastStateTime = Time.time;
        }

        private void HandlePlayerState(PlayerStateMessage state)
        {
            EnsureRemoteProxy();
            if (_remoteProxy == null)
            {
                ModRuntime.Log?.Warning("[Net] HandlePlayerState: no proxy, cannot apply state");
                return;
            }

            var go = _remoteProxy.GameObject;
            if (go == null)
            {
                ModRuntime.Log?.Warning("[Net] HandlePlayerState: proxy GameObject destroyed, recreating");
                DestroyRemoteProxy();
                EnsureRemoteProxy();
                if (_remoteProxy == null) return;
                go = _remoteProxy.GameObject;
            }

            var targetPos = new Vector3(state.PosX, state.PosY, state.PosZ);
            _lastStateTime = Time.time;

            ClientEntityInterpolationService.ApplyPlayerState(targetPos, state.RotY);
            _remoteProxy.ApplyState(state);
            if (Time.time - _lastRecvLogTime > 5f)
            {
                ModRuntime.Log?.Msg("[Recv] pos=" + targetPos.ToString("F1") + " rotY=" + state.RotY.ToString("F1"));
                _lastRecvLogTime = Time.time;
            }
        }

        private void EnsureRemoteProxy()
        {
            if (_remoteProxy != null)
                return;

            GameObject clone = null;
            try
            {
                GameObject source = PlayerState.player;
                if (source == null)
                {
                    ModRuntime.Log?.Warning("Cannot spawn remote proxy: no local player.");
                    return;
                }

                _localPlayer = source;
                clone = PlayerProxyBuilder.CreatePlayerClone(source, "RemotePlayer", Vector3.zero, ModRuntime.Log);
                if (clone == null)
                {
                    ModRuntime.Log?.Warning("Failed to create player clone");
                    _localPlayer = null;
                    return;
                }

                _proxyGameObject = clone;
                _remoteProxy = new RemotePlayerProxy(clone);
                ModRuntime.Log?.Msg("Remote proxy spawned");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Error("Failed to spawn remote proxy: " + ex);
                if (clone != null)
                {
                    UnityEngine.Object.Destroy(clone);
                    clone = null;
                }
                _localPlayer = null;
                _remoteProxy = null;
            }
        }

        public void OnSceneChanged()
        {
            _localPlayer = null;
            _lastSentPosition = Vector3.zero;
            SourceAnimReader.Reset();
            _sendTimer = 0f;
            _lastStateTime = 0f;
            DestroyRemoteProxy();
            ClientEntityInterpolationService.Reset();
            DoorSyncService.RefreshScene();
        }

        private void DestroyRemoteProxy()
        {
            if (_remoteProxy != null)
            {
                _remoteProxy.Destroy();
                UnityEngine.Object.Destroy(_remoteProxy.GameObject);
                _remoteProxy = null;
            }
            _proxyGameObject = null;
            ClientEntityInterpolationService.Reset();
        }
    }
}
