using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Cheats;
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

        internal void SendEnemyState(IList<EnemySnapshotNet> snaps)
        {
            if (snaps == null || snaps.Count == 0) return;
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EnemyState);
            writer.Put(snaps.Count);
            for (int i = 0; i < snaps.Count; i++)
                snaps[i].Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
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
            if (_net.Role == NetworkRole.Host)
                HandleEnemySpawn(msg);
            else if (_net.TryGetPeer(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void BroadcastEnemySpawn(EnemySpawnMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EnemySpawn);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleEnemySpawn(EnemySpawnMessage msg)
        {
            if (_net.Role == NetworkRole.Host)
            {
                if (msg.Seq > 0) return;
                EntitySpawner.FinishSpawn(msg.TypeKey, new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.RotY, 0, true);
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

        internal void SendNativeEnemyHit(ulong enemyWorldId, float fire, float crit, float hurt, bool noSneak)
        {
            var msg = new EnemyDamageMessage
            {
                AttackerPlayerId = _net.LocalPlayerId,
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

            if (_net.Role == NetworkRole.Host)
            {
                if (!_net.EnemySync.ApplyNativeTakeDamageOnHost(enemyWorldId, fire, crit, hurt, noSneak))
                    PlaytestLog.Event("Damage", "host hit miss id=" + enemyWorldId.ToString("X16"));
            }
            else if (_net.TryGetPeer(0, out var peer)
                && peer.ConnectionState == ConnectionState.Connected)
            {
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        internal void HandleEnemyDamage(EnemyDamageMessage msg)
        {
            ulong enemyId = unchecked((ulong)msg.EnemyWorldId);

            if (msg.TargetPlayerId == _net.LocalPlayerId)
            {
                PlaytestLog.Verbose("Enemy", "dmg=" + msg.Damage.ToString("F0")
                    + " from " + enemyId.ToString("X16"));
                NetworkDamageSystem.ApplyDamage(msg.Damage, Vector3.zero, Vector3.zero);
            }
            else if (msg.AttackerPlayerId >= 0 && msg.TargetPlayerId < 0 && _net.Role == NetworkRole.Host)
            {
                if (msg.NativeTakeDamage)
                {
                    if (!_net.EnemySync.ApplyNativeTakeDamageOnHost(enemyId, msg.FireChance, msg.CriticalChance, msg.HurtChance, msg.NoSneak))
                        PlaytestLog.Event("Damage", "client hit miss id=" + enemyId.ToString("X16")
                            + " from=" + msg.AttackerPlayerId);
                }
                else
                    _net.EnemySync.ApplyDamageOnHost(enemyId, msg.Damage);
            }
        }
    }
}
