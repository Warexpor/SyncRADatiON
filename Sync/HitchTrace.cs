// Sparse hitch cadence: send/recv gaps, frame dt, interp mode. Not a dump of the whole mod.
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class HitchTrace
    {
        private const float GapWarn = 0.08f;
        private const float DtWarn = 0.05f;
        private const float SummaryEvery = 5f;
        private const float EventCooldown = 0.25f;

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
        }

        public static void Frame()
        {
            if (!ModRuntime.VerboseLogging) return;
            float dt = Time.unscaledDeltaTime;
            if (dt > _maxDt) _maxDt = dt;
            if (dt >= DtWarn)
                Event("frame dt=" + (dt * 1000f).ToString("F0") + "ms");
            MaybeSummary();
        }

        public static void Send()
        {
            if (!ModRuntime.VerboseLogging) return;
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
            if (!ModRuntime.VerboseLogging) return;
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
            if (!ModRuntime.VerboseLogging) return;
            if (mode == "lerp") _lerp++;
            else if (mode == "extrap") _extrap++;
            else _hold++;
        }

        public static void Cost(string what, float ms)
        {
            if (!ModRuntime.VerboseLogging) return;
            if (ms > _maxCostMs)
            {
                _maxCostMs = ms;
                _maxCostWhat = what;
            }
            if (ms >= 8f)
                Event(what + " " + ms.ToString("F1") + "ms");
        }

        private static void Event(string msg)
        {
            float now = Time.unscaledTime;
            if (now - _lastEvent < EventCooldown) return;
            _lastEvent = now;
            ModRuntime.Log?.Msg("[Hitch] " + msg);
        }

        private static void MaybeSummary()
        {
            float now = Time.unscaledTime;
            if (_lastSummary <= 0f) _lastSummary = now;
            if (now - _lastSummary < SummaryEvery) return;
            float span = now - _lastSummary;
            if (span < 0.5f) return;
            _lastSummary = now;
            float sendHz = span > 0f ? _sends / span : 0f;
            float recvHz = span > 0f ? _recvs / span : 0f;
            ModRuntime.Log?.Msg("[Hitch] 5s sendHz=" + sendHz.ToString("F1")
                + " recvHz=" + recvHz.ToString("F1")
                + " maxSend=" + (_maxSendGap * 1000f).ToString("F0") + "ms"
                + " maxRecv=" + (_maxRecvGap * 1000f).ToString("F0") + "ms"
                + " maxDt=" + (_maxDt * 1000f).ToString("F0") + "ms"
                + " lerp=" + _lerp + " extrap=" + _extrap + " hold=" + _hold
                + " cost=" + _maxCostWhat + " " + _maxCostMs.ToString("F1") + "ms");
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
        }
    }
}
