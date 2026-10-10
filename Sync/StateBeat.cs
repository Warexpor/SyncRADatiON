// [State]: one line that says where everyone is and what the world is doing, written when that picture changes
// (a room, a game state, a peer going down, combat, a cutscene, an enemy dying) and every HeartbeatSeconds otherwise.
// Reading a log around a problem starts at the nearest [State] line instead of at twenty scattered ones.
using System.Text;
using SyncRADation.Networking;
using SyncRADation.Players;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class StateBeat
    {
        const float SampleSeconds = 0.5f;
        const float MinGapSeconds = 2f;
        const float HeartbeatSeconds = 30f;

        static float _nextSample;
        static float _lastAt = -999f;
        static string _lastSig = "";
        static readonly StringBuilder _sig = new StringBuilder(160);
        static readonly StringBuilder _line = new StringBuilder(320);

        public static void Reset()
        {
            _nextSample = 0f;
            _lastAt = -999f;
            _lastSig = "";
        }

        /// <summary>Write the line on the next tick whatever changed (a peer joined, a scene loaded).</summary>
        public static void Force() => _lastSig = "";

        public static void Tick(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected) return;
            float now = Time.unscaledTime;
            if (now < _nextSample) return;
            _nextSample = now + SampleSeconds;
            try { Sample(net, now); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void Sample(LanNetworkManager net, float now)
        {
            _sig.Length = 0;
            _line.Length = 0;

            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception e) { Guard.Swallow(e); }
            string room = NetworkDamageSystem.CurrentRoomName();
            var gs = PlayerState.gameState;
            bool dead = NetworkDamageSystem.IsDead;
            GameObject me = PlayerState.player;

            // A cutscene flips cutscene <-> dialogue with every line of text, and a door transit is a second of
            // traversing: neither is news. The line itself still prints the exact state.
            string cut = CutsceneSync.LiveNames();
            string coarse = cut.Length > 0 ? "cut"
                : gs == PlayerState.gameStates.traversing ? ((int)PlayerState.gameStates.play).ToString()
                : ((int)gs).ToString();
            _sig.Append(scene).Append('|').Append(room).Append('|').Append(coarse).Append(dead ? "|D" : "|A");
            _line.Append(scene).Append(" | me p").Append(net.LocalPlayerId).Append(" '").Append(room).Append("' gs=").Append(gs)
                .Append(" hp=").Append(PlayerState.hp);
            if (dead) _line.Append(" DOWN");
            try { _line.Append(" weapon=").Append(WeaponUtils.EquippedWeaponType()); }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (me != null) _line.Append(" at=").Append(Pos(me.transform.position));

            foreach (int pid in net.GetRemotePlayerIds())
            {
                string pr = PartyVitals.RoomOf(pid) ?? "";
                bool down = PartyVitals.IsDown(pid);
                string ps = net.SceneOf(pid) ?? "";
                _sig.Append("|p").Append(pid).Append(pr).Append(down ? 'D' : 'A').Append(ps);
                _line.Append(" | p").Append(pid).Append(" '").Append(pr.Length > 0 ? pr : "?").Append('\'');
                if (ps.Length > 0 && ps != scene) _line.Append(" scene=").Append(ps);
                if (down) _line.Append(" DOWN");
                Vector3 pp;
                if (me != null && PartyVitals.TryGetPos(pid, out pp))
                    _line.Append(" d=").Append(Vector3.Distance(pp, me.transform.position).ToString("F0"));
                _line.Append(" ping=").Append(net.GetPeerPing(pid)).Append("ms");
            }

            int total = 0, alive = 0, awake = 0, hunting = 0;
            foreach (var kv in WorldRegistry.AllEnemies())
            {
                var e = kv.Value;
                if (e == null) continue;
                total++;
                var st = e.state;
                if (st == EnemyController.enemystate.dead) continue;
                alive++;
                if (!e.gameObject.activeInHierarchy) continue;
                awake++;
                if (st != EnemyController.enemystate.sleep) hunting++;
            }
            bool inCombat = EnemyManager.inCombat, presence = EnemyManager.enemyPresence;
            _sig.Append("|e").Append(alive).Append(hunting > 0 ? 'h' : '-').Append(inCombat ? 'c' : '-').Append(presence ? 'p' : '-');
            _line.Append(" | enemies ").Append(alive).Append('/').Append(total).Append(" alive, ").Append(awake).Append(" awake, ")
                .Append(hunting).Append(" hunting, inCombat=").Append(inCombat).Append(" presence=").Append(presence);

            _sig.Append("|c").Append(cut);
            if (cut.Length > 0) _line.Append(" | cutscene ").Append(cut);

            string sig = _sig.ToString();
            bool changed = sig != _lastSig;
            if (changed ? now - _lastAt < MinGapSeconds : now - _lastAt < HeartbeatSeconds) return;
            _lastSig = sig;
            _lastAt = now;
            float dt = Time.smoothDeltaTime;
            _line.Append(" | fps=").Append(dt > 0.0001f ? (1f / dt).ToString("F0") : "?").Append(changed ? "" : " (heartbeat)");
            PlaytestLog.EventAlways("State", _line.ToString());
        }

        static string Pos(Vector3 p) => "(" + p.x.ToString("F0") + "," + p.y.ToString("F0") + "," + p.z.ToString("F1") + ")";
    }
}
