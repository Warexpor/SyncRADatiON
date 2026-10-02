// Story inventory wipe between chapters, mirrored on every peer.
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    /// <summary>
    /// PEN_CodeRoomEnd's cutscene (PEN_Hole) clears InventoryManager.elsterItems right before it loads
    /// LOV_Reeducation (Ghidra PEN_CodeRoomEnd.c &lt;Cutscene&gt; state 0): Elster reaches Sierpinski empty-handed and
    /// the intro gives her Alina's photo. The cutscene plays on one peer only; the others just follow the load, so
    /// they kept their bag (a client walked in holding the King in Yellow) and the party key ring kept every
    /// prologue key/object, which then masqueraded in everyone's slots. Every peer arriving in LOV_Reeducation
    /// straight from PEN_Hole clears its bag the same way, and the ring empties with it.
    /// </summary>
    public static class ChapterWipe
    {
        const string From = "PEN_Hole";
        const string To = "LOV_Reeducation";

        // persistent: the previous real scene is the input, it must outlive every scene reset
        static string _lastRealScene = "";

        /// <summary>ModRuntime.OnSceneChanged, before the network scene path (hello / dump).</summary>
        public static void OnSceneArrived(string scene)
        {
            if (string.IsNullOrEmpty(scene) || SceneFollowService.IsTransient(scene)) return;
            string prev = _lastRealScene;
            _lastRealScene = scene;
            if (prev != From || scene != To || !NetGate.Live) return;
            // Exactly the native wipe: Clear, then AddItem(None). The None entry is the empty-hands item the inventory
            // UI and SaveManager rely on (Ghidra InventoryBase.c / SaveManager.c skip or seed item 0x66); cleared
            // without it, the slots were off by one and the new photo only showed while scrolling.
            try
            {
                var bag = InventoryManager.elsterItems;
                if (bag != null) bag.Clear();
                var none = InventoryManager.getItem(Items.itemlist.None);
                if (none != null) InventoryManager.AddItem(none);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            PartyKeyRing.Reset();
            PlaytestLog.Event("KeyRing", "chapter wipe " + prev + " -> " + scene + ": bag and ring cleared");
            PartyKeyRing.Broadcast();
        }
    }
}
