using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>
    /// Source scan: every mutable static in the mod (a non-readonly static field or settable static auto-property, or a
    /// readonly static collection / sized buffer) must either belong to a class that Bootstrap/SessionResetRegistrations.cs
    /// names (a registered reset, or an "also clears" comment on one), or carry a "// persistent: &lt;reason&gt;" line in the
    /// comment / attribute block directly above the declaration. New session state therefore cannot leak between
    /// sessions, wipe reloads or scenes without someone deciding which it is.
    /// Not counted: const, readonly scalars / references, readonly arrays initialized from a literal list (lookup tables),
    /// get-only or computed properties, events, methods.
    /// </summary>
    public class StaticStateGuardTests
    {
        static readonly Regex CollectionType = new Regex(
            @"\b(Dictionary|HashSet|List|Queue|Stack|LinkedList|SortedSet|SortedDictionary|SortedList|Concurrent\w+|StringBuilder|IList|IDictionary|ICollection|ISet)\b");
        static readonly Regex TypeDecl = new Regex(@"\b(class|struct|interface|enum|record)\s+(\w+)");
        static readonly Regex Persistent = new Regex(@"//\s*persistent:\s*\S");
        static readonly Regex LiteralArrayInit = new Regex(@"^=\s*(\{|new\s*[\w.]*\s*\[\s*\]\s*\{|new\s*\[\s*\]\s*\{)");

        internal sealed class Hit
        {
            public string File;
            public int Line;
            public string Class;
            public string Text;
            public override string ToString() => File + ":" + Line + " [" + Class + "] " + Text;
        }

        static string Root()
        {
            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "SyncRADation.csproj"))) root = Path.GetDirectoryName(root);
            return root;
        }

        static IEnumerable<string> ModSources(string root)
        {
            foreach (string f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length).Replace('\\', '/');
                if (rel.StartsWith("/tests/") || rel.StartsWith("/obj/") || rel.StartsWith("/bin/") || rel.StartsWith("/dist/")
                    || rel.StartsWith("/.claude/") || rel.StartsWith("/.git/") || rel.StartsWith("/packages/")) continue;
                yield return f;
            }
        }

        /// <summary>Code of one line with comments, string and char literals blanked (block-comment state carried across lines).</summary>
        static string Code(string line, ref bool inBlock)
        {
            var sb = new StringBuilder(line.Length);
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inBlock)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { inBlock = false; i++; }
                    continue;
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlock = true; i++; continue; }
                if (c == '"')
                {
                    bool verbatim = i > 0 && (line[i - 1] == '@' || (line[i - 1] == '$' && i > 1 && line[i - 2] == '@'));
                    i++;
                    while (i < line.Length)
                    {
                        if (!verbatim && line[i] == '\\') { i += 2; continue; }
                        if (line[i] == '"')
                        {
                            if (verbatim && i + 1 < line.Length && line[i + 1] == '"') { i += 2; continue; }
                            break;
                        }
                        i++;
                    }
                    sb.Append("\"\"");
                    continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < line.Length && line[i] != '\'')
                    {
                        if (line[i] == '\\') i++;
                        i++;
                    }
                    sb.Append("' '");
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        internal static List<Hit> Scan(string root, out int total)
        {
            string reg = File.ReadAllText(Path.Combine(root, "Bootstrap", "SessionResetRegistrations.cs"));
            var hits = new List<Hit>();
            total = 0;
            foreach (string f in ModSources(root))
            {
                string[] lines = File.ReadAllLines(f);
                var code = new string[lines.Length];
                bool inBlock = false;
                for (int i = 0; i < lines.Length; i++) code[i] = Code(lines[i], ref inBlock);

                var stack = new List<KeyValuePair<string, int>>(); // class name, brace depth outside its body
                int depth = 0;
                string pending = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = code[i];
                    // A type declared on an earlier line opens at the first brace of this one.
                    var decls = TypeDecl.Matches(s);

                    if (stack.Count > 0 && depth == stack[stack.Count - 1].Value + 1
                        && Regex.IsMatch(s, @"\bstatic\b") && !Regex.IsMatch(s, @"\b(const|class|struct|event|using|delegate|operator|implicit|explicit)\b"))
                    {
                        if (IsMutableStatic(s, code, i))
                        {
                            total++;
                            if (!Annotated(lines, i) && !Registered(stack, reg))
                                hits.Add(new Hit
                                {
                                    File = f.Substring(root.Length + 1).Replace('\\', '/'),
                                    Line = i + 1,
                                    Class = stack[stack.Count - 1].Key,
                                    Text = lines[i].Trim()
                                });
                        }
                    }

                    int nextDecl = 0;
                    for (int k = 0; k < s.Length; k++)
                    {
                        while (nextDecl < decls.Count && decls[nextDecl].Index <= k)
                            pending = decls[nextDecl++].Groups[2].Value;
                        char ch = s[k];
                        if (ch == '{')
                        {
                            if (pending != null) { stack.Add(new KeyValuePair<string, int>(pending, depth)); pending = null; }
                            depth++;
                        }
                        else if (ch == '}')
                        {
                            depth--;
                            if (stack.Count > 0 && stack[stack.Count - 1].Value == depth) stack.RemoveAt(stack.Count - 1);
                        }
                    }
                    while (nextDecl < decls.Count) pending = decls[nextDecl++].Groups[2].Value;
                    // The member scan works per line: a static member on the same line as its type's opening brace would be
                    // invisible to it, so that layout is rejected outright.
                    if (decls.Count > 0)
                    {
                        int brace = s.IndexOf('{', decls[decls.Count - 1].Index);
                        if (brace >= 0 && Regex.IsMatch(Regex.Replace(s.Substring(brace), @"\bstatic\s+(class|struct)\b", ""), @"\bstatic\b"))
                            hits.Add(new Hit
                            {
                                File = f.Substring(root.Length + 1).Replace('\\', '/'),
                                Line = i + 1,
                                Class = decls[decls.Count - 1].Groups[2].Value,
                                Text = "static member on the type's brace line (put it on its own line): " + lines[i].Trim()
                            });
                    }
                    if (pending != null && s.TrimEnd().EndsWith(";")) pending = null; // positional record / forward declaration
                }
            }
            return hits;
        }

        static bool IsMutableStatic(string s, string[] code, int i)
        {
            var term = Regex.Match(s, "=|;|\\{");
            string head = term.Success ? s.Substring(0, term.Index) : s;
            string rest = term.Success ? s.Substring(term.Index).Trim() : "";
            if (IsMethodHead(head)) return false;                       // method / ctor / local function
            if (rest.StartsWith("=>")) return false;                    // computed property
            if (!term.Success)
            {
                // Declaration continues on the next code line: '{' = property / accessor body, '=' = initializer.
                for (int j = i + 1; j < code.Length; j++)
                {
                    string n = code[j].Trim();
                    if (n.Length == 0) continue;
                    if (n.StartsWith("=>")) return false;
                    if (n.StartsWith("{")) return Regex.IsMatch(n, @"\bset\s*;");
                    rest = n;
                    break;
                }
            }
            else if (rest.StartsWith("{"))
            {
                return Regex.IsMatch(rest, @"\bset\s*;");                // auto-property with a setter
            }
            if (head.Trim().Length == 0) return false;
            if (rest == "=")
            {
                // "X =" with the initializer on the next code line.
                for (int j = i + 1; j < code.Length; j++)
                {
                    string n = code[j].Trim();
                    if (n.Length == 0) continue;
                    rest = "= " + n;
                    break;
                }
            }
            if (!Regex.IsMatch(head, @"\breadonly\b")) return true;
            if (CollectionType.IsMatch(head)) return true;
            if (head.Contains("[]")) return !LiteralArrayInit.IsMatch(rest);
            return false;
        }

        static readonly HashSet<string> Modifiers = new HashSet<string>
        {
            "static", "readonly", "public", "private", "internal", "protected", "volatile", "new", "unsafe", "extern"
        };

        /// <summary>A '(' right after a member name is a method; after a modifier or inside generics it is a tuple type.</summary>
        static bool IsMethodHead(string head)
        {
            var sb = new StringBuilder(head.Length);
            int angle = 0;
            foreach (char c in head)
            {
                if (c == '<') { angle++; continue; }
                if (c == '>') { if (angle > 0) angle--; continue; }
                if (angle == 0) sb.Append(c);
            }
            string h = sb.ToString();
            int p = h.IndexOf('(');
            if (p < 0) return false;
            var m = Regex.Match(h.Substring(0, p), @"(\w+)\s*$");
            return m.Success && !Modifiers.Contains(m.Groups[1].Value);
        }

        static bool Annotated(string[] lines, int i)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                string t = lines[j].Trim();
                if (!(t.StartsWith("//") || t.StartsWith("["))) break;
                if (Persistent.IsMatch(t)) return true;
            }
            return Persistent.IsMatch(lines[i]);
        }

        static bool Registered(List<KeyValuePair<string, int>> stack, string reg)
        {
            for (int k = 0; k < stack.Count; k++)
            {
                if (Regex.IsMatch(reg, @"\b" + Regex.Escape(stack[k].Key) + @"\b")) return true;
            }
            return false;
        }

        [Fact]
        public void Every_mutable_static_is_reset_or_marked_persistent()
        {
            string root = Root();
            Assert.NotNull(root);
            int total;
            var hits = Scan(root, out total);
            // Sanity floor for the scanner (the mod has hundreds of static fields).
            Assert.True(total >= 200, "scanner found only " + total + " mutable statics: parser out of date?");
            Assert.True(hits.Count == 0,
                hits.Count + " mutable static(s) neither reset through Bootstrap/SessionResetRegistrations.cs nor marked"
                + " '// persistent: <reason>' on the line above:\n  " + string.Join("\n  ", hits));
        }

        [Fact]
        public void Scanner_classifies_declarations()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sr_static_scan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "Bootstrap"));
            try
            {
                File.WriteAllText(Path.Combine(dir, "SyncRADation.csproj"), "");
                File.WriteAllText(Path.Combine(dir, "Bootstrap", "SessionResetRegistrations.cs"), "// Registered.Reset\n");
                File.WriteAllText(Path.Combine(dir, "Sample.cs"), string.Join("\n", new[]
                {
                    "namespace X {",
                    "static class Unreg {",
                    "  static int _a;",                                                  // hit
                    "  static readonly List<int> _b = new List<int>();",                 // hit
                    "  static readonly int[] _buf = new int[4];",                        // hit
                    "  static readonly string[] Table = { \"a\", \"{\" };",              // table: no
                    "  static readonly int _c = 1;",                                     // no
                    "  const int K = 2;",                                                // no
                    "  public static int P { get; private set; }",                       // hit
                    "  public static int G => _a;",                                      // no
                    "  public static int Q",                                             // computed: no
                    "  {",
                    "    get { return 1; }",
                    "  }",
                    "  static void M() { int x = 0; }",                                  // no
                    "  static List<T> Gen<T>(T a) { return null; }",                     // no
                    "  static readonly Dictionary<(int, int), bool> _tuple = new Dictionary<(int, int), bool>();", // hit
                    "  static (int, int) _pair;",                                        // hit
                    "  static readonly HashSet<int> _multi",                             // hit
                    "    = new HashSet<int>();",
                    "  // persistent: per-process cache",
                    "  static int _ok;",                                                 // annotated: no
                    "  /// <summary>doc</summary>",
                    "  // persistent: warn-once",
                    "  [System.NonSerialized]",
                    "  static bool _ok2;",                                               // annotated: no
                    "  static string _s = \"}\";",                                       // hit (brace inside a string)
                    "  static readonly string[] Table2 =",                               // table on the next line: no
                    "  {",
                    "    \"x\"",
                    "  };",
                    "}",
                    "static class Registered {",
                    "  static int _r;",                                                  // registered: no
                    "}",
                    "static class Outer { static class Inner {",
                    "  static int _i;",                                                  // hit
                    "} }",
                    "static class OneLine { static int _hidden; }",                      // layout hit
                    "}"
                }));
                int total;
                var hits = Scan(dir, out total);
                var names = hits.Select(h => h.Text).ToList();
                Assert.True(hits.Count == 10, string.Join("\n", names));
                Assert.Contains(names, n => n.Contains("_tuple"));
                Assert.Contains(names, n => n.Contains("_pair"));
                Assert.Contains(hits, h => h.Class == "OneLine" && h.Text.StartsWith("static member on the type's brace line"));
                Assert.Contains(names, n => n.Contains("_a;"));
                Assert.Contains(names, n => n.Contains("_b ="));
                Assert.Contains(names, n => n.Contains("_buf"));
                Assert.Contains(names, n => n.Contains(" P "));
                Assert.Contains(names, n => n.Contains("_multi"));
                Assert.Contains(names, n => n.Contains("_s ="));
                Assert.Contains(hits, h => h.Text.Contains("_i;") && h.Class == "Inner");
                Assert.Equal(12, total);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
