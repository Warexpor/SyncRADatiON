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
        // persistent: process logger
        public static MelonLogger.Instance Log;
        public static bool VerboseLogging => ModConfig.VerboseLogging?.Value == true;

        // persistent: boot-once flag (the network manager lives for the process)
        private static bool _running;
        // persistent: process Harmony instance
        private static HarmonyLib.Harmony _harmony;

        /// <summary>False when the boot audit found a Harmony target or reflected member that no longer resolves.</summary>
        // persistent: boot audit result
        public static bool PatchAuditOk { get; private set; } = true;
        /// <summary>One line for F2: "Harmony audit: N ok, M missing" (or "not run").</summary>
        // persistent: boot audit result
        public static string PatchAuditSummary { get; private set; } = "Harmony audit: not run";
        // persistent: boot audit result
        private static readonly System.Collections.Generic.List<string> _patchAuditMissing = new System.Collections.Generic.List<string>();
        /// <summary>First missing items (capped) for F2.</summary>
        public static System.Collections.Generic.IReadOnlyList<string> PatchAuditMissing => _patchAuditMissing;

        internal static void SetPatchAudit(bool ok, int okCount, int missingCount, long ms)
        {
            PatchAuditOk = ok;
            PatchAuditSummary = "Harmony audit: " + okCount + " ok, " + missingCount + " missing, " + ms + " ms";
            _patchAuditMissing.Clear();
        }

        /// <summary>Diagnostics off: the full audit did not run (F2 says so instead of a stale "not run").</summary>
        internal static void SetPatchAuditOff()
        {
            PatchAuditOk = true;
            PatchAuditSummary = "Harmony audit: off (Diagnostics pref)";
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

        // Friendly-fire shot edge: resynced on every scene load (SessionReset "FriendlyFireEdge", scene scope).
        private static int _ffShotSerial;
        // persistent: once per process (first scene load)
        private static bool _lateAuditDone;

        /// <summary>Scene load: a shot counted in the previous scene must not fire a ray in this one.</summary>
        internal static void ResyncFriendlyFireEdge() => _ffShotSerial = SourceAnimReader.ShotSerial;

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
                try { DumpFlushRegistrations.RegisterAll(); }
                catch (System.Exception ex) { Guard.Swallow("ModRuntime.DumpFlush", ex); }
                Log.Msg("[SessionReset] " + SessionReset.Count + " steps registered (scene " + SessionReset.CountOf(ResetScope.Scene)
                    + ", session " + SessionReset.CountOf(ResetScope.Session) + ", connection " + SessionReset.CountOf(ResetScope.Connection)
                    + "); " + DumpFlush.Count + " dump flushes");

                Log.Msg("=============================================");
                Log.Msg("  " + PluginInfo.Name + " v" + PluginInfo.Version);
                Log.Msg("  " + PluginInfo.Description);
                Log.Msg("  Protocol v" + PluginInfo.ProtocolVersion + " | Port " + PluginInfo.DefaultPort
                    + " | MaxPlayers " + PluginInfo.MaxPlayers + " | schema #" + NetSchema.Hash.ToString("X8"));
                Log.Msg("  Game build " + GameBuild.Label + " (handshake rejects a different build)");
                Log.Msg("  F2 menu | F3 quick connect | G/DROP drop | native TAKE pickup");
                Log.Msg("  FriendlyFire=" + (ModConfig.FriendlyFire?.Value == true)
                    + " VerboseLogging=" + VerboseLogging + " Diagnostics=" + ModConfig.DiagnosticsOn);
                Log.Msg("  Host MelonLoader log:");
                Log.Msg("    ~/.local/share/Steam/steamapps/common/SIGNALIS/MelonLoader/Latest.log");
                Log.Msg("  Client MelonLoader log:");
                Log.Msg("    ~/Work/MyProjects/SIGNALIS/MelonLoader/Latest.log");
                Log.Msg("  Prefs (each install): .../SIGNALIS/UserData/MelonPreferences.cfg");
                Log.Msg("  grep always-on: [Story] [Interact] [KeyRing] [StorageBox] [Scene] [Damage]");
                Log.Msg("    [Door] [Puzzle] [Pickup] [Harmony] [Hitch] [Spawn] [Enemy] [Proxy] [Weapon]");
                Log.Msg("  VerboseLogging also: [FMOD] Play/Stop, [Proxy]/[DRV] clone/FX, puzzle diffs");
                Log.Msg("  Diagnostics also: [Room] [Proxy] vis/jump, [Enemy] client/wake, [Move] [Bag] [EventCam],");
                Log.Msg("    [Hitch] phase=/stall, full [Harmony] audit");
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
            new LanNetworkManager(); // the constructor publishes LanNetworkManager.Instance
        }

        public static void OnUpdate()
        {
            var net = LanNetworkManager.Instance;
            var pm = net?.ProxyManager;
            HitchTrace.FrameBegin(net != null && net.IsConnected);
            long tp = HitchTrace.Begin();
            try { SyncRADation.UI.FreeCursor.Tick(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { EventCamTrace.Tick(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { BagTrace.Tick(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { MoveTrace.Tick(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { MenuHit.Tick(); } catch (System.Exception e) { Guard.Swallow(e); }
            try { DroppedItemRegistry.TickDeferred(); } catch (System.Exception e) { Guard.Swallow(e); }
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

            // Friendly fire only (opt-in, host's toggle while connected). Enemy hits go through Harmony →
            // EnemyController.TakeDamage (Domains/Enemies/Patches/EnemyTakeDamagePatches) — not DIY raycasts.
            // One ray per live round: the equipped magAmmo-drop edge SourceAnimReader also sends as AvatarCue.Fire
            // (a trigger press while not aiming, empty, reloading or in a menu fires nothing). Chest height: up is -Z.
            int shotSerial = SourceAnimReader.ShotSerial;
            if (net != null && net.IsConnected && ModConfig.FriendlyFireEnabled && shotSerial != _ffShotSerial)
            {
                GameObject pl = PlayerState.player;
                if (pl != null)
                {
                    Vector3 origin = pl.transform.position + new Vector3(0f, 0f, -0.8f);
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
                            // Clamped / NaN-checked again in SendFriendlyFire and on the receiving side.
                            float dmg = RemoteWeaponSync.GetDamage(WeaponUtils.EquippedWeaponType());
                            net.CombatHandlers.SendFriendlyFire(hitPid, dmg, hit.point);
                        }
                    }
                }
            }
            _ffShotSerial = shotSerial;

            HitchTrace.End("friendlyFire", tp);
            tp = HitchTrace.Begin();

            try { NetworkDamageSystem.TickRespawn(); }
            catch (System.Exception ex) { Guard.Swallow("ModRuntime.TickRespawn", ex); }
            HitchTrace.End("tickRespawn", tp);

            if (net != null && net.IsConnected)
                HitchTrace.Frame();

            tp = HitchTrace.Begin();
            try { net?.Update(); }
            catch (System.Exception ex) { Log?.Error("Network.Update crashed: " + ex); }
            HitchTrace.End("net.Update", tp);
            HitchTrace.FrameEnd();
        }

        public static void OnLateUpdate()
        {
            HitchTrace.LateBegin();
            long tp = HitchTrace.Begin();
            try { LanNetworkManager.Instance?.LateUpdate(); }
            catch (System.Exception ex) { Log?.Error("Network.LateUpdate crashed: " + ex); }
            HitchTrace.End("net.LateUpdate", tp);
            HitchTrace.LateEnd();
        }

        public static void OnSceneChanged()
        {
            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch (System.Exception e) { Guard.Swallow(e); }
            PlaytestLog.EventAlways("Scene", "loaded '" + scene + "'");
            if (!_lateAuditDone)
            {
                _lateAuditDone = true;
                try { PatchAudit.RunLate(); }
                catch (System.Exception e) { Guard.Swallow("ModRuntime.PatchAuditLate", e); }
            }
            // One scene scan, then every scripted object's WorldId is pinned before gameplay can Destroy a sibling (a later
            // sibling's index-based id would shift on this peer only). Runs solo too: hosting later in this scene must
            // still agree with a peer that loads it fresh. Invalidate first: a same-name reload has all-new instances.
            try
            {
                WorldLookup.Invalidate();
                WorldId.BeginScene(scene ?? "");
                WorldScan.BuildAndWarm();
            }
            catch (System.Exception e) { Guard.Swallow("ModRuntime.WorldIdWarm", e); }
            // Reuses that scan and the pinned ids (no second scene walk); read-only, so solo stays vanilla.
            WorldRegistry.Rebuild(rescan: false);
            // F11 template harvest (two Resources.FindObjectsOfTypeAll + template clones) only for a live session or an
            // open F11 window. FinishSpawn / AdoptNativeSpawn harvest on demand, so a session started later still works.
            if (NetGate.Live || Cheats.EntitySpawner.ShowMenu)
                Cheats.EntitySpawner.HarvestLoaded();
            // Before the network scene path: a wipe reload that has arrived resets the host's world state first, so the
            // full snapshot sent after the scene resets is the post-wipe one (clients reset locally on the Wipe message).
            try { HostReload.OnSceneArrived(scene); }
            catch (System.Exception e) { Guard.Swallow("ModRuntime.HostReloadArrived", e); }
            try { ChapterWipe.OnSceneArrived(scene); }
            catch (System.Exception e) { Guard.Swallow("ModRuntime.ChapterWipe", e); }
            // Every Scene-scope SessionReset step (Bootstrap/SessionResetRegistrations.cs), then the hello / dump.
            LanNetworkManager.Instance?.OnSceneChanged(scene);
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
            LanNetworkManager.Instance?.StopNetwork();
            _harmony?.UnpatchSelf();
            _running = false;
        }
    }
}
