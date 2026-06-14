// SyncRADation � LiteNetLib host/client, N-peer management, message routing+relay, state building
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.ItemSystem;
using SyncRADation.Players;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class LanNetworkManager : INetEventListener
    {
        public static LanNetworkManager Instance { get; private set; }

        private NetManager _net;
        private NetworkRole _role = NetworkRole.Offline;
        private int _localPlayerId;
        private int _nextClientId = 1;
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>(); // playerId -> peer
        private readonly Dictionary<NetPeer, int> _peerToId = new Dictionary<NetPeer, int>(); // peer -> playerId
        private readonly PlayerProxyManager _proxyManager = new PlayerProxyManager();
        private readonly EnemySyncService _enemySync = new EnemySyncService();
        private readonly PuzzleSyncService _puzzleSync = new PuzzleSyncService();
        private readonly BossSyncService _bossSync = new BossSyncService();
        private GameObject _localPlayer;
        private float _sendTimer;
        private int _boneSendCounter;
        private Vector3 _lastSentPosition;
        private float _lastStateTime;

        private bool _handshakeComplete;

        private ushort _nextItemIndex = 1;
        public NetworkRole Role => _role;
        public bool IsConnected => _peers.Count > 0 && _handshakeComplete;
        public int LocalPlayerId => _localPlayerId;
        public string StatusText { get; private set; } = "Offline";
        public PlayerProxyManager ProxyManager => _proxyManager;
        public EnemySyncService EnemySync => _enemySync;
        public PuzzleSyncService PuzzleSync => _puzzleSync;
        public BossSyncService BossSync => _bossSync;
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
            _localPlayerId = 0;
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
            var peer = _net.Connect(address, port, "SyncRADation");
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
            SourceAnimReader.Reset();
            DroppedItemManager.SaveToFile();
            DroppedItemManager.ClearAll();
            _handshakeComplete = false;
            _peers.Clear();
            _peerToId.Clear();
            _sendTimer = 0f;
            _nextClientId = 1;

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

            DoorSyncService.Tick();
            _enemySync.TickHost(this);
            _puzzleSync.TickHost(this);
            _bossSync.TickHost(this);

            // Pickup nearby dropped item
            if (Input.GetKeyDown(KeyCode.E) && WorldItem.NearbyID >= 0
                && PlayerState.gameState == PlayerState.gameStates.play)
            {
                int localID = WorldItem.NearbyID;
                WorldItem.NearbyID = -1;
                DoPickupItem(localID);
            }

            if (Input.GetKeyDown(KeyCode.G) && PlayerState.gameState == PlayerState.gameStates.play)
            {
                TryDropCurrentItem();
            }

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < PluginInfo.SendInterval)
                return;
            _sendTimer = 0f;

            if (_localPlayer == null && PlayerState.player != null)
            {
                _localPlayer = PlayerState.player;
                ModRuntime.Log?.Msg("[DIAG] Initial player: " + _localPlayer.name);
            }

            // Send local player state to ALL connected peers
            GameObject player = _localPlayer;
            if (player == null)
                return;

            var msg = BuildPlayerStateMessage(player);
            SendToAll(msg, DeliveryMethod.ReliableOrdered, excludePlayerId: -1); // -1 means send to all

            // Host also needs to relay states it received from clients — but that's handled
            // in OnReceive: the host stores the state and re-sends to all other peers
        }

        public void LateUpdate()
        {
            _proxyManager.LateUpdate();
        }

        private PlayerStateMessage BuildPlayerStateMessage(GameObject player)
        {
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

            var msg = new PlayerStateMessage
            {
                SenderPlayerId = _localPlayerId,
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
                AnimBools = 0
            };

            SourceAnimReader.ReadFromPlayer(player, ref msg);

            _boneSendCounter++;
            if (_boneSendCounter % PluginInfo.BoneSendDivider != 0)
                msg.BoneRotations = null;

            if (PlayerState.aiming) msg.AnimBools |= AnimBools.Aiming;
            if (PlayerState.shooting) msg.AnimBools |= AnimBools.Shooting;
            if (PlayerState.charState == PlayerState.charStates.run) msg.AnimBools |= AnimBools.Running;

            return msg;
        }

        public int GetPlayerCount()
        {
            int count = 1; // local player
            foreach (var kvp in _peers)
            {
                if (kvp.Key != _localPlayerId)
                    count++;
            }
            return count;
        }

        public IEnumerable<int> GetRemotePlayerIds()
        {
            foreach (var kvp in _peers)
            {
                if (kvp.Key != _localPlayerId)
                    yield return kvp.Key;
            }
        }

        public NetPeer GetPeer(int playerId)
        {
            _peers.TryGetValue(playerId, out var peer);
            return peer;
        }

        // Send PlayerStateMessage to ALL peers (for host) or to the single connected peer (for client)
        private void SendToAll(PlayerStateMessage msg, DeliveryMethod method, int excludePlayerId = -1)
        {
            foreach (var kvp in _peers)
            {
                if (kvp.Key == excludePlayerId) continue;
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.PlayerState);
                msg.Serialize(writer);
                peer.Send(writer, method);
            }
        }

        public void SendDoorState(DoorStateMessage msg)
        {
            foreach (var kvp in _peers)
            {
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.DoorState);
                msg.Serialize(writer);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendDropItem(DropItemSpawnMessage msg)
        {
            foreach (var kvp in _peers)
            {
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.DropItemSpawn);
                msg.Serialize(writer);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendItemPickedUp(ItemPickedUpMessage msg)
        {
            foreach (var kvp in _peers)
            {
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.ItemPickedUp);
                msg.Serialize(writer);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendFriendlyFire(int targetPlayerId, float damage, Vector3 hitPos)
        {
            var msg = new FriendlyFireMessage
            {
                TargetPlayerId = targetPlayerId,
                AttackerPlayerId = _localPlayerId,
                Damage = damage,
                HitPosX = hitPos.x,
                HitPosY = hitPos.y,
                HitPosZ = hitPos.z
            };
            foreach (var kvp in _peers)
            {
                if (kvp.Key != targetPlayerId) continue;
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.FriendlyFire);
                msg.Serialize(writer);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
                ModRuntime.Log?.Msg("[FF] Sent damage=" + damage.ToString("F0") + " to player " + targetPlayerId);
            }
        }

        public void SendEnemyState(EnemySnapshotNet[] snaps)
        {
            var msg = new EnemyStateMessage { Enemies = snaps };
            foreach (var kvp in _peers)
            {
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.EnemyState);
                msg.Serialize(writer);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendEnemyDamage(int targetPlayerId, int hostEnemyInstanceID, float damage, bool stagger)
        {
            var msg = new EnemyDamageMessage
            {
                AttackerPlayerId = -1,
                TargetPlayerId = targetPlayerId,
                HostEnemyInstanceID = hostEnemyInstanceID,
                Damage = damage,
                IsStagger = stagger
            };
            foreach (var kvp in _peers)
            {
                if (kvp.Key != targetPlayerId) continue;
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.EnemyDamage);
                msg.Serialize(writer);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendPuzzleState(PuzzleStateEntry[] entries, bool fullRefresh)
        {
            var msg = new PuzzleStateMessage
            {
                SenderPlayerId = _localPlayerId,
                Entries = entries,
                FullRefresh = fullRefresh
            };
            foreach (var kvp in _peers)
            {
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.PuzzleState);
                msg.Serialize(writer);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendBossState(BossSnapshotNet[] snaps)
        {
            var msg = new BossStateMessage { Bosses = snaps };
            foreach (var kvp in _peers)
            {
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.BossState);
                msg.Serialize(writer);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendPlayerShotEnemy(int hostEnemyInstanceID, float damage)
        {
            var msg = new EnemyDamageMessage
            {
                AttackerPlayerId = _localPlayerId,
                TargetPlayerId = -1,
                HostEnemyInstanceID = hostEnemyInstanceID,
                Damage = damage,
                IsStagger = false
            };
            foreach (var kvp in _peers)
            {
                if (kvp.Key != 0) continue; // only send to host
                var peer = kvp.Value;
                if (peer.ConnectionState != ConnectionState.Connected) continue;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.EnemyDamage);
                msg.Serialize(writer);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public ushort AllocateItemIndex()
        {
            return _nextItemIndex++;
        }

        private void TryDropCurrentItem()
        {
            if (_localPlayer == null) return;
            var pos = _localPlayer.transform.position;

            Items.itemlist itemToDrop = Items.itemlist.None;
            int count = 1;

            try
            {
                var current = InventoryManager.CurrentItem;
                if (current != null && current._item != Items.itemlist.None && current._item != Items.itemlist.Injector)
                    itemToDrop = current._item;
            }
            catch { }

            if (itemToDrop == Items.itemlist.None)
            {
                ModRuntime.Log?.Msg("[Drop] No item to drop");
                return;
            }

            try
            {
                var anItem = InventoryManager.getItem(itemToDrop);
                if (anItem == null) return;
                if (InventoryManager.getCount(anItem) <= 0) return;
                InventoryManager.RemoveItem(anItem, count);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] RemoveItem failed: " + ex.Message);
                return;
            }

            ushort idx = _nextItemIndex++;
            int key = (_localPlayerId << 16) | idx;
            DroppedItemManager.SpawnLocalItem(itemToDrop, count, key, pos);

            SendDropItem(new DropItemSpawnMessage
            {
                SenderID = (byte)_localPlayerId,
                LocalIndex = idx,
                ItemEnum = (ushort)itemToDrop,
                Count = count,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z
            });

            ModRuntime.Log?.Msg("[Drop] Dropped " + itemToDrop + " x" + count);
        }

        private void DoPickupItem(int itemKey)
        {
            var go = DroppedItemManager.GetItem(itemKey);
            if (go == null)
            {
                ModRuntime.Log?.Warning("[Pickup] WorldItem not found for key " + itemKey);
                return;
            }
            var wi = go.GetComponent<WorldItem>();
            if (wi == null) return;

            try
            {
                var item = InventoryManager.getItem(wi.ItemEnum);
                if (item == null)
                {
                    ModRuntime.Log?.Warning("[Pickup] Unknown item enum " + wi.ItemEnum);
                    return;
                }
                InventoryManager.AddItem(item, wi.Count);
                ModRuntime.Log?.Msg("[Pickup] Picked up " + wi.ItemEnum + " x" + wi.Count);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Pickup] AddItem failed: " + ex.Message);
                return;
            }

            int senderID = (itemKey >> 16) & 0xFF;
            ushort localIdx = (ushort)(itemKey & 0xFFFF);

            SendItemPickedUp(new ItemPickedUpMessage
            {
                SenderID = (byte)senderID,
                LocalIndex = localIdx,
                ItemEnum = (ushort)wi.ItemEnum,
                Count = wi.Count,
                GrantToReceiver = IsSharedItem(wi.ItemEnum)
            });

            DroppedItemManager.DespawnItem(itemKey);
        }

        private static bool IsSharedItem(Items.itemlist itemEnum)
        {
            try
            {
                var itemData = InventoryManager.getItem(itemEnum);
                if (itemData == null) return false;
                return itemData.type == AnItem.AnItemType.Object;
            }
            catch { return false; }
        }

        // --- Network events ---

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            int playerId;
            if (_role == NetworkRole.Host)
            {
                playerId = _nextClientId++;
                _peers[playerId] = peer;
                _peerToId[peer] = playerId;
                ModRuntime.Log?.Msg("[Network] Client connected, assigned playerId=" + playerId);

                // Send handshake with assigned ID
                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.Handshake);
                new HandshakeMessage { ProtocolVersion = PluginInfo.ProtocolVersion, AssignedPlayerId = playerId }.Serialize(w);
                peer.Send(w, DeliveryMethod.ReliableOrdered);

                // If this is the first client, fire Connected event
                var c = _connected;
                if (c != null) c();
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

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (!reader.TryGetByte(out byte messageType))
                return;

            var type = (NetMessageType)messageType;
            int senderId = _peerToId.TryGetValue(peer, out int id) ? id : -1;

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
            case NetMessageType.DropItemSpawn:
                HandleDropItemSpawn(DropItemSpawnMessage.Deserialize(reader));
                break;
            case NetMessageType.ItemPickedUp:
                HandleItemPickedUp(ItemPickedUpMessage.Deserialize(reader));
                break;
            case NetMessageType.FriendlyFire:
                HandleFriendlyFire(FriendlyFireMessage.Deserialize(reader));
                break;
            case NetMessageType.EnemyState:
                _enemySync.OnEnemyStateReceived(EnemyStateMessage.Deserialize(reader));
                break;
            case NetMessageType.EnemyDamage:
                HandleEnemyDamage(EnemyDamageMessage.Deserialize(reader));
                break;
            case NetMessageType.SceneSync:
                HandleSceneSync(SceneSyncMessage.Deserialize(reader));
                break;
            case NetMessageType.PuzzleState:
                _puzzleSync.ApplyPuzzleState(PuzzleStateMessage.Deserialize(reader));
                break;
            case NetMessageType.BossState:
                _bossSync.OnBossStateReceived(BossStateMessage.Deserialize(reader));
                break;
            default:
                ModRuntime.Log?.Warning("[Network] Unhandled message type: " + type + " (" + messageType + ")");
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
                foreach (var kvp in _peers)
                    kvp.Value.Disconnect();
                return;
            }

            if (_role == NetworkRole.Client)
            {
                // Host tells us our assigned player ID
                _localPlayerId = handshake.AssignedPlayerId;
                ModRuntime.Log?.Msg("[Network] Host assigned playerId=" + _localPlayerId);
            }

            _handshakeComplete = true;
            _lastStateTime = Time.time;
            StatusText = _role == NetworkRole.Host ? "Clients connected" : "Connected to host";
            ModRuntime.Log?.Msg("[Network] Handshake OK, local playerId=" + _localPlayerId);
        }

        private void HandlePlayerState(PlayerStateMessage state)
        {
            int senderId = state.SenderPlayerId;

            // Ensure we have a proxy for this player
            if (!_proxyManager.HasProxy(senderId))
            {
                GameObject source = PlayerState.player;
                if (source == null)
                {
                    ModRuntime.Log?.Warning("[Net] Cannot create proxy: no local player");
                    return;
                }
                _proxyManager.CreateProxy(senderId, source);
            }

            _proxyManager.ApplyState(senderId, state);
            _lastStateTime = Time.time;

            // Host: relay to all OTHER peers (not back to sender)
            if (_role == NetworkRole.Host)
            {
                foreach (var kvp in _peers)
                {
                    if (kvp.Key == senderId) continue;
                    if (kvp.Key == _localPlayerId) continue; // don't send to self
                    var peer = kvp.Value;
                    if (peer.ConnectionState != ConnectionState.Connected) continue;
                    var writer = new NetDataWriter();
                    writer.Put((byte)NetMessageType.PlayerState);
                    state.Serialize(writer);
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        private void HandleDropItemSpawn(DropItemSpawnMessage msg)
        {
            int key = (msg.SenderID << 16) | msg.LocalIndex;
            var pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            DroppedItemManager.SpawnLocalItem((Items.itemlist)msg.ItemEnum, msg.Count, key, pos);
            ModRuntime.Log?.Msg("[Drop] Remote dropped " + (Items.itemlist)msg.ItemEnum);
        }

        private void HandleItemPickedUp(ItemPickedUpMessage msg)
        {
            int key = (msg.SenderID << 16) | msg.LocalIndex;
            DroppedItemManager.DespawnItem(key);

            if (msg.GrantToReceiver)
            {
                try
                {
                    var item = InventoryManager.getItem((Items.itemlist)msg.ItemEnum);
                    if (item != null)
                    {
                        InventoryManager.AddItem(item, msg.Count);
                        ModRuntime.Log?.Msg("[Drop] Shared item granted: " + (Items.itemlist)msg.ItemEnum);
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.Warning("[Drop] Grant shared item failed: " + ex.Message);
                }
            }
        }

        private void HandleFriendlyFire(FriendlyFireMessage msg)
        {
            if (msg.TargetPlayerId == _localPlayerId)
            {
                Vector3 hitPos = new Vector3(msg.HitPosX, msg.HitPosY, msg.HitPosZ);
                ModRuntime.Log?.Msg("[FF] Received damage=" + msg.Damage.ToString("F0") + " from player " + msg.AttackerPlayerId);
                NetworkDamageSystem.ApplyDamage(msg.Damage, hitPos, Vector3.zero);
            }
        }

        private void HandleEnemyDamage(EnemyDamageMessage msg)
        {
            if (msg.TargetPlayerId == _localPlayerId)
            {
                ModRuntime.Log?.Msg("[Enemy] Received damage=" + msg.Damage.ToString("F0") + " from enemy " + msg.HostEnemyInstanceID);
                if (msg.IsStagger)
                    NetworkDamageSystem.ApplyDamage(msg.Damage, Vector3.zero, Vector3.zero);
                else
                    NetworkDamageSystem.ApplyDamage(msg.Damage, Vector3.zero, Vector3.zero);
            }
            else if (msg.AttackerPlayerId != -1 && _role == NetworkRole.Host)
            {
                // Host: a player shot an enemy — modify fields directly (IL2CPP-safe)
                var enemies = GameObject.FindObjectsOfType<EnemyController>();
                foreach (var e in enemies)
                {
                    if (e != null && e.gameObject.GetInstanceID() == msg.HostEnemyInstanceID)
                    {
                        if (e.hitbox != null)
                        {
                            e.hitbox.HP -= (int)msg.Damage;
                            if (e.hitbox.HP <= 0)
                                e.state = EnemyController.enemystate.dead;
                        }
                        ModRuntime.Log?.Msg("[Enemy] Player " + msg.AttackerPlayerId + " damaged enemy " + msg.HostEnemyInstanceID + " HP=" + (e.hitbox != null ? e.hitbox.HP.ToString() : "?"));
                        break;
                    }
                }
            }
        }

        private void HandleSceneSync(SceneSyncMessage msg)
        {
            if (msg.SenderPlayerId != _localPlayerId)
            {
                ModRuntime.Log?.Msg("[Scene] Player " + msg.SenderPlayerId + " is in scene '" + msg.SceneName + "'");
            }
        }

        public void OnSceneChanged()
        {
            _localPlayer = null;
            _lastSentPosition = Vector3.zero;
            SourceAnimReader.Reset();
            _sendTimer = 0f;
            _lastStateTime = 0f;
            _proxyManager.DestroyAll();
            DroppedItemManager.ClearAll();
            DroppedItemManager.LoadFromFile();
            DoorSyncService.RefreshScene();
            _enemySync.OnSceneChanged();
            _puzzleSync.RefreshScene();
            _bossSync.OnSceneChanged();
        }
    }
}
