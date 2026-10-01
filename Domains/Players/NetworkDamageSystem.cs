// Native HurtElster is the only HP path. Co-op death: any player (host or client) who dies while
// a teammate lives is DOWNED (input off, cloaked so enemies ignore them, proxy shows dead), then
// the host respawns them next to the nearest living teammate after DownedRespawnDelay seconds.
// The party wipes (host reloads its last save via HostReload, clients restore bag snapshots and follow the
// host's scene load) only when every player is down. Solo / no party: nothing here runs, the native game over
// stays vanilla.
using FMODUnity;
using SyncRADation.Config;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Players
{
    public static class NetworkDamageSystem
    {
        /// <summary>Everyone down for this long before the host pulls the wipe (lets the death land).</summary>
        private const float WipeDelay = 3f;
        private const float WipeDebounce = 6f;
        private const float RoomReportInterval = 0.5f;
        private const float HostTickInterval = 0.25f;
        /// <summary>
        /// A downed player drops its bag only after this grace, and only if a teammate is still up then. Two players
        /// dying together learn about each other within an RTT; deciding at the moment of death dropped both bags just
        /// before the wipe reload destroyed them.
        /// </summary>
        private const float DropGrace = 1.5f;
        private const float WipeLogInterval = 5f;

        private static bool _isDead;
        private static float _downAt;
        private static bool _prevCloaked;
        private static float _allDownSince = -1f;
        private static float _wipeAt = -99f;
        private static float _hostTick;
        private static float _roomTimer;
        private static float _roomRefresh;
        private const float RoomRefreshInterval = 5f;
        private static string _lastRoomSent = "";
        private static bool _hasPendingRevive;
        private static PartyLifeMessage _pendingRevive;
        private static float _lastSafetyLog = -99f;
        private static bool _dropPending;
        private static float _dropAt;
        private static float _wipeBlockedLog = -99f;
        private static Animator _deathAnim;
        private static GameObject _deathAnimFor;
        // Animator.StringToHash once: ApplyDownedLocal polls the Dead bool every frame while downed.
        private static readonly int DeadHash = Animator.StringToHash("Dead");
        private static readonly int DieHash = Animator.StringToHash("Die");

        public static bool IsDead => _isDead;

        /// <summary>A real party exists (2+ session players on a live link). Gates all co-op death behaviour.</summary>
        public static bool PartyLive
        {
            get
            {
                var net = ModRuntime.Network;
                return net != null && net.IsConnected && net.GetPlayerCount() >= 2;
            }
        }

        public static float PlayerHP
        {
            get
            {
                try { return PlayerState.hp; } catch { return 0f; }
            }
        }

        public static float MaxHP => 100f;

        // ------------------------------------------------------------------ native hooks

        /// <summary>GameOverHandler.hurt prefix: true = skip the native game over screen.</summary>
        public static bool ShouldSuppressNativeGameOver()
        {
            if (!PartyLive) return false;
            if (_isDead) return true;
            try
            {
                if (global::Cheats.buddha || global::Cheats.kami || global::Cheats.hastur) return false;
            }
            catch (System.Exception ex) { LogOnce("cheat flags", ex); }
            try
            {
                if (PlayerState.hp >= 2) return false;
                var tool = InventoryManager.EquippedTool;
                if (tool != null && tool._item == Items.itemlist.Injector) return false;
            }
            catch (System.Exception ex) { LogOnce("suppress check", ex); return false; }
            return true;
        }

        /// <summary>PlayerState.HurtElster prefix gate (native: only in play / dialogue).</summary>
        public static bool HurtGateOpen()
        {
            try
            {
                var gs = PlayerState.gameState;
                return gs == PlayerState.gameStates.play || gs == PlayerState.gameStates.dialogue;
            }
            catch (System.Exception ex) { LogOnce("hurt gate", ex); return false; }
        }

        /// <summary>PlayerState.HurtElster postfix: native damage finished and hp is at the native death threshold.</summary>
        public static void OnNativeHurt()
        {
            if (_isDead || !PartyLive) return;
            try
            {
                if (global::Cheats.buddha || global::Cheats.kami || global::Cheats.hastur) return;
                if (PlayerState.hp >= 2) return;
            }
            catch (System.Exception ex) { LogOnce("hurt post", ex); return; }
            PlaytestLog.Event("Damage", "native lethal hit hp=" + PlayerState.hp);
            Die();
        }

        public static void ApplyDamage(float damage, Vector3 hitPoint, Vector3 hitDir)
        {
            if (_isDead) return;
            try
            {
                if (PlayerState.hp <= 0 || PlayerState.charState == PlayerState.charStates.dead)
                    return;
            }
            catch (System.Exception ex) { LogOnce("damage pre", ex); }

            try
            {
                PlayerState.HurtElster((int)damage, new Vector2(hitDir.x, hitDir.z));
            }
            catch (System.Exception hurtEx)
            {
                LogOnce("HurtElster", hurtEx);
                try
                {
                    var hurtSound = PlayerState.player?.GetComponent<ElsterHurtSound>();
                    if (hurtSound != null && !string.IsNullOrEmpty(hurtSound.HurtSound))
                        RuntimeManager.PlayOneShot(hurtSound.HurtSound, hitPoint);
                }
                catch (System.Exception ex) { LogOnce("hurt sound", ex); }

                try { PlayerState.hp = Mathf.Max(0, PlayerState.hp - (int)damage); } catch (System.Exception ex) { LogOnce("hp set", ex); }
                try { PlayerState.charState = PlayerState.charStates.grabbed; } catch (System.Exception ex) { LogOnce("charState set", ex); }
                try
                {
                    var anim = PlayerState.player?.GetComponentInChildren<Animator>(true);
                    if (anim != null)
                    {
                        anim.SetFloat("HurtTime", 1f);
                        anim.SetBool("Injured", true);
                    }
                }
                catch (System.Exception ex) { LogOnce("hurt anim", ex); }
            }

            int hp = 0;
            try { hp = PlayerState.hp; } catch (System.Exception ex) { LogOnce("hp read", ex); }
            PlaytestLog.Event("Damage", "-" + damage.ToString("F0") + " HP remain=" + hp);

            // Native postfix normally downs us inside HurtElster; this is the fallback path.
            if (hp < 2 && PartyLive)
                Die();
        }

        // ------------------------------------------------------------------ downed

        private static void Die()
        {
            if (_isDead) return;
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || !PartyLive) return;

            _isDead = true;
            _downAt = Time.unscaledTime;
            _hasPendingRevive = false;
            PartySaveService.NoteBagAtDown();
            ApplyDownedLocal(true);

            // Floor drop is decided DropGrace later (TickDownedDrop): if everyone is down by then the wipe reload
            // follows and dropped items would only be destroyed.
            _dropPending = true;
            _dropAt = _downAt + DropGrace;

            try
            {
                var hurtSound = PlayerState.player?.GetComponent<ElsterHurtSound>();
                if (hurtSound != null && !string.IsNullOrEmpty(hurtSound.DeathSound))
                    RuntimeManager.PlayOneShot(hurtSound.DeathSound);
            }
            catch (System.Exception ex) { LogOnce("death sound", ex); }

            net.SendDeathPolicy(DeathKind.ClientDowned);
            try { net.AvatarHandlers.SendLocalVital(); }
            catch (System.Exception ex) { LogOnce("vital send", ex); }
            PlaytestLog.Event("Damage", (net.Role == NetworkRole.Host ? "host" : "client")
                + " downed — respawn in " + ModConfig.DownedRespawnSeconds.ToString("F0") + "s");
        }

        private static void ApplyDownedLocal(bool firstTime)
        {
            try { PlayerState.charState = PlayerState.charStates.dead; } catch (System.Exception ex) { LogOnce("down charState", ex); }
            try { PlayerState.suspendInput = true; } catch (System.Exception ex) { LogOnce("down suspendInput", ex); }
            try
            {
                if (firstTime) _prevCloaked = PlayerState.cloaked;
                // EnemyVision.CanSee returns false while cloaked: enemies lose the downed player.
                PlayerState.cloaked = true;
            }
            catch (System.Exception ex) { LogOnce("down cloak", ex); }

            // Every frame while downed (firstTime or not): a scene change swaps the player object and its animator
            // comes up alive, so re-assert the dead pose whenever the Dead bool is not set.
            try
            {
                var anim = DeathAnimator();
                if (anim != null && !anim.GetBool(DeadHash))
                {
                    anim.SetTrigger(DieHash);
                    anim.SetBool(DeadHash, true);
                }
            }
            catch (System.Exception ex) { LogOnce("down anim", ex); }
        }

        /// <summary>The animator ElsterDeathHandler drives (its own <c>anim</c>), falling back to the first one in the tree.</summary>
        private static Animator DeathAnimator()
        {
            var player = PlayerState.player;
            if (player == null) return null;
            if (_deathAnimFor == player && _deathAnim != null) return _deathAnim;
            Animator anim = null;
            try
            {
                var handler = player.GetComponent<ElsterDeathHandler>();
                if (handler != null) anim = handler.anim;
            }
            catch (System.Exception ex) { LogOnce("death handler anim", ex); }
            if (anim == null)
                anim = player.GetComponentInChildren<Animator>(true);
            _deathAnimFor = player;
            _deathAnim = anim;
            return anim;
        }

        /// <summary>While downed: after DropGrace, drop the bag if a teammate is still up (else the wipe reload follows).</summary>
        private static void TickDownedDrop(LanNetworkManager net)
        {
            if (!_dropPending || Time.unscaledTime < _dropAt) return;
            _dropPending = false;
            if (PartyVitals.AnyOtherAlive(net))
                DropInventoryOnDeath();
            else
                PlaytestLog.Event("Damage", "last player down — skip floor drop (wipe follows)");
        }

        private static void DropInventoryOnDeath()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected) return;
            var player = PlayerState.player;
            if (player == null) return;

            Vector3 pos = DroppedItemManager.FloorDropPos(player.transform);

            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict == null) return;

                var itemsToDrop = new System.Collections.Generic.List<(AnItem item, int count, Items.itemlist enumVal)>();
                var enumerator = dict.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var kvp = enumerator.Current;
                    var item = kvp.key;
                    int count = kvp.value;
                    if (item == null || count <= 0) continue;
                    Items.itemlist itemEnum;
                    try { itemEnum = item._item; } catch { continue; }
                    if (itemEnum == Items.itemlist.None || itemEnum == Items.itemlist.Injector) continue;
                    itemsToDrop.Add((item, count, itemEnum));
                }
                enumerator.Dispose();

                int n = 0;
                foreach (var entry in itemsToDrop)
                {
                    // Party ring is SoT for unique Key/Object. Bag copies are EnsureInBag /
                    // grant mirrors. Floor-dropping them DetachDroppedKey-clears the ring
                    // for every peer while box / WorldId claim may still hold the real unique
                    // → ghost floor + UseItem softlock. Never floor uniques on death: Note
                    // onto the ring if the bag somehow held one the ring missed (race), then
                    // strip the bag mirror only. G-drop still transfers ownership.
                    if (PartyKeyRing.IsKeyOrObject(entry.enumVal))
                    {
                        if (!PartyKeyRing.Has(entry.enumVal))
                        {
                            PartyKeyRing.Note(entry.enumVal);
                            PartyKeyRing.OfferToHost(entry.item);
                            PlaytestLog.Event("Damage", "death note ring unique " + entry.enumVal);
                        }
                        try { InventoryManager.RemoveItem(entry.item, entry.count); }
                        catch (System.Exception ex) { LogOnce("death strip unique", ex); }
                        PlaytestLog.Event("Damage", "death skip floor unique " + entry.enumVal);
                        continue;
                    }

                    ushort idx = net.AllocateItemIndex();
                    int key = (net.LocalPlayerId << 16) | idx;
                    Vector3 dropPos = pos + new Vector3(
                        UnityEngine.Random.Range(-0.35f, 0.35f),
                        UnityEngine.Random.Range(-0.08f, 0.08f),
                        0f);
                    n++;
                    DroppedItemManager.SpawnLocalItem(entry.enumVal, entry.count, key, dropPos);

                    net.SendDropItem(new Networking.DropItemSpawnMessage
                    {
                        SenderID = (byte)net.LocalPlayerId,
                        LocalIndex = idx,
                        ItemEnum = (ushort)entry.enumVal,
                        Count = entry.count,
                        PosX = dropPos.x,
                        PosY = dropPos.y,
                        PosZ = dropPos.z
                    });

                    try { InventoryManager.RemoveItem(entry.item, entry.count); }
                    catch (System.Exception ex) { LogOnce("death remove item", ex); }
                    PartyKeyRing.Remove(entry.enumVal);
                }
                if (net.Role == NetworkRole.Host)
                    PartyKeyRing.Broadcast();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[DeathDrop] Failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ wire handlers

        public static void HandleDeathPolicy(DeathPolicyMessage msg)
        {
            var net = ModRuntime.Network;
            if (net == null) return;
            if (msg.SenderPlayerId == net.LocalPlayerId) return;

            if (msg.Kind == DeathKind.ClientDowned)
            {
                PartyVitals.NoteDown(msg.SenderPlayerId);
                return;
            }

            // Legacy HostWipeReload: wipes now travel as PartyLife(Wipe); clients never load their own slot.
            if (msg.Kind == DeathKind.HostWipeReload)
                PlaytestLog.Event("Damage", "legacy HostWipeReload ignored (PartyLife wipe is authoritative)");
        }

        /// <summary>Every peer: host announced a revive.</summary>
        public static void ApplyRevive(PartyLifeMessage msg)
        {
            var net = ModRuntime.Network;
            if (net == null) return;

            if (msg.PlayerId != net.LocalPlayerId)
            {
                PartyVitals.NoteRevived(msg.PlayerId);
                try { net.ProxyManager.GetProxy(msg.PlayerId)?.SetVital(false); }
                catch (System.Exception ex) { LogOnce("proxy revive", ex); }
                PlaytestLog.Event("Damage", "peer " + msg.PlayerId + " respawned");
                return;
            }

            if (!_isDead) return;
            _pendingRevive = msg;
            _hasPendingRevive = true;
            TryApplyPendingRevive();
        }

        private static void TryApplyPendingRevive()
        {
            if (!_hasPendingRevive) return;
            if (PlayerState.player == null || SceneFollowService.LocalIsTransient()) return;
            _hasPendingRevive = false;
            var msg = _pendingRevive;

            // The teleport target is in msg.Scene; if we loaded somewhere else meanwhile its XYZ is meaningless here.
            bool teleport = msg.HasPos && SceneMatches(msg.Scene);
            if (teleport)
            {
                try { PlayerState.player.transform.position = new Vector3(msg.PosX, msg.PosY, msg.PosZ); }
                catch (System.Exception ex) { LogOnce("revive teleport", ex); }
                EnterRoomByName(msg.Room);
            }

            ClearDownedLocal(msg.Hp > 0 ? msg.Hp : ReviveHp(), false);
            PlaytestLog.Event("Damage", "respawned hp=" + PlayerState.hp
                + (teleport ? " at teammate (" + msg.PosX.ToString("F1") + "," + msg.PosZ.ToString("F1") + ")"
                    : (msg.HasPos ? " in place (teammate scene '" + msg.Scene + "' != here)" : " in place")));
            var net = ModRuntime.Network;
            try { net?.AvatarHandlers.SendLocalVital(); }
            catch (System.Exception ex) { LogOnce("vital send", ex); }
        }

        private static bool SceneMatches(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return true;
            try { return string.Equals(SceneManager.GetActiveScene().name, scene, System.StringComparison.Ordinal); }
            catch (System.Exception ex) { LogOnce("scene match", ex); return true; }
        }

        /// <summary>Client: host wiped the party. No own-slot load: bag from the save snapshot, follow the host reload.</summary>
        public static void ApplyWipeAsClient(PartyLifeMessage msg)
        {
            var net = ModRuntime.Network;
            if (net == null) return;
            PlaytestLog.Event("Damage", "party wipe — host reloads '" + msg.Scene + "'");
            // The host reverted to a save, this peer never loads a slot: story progress is replaced by the host's next full dump.
            try { net.StorySync.OnPartyWipe(); }
            catch (System.Exception ex) { LogOnce("wipe story", ex); }

            var token = new PartySaveToken { Slot = msg.SaveSlot, Counter = msg.SaveCounter, Stamp = msg.SaveStamp };
            WipeWorldLocal(net);
            FollowWipeReload(msg.Scene);

            string source = "none";
            // SaveSlot -1 = the host started a new game: nothing carries over, the bag is emptied like the host's.
            var bag = msg.SaveSlot < 0 ? new BagEntry[0] : PartySaveService.ResolveWipeBag(token, out source);
            if (msg.SaveSlot < 0) source = "new game (empty bag)";
            if (bag != null)
            {
                PlaytestLog.Event("PartySave", "wipe bag restore from " + source);
                PartySaveService.RestoreBag(bag);
            }
            else
            {
                PlaytestLog.Event("PartySave", "wipe: no bag snapshot (keep bag)");
            }

            ClearDownedLocal(100, true);
            PartyVitals.ReviveAll(net);
            // Session-scoped sticky state (puzzle memory, fired zones, held boss snapshots...) belongs to the world
            // that is being replaced. Clients never run SaveManager.Load, so they reset here; the host in its Load postfix.
            SessionReset.RunAll(SessionReset.ReasonWipe);
            try { net.AvatarHandlers.SendLocalVital(); }
            catch (System.Exception ex) { LogOnce("vital send", ex); }
        }

        /// <summary>
        /// The host's LoadLevel already broadcast SceneFollow, which drags clients in a different scene. A client that
        /// is already in the reload scene would ignore it (Apply: same scene), so it reloads itself: the world it has
        /// is the pre-wipe one. SceneFollowService.AlreadyGoingTo covers the follow having arrived first.
        /// </summary>
        private static void FollowWipeReload(string scene)
        {
            if (string.IsNullOrEmpty(scene) || SceneFollowService.IsTransient(scene)) return;
            try
            {
                if (SceneFollowService.LocalIsTransient() || SceneFollowService.AlreadyGoingTo(scene)) return;
                if (!SceneMatches(scene))
                {
                    // The wipe is authoritative. The host's SceneFollow normally already dragged us here, but it is not
                    // reliable (airlock wreck<->hole split ignores it, personal chapter loads skip the broadcast, or
                    // loads were suppressed when it arrived), and the host's world is a reverted one: be in its scene.
                    // The wreck<->hole rule (ShouldIgnoreHostFollow) stays in force for ordinary follows only; a wipe
                    // voids the airlock split (everyone is reloaded from one save / new game).
                    PlaytestLog.Event("Damage", "wipe: scene mismatch, following directly '" + scene + "'");
                    SceneFollowService.Apply(scene);
                    return;
                }
                SceneFollowService.NoteGoingTo(scene);
                try { DroppedItemManager.RestorePlayForLoad(); }
                catch (System.Exception ex) { LogOnce("wipe restore play", ex); }
                NetGate.BeginApply();
                try { AsyncLoader.LoadLevel(scene); }
                finally { NetGate.EndApply(); }
                PlaytestLog.Event("Damage", "wipe: same-scene reload '" + scene + "'");
            }
            catch (System.Exception ex) { LogOnce("wipe follow", ex); }
        }

        /// <summary>Clear floor drops + claim state on this peer before the save reload.</summary>
        private static void WipeWorldLocal(LanNetworkManager net)
        {
            try { DroppedItemManager.ClearAll(); }
            catch (System.Exception ex) { LogOnce("wipe drops", ex); }
            try { SyncRADation.Patches.ItemPickupPatches.ResetDropClaims(); }
            catch (System.Exception ex) { LogOnce("wipe drop claims", ex); }
            try { net.PickupSync.Reset(); }
            catch (System.Exception ex) { LogOnce("wipe pickup claims", ex); }
        }

        // ------------------------------------------------------------------ host: revive + wipe

        private static void TickHostParty(LanNetworkManager net)
        {
            if (net.Role != NetworkRole.Host || !net.IsConnected) return;
            _hostTick += Time.unscaledDeltaTime;
            if (_hostTick < HostTickInterval) return;
            _hostTick = 0f;

            PartyVitals.Prune(net);

            bool anyDown = _isDead;
            bool allDown = _isDead;
            foreach (int pid in net.GetRemotePlayerIds())
            {
                if (PartyVitals.IsDown(pid)) anyDown = true;
                else allDown = false;
            }

            if (!anyDown)
            {
                _allDownSince = -1f;
                return;
            }

            float now = Time.unscaledTime;
            if (allDown)
            {
                if (_allDownSince < 0f) _allDownSince = now;
                if (now - _allDownSince >= WipeDelay && now - _wipeAt >= WipeDebounce)
                    ExecuteWipe(net);
                return;
            }

            _allDownSince = -1f;
            float delay = ModConfig.DownedRespawnSeconds;

            if (_isDead && now - _downAt >= delay)
                ReviveOne(net, net.LocalPlayerId);
            foreach (int pid in net.GetRemotePlayerIds())
            {
                if (!PartyVitals.IsDown(pid)) continue;
                if (now - PartyVitals.DownAt(pid) >= delay)
                    ReviveOne(net, pid);
            }
        }

        private static bool SameScene(string a, string b)
        {
            return string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b;
        }

        private static bool TryGetPlayerPos(LanNetworkManager net, int pid, out Vector3 pos)
        {
            if (pid == net.LocalPlayerId)
            {
                var p = PlayerState.player;
                if (p != null)
                {
                    pos = p.transform.position;
                    return true;
                }
                pos = Vector3.zero;
                return false;
            }
            return PartyVitals.TryGetPos(pid, out pos);
        }

        private static string RoomOf(LanNetworkManager net, int pid)
        {
            if (pid == net.LocalPlayerId) return CurrentRoomName();
            return PartyVitals.RoomOf(pid);
        }

        private static void ReviveOne(LanNetworkManager net, int pid)
        {
            Vector3 body;
            bool hasBody = TryGetPlayerPos(net, pid, out body);
            string bodyScene = net.SceneOf(pid);

            int best = -1;
            float bestD = float.MaxValue;
            Vector3 bestPos = Vector3.zero;
            var ids = new System.Collections.Generic.List<int>(8);
            ids.Add(net.LocalPlayerId);
            foreach (int id in net.GetRemotePlayerIds()) ids.Add(id);
            for (int i = 0; i < ids.Count; i++)
            {
                int cid = ids[i];
                if (cid == pid || PartyVitals.IsDown(cid)) continue;
                if (!SameScene(bodyScene, net.SceneOf(cid))) continue;
                Vector3 cpos;
                if (!TryGetPlayerPos(net, cid, out cpos)) continue;
                float d = hasBody ? (cpos - body).sqrMagnitude : 0f;
                if (d < bestD)
                {
                    bestD = d;
                    best = cid;
                    bestPos = cpos;
                }
            }

            var msg = new PartyLifeMessage
            {
                Kind = PartyLifeKind.Revive,
                PlayerId = pid,
                Hp = ReviveHp(),
                HasPos = best >= 0,
                PosX = bestPos.x,
                PosY = bestPos.y,
                PosZ = bestPos.z,
                Room = best >= 0 ? RoomOf(net, best) : "",
                Scene = best >= 0 ? net.SceneOf(best) : ""
            };
            PlaytestLog.Event("Damage", "revive p" + pid + (best >= 0 ? " near p" + best : " in place (no teammate in scene)"));

            if (pid != net.LocalPlayerId)
            {
                PartyVitals.NoteRevived(pid);
                try { net.ProxyManager.GetProxy(pid)?.SetVital(false); }
                catch (System.Exception ex) { LogOnce("proxy revive", ex); }
            }
            net.SendPartyLife(msg);
            if (pid == net.LocalPlayerId)
                ApplyRevive(msg);
        }

        /// <summary>
        /// Everyone is down: reload the host into its save (HostReload replicates LoadMenuUI.confirmLoading), then tell
        /// clients. Returns without touching any state (so the next tick retries) while the host is mid-load.
        /// The key ring / floor drops / pickup claims are reset by the SaveManager.Load postfix once the load
        /// really ran, never here.
        /// </summary>
        private static void ExecuteWipe(LanNetworkManager net)
        {
            float now = Time.unscaledTime;
            if (SceneFollowService.LocalIsTransient() || HostReload.Pending)
            {
                if (now - _wipeBlockedLog >= WipeLogInterval)
                {
                    _wipeBlockedLog = now;
                    PlaytestLog.Event("Damage", "wipe deferred (" + (HostReload.Pending ? HostReload.Describe : "host loading") + ")");
                }
                return;
            }

            _wipeAt = now;
            _allDownSince = -1f;

            var plan = HostReload.TryBegin();
            if (!plan.Started)
            {
                PlaytestLog.Warn("Damage", "party wipe could not start a reload - retry after debounce");
                return;
            }

            var token = plan.Mode == HostReload.Mode.Save ? PartySaveService.TokenForSlot(plan.Slot) : default(PartySaveToken);
            // NewGame: SaveSlot = -1 tells clients the world is brand new (empty bag), not "restore bag at down".
            if (plan.Mode == HostReload.Mode.NewGame) token.Slot = -1;
            PlaytestLog.Event("Damage", "party wipe — " + plan.Mode + " reload '" + plan.Scene + "'"
                + (token.Valid ? " token=" + token.Key : " (no party snapshot)"));

            net.SendPartyLife(new PartyLifeMessage
            {
                Kind = PartyLifeKind.Wipe,
                PlayerId = -1,
                Room = "",
                Scene = plan.Scene,
                SaveSlot = token.Slot,
                SaveCounter = token.Counter,
                SaveStamp = token.Stamp
            });

            ClearDownedLocal(100, true);
            PartyVitals.ReviveAll(net);
        }

        // ------------------------------------------------------------------ revive helpers

        private static int ReviveHp()
        {
            int hp = 50;
            try
            {
                var s = PlayerState.settings;
                if (s != null && s.InjectorReviveHP > 0) hp = s.InjectorReviveHP;
            }
            catch (System.Exception ex) { LogOnce("revive hp", ex); }
            return Mathf.Clamp(hp, 1, 100);
        }

        public static string CurrentRoomName()
        {
            try
            {
                var r = PlayerState.currentRoom;
                if (r != null && !string.IsNullOrEmpty(r.roomName)) return r.roomName;
            }
            catch (System.Exception ex) { LogOnce("room name", ex); }
            return "";
        }

        private static void EnterRoomByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                var cur = PlayerState.currentRoom;
                if (cur != null && cur.roomName == name) return;

                Room target = null;
                var all = Resources.FindObjectsOfTypeAll<Room>();
                for (int i = 0; i < all.Length; i++)
                {
                    var r = all[i];
                    if (r == null || r.roomName != name) continue;
                    if (!r.gameObject.scene.IsValid()) continue;
                    target = r;
                    break;
                }
                if (target == null)
                {
                    PlaytestLog.Event("Damage", "revive room '" + name + "' not found");
                    return;
                }
                if (cur != null)
                {
                    try { cur.LeaveRoom(); }
                    catch (System.Exception ex) { LogOnce("revive leave room", ex); }
                }
                target.EnterRoom();
            }
            catch (System.Exception ex) { LogOnce("revive enter room", ex); }
        }

        /// <summary>Undo every downed-state side effect and set hp.</summary>
        private static void ClearDownedLocal(int hp, bool wiped)
        {
            bool wasDead = _isDead;
            _isDead = false;
            _hasPendingRevive = false;
            _dropPending = false;
            PartySaveService.ClearBagAtDown();
            try { PlayerState.hp = Mathf.Clamp(hp, 1, 100); } catch (System.Exception ex) { LogOnce("clear hp", ex); }
            // Only undo the cloak we set: a peer that was never downed (wipe) may be cloaked by GrayFox on its own.
            if (wasDead)
            {
                try { PlayerState.cloaked = _prevCloaked; } catch (System.Exception ex) { LogOnce("clear cloak", ex); }
            }
            _prevCloaked = false;
            try { PlayerState.suspendInput = false; } catch (System.Exception ex) { LogOnce("clear suspendInput", ex); }
            try { PlayerState.gameOver = false; } catch (System.Exception ex) { LogOnce("clear gameOver", ex); }
            try { PlayerState.charState = PlayerState.charStates.idle; } catch (System.Exception ex) { LogOnce("clear charState", ex); }
            try
            {
                // Native ElsterDeathHandler parks gameState in cutscene while dead.
                if (PlayerState.gameState == PlayerState.gameStates.cutscene)
                    PlayerState.gameState = PlayerState.gameStates.play;
            }
            catch (System.Exception ex) { LogOnce("clear gameState", ex); }
            try
            {
                var anim = DeathAnimator();
                if (anim != null)
                {
                    anim.SetBool(DeadHash, false);
                    anim.ResetTrigger(DieHash);
                }
            }
            catch (System.Exception ex) { LogOnce("clear anim", ex); }

            if (wiped) return;
            try
            {
                var s = PlayerState.settings;
                // Native healElster sets phoenix itself, except on difficulty 2 (no free revive there): mirror that.
                if (s != null && s.difficulty != 2) PlayerState.phoenix = true;
                PlayerState.hurtCool = 0f;
                if (s != null) PlayerState.inviTimer = s.maxInvi;
            }
            catch (System.Exception ex) { LogOnce("clear invi", ex); }
            try { PlayerState.healElster(); }
            catch (System.Exception ex) { LogOnce("healElster", ex); }
        }

        // ------------------------------------------------------------------ per-frame

        /// <summary>Called every frame from ModRuntime.OnUpdate (name kept from the old respawn timer).</summary>
        public static void TickRespawn()
        {
            var net = ModRuntime.Network;
            if (net == null) return;

            HostReload.Tick();

            if (_isDead)
            {
                if (!net.IsConnected)
                {
                    // Link dropped while downed (StopNetwork normally Reset()s; this is belt and braces).
                    ClearDownedLocal(ReviveHp(), false);
                    return;
                }
                ApplyDownedLocal(false);
                TickDownedDrop(net);
                TryApplyPendingRevive();
            }
            else if (PartyLive)
            {
                try
                {
                    if (PlayerState.hp <= 0 && PlayerState.player != null
                        && PlayerState.gameState != PlayerState.gameStates.menu
                        && PlayerState.gameState != PlayerState.gameStates.loading)
                    {
                        if (Time.unscaledTime - _lastSafetyLog > 5f)
                        {
                            _lastSafetyLog = Time.unscaledTime;
                            PlaytestLog.Event("Damage", "hp<=0 poll — downing");
                        }
                        Die();
                    }
                }
                catch (System.Exception ex) { LogOnce("hp poll", ex); }
            }

            if (net.IsConnected)
            {
                TickHostParty(net);
                ReportRoom(net);
            }
        }

        // Every peer reports its room (the host relays client rooms), so each side knows where everyone is. Unchanged
        // rooms are re-sent every RoomRefreshInterval so a late joiner learns the others' rooms without a move.
        private static void ReportRoom(LanNetworkManager net)
        {
            _roomTimer += Time.unscaledDeltaTime;
            _roomRefresh += Time.unscaledDeltaTime;
            if (_roomTimer < RoomReportInterval) return;
            _roomTimer = 0f;
            string room = CurrentRoomName();
            if (room == _lastRoomSent && _roomRefresh < RoomRefreshInterval) return;
            _lastRoomSent = room;
            _roomRefresh = 0f;
            try { net.PartyHandlers.SendPartyRoom(room); }
            catch (System.Exception ex) { LogOnce("room report", ex); }
        }

        public static void Reset()
        {
            if (_isDead)
                ClearDownedLocal(ReviveHp(), false);
            _isDead = false;
            _hasPendingRevive = false;
            _dropPending = false;
            _deathAnim = null;
            _deathAnimFor = null;
            _allDownSince = -1f;
            _wipeAt = -99f;
            _wipeBlockedLog = -99f;
            _hostTick = 0f;
            _roomTimer = 0f;
            _roomRefresh = 0f;
            _lastRoomSent = "";
            _prevCloaked = false;
            PartyVitals.Reset();
            PartySaveService.Reset();
            HostReload.Reset();
            // suspendInput is restored by ClearDownedLocal when we were downed; touching it while never downed would
            // un-pause a menu/inventory the player has open (StartHost/Connect call StopNetwork first).
        }

        // Warn-once keys: intentionally persistent.
        private static readonly System.Collections.Generic.HashSet<string> _logged =
            new System.Collections.Generic.HashSet<string>();

        private static void LogOnce(string what, System.Exception ex)
        {
            if (!_logged.Add(what)) return;
            ModRuntime.Log?.Warning("[Damage] " + what + ": " + ex.Message);
        }
    }
}
