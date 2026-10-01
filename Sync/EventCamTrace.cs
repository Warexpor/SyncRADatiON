// Event-screen click trace (keypads, cryo codepad, any zoom-in puzzle). Native EventScreen3DCam only
// triggers the interaction under its cursor when gameState == eventScreen, zoom >= 1 and Use (or Attack on
// controller 1) was pressed (Ghidra EventScreen3DCam.c Update). One line per click with each of those
// inputs, so "clicks do nothing" names the failing condition. Always on while connected; at most one line
// per 0.15 s.
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class EventCamTrace
    {
        static float _lastLog = -99f;
        static bool _wasInScreen;

        public static void Tick()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            bool inScreen;
            try { inScreen = PlayerState.gameState == PlayerState.gameStates.eventScreen; }
            catch { return; }
            if (inScreen != _wasInScreen)
            {
                _wasInScreen = inScreen;
                PlaytestLog.Event("EventCam", inScreen ? "screen open" : "screen closed");
            }
            if (!inScreen) return;

            bool mouse = false, use = false, attack = false;
            try { mouse = Input.GetMouseButtonDown(0); } catch (System.Exception e) { Guard.Swallow(e); }
            string ctrl = "?";
            try
            {
                var input = PlayerState.input;
                if (input != null)
                {
                    if (input.Use != null) use = input.Use.WasPressed;
                    if (input.Attack != null) attack = input.Attack.WasPressed;
                    ctrl = input.lastActiveController.ToString();
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (!mouse && !use && !attack) return;
            float now = Time.unscaledTime;
            if (now - _lastLog < 0.15f) return;
            _lastLog = now;

            try { Log(net, mouse, use, attack, ctrl); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void Log(LanNetworkManager net, bool mouse, bool use, bool attack, string ctrl)
        {
            var cam = Object.FindObjectOfType<EventScreen3DCam>();
            var cur = EventScreen3DCam.cursorPosition;
            var mp = Input.mousePosition;
            string s = "click mouse=" + mouse + " use=" + use + " attack=" + attack + " ctrl=" + ctrl
                + " cursor=(" + cur.x.ToString("F2") + "," + cur.y.ToString("F2") + ")"
                + " unlocked=" + EventScreen3DCam.unlockedCursor
                + " mouse=(" + mp.x.ToString("F0") + "," + mp.y.ToString("F0") + ")"
                + " screen=" + Screen.width + "x" + Screen.height + " scale=" + ScalingManager.scale
                + " lock=" + Cursor.lockState + " focus=" + Application.isFocused;
            if (cam == null)
            {
                PlaytestLog.Event("EventCam", s + " cam=none");
                return;
            }
            s += " zoom=" + cam.zoom.ToString("F2") + " depth=" + cam.depth;
            var ci = cam.currentInter;
            s += " current=" + (ci != null ? Describe(net, ci) : "none");

            Camera c = null;
            try { c = cam.cam; } catch (System.Exception e) { Guard.Swallow(e); }
            if (c == null) c = cam.GetComponentInChildren<Camera>();
            if (c != null)
            {
                RaycastHit hit;
                var ray = c.ViewportPointToRay(new Vector3(cur.x, cur.y, 0f));
                if (Physics.Raycast(ray, out hit, cam.interactionRange))
                {
                    var col = hit.collider;
                    var it = col != null ? col.GetComponent<Interaction>() : null;
                    s += " hit=" + (col != null ? col.gameObject.name : "?")
                        + (it != null ? " [" + Describe(net, it) + "]" : " [no Interaction]");
                }
                else
                    s += " hit=none range=" + cam.interactionRange.ToString("F1");
            }
            PlaytestLog.Event("EventCam", s);
        }

        static string Describe(LanNetworkManager net, Interaction it)
        {
            bool kill = false;
            try { kill = net.PuzzleSync.ShouldKillOverlay(it); } catch (System.Exception e) { Guard.Swallow(e); }
            return it.gameObject.name + " en=" + it.enabled + " active=" + it.gameObject.activeInHierarchy
                + " trig=" + it.triggered + " inRange=" + it.inRange + (kill ? " MOD-KILLED" : "");
        }
    }
}
