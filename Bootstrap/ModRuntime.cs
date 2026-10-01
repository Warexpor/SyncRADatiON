// Main orchestrator: guards, friendly fire (optional), network lifecycle
using MelonLoader;
using SyncRADation.Config;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Players;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation
{
    public static class ModRuntime
    {
        public static MelonLogger.Instance Log;
        public static LanNetworkManager Network { get; private set; }
        public static bool VerboseLogging => ModConfig.VerboseLogging?.Value == true;

        // Boot/process-lifetime values (logger, harmony, running flag, FF edge state reset per scene): not session state.
        private static bool _running;
        private static HarmonyLib.Harmony _harmony;

        /// <summary>False when the boot audit found a Harmony target or reflected member that no longer resolves.</summary>
        public static bool PatchAuditOk { get; private set; } = true;
        /// <summary>One line for F2: "Harmony audit: N ok, M missing" (or "not run").</summary>
        public static string PatchAuditSummary { get; private set; } = "Harmony audit: not run";
        private static readonly System.Collections.Generic.List<string> _patchAuditMissing = new System.Collections.Generic.List<string>();
        /// <summary>First missing items (capped) for F2.</summary>
        public static System.Collections.Generic.IReadOnlyList<string> PatchAuditMissing => _patchAuditMissing;

        internal static void SetPatchAudit(bool ok, int okCount, int missingCount, long ms)
        {
            PatchAuditOk = ok;
            PatchAuditSummary = "Harmony audit: " + okCount + " ok, " + missingCount + " missing, " + ms + " ms";
            _patchAuditMissing.Clear();
        }

        /// <summary>A late (first scene load) audit item went missing: show it in F2 like a boot one.</summary>
        internal static void AddPatchAuditLate(string item)
        {
            PatchAuditOk = false;
            PatchAuditSummary += " + late: 1 missing";
            AddPatchAuditMissing(item);
        }

        internal static void AddPatchAuditMissing(string item)
        {
            if (_patchAuditMissing.Count < 8) _patchAuditMissing.Add(item);
        }

        private static bool _lastLocalShooting;
        private static float _ffCooldown;
        private static bool _lateAuditDone;

        public static void Start(MelonLogger.Instance log, HarmonyLib.Harmony harmony)
        {
            Log = log;
            _harmony = harmony;

            try
            {
                ModConfig.Bind();
                PatchAllSafe();
                try { HarmonyPhaseTiming.Install(_harmony, typeof(ModRuntime).Assembly.GetTypes()); }
                catch (System.Exception ex) { Guard.Swallow("ModRuntime.PhaseTiming", ex); }
                try { PatchAudit.Run(typeof(ModRuntime).Assembly.GetTypes()); }
                catch (System.Exception ex) { Guard.Swallow("ModRuntime.PatchAudit", ex); }
                GameBuild.Compute();
                try { SessionResetRegistrations.RegisterAll(); }
                catch (System.Exception ex) { Guard.Swallow("ModRuntime.SessionReset", ex); }
                Log.Msg("[SessionReset] " + SessionReset.Count + " clears registered");

                Log.Msg("=============================================");
                Log.Msg("  " + PluginInfo.Name + " v" + PluginInfo.Version);
                Log.Msg("  " + PluginInfo.Description);
                Log.Msg("  Protocol v" + PluginInfo.ProtocolVersion + " | Port " + PluginInfo.DefaultPort
                    + " | MaxPlayers " + PluginInfo.MaxPlayers + " | schema #" + NetSchema.Hash.ToString("X8"));
                Log.Msg("  Game build " + GameBuild.Label + " (handshake rejects a different build)");
                Log.Msg("  F2 menu | F3 quick connect | G/DROP drop | native TAKE pickup");
                Log.Msg("  FriendlyFire=" + (ModConfig.FriendlyFire?.Value == true)
                    + " VerboseLogging=" + VerboseLogging);
                Log.Msg("  Host MelonLoader log:");
                Log.Msg("    ~/.local/share/Steam/steamapps/common/SIGNALIS/MelonLoader/Latest.log");
                Log.Msg("  Client MelonLoader log:");
                Log.Msg("    ~/Work/MyProjects/SIGNALIS/MelonLoader/Latest.log");
                Log.Msg("  Prefs (each install): .../SIGNALIS/UserData/MelonPreferences.cfg");
                Log.Msg("  grep always-on: [Story] [Interact] [KeyRing] [StorageBox] [Scene] [Damage]");
                Log.Msg("    [Door] [Puzzle] [Pickup] [Harmony] [Hitch] [Spawn] [Enemy] [Proxy] [Weapon]");
                Log.Msg("  VerboseLogging also: [FMOD] Play/Stop, [Proxy]/[DRV] clone/FX, puzzle diffs");
                Log.Msg("  Hitch tags (spike-only): frame | send gap | recv | puzzle | enemy | boss");
                Log.Msg("    | pickup | weaponClone | 5s sendHz/recvHz/maxSend/maxRecv/maxDt/cost");
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
            HitchTrace.FrameBegin(net != null && net.IsConnected);
            long tp = HitchTrace.Begin();
            try { SyncRADation.UI.FreeCursor.Tick(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { EventCamTrace.Tick(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { BagTrace.Tick(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { SyncRADation.Patches.NoPausePatch.Tick(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { DroppedItemManager.TickDeferred(); } catch (System.Exception e) { Guard.Swallow(e); }
            HitchTrace.End("dropTick", tp);
            tp = HitchTrace.Begin();

            // Guard: PlayerState.player must never point at a remote proxy
            if (pm != null)
            {
                var local = net?.GetLocalPlayer();
                if (local != null && PlayerState.player != null && PlayerState.player != local)
                {
                    foreach (int pid in pm.GetProxyPlayerIds())
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

            HitchTrace.End("guard", tp);
            tp = HitchTrace.Begin();

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
                                float dmg = RemoteWeaponSync.GetDamage(WeaponUtils.EquippedWeaponType());
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

            HitchTrace.End("friendlyFire", tp);
            tp = HitchTrace.Begin();

            try { NetworkDamageSystem.TickRespawn(); }
            catch (System.Exception ex) { Guard.Swallow("ModRuntime.TickRespawn", ex); }
            HitchTrace.End("tickRespawn", tp);
            tp = HitchTrace.Begin();
            try { Cheats.EntitySpawner.Tick(); }
            catch (System.Exception ex) { Guard.Swallow("ModRuntime.EntitySpawnerTick", ex); }
            HitchTrace.End("entitySpawner", tp);

            if (net != null && net.IsConnected)
                HitchTrace.Frame();

            tp = HitchTrace.Begin();
            try { net?.Update(); }
            catch (System.Exception ex) { Log?.Error("Network.Update crashed: " + ex); }
            HitchTrace.End("net.Update", tp);

            tp = HitchTrace.Begin();
            if (pm != null)
            {
                foreach (int pid in pm.GetProxyPlayerIds())
                    pm.GetProxy(pid)?.AnimDriver?.PreTick();
            }
            HitchTrace.End("proxyPreTick", tp);
            HitchTrace.FrameEnd();
        }

        public static void OnLateUpdate()
        {
            HitchTrace.LateBegin();
            long tp = HitchTrace.Begin();
            try { Network?.LateUpdate(); }
            catch (System.Exception ex) { Log?.Error("Network.LateUpdate crashed: " + ex); }
            HitchTrace.End("net.LateUpdate", tp);
            HitchTrace.LateEnd();
        }

        public static void OnSceneChanged()
        {
            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch (System.Exception e) { Guard.Swallow(e); }
            PlaytestLog.Reset();
            PlaytestLog.Event("Scene", "loaded '" + scene + "'");
            _lastLocalShooting = false;
            _ffCooldown = 0f;
            if (!_lateAuditDone)
            {
                _lateAuditDone = true;
                try { PatchAudit.RunLate(); }
                catch (System.Exception e) { Guard.Swallow("ModRuntime.PatchAuditLate", e); }
            }
            WorldRegistry.Rebuild();
            Cheats.EntitySpawner.HarvestLoaded();
            // Before Network.OnSceneChanged: a wipe reload that has arrived resets the host's world state first, so the
            // full snapshot Network.OnSceneChanged sends is the post-wipe one (clients reset locally on the Wipe message).
            try { HostReload.OnSceneArrived(scene); }
            catch (System.Exception e) { Guard.Swallow("ModRuntime.HostReloadArrived", e); }
            Network?.OnSceneChanged();
        }

        private static int GetWallMask()
        {
            try
            {
                var pa = PlayerState.player?.GetComponentInChildren<PlayerAttack>(true);
                if (pa != null) return pa.WallMask;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
