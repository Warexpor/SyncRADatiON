using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Held-state + rematch hooks Cryo/Codepad OnEnable handlers need from PuzzleSyncService.
    /// </summary>
    internal interface IPuzzleDomainHost
    {
        bool IsHeld(PuzzleType type, ulong worldId);
        /// <summary>A solved entry of this type is held under a WorldId no local component has, and candidateId is a
        /// local component of the same type the host never sent (so it is that entry's local copy).</summary>
        bool HeldUnmatched(PuzzleType type, ulong candidateId);
        void RemapHeld(PuzzleType type, ulong newId);
    }

    /// <summary>
    /// Shared hierarchy walk + PuzzleStateEntry factory used by puzzle domains.
    /// </summary>
    internal static class PuzzleDomainUtil
    {
        public static T FindInParents<T>(GameObject go) where T : Component
        {
            if (go == null) return null;
            Transform t = go.transform;
            while (t != null)
            {
                var c = t.GetComponent<T>();
                if (c != null) return c;
                t = t.parent;
            }
            return null;
        }

        public static PuzzleStateEntry Mk(
            PuzzleType type, long worldId,
            bool b0, bool b1, bool b2,
            int i0, int i1, int i2, int i3, float f0, float f1 = 0f)
        {
            return new PuzzleStateEntry
            {
                Type = type,
                WorldId = worldId,
                Bool0 = b0, Bool1 = b1, Bool2 = b2,
                Int0 = i0, Int1 = i1, Int2 = i2, Int3 = i3,
                Float0 = f0,
                Float1 = f1
            };
        }
    }
}
