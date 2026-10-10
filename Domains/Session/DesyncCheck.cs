// Live desync check: every DesyncCheckSeconds the host sends its shared-world digest (TestPilot.SharedLines, the same
// lines the pilot's digest files hold), each client builds its own at that moment and compares. A key that differs in
// two checks in a row is logged once as "[Desync] DESYNC <section> <key>: host=... client=...", and once more as
// "resolved" when it matches again; the client sends those lines to the host, whose log carries them as "[Desync] pN".
// Two in a row because the two digests are taken a network hop apart: one check can catch a door mid-swing or a hit
// in flight. Compare rules: DesyncCompare (a port of scripts/pilot/digest-diff.py).
using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Config;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    internal static class DesyncCheck
    {
        /// <summary>A "still fine" line every this many checks (and on the first one, and whenever the open count changes).</summary>
        const int OkLineEvery = 15;
        const int MaxReportLines = 40;

        static float _nextAt;
        static int _seq;
        static int _checks;
        static int _lastOpenLogged = -1;

        // Client: chunks of the digest being received.
        static int _rxSeq = -1;
        static int _rxParts;
        static readonly List<string> _rxLines = new List<string>(512);

        // Client: differences of the previous check, and the ones already reported (open).
        static readonly Dictionary<string, string> _prev = new Dictionary<string, string>();
        static readonly Dictionary<string, string> _open = new Dictionary<string, string>();

        static readonly List<string> _mine = new List<string>(512);
        static readonly List<string> _report = new List<string>(8);

        /// <summary>Session end and scene change: every key belongs to one scene of one session.</summary>
        public static void Reset()
        {
            _nextAt = 0f;
            _checks = 0;
            _lastOpenLogged = -1;
            _rxSeq = -1;
            _rxParts = 0;
            _rxLines.Clear();
            _prev.Clear();
            _open.Clear();
        }

        static string ActiveScene()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch (Exception e) { Guard.Swallow(e); return ""; }
        }

        static bool Settled(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected || net.SceneMismatch) return false;
            return TestPilot.LocalSettled();
        }

        // ------------------------------------------------------------------ host

        public static void TickHost(LanNetworkManager net)
        {
            if (!NetGate.Host || !ModConfig.DesyncCheckOn) return;
            float now = Time.unscaledTime;
            if (_nextAt <= 0f) { _nextAt = now + ModConfig.DesyncCheckInterval; return; }
            if (now < _nextAt) return;
            _nextAt = now + ModConfig.DesyncCheckInterval;
            if (net.UnicastActive || !Settled(net)) return;
            try
            {
                _mine.Clear();
                TestPilot.SharedLines(_mine);
                _mine.Sort(StringComparer.Ordinal);
                string scene = ActiveScene();
                int parts = Math.Max(1, (_mine.Count + NetWire.MaxDigestLines - 1) / NetWire.MaxDigestLines);
                if (parts > 255) return;
                _seq++;
                for (int part = 0; part < parts; part++)
                {
                    int from = part * NetWire.MaxDigestLines;
                    int n = Math.Min(NetWire.MaxDigestLines, _mine.Count - from);
                    var slice = new string[Math.Max(0, n)];
                    for (int i = 0; i < slice.Length; i++) slice[i] = _mine[from + i];
                    Send(net, -1, new WorldDigestMessage
                    {
                        Kind = 0, Seq = _seq, Part = (byte)part, Parts = (byte)parts, Scene = scene, Lines = slice
                    });
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        static void Send(LanNetworkManager net, int toPlayer, WorldDigestMessage msg)
        {
            var w = new NetDataWriter();
            w.Put((byte)NetMessageType.WorldDigest);
            msg.Serialize(w);
            if (toPlayer < 0) net.BroadcastRaw(w, DeliveryMethod.ReliableOrdered);
            else net.SendToPlayer(toPlayer, w, DeliveryMethod.ReliableOrdered);
        }

        // ------------------------------------------------------------------ both: receive

        public static void Handle(LanNetworkManager net, WorldDigestMessage msg, int senderId)
        {
            if (NetGate.HostRole)
            {
                // A client's findings, for the host's log.
                if (msg.Kind != 1 || senderId < 1 || msg.Lines == null) return;
                for (int i = 0; i < msg.Lines.Length && i < MaxReportLines + 2; i++)
                {
                    string l = msg.Lines[i] ?? "";
                    if (l.StartsWith("DESYNC", StringComparison.Ordinal)) PlaytestLog.Warn("Desync", "p" + senderId + " " + l);
                    else PlaytestLog.Event("Desync", "p" + senderId + " " + l);
                }
                return;
            }
            if (msg.Kind != 0 || senderId != 0) return;
            if (msg.Seq != _rxSeq)
            {
                _rxSeq = msg.Seq;
                _rxParts = 0;
                _rxLines.Clear();
            }
            if (msg.Lines != null) _rxLines.AddRange(msg.Lines);
            _rxParts++;
            if (_rxParts < msg.Parts) return;
            try { CompareNow(net, msg.Scene ?? ""); }
            catch (Exception e) { Guard.Swallow(e); }
            _rxLines.Clear();
            _rxSeq = -1;
        }

        // ------------------------------------------------------------------ client: compare

        static void CompareNow(LanNetworkManager net, string hostScene)
        {
            if (!Settled(net) || hostScene != ActiveScene()) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _mine.Clear();
            TestPilot.SharedLines(_mine);
            var now = new Dictionary<string, string>();
            int compared, skipped;
            DesyncCompare.Diff(_rxLines, _mine, now, out compared, out skipped);
            _checks++;
            _report.Clear();

            // Differs now and differed in the previous check: a desync (reported once).
            foreach (var kv in now)
            {
                if (!_prev.ContainsKey(kv.Key) || _open.ContainsKey(kv.Key)) continue;
                _open[kv.Key] = kv.Value;
                string line = "DESYNC " + kv.Key + ": " + kv.Value;
                if (_report.Count < MaxReportLines) _report.Add(line);
                PlaytestLog.Warn("Desync", line);
            }
            // Reported and equal again.
            List<string> closed = null;
            foreach (var kv in _open)
                if (!now.ContainsKey(kv.Key)) (closed ?? (closed = new List<string>())).Add(kv.Key);
            if (closed != null)
            {
                foreach (string key in closed)
                {
                    _open.Remove(key);
                    string line = "resolved " + key;
                    if (_report.Count < MaxReportLines) _report.Add(line);
                    PlaytestLog.Event("Desync", line);
                }
            }
            _prev.Clear();
            foreach (var kv in now) _prev[kv.Key] = kv.Value;

            if (_checks == 1 || _checks % OkLineEvery == 0 || _open.Count != _lastOpenLogged)
            {
                _lastOpenLogged = _open.Count;
                // Differing once is usually a state in flight; naming a few says which, should it come back.
                string first = "";
                int shown = 0;
                foreach (var kv in now)
                {
                    if (shown++ >= 3) break;
                    first += (first.Length == 0 ? " (" : ", ") + kv.Key;
                }
                if (first.Length > 0) first += now.Count > 3 ? ", ...)" : ")";
                string line = "check #" + _checks + ": " + _open.Count + " desync open, " + now.Count + " differing now" + first + ", "
                    + compared + " lines compared, " + skipped + " not compared (asleep / per player), "
                    + watch.ElapsedMilliseconds + " ms";
                _report.Add(line);
                PlaytestLog.Event("Desync", line);
            }
            if (_report.Count > 0)
                Send(net, 0, new WorldDigestMessage { Kind = 1, Seq = _checks, Parts = 1, Scene = hostScene, Lines = _report.ToArray() });
        }
    }
}
