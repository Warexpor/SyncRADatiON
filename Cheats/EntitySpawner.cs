using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Cheats
{
    public static class EntitySpawner
    {
        public static bool ShowMenu;

        private static Vector2 _scrollPos;
        private static string _statusMessage = "";
        private static float _statusTimer;
        private static readonly List<EnemyEntry> _enemies = new List<EnemyEntry>();

        private static Rect _windowRect = new Rect(250f, 120f, 450f, 480f);

        public static void OnGUI()
        {
            if (!ShowMenu) return;

            RefreshEnemyList();
            _windowRect = GUI.Window(997, _windowRect, (GUI.WindowFunction)DrawWindow, "Entity Spawner (F7)");
        }

        private static void RefreshEnemyList()
        {
            _enemies.Clear();
            try
            {
                var controllers = Object.FindObjectsOfType<EnemyController>();
                foreach (var c in controllers)
                {
                    if (c == null || c.gameObject == null) continue;
                    string typeName = "Unknown";
                    try
                    {
                        if (c.Preset != null)
                            typeName = c.Preset.Type.ToString();
                    }
                    catch { }
                    _enemies.Add(new EnemyEntry
                    {
                        Name = c.gameObject.name,
                        TypeName = typeName,
                        GameObject = c.gameObject,
                        IsActive = c.gameObject.activeInHierarchy,
                        Distance = Vector3.Distance(c.transform.position, GetPlayerPos())
                    });
                }
            }
            catch { }
        }

        private static void DrawWindow(int id)
        {
            var y = 20f;
            GUI.Label(new Rect(10, y, 430, 20), "Click an enemy to clone it at player position");
            y += 24;
            GUI.Label(new Rect(10, y, 430, 20), _enemies.Count + " enemies in scene");
            y += 24;

            var viewRect = new Rect(0, 0, 420, _enemies.Count * 28f + 40f);
            _scrollPos = GUI.BeginScrollView(new Rect(10, y, 430, 340), _scrollPos, viewRect);
            var sy = 0f;

            foreach (var e in _enemies)
            {
                var label = (e.IsActive ? "" : "[INACTIVE] ") + e.Name + " (" + e.TypeName + ") " + e.Distance.ToString("F0") + "m";
                if (GUI.Button(new Rect(15, sy, 400, 24), label))
                    SpawnEnemy(e);
                sy += 28;
            }

            GUI.EndScrollView();

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                y += 340 + 6;
                GUI.Label(new Rect(10, y, 430, 20), _statusMessage);
                if (Time.realtimeSinceStartup > _statusTimer)
                    _statusMessage = "";
            }

            GUI.DragWindow();
        }

        private static void SpawnEnemy(EnemyEntry entry)
        {
            try
            {
                var playerPos = GetPlayerPos();
                var playerFwd = GetPlayerForward();
                var spawnPos = playerPos + playerFwd * 5f + Vector3.up * 0.1f;

                var go = Object.Instantiate(entry.GameObject, spawnPos, Quaternion.identity);
                if (go == null)
                {
                    SetStatus("Failed to spawn: Instantiate returned null", true);
                    return;
                }

                go.name = entry.Name + "_spawned";
                go.SetActive(true);

                var ec = go.GetComponent<EnemyController>();
                if (ec != null)
                {
                    try { ec.WakeUp(); } catch { }
                }

                SetStatus("Spawned: " + entry.Name + " at " + spawnPos.ToString("F1"));
            }
            catch (System.Exception ex)
            {
                SetStatus("Error: " + ex.Message, true);
            }
        }

        private static Vector3 GetPlayerPos()
        {
            try
            {
                if (PlayerState.player != null)
                    return PlayerState.player.transform.position;
            }
            catch { }
            return Vector3.zero;
        }

        private static Vector3 GetPlayerForward()
        {
            try
            {
                if (PlayerState.player != null)
                    return PlayerState.player.transform.forward;
            }
            catch { }
            return Vector3.forward;
        }

        private static void SetStatus(string msg, bool isError = false)
        {
            _statusMessage = (isError ? "ERROR: " : "") + msg;
            _statusTimer = Time.realtimeSinceStartup + 3f;
        }

        private class EnemyEntry
        {
            public string Name;
            public string TypeName;
            public GameObject GameObject;
            public bool IsActive;
            public float Distance;
        }
    }
}
