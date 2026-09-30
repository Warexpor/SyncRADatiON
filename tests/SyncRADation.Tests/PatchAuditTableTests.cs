using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>Source scan: every member the mod looks up by string through reflection must be in Bootstrap/PatchAudit.cs' Reflected table.</summary>
    public class PatchAuditTableTests
    {
        [Fact]
        public void Every_reflected_string_lookup_is_in_the_PatchAudit_table()
        {
            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "SyncRADation.csproj"))) root = Path.GetDirectoryName(root);
            Assert.NotNull(root);

            string audit = File.ReadAllText(Path.Combine(root, "Bootstrap", "PatchAudit.cs"));
            var lookups = new List<string>();
            foreach (string f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length).Replace('\\', '/');
                if (rel.StartsWith("/tests/") || rel.StartsWith("/obj/") || rel.StartsWith("/bin/") || rel.StartsWith("/dist/")
                    || rel.StartsWith("/.claude/") || rel.StartsWith("/.git/") || rel.StartsWith("/packages/") || rel == "/Bootstrap/PatchAudit.cs") continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(f), "(?:GetField|GetMethod|GetProperty|AccessTools\\.(?:Field|Method|Property))\\(\\s*\"(\\w+)\""))
                    lookups.Add(m.Groups[1].Value + " (" + Path.GetFileName(f) + ")");
            }
            Assert.True(lookups.Count >= 6, "scanner found only " + lookups.Count + " reflected lookups: regex out of date?");
            var missing = lookups.Where(l => !audit.Contains("\"" + l.Substring(0, l.IndexOf(' ')) + "\"")).ToList();
            Assert.True(missing.Count == 0, "add to PatchAudit.Reflected: " + string.Join(", ", missing));
        }
    }
}
