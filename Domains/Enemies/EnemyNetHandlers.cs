using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Cheats;
using SyncRADation.Config;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>Enemy state / spawn / damage send + apply (thin over EnemySyncService).</summary>
    internal sealed class EnemyNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal EnemyNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        // Persistent on purpose: serialized size of an empty snapshot struct, a pure cache that never depends on session state.
        // persistent: serialized size of an empty snapshot (pure cache)
        private static int _snapBytes;
        // Snapshot packets are built one at a time and copied by LiteNetLib on send: one writer serves them all.
        private readonly NetDataWriter _stateWriter = new NetDataWriter();

        static int SnapshotBytes()
        {
            if (_snapBytes == 0)
            {
                var probe = new NetDataWriter();
                new EnemySnapshotNet().Serialize(probe);
                _snapBytes = probe.Length;
            }
            return _snapBytes;
        }

        /// <summary>
        /// Continuous snapshot: Sequenced on the State channel, chunked to one packet each (Sequenced cannot
        /// fragment), so a stall here never blocks the ReliableOrdered event stream. Full list re-sent at 15 Hz.
        /// </summary>
        internal void SendEnemyState(IList<EnemySnapshotNet> snaps)
        {
            if (snaps == null || snaps.Count == 0) return;
            int perPacket = (_net.StatePacketBudget() - 1 - 4) / SnapshotBytes();
            if (perPacket < 1) perPacket = 1;
            if (perPacket > NetWire.MaxEnemies) perPacket = NetWire.MaxEnemies;
            int total = NetWire.ClampCount(snaps.Count, 4096, "EnemyState send");
            for (int start = 0; start < total; start += perPacket)
            {
                int n = total - start;
                if (n > perPacket) n = perPacket;
                var writer = _stateWriter;
                writer.Reset();
                writer.Put((byte)NetMessageType.EnemyState);
                writer.Put(n);
                for (int i = 0; i < n; i++)
                    snaps[start + i].Serialize(writer);
                _net.BroadcastState(writer);
            }
        }

        internal void SendEnemySpawnRequest(string typeKey, Vector3 pos, float rotY)
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
            if (NetGate.HostRole)
                EntitySpawner.FinishSpawn(msg.TypeKey, new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.RotY, 0, true);
            else if (_net.TryGetPeer(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void BroadcastEnemySpawn(EnemySpawnMessage msg)
        {
            if (!NetGate.HostRole) return;
            // Host just spawned/adopted an enemy (spawner child, F11): its hurtboxes join the damage scan now.
            ClientDamageService.NoteSpawn();
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EnemySpawn);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleEnemySpawn(EnemySpawnMessage msg)
        {
            if (NetGate.HostRole)
            {
                if (msg.Seq > 0) return;
                // A client's F11 spawns for the whole party: only when the host allows client cheats.
                if (ModConfig.AllowClientCheats?.Value != true)
                {
                    PlaytestLog.Warn("Spawn", "rejected client spawn " + msg.TypeKey + " (AllowClientCheats off)");
                    return;
                }
                EntitySpawner.FinishSpawn(msg.TypeKey, new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.RotY, 0, true);
                ClientDamageService.NoteSpawn();
                return;
            }
            EntitySpawner.ApplyFromNet(msg);
        }

        internal void HandleEnemyState(EnemyStateMessage msg)
        {
            _net.EnemySync.OnEnemyStateReceived(msg);
        }

        internal void SendEnemyDamage(int targetPlayerId, ulong enemyWorldId, float damage, bool stagger)
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
            if (_net.TryGetPeer(targetPlayerId, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Client → host: a local hit on a puppet (HP the client's PlayerAttack took off + TakeDamage chances).</summary>
        internal void SendNativeEnemyHit(ulong enemyWorldId, int damage, float fire, float crit, float hurt, bool noSneak)
        {
            if (!NetGate.ClientRole) return;
            var msg = new EnemyDamageMessage
            {
                AttackerPlayerId = _net.LocalPlayerId,
                TargetPlayerId = -1,
                EnemyWorldId = unchecked((long)enemyWorldId),
                Damage = damage,
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
            if (_net.TryGetPeer(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Client → host: stomp/push/burn/wake side effects the puppeted client cannot run itself.</summary>
        internal void SendEnemyAction(ulong enemyWorldId, EnemyActionKind action)
        {
            if (!NetGate.ClientRole) return;
            var msg = new EnemyActionMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                EnemyWorldId = unchecked((long)enemyWorldId),
                Action = action
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EnemyAction);
            msg.Serialize(writer);
            if (_net.TryGetPeer(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleEnemyAction(EnemyActionMessage msg, int senderId)
        {
            if (!NetGate.HostRole) return;
            _net.EnemySync.ApplyActionOnHost(unchecked((ulong)msg.EnemyWorldId), msg.Action, senderId);
        }

        internal void HandleEnemyDamage(EnemyDamageMessage msg)
        {
            ulong enemyId = unchecked((ulong)msg.EnemyWorldId);

            // Host receives only client-originated EnemyDamage (AttackerPlayerId stamped from the peer map by dispatch).
            // A client may hit enemies (TargetPlayerId < 0). No client sends EnemyDamage at a player: player->player
            // damage is the FriendlyFire message (opt-in, clamped in CombatNetHandlers). Refuse any such packet.
            // Enemy->player damage the host itself authors is sent host->client and handled on the client below.
            if (NetGate.HostRole && msg.TargetPlayerId >= 0)
            {
                PlaytestLog.Warn("Damage", "rejected client damage to player " + msg.TargetPlayerId
                    + " from " + msg.AttackerPlayerId);
                return;
            }

            if (msg.TargetPlayerId == _net.LocalPlayerId)
            {
                PlaytestLog.Verbose("Enemy", "dmg=" + msg.Damage.ToString("F0")
                    + " from " + enemyId.ToString("X16"));
                NetworkDamageSystem.ApplyDamage(msg.Damage);
            }
            else if (msg.AttackerPlayerId >= 0 && msg.TargetPlayerId < 0 && NetGate.HostRole
                && msg.NativeTakeDamage)
            {
                // Damage = HP the client's own PlayerAttack took off its puppet (native mode only).
                if (!_net.EnemySync.ApplyNativeTakeDamageOnHost(enemyId, msg.Damage, msg.FireChance, msg.CriticalChance, msg.HurtChance, msg.NoSneak))
                    PlaytestLog.Event("Damage", "client hit miss id=" + enemyId.ToString("X16")
                        + " from=" + msg.AttackerPlayerId);
            }
        }
    }
}
