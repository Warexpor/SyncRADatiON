using UnityEngine;

namespace SyncRADation.Networking
{
    public static class PlayerPositionManager
    {
        private static Vector3 _hostPos;
        private static Vector3 _remotePos;
        private static float _remoteRotY;
        private static bool _hasRemote;
        private static float _lastRemoteUpdateTime;

        public static void ReportHostPosition(Vector3 pos)
        {
            _hostPos = pos;
        }

        public static void UpdateRemotePlayer(Vector3 pos, float rotY)
        {
            _remotePos = pos;
            _remoteRotY = rotY;
            _hasRemote = true;
            _lastRemoteUpdateTime = Time.time;
        }

        public static bool HasRemotePlayer => _hasRemote && (Time.time - _lastRemoteUpdateTime) < 3f;
        public static Vector3 RemotePlayerPosition => _remotePos;
        public static float RemotePlayerRotY => _remoteRotY;

        public static Vector3 GetNearestPlayerPosition(Vector3 fromPos)
        {
            Vector3 nearest = _hostPos;
            float nearestSq = Vector3.SqrMagnitude(_hostPos - fromPos);
            if (_hasRemote)
            {
                float d = Vector3.SqrMagnitude(_remotePos - fromPos);
                if (d < nearestSq)
                    nearest = _remotePos;
            }
            return nearest;
        }

        public static void Clear()
        {
            _hasRemote = false;
        }
    }
}
