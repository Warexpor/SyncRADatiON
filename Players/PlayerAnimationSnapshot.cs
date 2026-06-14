using UnityEngine;

namespace SyncRADation.Players
{
    public static class PlayerAnimationSnapshot
    {
        public static byte ReadCharState(GameObject player)
        {
            if (player == null) return 0;
            var ps = player.GetComponent<PlayerState>();
            if (ps == null) return 0;
            return (byte)PlayerState.charState;
        }

        public static byte ReadFacing(GameObject player)
        {
            if (player == null) return 0;
            var pc8 = player.GetComponent<PlayerController8>();
            if (pc8 == null) return 0;
            return (byte)pc8.facing;
        }

        public static bool ReadAiming(GameObject player)
        {
            if (player == null) return false;
            var pc8 = player.GetComponent<PlayerController8>();
            return pc8 != null && pc8.aiming;
        }

        public static bool ReadShooting(GameObject player)
        {
            if (player == null) return false;
            var pc8 = player.GetComponent<PlayerController8>();
            return pc8 != null && pc8.shooting;
        }

        public static bool ReadRunning(GameObject player)
        {
            if (player == null) return false;
            return PlayerState.charState == PlayerState.charStates.run;
        }
    }
}
