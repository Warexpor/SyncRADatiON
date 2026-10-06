// Native HurtElster is the only HP path. Co-op death: any player (host or client) who dies while
// a teammate lives is DOWNED (input off, cloaked so enemies ignore them, proxy shows dead), then
// the host respawns them next to the nearest living teammate after DownedRespawnDelay seconds.
// The party wipes (host reloads its last save via HostReload, clients restore bag snapshots and follow the
// host's scene load) only when every player is down. Solo / no party: nothing here runs, the native game over
// stays vanilla.
using System.Collections.Generic;
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
        private const float RoomRefreshInterval = 5f;
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
                var net = LanNetworkManager.Instance;
                return net != null && net.IsConnected && net.GetPlayerCount() >= 2;
            }
        }

        public static float PlayerHP => PlayerState.hp;

        private static bool CheatImmortal => global::Cheats.buddha || global::Cheats.kami || global::Cheats.hastur;

        // ------------------------------------------------------------------ native hooks

        /// <summary>GameOverHandler.hurt prefix: true = skip the native game over screen.</summary>
        public static bool ShouldSuppressNativeGameOver()
        {
            if (!PartyLive) return false;
            if (_isDead) return true;
            if (CheatImmortal || PlayerState.hp >= 2) return false;
            var tool = InventoryManager.EquippedTool;
            return tool == null || tool._item != Items.itemlist.Injector;
        }

        /// <summary>PlayerState.HurtElster prefix gate (native: only in play / dialogue).</summary>
        public static bool HurtGateOpen()
        {
            var gs = PlayerState.gameState;
            return gs == PlayerState.gameStates.play || gs == PlayerState.gameStates.dialogue;
        }

        /// <summary>PlayerState.HurtElster postfix: native damage finished and hp is at the native death threshold.</summary>
        public static void OnNativeHurt()
        {
            if (_isDead || !PartyLive || CheatImmortal || PlayerState.hp >= 2) return;
            PlaytestLog.Event("Damage", "native lethal hit hp=" + PlayerState.hp);
            Die();
        }

        /// <summary>Remote-authored damage (host enemy hit, friendly fire) through native HurtElster.</summary>
        public static void ApplyDamage(float damage)
        {
            if (_isDead || float.IsNaN(damage) || damage <= 0f) return;
            if (PlayerState.hp <= 0 || PlayerState.charState == PlayerState.charStates.dead) return;

            // No hit direction on the wire: zero, as the callers always passed.
            try { PlayerState.HurtElster((int)damage, Vector2.zero); }
            catch (System.Exception ex) { LogOnce("HurtElster", ex); }

            int hp = PlayerState.hp;
            PlaytestLog.Event("Damage", "-" + damage.ToString("F0") + " HP remain=" + hp);
            // Native postfix normally downs us inside HurtElster; this is the fallback path.
            if (hp < 2 && PartyLive)
                Die();
        }

        // ------------------------------------------------------------------ downed

        private static void Die()
        {
            if (_isDead) return;
            var net = LanNetworkManager.Instance;
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

            PlayDeathSound();
            net.CombatHandlers.SendDeathPolicy(DeathKind.ClientDowned);
            net.AvatarHandlers.SendLocalVital();
            PlaytestLog.Event("Damage", (NetGate.HostRole ? "host" : "client")
                + " downed — respawn in " + ModConfig.DownedRespawnSeconds.ToString("F0") + "s");
        }

        private static void PlayDeathSound()
        {
            var player = PlayerState.player;
            if (player == null) return;
            try
            {
                var hurtSound = player.GetComponent<ElsterHurtSound>();
                if (hurtSound != null && !string.IsNullOrEmpty(hurtSound.DeathSound))
                    RuntimeManager.PlayOneShot(hurtSound.DeathSound);
            }
            catch (System.Exception ex) { LogOnce("death sound", ex); }
        }

        private static void ApplyDownedLocal(bool firstTime)
        {
            PlayerState.charState = PlayerState.charStates.dead;
            PlayerState.suspendInput = true;
            if (firstTime) _prevCloaked = PlayerState.cloaked;
            // EnemyVision.CanSee returns false while cloaked: enemies lose the downed player.
            PlayerState.cloaked = true;

            // Every frame while downed (firstTime or not): a scene change swaps the player object and its animator
            // comes up alive, so re-assert the dead pose whenever the Dead bool is not set.
            var anim = DeathAnimator();
            if (anim != null && !anim.GetBool(DeadHash))
            {
                anim.SetTrigger(DieHash);
                anim.SetBool(DeadHash, true);
            }
        }

        /// <summary>The animator ElsterDeathHandler drives (its own <c>anim</c>), falling back to the first one in the tree.</summary>
        private static Animator DeathAnimator()
        {
            var player = PlayerState.player;
            if (player == null) return null;
            if (_deathAnimFor == player && _deathAnim != null) return _deathAnim;
            var handler = player.GetComponent<ElsterDeathHandler>();
            Animator anim = handler != null ? handler.anim : null;
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
                DropInventoryOnDeath(net);
            else
                PlaytestLog.Event("Damage", "last player down — skip floor drop (wipe follows)");
        }

        private static void DropInventoryOnDeath(LanNetworkManager net)
        {
            var player = PlayerState.player;
            var dict = InventoryManager.elsterItems;
            if (player == null || dict == null) return;
            Vector3 pos = DroppedItemRegistry.FloorDropPos(player.transform);

            try
            {
                // Snapshot first: RemoveItem below mutates the bag dictionary.
                var itemsToDrop = new List<(AnItem item, int count, Items.itemlist enumVal)>();
                var enumerator = dict.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var kvp = enumerator.Current;
                    var item = kvp.key;
                    int count = kvp.value;
                    if (item == null || count <= 0) continue;
                    var itemEnum = item._item;
                    if (itemEnum == Items.itemlist.None || itemEnum == Items.itemlist.Injector) continue;
                    itemsToDrop.Add((item, count, itemEnum));
                }
                enumerator.Dispose();

                foreach (var entry in itemsToDrop)
                {
                    // Party ring is SoT for unique Key/Object. Bag copies are claim / storage
                    // grants. Floor-dropping them DetachDroppedKey-clears the ring
                    // for every peer while box / WorldId claim may still hold the real unique
                    // → ghost floor + UseItem softlock. Never floor uniques on death: Note
                    // onto the ring if the bag somehow held one the ring missed (race), then
                    // strip the bag copy only. G-drop still transfers ownership.
                    if (PartyKeyRing.IsKeyOrObject(entry.enumVal))
                    {
                        if (!PartyKeyRing.Has(entry.enumVal))
                        {
                            PartyKeyRing.Note(entry.enumVal);
                            PartyKeyRing.OfferToHost(entry.item);
                            PlaytestLog.Event("Damage", "death note ring unique " + entry.enumVal);
                        }
                        InventoryManager.RemoveItem(entry.item, entry.count);
                        PlaytestLog.Event("Damage", "death skip floor unique " + entry.enumVal);
                        continue;
                    }

                    ushort idx = net.DroppedItemHandlers.AllocateItemIndex();
                    int key = (net.LocalPlayerId << 16) | idx;
                    Vector3 dropPos = pos + new Vector3(
                        Random.Range(-0.35f, 0.35f),
                        Random.Range(-0.08f, 0.08f),
                        0f);
                    DroppedItemSpawner.SpawnLocalItem(entry.enumVal, entry.count, key, dropPos);
                    net.DroppedItemHandlers.SendDropItem(new DropItemSpawnMessage
                    {
                        SenderID = (byte)net.LocalPlayerId,
                        LocalIndex = idx,
                        ItemEnum = (ushort)entry.enumVal,
                        Count = entry.count,
                        PosX = dropPos.x,
                        PosY = dropPos.y,
                        PosZ = dropPos.z
                    });
                    InventoryManager.RemoveItem(entry.item, entry.count);
                    PartyKeyRing.Remove(entry.enumVal);
                }
                if (NetGate.HostRole)
                    PartyKeyRing.Broadcast();
            }
            catch (System.Exception ex) { LogOnce("death drop", ex); }
        }

        // ------------------------------------------------------------------ wire handlers

        public static void HandleDeathPolicy(DeathPolicyMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || msg.SenderPlayerId == net.LocalPlayerId) return;

            // Wipes travel as PartyLife(Wipe); DeathKind.HostWipeReload is never sent (its wire id stays reserved).
            if (msg.Kind == DeathKind.ClientDowned)
                PartyVitals.NoteDown(msg.SenderPlayerId);
        }

        /// <summary>Every peer: host announced a revive.</summary>
        public static void ApplyRevive(PartyLifeMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;

            if (msg.PlayerId != net.LocalPlayerId)
            {
                PartyVitals.NoteRevived(msg.PlayerId);
                net.ProxyManager.GetProxy(msg.PlayerId)?.SetVital(false);
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
            var player = PlayerState.player;
            if (player == null || SceneFollowService.LocalIsTransient()) return;
            _hasPendingRevive = false;
            var msg = _pendingRevive;

            // The teleport target is in msg.Scene; if we loaded somewhere else meanwhile its XYZ is meaningless here.
            bool teleport = msg.HasPos && SceneMatches(msg.Scene);
            if (teleport)
            {
                player.transform.position = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                EnterRoomByName(msg.Room);
            }

            ClearDownedLocal(msg.Hp > 0 ? msg.Hp : ReviveHp(), false);
            PlaytestLog.Event("Damage", "respawned hp=" + PlayerState.hp
                + (teleport ? " at teammate (" + msg.PosX.ToString("F1") + "," + msg.PosZ.ToString("F1") + ")"
                    : (msg.HasPos ? " in place (teammate scene '" + msg.Scene + "' != here)" : " in place")));
            LanNetworkManager.Instance?.AvatarHandlers.SendLocalVital();
        }

        private static bool SceneMatches(string scene)
        {
            return string.IsNullOrEmpty(scene)
                || string.Equals(SceneManager.GetActiveScene().name, scene, System.StringComparison.Ordinal);
        }

        /// <summary>Client: host wiped the party. No own-slot load: bag from the save snapshot, follow the host reload.</summary>
        public static void ApplyWipeAsClient(PartyLifeMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            PlaytestLog.Event("Damage", "party wipe — host reloads '" + msg.Scene + "'");
            // The host reverted to a save, this peer never loads a slot: story progress is replaced by the host's next full dump.
            Step("wipe story", net.StorySync.OnPartyWipe);

            var token = new PartySaveToken { Slot = msg.SaveSlot, Counter = msg.SaveCounter, Stamp = msg.SaveStamp };
            WipeWorldLocal(net);
            bool loadPending = FollowWipeReload(msg.Scene);
            // A save reload: load like the host (SaveManager.loading) and start at its save point, without this
            // peer's own slot ever being read (WipePlacement / LoadingManagerWipePatch).
            if (msg.SaveSlot > 0 && !string.IsNullOrEmpty(msg.Room))
                WipePlacement.Arm(msg.Scene, msg.Room, loadPending);

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
            net.AvatarHandlers.SendLocalVital();
        }

        /// <summary>
        /// The host's LoadLevel already broadcast SceneFollow, which drags clients in a different scene. A client that
        /// is already in the reload scene would ignore it (Apply: same scene), so it reloads itself: the world it has
        /// is the pre-wipe one. SceneFollowService.AlreadyGoingTo covers the follow having arrived first.
        /// </summary>
        /// <returns>true while a load of the reload scene is still to arrive (its LoadingManager.Start has not run).</returns>
        private static bool FollowWipeReload(string scene)
        {
            if (string.IsNullOrEmpty(scene) || SceneFollowService.IsTransient(scene)) return false;
            if (SceneFollowService.LocalIsTransient()) return true;
            // The host's follow already brought this peer: in the scene and out of the loading screen means loaded.
            if (SceneFollowService.AlreadyGoingTo(scene)) return !SceneMatches(scene);
            if (!SceneMatches(scene))
            {
                // The wipe is authoritative. The host's SceneFollow normally already dragged us here, but it is not
                // reliable (airlock wreck<->hole split ignores it, personal chapter loads skip the broadcast, or
                // loads were suppressed when it arrived), and the host's world is a reverted one: be in its scene.
                // The wreck<->hole rule (ShouldIgnoreHostFollow) stays in force for ordinary follows only; a wipe
                // voids the airlock split (everyone is reloaded from one save / new game).
                PlaytestLog.Event("Damage", "wipe: scene mismatch, following directly '" + scene + "'");
                SceneFollowService.Apply(scene);
                return true;
            }
            SceneFollowService.NoteGoingTo(scene);
            Step("wipe restore play", DroppedItemRegistry.RestorePlayForLoad);
            NetGate.BeginApply();
            try { AsyncLoader.LoadLevel(scene); }
            catch (System.Exception ex) { LogOnce("wipe reload", ex); }
            finally { NetGate.EndApply(); }
            PlaytestLog.Event("Damage", "wipe: same-scene reload '" + scene + "'");
            return true;
        }

        /// <summary>Clear floor drops + claim state on this peer before the save reload.</summary>
        private static void WipeWorldLocal(LanNetworkManager net)
        {
            Step("wipe drops", DroppedItemRegistry.ClearAll);
            Step("wipe drop claims", SyncRADation.Patches.ItemPickupPatches.ResetDropClaims);
            Step("wipe pickup claims", net.PickupSync.Reset);
        }

        /// <summary>One independent step of the wipe: a failing step must not skip the ones after it.</summary>
        private static void Step(string what, System.Action action)
        {
            try { action(); }
            catch (System.Exception ex) { LogOnce(what, ex); }
        }

        // ------------------------------------------------------------------ host: revive + wipe

        private static void TickHostParty(LanNetworkManager net)
        {
            if (!NetGate.HostRole) return;
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
                if (PartyVitals.IsDown(pid) && now - PartyVitals.DownAt(pid) >= delay)
                    ReviveOne(net, pid);
            }
        }

        private static bool SameScene(string a, string b)
        {
            return string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b;
        }

        private static bool TryGetPlayerPos(LanNetworkManager net, int pid, out Vector3 pos)
        {
            if (pid != net.LocalPlayerId)
                return PartyVitals.TryGetPos(pid, out pos);
            var p = PlayerState.player;
            pos = p != null ? p.transform.position : Vector3.zero;
            return p != null;
        }

        private static void ReviveOne(LanNetworkManager net, int pid)
        {
            bool hasBody = TryGetPlayerPos(net, pid, out Vector3 body);
            string bodyScene = net.SceneOf(pid);

            // Nearest living teammate in the downed player's scene.
            int best = -1;
            float bestD = float.MaxValue;
            Vector3 bestPos = Vector3.zero;
            var remotes = net.GetRemotePlayerIds();
            for (int i = -1; i < remotes.Length; i++)
            {
                int cid = i < 0 ? net.LocalPlayerId : remotes[i];
                if (cid == pid || PartyVitals.IsDown(cid)) continue;
                if (!SameScene(bodyScene, net.SceneOf(cid))) continue;
                if (!TryGetPlayerPos(net, cid, out Vector3 cpos)) continue;
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
                Room = best < 0 ? "" : best == net.LocalPlayerId ? CurrentRoomName() : PartyVitals.RoomOf(best),
                Scene = best >= 0 ? net.SceneOf(best) : ""
            };
            PlaytestLog.Event("Damage", "revive p" + pid + (best >= 0 ? " near p" + best : " in place (no teammate in scene)"));

            if (pid != net.LocalPlayerId)
            {
                PartyVitals.NoteRevived(pid);
                net.ProxyManager.GetProxy(pid)?.SetVital(false);
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
                // Save reload: the save room, so every client starts at the host's save point (WipePlacement).
                Room = plan.Room ?? "",
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
            var s = PlayerState.settings;
            int hp = s != null && s.InjectorReviveHP > 0 ? s.InjectorReviveHP : 50;
            return Mathf.Clamp(hp, 1, 100);
        }

        public static string CurrentRoomName()
        {
            var r = PlayerState.currentRoom;
            return r != null && !string.IsNullOrEmpty(r.roomName) ? r.roomName : "";
        }

        private static void EnterRoomByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var cur = PlayerState.currentRoom;
            if (cur != null && cur.roomName == name) return;

            Room target = null;
            foreach (var r in Resources.FindObjectsOfTypeAll<Room>())
            {
                if (r != null && r.roomName == name && r.gameObject.scene.IsValid())
                {
                    target = r;
                    break;
                }
            }
            if (target == null)
            {
                PlaytestLog.Event("Damage", "revive room '" + name + "' not found");
                return;
            }
            try
            {
                if (cur != null) cur.LeaveRoom();
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
            PlayerState.hp = Mathf.Clamp(hp, 1, 100);
            // Only undo the cloak we set: a peer that was never downed (wipe) may be cloaked by GrayFox on its own.
            if (wasDead) PlayerState.cloaked = _prevCloaked;
            _prevCloaked = false;
            PlayerState.suspendInput = false;
            PlayerState.gameOver = false;
            PlayerState.charState = PlayerState.charStates.idle;
            // Native ElsterDeathHandler parks gameState in cutscene while dead.
            if (PlayerState.gameState == PlayerState.gameStates.cutscene)
                PlayerState.gameState = PlayerState.gameStates.play;
            var anim = DeathAnimator();
            if (anim != null)
            {
                anim.SetBool(DeadHash, false);
                anim.ResetTrigger(DieHash);
            }

            if (wiped) return;
            var s = PlayerState.settings;
            // Native healElster sets phoenix itself, except on difficulty 2 (no free revive there): mirror that.
            if (s != null && s.difficulty != 2) PlayerState.phoenix = true;
            PlayerState.hurtCool = 0f;
            if (s != null) PlayerState.inviTimer = s.maxInvi;
            try { PlayerState.healElster(); }
            catch (System.Exception ex) { LogOnce("healElster", ex); }
        }

        // ------------------------------------------------------------------ per-frame

        /// <summary>Called every frame from ModRuntime.OnUpdate (name kept from the old respawn timer).</summary>
        public static void TickRespawn()
        {
            var net = LanNetworkManager.Instance;
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
            else if (PartyLive && PlayerState.hp <= 0 && PlayerState.player != null
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
            net.PartyHandlers.SendPartyRoom(room);
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
            // suspendInput is restored by ClearDownedLocal when we were downed; touching it while never downed would
            // un-pause a menu/inventory the player has open (StartHost/Connect call StopNetwork first).
        }

        // Warn-once keys: intentionally persistent.
        private static readonly HashSet<string> _logged = new HashSet<string>();

        private static void LogOnce(string what, System.Exception ex)
        {
            if (!_logged.Add(what)) return;
            ModRuntime.Log?.Warning("[Damage] " + what + ": " + ex.Message);
        }
    }
}
