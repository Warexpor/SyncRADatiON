using LiteNetLib.Utils;
using SyncRADation.Sync;
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class ClientEntityInterpolationService
    {
        private class InterpState
        {
            public Vector3 previousPosition;
            public Vector3 targetPosition;
            public float previousRotY;
            public float targetRotY;
            public float arrivalTime;
            public bool hasTarget;
            public bool isFirst;
            public string entityName;
        }

        private static readonly Dictionary<short, InterpState> _states = new Dictionary<short, InterpState>();
        private static readonly Dictionary<short, Vector3> _displayPositions = new Dictionary<short, Vector3>();
        private static readonly Dictionary<short, float> _displayRotations = new Dictionary<short, float>();

        private const float SnapshotInterval = 0.033f;
        private const float MaxInterpDelay = 0.3f;
        private const float MatchRadius = 10f;
        private const float TeleportDistance = 15f;
        private const float TeleportTimeGap = 0.5f;

        public static void ApplyPlayerState(Vector3 position, float rotY)
        {
            const short id = 0;

            if (!_states.TryGetValue(id, out var state))
            {
                state = new InterpState { isFirst = true };
                _states[id] = state;
            }

            bool teleport = !state.isFirst &&
                (Vector3.Distance(state.targetPosition, position) > TeleportDistance ||
                 Time.time - state.arrivalTime > TeleportTimeGap);

            if (state.isFirst || teleport)
            {
                _displayPositions[id] = position;
                _displayRotations[id] = rotY;
                state.previousPosition = position;
                state.previousRotY = rotY;
                state.targetPosition = position;
                state.targetRotY = rotY;
                state.arrivalTime = Time.time;
                state.hasTarget = true;
                state.isFirst = false;
                return;
            }

            state.previousPosition = _displayPositions[id];
            state.previousRotY = _displayRotations[id];
            state.targetPosition = position;
            state.targetRotY = rotY;
            state.arrivalTime = Time.time;
            state.hasTarget = true;
        }

        public static void ApplySnapshot(NetDataReader reader)
        {
            var msg = new EntityStateMessage();
            int count = reader.GetInt();
            msg.Entities = new EntitySnapshotNet[count];
            for (int i = 0; i < count; i++)
                msg.Entities[i] = EntitySnapshotNet.Deserialize(reader);

            if (msg.Entities == null || msg.Entities.Length == 0)
                return;

            foreach (var e in msg.Entities)
            {
                var targetPos = new Vector3(e.PosX, e.PosY, e.PosZ);

                if (!_states.TryGetValue(e.Index, out var state))
                {
                    state = new InterpState { isFirst = true, entityName = e.EntityName };
                    _states[e.Index] = state;
                }

                if (state.isFirst)
                {
                    // Try to find matching local entity
                    var local = GameObjectEntityTracker.FindByStableId(e.Index);
                    if (local == null && !string.IsNullOrEmpty(e.EntityName))
                    {
                        var exclude = new HashSet<short>(_states.Keys);
                        exclude.Remove(e.Index);
                        local = GameObjectEntityTracker.FindByPositionAndName(targetPos, e.EntityName, MatchRadius, exclude);
                        if (local != null)
                            GameObjectEntityTracker.AssignId(local, e.Index);
                    }

                    _displayPositions[e.Index] = local != null ? local.transform.position : targetPos;
                    _displayRotations[e.Index] = local != null ? local.transform.eulerAngles.y : e.RotY;
                    state.isFirst = false;
                }

                state.previousPosition = _displayPositions[e.Index];
                state.previousRotY = _displayRotations[e.Index];
                state.targetPosition = targetPos;
                state.targetRotY = e.RotY;
                state.arrivalTime = Time.time;
                state.hasTarget = true;
            }
        }

        public static void TickLateUpdate()
        {
            float now = Time.time;
            var staleKeys = new List<short>();

            foreach (var kvp in _states)
            {
                short id = kvp.Key;
                var state = kvp.Value;

                if (!state.hasTarget)
                {
                    staleKeys.Add(id);
                    _displayPositions.Remove(id);
                    _displayRotations.Remove(id);
                    continue;
                }

                float elapsed = now - state.arrivalTime;

                if (elapsed > MaxInterpDelay)
                {
                    _displayPositions[id] = state.targetPosition;
                    _displayRotations[id] = state.targetRotY;
                    state.hasTarget = false;
                }
                else if (elapsed > SnapshotInterval)
                {
                    float extrapT = elapsed - SnapshotInterval;
                    Vector3 velocity = (state.targetPosition - state.previousPosition) / SnapshotInterval;
                    _displayPositions[id] = state.targetPosition + velocity * extrapT;
                    _displayRotations[id] = state.targetRotY;
                }
                else
                {
                    float t = elapsed / SnapshotInterval;
                    float smoothT = t * t * (3f - 2f * t);
                    _displayPositions[id] = Vector3.Lerp(state.previousPosition, state.targetPosition, smoothT);
                    _displayRotations[id] = Mathf.LerpAngle(state.previousRotY, state.targetRotY, smoothT);
                }

                if (!_displayPositions.TryGetValue(id, out Vector3 pos))
                    continue;

                if (id == 0)
                {
                    var proxy = Players.RemotePlayerProxy.Instance;
                    if (proxy != null && proxy.GameObject != null)
                    {
                        proxy.GameObject.transform.position = pos;
                    }
                    continue;
                }

                var target = GameObjectEntityTracker.FindByStableId(id);
                if (target != null)
                {
                    target.transform.position = pos;
                    if (_displayRotations.TryGetValue(id, out float rotY))
                    {
                        Vector3 euler = target.transform.eulerAngles;
                        euler.y = rotY;
                        target.transform.eulerAngles = euler;
                    }
                }
            }

            foreach (short k in staleKeys)
                _states.Remove(k);
        }

        public static void ResetPlayerState()
        {
            const short id = 0;
            _states.Remove(id);
            _displayPositions.Remove(id);
            _displayRotations.Remove(id);
        }

        public static void Reset()
        {
            _states.Clear();
            _displayPositions.Clear();
            _displayRotations.Clear();
            GameObjectEntityTracker.Clear();
        }
    }
}
