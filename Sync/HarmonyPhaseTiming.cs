// Hitch instrumentation (Diagnostics pref only, installed at boot): times every Harmony-patched Update/LateUpdate/FixedUpdate
// (original + all our prefixes/postfixes) and prints "[Hitch] phase=<Type.Method> Nms" only when one call exceeds the phase
// threshold (see HitchTrace.EndMethod).
// Every other patched method gets a marker-only prefix / finalizer so the stall watch can name the last patched game
// method that ran (a client froze natively after a scene load with no Update-family patch on the stack, test pilot).
// Not a [HarmonyPatch] class on purpose: it piggybacks on the targets the real patches already resolved.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace SyncRADation.Sync
{
    internal static class HarmonyPhaseTiming
    {
        // persistent: constant name table
        private static readonly string[] FrameMethods = { "Update", "LateUpdate", "FixedUpdate" };

        internal static void Install(HarmonyLib.Harmony harmony, Type[] types)
        {
            // Two extra patch calls per Update-family call of every patched game type: only for a diagnostics run.
            if (!Config.ModConfig.DiagnosticsOn)
            {
                ModRuntime.Log?.Msg("[Hitch] phase timing off (Diagnostics=false)");
                return;
            }
            var targets = new HashSet<MethodBase>();
            var others = new HashSet<MethodBase>();
            for (int i = 0; i < types.Length; i++)
            {
                try
                {
                    var attrs = types[i].GetCustomAttributes(typeof(HarmonyPatch), true);
                    for (int a = 0; a < attrs.Length; a++)
                    {
                        var info = ((HarmonyPatch)attrs[a]).info;
                        if (info == null || info.declaringType == null || string.IsNullOrEmpty(info.methodName)) continue;
                        bool frame = Array.IndexOf(FrameMethods, info.methodName) >= 0;
                        var m = info.argumentTypes != null
                            ? AccessTools.DeclaredMethod(info.declaringType, info.methodName, info.argumentTypes)
                            : AccessTools.DeclaredMethod(info.declaringType, info.methodName);
                        if (m == null) continue;
                        if (frame) targets.Add(m);
                        else others.Add(m);
                    }
                }
                catch (Exception ex) { Guard.Swallow("HarmonyPhaseTiming.scan", ex); }
            }

            var pre = new HarmonyMethod(typeof(HarmonyPhaseTiming).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic))
            { priority = Priority.First };
            var post = new HarmonyMethod(typeof(HarmonyPhaseTiming).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic))
            { priority = Priority.Last };
            int ok = 0;
            foreach (var m in targets)
            {
                try { harmony.Patch(m, pre, post); ok++; }
                catch (Exception ex) { Guard.Swallow("HarmonyPhaseTiming.patch " + m.Name, ex); }
            }
            var mark = new HarmonyMethod(typeof(HarmonyPhaseTiming).GetMethod(nameof(Mark), BindingFlags.Static | BindingFlags.NonPublic))
            { priority = Priority.First };
            var done = new HarmonyMethod(typeof(HarmonyPhaseTiming).GetMethod(nameof(Done), BindingFlags.Static | BindingFlags.NonPublic));
            int marked = 0;
            foreach (var m in others)
            {
                if (targets.Contains(m)) continue;
                try { harmony.Patch(m, mark, finalizer: done); marked++; }
                catch (Exception ex) { Guard.Swallow("HarmonyPhaseTiming.mark " + m.Name, ex); }
            }
            ModRuntime.Log?.Msg("[Hitch] phase timing on " + ok + " Update-family patches, stall markers on " + marked + " others");
        }

        private static void Mark(MethodBase __originalMethod) => StallWatch.Enter(__originalMethod);

        private static Exception Done(Exception __exception, MethodBase __originalMethod)
        {
            StallWatch.Exit(__originalMethod);
            return __exception;
        }

        private static void Pre(out long __state, MethodBase __originalMethod)
        {
            StallWatch.Enter(__originalMethod);
            __state = Stopwatch.GetTimestamp();
        }

        private static void Post(long __state, MethodBase __originalMethod)
        {
            StallWatch.Exit(__originalMethod);
            HitchTrace.EndMethod(__originalMethod, __state);
        }
    }
}
