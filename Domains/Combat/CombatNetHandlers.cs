using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Config;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>Friendly-fire unicast + death-policy wire.</summary>
    internal sealed class CombatNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal CombatNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>Most one friendly-fire hit may take (Elster has 100 HP; a bad / forged value must not one-shot).</summary>
        internal const float MaxFriendlyDamage = 100f;

        /// <summary>Usable friendly-fire damage, or 0 to drop the hit (NaN, negative, zero).</summary>
        internal static float ClampFriendlyDamage(float damage)
        {
            if (float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f) return 0f;
            return damage > MaxFriendlyDamage ? MaxFriendlyDamage : damage;
        }

        internal void SendFriendlyFire(int targetPlayerId, float damage, Vector3 hitPos)
        {
            if (ModConfig.FriendlyFire?.Value != true) return;
            if (PartyVitals.IsDown(targetPlayerId)) return; // downed players cannot be shot
            damage = ClampFriendlyDamage(damage);
            if (damage <= 0f) return;

            var msg = new FriendlyFireMessage
            {
                TargetPlayerId = targetPlayerId,
                AttackerPlayerId = _net.LocalPlayerId,
                Damage = damage,
                HitPosX = hitPos.x,
                HitPosY = hitPos.y,
                HitPosZ = hitPos.z
            };
            // Send to host if we are client (host relays); host sends direct to target
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.FriendlyFire);
            msg.Serialize(writer);

            if (NetGate.HostRole)
            {
                if (_net.TryGetPeer(targetPlayerId, out var peer)
                    && peer.ConnectionState == ConnectionState.Connected)
                {
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
                    ModRuntime.Log?.Msg("[FF] Host sent dmg=" + damage.ToString("F0") + " to " + targetPlayerId);
                }
            }
            else
            {
                _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
                ModRuntime.Log?.Msg("[FF] Client sent dmg=" + damage.ToString("F0") + " target=" + targetPlayerId);
            }
        }

        internal void HandleFriendlyFire(FriendlyFireMessage msg, int senderId)
        {
            msg.Damage = ClampFriendlyDamage(msg.Damage);
            if (msg.Damage <= 0f) return;
            if (PartyVitals.IsDown(msg.TargetPlayerId)) return; // downed players cannot be shot
            if (NetGate.HostRole && msg.TargetPlayerId != _net.LocalPlayerId)
            {
                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.FriendlyFire);
                msg.Serialize(w);
                if (_net.TryGetPeer(msg.TargetPlayerId, out var tpeer)
                    && tpeer.ConnectionState == ConnectionState.Connected)
                    tpeer.Send(w, DeliveryMethod.ReliableOrdered);
            }

            if (ModConfig.FriendlyFire?.Value != true) return;
            if (msg.TargetPlayerId == _net.LocalPlayerId)
            {
                ModRuntime.Log?.Msg("[FF] Received damage=" + msg.Damage.ToString("F0") + " from player " + msg.AttackerPlayerId);
                NetworkDamageSystem.ApplyDamage(msg.Damage);
            }
        }

        internal void SendDeathPolicy(DeathKind kind)
        {
            var msg = new DeathPolicyMessage { SenderPlayerId = _net.LocalPlayerId, Kind = kind };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.DeathPolicy);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleDeathPolicy(DeathPolicyMessage msg)
        {
            NetworkDamageSystem.HandleDeathPolicy(msg);
        }
    }
}
