using MelonLoader;
using SyncRADation.Config;
using SyncRADation.Networking;
using SyncRADation.Players;
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation
{
    public static class ModRuntime
    {
        public static MelonLogger.Instance Log;
        public static LanNetworkManager Network { get; private set; }
        public static bool VerboseLogging { get; set; }
        public static bool TraversalActive { get; private set; }

        private static bool _running;
        private static HarmonyLib.Harmony _harmony;
        private static readonly List<RemoteAnimatorDriver> _animDrivers = new List<RemoteAnimatorDriver>();

        public static void Start(MelonLogger.Instance log, HarmonyLib.Harmony harmony)
        {
            Log = log;
            _harmony = harmony;

            try
            {
                ModConfig.Bind();
                _harmony.PatchAll();

                Log.Msg("================================================");
                Log.Msg("  " + PluginInfo.Name + " v" + PluginInfo.Version);
                Log.Msg("  F2=menu, F3=debug");
                Log.Msg("================================================");

                Application.runInBackground = true;
                EnsureRunning();
            }
            catch (System.Exception ex)
            {
                Log.Error("ModRuntime.Start failed: " + ex);
            }
        }

        public static void EnsureRunning()
        {
            if (_running) return;
            _running = true;

            var root = new GameObject("SyncRADation_Runtime");
            Object.DontDestroyOnLoad(root);

            Network = new LanNetworkManager();
        }

        private static float _lastCharStateLog;
        private static float _charStateStuckTime;
        private static int _lastCharState;
        private static float _gameStateStuckTime;
        private static int _lastGameState;
        private static Vector3 _lastPlayerPos;
        private static float _lastPlayerPosLogTime;
        private static bool _lastSuspendInput;
        private static bool _lastTraversing;
        private static bool _diagDumped;
        private static float _lastDoorLogTime;

        public static void OnUpdate()
        {
            var proxy = Players.RemotePlayerProxy.Instance;
            var net = Network;

            if (proxy != null && proxy.GameObject != null && PlayerState.player == proxy.GameObject)
            {
                var local = net?.GetLocalPlayer();
                if (local != null)
                    PlayerState.player = local;
            }

            if (!_diagDumped && proxy != null)
            {
                _diagDumped = true;
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                foreach (var f in typeof(PlayerState).GetFields(flags))
                {
                    object val = null;
                    try { val = f.GetValue(null); } catch { val = "??"; }
                    Log?.Msg("[DIAG] PlayerState." + f.Name + " = " + val);
                }
            }

            // Track charState — if stuck in non-idle for >3s, force idle
            int cs = (int)PlayerState.charState;
            if (cs != 0)
            {
                if (cs == _lastCharState)
                    _charStateStuckTime += Time.deltaTime;
                else
                    _charStateStuckTime = 0f;

                if (_charStateStuckTime > 3f)
                {
                    PlayerState.charState = PlayerState.charStates.idle;
                    Log?.Msg("[Guard] Force reset charState (stuck " + _charStateStuckTime.ToString("F1") + "s)");
                    _charStateStuckTime = 0f;
                }
            }
            else
            {
                _charStateStuckTime = 0f;
            }
            _lastCharState = cs;

            int gs = (int)PlayerState.gameState;
            if (gs != _lastGameState)
            {
                Log?.Msg("[DIAG] gameState changed: " + _lastGameState + " -> " + gs);
                _lastGameState = gs;
                _gameStateStuckTime = 0f;
            }

            if (gs != 0 && PlayerState.player != null)
            {
                _gameStateStuckTime += Time.deltaTime;
                if (_gameStateStuckTime > 10f)
                {
                    PlayerState.gameState = 0;
                    Log?.Msg("[Guard] Force reset gameState (stuck " + _gameStateStuckTime.ToString("F1") + "s)");
                    _gameStateStuckTime = 0f;
                }
            }
            else
            {
                _gameStateStuckTime = 0f;
            }

            if (PlayerState.suspendInput != _lastSuspendInput)
            {
                Log?.Msg("[DIAG] suspendInput changed: " + _lastSuspendInput + " -> " + PlayerState.suspendInput);
                _lastSuspendInput = PlayerState.suspendInput;
            }

            GameObject pObj = PlayerState.player;
            if (pObj != null)
            {
                var apc = pObj.GetComponent<AlternatePlayerController>();
                if (apc != null && apc.traversing != _lastTraversing)
                {
                    Log?.Msg("[DIAG] APC.traversing changed: " + _lastTraversing + " -> " + apc.traversing + " playing=" + apc.playing);
                    _lastTraversing = apc.traversing;
                }

                Vector3 ppos = pObj.transform.position;
                if ((ppos - _lastPlayerPos).sqrMagnitude > 0.01f && Time.time - _lastPlayerPosLogTime > 0.5f)
                {
                    Log?.Msg("[DIAG] player pos: " + ppos.ToString("F1") + " gs=" + gs + " cs=" + cs);
                    _lastPlayerPos = ppos;
                    _lastPlayerPosLogTime = Time.time;
                }
            }

            if (Time.time - _lastCharStateLog > 5f)
            {
                Log?.Msg("[DIAG] charState=" + cs + " gs=" + gs + " player=" + (pObj != null ? pObj.name : "NULL") + " suspendInput=" + PlayerState.suspendInput);
                _lastCharStateLog = Time.time;
            }

            // Log all nearby AutoTraverseDoors (by their A/B transform positions) every 3s
            if (pObj != null && Time.time - _lastDoorLogTime > 3f)
            {
                Vector3 pp = pObj.transform.position;
                foreach (var atd in GameObject.FindObjectsOfType<AutoTraverseDoor>())
                {
                    if (atd == null) continue;
                    Vector3? nearPos = null;
                    if (atd.A != null) nearPos = atd.A.position;
                    else if (atd.B != null) nearPos = atd.B.position;
                    else continue;
                    float d = Vector3.Distance(nearPos.Value, pp);
                    if (d > 25f) continue;
                    string side = atd.A != null && Vector3.Distance(atd.A.position, pp) < Vector3.Distance(atd.B.position, pp) ? "A" : "B";
                    Log?.Msg("[DOOR] ATD=" + atd.name + " con=" + (atd.connection != null ? atd.connection.name : "NULL")
                        + " side=" + side + " d=" + d.ToString("F1")
                        + " BA=" + atd.BA + " dir=" + atd.direction
                        + " c.inProg=" + (atd.connection != null ? atd.connection.inProgress.ToString() : "?")
                        + " c.single=" + (atd.connection != null ? atd.connection.singleUse.ToString() : "?")
                        + " c.fwd=" + (atd.connection != null ? atd.connection.forwards.ToString() : "?")
                        + " c.BT=" + (atd.connection != null ? atd.connection.BacktrackDoor.ToString() : "?")
                        + " c.BD=" + (atd.connection != null ? ((int)atd.connection.BacktrackingDirection).ToString() : "?"));
                }
                Log?.Msg("[DOOR] lastExitDoorA=" + PlayerState.lastExitDoorA.ToString("F1")
                    + " lastExitDoorB=" + PlayerState.lastExitDoorB.ToString("F1"));
                _lastDoorLogTime = Time.time;
            }

            for (int i = 0; i < _animDrivers.Count; i++)
                _animDrivers[i]?.PreTick();

            try { net?.Update(); }
            catch (System.Exception ex) { Log?.Error("Network.Update crashed: " + ex); }
        }

        public static void OnLateUpdate()
        {
            try { Network?.LateUpdate(); }
            catch (System.Exception ex) { Log?.Error("Network.LateUpdate crashed: " + ex); }

            for (int i = 0; i < _animDrivers.Count; i++)
                _animDrivers[i]?.Tick();

            for (int i = 0; i < _animDrivers.Count; i++)
                _animDrivers[i]?.LateTick();
        }

        public static void OnSceneChanged()
        {
            Log?.Msg("[Runtime] Scene changed, resetting local references");
            Network?.OnSceneChanged();
            _animDrivers.Clear();
        }

        public static void RegisterAnimDriver(RemoteAnimatorDriver driver)
        {
            if (driver != null && !_animDrivers.Contains(driver))
                _animDrivers.Add(driver);
        }

        public static void UnregisterAnimDriver(RemoteAnimatorDriver driver)
        {
            _animDrivers.Remove(driver);
        }

        public static void Stop()
        {
            Network?.StopNetwork();
            _animDrivers.Clear();
            _harmony?.UnpatchSelf();
            _running = false;
        }
    }
}
