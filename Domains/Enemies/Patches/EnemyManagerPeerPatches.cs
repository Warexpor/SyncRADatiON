// Host: a room's CheckIfLeft keeps its enemies awake while a remote player is still in that room.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    /// <summary>
    /// EnemyManager.CheckIfLeft (Ghidra EnemyManager.c, &lt;CheckIfLeft&gt;d__23.MoveNext) loops while
    /// PlayerState.currentRoom is its room and, once it is not, resets and switches off the room's enemies. On the host
    /// every step of it runs against the room of a remote player standing there (PeerRoomEnemies.PeerView), so a room
    /// a client fights in stays live when the host is elsewhere; with nobody left in it the native step sleeps it.
    /// The host's own room is never touched (its currentRoom already says so).
    /// </summary>
    [HarmonyPatch(typeof(EnemyManager._CheckIfLeft_d__23), nameof(EnemyManager._CheckIfLeft_d__23.MoveNext))]
    public static class EnemyManagerCheckIfLeftPatch
    {
        [HarmonyPrefix]
        public static void Prefix(EnemyManager._CheckIfLeft_d__23 __instance, out PeerRoomEnemies.PeerView? __state)
        {
            __state = null;
            if (!NetGate.SceneOwner) return;
            try
            {
                var m = __instance.__4__this;
                var room = m != null ? m.room : null;
                if (room == null) return;
                if (room == PlayerState.currentRoom)
                {
                    PeerRoomEnemies.NoteHostIn(m);
                    return;
                }
                bool hostLeft = PeerRoomEnemies.TakeHostLeft(m);
                var net = LanNetworkManager.Instance;
                if (net == null) return;
                var peer = PeerRoomEnemies.PeerIn(room, net.ProxyManager, net.GetRemotePlayerIds());
                // Nobody left: the native step sleeps the room. It also clears inCombat / enemyPresence, which are the
                // host's own: right when the host is the one who left, wrong when a remote player did while the host
                // fights elsewhere (DET: the music cut out for a frame each time the client changed rooms).
                if (peer == null)
                {
                    if (!hostLeft) __state = PeerRoomEnemies.PeerView.StaticsOnly();
                    return;
                }
                __state = new PeerRoomEnemies.PeerView(room, peer, hostLeft);
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

    /// <summary>
    /// EnemyManager.Enter is one handler of PlayerState.EnteredRoomEvent (via Room.EnterRoom). An exception in one
    /// handler ends the whole multicast call, so every later manager of that room never switched its enemies on:
    /// test pilot clients (LAB_Labyrinth Glass Cage Room, ROT Dark Room) logged a NullReferenceException in
    /// Room.EnterRoom and the room's other STCR stayed inactive. The failing manager is named with its list state and
    /// the exception stops here, so the rest of the room's handlers run.
    /// The NullReferenceException itself (test pilot run 6, LAB Glass Cage Room): native Enter (Ghidra EnemyManager.c)
    /// switches each enemy on and, for one whose behaviour is not idle and that stands within 20 of the player, picks a
    /// random point of waypoints.Waypoints with no null check. An enemy authored as patrol / random without an
    /// EnemyPatrol threw there, and every enemy after it in the list stayed off. A player walks in through a door,
    /// far away; the player stood beside it (pilot, or the host's PeerView for a client standing there) hit it. For
    /// the call, such an enemy counts as idle (native skips the repositioning for idle), then gets its value back.
    /// Host walking into a room that already runs for a remote player (in operation, entered through PeerView): native
    /// Enter has no "already running" guard. It would move every patrolling enemy within 20 of the host to another
    /// waypoint ("Cycling to other patrol point to give elster some space") in the middle of the client's fight and
    /// start a second CheckIfLeft. The entry is skipped; the running CheckIfLeft now sees the host in the room and
    /// writes its combat statics natively. checkVeil (the one other thing Enter does) still runs.
    /// </summary>
    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.Enter))]
    public static class EnemyManagerEnterGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(EnemyManager __instance, Room enterRoom,
            out System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<EnemyController, EnemyController.idleBehaviour>> __state)
        {
            __state = null;
            try
            {
                if (LiveForPeer(__instance, enterRoom))
                {
                    PlaytestLog.Event("Enemy", "host enters " + enterRoom.roomName + ": already live for a peer, native Enter skipped");
                    var veil = EnemyManager.checkVeil;
                    if (veil != null) veil.Invoke();
                    return false;
                }
                var en = __instance != null ? __instance.enemies : null;
                if (en == null) return true;
                for (int i = 0; i < en.Count; i++)
                {
                    var e = en[i];
                    if (e == null || e.behaviour == EnemyController.idleBehaviour.idle) continue;
                    var wp = e.waypoints;
                    var pts = wp != null ? wp.Waypoints : null;
                    if (pts != null && pts.Count > 0) continue;
                    if (__state == null)
                        __state = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<EnemyController, EnemyController.idleBehaviour>>();
                    __state.Add(new System.Collections.Generic.KeyValuePair<EnemyController, EnemyController.idleBehaviour>(e, e.behaviour));
                    e.behaviour = EnemyController.idleBehaviour.idle;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        static bool LiveForPeer(EnemyManager m, Room enterRoom)
        {
            if (!NetGate.SceneOwner || m == null || enterRoom == null || !m.inOperation || m.room != enterRoom) return false;
            if (global::Cheats.tourist) return false;
            var net = LanNetworkManager.Instance;
            return net != null && PeerRoomEnemies.PeerIn(enterRoom, net.ProxyManager, net.GetRemotePlayerIds()) != null;
        }

        [HarmonyFinalizer]
        public static System.Exception Finalizer(EnemyManager __instance, Room enterRoom, System.Exception __exception,
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<EnemyController, EnemyController.idleBehaviour>> __state)
        {
            if (__state != null)
            {
                for (int i = 0; i < __state.Count; i++)
                {
                    try { if (__state[i].Key != null) __state[i].Key.behaviour = __state[i].Value; }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            if (__exception == null) return null;
            try
            {
                string list = "?";
                var en = __instance != null ? __instance.enemies : null;
                if (en == null) list = "enemies=null";
                else
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < en.Count; i++)
                    {
                        var e = en[i];
                        sb.Append(i == 0 ? "" : ",").Append(e == null ? "null" : e.gameObject.name.Replace(' ', '_'));
                    }
                    list = sb.ToString();
                }
                PlaytestLog.Event("Enemy", "WARN EnemyManager.Enter threw in " + (enterRoom != null ? enterRoom.roomName : "?")
                    + " manager=" + (__instance != null ? __instance.gameObject.name : "?") + " room="
                    + (__instance != null && __instance.room != null ? __instance.room.roomName : "null")
                    + " kolibri=" + (__instance != null && __instance.Kolibri != null) + " enemies=[" + list + "]: "
                    + __exception.GetType().Name + " " + __exception.Message);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return null;
        }
    }
}

namespace SyncRADation.Patches
{
    /// <summary>
    /// Room.LeaveRoom starts delayedDisabling (Ghidra Room.c): the instantChunk goes off at once and, 2 s later, the
    /// chunk and Cell, unless containsElster came back. On the host that pulled the floor, walls and colliders from
    /// under a remote player's fight (PeerRoomEnemies). While one is in the room, each step sees containsElster set,
    /// which is the native "the player is back" path: nothing is switched off. PeerRoomEnemies.Tick lets the chunk go
    /// once the room is empty.
    /// </summary>
    [HarmonyPatch(typeof(Room._delayedDisabling_d__28), nameof(Room._delayedDisabling_d__28.MoveNext))]
    public static class RoomDelayedDisablingPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Room._delayedDisabling_d__28 __instance, out Room __state)
        {
            __state = null;
            if (!NetGate.SceneOwner) return;
            try
            {
                var room = __instance.__4__this;
                if (room == null || room.containsElster) return;
                var net = LanNetworkManager.Instance;
                if (net == null || PeerRoomEnemies.PeerIn(room, net.ProxyManager, net.GetRemotePlayerIds()) == null) return;
                PeerRoomEnemies.Hold(room);
                room.containsElster = true;
                __state = room;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        [HarmonyFinalizer]
        public static System.Exception Finalizer(Room __state, System.Exception __exception)
        {
            try { if (__state != null) __state.containsElster = false; }
            catch (System.Exception e) { Guard.Swallow(e); }
            return __exception;
        }
    }
}
