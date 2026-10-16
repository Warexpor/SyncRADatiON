// Native stack of the main thread, taken from the stall watch thread (Diagnostics only): suspend, read its context,
// unwind with the PE unwind tables (RtlLookupFunctionEntry / RtlVirtualUnwind), resume. A test pilot client froze inside
// native CutsceneManager.Skip and grew by GBs with no managed frame to name the loop, and ptrace is not allowed on the
// pilot box, so the frames are read in-process. Printed as module+RVA; GameAssembly RVAs match the Ghidra "@ RVA" lines
// (scripts/pilot/symbolize.py).
using System;
using System.Runtime.InteropServices;

namespace SyncRADation.Sync
{
    internal static class NativeStack
    {
        const int MaxFrames = 32;
        const uint ThreadAccess = 0x0002 | 0x0008 | 0x0010 | 0x0040; // SUSPEND_RESUME | GET_CONTEXT | SET_CONTEXT | QUERY_INFORMATION
        const uint ContextAmd64Full = 0x100000 | 0x1 | 0x2 | 0x8; // CONTROL | INTEGER | FLOATING_POINT
        const int ContextSize = 1232;
        const int OffFlags = 0x30, OffRsp = 0x98, OffRip = 0xF8;

        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] static extern uint SuspendThread(IntPtr thread);
        [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] static extern bool GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr RtlLookupFunctionEntry(ulong pc, out ulong imageBase, IntPtr history);
        [DllImport("kernel32.dll")]
        static extern IntPtr RtlVirtualUnwind(uint type, ulong imageBase, ulong pc, IntPtr entry, IntPtr context,
            out IntPtr handlerData, out ulong establisher, IntPtr pointers);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetModuleFileNameW(IntPtr module, System.Text.StringBuilder name, uint size);

        // persistent: native id of the game's main thread, captured once on it
        static uint _mainId;
        // persistent: process-wide buffers of the watcher thread (no allocation while the main thread is suspended)
        static IntPtr _ctx;
        // persistent: process-wide buffers of the watcher thread (no allocation while the main thread is suspended)
        static readonly ulong[] _pcs = new ulong[MaxFrames];

        /// <summary>Main thread, once.</summary>
        public static void CaptureMain()
        {
            try { _mainId = GetCurrentThreadId(); } catch { _mainId = 0; }
        }

        /// <summary>Watcher thread: the main thread's native frames as "module+0xRVA" (or null when not available).</summary>
        public static string Sample()
        {
            if (_mainId == 0) return null;
            int n = 0;
            IntPtr h = IntPtr.Zero;
            try
            {
                if (_ctx == IntPtr.Zero)
                {
                    // CONTEXT needs 16-byte alignment.
                    var raw = Marshal.AllocHGlobal(ContextSize + 16);
                    _ctx = new IntPtr((raw.ToInt64() + 15) & ~15L);
                }
                h = OpenThread(ThreadAccess, false, _mainId);
                if (h == IntPtr.Zero) return null;
                if (SuspendThread(h) == 0xFFFFFFFF) return null;
                try
                {
                    for (int i = 0; i < ContextSize; i += 8) Marshal.WriteInt64(_ctx, i, 0);
                    Marshal.WriteInt32(_ctx, OffFlags, unchecked((int)ContextAmd64Full));
                    if (!GetThreadContext(h, _ctx)) return null;
                    for (; n < MaxFrames; n++)
                    {
                        ulong pc = (ulong)Marshal.ReadInt64(_ctx, OffRip);
                        if (pc == 0) break;
                        _pcs[n] = pc;
                        var entry = RtlLookupFunctionEntry(pc, out ulong imageBase, IntPtr.Zero);
                        if (entry != IntPtr.Zero)
                        {
                            RtlVirtualUnwind(0, imageBase, pc, entry, _ctx, out _, out _, IntPtr.Zero);
                        }
                        else
                        {
                            // Leaf / no unwind info (JIT code): the return address is at rsp.
                            long rsp = Marshal.ReadInt64(_ctx, OffRsp);
                            if (rsp == 0) { n++; break; }
                            Marshal.WriteInt64(_ctx, OffRip, Marshal.ReadInt64(new IntPtr(rsp)));
                            Marshal.WriteInt64(_ctx, OffRsp, rsp + 8);
                        }
                    }
                }
                finally { ResumeThread(h); }
            }
            catch { }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
            if (n == 0) return null;
            var sb = new System.Text.StringBuilder();
            var name = new System.Text.StringBuilder(260);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(" < ");
                // FROM_ADDRESS | UNCHANGED_REFCOUNT
                if (GetModuleHandleExW(0x4 | 0x2, new IntPtr((long)_pcs[i]), out IntPtr mod) && mod != IntPtr.Zero)
                {
                    name.Length = 0;
                    GetModuleFileNameW(mod, name, 260);
                    string file = name.ToString();
                    int slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
                    sb.Append(slash >= 0 ? file.Substring(slash + 1) : file)
                      .Append("+0x").Append((_pcs[i] - (ulong)mod.ToInt64()).ToString("x"));
                }
                else sb.Append("0x").Append(_pcs[i].ToString("x"));
            }
            return sb.ToString();
        }
    }
}
