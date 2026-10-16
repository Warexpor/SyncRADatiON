// Native HurtElster is the only HP path. Co-op death: any player (host or client) who dies in a live party plays its
// death (input off, cloaked so enemies ignore it, proxy shows dead), then reloads the scene it died in on its own and
// starts at its last save point there (DeathReload). The world is never reverted: no save slot is read, the bag is
// kept, and nobody else is moved. Solo / no party: nothing here runs, the native game over stays vanilla.
using System.Collections.Generic;
using FMODUnity;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class NetworkDamageSystem
    {
        private const float RoomReportInterval = 0.5f;
        private const float RoomRefreshInterval = 5f;
        private const float PruneInterval = 1f;

        private static bool _isDead;
        private static bool _prevCloaked;
        private static float _roomTimer;
        private static float _roomRefresh;
        private static float _pruneTimer;
        private static string _lastRoomSent = "";
        private static float _lastSafetyLog = -99f;
        private static Animator _deathAnim;
        private static GameObject _deathAnimFor;
        // Animator.StringToHash once: ApplyDeadLocal polls the Dead bool every frame while dead.
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
            // Native postfix normally kills us inside HurtElster; this is the fallback path.
            if (hp < 2 && PartyLive)
                Die();
        }

        // ------------------------------------------------------------------ death

        private static void Die()
        {
            if (_isDead) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !PartyLive) return;

            _isDead = true;
            ApplyDeadLocal(true);
            DeathReload.OnDied();

            PlayDeathSound();
            net.CombatHandlers.SendDeathPolicy(DeathKind.ClientDowned);
            net.AvatarHandlers.SendLocalVital();
            PlaytestLog.Event("Damage", (NetGate.HostRole ? "host" : "client") + " died - reloading this scene on its own");
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

        private static void ApplyDeadLocal(bool firstTime)
        {
            PlayerState.charState = PlayerState.charStates.dead;
            PlayerState.suspendInput = true;
            if (firstTime) _prevCloaked = PlayerState.cloaked;
            // EnemyVision.CanSee returns false while cloaked: enemies lose the dead player.
            PlayerState.cloaked = true;

            // Every frame (firstTime or not): a scene change swaps the player object and its animator comes up alive,
            // so re-assert the dead pose whenever the Dead bool is not set.
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

        // ------------------------------------------------------------------ wire handlers

        public static void HandleDeathPolicy(DeathPolicyMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || msg.SenderPlayerId == net.LocalPlayerId) return;
            // DeathKind.HostWipeReload is a wire value only (no party wipe since 0.5.76).
            if (msg.Kind == DeathKind.ClientDowned)
                PartyVitals.NoteDown(msg.SenderPlayerId);
        }

        /// <summary>DeathReload: the player is back in the scene it died in, at its restart point.</summary>
        internal static void OnReloaded()
        {
            if (!_isDead) return;
            ClearDeadLocal();
            PlaytestLog.Event("Damage", "back after death hp=" + PlayerState.hp + " in '" + CurrentRoomName() + "'");
            LanNetworkManager.Instance?.AvatarHandlers.SendLocalVital();
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Room.EnterRoom ran: the next party tick reports the room at once. Enemy wake on the host (PeerRoomEnemies)
        /// and room-scoped cutscenes key off it, and a report slot could be half a second away.</summary>
        public static void RoomChangedNow() => _roomTimer = RoomReportInterval;

        public static string CurrentRoomName()
        {
            var r = PlayerState.currentRoom;
            return r != null && !string.IsNullOrEmpty(r.roomName) ? r.roomName : "";
        }

        /// <summary>Undo every death side effect; full health like a load.</summary>
        private static void ClearDeadLocal()
        {
            bool wasDead = _isDead;
            _isDead = false;
            // Only undo the cloak we set: a player may be cloaked by GrayFox on its own.
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
            PlayerState.hp = 100;
            PlayerState.hurtCool = 0f;
            var s = PlayerState.settings;
            if (s != null) PlayerState.inviTimer = s.maxInvi;
            // Native healElster sets phoenix itself (except on difficulty 2) and runs the heal event.
            try { PlayerState.healElster(); }
            catch (System.Exception ex) { LogOnce("healElster", ex); }
        }

        // ------------------------------------------------------------------ per-frame

        /// <summary>Called every frame from ModRuntime.OnUpdate (name kept from the old respawn timer).</summary>
        public static void TickRespawn()
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;

            if (_isDead)
            {
                if (!net.IsConnected)
                {
                    // Link dropped while dead: the party is gone, get up where the player lies.
                    DeathReload.Reset();
                    ClearDeadLocal();
                    return;
                }
                ApplyDeadLocal(false);
                DeathReload.Tick();
            }
            else if (PartyLive && PlayerState.hp <= 0 && PlayerState.player != null
                && PlayerState.gameState != PlayerState.gameStates.menu
                && PlayerState.gameState != PlayerState.gameStates.loading)
            {
                if (Time.unscaledTime - _lastSafetyLog > 5f)
                {
                    _lastSafetyLog = Time.unscaledTime;
                    PlaytestLog.Event("Damage", "hp<=0 poll - dying");
                }
                Die();
            }

            if (net.IsConnected)
            {
                _pruneTimer += Time.unscaledDeltaTime;
                if (_pruneTimer >= PruneInterval)
                {
                    _pruneTimer = 0f;
                    PartyVitals.Prune(net);
                }
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
            {
                DeathReload.Reset();
                ClearDeadLocal();
            }
            _isDead = false;
            _deathAnim = null;
            _deathAnimFor = null;
            _roomTimer = 0f;
            _roomRefresh = 0f;
            _pruneTimer = 0f;
            _lastRoomSent = "";
            _prevCloaked = false;
            // suspendInput is restored by ClearDeadLocal when we were dead; touching it otherwise would un-pause a
            // menu / inventory the player has open (StartHost / Connect call StopNetwork first).
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
