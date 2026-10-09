// Test pilot commands (one line of cmd.txt each). Every command that acts goes through the game function a player's
// action would reach (Interaction.trigger, EnemyController.TakeDamage, PlayerState.HurtElster, SaveManager, the F6 /
// F7 / F11 paths), so the mod's patches see exactly what they see in play.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Players;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static partial class TestPilot
    {
        // persistent: "god" toggle of the pilot run
        private static bool _god;
        // persistent: "god" hp floor (the hp when it was switched on)
        private static int _godHp;
        // persistent: "autodlg" toggle (default on: a yes/no prompt or a line must not stall an unattended run)
        private static bool _autoDialogue = true;
        // persistent: "autodlg" choice picked at a prompt
        private static int _autoChoice;
        // persistent: realtime the current dialogue state was first seen
        private static float _dialogueSince = -1f;
        // persistent: realtime of the last auto continue
        private static float _dialogueNextAt;
        // persistent: a deferred action (take / use / cut / door / hit wait for the target's room chunk to wake)
        private static Func<bool> _deferred;
        // persistent: what the deferred action is, for the log
        private static string _deferredWhat;
        // persistent: realtime the deferred action gives up
        private static float _deferredUntil;
        // persistent: extra state for the "stayed inactive" line (nest ARAR: the nest's flags and distance)
        private static Func<string> _deferredDetail;
        // persistent: realtime the deferred action's target was first seen awake
        private static float _awakeSince = -1f;
        // persistent: "walk" (realtime it ends, -1 = not walking)
        private static float _walkUntil = -1f;
        // persistent: "walk" stick direction
        private static Vector2 _walkDir;
        // persistent: "walk" start position
        private static Vector3 _walkFrom;

        private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        private static int I(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static void Run(LanNetworkManager net, string[] a)
        {
            GameObject me = PlayerState.player;
            switch (a[0].ToLowerInvariant())
            {
                case "status":
                    Status(net);
                    return;
                case "net":
                    NetStatus(net);
                    return;
                case "wait":
                    _waitUntil = Time.realtimeSinceStartup + F(a[1]);
                    return;
                case "at":
                    // "at <unix ms> <command...>": run the command at that moment (races between peers).
                    _atMs = long.Parse(a[1], CultureInfo.InvariantCulture);
                    _atCmd = new string[a.Length - 2];
                    Array.Copy(a, 2, _atCmd, 0, a.Length - 2);
                    Out("  queued for " + _atMs);
                    return;
                case "say":
                    Out("  " + string.Join(" ", a, 1, a.Length - 1));
                    return;
                case "errors":
                    foreach (var kv in _errorCounts) Out("  x" + kv.Value + " " + kv.Key);
                    Out("  " + _errorCounts.Count + " distinct unity error(s)");
                    return;

                // ---------------------------------------------------------------- player
                case "god":
                    _god = a.Length < 2 || a[1] != "0";
                    _godHp = Math.Max(PlayerState.hp, 1);
                    Out("  god=" + _god + " hp=" + PlayerState.hp);
                    return;
                case "hurt":
                    // The native hurt path an enemy hit takes (party patches: downed instead of game over).
                    PlayerState.HurtElster(I(a[1]), Vector2.zero);
                    Out("  hp=" + PlayerState.hp + " dead=" + NetworkDamageSystem.IsDead);
                    return;
                case "die":
                {
                    // Hits through the native hurt path until this player is down (one hit is capped, and the hurt
                    // cool-down / i-frames swallow the next ones).
                    _god = false;
                    int hits = 0;
                    Defer("die", () =>
                    {
                        if (NetworkDamageSystem.IsDead || PlayerState.hp <= 0)
                        {
                            Out("  down after " + hits + " hit(s): hp=" + PlayerState.hp + " dead=" + NetworkDamageSystem.IsDead);
                            return true;
                        }
                        PlayerState.inviTimer = 0f;
                        PlayerState.hurtCool = 0f;
                        PlayerState.HurtElster(100, Vector2.zero);
                        hits++;
                        return false;
                    });
                    return;
                }
                case "tp":
                    if (me == null) { Out("  no player"); return; }
                    me.transform.position = new Vector3(F(a[1]), F(a[2]), a.Length > 3 ? F(a[3]) : me.transform.position.z);
                    Out("  at " + Pos(me.transform.position) + " room=" + RoomName());
                    return;
                case "walk":
                {
                    // "walk <x> <y> <seconds>": hold the stick that way (the traverse input path, so the character
                    // controller and every collider act as for a player's walk).
                    if (me == null) { Out("  no player"); return; }
                    _walkDir = Vector2.ClampMagnitude(new Vector2(F(a[1]), F(a[2])), 1f);
                    _walkUntil = Time.realtimeSinceStartup + F(a[3]);
                    _walkFrom = me.transform.position;
                    Out("  walking " + _walkDir + " for " + a[3] + " s from " + Pos(_walkFrom) + " room=" + RoomName());
                    return;
                }
                case "comps":
                {
                    // "comps <player|name>": the components on that object and the colliders (2D / 3D) under it.
                    GameObject go = a[1] == "player" ? me : GameObject.Find(string.Join(" ", a, 1, a.Length - 1));
                    if (go == null) { Out("  no object " + a[1]); return; }
                    var sb = new StringBuilder("  " + go.name + " layer=" + go.layer + ":");
                    foreach (var c in go.GetComponents<Component>()) if (c != null) sb.Append(' ').Append(c.GetIl2CppType().Name);
                    Out(sb.ToString());
                    foreach (var c in go.GetComponentsInChildren<Collider2D>(true))
                        Out("    2D " + Owner(c) + " " + c.GetIl2CppType().Name + " layer=" + c.gameObject.layer + " trigger=" + c.isTrigger + " on=" + c.enabled);
                    foreach (var c in go.GetComponentsInChildren<Collider>(true))
                        Out("    3D " + Owner(c) + " " + c.GetIl2CppType().Name + " layer=" + c.gameObject.layer + " trigger=" + c.isTrigger + " on=" + c.enabled);
                    return;
                }
                case "atd":
                    ListTraverseDoors(a.Length > 1 ? F(a[1]) : 20f);
                    return;
                case "rooms":
                    ListRooms();
                    return;
                case "room":
                    GotoRoom(string.Join(" ", a, 1, a.Length - 1));
                    return;

                // ---------------------------------------------------------------- scene / session
                case "scene":
                    // Host: the F7 chapter load (everyone follows). Client: a follow request (host's AllowClientCheats).
                    Out("  load " + a[1] + " as " + net.Role);
                    Cheats.LocationTeleporter.LoadChapterByName(a[1]);
                    return;
                case "save":
                    SaveManager.Save();
                    Out("  saved");
                    return;
                case "load":
                    SaveManager.Load();
                    Out("  loading the save");
                    return;
                case "resync":
                    if (!NetGate.Client) { Out("  client only"); return; }
                    net.SessionHandlers.RequestWorldSnapshot();
                    Out("  world snapshot requested");
                    return;
                case "leave":
                    net.StopNetwork();
                    Out("  offline");
                    return;
                case "connect":
                {
                    string addr = a.Length > 1 ? a[1] : Config.ModConfig.ConnectAddress?.Value ?? "127.0.0.1";
                    int port = a.Length > 2 ? I(a[2]) : Config.ModConfig.ConnectPort?.Value ?? PluginInfo.DefaultPort;
                    net.ConnectToHost(addr, port);
                    Out("  connecting " + addr + ":" + port);
                    return;
                }

                // ---------------------------------------------------------------- enemies
                case "enemies":
                    ListEnemies(a.Length > 1 ? F(a[1]) : float.MaxValue);
                    return;
                case "hit":
                case "kill":
                    HitEnemy(a.Length > 1 ? a[1] : "near", a[0] == "kill" ? -1 : (a.Length > 2 ? I(a[2]) : 20),
                        a.Length > 3 ? F(a[3]) : 40f, a[0] == "kill" && a.Length > 2 ? F(a[2]) : 40f);
                    return;
                case "path":
                {
                    // "path <enemy id|object name>": the hierarchy up to the root with each level's own active flag, and
                    // the room chunk objects (what decides whether a wake may switch it on).
                    Transform t = null;
                    foreach (var kv in WorldRegistry.AllEnemies())
                        if (kv.Value != null && string.Equals(kv.Key.ToString("X16"), a[1], StringComparison.OrdinalIgnoreCase)) t = kv.Value.transform;
                    if (t == null)
                        foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
                            if (tr != null && tr.gameObject.scene.IsValid() && tr.name == a[1]) { t = tr; break; }
                    if (t == null) { Out("  nothing " + a[1]); return; }
                    var room = FindRoomOf(t);
                    for (Transform q = t; q != null; q = q.parent)
                    {
                        string tag = room != null && q.gameObject == room.chunk ? " [chunk]" : room != null && q.gameObject == room.instantChunk ? " [instantChunk]"
                            : room != null && q.gameObject == room.Cell ? " [Cell]" : room != null && q.gameObject == room.Tilesystem ? " [Tilesystem]"
                            : q.GetComponent<Room>() != null ? " [Room]" : "";
                        Out("  " + (q.gameObject.activeSelf ? "on " : "OFF") + " " + q.name + tag);
                    }
                    if (room != null)
                        Out("  room " + RoomLabel(room) + " chunk=" + (room.chunk != null ? room.chunk.name + (room.chunk.activeSelf ? " on" : " off") : "-")
                            + " instant=" + (room.instantChunk != null ? room.instantChunk.name + (room.instantChunk.activeSelf ? " on" : " off") : "-")
                            + " cell=" + (room.Cell != null ? room.Cell.name + (room.Cell.activeSelf ? " on" : " off") : "-"));
                    return;
                }
                case "spawn":
                    Cheats.EntitySpawner.RequestSpawn(a[1]);
                    Out("  spawn " + a[1] + " requested as " + net.Role);
                    return;

                // ---------------------------------------------------------------- world objects
                case "pickups":
                    ListPickups(a.Length > 1 ? F(a[1]) : float.MaxValue);
                    return;
                case "take":
                    Take(a.Length > 1 ? a[1] : "near");
                    return;
                case "inters":
                    ListInteractions(a.Length > 1 ? F(a[1]) : 30f, a.Length > 2 ? a[2] : null);
                    return;
                case "use":
                    Use(a[1]);
                    return;
                case "doors":
                    ListDoors(a.Length > 1 ? F(a[1]) : float.MaxValue);
                    return;
                case "door":
                    Door(a[1], a.Length < 3 || a[2] != "close");
                    return;
                case "cuts":
                    ListCutscenes();
                    return;
                case "cut":
                    StartCutscene(a[1]);
                    return;
                case "skip":
                    SkipCutscenes();
                    return;
                case "dlg":
                    if (a.Length > 1) { Dialoguer.ContinueDialogue(I(a[1])); Out("  continue " + a[1]); }
                    else Out("  gameState=" + PlayerState.gameState);
                    return;
                case "autodlg":
                    _autoDialogue = a.Length < 2 || a[1] != "0";
                    if (a.Length > 2) _autoChoice = I(a[2]);
                    Out("  autodlg=" + _autoDialogue + " choice=" + _autoChoice);
                    return;

                // ---------------------------------------------------------------- inventory
                case "give":
                {
                    string err = Cheats.ItemGiver.Give(a[1], a.Length > 2 ? I(a[2]) : 1);
                    Out(err == null ? "  gave " + a[1] : "  give refused: " + err);
                    return;
                }
                case "inv":
                    Out("  bag=[" + BagText() + "] ring=[" + RingText() + "] box=[" + BoxText() + "]");
                    return;

                // ---------------------------------------------------------------- state
                case "story":
                {
                    var flags = new Dictionary<string, StoryFlagEntry>();
                    if (!SyncRADation.Networking.ProgressSlot.ReadShared(flags)) { Out("  no progress slot"); return; }
                    int shown = 0;
                    foreach (var kv in flags)
                    {
                        if (a.Length > 1 && kv.Key.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (shown++ < 60) Out("  " + kv.Key + "=" + FlagText(kv.Value));
                    }
                    Out("  " + shown + " of " + flags.Count + " shared key(s)");
                    return;
                }
                case "digest":
                    WriteDigest(a.Length > 1 ? a[1] : "now");
                    return;
                case "shot":
                {
                    string file = Path.Combine(Dir, (a.Length > 1 ? a[1] : "shot") + ".png");
                    ScreenCapture.CaptureScreenshot(file);
                    Out("  screenshot " + file);
                    return;
                }
                case "fps":
                    if (a.Length > 1)
                    {
                        QualitySettings.vSyncCount = 0;
                        Application.targetFrameRate = I(a[1]);
                    }
                    Out("  dt=" + (Time.unscaledDeltaTime * 1000f).ToString("0.0", CultureInfo.InvariantCulture)
                        + "ms target=" + Application.targetFrameRate + " vsync=" + QualitySettings.vSyncCount);
                    return;
                case "quit":
                    Out("  quitting");
                    Application.Quit();
                    return;
                default:
                    Out("  unknown command");
                    return;
            }
        }

        // ------------------------------------------------------------ every-frame helpers

        /// <summary>"god": i-frames kept up and hp held at its floor (a hit still staggers, never downs).</summary>
        private static void KeepGod()
        {
            if (!_god) return;
            try
            {
                if (PlayerState.inviTimer < 1f) PlayerState.inviTimer = 1f;
                if (PlayerState.hp < _godHp) PlayerState.hp = _godHp;
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        /// <summary>Run <paramref name="act"/> every tick until it returns true (the target's room woke) or 8 s pass.</summary>
        private static void Defer(string what, Func<bool> act)
        {
            _deferred = act;
            _deferredWhat = what;
            _deferredUntil = Time.realtimeSinceStartup + 15f;
            _deferredDetail = null;
            _awakeSince = -1f;
            RunDeferred(Time.realtimeSinceStartup);
        }

        /// <summary>
        /// The target is awake and has been for half a second: a room chunk that just woke runs its components'
        /// OnEnable / Start (enemy reset, pickup wiring) over the next frames, which would undo an action taken at once.
        /// </summary>
        private static bool Settled(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) { _awakeSince = -1f; return false; }
            float now = Time.realtimeSinceStartup;
            if (_awakeSince < 0f) _awakeSince = now;
            return now - _awakeSince >= 0.5f;
        }

        private static void RunDeferred(float now)
        {
            if (_deferred == null) return;
            bool done;
            try { done = _deferred(); }
            catch (Exception ex) { Out("  " + _deferredWhat + " failed: " + ex.GetType().Name + ": " + ex.Message); done = true; }
            if (done) { _deferred = null; return; }
            if (now < _deferredUntil) return;
            string detail = null;
            try { detail = _deferredDetail?.Invoke(); } catch (Exception ex) { Guard.Swallow(ex); }
            Out("  " + _deferredWhat + ": target stayed inactive (room asleep, or hidden: withheld / claimed)"
                + (detail != null ? " " + detail : ""));
            _deferred = null;
        }

        /// <summary>"walk": the stick is held through gameState traversing + overrideInput (what a door traverse uses).</summary>
        private static void KeepWalking(float now)
        {
            if (_walkUntil < 0f) return;
            try
            {
                if (now < _walkUntil)
                {
                    PlayerState.gameState = PlayerState.gameStates.traversing;
                    PlayerState.overrideInput = _walkDir;
                    return;
                }
                _walkUntil = -1f;
                PlayerState.overrideInput = Vector2.zero;
                PlayerState.gameState = PlayerState.gameStates.play;
                GameObject me = PlayerState.player;
                Vector3 to = me != null ? me.transform.position : _walkFrom;
                Out("  walked " + (to - _walkFrom).magnitude.ToString("0.00", CultureInfo.InvariantCulture) + " to " + Pos(to)
                    + " room=" + RoomName());
            }
            catch (Exception e) { Guard.Swallow(e); _walkUntil = -1f; }
        }

        private static void ListTraverseDoors(float radius)
        {
            var list = new List<AutoTraverseDoor>();
            try
            {
                foreach (var d in UnityEngine.Object.FindObjectsOfType<AutoTraverseDoor>())
                    if (d != null) list.Add(d);
            }
            catch (Exception e) { Guard.Swallow(e); }
            list.Sort((x, y) => Dist(x.transform.position).CompareTo(Dist(y.transform.position)));
            int n = 0;
            foreach (var d in list)
            {
                float dd = Dist(d.transform.position);
                if (dd > radius || n++ >= 40) continue;
                Out("  " + Owner(d) + "@" + Pos(d.transform.position) + " d=" + dd.ToString("0.0", CultureInfo.InvariantCulture)
                    + " blocker=" + (d.blocker == null ? "-" : d.blocker.activeSelf.ToString())
                    + " link=" + (d.connection != null ? WorldId.FromGameObject(d.connection.gameObject).ToString("X16") + " locked=" + d.connection.locked : "-")
                    + " door=" + (d.door != null ? "open=" + d.door.open + " locked=" + d.door.locked : "-")
                    + " room=" + (d.room != null ? RoomLabel(d.room) : "-") + " traversing=" + d.traversing);
            }
            Out("  " + n + " traverse door(s) within " + radius);
        }

        /// <summary>"autodlg": a line or yes/no prompt open for over a second is continued with the set choice.</summary>
        private static void AutoDialogue(float now)
        {
            bool open;
            try { open = PlayerState.gameState == PlayerState.gameStates.dialogue; }
            catch (Exception e) { Guard.Swallow(e); return; }
            if (!open || !_autoDialogue)
            {
                _dialogueSince = -1f;
                return;
            }
            if (_dialogueSince < 0f) { _dialogueSince = now; _dialogueNextAt = now + 1.2f; return; }
            if (now < _dialogueNextAt) return;
            _dialogueNextAt = now + 0.7f;
            try { Dialoguer.ContinueDialogue(_autoChoice); }
            catch (Exception e) { Guard.Swallow(e); }
            if (now - _dialogueSince > 30f)
            {
                Out("  dialogue open for 30 s, ending it");
                try { Dialoguer.EndDialogue(); } catch (Exception e) { Guard.Swallow(e); }
                _dialogueSince = -1f;
            }
        }

        // ------------------------------------------------------------ status

        private static void Status(LanNetworkManager net)
        {
            var sb = new StringBuilder("  ");
            GameObject me = PlayerState.player;
            sb.Append("role=").Append(net.Role).Append(" id=p").Append(net.LocalPlayerId)
                .Append(" scene=").Append(ActiveScene()).Append(" settled=").Append(_settledScene.Length > 0)
                .Append(" room=").Append(RoomName().Replace(' ', '_'))
                .Append(" pos=").Append(me != null ? Pos(me.transform.position) : "-")
                .Append(" hp=").Append(PlayerState.hp).Append(" dead=").Append(NetworkDamageSystem.IsDead)
                .Append(" gs=").Append(PlayerState.gameState).Append(" cs=").Append(PlayerState.charState)
                .Append(" cutscene=").Append(PlayerState.cutscene).Append(" god=").Append(_god)
                .Append(" players=").Append(net.IsConnected ? net.GetPlayerCount() : 0)
                .Append(" mismatch=").Append(net.SceneMismatch);
            try
            {
                var pm = net.ProxyManager;
                if (pm != null)
                    foreach (int pid in pm.GetProxyPlayerIds())
                    {
                        var p = pm.GetProxy(pid);
                        if (p == null || p.GameObject == null) continue;
                        sb.Append(" | p").Append(pid).Append('@').Append(Pos(p.GameObject.transform.position))
                            .Append(p.LastDead ? " dead" : "");
                    }
            }
            catch (Exception e) { Guard.Swallow(e); }
            Out(sb.ToString());
        }

        private static void NetStatus(LanNetworkManager net)
        {
            var sb = new StringBuilder("  ");
            sb.Append(net.Role).Append(" connected=").Append(net.IsConnected).Append(" me=p").Append(net.LocalPlayerId)
                .Append(" status='").Append(net.StatusText).Append("' hostScene=").Append(net.HostSceneName)
                .Append(" mismatch=").Append(net.SceneMismatch);
            if (net.IsConnected)
                foreach (int pid in net.GetSessionPlayerIdsSorted())
                {
                    sb.Append(" | p").Append(pid);
                    if (pid == net.LocalPlayerId) { sb.Append(" (me)"); continue; }
                    string s = net.SceneOf(pid);
                    if (!string.IsNullOrEmpty(s)) sb.Append(" scene=").Append(s);
                    int ping = net.GetPeerPing(pid);
                    if (ping >= 0) sb.Append(" ping=").Append(ping);
                }
            Out(sb.ToString());
        }

        // ------------------------------------------------------------ rooms

        private static List<Room> Rooms()
        {
            var list = new List<Room>();
            try
            {
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Room>())
                    if (r != null) list.Add(r);
            }
            catch (Exception e) { Guard.Swallow(e); }
            list.Sort((x, y) => string.CompareOrdinal(RoomLabel(x), RoomLabel(y)));
            return list;
        }

        private static string RoomLabel(Room r)
        {
            string n = string.IsNullOrEmpty(r.roomName) ? r.gameObject.name : r.roomName;
            return n.Replace(' ', '_');
        }

        private static void ListRooms()
        {
            var rooms = Rooms();
            for (int i = 0; i < rooms.Count; i++)
            {
                var r = rooms[i];
                Transform spawn = r.gotoSpawn != null ? r.gotoSpawn : r.transform;
                Out("  room #" + i + " " + RoomLabel(r) + " go=" + r.gameObject.name.Replace(' ', '_') + "@" + Pos(spawn.position)
                    + (r.containsElster ? " here" : ""));
            }
            Out("  " + rooms.Count + " rooms");
        }

        /// <summary>"room &lt;label|#index&gt;": stand on the room's spawn and enter it (the F7 Rooms tab, allowed in a session here).</summary>
        private static void GotoRoom(string want)
        {
            var rooms = Rooms();
            Room room = null;
            if (want.StartsWith("#", StringComparison.Ordinal))
            {
                int i = I(want.Substring(1));
                if (i >= 0 && i < rooms.Count) room = rooms[i];
            }
            else
                foreach (var r in rooms)
                    if (string.Equals(RoomLabel(r), want, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.gameObject.name, want, StringComparison.OrdinalIgnoreCase))
                    { room = r; break; }
            GameObject me = PlayerState.player;
            if (room == null || me == null) { Out("  no room " + want); return; }
            Transform spawn = room.gotoSpawn != null ? room.gotoSpawn : room.transform;
            me.transform.position = spawn.position;
            try { room.EnterRoom(); } catch (Exception e) { Guard.Swallow(e); }
            Out("  entered " + RoomLabel(room) + " at " + Pos(spawn.position));
        }

        private static float Dist(Vector3 p)
        {
            GameObject me = PlayerState.player;
            return me == null ? 0f : Vector3.Distance(me.transform.position, p);
        }

        /// <summary>Stand beside a target the way the player walks up to it (planar offset; up is -Z in SIGNALIS).</summary>
        private static void StandAt(Transform t, Room room = null, float off = 0.8f)
        {
            GameObject me = PlayerState.player;
            if (me == null || t == null) return;
            me.transform.position = t.position + new Vector3(off, 0f, 0f);
            try
            {
                var here = room ?? FindRoomOf(t);
                if (here != null && here != PlayerState.currentRoom) here.EnterRoom();
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        // An enemy wakes with the room of the EnemyManager that lists it (EnemyManager.Enter), which is not always the
        // room its transform sits under (a patrol placed across a doorway): enter that one.
        private static EnemyManager ManagerOf(EnemyController e)
        {
            var managers = WorldLookup.All<EnemyManager>();
            if (managers == null) return null;
            foreach (var m in managers)
            {
                var list = m != null ? m.enemies : null;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] == e) return m;
            }
            return null;
        }

        /// <summary>
        /// " nest=remote" for the ARAR of a remoteActivationOnly (or 0-range) nest: native Update never triggers it by distance; a
        /// boss script does (MED_MynahBoss, END_Boss), so a player cannot wake it by walking up (MED Morgue Enemy 3).
        /// </summary>
        private static string NestTag(EnemyController e)
        {
            try
            {
                var n = NestOf(e);
                // triggerRange 0: distance never triggers it either (BOS_Adler Falke nests).
                if (n != null && (n.remoteActivationOnly || n.triggerRange <= 0f)) return " nest=remote";
            }
            catch (Exception ex) { Guard.Swallow(ex); }
            return "";
        }

        // persistent: constant step directions (planar; up is -Z in SIGNALIS)
        private static readonly Vector3[] StepSides = { new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, -1f, 0f) };

        /// <summary>The AraNest whose ARAR this enemy is (AraNest.AraLogic), or null.</summary>
        private static AraNest NestOf(EnemyController e)
        {
            var nests = WorldLookup.All<AraNest>();
            if (nests == null || e == null) return null;
            foreach (var n in nests)
                if (n != null && n.AraLogic == e) return n;
            return null;
        }

        private static Room ManagerRoom(EnemyController e)
        {
            var m = ManagerOf(e);
            return m != null ? m.room : null;
        }

        /// <summary>"off" when the enemy's EnemyManager object is switched off (story-gated: native Enter cannot wake it).</summary>
        private static string ManagerState(EnemyController e)
        {
            var m = ManagerOf(e);
            return m == null ? "none" : m.gameObject.activeInHierarchy ? "on" : "off";
        }

        /// <summary>
        /// The object or a parent below its Room is switched off, and that parent is not one Room.EnterRoom switches on
        /// (chunk, Cell, instantChunk: Ghidra Room.c): story-gated, not asleep (DET Isolation KeyOfLove: active itself
        /// under an off holder, no peer could take it). An NGP_only holder counts as off while the NGP pref is false even
        /// before its room ever woke: NGP_only.Start (Ghidra NGP_only.c) only switches it off on the first wake.
        /// </summary>
        private static bool GatedOff(Transform t)
        {
            var room = FindRoomOf(t);
            bool ngp = FileBasedPrefs.GetBool("NGP", false);
            for (Transform q = t; q != null; q = q.parent)
            {
                if (room != null && q == room.transform) return false;
                var go = q.gameObject;
                if (!ngp && q.GetComponent<NGP_only>() != null) return true;
                if (go.activeSelf) continue;
                if (room != null && (go == room.chunk || go == room.Cell || go == room.instantChunk)) continue;
                return true;
            }
            return false;
        }

        private static Room FindRoomOf(Transform t)
        {
            for (Transform q = t; q != null; q = q.parent)
            {
                var r = q.GetComponent<Room>();
                if (r != null) return r;
            }
            return null;
        }

        // ------------------------------------------------------------ enemies

        private static void ListEnemies(float radius)
        {
            int n = 0, alive = 0;
            foreach (var kv in WorldRegistry.AllEnemies())
            {
                var e = kv.Value;
                if (e == null) continue;
                float d = Dist(e.transform.position);
                bool live = e.state != EnemyController.enemystate.dead;
                if (live) alive++;
                if (d > radius) continue;
                if (n++ >= 60) continue;
                Out("  " + kv.Key.ToString("X16") + " " + e.gameObject.name.Replace(' ', '_') + "@" + Pos(e.transform.position)
                    + " d=" + d.ToString("0.0", CultureInfo.InvariantCulture) + " state=" + e.state
                    + " hp=" + (e.hitbox != null ? e.hitbox.HP : -1) + " active=" + e.gameObject.activeInHierarchy + " mgr=" + ManagerState(e) + NestTag(e));
            }
            Out("  " + WorldRegistry.EnemyCount + " enemies, " + alive + " alive");
        }

        /// <summary>
        /// "hit &lt;name|near|id&gt; [dmg]" / "kill &lt;name|near|id&gt;": a player's hit on the nearest matching live enemy (Hitbox.HP lowered, then
        /// native TakeDamage, as PlayerAttack does), from beside it. On a client the mod forwards the hit to the host.
        /// </summary>
        private static void HitEnemy(string want, int damage, float hurt, float radius)
        {
            EnemyController best = null;
            float bestD = float.MaxValue;
            foreach (var kv in WorldRegistry.AllEnemies())
            {
                var e = kv.Value;
                if (e == null || e.state == EnemyController.enemystate.dead) continue;
                // "near" means what this player can reach now; a named one may sleep in another room (StandAt wakes it).
                if (want == "near" && !e.gameObject.activeInHierarchy) continue;
                if (want != "near" && want != "any"
                    && !string.Equals(kv.Key.ToString("X16"), want, StringComparison.OrdinalIgnoreCase)
                    && e.gameObject.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                float d = Dist(e.transform.position);
                if (want == "near" && d > radius) continue;
                if (d < bestD) { bestD = d; best = e; }
            }
            if (best == null) { Out("  no live enemy '" + want + "'"); return; }
            // A few metres off, as a player who fights it (hits here go through TakeDamage, not the gun's range).
            // A nest ARAR is walked up to the way a player meets it (Ghidra AraNest.c): into the nest's triggerRange
            // (Update sets triggered), then more than 3.2 away, since delayedLogic holds the ARAR while the player stands
            // within 3.2 of the nest. Standing 4 off the ARAR itself left the player under its nest and it never dropped
            // (test pilot MED_Medical Morgue Enemy 3 ARAR: "target stayed inactive").
            AraNest nest = want != "near" ? NestOf(best) : null;
            if (nest != null) StandAt(nest.transform, nest.room ?? ManagerRoom(best), Math.Min(1f, nest.triggerRange * 0.5f));
            else if (want != "near") StandAt(best.transform, ManagerRoom(best), 4f);
            int stepTries = 0;
            float nextStep = 0f;
            EnemyController target = best;
            // "kill": native TakeDamage at 0 HP sends an enemy critical (downed, HP back to its revive value); the next
            // hit while downed finishes it (Ghidra EnemyController.c TakeDamage). So kill hits again until it is dead,
            // as a player keeps shooting. On a client the state is the host's snapshot.
            bool kill = damage < 0;
            int hits = 0;
            float nextHit = 0f;
            Defer((kill ? "kill " : "hit ") + target.gameObject.name, () =>
            {
                if (target == null) return true;
                // Out past 3.2 once the nest is triggered. A step into a wall is pushed back by the character
                // controller (MED Morgue: 4 off ended 2.1 from the nest), so another side is tried until it holds.
                if (nest != null && nest.triggered && !target.gameObject.activeSelf && stepTries < 8
                    && Time.realtimeSinceStartup >= nextStep && Dist(nest.transform.position) < 3.6f)
                {
                    var me = PlayerState.player;
                    var side = StepSides[stepTries % StepSides.Length];
                    if (me != null) me.transform.position = nest.transform.position + side * (stepTries < 4 ? 5f : 7f);
                    stepTries++;
                    nextStep = Time.realtimeSinceStartup + 0.5f;
                }
                if (!Settled(target.gameObject)) return false;
                if (target.state == EnemyController.enemystate.dead)
                {
                    Out("  " + target.gameObject.name + " dead after " + hits + " hit(s)");
                    return true;
                }
                float now = Time.realtimeSinceStartup;
                if (now < nextHit) return false;
                if (hits > 0 && (!kill || hits >= 12))
                {
                    if (kill) Out("  " + target.gameObject.name + " still " + target.state + " after " + hits + " hits");
                    return true;
                }
                nextHit = now + 0.6f;
                var hb = target.hitbox;
                int before = hb != null ? hb.HP : -1;
                int dmg = kill ? Math.Max(before, 1) : damage;
                if (hb != null) hb.HP = before - dmg;
                target.TakeDamage(0f, 0f, hurt / 100f, true);
                hits++;
                Out("  hit " + target.gameObject.name + "@" + Pos(target.transform.position) + " for " + dmg + " hp " + before + "->"
                    + (hb != null ? hb.HP : -1) + " state=" + target.state);
                return false;
            });
            if (nest != null)
                _deferredDetail = () => "nest=" + nest.gameObject.name + " room=" + (nest.room != null ? nest.room.roomName : "-")
                    + " activated=" + nest.activated + " triggered=" + nest.triggered + " dead=" + nest.dead
                    + " range=" + nest.triggerRange + " d=" + Dist(nest.transform.position).ToString("F1")
                    + " ara=" + (nest.AraLogic != null ? nest.AraLogic.gameObject.activeSelf + "/" + nest.AraLogic.state : "null");
            else if (want != "near")
                _deferredDetail = () => "nest=none";
        }

        // ------------------------------------------------------------ pickups / interactions / doors

        private static List<ItemPickup> Pickups()
        {
            var list = new List<ItemPickup>();
            try
            {
                // Sleeping room chunks too (WorldLookup includes inactive objects): "take" enters the pickup's room.
                var sync = LanNetworkManager.Instance?.PickupSync;
                foreach (var p in WorldLookup.All<ItemPickup>())
                    if (p != null && p.enabled && !p.triggered && !Pickups_IsDropped(p) && (sync == null || !sync.IsClaimedPickup(p))
                        && (p.gameObject.activeSelf || FindRoomOf(p.transform) != null)) list.Add(p);
            }
            catch (Exception e) { Guard.Swallow(e); }
            return list;
        }

        private static bool Pickups_IsDropped(ItemPickup p)
        {
            try { return DroppedItemRegistry.IsDropped(p); } catch { return false; }
        }

        private static void ListPickups(float radius)
        {
            var list = Pickups();
            list.Sort((x, y) => Dist(x.transform.position).CompareTo(Dist(y.transform.position)));
            int n = 0;
            foreach (var p in list)
            {
                float d = Dist(p.transform.position);
                if (d > radius) continue;
                if (n++ >= 60) continue;
                Out("  " + WorldId.FromGameObject(p.gameObject).ToString("X16") + " " + p.gameObject.name.Replace(' ', '_')
                    + " item=" + WorldPickupSyncService.ResolveItem(p, false) + "x" + p.count + "@" + Pos(p.transform.position)
                    + " d=" + d.ToString("0.0", CultureInfo.InvariantCulture)
                    // switched off itself or under a switched-off holder (story-gated, not a sleeping room chunk):
                    // no player can take it yet
                    + (GatedOff(p.transform) ? " self=off" : "")
                    + " room=" + (FindRoomOf(p.transform) is Room r ? RoomLabel(r) : "-"));
            }
            Out("  " + list.Count + " pickup(s) left in the scene");
        }

        /// <summary>"take &lt;name|item|near|id&gt;": walk up to the pickup and press on it (Interaction.trigger, the yes/no that follows is autodlg's).</summary>
        private static void Take(string want)
        {
            ItemPickup best = null;
            float bestD = float.MaxValue;
            foreach (var p in Pickups())
            {
                if (want != "near"
                    && !string.Equals(WorldId.FromGameObject(p.gameObject).ToString("X16"), want, StringComparison.OrdinalIgnoreCase)
                    && p.gameObject.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0
                    && WorldPickupSyncService.ResolveItem(p, false).ToString().IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                float d = Dist(p.transform.position);
                if (d < bestD) { bestD = d; best = p; }
            }
            if (best == null) { Out("  no pickup '" + want + "'"); return; }
            StandAt(best.transform);
            ItemPickup target = best;
            string label = target.gameObject.name + " item=" + WorldPickupSyncService.ResolveItem(target, false) + "x" + target.count + " id="
                + WorldId.FromGameObject(target.gameObject).ToString("X16");
            Defer("take " + label, () =>
            {
                if (target == null) { Out("  take: the pickup is gone"); return true; }
                if (!Settled(target.gameObject)) return false;
                // What ItemPickup.Update does once the player's press set its Interaction triggered (Ghidra ItemPickup.c:
                // its own triggered, pickUp, then inter.triggered back to false). The Interactor's range / facing test is
                // skipped. inter.triggered must end false: Update returns early while triggered is set, and once release
                // clears triggered a still-set inter.triggered runs pickUp again (a second prompt and a second release).
                target.triggered = true;
                target.pickUp();
                var inter = target.inter;
                if (inter != null) inter.triggered = false;
                Out("  take " + label + " gameState=" + PlayerState.gameState);
                return true;
            });
        }

        private static void ListInteractions(float radius, string filter)
        {
            int n = 0;
            var list = new List<Interaction>();
            try
            {
                foreach (var it in UnityEngine.Object.FindObjectsOfType<Interaction>())
                    if (it != null && it.enabled) list.Add(it);
            }
            catch (Exception e) { Guard.Swallow(e); }
            list.Sort((x, y) => Dist(x.transform.position).CompareTo(Dist(y.transform.position)));
            foreach (var it in list)
            {
                float d = Dist(it.transform.position);
                string path = Owner(it);
                if (d > radius || (filter != null && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (n++ >= 60) continue;
                Out("  " + path + "@" + Pos(it.transform.position) + " d=" + d.ToString("0.0", CultureInfo.InvariantCulture)
                    + " type=" + it.type + " triggered=" + it.triggered + " inRange=" + it.inRange);
            }
            Out("  " + n + " interaction(s)");
        }

        /// <summary>A player's press on an interaction: what Interactor does (triggered, then trigger(); Ghidra Interactor.c).</summary>
        private static void Press(Interaction it)
        {
            it.setInRange(true);
            it.triggered = true;
            it.trigger();
        }

        private static string Owner(Component c)
        {
            Transform t = c.transform;
            string n = t.name;
            if (t.parent != null) n = t.parent.name + "/" + n;
            return n.Replace(' ', '_');
        }

        /// <summary>"use &lt;name&gt;": walk up to the nearest interaction whose object (or parent) name matches and press it.</summary>
        private static void Use(string want)
        {
            Interaction best = null;
            float bestD = float.MaxValue;
            try
            {
                foreach (var it in UnityEngine.Object.FindObjectsOfType<Interaction>())
                {
                    if (it == null || !it.enabled || Owner(it).IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    float d = Dist(it.transform.position);
                    if (d < bestD) { bestD = d; best = it; }
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
            if (best == null) { Out("  nothing to use named like " + want); return; }
            StandAt(best.transform);
            Interaction target = best;
            Defer("use " + Owner(target), () =>
            {
                if (target == null) return true;
                if (!Settled(target.gameObject)) return false;
                Press(target);
                Out("  used " + Owner(target) + "@" + Pos(target.transform.position) + " triggered=" + target.triggered);
                return true;
            });
        }

        private static void ListDoors(float radius)
        {
            int n = 0;
            foreach (var kv in WorldRegistry.AllDoubleDoors())
            {
                var d = kv.Value;
                if (d == null || Dist(d.transform.position) > radius) continue;
                if (n++ < 60)
                    Out("  double " + kv.Key.ToString("X16") + " " + Owner(d) + "@" + Pos(d.transform.position)
                        + " open=" + d.open + " locked=" + d.locked);
            }
            foreach (var kv in WorldRegistry.AllSlidingDoors())
            {
                var d = kv.Value;
                if (d == null || Dist(d.transform.position) > radius) continue;
                if (n++ < 60)
                    Out("  sliding " + kv.Key.ToString("X16") + " " + Owner(d) + "@" + Pos(d.transform.position)
                        + " open=" + d.opened + " moving=" + d.moving);
            }
            foreach (var kv in WorldRegistry.AllConnectedDoors())
            {
                var d = kv.Value;
                if (d == null || Dist(d.transform.position) > radius) continue;
                if (n++ < 60)
                    Out("  link " + kv.Key.ToString("X16") + " " + Owner(d) + "@" + Pos(d.transform.position) + " locked=" + d.locked);
            }
            Out("  " + n + " door(s)");
        }

        /// <summary>"door &lt;name|id&gt; [open|close]": the native open / close of a double door, or a sliding door's cycle.</summary>
        private static void Door(string want, bool open)
        {
            foreach (var kv in WorldRegistry.AllDoubleDoors())
            {
                var d = kv.Value;
                if (d == null || !Matches(kv.Key, d, want)) continue;
                StandAt(d.transform);
                Doorway_Double dd = d;
                Defer("door " + Owner(dd), () =>
                {
                    if (dd == null) return true;
                    if (!Settled(dd.gameObject)) return false;
                    // Open = a player's press (Interactor: inter.triggered, which Doorway_Double.Update turns into its
                    // cycle, or Deny when locked; Ghidra Doorway_Double.c). Close has no press: the native closeDoors.
                    if (open && dd.inter != null) Press(dd.inter);
                    else if (open) dd.Triggered();
                    else dd.closeDoors();
                    Out("  double " + Owner(dd) + " pressed, locked=" + dd.locked);
                    return true;
                });
                return;
            }
            foreach (var kv in WorldRegistry.AllSlidingDoors())
            {
                var d = kv.Value;
                if (d == null || !Matches(kv.Key, d, want)) continue;
                StandAt(d.transform);
                EventSlidingDoor sd = d;
                Defer("door " + Owner(sd), () =>
                {
                    if (sd == null) return true;
                    if (!Settled(sd.gameObject)) return false;
                    if (sd.opened != open) sd.cycle();
                    Out("  sliding " + Owner(sd) + " cycling to open=" + open);
                    return true;
                });
                return;
            }
            Out("  no door " + want);
        }

        private static bool Matches(ulong id, Component c, string want) =>
            string.Equals(id.ToString("X16"), want, StringComparison.OrdinalIgnoreCase)
            || Owner(c).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;

        // ------------------------------------------------------------ cutscenes

        private static void ListCutscenes()
        {
            int n = 0;
            try
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<CutsceneManager>())
                {
                    if (c == null || !c.gameObject.scene.IsValid()) continue;
                    n++;
                    Out("  " + WorldId.FromGameObject(c.gameObject).ToString("X16") + " " + Owner(c) + " completed=" + c.completed
                        + " running=" + (c.cutscene != null) + " active=" + c.gameObject.activeInHierarchy
                        + " onStart=" + c.onStart + " unskippable=" + c.unskippable);
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
            Out("  " + n + " cutscene(s)");
        }

        /// <summary>"cut &lt;name|id&gt;": start a cutscene the way its trigger does (its Interaction if it has one, else StartCutscene).</summary>
        private static void StartCutscene(string want)
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<CutsceneManager>())
            {
                if (c == null || !c.gameObject.scene.IsValid() || !Matches(WorldId.FromGameObject(c.gameObject), c, want)) continue;
                StandAt(c.transform);
                CutsceneManager cm = c;
                Defer("cut " + Owner(cm), () =>
                {
                    if (cm == null) return true;
                    if (!Settled(cm.gameObject)) return false;
                    if (cm.inter != null) Press(cm.inter);
                    else cm.StartCutscene();
                    Out("  started " + Owner(cm) + " via " + (cm.inter != null ? "press" : "StartCutscene"));
                    return true;
                });
                return;
            }
            Out("  no cutscene " + want);
        }

        private static void SkipCutscenes()
        {
            int n = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<CutsceneManager>())
            {
                if (c == null || c.cutscene == null) continue;
                c.Skip();
                n++;
                Out("  skipped " + Owner(c));
            }
            if (n == 0) Out("  no cutscene running");
        }

        // ------------------------------------------------------------ inventory text

        internal static string BagText()
        {
            var parts = new List<string>();
            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict != null)
                {
                    // Keys + native getCount: the Il2Cpp KeyValuePair enumerator returns garbage values (BagTrace).
                    var en = dict.Keys.GetEnumerator();
                    int count = dict.Count;
                    for (int step = 0; step < count && en.MoveNext(); step++)
                    {
                        var key = en.Current;
                        if (key == null || key._item == Items.itemlist.None) continue;
                        int n = InventoryManager.getCount(key);
                        if (n > 0) parts.Add(key._item + "x" + n);
                    }
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts.ToArray());
        }

        internal static string RingText()
        {
            var parts = new List<string>();
            try
            {
                var ring = PartyKeyRing.Export();
                if (ring != null) foreach (ushort k in ring) parts.Add(((Items.itemlist)k).ToString());
            }
            catch (Exception e) { Guard.Swallow(e); }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts.ToArray());
        }

        internal static string BoxText()
        {
            var parts = new List<string>();
            try
            {
                foreach (Items.itemlist it in Enum.GetValues(typeof(Items.itemlist)))
                {
                    if (it == Items.itemlist.None) continue;
                    var item = InventoryManager.getItem(it);
                    if (item == null) continue;
                    int n = InventoryManager.boxContainsItemCount(item);
                    if (n > 0) parts.Add(it + "x" + n);
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts.ToArray());
        }

        internal static string FlagText(StoryFlagEntry e)
        {
            switch (e.Kind)
            {
                case 0: return e.BoolVal ? "1" : "0";
                case 1: return e.IntVal.ToString(CultureInfo.InvariantCulture);
                case 2: return e.FloatVal.ToString("0.###", CultureInfo.InvariantCulture);
                case 3: return "'" + (e.StringVal ?? "").Replace('\n', ' ') + "'";
                default:
                    return e.FloatVal.ToString("0.##", CultureInfo.InvariantCulture) + "," + e.VecY.ToString("0.##", CultureInfo.InvariantCulture)
                        + "," + e.VecZ.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }
    }
}
