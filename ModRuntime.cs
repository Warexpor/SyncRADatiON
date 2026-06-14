// SyncRADation — main orchestrator: proxy guard, charState watchdog, friendly fire, network lifecycle
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

        private static bool _running;
        private static HarmonyLib.Harmony _harmony;

        private static float _lastCharStateLog;
        private static int _lastCharState;
        private static float _charStateStuckTime;
        private static int _lastGameState;
        private static float _gameStateStuckTime;
        private static bool _lastSuspendInput;
        private static bool _lastLocalShooting;
        private static float _ffCooldown;

        public static void Start(MelonLogger.Instance log, HarmonyLib.Harmony harmony)
        {
            Log = log;
            _harmony = harmony;

            try
            {
                ModConfig.Bind();
                _harmony.PatchAll();

                Log.Msg("=============================================");
                Log.Msg("  " + PluginInfo.Name + " v" + PluginInfo.Version);
                Log.Msg("  " + PluginInfo.Description);
                Log.Msg("");
                Log.Msg("  Controls:");
                Log.Msg("    F2 - Multiplayer menu");
                Log.Msg("    F3 - Quick Connect");
                Log.Msg("");
                Log.Msg("  Protocol v" + PluginInfo.ProtocolVersion + " | Port " + PluginInfo.DefaultPort);
                Log.Msg("=============================================");

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

        public static void OnUpdate()
        {
            var pm = Network?.ProxyManager;
            var net = Network;

            // Guard: ensure PlayerState.player never points to a proxy
            if (pm != null)
            {
                var local = net?.GetLocalPlayer();
                if (local != null && PlayerState.player != null && PlayerState.player != local)
                {
                    foreach (int pid in GetProxyIds(pm))
                    {
                        var p = pm.GetProxy(pid);
                        if (p != null && p.GameObject == PlayerState.player)
                        {
                            PlayerState.player = local;
                            Log?.Msg("[Guard] Fixed PlayerState.player pointing to proxy, restored to local player");
                            break;
                        }
                    }
                }
            }

            // Track charState â€” if stuck in non-idle for >3s, force idle
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

            if (Time.time - _lastCharStateLog > 5f)
            {
                Log?.Msg("[DIAG] charState=" + cs + " gs=" + gs + " player=" + (pObj != null ? pObj.name : "NULL") + " suspendInput=" + PlayerState.suspendInput);
                _lastCharStateLog = Time.time;
            }

            // PreTick all proxy drivers (interpolation of animator params toward targets)
            if (pm != null)
            {
                foreach (int pid in GetProxyIds(pm))
                {
                    var driver = pm.GetProxy(pid)?.AnimDriver;
                    driver?.PreTick();
                }
            }

            // Friendly fire: detect local player's shot hitting a proxy
            if (net != null && net.IsConnected)
            {
                _ffCooldown -= Mathf.Min(Time.deltaTime, 0.1f);
                bool curShooting = PlayerState.shooting;
                if (curShooting && !_lastLocalShooting && _ffCooldown <= 0f)
                {
                    GameObject pl = PlayerState.player;
                    if (pl != null)
                    {
                        Vector3 origin = pl.transform.position + Vector3.up * 0.8f;
                        Quaternion facingRot;
                        var apc = pl.GetComponent<AlternatePlayerController>();
                        if (apc != null)
                            facingRot = Quaternion.Euler(0, apc.fAngle, 0);
                        else
                            facingRot = Quaternion.Euler(0, pl.transform.eulerAngles.y, 0);
                        Vector3 dir = facingRot * Vector3.forward;
                        // Use WallMask from PlayerAttack to prevent shooting through walls
                        // OR in proxy layer so we can detect proxy colliders
                        int wallMask = GetWallMask();
                        if (pm.ProxyLayer >= 0)
                            wallMask |= (1 << pm.ProxyLayer);
                        RaycastHit hit;
                        if (Physics.Raycast(origin, dir, out hit, 50f, wallMask))
                        {
                            int hitPid = pm.GetPlayerIdByCollider(hit.collider);
                            if (hitPid >= 0)
                            {
                                float dmg = RemoteWeaponSync.GetDamage(ReadWeaponFromInventory());
                                net.SendFriendlyFire(hitPid, dmg, hit.point);
                                _ffCooldown = 0.2f;
                            }
                            else
                            {
                                // Check if hit an enemy
                                var hb = hit.collider.GetComponentInChildren<Hitbox>(true);
                                if (hb != null)
                                {
                                    var ec = hb.GetComponentInParent<EnemyController>();
                                    if (ec != null)
                                    {
                                        float dmg = RemoteWeaponSync.GetDamage(ReadWeaponFromInventory());
                                        net.SendPlayerShotEnemy(ec.gameObject.GetInstanceID(), dmg);
                                        _ffCooldown = 0.2f;
                                    }
                                }
                            }
                        }
                    }
                }
                _lastLocalShooting = curShooting;
            }

            NetworkDamageSystem.TickRespawn();

            try { net?.Update(); }
            catch (System.Exception ex) { Log?.Error("Network.Update crashed: " + ex); }
        }

        public static void OnLateUpdate()
        {
            try { Network?.LateUpdate(); }
            catch (System.Exception ex) { Log?.Error("Network.LateUpdate crashed: " + ex); }
        }

        public static void OnSceneChanged()
        {
            Log?.Msg("[Runtime] Scene changed, resetting local references");
            NetworkDamageSystem.Reset();
            _lastLocalShooting = false;
            _ffCooldown = 0f;
            Network?.OnSceneChanged();
        }

        private static IEnumerable<int> GetProxyIds(PlayerProxyManager pm)
        {
            for (int i = 0; i < 256; i++)
            {
                if (pm.HasProxy(i))
                    yield return i;
            }
        }

        private static Networking.WeaponType ReadWeaponFromInventory()
        {
            try
            {
                var equipped = InventoryManager.EquippedWeapon;
                if (equipped == null || equipped.parentItem == null) return 0;
                return Networking.WeaponUtils.ItemToWeaponType(equipped.parentItem._item);
            }
            catch { return 0; }
        }

        private static int GetWallMask()
        {
            try
            {
                var pa = PlayerState.player?.GetComponentInChildren<PlayerAttack>(true);
                if (pa != null) return pa.WallMask;
            }
            catch { }
            return ~0;
        }

        public static void Stop()
        {
            Network?.StopNetwork();
            _harmony?.UnpatchSelf();
            _running = false;
        }
    }
}
