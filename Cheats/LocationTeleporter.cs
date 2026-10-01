// F7 IMGUI: click a major chapter/room to teleport (uses native Cheats.cheat lvl/goto).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Cheats
{
    public static class LocationTeleporter
    {
        // persistent: F7 window state
        public static bool ShowMenu;

        // persistent: F7 window state
        private static Vector2 _scrollPos;
        // persistent: F7 window state
        private static string _statusMessage = "";
        // persistent: F7 window state
        private static float _statusTimer;
        // persistent: F7 window state
        private static int _tab; // 0 = chapters, 1 = rooms in current level
        // persistent: F7 window state
        private static Rect _windowRect = new Rect(220f, 80f, 420f, 520f);

        private static readonly LocationEntry[] Chapters =
        {
            new LocationEntry("Penrose (Wreck)", "PEN_Wreck"),
            new LocationEntry("Penrose (Hole)", "PEN_Hole"),
            new LocationEntry("Reeducation (normal)", "LOV_Reeducation"),
            new LocationEntry("Reeducation (corrupted)", "BIO_Reeducation"),
            new LocationEntry("Detention", "DET_Detention"),
            new LocationEntry("Medical", "MED_Medical"),
            new LocationEntry("Residential", "RES_Residential"),
            new LocationEntry("School", "RES_School"),
            new LocationEntry("Rotfront", "ROT_Rotfront"),
            new LocationEntry("Mines", "EXC_Mines"),
            new LocationEntry("Nowhere / Gestade", "EXC_Gestade"),
            new LocationEntry("Labyrinth", "LAB_Labyrinth"),
            new LocationEntry("Emptiness", "LAB_Emptiness"),
            new LocationEntry("Memory", "MEM_Memory"),
            new LocationEntry("Memory Gestade", "MEM_Gestade"),
            new LocationEntry("Boss Adler", "BOS_Adler"),
            new LocationEntry("Dead menu (Memory retry)", "DeadMenu"),
        };

        public static void OnGUI()
        {
            if (!ShowMenu) return;
            _windowRect = GUI.Window(996, _windowRect, (GUI.WindowFunction)DrawWindow, "Locations (F7)");
        }

        private static void DrawWindow(int id)
        {
            float y = 22f;
            GUI.Label(new Rect(10, y, 400, 18), "Click a location to jump there");
            y += 22f;

            if (GUI.Toggle(new Rect(10, y, 190, 22), _tab == 0, "Chapters", GUI.skin.button) && _tab != 0)
                _tab = 0;
            if (GUI.Toggle(new Rect(210, y, 190, 22), _tab == 1, "Rooms here", GUI.skin.button) && _tab != 1)
                _tab = 1;
            y += 28f;

            if (_tab == 0)
                DrawChapters(y);
            else
                DrawRooms(y);

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.Label(new Rect(10, 480, 400, 20), _statusMessage);
                if (Time.realtimeSinceStartup > _statusTimer)
                    _statusMessage = "";
            }

            GUI.DragWindow();
        }

        private static void DrawChapters(float y)
        {
            var view = new Rect(0, 0, 390, Chapters.Length * 26f + 8f);
            _scrollPos = GUI.BeginScrollView(new Rect(10, y, 400, 420), _scrollPos, view);
            float sy = 0f;
            foreach (var loc in Chapters)
            {
                if (GUI.Button(new Rect(10, sy, 370, 24), loc.Label))
                    LoadChapter(loc);
                sy += 26f;
            }
            GUI.EndScrollView();
        }

        private static void DrawRooms(float y)
        {
            var rooms = CollectRooms();
            var view = new Rect(0, 0, 390, Mathf.Max(40f, rooms.Count * 26f + 8f));
            _scrollPos = GUI.BeginScrollView(new Rect(10, y, 400, 420), _scrollPos, view);
            float sy = 0f;
            if (rooms.Count == 0)
                GUI.Label(new Rect(10, sy, 370, 24), "(no Room objects in this scene)");
            foreach (var r in rooms)
            {
                string label = string.IsNullOrEmpty(r.roomName) ? r.gameObject.name : r.roomName;
                if (r.containsElster) label = "* " + label;
                if (GUI.Button(new Rect(10, sy, 370, 24), label))
                    GotoRoom(r);
                sy += 26f;
            }
            GUI.EndScrollView();
        }

        // OnGUI runs several times per frame while the Rooms tab is open: scan + sort once per registry generation
        // (every scene load / rebuild bumps it), not per repaint.
        // persistent: cache keyed by WorldRegistry.Generation (self-invalidating)
        private static readonly List<Room> _rooms = new List<Room>();
        // persistent: cache key of _rooms
        private static int _roomsGeneration = -1;

        private static List<Room> CollectRooms()
        {
            int g = WorldRegistry.Generation;
            if (g == _roomsGeneration)
            {
                _rooms.RemoveAll(r => r == null);
                return _rooms;
            }
            _roomsGeneration = g;
            _rooms.Clear();
            try
            {
                var all = Object.FindObjectsOfType<Room>();
                if (all == null) return _rooms;
                foreach (var r in all)
                {
                    if (r != null) _rooms.Add(r);
                }
                _rooms.Sort((a, b) =>
                {
                    string an = a.roomName ?? a.name;
                    string bn = b.roomName ?? b.name;
                    return string.CompareOrdinal(an, bn);
                });
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return _rooms;
        }

        /// <summary>Chapter load by scene name (same path as the F7 click); used by the <c>--sync-scene</c> boot argument.</summary>
        public static void LoadChapterByName(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return;
            LoadChapter(new LocationEntry(scene, scene));
        }

        private static void LoadChapter(LocationEntry loc)
        {
            if (NetGate.Client)
            {
                // A client F7 makes the host load the chapter for everyone: only when the host allows client cheats.
                if (!Config.ModConfig.ClientCheatsAllowed)
                {
                    SetStatus("Host does not allow client cheats", true);
                    return;
                }
                SceneFollowService.RequestFollow(loc.Scene);
                SetStatus("Requested host load: " + loc.Scene);
                ShowMenu = false;
                return;
            }

            // Prefer AsyncLoader (same path as SceneFollow). Native Cheats.cheat often no-ops.
            string err = null;

            try
            {
                AsyncLoader.LoadLevel(loc.Scene);
                SetStatus("Loading via AsyncLoader: " + loc.Scene);
                ShowMenu = false;
                return;
            }
            catch (System.Exception ex)
            {
                err = "AsyncLoader: " + ex.Message;
            }

            try
            {
                var helpers = Object.FindObjectsOfType<SceneHelper>();
                SceneHelper sh = helpers != null && helpers.Length > 0 ? helpers[0] : null;
                if (sh != null)
                {
                    try
                    {
                        sh.LoadScene(loc.Scene);
                        SetStatus("Loading via SceneHelper: " + loc.Scene);
                        ShowMenu = false;
                        return;
                    }
                    catch (System.Exception ex)
                    {
                        err = "LoadScene: " + ex.Message;
                        try
                        {
                            sh.LoadSceneDirect(loc.Scene);
                            SetStatus("Loading via SceneHelper.Direct: " + loc.Scene);
                            ShowMenu = false;
                            return;
                        }
                        catch (System.Exception ex2)
                        {
                            err = "LoadSceneDirect: " + ex2.Message;
                        }
                    }
                }
                else
                    err = "no SceneHelper in scene";
            }
            catch (System.Exception ex)
            {
                err = "SceneHelper find: " + ex.Message;
            }

            try
            {
                if (Sync.NetGate.Live)
                    throw new System.InvalidOperationException("skip SceneManager while connected");
                UnityEngine.SceneManagement.SceneManager.LoadScene(loc.Scene);
                SetStatus("Loading via SceneManager: " + loc.Scene);
                ShowMenu = false;
                return;
            }
            catch (System.Exception ex)
            {
                err = (err != null ? err + " | " : "") + "SceneManager: " + ex.Message;
            }

            try
            {
                if (Sync.NetGate.Live)
                    throw new System.InvalidOperationException("skip cheat lvl while connected");
                global::Cheats.cheat("lvl " + loc.Scene);
                SetStatus("Sent cheat lvl " + loc.Scene + " (may no-op if console locked)");
                ShowMenu = false;
                return;
            }
            catch (System.Exception ex)
            {
                err = (err != null ? err + " | " : "") + "cheat: " + ex.Message;
            }

            SetStatus("Chapter load failed for " + loc.Scene + " — " + err, true);
        }

        private static void GotoRoom(Room room)
        {
            try
            {
                if (Sync.NetGate.Live)
                {
                    SetStatus("Rooms disabled while connected", true);
                    return;
                }

                string name = !string.IsNullOrEmpty(room.roomName) ? room.roomName : room.gameObject.name;

                // One path only: move the player to gotoSpawn + EnterRoom. The native console "goto" used to run
                // first and then this teleport + EnterRoom ran again on top of it; the console is often locked anyway.
                var player = PlayerState.player;
                Transform spawn = room.gotoSpawn != null ? room.gotoSpawn : room.transform;
                if (player == null || spawn == null)
                {
                    SetStatus("No player / spawn for " + name, true);
                    return;
                }
                player.transform.position = spawn.position;
                try { room.EnterRoom(); } catch (System.Exception e) { Guard.Swallow(e); }

                SetStatus("teleport " + name);
                ShowMenu = false;
            }
            catch (System.Exception ex)
            {
                SetStatus(ex.Message, true);
            }
        }

        private static void SetStatus(string msg, bool error = false)
        {
            _statusMessage = (error ? "ERR: " : "") + msg;
            _statusTimer = Time.realtimeSinceStartup + 4f;
            ModRuntime.Log?.Msg("[Locations] " + _statusMessage);
        }

        private struct LocationEntry
        {
            public readonly string Label;
            public readonly string Scene;
            public LocationEntry(string label, string scene) { Label = label; Scene = scene; }
        }
    }
}
