// Books / notes stay local (docs/SYNC.md): a peer's use replayed here (UseItem onSuccessful, e.g. LOV_Microfiche.UseFiche
// -> BookScreen.OpenBook) must not open the reading screen on this player. The world half of the event still runs.
using HarmonyLib;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(BookScreen), nameof(BookScreen.OpenBook))]
    public static class BookScreenRemoteOpenPatch
    {
        [HarmonyPrefix]
        public static bool Prefix() => !RemoteApply("OpenBook");

        internal static bool RemoteApply(string what)
        {
            if (!NetGate.IsApplying || !NetGate.Party) return false;
            PlaytestLog.Event("Interact", what + " skipped (a peer's apply, presentation stays local)");
            return true;
        }
    }

    [HarmonyPatch(typeof(BookScreen), nameof(BookScreen.OpenBookMemory))]
    public static class BookScreenRemoteOpenMemoryPatch
    {
        [HarmonyPrefix]
        public static bool Prefix() => !BookScreenRemoteOpenPatch.RemoteApply("OpenBookMemory");
    }
}
