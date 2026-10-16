// [Call]: every game method the mod patches is a native event the mod cares about (an interaction fired, an item was
// picked up, a door opened, a save ran). This puts one postfix and one finalizer on each of them and writes
//   [Call] ItemPickup.release 'Pistol_Ammo' ran
//   [Call] Doorway_Double.openDoors 'Door_Mensa' SKIPPED          (a mod prefix returned false: the native body did not run)
//   [Call] Interactor.Interact 'Elster' THREW NullReferenceException: ...
// so the log shows what the game did and what the mod stopped it from doing, also where no domain writes a line.
// Per method: a few lines a second, then "(+N suppressed)"; a method called more than HotPerSecond times in a second
// is "hot" and only counted (one line per HotReportSeconds). Per-frame methods and coroutine steps are left out.
// Not a [HarmonyPatch] class: like HarmonyPhaseTiming it piggybacks on the targets the real patches resolved.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SyncRADation.Sync
{
    internal static class CallTrace
    {
        const int LinesPerSecond = 4;
        const int SkipLinesPerSecond = 8;
        const int HotPerSecond = 25;
        const float HotReportSeconds = 30f;

        sealed class Slot
        {
            public string Name;
            public float WindowAt;
            public int InWindow, Lines, Suppressed;
            public bool Hot;
            public float HotAt;
            public int HotCalls, HotSkipped;
            public int Total, Skipped, Threw;
        }

        // persistent: one slot per patched method, filled at boot; the counters are cleared by Reset
        static readonly Dictionary<MethodBase, Slot> _slots = new Dictionary<MethodBase, Slot>(256);

        // persistent: constant name tables
        static readonly HashSet<string> SkipNames = new HashSet<string>
        {
            "Update", "LateUpdate", "FixedUpdate", "MoveNext", "Awake", "OnEnable", "OnDisable"
        };
        // persistent: constant name table (queries and plumbing, not events)
        static readonly HashSet<string> SkipFull = new HashSet<string>
        {
            "InventoryManager.getCount", "InventoryManager.getName", "InventoryManager.hasItem", "Time.set_timeScale",
            "Cursor.set_lockState", "FileBasedPrefs.DataScrambler", "OobChecker.Check", "StudioEventEmitter.Play",
            "StudioEventEmitter.Stop", "RuntimeManager.PlayOneShot", "Interaction.setInRange", "Interaction.reset",
            "AnItem.localizedName", "InventoryThumbs.ItemViewHandling"
        };

        internal static void Install(HarmonyLib.Harmony harmony, Type[] types)
        {
            if (!Config.ModConfig.CallTraceOn)
            {
                ModRuntime.Log?.Msg("[Call] trace off (CallTrace=false)");
                return;
            }
            var targets = new HashSet<MethodBase>();
            for (int i = 0; i < types.Length; i++)
            {
                try
                {
                    var attrs = types[i].GetCustomAttributes(typeof(HarmonyPatch), true);
                    for (int a = 0; a < attrs.Length; a++)
                    {
                        var info = ((HarmonyPatch)attrs[a]).info;
                        if (info == null || info.declaringType == null || string.IsNullOrEmpty(info.methodName)) continue;
                        if (SkipNames.Contains(info.methodName)) continue;
                        string full = info.declaringType.Name + "." + info.methodName;
                        if (SkipFull.Contains(full)) continue;
                        var m = info.argumentTypes != null
                            ? AccessTools.DeclaredMethod(info.declaringType, info.methodName, info.argumentTypes)
                            : AccessTools.DeclaredMethod(info.declaringType, info.methodName);
                        if (m == null || !targets.Add(m)) continue;
                        _slots[m] = new Slot { Name = full };
                    }
                }
                catch (Exception ex) { Guard.Swallow("CallTrace.scan", ex); }
            }
            var after = new HarmonyMethod(typeof(CallTrace).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic))
            { priority = Priority.Last };
            var threw = new HarmonyMethod(typeof(CallTrace).GetMethod(nameof(Threw), BindingFlags.Static | BindingFlags.NonPublic));
            int ok = 0;
            foreach (var m in targets)
            {
                try { harmony.Patch(m, postfix: after, finalizer: threw); ok++; }
                catch (Exception ex)
                {
                    _slots.Remove(m);
                    Guard.Swallow("CallTrace.patch " + m.Name, ex);
                }
            }
            ModRuntime.Log?.Msg("[Call] trace on " + ok + " patched game methods");
        }

        /// <summary>Session end: the rate state. The totals are printed and cleared by WriteTotals (the summary).</summary>
        public static void Reset()
        {
            foreach (var s in _slots.Values)
            {
                s.WindowAt = 0f;
                s.InWindow = s.Lines = s.Suppressed = 0;
                s.Hot = false;
                s.HotCalls = s.HotSkipped = 0;
            }
        }

        static void After(MethodBase __originalMethod, object __instance, bool __runOriginal)
        {
            try
            {
                if (!NetGate.Live) return;
                Slot s;
                if (!_slots.TryGetValue(__originalMethod, out s)) return;
                s.Total++;
                if (!__runOriginal) s.Skipped++;
                float now = Time.unscaledTime;
                if (s.Hot)
                {
                    s.HotCalls++;
                    if (!__runOriginal) s.HotSkipped++;
                    if (now - s.HotAt < HotReportSeconds) return;
                    ModRuntime.Log?.Msg("[Call] " + s.Name + " x" + s.HotCalls + " in " + (now - s.HotAt).ToString("F0") + "s"
                        + (s.HotSkipped > 0 ? " (" + s.HotSkipped + " SKIPPED)" : ""));
                    s.HotAt = now;
                    s.HotCalls = s.HotSkipped = 0;
                    return;
                }
                if (now - s.WindowAt >= 1f)
                {
                    s.WindowAt = now;
                    s.InWindow = 0;
                    s.Lines = 0;
                }
                s.InWindow++;
                if (s.InWindow > HotPerSecond)
                {
                    s.Hot = true;
                    s.HotAt = now;
                    ModRuntime.Log?.Msg("[Call] " + s.Name + " is called more than " + HotPerSecond + " times a second: counted from here on");
                    return;
                }
                if (s.Lines >= (__runOriginal ? LinesPerSecond : SkipLinesPerSecond))
                {
                    s.Suppressed++;
                    return;
                }
                s.Lines++;
                string line = "[Call] " + s.Name + Who(__instance) + (__runOriginal ? " ran" : " SKIPPED");
                if (s.Suppressed > 0)
                {
                    line += " (+" + s.Suppressed + " suppressed)";
                    s.Suppressed = 0;
                }
                ModRuntime.Log?.Msg(line);
            }
            catch (Exception) { /* a trace postfix must never throw into the game */ }
        }

        static Exception Threw(Exception __exception, MethodBase __originalMethod, object __instance)
        {
            if (__exception == null) return null;
            try
            {
                Slot s;
                if (_slots.TryGetValue(__originalMethod, out s))
                {
                    s.Threw++;
                    string msg = __exception.Message ?? "";
                    int nl = msg.IndexOf('\n');
                    if (nl >= 0) msg = msg.Substring(0, nl);
                    if (msg.Length > 200) msg = msg.Substring(0, 200) + "...";
                    PlaytestLog.Rate("Call", "threw:" + s.Name, 5f, s.Name + Who(__instance) + " THREW "
                        + __exception.GetType().Name + ": " + msg, warn: true);
                }
            }
            catch (Exception) { /* never throws */ }
            return __exception;
        }

        static string Who(object instance)
        {
            if (instance == null) return "";
            try
            {
                var c = instance as Component;
                if (c != null) return " '" + c.gameObject.name + "'";
            }
            catch (Exception) { /* a destroyed object has no name */ }
            return "";
        }

        /// <summary>One line at session end: every native the mod kept from running, and how often.</summary>
        public static void WriteTotals()
        {
            try
            {
                var list = new List<Slot>();
                foreach (var s in _slots.Values)
                    if (s.Skipped > 0 || s.Threw > 0) list.Add(s);
                if (list.Count > 0)
                {
                    list.Sort((a, b) => b.Skipped.CompareTo(a.Skipped));
                    var sb = new StringBuilder("[Call] natives the mod skipped this session (skipped/calls):");
                    for (int i = 0; i < list.Count && i < 40; i++)
                    {
                        sb.Append(' ').Append(list[i].Name).Append(' ').Append(list[i].Skipped).Append('/').Append(list[i].Total);
                        if (list[i].Threw > 0) sb.Append(" threw ").Append(list[i].Threw);
                    }
                    ModRuntime.Log?.Msg(sb.ToString());
                }
                foreach (var s in _slots.Values) s.Total = s.Skipped = s.Threw = 0;
            }
            catch (Exception e) { Guard.Swallow(e); }
        }
    }
}
