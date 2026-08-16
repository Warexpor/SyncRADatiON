// F11: spawn replika types at the local Elster from in-memory templates (never chapter-load).
using System.Collections.Generic;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Cheats
{
    public static class EntitySpawner
    {
        public static bool ShowMenu;

        public const string SpawnPrefix = "SR_Spawn_";
        public const string TemplatePrefix = "SR_Template_";

        private static readonly string[] TypeKeys =
        {
            "EULR", "STAR", "ARAR", "STCR", "MNHR", "KLBR", "KNCR", "ADLR"
        };

        private static readonly Dictionary<string, string> HomeHint = new Dictionary<string, string>
        {
            { "EULR", "PEN_Wreck" },
            { "STCR", "PEN_Wreck" },
            { "STAR", "LOV_Reeducation" },
            { "ARAR", "RES_Residential" },
            { "MNHR", "MED_Medical" },
            { "KLBR", "ROT_Rotfront" },
            { "KNCR", "BOS_Adler" },
            { "ADLR", "BOS_Adler" },
        };

        private static readonly Dictionary<string, EnemyController> Vault = new Dictionary<string, EnemyController>();
        private static int _nextSeq = 1;
        private static Vector2 _scrollPos;
        private static string _statusMessage = "";
        private static float _statusTimer;
        private static Rect _windowRect = new Rect(250f, 120f, 450f, 480f);

        public static bool BankLoadActive => false;

        public static void Tick() { }

        public static bool OnBankSceneLoaded(string sceneName) => false;

        public static void OnGUI()
        {
            if (!ShowMenu) return;
            _windowRect = GUI.Window(997, _windowRect, (GUI.WindowFunction)DrawWindow, "Entity Spawner (F11)");
        }

        private static void DrawWindow(int id)
        {
            float y = 20f;
            GUI.Label(new Rect(10, y, 430, 20), "Spawn at your Elster. Types bank after their chapter is loaded.");
            y += 22f;
            GUI.Label(new Rect(10, y, 430, 20), "Banked: " + Vault.Count + " / " + TypeKeys.Length);
            y += 24f;

            var view = new Rect(0, 0, 420, TypeKeys.Length * 28f + 8f);
            _scrollPos = GUI.BeginScrollView(new Rect(10, y, 430, 340), _scrollPos, view);
            float sy = 0f;
            for (int i = 0; i < TypeKeys.Length; i++)
            {
                string key = TypeKeys[i];
                bool have = HasTemplate(key);
                string hint;
                HomeHint.TryGetValue(key, out hint);
                string label = have ? key : key + "  (need " + hint + ")";
                if (GUI.Button(new Rect(15, sy, 400, 24), label))
                    RequestSpawn(key);
                sy += 28f;
            }
            GUI.EndScrollView();

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                y += 346f;
                GUI.Label(new Rect(10, y, 430, 20), _statusMessage);
                if (Time.realtimeSinceStartup > _statusTimer)
                    _statusMessage = "";
            }

            GUI.DragWindow();
        }

        public static void RequestSpawn(string typeKey)
        {
            if (string.IsNullOrEmpty(typeKey)) return;
            var pos = SpawnPos();
            float rotY = 0f;
            try
            {
                if (PlayerState.player != null)
                    rotY = PlayerState.player.transform.eulerAngles.y;
            }
            catch { }

            if (NetGate.Client)
            {
                LanNetworkManager.Instance?.SendEnemySpawnRequest(typeKey, pos, rotY);
                SetStatus("Requested host spawn: " + typeKey);
                PlaytestLog.Event("Spawn", "request " + typeKey);
                return;
            }

            FinishSpawn(typeKey, pos, rotY, 0, true);
        }

        public static void FinishSpawn(string typeKey, Vector3 pos, float rotY, int seq, bool broadcast)
        {
            HarvestLoaded();
            if (!HasTemplate(typeKey))
            {
                string hint;
                HomeHint.TryGetValue(typeKey, out hint);
                SetStatus("No " + typeKey + " in memory ? load " + hint + " once (F7)", true);
                PlaytestLog.Event("Spawn", "MISS template " + typeKey);
                return;
            }

            if (seq <= 0)
                seq = _nextSeq++;
            else if (seq >= _nextSeq)
                _nextSeq = seq + 1;

            string goName = SpawnPrefix + seq + "_" + typeKey;
            ulong id = WorldId.Compute("spawn", goName);
            EnemyController existing;
            if (WorldRegistry.TryGetEnemy(id, out existing) && existing != null)
            {
                PlaytestLog.Event("Spawn", "already id=" + id.ToString("X16"));
                return;
            }

            GameObject go;
            try { go = Object.Instantiate(Vault[typeKey].gameObject); }
            catch (System.Exception ex)
            {
                SetStatus("Instantiate failed: " + ex.Message, true);
                PlaytestLog.Event("Spawn", "Instantiate failed " + typeKey + " " + ex.Message);
                return;
            }
            if (go == null)
            {
                SetStatus("Instantiate returned null", true);
                return;
            }

            go.name = goName;
            go.SetActive(true);
            try
            {
                go.transform.SetParent(null, true);
                if (PlayerState.currentRoom != null)
                    go.transform.SetParent(PlayerState.currentRoom.transform, true);
            }
            catch { }

            go.transform.position = pos;
            var eul = go.transform.eulerAngles;
            eul.y = rotY;
            go.transform.eulerAngles = eul;

            var ec = go.GetComponent<EnemyController>();
            if (ec == null)
                ec = go.GetComponentInChildren<EnemyController>(true);
            if (ec == null)
            {
                Object.Destroy(go);
                SetStatus("Clone had no EnemyController", true);
                return;
            }

            try { ec.ResetEnemy(); } catch (System.Exception ex) { PlaytestLog.Event("Spawn", "ResetEnemy: " + ex.Message); }
            try
            {
                if (ec.agent != null) ec.agent.enabled = true;
                if (ec.seeker != null) ec.seeker.enabled = true;
                ec.enabled = true;
            }
            catch { }
            try { ec.WakeUp(); } catch (System.Exception ex) { PlaytestLog.Event("Spawn", "WakeUp: " + ex.Message); }
            try
            {
                if (PlayerState.player != null)
                {
                    ec.playerPos = PlayerState.player.transform;
                    ec.AimTarget = PlayerState.player.transform;
                    ec.IkTarget = PlayerState.player.transform;
                }
            }
            catch { }

            WorldRegistry.RegisterEnemy(id, ec);
            if (NetGate.Client)
            {
                try
                {
                    ec.enabled = false;
                    if (ec.agent != null) ec.agent.enabled = false;
                }
                catch { }
            }

            var net = LanNetworkManager.Instance;
            net?.EnemySync.RequestFullSend();
            PlaytestLog.Event("Spawn", "ok " + typeKey + " seq=" + seq + " id=" + id.ToString("X16")
                + " pos=" + pos.ToString("F1"));
            SetStatus("Spawned " + typeKey);

            if (broadcast && NetGate.Live && NetGate.Host)
                net?.BroadcastEnemySpawn(new EnemySpawnMessage
                {
                    Seq = seq,
                    TypeKey = typeKey,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotY = rotY
                });
        }

        public static void ApplyFromNet(EnemySpawnMessage msg)
        {
            if (msg.Seq <= 0 || string.IsNullOrEmpty(msg.TypeKey)) return;
            FinishSpawn(msg.TypeKey, new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.RotY, msg.Seq, false);
        }

        public static void DumpLiveSpawns(LanNetworkManager net)
        {
            if (net == null) return;
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null || e.gameObject == null) continue;
                int seq;
                string type;
                if (!TryParseSpawnName(e.gameObject.name, out seq, out type)) continue;
                var p = e.transform.position;
                net.BroadcastEnemySpawn(new EnemySpawnMessage
                {
                    Seq = seq,
                    TypeKey = type,
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    RotY = e.transform.eulerAngles.y
                });
            }
        }

        public static bool TryParseSpawnName(string name, out int seq, out string typeKey)
        {
            seq = 0;
            typeKey = "";
            if (string.IsNullOrEmpty(name) || !name.StartsWith(SpawnPrefix, System.StringComparison.Ordinal))
                return false;
            string rest = name.Substring(SpawnPrefix.Length);
            int under = rest.IndexOf('_');
            if (under <= 0) return false;
            if (!int.TryParse(rest.Substring(0, under), out seq) || seq <= 0) return false;
            typeKey = rest.Substring(under + 1);
            return typeKey.Length > 0;
        }

        public static bool IsTemplateObject(GameObject go)
        {
            if (go == null) return false;
            string n = go.name;
            return n.StartsWith(TemplatePrefix, System.StringComparison.Ordinal);
        }

        public static void HarvestLoaded()
        {
            HarvestControllers();
            HarvestSpawners();
        }

        private static void HarvestControllers()
        {
            EnemyController[] all = null;
            try { all = Resources.FindObjectsOfTypeAll<EnemyController>(); }
            catch
            {
                try { all = Object.FindObjectsOfType<EnemyController>(true); }
                catch { try { all = Object.FindObjectsOfType<EnemyController>(); } catch { return; } }
            }
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
                Consider(all[i]);
        }

        private static void HarvestSpawners()
        {
            EnemySpawner[] spawners = null;
            try { spawners = Resources.FindObjectsOfTypeAll<EnemySpawner>(); }
            catch
            {
                try { spawners = Object.FindObjectsOfType<EnemySpawner>(true); }
                catch { return; }
            }
            if (spawners == null) return;
            for (int i = 0; i < spawners.Length; i++)
            {
                var sp = spawners[i];
                if (sp == null) continue;
                GameObject prefab = null;
                try { prefab = sp.EnemyType; } catch { }
                if (prefab == null) continue;
                EnemyController ec = null;
                try { ec = prefab.GetComponent<EnemyController>(); } catch { }
                if (ec == null)
                {
                    try { ec = prefab.GetComponentInChildren<EnemyController>(true); } catch { }
                }
                Consider(ec);
            }
        }

        private static void Consider(EnemyController e)
        {
            if (e == null || e.gameObject == null) return;
            string n = e.gameObject.name;
            if (n.StartsWith(SpawnPrefix, System.StringComparison.Ordinal)) return;
            if (n.StartsWith(TemplatePrefix, System.StringComparison.Ordinal)) return;
            string key = TypeKeyOf(e);
            if (string.IsNullOrEmpty(key)) return;
            if (HasTemplate(key) && !BetterTemplate(e, Vault[key])) return;
            Stash(e, key);
        }

        private static void Stash(EnemyController src, string key)
        {
            try
            {
                var go = Object.Instantiate(src.gameObject);
                if (go == null) return;
                Object.DontDestroyOnLoad(go);
                go.name = TemplatePrefix + key;
                go.SetActive(false);
                var ec = go.GetComponent<EnemyController>();
                if (ec == null) ec = go.GetComponentInChildren<EnemyController>(true);
                if (ec == null)
                {
                    Object.Destroy(go);
                    return;
                }
                EnemyController old;
                if (Vault.TryGetValue(key, out old) && old != null && old.gameObject != null
                    && old.gameObject.name.StartsWith(TemplatePrefix, System.StringComparison.Ordinal))
                    Object.Destroy(old.gameObject);
                Vault[key] = ec;
                PlaytestLog.Event("Spawn", "banked " + key + " from '" + src.gameObject.name + "'");
            }
            catch (System.Exception ex)
            {
                PlaytestLog.Event("Spawn", "stash failed " + key + " " + ex.Message);
            }
        }

        private static bool HasTemplate(string key)
        {
            EnemyController e;
            return Vault.TryGetValue(key, out e) && e != null && e.gameObject != null;
        }

        private static bool BetterTemplate(EnemyController candidate, EnemyController current)
        {
            if (current == null || current.gameObject == null) return true;
            try
            {
                if (current.state == EnemyController.enemystate.dead
                    && candidate.state != EnemyController.enemystate.dead)
                    return true;
            }
            catch { }
            return false;
        }

        private static string TypeKeyOf(EnemyController e)
        {
            try
            {
                if (e.Preset != null)
                {
                    string t = e.Preset.Type.ToString();
                    if (IsKnownType(t)) return t;
                }
            }
            catch { }
            try
            {
                string n = e.gameObject.name ?? "";
                for (int i = 0; i < TypeKeys.Length; i++)
                {
                    string k = TypeKeys[i];
                    if (n == k || n.StartsWith(k + "_", System.StringComparison.Ordinal)
                        || n.StartsWith(k + " ", System.StringComparison.Ordinal)
                        || n.StartsWith(k + "(", System.StringComparison.Ordinal))
                        return k;
                }
            }
            catch { }
            return "";
        }

        private static bool IsKnownType(string t)
        {
            for (int i = 0; i < TypeKeys.Length; i++)
                if (TypeKeys[i] == t) return true;
            return false;
        }

        private static Vector3 SpawnPos()
        {
            try
            {
                if (PlayerState.player != null)
                    return PlayerState.player.transform.position
                        + PlayerState.player.transform.forward * 5f
                        + Vector3.up * 0.1f;
            }
            catch { }
            return Vector3.zero;
        }

        private static void SetStatus(string msg, bool isError = false)
        {
            _statusMessage = (isError ? "ERROR: " : "") + msg;
            _statusTimer = Time.realtimeSinceStartup + 4f;
        }
    }
}
