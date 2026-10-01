// Boot-time audit: does every Harmony patch target still resolve, and does every member the mod looks up
// by name via reflection still exist? A game update (or an Il2Cpp-unhollower naming quirk) that renames
// something otherwise fails silently: the patch class is skipped / the lookup returns null and the feature
// is just dead. Output: ONE "[Harmony] audit: N ok, M missing, T ms" line plus one line per missing item.
// Runs only with the Diagnostics pref; otherwise one "[Harmony] audit off" line (PatchAllSafe still logs every skipped class).
// Never throws. Cost is measured and logged (T): resolution only, nothing is invoked except [HarmonyTargetMethod]
// providers, but it reads custom attributes of every patch method, so it is not "a couple of ms".
// The name-keyed Shader.Find check runs later (RunLate, first scene load): the shader table is not ready at boot.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SyncRADation
{
    internal static class PatchAudit
    {
        private const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Members looked up by string name through reflection outside Harmony attributes. Keep in sync with the
        /// call sites (owner listed in the last column); the audit only checks the names exist.
        /// </summary>
        private static readonly ReflectedMember[] Reflected =
        {
            new ReflectedMember(typeof(EventSlidingDoor), "cycle", MemberKind.Method, "DoorNative"),
            new ReflectedMember(typeof(StorageBox), "open", MemberKind.FieldOrProperty, "StorageLidSyncService"),
            new ReflectedMember(typeof(UseItemMultiInteraction), "ready", MemberKind.Method, "InteractionSyncService"),
        };

        /// <summary>
        /// Name-keyed asset lookups (Shader.Find in RemoteWeaponEffects): the laser-dot material uses the first one that resolves,
        /// so only "none resolves" is reported. Complete list of name-string lookups outside Harmony attributes: the table above
        /// (every GetField/GetMethod/GetProperty literal in the tree) + these shaders. Not auditable at boot: Transform.Find("Model3D")
        /// in DroppedItemSpawner (a child of a prefab clone that only exists at runtime).
        /// </summary>
        private static readonly string[] ShaderAnyOf = { "Sprites/Default", "Unlit/Color" };

        private enum MemberKind { Method, FieldOrProperty }

        private struct ReflectedMember
        {
            public readonly Type Owner;
            public readonly string Name;
            public readonly MemberKind Kind;
            public readonly string UsedBy;

            public ReflectedMember(Type owner, string name, MemberKind kind, string usedBy)
            {
                Owner = owner;
                Name = name;
                Kind = kind;
                UsedBy = usedBy;
            }
        }

        public static void Run(Type[] types)
        {
            // The full audit reads the custom attributes of every patch method (tens of ms at boot): Diagnostics only.
            // Always on regardless: PatchAllSafe's "[Harmony] patched N classes, skipped M" + one line per skipped class.
            if (!Config.ModConfig.DiagnosticsOn)
            {
                ModRuntime.SetPatchAuditOff();
                ModRuntime.Log?.Msg("[Harmony] audit off (Diagnostics=false)");
                return;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int ok = 0;
            var missing = new List<string>();
            try
            {
                AuditPatches(types, ref ok, missing);
            }
            catch (Exception ex)
            {
                Guard.Swallow("PatchAudit.patches", ex);
                missing.Add("audit of patch targets aborted: " + ex.Message);
            }
            try
            {
                AuditReflected(ref ok, missing);
            }
            catch (Exception ex)
            {
                Guard.Swallow("PatchAudit.reflected", ex);
                missing.Add("audit of reflected members aborted: " + ex.Message);
            }

            clock.Stop();
            ModRuntime.SetPatchAudit(missing.Count == 0, ok, missing.Count, clock.ElapsedMilliseconds);
            ModRuntime.Log?.Msg("[Harmony] audit: " + ok + " ok, " + missing.Count + " missing, " + clock.ElapsedMilliseconds + " ms");
            for (int i = 0; i < missing.Count; i++)
            {
                ModRuntime.Log?.Warning("[Harmony] audit missing: " + missing[i]);
                ModRuntime.AddPatchAuditMissing(missing[i]);
            }
        }

        // ------------------------------------------------------------------ Harmony targets

        private sealed class Merged
        {
            public Type Declaring;
            public string Name;
            public MethodType? Kind;
            public Type[] Args;
        }

        private static void AuditPatches(Type[] types, ref int ok, List<string> missing)
        {
            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];
                List<HarmonyMethod> classInfos;
                try { classInfos = Infos(t); }
                catch (Exception ex) { Guard.Swallow("PatchAudit.attrs", ex); continue; }

                MethodInfo[] methods;
                try { methods = t.GetMethods(AnyMember); }
                catch (Exception ex) { Guard.Swallow("PatchAudit.methods", ex); continue; }

                bool any = classInfos.Count > 0;
                var patchMethods = new List<MethodInfo>();
                MethodInfo targetProvider = null;
                for (int m = 0; m < methods.Length; m++)
                {
                    var mi = methods[m];
                    if (IsPatchMethod(mi)) patchMethods.Add(mi);
                    if (mi.IsStatic && mi.GetParameters().Length == 0
                        && (mi.Name == "TargetMethod" || mi.Name == "TargetMethods"
                            || HasAttr(mi, typeof(HarmonyTargetMethod)) || HasAttr(mi, typeof(HarmonyTargetMethods))))
                        targetProvider = mi;
                    if (!any && Infos(mi).Count > 0) any = true;
                }
                if (!any) continue;

                if (targetProvider != null)
                {
                    AuditProvider(t, targetProvider, ref ok, missing);
                    continue;
                }

                // One site per distinct (declaring type, name, args): several patch methods on one target count once.
                var seen = new HashSet<string>();
                if (patchMethods.Count == 0) patchMethods.Add(null);
                for (int p = 0; p < patchMethods.Count; p++)
                {
                    var infos = new List<HarmonyMethod>(classInfos);
                    if (patchMethods[p] != null) infos.AddRange(Infos(patchMethods[p]));
                    var merged = Merge(infos);
                    string label = Describe(t, merged);
                    if (!seen.Add(label)) continue;
                    string why = Resolve(merged);
                    if (why == null) ok++;
                    else missing.Add("patch " + label + " - " + why);
                }
            }
        }

        private static void AuditProvider(Type owner, MethodInfo provider, ref int ok, List<string> missing)
        {
            try
            {
                object r = provider.Invoke(null, null);
                var single = r as MethodBase;
                if (single != null) { ok++; return; }
                var many = r as System.Collections.IEnumerable;
                if (many != null)
                {
                    int n = 0;
                    foreach (object o in many)
                    {
                        n++;
                        if (o as MethodBase == null) missing.Add("patch " + owner.Name + " target #" + n + " is null");
                        else ok++;
                    }
                    if (n == 0) missing.Add("patch " + owner.Name + " TargetMethods returned nothing");
                    return;
                }
                missing.Add("patch " + owner.Name + " TargetMethod returned null");
            }
            catch (Exception ex)
            {
                missing.Add("patch " + owner.Name + " TargetMethod threw " + Short(ex));
            }
        }

        private static bool IsPatchMethod(MethodInfo mi)
        {
            if (HasAttr(mi, typeof(HarmonyPrefix)) || HasAttr(mi, typeof(HarmonyPostfix))
                || HasAttr(mi, typeof(HarmonyTranspiler)) || HasAttr(mi, typeof(HarmonyFinalizer)))
                return true;
            string n = mi.Name;
            return n == "Prefix" || n == "Postfix" || n == "Transpiler" || n == "Finalizer";
        }

        private static bool HasAttr(MemberInfo m, Type attr)
        {
            try { return m.GetCustomAttributes(attr, false).Length > 0; }
            catch (Exception ex) { Guard.Swallow("PatchAudit.hasattr", ex); return false; }
        }

        private static List<HarmonyMethod> Infos(MemberInfo m)
        {
            var list = new List<HarmonyMethod>(2);
            object[] attrs = m.GetCustomAttributes(typeof(HarmonyAttribute), false);
            for (int i = 0; i < attrs.Length; i++)
            {
                var a = attrs[i] as HarmonyAttribute;
                if (a != null && a.info != null) list.Add(a.info);
            }
            return list;
        }

        private static Merged Merge(List<HarmonyMethod> infos)
        {
            var r = new Merged();
            for (int i = 0; i < infos.Count; i++)
            {
                var h = infos[i];
                if (h.declaringType != null) r.Declaring = h.declaringType;
                if (h.methodName != null) r.Name = h.methodName;
                if (h.methodType != null) r.Kind = h.methodType;
                if (h.argumentTypes != null) r.Args = h.argumentTypes;
            }
            return r;
        }

        private static string Describe(Type patchClass, Merged m)
        {
            string type = m.Declaring != null ? m.Declaring.Name : "?";
            string kind = m.Kind.HasValue && m.Kind.Value != MethodType.Normal ? " (" + m.Kind.Value + ")" : "";
            string args = "";
            if (m.Args != null)
            {
                var names = new string[m.Args.Length];
                for (int i = 0; i < names.Length; i++) names[i] = m.Args[i] != null ? m.Args[i].Name : "?";
                args = "(" + string.Join(",", names) + ")";
            }
            return type + "." + (m.Name ?? (m.Kind == MethodType.Constructor ? ".ctor" : "?")) + args + kind
                + " [" + patchClass.Name + "]";
        }

        /// <summary>Null when the target resolves, otherwise the reason.</summary>
        private static string Resolve(Merged m)
        {
            if (m.Declaring == null) return "no declaring type";
            try
            {
                MethodBase found = null;
                switch (m.Kind ?? MethodType.Normal)
                {
                case MethodType.Normal:
                    if (string.IsNullOrEmpty(m.Name)) return "no method name";
                    found = AccessTools.DeclaredMethod(m.Declaring, m.Name, m.Args);
                    break;
                case MethodType.Getter:
                    found = AccessTools.DeclaredProperty(m.Declaring, m.Name)?.GetGetMethod(true);
                    break;
                case MethodType.Setter:
                    found = AccessTools.DeclaredProperty(m.Declaring, m.Name)?.GetSetMethod(true);
                    break;
                case MethodType.Constructor:
                    found = AccessTools.DeclaredConstructor(m.Declaring, m.Args);
                    break;
                default:
                    return null; // enumerator / async patterns: Harmony resolves them itself
                }
                return found != null ? null : "not found";
            }
            catch (Exception ex)
            {
                return Short(ex);
            }
        }

        // ------------------------------------------------------------------ reflected lookups

        private static void AuditReflected(ref int ok, List<string> missing)
        {
            for (int i = 0; i < Reflected.Length; i++)
            {
                var r = Reflected[i];
                bool found = false;
                try
                {
                    const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    if (r.Kind == MemberKind.Method)
                        found = r.Owner.GetMethod(r.Name, all) != null;
                    else
                        found = r.Owner.GetField(r.Name, all) != null || r.Owner.GetProperty(r.Name, all) != null;
                }
                catch (Exception ex)
                {
                    Guard.Swallow("PatchAudit.reflected." + r.Name, ex);
                }
                if (found) ok++;
                else missing.Add("reflect " + r.Owner.Name + "." + r.Name + " (" + r.Kind + ", used by " + r.UsedBy + ")");
            }
        }

        /// <summary>
        /// First scene load (Shader.Find resolves against the loaded shader table; at OnInitializeMelon it can be null and
        /// would report a false "missing"). Informational: only "none resolves" is reported, never fails a patch.
        /// </summary>
        public static void RunLate()
        {
            if (!Config.ModConfig.DiagnosticsOn) return; // part of the full audit
            bool found = false;
            try
            {
                for (int i = 0; i < ShaderAnyOf.Length && !found; i++)
                    found = UnityEngine.Shader.Find(ShaderAnyOf[i]) != null;
            }
            catch (Exception ex)
            {
                Guard.Swallow("PatchAudit.shaders", ex);
            }
            if (found)
            {
                ModRuntime.Log?.Msg("[Harmony] audit (late): shaders ok");
                return;
            }
            string item = "shader " + string.Join(" or ", ShaderAnyOf) + " (used by RemoteWeaponEffects laser dot)";
            ModRuntime.Log?.Warning("[Harmony] audit missing: " + item);
            ModRuntime.AddPatchAuditLate(item);
        }

        private static string Short(Exception ex)
        {
            string m = ex.Message ?? "";
            int nl = m.IndexOf('\n');
            if (nl > 0) m = m.Substring(0, nl);
            return ex.GetType().Name + ": " + (m.Length > 120 ? m.Substring(0, 120) : m);
        }
    }
}
