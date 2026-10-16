// Hitch cadence: send/recv gaps, frame dt, interp mode, per-tick Cost spikes and the 5 s anomaly summary are always on
// (cheap; prints only when a threshold trips, no healthy 5s spam). Per-phase timing ("phase=") and the frame stall
// breakdown ("stall dt=") need the Diagnostics pref: Begin() returns 0 and every phase / stall hook returns at once when off.
using System.Diagnostics;
using SyncRADation.Config;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class HitchTrace
    {
        private const float GapWarn = 0.08f;
        private const float DtWarn = 0.05f;
        private const float SummaryEvery = 5f;
        private const float EventCooldown = 0.25f;
        private const float CostWarnMs = 8f;

        private static float _lastSend;
        private static float _lastRecv;
        private static float _lastSummary;
        private static float _lastEvent;
        private static int _sends;
        private static int _recvs;
        private static int _lerp;
        private static int _extrap;
        private static int _hold;
        private static float _maxSendGap;
        private static float _maxRecvGap;
        private static float _maxDt;
        private static float _maxCostMs;
        private static string _maxCostWhat = "";
        private static bool _hadAnomaly;
        // GC collection counts sampled once per frame (FrameBegin, connected only); -1 = not sampled this frame.
        private static int _gcIl2Frame = -1;
        private static int _gcMonoFrame = -1;
        private static bool _gcIl2Broken; // Il2CppSystem.GC unavailable: stop asking (per process)

        // Phase cost (spike-only): "[Hitch] phase=<name> Nms" when one top-level per-frame phase of the mod takes >= PhaseWarnMs.
        private const float PhaseWarnMs = 50f;
        private const int PhaseMaxPerSecond = 20;
        // Stall breakdown: a frame this long gets a where-did-the-time-go line (first few of a run, then every 10th).
        private const float StallWarnMs = 400f;
        private static long _tUpdBegin;
        private static long _tUpdEnd;
        private static long _tLateBegin;
        private static long _tLateEnd;
        private static float _prevUpdMs;
        private static float _prevScriptMs;
        private static float _prevLateMs;
        private static float _prevGuiMs;
        private static float _guiMs;
        private static int _stallRun;
        private static float _phaseWindow;
        private static int _phaseCount;
        private static long _lastCpuTicks;
        private static long _lastCpuWall;

        public static void Reset()
        {
            _lastSend = 0f;
            _lastRecv = 0f;
            _lastSummary = 0f;
            _lastEvent = 0f;
            _sends = 0;
            _recvs = 0;
            _lerp = 0;
            _extrap = 0;
            _hold = 0;
            _maxSendGap = 0f;
            _maxRecvGap = 0f;
            _maxDt = 0f;
            _maxCostMs = 0f;
            _maxCostWhat = "";
            _hadAnomaly = false;
            _tUpdBegin = _tUpdEnd = _tLateBegin = _tLateEnd = 0;
            _prevUpdMs = _prevScriptMs = _prevLateMs = _prevGuiMs = _guiMs = 0f;
            _stallRun = 0;
            _phaseWindow = 0f;
            _phaseCount = 0;
            _lastCpuTicks = 0;
            _lastCpuWall = 0;
        }

        // ---------------------------------------------------------------- phase cost + stall breakdown

        /// <summary>Phase start stamp; 0 (and End is a no-op) unless the Diagnostics pref is on.</summary>
        public static long Begin() => ModConfig.DiagnosticsOn ? Stopwatch.GetTimestamp() : 0L;

        static float Ms(long ticks) => ticks * 1000f / Stopwatch.Frequency;

        /// <summary>Close a timed phase opened with Begin(); prints only when it exceeded PhaseWarnMs.</summary>
        public static void End(string phase, long t0)
        {
            if (t0 == 0L) return;
            StallWatch.Phase(phase);
            float ms = Ms(Stopwatch.GetTimestamp() - t0);
            if (ms < PhaseWarnMs) return;
            float now = Time.unscaledTime;
            if (now - _phaseWindow >= 1f) { _phaseWindow = now; _phaseCount = 0; }
            if (++_phaseCount > PhaseMaxPerSecond) return;
            PlaytestLog.Event("Hitch", "phase=" + phase + " " + ms.ToString("F0") + "ms");
        }

        /// <summary>End() for a patched game method: the name is only built when the threshold trips.</summary>
        public static void EndMethod(System.Reflection.MethodBase m, long t0)
        {
            if (t0 == 0L || Ms(Stopwatch.GetTimestamp() - t0) < PhaseWarnMs) return;
            End("harmony:" + (m?.DeclaringType?.Name ?? "?") + "." + (m?.Name ?? "?"), t0);
        }

        /// <summary>
        /// Start of ModRuntime.OnUpdate. report=true on a networked session (the only time frame dt is traced): a long frame
        /// is split into this mod's Update/LateUpdate/OnGUI bodies, the game-script gap (Update end to LateUpdate start) and
        /// the render gap (LateUpdate end to this Update: culling, present, vsync, compositor, other mods). render ~ dt means
        /// the frame was lost outside any mod code.
        /// </summary>
        public static void FrameBegin(bool report)
        {
            // GC counts back the always-on Cost spike lines; the stall breakdown is Diagnostics only.
            SampleGc(report);
            if (!ModConfig.DiagnosticsOn) return;
            long now = Stopwatch.GetTimestamp();
            float dt = 0f;
            try { dt = Time.unscaledDeltaTime; } catch (System.Exception e) { Guard.Swallow(e); }
            if (report && dt * 1000f >= StallWarnMs && _tLateEnd != 0)
            {
                _stallRun++;
                if (_stallRun <= 3 || _stallRun % 10 == 0)
                    StallReport(now, dt);
            }
            else if (dt * 1000f < StallWarnMs) _stallRun = 0;
            _prevGuiMs = _guiMs;
            _guiMs = 0f;
            _tUpdBegin = now;
        }

        /// <summary>Frame-start GC counts: a Cost spike reports the collections since then (two calls per frame, not per Cost).</summary>
        static void SampleGc(bool report)
        {
            _gcMonoFrame = -1;
            _gcIl2Frame = -1;
            if (!report) return;
            _gcMonoFrame = System.GC.CollectionCount(0);
            _gcIl2Frame = Il2GcCount();
        }

        static int Il2GcCount()
        {
            if (_gcIl2Broken) return -1;
            try { return Il2CppSystem.GC.CollectionCount(0); }
            catch (System.Exception e)
            {
                _gcIl2Broken = true;
                Guard.Swallow("HitchTrace.il2cppGc", e);
                return -1;
            }
        }

        public static void FrameEnd()
        {
            if (!ModConfig.DiagnosticsOn) return;
            _tUpdEnd = Stopwatch.GetTimestamp();
            _prevUpdMs = Ms(_tUpdEnd - _tUpdBegin);
        }

        public static void LateBegin()
        {
            if (!ModConfig.DiagnosticsOn) return;
            _tLateBegin = Stopwatch.GetTimestamp();
            if (_tUpdEnd != 0) _prevScriptMs = Ms(_tLateBegin - _tUpdEnd);
        }

        public static void LateEnd()
        {
            if (!ModConfig.DiagnosticsOn) return;
            _tLateEnd = Stopwatch.GetTimestamp();
            _prevLateMs = Ms(_tLateEnd - _tLateBegin);
        }

        /// <summary>OnGUI runs several times per frame (layout, repaint, input events): accumulate.</summary>
        public static void GuiEnd(long t0)
        {
            if (t0 == 0L) return;
            _guiMs += Ms(Stopwatch.GetTimestamp() - t0);
        }

        static void StallReport(long now, float dt)
        {
            float renderGap = Ms(now - _tLateEnd);
            string cpu = "?";
            try
            {
                long wall = now;
                using (var proc = Process.GetCurrentProcess())
                {
                    long cpuTicks = proc.TotalProcessorTime.Ticks;
                    if (_lastCpuWall != 0)
                        cpu = ((cpuTicks - _lastCpuTicks) / 10000f).ToString("F0") + "ms/" + Ms(wall - _lastCpuWall).ToString("F0") + "ms";
                    _lastCpuTicks = cpuTicks;
                    _lastCpuWall = wall;
                }
            }
            catch (System.Exception e) { Guard.Swallow("HitchTrace.cpu", e); }
            string env = "";
            try
            {
                env = " foc=" + (Application.isFocused ? 1 : 0) + " bg=" + (Application.runInBackground ? 1 : 0)
                    + " vsync=" + QualitySettings.vSyncCount + " fps=" + Application.targetFrameRate
                    + " ts=" + Time.timeScale.ToString("F2") + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            }
            catch (System.Exception e) { Guard.Swallow("HitchTrace.env", e); }
            PlaytestLog.Event("Hitch", "stall dt=" + (dt * 1000f).ToString("F0") + "ms (x" + _stallRun + ") modUpdate=" + _prevUpdMs.ToString("F1")
                + " gameScripts=" + _prevScriptMs.ToString("F1") + " modLate=" + _prevLateMs.ToString("F1")
                + " modGui=" + _prevGuiMs.ToString("F1") + " renderGap=" + renderGap.ToString("F0")
                + "ms cpu=" + cpu + env);
        }

        public static void Frame()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > _maxDt) _maxDt = dt;
            if (dt >= DtWarn)
                Event("frame dt=" + (dt * 1000f).ToString("F0") + "ms");
            MaybeSummary();
        }

        public static void Send()
        {
            float now = Time.unscaledTime;
            if (_lastSend > 0f)
            {
                float gap = now - _lastSend;
                if (gap > _maxSendGap) _maxSendGap = gap;
                if (gap >= GapWarn)
                    Event("send gap=" + (gap * 1000f).ToString("F0") + "ms");
            }
            _lastSend = now;
            _sends++;
        }

        public static void Recv(int playerId)
        {
            float now = Time.unscaledTime;
            if (_lastRecv > 0f)
            {
                float gap = now - _lastRecv;
                if (gap > _maxRecvGap) _maxRecvGap = gap;
                if (gap >= GapWarn)
                    Event("recv p" + playerId + " gap=" + (gap * 1000f).ToString("F0") + "ms");
            }
            _lastRecv = now;
            _recvs++;
        }

        public static void Interp(string mode)
        {
            if (mode == "lerp") _lerp++;
            else if (mode == "extrap") _extrap++;
            else _hold++;
        }

        public static void Cost(string what, float ms)
        {
            if (ms > _maxCostMs)
            {
                _maxCostMs = ms;
                _maxCostWhat = what;
            }
            if (ms < CostWarnMs) return;
            // GC counts since this frame started: a spike with gc=il2cpp+N / mono+N had a collection land inside the
            // frame (likely inside this tick), not only the tick's own work. Counts are read only on a spike.
            string gc = "";
            if (_gcIl2Frame >= 0)
            {
                int il2 = Il2GcCount();
                if (il2 > _gcIl2Frame) gc += " gc=il2cpp+" + (il2 - _gcIl2Frame);
            }
            if (_gcMonoFrame >= 0)
            {
                int mono = System.GC.CollectionCount(0);
                if (mono > _gcMonoFrame) gc += (gc.Length == 0 ? " gc=" : ",") + "mono+" + (mono - _gcMonoFrame);
            }
            Event(what + " " + ms.ToString("F1") + "ms" + gc);
        }

        private static void Event(string msg)
        {
            _hadAnomaly = true;
            float now = Time.unscaledTime;
            if (now - _lastEvent < EventCooldown) return;
            _lastEvent = now;
            PlaytestLog.Event("Hitch", msg);
        }

        private static void MaybeSummary()
        {
            float now = Time.unscaledTime;
            if (_lastSummary <= 0f) _lastSummary = now;
            if (now - _lastSummary < SummaryEvery) return;
            float span = now - _lastSummary;
            if (span < 0.5f) return;
            _lastSummary = now;
            bool bad = _hadAnomaly
                || _maxSendGap >= GapWarn
                || _maxRecvGap >= GapWarn
                || _maxDt >= DtWarn
                || _maxCostMs >= CostWarnMs;
            if (bad)
            {
                float sendHz = span > 0f ? _sends / span : 0f;
                float recvHz = span > 0f ? _recvs / span : 0f;
                PlaytestLog.Event("Hitch", "5s sendHz=" + sendHz.ToString("F1")
                    + " recvHz=" + recvHz.ToString("F1")
                    + " maxSend=" + (_maxSendGap * 1000f).ToString("F0") + "ms"
                    + " maxRecv=" + (_maxRecvGap * 1000f).ToString("F0") + "ms"
                    + " maxDt=" + (_maxDt * 1000f).ToString("F0") + "ms"
                    + " lerp=" + _lerp + " extrap=" + _extrap + " hold=" + _hold
                    + " cost=" + _maxCostWhat + " " + _maxCostMs.ToString("F1") + "ms");
            }
            _sends = 0;
            _recvs = 0;
            _lerp = 0;
            _extrap = 0;
            _hold = 0;
            _maxSendGap = 0f;
            _maxRecvGap = 0f;
            _maxDt = 0f;
            _maxCostMs = 0f;
            _maxCostWhat = "";
            _hadAnomaly = false;
        }
    }
}
