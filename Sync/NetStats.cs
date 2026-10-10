// Traffic by message type: counted where packets are handed to / taken from LiteNetLib, reported as one [Net] line
// every ReportSeconds while connected. No allocation per packet (arrays indexed by the type byte).
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class NetStats
    {
        const float ReportSeconds = 30f;
        const int Top = 5;

        // Window counters: cleared after every report and by Reset (session end).
        static readonly int[] _txN = new int[256];
        static readonly int[] _txB = new int[256];
        static readonly int[] _rxN = new int[256];
        static readonly int[] _rxB = new int[256];
        static float _windowStart = -1f;

        public static void Reset()
        {
            System.Array.Clear(_txN, 0, 256);
            System.Array.Clear(_txB, 0, 256);
            System.Array.Clear(_rxN, 0, 256);
            System.Array.Clear(_rxB, 0, 256);
            _windowStart = -1f;
        }

        /// <summary>A packet handed to one peer (the writer's first byte is its NetMessageType).</summary>
        public static void Tx(NetDataWriter w)
        {
            if (w == null || w.Length == 0) return;
            byte t = w.Data[0];
            _txN[t]++;
            _txB[t] += w.Length;
        }

        public static void Rx(byte type, int bytes)
        {
            _rxN[type]++;
            _rxB[type] += bytes;
        }

        /// <summary>peer.Send with the packet counted: the direct sends of the domain handlers go through here.</summary>
        public static void Send(NetPeer peer, NetDataWriter w, DeliveryMethod method)
        {
            Tx(w);
            peer.Send(w, method);
        }

        public static void Tick(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected || !net.HasReadyPeers)
            {
                _windowStart = -1f;
                return;
            }
            float now = Time.unscaledTime;
            if (_windowStart < 0f) { _windowStart = now; return; }
            float span = now - _windowStart;
            if (span < ReportSeconds) return;
            _windowStart = now;
            try
            {
                var sb = new StringBuilder(220);
                sb.Append(span.ToString("F0")).Append("s ");
                Describe(sb, "tx", _txN, _txB);
                sb.Append(" | ");
                Describe(sb, "rx", _rxN, _rxB);
                sb.Append(" | ").Append(net.LinkSummary());
                PlaytestLog.Event("Net", sb.ToString());
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            System.Array.Clear(_txN, 0, 256);
            System.Array.Clear(_txB, 0, 256);
            System.Array.Clear(_rxN, 0, 256);
            System.Array.Clear(_rxB, 0, 256);
        }

        static void Describe(StringBuilder sb, string dir, int[] n, int[] b)
        {
            int totalN = 0;
            long totalB = 0;
            for (int i = 0; i < 256; i++) { totalN += n[i]; totalB += b[i]; }
            sb.Append(dir).Append(' ').Append(totalN).Append(" msg ").Append(Kb(totalB));
            if (totalN == 0) return;
            sb.Append(" top:");
            // Top by bytes: five passes over 256 slots, twice per report.
            int last = int.MaxValue, lastIdx = -1;
            for (int k = 0; k < Top; k++)
            {
                int best = -1;
                for (int i = 0; i < 256; i++)
                {
                    if (n[i] == 0) continue;
                    if (b[i] > last || (b[i] == last && i <= lastIdx)) continue;
                    if (best < 0 || b[i] > b[best]) best = i;
                }
                if (best < 0) break;
                last = b[best];
                lastIdx = best;
                sb.Append(' ').Append((NetMessageType)best).Append(' ').Append(n[best]).Append('/').Append(Kb(b[best]));
            }
        }

        static string Kb(long bytes) => (bytes / 1024f).ToString("0.0") + "KB";
    }
}
