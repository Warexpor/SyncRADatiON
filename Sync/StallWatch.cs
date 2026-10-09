// Main-thread stall watch (Diagnostics pref only): a background thread notices the frame counter stop and logs where the
// mod last was. Test pilot clients froze after a scene load and grew from 5 to 13 GB in seconds with no log line, so the
// memory guard could only kill them.
using System;
using System.Threading;
using SyncRADation.Config;

namespace SyncRADation.Sync
{
    public static class StallWatch
    {
        const int PollMs = 200;
        const int ReportAfterMs = 1000;
        const int ReportEveryMs = 1000;
        // A stall that grew the process by this much is the runaway (5 GB -> 10+ GB in about 3 s, test pilot clients):
        // its report carries the native frames at once (NativeStack). An abort of the main thread (the first try) only
        // landed when native CutsceneManager.Skip returned and left the client stuck in the cutscene state.
        const long RunawayBytes = 1024L * 1024 * 1024;

        // persistent: process-wide watcher thread
        static Thread _watcher;
        // persistent: process-wide frame counter written by the main thread, read by the watcher
        static int _frame;
        // persistent: last place markers, written by the main thread, read by the watcher (diagnostics only)
        static volatile string _phase = "";
        // persistent: last place markers, written by the main thread, read by the watcher (diagnostics only)
        static volatile System.Reflection.MethodBase _method;
        // persistent: last place markers, written by the main thread, read by the watcher (diagnostics only)
        static volatile System.Reflection.MethodBase _lastDone;
        // persistent: last place markers, written by the main thread, read by the watcher (diagnostics only)
        static volatile int _msg = -1;

        /// <summary>Main thread, once per frame (ModRuntime.OnUpdate): starts the watcher on first call.</summary>
        public static void Frame()
        {
            if (!ModConfig.DiagnosticsOn) return;
            Interlocked.Increment(ref _frame);
            if (_watcher != null) return;
            NativeStack.CaptureMain();
            _watcher = new Thread(Watch) { IsBackground = true, Name = "SyncRADation.StallWatch" };
            _watcher.Start();
        }

        public static void Phase(string phase) => _phase = phase;
        public static void Enter(System.Reflection.MethodBase m) => _method = m;
        public static void Exit(System.Reflection.MethodBase m)
        {
            _method = null;
            _lastDone = m;
        }

        static string Name(System.Reflection.MethodBase m) =>
            m != null ? (m.DeclaringType?.Name ?? "?") + "." + m.Name : "-";
        public static void Message(int type) => _msg = type;
        public static void MessageDone() => _msg = -1;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MemoryCounters
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
        static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        // The working set from kernel32 directly. Under Proton, Process.PrivateMemorySize64 is always 0 (test pilot
        // run 4: "process 0->0MB") and a file read of Z:\proc\self\status is opened by wineserver, so it reports
        // wineserver's 16 MB (run 5); Wine's GetProcessMemoryInfo reads the game's own VmRSS in-process.
        static long ProcessBytes()
        {
            try
            {
                var c = new MemoryCounters { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryCounters)) };
                if (GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.cb)) return (long)c.WorkingSetSize.ToUInt64();
            }
            catch { }
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                    return p.WorkingSet64;
            }
            catch { return 0; }
        }

        static void Watch()
        {
            int seen = -1;
            long stillSince = 0, nextReport = 0, heapAtStart = 0, procAtStart = 0;
            ModRuntime.Log?.Msg("[Stall] watching: process " + (ProcessBytes() >> 20) + "MB");
            while (true)
            {
                Thread.Sleep(PollMs);
                try
                {
                    int f = Volatile.Read(ref _frame);
                    long now = Environment.TickCount;
                    if (f != seen)
                    {
                        // Frames still running: a jump of the runaway size within one poll is reported (no abort:
                        // the main thread is not stuck, the place markers say where this frame was).
                        long p = ProcessBytes();
                        if (procAtStart > 0 && p - procAtStart > RunawayBytes)
                        {
                            var rm = _method;
                            ModRuntime.Log?.Warning("[Stall] memory jump " + (procAtStart >> 20) + "->" + (p >> 20)
                                + "MB in " + PollMs + "ms while frames run: phase after=" + _phase + " harmony="
                                + Name(rm) + " lastPatched=" + Name(_lastDone));
                        }
                        seen = f;
                        stillSince = now;
                        nextReport = now + ReportAfterMs;
                        heapAtStart = GC.GetTotalMemory(false);
                        procAtStart = p;
                        continue;
                    }
                    long proc = ProcessBytes();
                    bool runaway = procAtStart > 0 && proc - procAtStart > RunawayBytes;
                    if (now < nextReport && !runaway) continue;
                    nextReport = now + ReportEveryMs;
                    long heap = GC.GetTotalMemory(false);
                    var m = _method;
                    string where = "phase after=" + _phase
                        + " harmony=" + Name(m) + " lastPatched=" + Name(_lastDone)
                        + " msg=" + (_msg >= 0 ? ((Networking.NetMessageType)_msg).ToString() : "-");
                    // From the second report on (or on a runaway), the main thread's native frames: a scene load shows
                    // Unity's loader, the runaway names the GameAssembly function it loops in.
                    string native = now - stillSince >= 2000 || runaway ? NativeStack.Sample() : null;
                    ModRuntime.Log?.Warning("[Stall] main thread stuck " + (now - stillSince) + "ms " + where
                        + " managedHeap " + (heapAtStart >> 20) + "->" + (heap >> 20) + "MB"
                        + " process " + (procAtStart >> 20) + "->" + (proc >> 20) + "MB"
                        + (native != null ? " native " + native : ""));
                }
                catch (Exception e) { Guard.Swallow(e); }
            }
        }
    }
}
