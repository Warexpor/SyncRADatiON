// [Self]: the local player's own state edges, read from the native PlayerState statics every frame while in a session:
// game state (play / inventory / cutscene / dialogue / ...), HP, input locks, grapple, the player object going away.
// The other peers' logs only show this player through the network; this is what it looked like at the source.
using SyncRADation.Networking;
using SyncRADation.Players;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class SelfWatch
    {
        static bool _primed;
        static PlayerState.gameStates _gs;
        static int _hp;
        static bool _suspend, _grappled, _noPlayer, _gameOver, _cloaked, _hasPlayer, _playerActive;

        public static void Reset() => _primed = false;

        public static void Tick()
        {
            if (!NetGate.Live) { _primed = false; return; }
            var gs = PlayerState.gameState;
            int hp = PlayerState.hp;
            bool suspend = PlayerState.suspendInput;
            bool grappled = PlayerState.grappled;
            bool noPlayer = PlayerState.noplayer;
            bool gameOver = PlayerState.gameOver;
            bool cloaked = PlayerState.cloaked;
            GameObject me = PlayerState.player;
            bool hasPlayer = me != null;
            bool active = hasPlayer && me.activeInHierarchy;

            if (!_primed)
            {
                _primed = true;
            }
            else
            {
                if (gs != _gs)
                    PlaytestLog.Event("Self", "gs " + _gs + " -> " + gs + Where() + Prompt());
                if (hp == _hp + 1)
                    // Native regeneration ticks one point at a time.
                    PlaytestLog.Rate("Self", "regen", 10f, "hp " + _hp + " -> " + hp + " (regen) gs=" + gs + Where());
                else if (hp != _hp)
                    PlaytestLog.Event("Self", "hp " + _hp + " -> " + hp + " gs=" + gs + Where());
                if (suspend != _suspend) Flag("suspendInput", suspend, gs);
                if (grappled != _grappled) Flag("grappled", grappled, gs);
                if (noPlayer != _noPlayer) Flag("noplayer", noPlayer, gs);
                if (gameOver != _gameOver) Flag("gameOver", gameOver, gs);
                if (cloaked != _cloaked) Flag("cloaked", cloaked, gs);
                if (hasPlayer != _hasPlayer)
                    PlaytestLog.Event("Self", "player object " + (hasPlayer ? "set" : "GONE") + " gs=" + gs + Where());
                else if (active != _playerActive)
                    PlaytestLog.Event("Self", "player object " + (active ? "active" : "INACTIVE") + " gs=" + gs + Where());
            }
            _gs = gs;
            _hp = hp;
            _suspend = suspend;
            _grappled = grappled;
            _noPlayer = noPlayer;
            _gameOver = gameOver;
            _cloaked = cloaked;
            _hasPlayer = hasPlayer;
            _playerActive = active;
        }

        static void Flag(string name, bool on, PlayerState.gameStates gs) =>
            PlaytestLog.Event("Self", name + "=" + on + " gs=" + gs + Where());

        static string Where() => " in '" + NetworkDamageSystem.CurrentRoomName() + "'";

        /// <summary>The interaction prompt the player stood at when the game state flipped: what they pressed.</summary>
        static string Prompt()
        {
            try
            {
                string p = PlayerState.CurrentInteractionName;
                return string.IsNullOrEmpty(p) ? "" : " prompt='" + p + "'";
            }
            catch (System.Exception e) { Guard.Swallow(e); return ""; }
        }
    }
}
