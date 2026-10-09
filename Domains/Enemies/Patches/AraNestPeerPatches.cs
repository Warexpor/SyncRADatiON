// Host: a ARAR nest a remote player stands at stays triggered, as it does for the host's own room.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    /// <summary>
    /// AraNest.Update (Ghidra AraNest.c): a nest that is activated and triggered clears activated as soon as
    /// PlayerState.currentRoom is not its room, and the next frame (triggered, not activated) switches the ARAR off,
    /// resets it to state 0 and clears triggered. On the host that check only saw the host's room, so a nest a client
    /// set off (AraNest+ applied, TriggerTrap run) was put back to bed the next frame while the client was fighting it
    /// ("wake REFUSED ... ARAR disabled by the level", test pilot MED_Medical Sleeping Ward). With a remote player in
    /// the nest's room and the host elsewhere, Update runs against that player (PeerRoomEnemies.PeerView), so the trap
    /// range and the "left the room" step follow whoever is there; with nobody left the native reset runs.
    /// </summary>
    [HarmonyPatch(typeof(AraNest), nameof(AraNest.Update))]
    public static class AraNestUpdatePeerPatch
    {
        /// <summary>
        /// Client: a nest the host holds triggered (AraNest+ applied) in a room this player is not in is the
        /// "triggered, not activated" case above, so native Update put the ARAR back to bed locally and the client's
        /// puzzle diff sent triggered=0 to the host. With two clients that ping-ponged every 10 s (one in the room
        /// sending 1, one elsewhere sending 0) and the host's ARAR went inactive mid-fight (test pilot MED_Medical
        /// Morgue: "kill Enemy 3 ARAR: target stayed inactive", AraNest D4472A7D2230A0C9 DIFF). The reset of a nest
        /// outside this player's room is the host's (it runs it once nobody is left there); in its own room native runs.
        /// </summary>
        // Client: nests the host last sent as triggered (ApplyAraNest). A nest the host reset (nobody left in its room)
        // is not in here, so native Update resets it on this client too and the client's diff stops re-sending
        // triggered: with every reset blocked, a client away from the room sent AraNest+ every 10 s and the host
        // re-triggered and reset it in a loop (test pilot MED_Medical Morgue, run 3). Cleared by SessionReset
        // "AraNestHeld" (scene / session).
        static readonly System.Collections.Generic.HashSet<System.IntPtr> _hostTriggered =
            new System.Collections.Generic.HashSet<System.IntPtr>();

        public static void Reset() => _hostTriggered.Clear();

        /// <summary>ApplyAraNest on a client: the host's triggered bit for this nest.</summary>
        public static void NoteHost(AraNest n, bool triggered)
        {
            if (n == null) return;
            if (triggered) _hostTriggered.Add(n.Pointer);
            else _hostTriggered.Remove(n.Pointer);
        }

        static bool ClientSkipsReset(AraNest n)
        {
            if (n == null || n.dead || n.activated || !n.triggered) return false;
            if (!_hostTriggered.Contains(n.Pointer)) return false;
            var ara = n.AraLogic;
            if (ara == null || (int)ara.state == 3) return false; // native: state 3 = dead
            var room = n.room;
            return room != null && room != PlayerState.currentRoom;
        }

        [HarmonyPrefix]
        public static bool Prefix(AraNest __instance, out PeerRoomEnemies.PeerView? __state)
        {
            __state = null;
            if (NetGate.Live && !NetGate.Host)
            {
                try { return !ClientSkipsReset(__instance); }
                catch (System.Exception e) { Guard.Swallow(e); return true; }
            }
            if (!NetGate.Host) return true;
            try
            {
                var room = __instance != null ? __instance.room : null;
                if (room == null || room == PlayerState.currentRoom || __instance.dead) return true;
                var net = LanNetworkManager.Instance;
                if (net == null) return true;
                var peer = PeerRoomEnemies.PeerIn(room, net.ProxyManager, net.GetRemotePlayerIds());
                if (peer == null) return true;
                __state = new PeerRoomEnemies.PeerView(room, peer);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        [HarmonyFinalizer]
        public static System.Exception Finalizer(PeerRoomEnemies.PeerView? __state, System.Exception __exception)
        {
            __state?.Dispose();
            return __exception;
        }
    }

    /// <summary>
    /// AraNest.delayedLogic (Ghidra AraNest.c, &lt;delayedLogic&gt;d__38.MoveNext) drops the ARAR once the player is
    /// out of the nest's range or 6 s passed, and holds it while the player stands within 3.2 of the nest. On the host
    /// that distance was the host player's, far away from a nest a client set off, so the host's ARAR dropped at once
    /// while the client's still hung. With a remote player in the nest's room and the host elsewhere, each step
    /// measures that player (same view as AraNestUpdatePeerPatch).
    /// </summary>
    [HarmonyPatch(typeof(AraNest._delayedLogic_d__38), nameof(AraNest._delayedLogic_d__38.MoveNext))]
    public static class AraNestDelayedLogicPeerPatch
    {
        [HarmonyPrefix]
        public static void Prefix(AraNest._delayedLogic_d__38 __instance, out PeerRoomEnemies.PeerView? __state)
        {
            __state = null;
            if (!NetGate.Host) return;
            try
            {
                var nest = __instance != null ? __instance.__4__this : null;
                var room = nest != null ? nest.room : null;
                if (room == null || room == PlayerState.currentRoom) return;
                var net = LanNetworkManager.Instance;
                if (net == null) return;
                var peer = PeerRoomEnemies.PeerIn(room, net.ProxyManager, net.GetRemotePlayerIds());
                if (peer == null) return;
                __state = new PeerRoomEnemies.PeerView(room, peer);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        [HarmonyFinalizer]
        public static System.Exception Finalizer(PeerRoomEnemies.PeerView? __state, System.Exception __exception)
        {
            __state?.Dispose();
            return __exception;
        }
    }
}
