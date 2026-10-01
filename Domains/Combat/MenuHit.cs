// Co-op keeps time running in menus (NoPausePatch), so a menu must not be a safe room either. Native HurtElster drops
// every hit unless gameState is play or dialogue (Ghidra PlayerState.c). In a party a hit that lands while this player
// has the inventory / pause menu / a book / an event screen / the photo viewer open is held, that screen is closed the
// way the game closes it (InventoryBase.ToggleInventory, PauseMenu.togglePause, BookScreen.Close,
// EventScreenInteraction.exitEvent, EideticModule.DisableViewer), and the hit is replayed through HurtElster as soon as
// the player is back in play: normal hurt reaction, grab, downed + revive timer. Dialogue (incl. item prompts) is
// already gameState dialogue, where native damage lands and BlackSleekGuiSubs ends the dialogue itself.
using SyncRADation.ItemSystem;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class MenuHit
    {
        const float CloseTimeout = 2.5f;

        static bool _pending;
        static int _damage;
        static Vector2 _dir;
        static bool _hug;
        static float _at;
        static bool _forced;
        static bool _replaying;

        public static void Reset()
        {
            _pending = false;
            _forced = false;
            _replaying = false;
        }

        static bool MenuState(PlayerState.gameStates gs)
        {
            return gs == PlayerState.gameStates.menu
                || gs == PlayerState.gameStates.paused
                || gs == PlayerState.gameStates.inventory
                || gs == PlayerState.gameStates.eventScreen
                || gs == PlayerState.gameStates.book;
        }

        /// <summary>
        /// HurtElster / HurtElsterHug prefix (party only). True = let native run now. False = the hit is held until the
        /// open screen is closed (the native gate would drop it, or Hug would hurt a player still looking at a menu).
        /// </summary>
        public static bool Intercept(int damage, Vector2 dir, bool hug)
        {
            if (_replaying) return true;
            PlayerState.gameStates gs;
            try { gs = PlayerState.gameState; }
            catch { return true; }
            if (!MenuState(gs)) return true;

            // Several hits while the screen closes: keep the heaviest (native i-frames would eat the rest anyway).
            if (!_pending || damage > _damage)
            {
                _damage = damage;
                _dir = dir;
                _hug = hug;
            }
            if (!_pending)
            {
                _pending = true;
                _forced = false;
                _at = Time.unscaledTime;
                PlaytestLog.Event("Damage", "hit in " + gs + " (" + damage + ") — closing it");
                CloseOpenScreen(gs);
            }
            return false;
        }

        /// <summary>ModRuntime.OnUpdate: replay the held hit once back in play; force play if a screen will not close.</summary>
        public static void Tick()
        {
            if (!_pending) return;
            if (!NetworkDamageSystem.PartyLive || NetworkDamageSystem.IsDead)
            {
                _pending = false;
                return;
            }
            PlayerState.gameStates gs;
            try { gs = PlayerState.gameState; }
            catch { return; }
            if (gs != PlayerState.gameStates.play)
            {
                if (!_forced && Time.unscaledTime - _at > CloseTimeout && MenuState(gs))
                {
                    _forced = true;
                    PlaytestLog.Event("Damage", "screen " + gs + " did not close — forcing play");
                    try { DroppedItemManager.RestorePlay(); } catch (System.Exception e) { Guard.Swallow(e); }
                }
                return;
            }
            _pending = false;
            _replaying = true;
            try
            {
                if (_hug) PlayerState.HurtElsterHug(_damage, _dir);
                else PlayerState.HurtElster(_damage, _dir);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            finally { _replaying = false; }
        }

        static void CloseOpenScreen(PlayerState.gameStates gs)
        {
            try
            {
                switch (gs)
                {
                    case PlayerState.gameStates.paused:
                        var pm = Object.FindObjectOfType<PauseMenu>();
                        if (pm != null) pm.togglePause();
                        break;
                    case PlayerState.gameStates.inventory:
                        var inv = Object.FindObjectOfType<InventoryBase>();
                        if (inv != null)
                        {
                            try { if (inv.intMenuOn) inv.ToggleInteractMenu(); } catch (System.Exception e) { Guard.Swallow(e); }
                            if (PlayerState.gameState == PlayerState.gameStates.inventory) inv.ToggleInventory();
                        }
                        break;
                    case PlayerState.gameStates.book:
                        foreach (var bs in Object.FindObjectsOfType<BookScreen>())
                        {
                            if (bs == null) continue;
                            bool open = false;
                            try { open = bs.open; } catch (System.Exception e) { Guard.Swallow(e); }
                            if (open) bs.StartCoroutine(bs.Close());
                        }
                        break;
                    case PlayerState.gameStates.eventScreen:
                        foreach (var es in Object.FindObjectsOfType<EventScreenInteraction>())
                        {
                            if (es == null) continue;
                            bool on = false;
                            try { on = es.Eventing && !es.returning; } catch (System.Exception e) { Guard.Swallow(e); }
                            if (on) es.exitEvent();
                        }
                        break;
                }
                // The photo viewer can sit on top of any state.
                foreach (var em in Object.FindObjectsOfType<EideticModule>())
                {
                    if (em == null) continue;
                    bool viewer = false;
                    try { viewer = em.viewerMode; } catch (System.Exception e) { Guard.Swallow(e); }
                    if (viewer) em.DisableViewer(true);
                }
            }
            catch (System.Exception e)
            {
                ModRuntime.Log?.Warning("[Damage] close " + gs + ": " + e.Message);
            }
        }
    }
}
