// SyncRADation � LiteNetLib host/client, N-peer management, message routing+relay, state building
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Cheats;
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
    public sealed class LanNetworkManager : INetEventListener
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
        private int _boneSendCounter;
        private Vector3 _lastSentPosition;
        private float _lastSentTime;
        private float _lastStateTime;

        private bool _handshakeComplete;
        private readonly Dictionary<int, string> _peerScenes = new Dictionary<int, string>();
        private string _hostSceneName = "";
        private string _localSceneName = "";
        private bool _sceneMismatch;

        private ushort _nextItemIndex = 1;
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
            NetworkDamageSystem.Reset();
            DroppedItemManager.ClearAll();
            _handshakeComplete = false;
            _vitalTimer = 0f;
            _peers.Clear();
            _peerToId.Clear();
            _sessionPlayerIds.Clear();
            _peerScenes.Clear();
            _unicastPlayerId = -1;
            _sendTimer = 0f;
            _lastSentTime = 0f;
            _sceneMismatch = false;
            _hostSceneName = "";
            _localSceneName = "";
            _localPlayerId = 0;
            _nextItemIndex = 1;

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
                    SendLocalVital();
                }
            }

            // Pickup nearby dropped item
            DroppedItemManager.TickNearby();
            if (Input.GetKeyDown(KeyCode.E) && DroppedItemManager.NearbyID >= 0
                && PlayerState.gameState == PlayerState.gameStates.play)
            {
                int localID = DroppedItemManager.NearbyID;
                DroppedItemManager.NearbyID = -1;
                Items.itemlist peekItem;
                int peekCount;
                if (DroppedItemManager.TryGet(localID, out peekItem, out peekCount)
                    && !BagHasRoom(peekItem))
                {
                    ModRuntime.Log?.Msg("[Pickup] bag full");
                    return;
                }
                if (_role == NetworkRole.Host)
                    TryClaimDropped(localID, _localPlayerId, out _);
                else
                    SendInteractionRequest(0, InteractionKind.DroppedPickup, localID);
            }

            if (Input.GetKeyDown(KeyCode.G))
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
            SendPlayerState(msg);
            HitchTrace.Send();

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
            float dt = PluginInfo.SendInterval;
            if (_lastSentTime > 0f)
                dt = Mathf.Max(0.016f, Time.time - _lastSentTime);
            _lastSentTime = Time.time;
            Vector3 vel = (pos - _lastSentPosition) / dt;
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

            var euler = player.transform.eulerAngles;
            byte modelState = 0;
            bool wearHat = false;
            try
            {
                var cmt = player.GetComponentInChildren<CharacterModelType>(true);
                if (cmt != null) modelState = (byte)cmt.modelState;
                wearHat = CharacterModelType.wearHat;
            }
            catch { }

            var msg = new PlayerStateMessage
            {
                SenderPlayerId = _localPlayerId,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotY = rotY,
                RootY = euler.y,
                RootX = euler.x,
                RootZ = euler.z,
                VelX = vel.x,
                VelZ = vel.z,
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

            _boneSendCounter++;
            if (_boneSendCounter % PluginInfo.BoneSendDivider != 0)
                msg.BoneRotations = null;

            if (PlayerState.aiming) msg.AnimBools |= AnimBools.Aiming;
            if (PlayerState.charState == PlayerState.charStates.run) msg.AnimBools |= AnimBools.Running;

            return msg;
        }

        /// <summary>Host relays a raw writer to all peers except excludePlayerId.</summary>
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

        private void SendToPlayer(int playerId, NetDataWriter writer, DeliveryMethod method)
        {
            if (_peers.TryGetValue(playerId, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, method);
        }

        private void BroadcastRaw(NetDataWriter writer, DeliveryMethod method)
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

        private void BroadcastRawExcept(NetDataWriter writer, DeliveryMethod method, int exceptPlayerId)
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

        public void BroadcastSceneHello()
        {
            if (_net == null || !_handshakeComplete) return;
            _localSceneName = SceneManager.GetActiveScene().name ?? "";
            if (_role == NetworkRole.Host)
                _hostSceneName = _localSceneName;

            var msg = new SceneHelloMessage
            {
                SenderPlayerId = _localPlayerId,
                SceneName = _localSceneName,
                RoomName = WorldRegistry.GetLocalRoomName()
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.SceneHello);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
            ModRuntime.Log?.Msg("[Scene] Hello sent scene='" + msg.SceneName + "' room='" + msg.RoomName + "'");
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

        // Sequenced MTU is 1020. Pose stays small; bones go in the same packet only if they fit.
        private const int SequencedMtuFallback = 1020;
        private const int BonePoseHeaderBytes = 1 + 4 + 2 + 2 + 2; // type + sender + total + start + count
        private bool _loggedOversizedPose;

        private int SequencedMtu()
        {
            foreach (var kvp in _peers)
            {
                var peer = kvp.Value;
                if (peer != null && peer.ConnectionState == ConnectionState.Connected)
                    return peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced);
            }
            return SequencedMtuFallback;
        }

        private void SendPlayerState(PlayerStateMessage msg)
        {
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

        private void SendBoneChunks(int senderId, float[] eulers, int mtu)
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

        private void SendSequenced(NetDataWriter writer)
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
            foreach (var kvp in _peers)
            {
                var peer = kvp.Value;
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

        public void SendDoorState(DoorStateMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.DoorState);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendDropItem(DropItemSpawnMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.DropItemSpawn);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendItemPickedUp(ItemPickedUpMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.ItemPickedUp);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendFriendlyFire(int targetPlayerId, float damage, Vector3 hitPos)
        {
            if (ModConfig.FriendlyFire?.Value != true) return;

            var msg = new FriendlyFireMessage
            {
                TargetPlayerId = targetPlayerId,
                AttackerPlayerId = _localPlayerId,
                Damage = damage,
                HitPosX = hitPos.x,
                HitPosY = hitPos.y,
                HitPosZ = hitPos.z
            };
            // Send to host if we are client (host relays); host sends direct to target
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.FriendlyFire);
            msg.Serialize(writer);

            if (_role == NetworkRole.Host)
            {
                if (_peers.TryGetValue(targetPlayerId, out var peer)
                    && peer.ConnectionState == ConnectionState.Connected)
                {
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
                    ModRuntime.Log?.Msg("[FF] Host sent dmg=" + damage.ToString("F0") + " to " + targetPlayerId);
                }
            }
            else
            {
                BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
                ModRuntime.Log?.Msg("[FF] Client sent dmg=" + damage.ToString("F0") + " target=" + targetPlayerId);
            }
        }

        public void SendEnemyState(EnemySnapshotNet[] snaps)
        {
            var msg = new EnemyStateMessage { Enemies = snaps };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EnemyState);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendEnemySpawnRequest(string typeKey, Vector3 pos, float rotY)
        {
            var msg = new EnemySpawnMessage
            {
                Seq = 0,
                TypeKey = typeKey ?? "",
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotY = rotY
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EnemySpawn);
            msg.Serialize(writer);
            if (_role == NetworkRole.Host)
                HandleEnemySpawn(msg);
            else if (_peers.TryGetValue(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void BroadcastEnemySpawn(EnemySpawnMessage msg)
        {
            if (_role != NetworkRole.Host) return;
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EnemySpawn);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        private void HandleEnemySpawn(EnemySpawnMessage msg)
        {
            if (_role == NetworkRole.Host)
            {
                if (msg.Seq > 0) return;
                EntitySpawner.FinishSpawn(msg.TypeKey, new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.RotY, 0, true);
                return;
            }
            EntitySpawner.ApplyFromNet(msg);
        }

        public void SendEnemyDamage(int targetPlayerId, ulong enemyWorldId, float damage, bool stagger)
        {
            var msg = new EnemyDamageMessage
            {
                AttackerPlayerId = -1,
                TargetPlayerId = targetPlayerId,
                EnemyWorldId = unchecked((long)enemyWorldId),
                Damage = damage,
                IsStagger = stagger
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EnemyDamage);
            msg.Serialize(writer);
            if (_peers.TryGetValue(targetPlayerId, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendPuzzleState(PuzzleStateEntry[] entries, bool fullRefresh, int exceptPlayerId = -1)
        {
            var msg = new PuzzleStateMessage
            {
                SenderPlayerId = _localPlayerId,
                Entries = entries,
                FullRefresh = fullRefresh
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.PuzzleState);
            msg.Serialize(writer);
            if (exceptPlayerId >= 0)
                BroadcastRawExcept(writer, DeliveryMethod.ReliableOrdered, exceptPlayerId);
            else
                BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendBossState(BossSnapshotNet[] snaps)
        {
            var msg = new BossStateMessage { Bosses = snaps };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.BossState);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendWorldPickupState(WorldPickupEntry[] entries, bool fullRefresh)
        {
            var msg = new WorldPickupStateMessage
            {
                SenderPlayerId = _localPlayerId,
                FullRefresh = fullRefresh,
                Entries = entries
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.WorldPickupState);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        private void SendLocalVital()
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
            catch { }

            var msg = new PlayerVitalMessage
            {
                SenderPlayerId = _localPlayerId,
                Hp = hp,
                MaxHp = maxHp,
                GameState = gameState,
                CharState = charState,
                Dead = dead
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.PlayerVital);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host: dump full world. targetPlayerId &gt;= 0 unicasts (join / client resync); -1 = all peers.</summary>
        public void SendFullWorldSnapshot(int targetPlayerId = -1)
        {
            if (_role != NetworkRole.Host || !_handshakeComplete) return;
            ModRuntime.Log?.Msg("[Network] Sending full world snapshot"
                + (targetPlayerId >= 0 ? " to player " + targetPlayerId : " to all peers"));
            int prevUnicast = _unicastPlayerId;
            _unicastPlayerId = targetPlayerId;
            try
            {
                WorldRegistry.Rebuild();
                DoorSyncService.ForceFullSend();
                _puzzleSync.RequestFullSend();
                _puzzleSync.Tick(this);
                _pickupSync.RequestFullSend();
                _pickupSync.TickHost(this);
                EntitySpawner.DumpLiveSpawns(this);
                _enemySync.RequestFullSend();
                _enemySync.TickHost(this);
                _bossSync.TickHost(this);
                _storySync.RequestFullSend();
                _storySync.Send(this, true, replayPresentation: true);
                _storageSync.RequestSend();
                _storageSync.SendNow(this);
                PartyKeyRing.Broadcast();
                FmodEmitterSync.DumpPlaying();
                if (!SceneFollowService.IsTransient(SceneManager.GetActiveScene().name ?? ""))
                    SendSceneFollow(SceneManager.GetActiveScene().name ?? "", false);
                BroadcastSceneHello();
            }
            finally
            {
                _unicastPlayerId = prevUnicast;
            }
        }

        public void RequestWorldSnapshot()
        {
            if (_role != NetworkRole.Client || !_handshakeComplete) return;
            var msg = new SnapshotRequestMessage { SenderPlayerId = _localPlayerId };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.SnapshotRequest);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
            ModRuntime.Log?.Msg("[Network] Snapshot request sent to host");
        }

        /// <summary>Client → host: real TakeDamage chances from PlayerAttack / EnemyController patch.</summary>
        public void SendNativeEnemyHit(ulong enemyWorldId, float fire, float crit, float hurt, bool noSneak)
        {
            var msg = new EnemyDamageMessage
            {
                AttackerPlayerId = _localPlayerId,
                TargetPlayerId = -1,
                EnemyWorldId = unchecked((long)enemyWorldId),
                Damage = 0f,
                IsStagger = false,
                NativeTakeDamage = true,
                FireChance = fire,
                CriticalChance = crit,
                HurtChance = hurt,
                NoSneak = noSneak
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EnemyDamage);
            msg.Serialize(writer);

            if (_role == NetworkRole.Host)
            {
                _enemySync.ApplyNativeTakeDamageOnHost(enemyWorldId, fire, crit, hurt, noSneak);
            }
            else if (_peers.TryGetValue(0, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
            {
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendWorldPickupClaim(ulong worldId, Items.itemlist item = Items.itemlist.None, int count = 1)
        {
            var msg = new WorldPickupClaimMessage
            {
                ClaimerPlayerId = _localPlayerId,
                WorldId = unchecked((long)worldId),
                ItemEnum = (ushort)item,
                Count = count > 0 ? count : 1
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.WorldPickupClaim);
            msg.Serialize(writer);
            // Client → host only
            if (_peers.TryGetValue(0, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendWorldPickupGrant(int targetPlayerId, ulong worldId, Items.itemlist item, int count)
        {
            var msg = new WorldPickupGrantMessage
            {
                TargetPlayerId = targetPlayerId,
                WorldId = unchecked((long)worldId),
                ItemEnum = (ushort)item,
                Count = count
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.WorldPickupGrant);
            msg.Serialize(writer);
            if (_peers.TryGetValue(targetPlayerId, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            // Host also grants if target is host
            if (targetPlayerId == _localPlayerId)
                _pickupSync.ApplyGrant(msg);
        }

        public void SendSceneFollow(string sceneName, bool isRequest)
        {
            var msg = new SceneFollowMessage
            {
                SenderPlayerId = _localPlayerId,
                SceneName = sceneName ?? "",
                IsRequest = isRequest
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.SceneFollow);
            msg.Serialize(writer);
            if (isRequest)
            {
                if (_peers.TryGetValue(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
            else
                BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendInteractionRequest(ulong worldId, InteractionKind kind, int int0 = 0, int int1 = 0,
            float f0 = 0f, float f1 = 0f, float f2 = 0f, string text = "")
        {
            var msg = new InteractionRequestMessage
            {
                SenderPlayerId = _localPlayerId,
                WorldId = unchecked((long)worldId),
                Kind = kind,
                Int0 = int0,
                Int1 = int1,
                Float0 = f0,
                Float1 = f1,
                Float2 = f2,
                Text = text ?? ""
            };
            if (kind != InteractionKind.Gunshot)
                PlaytestLog.Event("Interact", "request " + kind + " id=" + worldId.ToString("X16"));
            if (_role == NetworkRole.Host)
            {
                InteractionSyncService.HandleRequest(msg);
                return;
            }
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.InteractionRequest);
            msg.Serialize(writer);
            if (_peers.TryGetValue(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendInteractionAck(int targetPlayerId, long worldId, InteractionKind kind, bool ok, string reason)
        {
            var msg = new InteractionAckMessage
            {
                TargetPlayerId = targetPlayerId,
                WorldId = worldId,
                Kind = kind,
                Ok = ok,
                Reason = reason ?? ""
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.InteractionAck);
            msg.Serialize(writer);
            if (targetPlayerId == _localPlayerId) return;
            if (_peers.TryGetValue(targetPlayerId, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendStoryCommit(StoryCommitMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.StoryCommit);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendStoryPresentation(StoryPresentationMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.StoryPresentation);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendStorageBoxBlob(StorageBoxItem[] items)
        {
            var msg = new StorageBoxBlobMessage { Items = items };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.StorageBoxBlob);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendPartyKeyRing(ushort[] enums)
        {
            var msg = new PartyKeyRingMessage { ItemEnums = enums };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.PartyKeyRing);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendDeathPolicy(DeathKind kind)
        {
            var msg = new DeathPolicyMessage { SenderPlayerId = _localPlayerId, Kind = kind };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.DeathPolicy);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendFmodEmitter(FmodEmitterMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.FmodEmitter);
            msg.Serialize(writer);
            BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        public ushort AllocateItemIndex()
        {
            return _nextItemIndex++;
        }

        private void TryDropCurrentItem()
        {
            PlayerState.gameStates gs;
            try { gs = PlayerState.gameState; }
            catch { return; }
            if (gs != PlayerState.gameStates.play && gs != PlayerState.gameStates.inventory)
            {
                ModRuntime.Log?.Msg("[Drop] skip state=" + gs);
                return;
            }

            if (_localPlayer == null) _localPlayer = PlayerState.player;
            if (_localPlayer == null)
            {
                ModRuntime.Log?.Msg("[Drop] No local player");
                return;
            }
            var pos = _localPlayer.transform.position;

            AnItem anItem = null;
            Items.itemlist itemToDrop = Items.itemlist.None;
            int count = 0;

            try
            {
                anItem = InventoryManager.CurrentItem;
                if (anItem != null) itemToDrop = anItem._item;
            }
            catch { }

            if (itemToDrop == Items.itemlist.None || itemToDrop == Items.itemlist.Injector)
            {
                try
                {
                    var tool = InventoryManager.EquippedTool;
                    if (tool != null && tool._item != Items.itemlist.None && tool._item != Items.itemlist.Injector)
                    {
                        anItem = tool;
                        itemToDrop = tool._item;
                    }
                }
                catch { }
            }

            if (itemToDrop == Items.itemlist.None || itemToDrop == Items.itemlist.Injector)
            {
                try
                {
                    var w = InventoryManager.EquippedWeapon;
                    if (w != null && w.parentItem != null)
                    {
                        anItem = w.parentItem;
                        itemToDrop = anItem._item;
                    }
                }
                catch { }
            }

            if (itemToDrop == Items.itemlist.None || itemToDrop == Items.itemlist.Injector)
            {
                ModRuntime.Log?.Msg("[Drop] No item to drop (select a slot, then G)");
                return;
            }

            try
            {
                if (anItem == null) anItem = InventoryManager.getItem(itemToDrop);
                if (anItem == null)
                {
                    ModRuntime.Log?.Msg("[Drop] getItem failed " + itemToDrop);
                    return;
                }
                int have = InventoryManager.getCount(anItem);
                if (have <= 0)
                {
                    ModRuntime.Log?.Msg("[Drop] count 0 for " + itemToDrop);
                    return;
                }
                count = have;
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

        public bool TryClaimDropped(int itemKey, int claimerId, out string reason)
        {
            reason = "";
            var go = DroppedItemManager.GetItem(itemKey);
            if (go == null) return false;
            Items.itemlist itemEnum;
            int storedCount;
            if (!DroppedItemManager.TryGet(itemKey, out itemEnum, out storedCount))
                return false;

            var item = InventoryManager.getItem(itemEnum);
            if (item == null) return false;
            int count = storedCount;
            bool shared = IsSharedItem(itemEnum);

            int senderID = (itemKey >> 16) & 0xFF;
            ushort localIdx = (ushort)(itemKey & 0xFFFF);

            SendItemPickedUp(new ItemPickedUpMessage
            {
                SenderID = (byte)senderID,
                LocalIndex = localIdx,
                ItemEnum = (ushort)itemEnum,
                Count = count,
                GrantToReceiver = shared
            });

            DroppedItemManager.DespawnItem(itemKey);

            if (shared)
            {
                PartyKeyRing.Note(item);
                PartyKeyRing.Broadcast();
                return true;
            }

            if (claimerId == _localPlayerId)
            {
                try { InventoryManager.AddItem(item, count); }
                catch (Exception ex)
                {
                    ModRuntime.Log?.Warning("[Pickup] AddItem failed: " + ex.Message);
                    return false;
                }
                PartyKeyRing.Note(item);
                PartyKeyRing.Broadcast();
            }
            else
                reason = "grant:" + (int)itemEnum + ":" + count;

            ModRuntime.Log?.Msg("[Pickup] Claimed " + itemEnum + " x" + count + " by " + claimerId);
            return true;
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

        static bool BagHasRoom(Items.itemlist itemEnum)
        {
            try
            {
                var item = InventoryManager.getItem(itemEnum);
                if (item != null && PartyKeyRing.InLocalBag(item)) return true;
                int used = 0;
                var dict = InventoryManager.elsterItems;
                if (dict == null) return true;
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                {
                    if (en.Current.key != null && en.Current.value > 0)
                        used++;
                }
                en.Dispose();
                int max = InventoryManager.maxSlots;
                if (max <= 0) max = 6;
                return used < max;
            }
            catch { return true; }
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

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (!reader.TryGetByte(out byte messageType))
                return;

            var type = (NetMessageType)messageType;
            int senderId = _peerToId.TryGetValue(peer, out int id) ? id : -1;

            switch (type)
            {
            case NetMessageType.Handshake:
                HandleHandshake(HandshakeMessage.Deserialize(reader), senderId);
                break;
            case NetMessageType.PlayerState:
                HandlePlayerState(PlayerStateMessage.Deserialize(reader), senderId);
                break;
            case NetMessageType.BonePose:
                HandleBonePose(BonePoseMessage.Deserialize(reader), senderId);
                break;
            case NetMessageType.DoorState:
            {
                var doorMsg = DoorStateMessage.Deserialize(reader);
                DoorSyncService.HandleMessage(doorMsg);
                if (_role == NetworkRole.Host)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)NetMessageType.DoorState);
                    doorMsg.Serialize(w);
                    RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
                }
                break;
            }
            case NetMessageType.DropItemSpawn:
            {
                var dropMsg = DropItemSpawnMessage.Deserialize(reader);
                HandleDropItemSpawn(dropMsg);
                if (_role == NetworkRole.Host)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)NetMessageType.DropItemSpawn);
                    dropMsg.Serialize(w);
                    RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
                }
                break;
            }
            case NetMessageType.ItemPickedUp:
            {
                var pickMsg = ItemPickedUpMessage.Deserialize(reader);
                HandleItemPickedUp(pickMsg);
                if (_role == NetworkRole.Host)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)NetMessageType.ItemPickedUp);
                    pickMsg.Serialize(w);
                    RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
                }
                break;
            }
            case NetMessageType.FriendlyFire:
            {
                var ff = FriendlyFireMessage.Deserialize(reader);
                if (_role == NetworkRole.Host && ff.TargetPlayerId != _localPlayerId)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)NetMessageType.FriendlyFire);
                    ff.Serialize(w);
                    if (_peers.TryGetValue(ff.TargetPlayerId, out var tpeer)
                        && tpeer.ConnectionState == ConnectionState.Connected)
                        tpeer.Send(w, DeliveryMethod.ReliableOrdered);
                }
                HandleFriendlyFire(ff);
                break;
            }
            case NetMessageType.EnemyState:
                _enemySync.OnEnemyStateReceived(EnemyStateMessage.Deserialize(reader));
                break;
            case NetMessageType.EnemySpawn:
                HandleEnemySpawn(EnemySpawnMessage.Deserialize(reader));
                break;
            case NetMessageType.EnemyDamage:
                HandleEnemyDamage(EnemyDamageMessage.Deserialize(reader));
                break;
            case NetMessageType.SceneHello:
                HandleSceneHello(SceneHelloMessage.Deserialize(reader));
                break;
            case NetMessageType.PuzzleState:
                if (ModConfig.PuzzlesEnabled)
                    _puzzleSync.ApplyPuzzleState(PuzzleStateMessage.Deserialize(reader));
                break;
            case NetMessageType.BossState:
                _bossSync.OnBossStateReceived(BossStateMessage.Deserialize(reader));
                break;
            case NetMessageType.SnapshotRequest:
            {
                var req = SnapshotRequestMessage.Deserialize(reader);
                if (_role == NetworkRole.Host)
                {
                    int target = req.SenderPlayerId;
                    if (target < 1 || !_peers.ContainsKey(target))
                        target = senderId;
                    ModRuntime.Log?.Msg("[Network] Snapshot requested by player " + target);
                    SendFullWorldSnapshot(target);
                }
                break;
            }
            case NetMessageType.WorldPickupState:
            {
                if (_role == NetworkRole.Host)
                    break;
                var pickMsg = WorldPickupStateMessage.Deserialize(reader);
                _pickupSync.ApplyHide(pickMsg);
                break;
            }
            case NetMessageType.WorldPickupClaim:
            {
                var claim = WorldPickupClaimMessage.Deserialize(reader);
                if (_role == NetworkRole.Host)
                    HandleWorldPickupClaim(claim);
                break;
            }
            case NetMessageType.WorldPickupGrant:
            {
                var grant = WorldPickupGrantMessage.Deserialize(reader);
                _pickupSync.ApplyGrant(grant);
                break;
            }
            case NetMessageType.PlayerVital:
            {
                var vital = PlayerVitalMessage.Deserialize(reader);
                HandlePlayerVital(vital);
                if (_role == NetworkRole.Host)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)NetMessageType.PlayerVital);
                    vital.Serialize(w);
                    RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
                }
                break;
            }
            case NetMessageType.SceneFollow:
                SceneFollowService.HandleMessage(SceneFollowMessage.Deserialize(reader));
                break;
            case NetMessageType.InteractionRequest:
                if (_role == NetworkRole.Host)
                    InteractionSyncService.HandleRequest(InteractionRequestMessage.Deserialize(reader));
                break;
            case NetMessageType.InteractionAck:
            {
                var ack = InteractionAckMessage.Deserialize(reader);
                HandleInteractionAck(ack);
                break;
            }
            case NetMessageType.StoryCommit:
                _storySync.ApplyCommit(StoryCommitMessage.Deserialize(reader));
                break;
            case NetMessageType.StoryPresentation:
                _storySync.ApplyPresentation(StoryPresentationMessage.Deserialize(reader));
                break;
            case NetMessageType.StorageBoxBlob:
                _storageSync.Apply(StorageBoxBlobMessage.Deserialize(reader));
                break;
            case NetMessageType.PartyKeyRing:
                PartyKeyRing.ApplyMessage(PartyKeyRingMessage.Deserialize(reader));
                break;
            case NetMessageType.DeathPolicy:
                NetworkDamageSystem.HandleDeathPolicy(DeathPolicyMessage.Deserialize(reader));
                break;
            case NetMessageType.FmodEmitter:
                FmodEmitterSync.Handle(FmodEmitterMessage.Deserialize(reader));
                break;
            case NetMessageType.PlayerRoster:
                HandlePlayerRoster(PlayerRosterMessage.Deserialize(reader));
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
                request.AcceptIfKey(PluginInfo.ConnectionKey);
            else
                request.Reject();
        }

        private void HandleInteractionAck(InteractionAckMessage ack)
        {
            if (ack.TargetPlayerId != _localPlayerId) return;

            if (!ack.Ok)
            {
                ModRuntime.Log?.Msg("[Interact] rejected " + ack.Kind
                    + (string.IsNullOrEmpty(ack.Reason) ? "" : ": " + ack.Reason));
                return;
            }
            PlaytestLog.Event("Interact", "ack " + ack.Kind);

            if (string.IsNullOrEmpty(ack.Reason)) return;
            try
            {
                ApplyBagAck(ack.Reason);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Interact] bag ack: " + ex.Message);
            }
        }

        private static void ApplyBagAck(string reason)
        {
            bool consume = reason.StartsWith("consume:");
            bool grant = reason.StartsWith("grant:");
            if (!consume && !grant) return;
            string rest = reason.Substring(consume ? 8 : 6);
            string[] groups = rest.Split('|');
            for (int g = 0; g < groups.Length; g++)
                ApplyBagAckPart(consume, groups[g]);
        }

        private static void ApplyBagAckPart(bool consume, string rest)
        {
            string[] parts = rest.Split(':');
            if (parts.Length < 1) return;
            int enumVal;
            if (!int.TryParse(parts[0], out enumVal)) return;
            int count = 1;
            if (parts.Length >= 2)
                int.TryParse(parts[1], out count);
            if (count < 1) count = 1;
            var item = InventoryManager.getItem((Items.itemlist)enumVal);
            if (item == null) return;
            if (consume) InventoryManager.RemoveItem(item, count);
            else
            {
                InventoryManager.AddItem(item, count);
                PartyKeyRing.OfferToHost(item);
            }
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

            var stale = new List<int>();
            foreach (int pid in _proxyManager.GetProxyPlayerIds())
            {
                if (!_sessionPlayerIds.Contains(pid))
                    stale.Add(pid);
            }
            for (int i = 0; i < stale.Count; i++)
                _proxyManager.DestroyProxy(stale[i]);

            StatusText = "Connected (" + GetPlayerCount() + " players)";
            ModRuntime.Log?.Msg("[Network] Roster applied, players=" + GetPlayerCount());
        }

        private void HandlePlayerVital(PlayerVitalMessage msg)
        {
            if (msg.SenderPlayerId == _localPlayerId) return;
            var proxy = _proxyManager.GetProxy(msg.SenderPlayerId);
            if (proxy == null) return;
            try
            {
                proxy.SetVital(msg.Hp, msg.MaxHp, msg.Dead, msg.GameState, msg.CharState);
            }
            catch { }
        }

        private void HandlePlayerState(PlayerStateMessage state, int peerId)
        {
            // Host: identity is the LiteNetLib peer map, not the wire field.
            // Client: all packets come from peer 0 (host); keep the stamped SenderPlayerId.
            if (_role == NetworkRole.Host && peerId >= 0)
                state.SenderPlayerId = peerId;
            int senderId = state.SenderPlayerId;
            if (senderId == _localPlayerId) return;

            if (_sceneMismatch)
                return;

            if (!_proxyManager.HasProxy(senderId))
            {
                GameObject source = PlayerState.player;
                if (source == null)
                {
                    ModRuntime.Log?.Warning("[Net] Cannot create proxy: no local player");
                    return;
                }
                try
                {
                    if (PlayerState.gameState != PlayerState.gameStates.play
                        && PlayerState.gameState != PlayerState.gameStates.traversing)
                    {
                        // Still allow proxy in most interactive states
                    }
                }
                catch { }
                _proxyManager.CreateProxy(senderId, source);
            }

            _proxyManager.ApplyState(senderId, state);
            _lastStateTime = Time.time;

            if (_role == NetworkRole.Host)
            {
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.PlayerState);
                state.Serialize(writer);
                RelayRaw(writer, DeliveryMethod.Sequenced, senderId);
            }
        }

        private void HandleBonePose(BonePoseMessage msg, int peerId)
        {
            if (_role == NetworkRole.Host && peerId >= 0)
                msg.SenderPlayerId = peerId;
            int senderId = msg.SenderPlayerId;
            if (senderId == _localPlayerId) return;
            if (_sceneMismatch) return;

            var proxy = _proxyManager.GetProxy(senderId);
            if (proxy != null && proxy.AnimDriver != null)
                proxy.AnimDriver.ApplyBoneChunk(msg.TotalBones, msg.StartBone, msg.Eulers);

            if (_role == NetworkRole.Host)
            {
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.BonePose);
                msg.Serialize(writer);
                RelayRaw(writer, DeliveryMethod.Sequenced, senderId);
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
                        PartyKeyRing.OfferToHost(item);
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
            if (ModConfig.FriendlyFire?.Value != true) return;
            if (msg.TargetPlayerId == _localPlayerId)
            {
                Vector3 hitPos = new Vector3(msg.HitPosX, msg.HitPosY, msg.HitPosZ);
                ModRuntime.Log?.Msg("[FF] Received damage=" + msg.Damage.ToString("F0") + " from player " + msg.AttackerPlayerId);
                NetworkDamageSystem.ApplyDamage(msg.Damage, hitPos, Vector3.zero);
            }
        }

        private void HandleEnemyDamage(EnemyDamageMessage msg)
        {
            ulong enemyId = unchecked((ulong)msg.EnemyWorldId);

            if (msg.TargetPlayerId == _localPlayerId)
            {
                ModRuntime.Log?.Msg("[Enemy] Received damage=" + msg.Damage.ToString("F0")
                    + " from enemy " + enemyId.ToString("X16"));
                NetworkDamageSystem.ApplyDamage(msg.Damage, Vector3.zero, Vector3.zero);
            }
            else if (msg.AttackerPlayerId >= 0 && msg.TargetPlayerId < 0 && _role == NetworkRole.Host)
            {
                if (msg.NativeTakeDamage)
                    _enemySync.ApplyNativeTakeDamageOnHost(enemyId, msg.FireChance, msg.CriticalChance, msg.HurtChance, msg.NoSneak);
                else
                    _enemySync.ApplyDamageOnHost(enemyId, msg.Damage);
            }
        }

        private void HandleWorldPickupClaim(WorldPickupClaimMessage claim)
        {
            ulong id = unchecked((ulong)claim.WorldId);
            Items.itemlist item;
            int count;
            if (!_pickupSync.TryClaimOnHost(id, claim.ClaimerPlayerId, out item, out count, hideNow: true,
                    hintItem: (Items.itemlist)claim.ItemEnum, hintCount: claim.Count))
            {
                ModRuntime.Log?.Msg("[WorldPickup] Claim denied id=" + id.ToString("X16")
                    + " by " + claim.ClaimerPlayerId);
                return;
            }

            ModRuntime.Log?.Msg("[WorldPickup] Claim OK id=" + id.ToString("X16")
                + " item=" + item + " x" + count + " → player " + claim.ClaimerPlayerId);

            PartyKeyRing.Note(item);
            PartyKeyRing.Broadcast();

            if (claim.ClaimerPlayerId == _localPlayerId)
            {
                try
                {
                    var an = InventoryManager.getItem(item);
                    if (an != null) InventoryManager.AddItem(an, count > 0 ? count : 1);
                }
                catch { }
            }
            else if (item != Items.itemlist.None)
            {
                SendWorldPickupGrant(claim.ClaimerPlayerId, id, item, count > 0 ? count : 1);
            }

            _pickupSync.BroadcastTriggered(id, true);
            _pickupSync.HideClaimed(null);
        }

        private void HandleSceneHello(SceneHelloMessage msg)
        {
            if (SceneFollowService.IsTransient(msg.SceneName))
            {
                PlaytestLog.Event("Scene", "Peer " + msg.SenderPlayerId + " still loading");
                return;
            }

            _peerScenes[msg.SenderPlayerId] = msg.SceneName ?? "";
            if (msg.SenderPlayerId == 0 || _role == NetworkRole.Client)
            {
                if (msg.SenderPlayerId == 0)
                    _hostSceneName = msg.SceneName ?? "";
            }

            _localSceneName = SceneManager.GetActiveScene().name ?? "";
            string compareTo = !string.IsNullOrEmpty(_hostSceneName) ? _hostSceneName : msg.SceneName;
            bool hostTransient = SceneFollowService.IsTransient(compareTo);
            bool localTransient = SceneFollowService.IsTransient(_localSceneName);
            _sceneMismatch = !hostTransient && !localTransient
                && !string.IsNullOrEmpty(compareTo)
                && !string.IsNullOrEmpty(_localSceneName)
                && !string.Equals(compareTo, _localSceneName, StringComparison.Ordinal);

            if (_role == NetworkRole.Client && !hostTransient && !string.IsNullOrEmpty(compareTo)
                && (_sceneMismatch || localTransient))
            {
                if (_sceneMismatch && AirlockCinematic.ShouldIgnoreHostFollow(compareTo))
                {
                    PlaytestLog.Event("Scene", "defer airlock follow host='" + compareTo
                        + "' local='" + _localSceneName + "'");
                    return;
                }
                if (_sceneMismatch)
                {
                    StatusText = "Following host scene '" + compareTo + "'";
                    ModRuntime.Log?.Warning("[Scene] MISMATCH local='" + _localSceneName + "' host='" + compareTo
                        + "' — following host");
                }
                SceneFollowService.Apply(compareTo);
            }
            else if (!_sceneMismatch)
            {
                ModRuntime.Log?.Msg("[Scene] Peer " + msg.SenderPlayerId + " scene='" + msg.SceneName
                    + "' room='" + msg.RoomName + "' OK");
            }
        }

        public void OnSceneChanged()
        {
            _localPlayer = null;
            _lastSentPosition = Vector3.zero;
            _lastSentTime = 0f;
            SourceAnimReader.Reset();
            HitchTrace.Reset();
            _sendTimer = 0f;
            _lastStateTime = 0f;
            _proxyManager.DestroyAll();
            DroppedItemManager.ClearAll();
            WorldRegistry.Rebuild();
            DoorSyncService.RefreshScene();
            FmodEmitterSync.Reset();
            _enemySync.OnSceneChanged();
            _puzzleSync.RefreshScene();
            _bossSync.OnSceneChanged();
            _pickupSync.RefreshScene();
            _storySync.RequestFullSend();
            Patches.EventZonePatch.OnSceneChanged();
            _sceneMismatch = false;
            if (_handshakeComplete)
            {
                if (SceneFollowService.LocalIsTransient())
                {
                    PlaytestLog.Event("Scene", "skip hello/dump (loading)");
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
