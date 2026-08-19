// Main orchestrator: guards, friendly fire (optional), network lifecycle
using MelonLoader;
using SyncRADation.Config;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Players;
using SyncRADation.Sync;
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation
{
    public static class ModRuntime
    {
        public static MelonLogger.Instance Log;
        public static LanNetworkManager Network { get; private set; }
        public static bool VerboseLogging => ModConfig.VerboseLogging?.Value == true;

        private static bool _running;
        private static HarmonyLib.Harmony _harmony;

        private static bool _lastLocalShooting;
        private static float _ffCooldown;

        public static void Start(MelonLogger.Instance log, HarmonyLib.Harmony harmony)
        {
            Log = log;
            _harmony = harmony;

            try
            {
                ModConfig.Bind();
                PatchAllSafe();

                Log.Msg("=============================================");
                Log.Msg("  " + PluginInfo.Name + " v" + PluginInfo.Version);
                Log.Msg("  " + PluginInfo.Description);
                Log.Msg("  Protocol v" + PluginInfo.ProtocolVersion + " | Port " + PluginInfo.DefaultPort);
                Log.Msg("  F2 menu | F3 quick connect | G/DROP drop | native TAKE pickup");
                Log.Msg("  FriendlyFire=" + (ModConfig.FriendlyFire?.Value == true)
                    + " VerboseLogging=" + VerboseLogging);
                Log.Msg("  grep: [Story] [Interact] [FMOD] [KeyRing] [StorageBox] [Scene] [Damage] [Door] [Puzzle] [Pickup] [Harmony] [Hitch] [Spawn]");
                Log.Msg("=============================================");

                Application.runInBackground = true;
            }
            catch (System.Exception ex)
            {
                Log.Error("ModRuntime.Start failed: " + ex);
            }

            EnsureRunning();
        }

        private static void PatchAllSafe()
        {
            var asm = typeof(ModRuntime).Assembly;
            var types = asm.GetTypes();
            int ok = 0;
            int fail = 0;
            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];
                try
                {
                    var attrs = t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), true);
                    if (attrs == null || attrs.Length == 0) continue;
                    _harmony.CreateClassProcessor(t).Patch();
                    ok++;
                }
                catch (System.Exception ex)
                {
                    fail++;
                    Log?.Warning("[Harmony] skip " + t.Name + ": " + ex.Message);
                }
            }
            Log?.Msg("[Harmony] patched " + ok + " classes, skipped " + fail);
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
            try { DroppedItemManager.TickDeferred(); } catch { }

            // Guard: PlayerState.player must never point at a remote proxy
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
                            Log?.Msg("[Guard] Restored PlayerState.player to local");
                            break;
                        }
                    }
                }
            }

            // Friendly fire only (opt-in). Enemy hits go through Harmony → EnemyController.TakeDamage
            // (see Patches/EnemyTakeDamagePatches) — not DIY raycasts.
            if (net != null && net.IsConnected && ModConfig.FriendlyFire?.Value == true)
            {
                _ffCooldown -= Mathf.Min(Time.deltaTime, 0.1f);
                bool curShooting = Input.GetButton("Fire1") || Input.GetMouseButton(0);
                if (curShooting && !_lastLocalShooting && _ffCooldown <= 0f)
                {
                    GameObject pl = PlayerState.player;
                    if (pl != null)
                    {
                        Vector3 origin = pl.transform.position + Vector3.up * 0.8f;
                        Vector3 dir = SourceAnimReader.ReadFacingWorldRotation(pl) * Vector3.forward;
                        if (dir.sqrMagnitude < 0.0001f)
                            dir = Vector3.forward;
                        else
                            dir.Normalize();
                        int wallMask = GetWallMask();
                        if (pm != null && pm.ProxyLayer >= 0)
                            wallMask |= (1 << pm.ProxyLayer);
                        RaycastHit hit;
                        if (Physics.Raycast(origin, dir, out hit, 50f, wallMask))
                        {
                            int hitPid = pm != null ? pm.GetPlayerIdByCollider(hit.collider) : -1;
                            if (hitPid >= 0)
                            {
                                float dmg = RemoteWeaponSync.GetDamage(ReadWeaponFromInventory());
                                net.SendFriendlyFire(hitPid, dmg, hit.point);
                                _ffCooldown = 0.2f;
                            }
                        }
                    }
                }
                _lastLocalShooting = curShooting;
            }
            else
            {
                _lastLocalShooting = Input.GetButton("Fire1") || Input.GetMouseButton(0);
            }

            NetworkDamageSystem.TickRespawn();
            Cheats.EntitySpawner.Tick();

            if (net != null && net.IsConnected)
                HitchTrace.Frame();

            try { net?.Update(); }
            catch (System.Exception ex) { Log?.Error("Network.Update crashed: " + ex); }

            if (pm != null)
            {
                foreach (int pid in GetProxyIds(pm))
                    pm.GetProxy(pid)?.AnimDriver?.PreTick();
            }
        }

        public static void OnLateUpdate()
        {
            try { Network?.LateUpdate(); }
            catch (System.Exception ex) { Log?.Error("Network.LateUpdate crashed: " + ex); }
        }

        public static void OnSceneChanged()
        {
            Log?.Msg("[Runtime] Scene changed");
            _lastLocalShooting = false;
            _ffCooldown = 0f;
            WorldRegistry.Rebuild();
            Cheats.EntitySpawner.HarvestLoaded();
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

        private static WeaponType ReadWeaponFromInventory()
        {
            try
            {
                var equipped = InventoryManager.EquippedWeapon;
                if (equipped == null || equipped.parentItem == null) return 0;
                return WeaponUtils.ItemToWeaponType(equipped.parentItem._item);
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
