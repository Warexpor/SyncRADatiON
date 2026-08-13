// F7 IMGUI: click a major chapter/room to teleport (uses native Cheats.cheat lvl/goto).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Cheats
{
    public static class LocationTeleporter
    {
        public static bool ShowMenu;

        private static Vector2 _scrollPos;
        private static string _statusMessage = "";
        private static float _statusTimer;
        private static int _tab; // 0 = chapters, 1 = rooms in current level
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

        private static List<Room> CollectRooms()
        {
            var list = new List<Room>();
            try
            {
                var all = Object.FindObjectsOfType<Room>();
                if (all == null) return list;
                foreach (var r in all)
                {
                    if (r != null) list.Add(r);
                }
                list.Sort((a, b) =>
                {
                    string an = a.roomName ?? a.name;
                    string bn = b.roomName ?? b.name;
                    return string.CompareOrdinal(an, bn);
                });
            }
            catch { }
            return list;
        }

        private static void LoadChapter(LocationEntry loc)
        {
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
                string name = !string.IsNullOrEmpty(room.roomName) ? room.roomName : room.gameObject.name;

                // Native goto first
                bool cheated = false;
                try { global::Cheats.cheat("goto " + name); cheated = true; } catch { }

                // Direct fallback: move player to gotoSpawn + EnterRoom
                var player = PlayerState.player;
                Transform spawn = room.gotoSpawn != null ? room.gotoSpawn : room.transform;
                if (player != null && spawn != null)
                {
                    player.transform.position = spawn.position;
                    try { room.EnterRoom(); } catch { }
                }

                SetStatus((cheated ? "goto " : "teleport ") + name);
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
