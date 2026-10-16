// Identical-code-folded IL2CPP methods share one native function (script.json: several names at one RVA), so a Harmony
// detour on one of them also fires for the others, with a `this` of a foreign class wrapped as the patched type.
// Shared-RVA patches call Is<T>(__instance) first and pass through when the object is not really a T (or the cast fails).
using UnhollowerBaseLib;

namespace SyncRADation.Sync
{
    public static class Il2CppRealType
    {
        public static bool Is<T>(Il2CppObjectBase o) where T : Il2CppObjectBase
        {
            if (o == null) return false;
            try { return o.TryCast<T>() != null; }
            catch (System.Exception e) { Guard.Swallow(e); return false; } // fail CLOSED: the patch passes through
        }
    }
}
