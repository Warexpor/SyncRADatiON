// "Cannot walk" trace. Native movement (AlternatePlayerController + ThirdPersonCharacter + 3D Rigidbody) has many
// gates: gameState, suspendInput*, hurt/stun, animating/useAnim/grappled, cutscene/eventScreen, overrideInput,
// MoveOverrideActive, the controller's own enabled/playing/traversing, the rigidbody, timeScale, foto mode, tuner.
// When the local player holds a direction for 1 s and does not move, one [Move] BLOCKED line dumps every gate plus
// the 3D colliders around the player; [Move] free when walking works again. Diagnostics pref only, while connected; at
// most one BLOCKED line per 4 s.
using SyncRADation.Config;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class MoveTrace
    {
        const float WishMin = 0.3f;
        const float StuckAfter = 1.0f;
        const float MovedMin = 0.15f;
        const float RelogEvery = 4f;

        static float _wishSince = -1f;
        static Vector3 _wishFrom;
        static bool _blocked;
        static float _lastLog = -99f;

        public static void Reset()
        {
            _wishSince = -1f;
            _blocked = false;
            _lastLog = -99f;
        }

        public static void Tick()
        {
            if (!ModConfig.DiagnosticsOn) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) { Reset(); return; }
            GameObject player = null;
            try { player = PlayerState.player; } catch (System.Exception e) { Guard.Swallow(e); }
            if (player == null) { Reset(); return; }

            Vector2 wish = Wish();
            Vector3 pos = player.transform.position;
            float now = Time.unscaledTime;
            if (wish.magnitude < WishMin)
            {
                _wishSince = -1f;
                return;
            }
            if (_wishSince < 0f)
            {
                _wishSince = now;
                _wishFrom = pos;
                return;
            }
            float moved = Vector2.Distance(new Vector2(pos.x, pos.y), new Vector2(_wishFrom.x, _wishFrom.y));
            if (moved >= MovedMin)
            {
                if (_blocked)
                {
                    _blocked = false;
                    PlaytestLog.Event("Move", "free at " + Fmt(pos));
                }
                _wishSince = now;
                _wishFrom = pos;
                return;
            }
            if (now - _wishSince < StuckAfter) return;
            if (_blocked && now - _lastLog < RelogEvery) return;
            _blocked = true;
            _lastLog = now;
            try { PlaytestLog.Event("Move", "BLOCKED " + Dump(player, wish, moved, now - _wishSince)); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static Vector2 Wish()
        {
            Vector2 v = Vector2.zero;
            try
            {
                var input = PlayerState.input;
                if (input != null) v = input.Move;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (v.magnitude >= WishMin) return v;
            try
            {
                float x = 0f, y = 0f;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) y -= 1f;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) y += 1f;
                if (x != 0f || y != 0f) v = new Vector2(x, y);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return v;
        }

        static string Dump(GameObject player, Vector2 wish, float moved, float held)
        {
            var sb = new System.Text.StringBuilder(512);
            sb.Append("wish=(").Append(wish.x.ToString("F2")).Append(',').Append(wish.y.ToString("F2")).Append(')')
              .Append(" held=").Append(held.ToString("F1")).Append("s moved=").Append(moved.ToString("F2"))
              .Append(" at=").Append(Fmt(player.transform.position))
              .Append(" focus=").Append(Application.isFocused)
              .Append(" ts=").Append(Time.timeScale.ToString("F2"));
            Try(sb, "gs", () => PlayerState.gameState.ToString());
            Try(sb, "cs", () => PlayerState.charState.ToString());
            Try(sb, "susp", () => PlayerState.suspendInput + "/" + PlayerState.suspendInputCheats + "/" + PlayerState.suspendInputUserLoggedOut);
            Try(sb, "hurt", () => PlayerState.hurt + " stun=" + PlayerState.stunTime.ToString("F2"));
            Try(sb, "anim", () => PlayerState.animating + " useAnim=" + PlayerState.useAnim + " grappled=" + PlayerState.grappled);
            Try(sb, "flags", () => "paused=" + PlayerState.paused + " menu=" + PlayerState.menu + " pauseMenu=" + PlayerState.pauseMenu
                + " cutscene=" + PlayerState.cutscene + " eventScreen=" + PlayerState.eventScreen + " gameOver=" + PlayerState.gameOver
                + " noplayer=" + PlayerState.noplayer);
            Try(sb, "override", () => "(" + PlayerState.overrideInput.x.ToString("F2") + "," + PlayerState.overrideInput.y.ToString("F2")
                + ") moveOverride=" + (AlternatePlayerController.MoveOverrideActive ?? "null") + " restrictRun=" + AlternatePlayerController.restrictRun);
            Try(sb, "foto", () => EideticModule.fotoMode.ToString());
            Try(sb, "tuner", () => RadioManager.tuner.ToString());
            Try(sb, "pcs", () =>
            {
                var pcs = PlayerState.pcs;
                if (pcs == null) return "null";
                string s = "en=" + pcs.enabled + " active=" + pcs.gameObject.activeInHierarchy + " playing=" + pcs.playing
                    + " traversing=" + pcs.traversing + " in=(" + pcs.input.x.ToString("F2") + "," + pcs.input.y.ToString("F2") + ")"
                    + " speed=" + pcs.speed.ToString("F2");
                var rb = pcs._rigidbody;
                if (rb != null)
                    s += " rb kin=" + rb.isKinematic + " cons=" + rb.constraints + " vel=" + Fmt(rb.velocity) + " sleep=" + rb.IsSleeping();
                var tpc = pcs.character;
                if (tpc != null) s += " tpc=" + tpc.enabled;
                return s;
            });
            Try(sb, "walls", Walls);
            Try(sb, "near", () => Near(player));
            return sb.ToString();
        }

        // Native ThirdPersonCharacter.CollisionDetection (Ghidra ThirdPersonCharacter.c): Physics.Raycast along
        // transform.forward from points up the body (and sideOffset left/right), length Distance, mask WallMask,
        // default trigger query (so triggers count). Any hit zeroes m_ForwardAmount: turning still works, walking
        // does not. Same casts here, naming what they hit.
        static string Walls()
        {
            var pcs = PlayerState.pcs;
            var tpc = pcs != null ? pcs.character : null;
            if (tpc == null) return "no tpc";
            var t = tpc.transform;
            Vector3 fwd = t.forward.normalized;
            float dist = tpc.Distance * (Mathf.Abs(fwd.y) * 0.2f + 0.8f);
            int mask = tpc.WallMask.value;
            var seen = new System.Collections.Generic.HashSet<string>();
            var sb = new System.Text.StringBuilder();
            sb.Append("dist=").Append(dist.ToString("F2")).Append(" side=").Append(tpc.sideOffset.ToString("F2"))
              .Append(" fwdAmt=").Append(tpc.m_ForwardAmount.ToString("F2")).Append(" hits:");
            int n = 0;
            float[] heights = { 0.1f, 0.4f, 0.8f, 1.2f, 1.6f };
            float[] sides = { 0f, -1f, 1f };
            for (int h = 0; h < heights.Length; h++)
            {
                for (int s = 0; s < sides.Length; s++)
                {
                    Vector3 o = t.position + t.up * heights[h] + t.right * (tpc.sideOffset * sides[s]);
                    RaycastHit hit;
                    if (!Physics.Raycast(o, fwd, out hit, dist, mask)) continue;
                    var c = hit.collider;
                    if (c == null) continue;
                    var go = c.gameObject;
                    string key = go.name + go.GetInstanceID();
                    if (!seen.Add(key)) continue;
                    sb.Append(' ').Append(Path(go.transform)).Append('[').Append(LayerMask.LayerToName(go.layer))
                      .Append(c.isTrigger ? ",trigger" : "").Append(",d=").Append(hit.distance.ToString("F2")).Append(']');
                    if (++n >= 6) return sb.ToString();
                }
            }
            if (n == 0) sb.Append(" none");
            return sb.ToString();
        }

        static string Path(Transform t)
        {
            string s = t.name;
            var p = t.parent;
            for (int i = 0; i < 3 && p != null; i++, p = p.parent) s = p.name + "/" + s;
            return s;
        }

        static string Near(GameObject player)
        {
            var hits = Physics.OverlapSphere(player.transform.position, 1.2f, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return "none";
            var sb = new System.Text.StringBuilder();
            int n = 0;
            for (int i = 0; i < hits.Length && n < 10; i++)
            {
                var c = hits[i];
                if (c == null) continue;
                var go = c.gameObject;
                if (go == player || go.transform.IsChildOf(player.transform)) continue;
                if (n++ > 0) sb.Append(", ");
                sb.Append(go.name).Append('[').Append(LayerMask.LayerToName(go.layer)).Append(']');
                var p = go.transform.parent;
                if (p != null) sb.Append('<').Append(p.name);
            }
            return n == 0 ? "none" : sb.ToString();
        }

        static void Try(System.Text.StringBuilder sb, string key, System.Func<string> f)
        {
            sb.Append(' ').Append(key).Append('=');
            try { sb.Append(f()); }
            catch (System.Exception e) { sb.Append("err:").Append(e.GetType().Name); }
        }

        static string Fmt(Vector3 v) => "(" + v.x.ToString("F1") + "," + v.y.ToString("F1") + "," + v.z.ToString("F1") + ")";
    }
}
