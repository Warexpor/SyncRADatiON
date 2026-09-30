using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Config;
using SyncRADation.Players;
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

        internal void SendFriendlyFire(int targetPlayerId, float damage, Vector3 hitPos)
        {
            if (ModConfig.FriendlyFire?.Value != true) return;
            if (PartyVitals.IsDown(targetPlayerId)) return; // downed players cannot be shot

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

            if (_net.Role == NetworkRole.Host)
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
            if (_net.Role == NetworkRole.Host && msg.TargetPlayerId != _net.LocalPlayerId)
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
                Vector3 hitPos = new Vector3(msg.HitPosX, msg.HitPosY, msg.HitPosZ);
                ModRuntime.Log?.Msg("[FF] Received damage=" + msg.Damage.ToString("F0") + " from player " + msg.AttackerPlayerId);
                NetworkDamageSystem.ApplyDamage(msg.Damage, hitPos, Vector3.zero);
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
