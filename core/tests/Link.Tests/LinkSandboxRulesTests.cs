using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// The files in core/src/Link are compiled by s&box as well as by the helper, and s&box refuses
    /// code that touches files, the network, threads, reflection and a good deal else. These tests
    /// read the source and fail if something off-limits creeps in. They are a tripwire, not a copy of
    /// s&box's real checker: the proof is still compiling them inside s&box.
    /// </summary>
    public class LinkSandboxRulesTests
    {
        private static readonly string[] Forbidden =
        {
            "System.IO", "System.Net", "System.Threading", "System.Reflection", "System.Text.Json",
            "System.Runtime.InteropServices", "System.Diagnostics", "unsafe", "stackalloc", "Span<", "Memory<",
            "DllImport", "Activator", "typeof(", "GetType(", "dynamic ", "Task", "async ", "await ",
            "Console.", "Environment.", "Marshal.", "fixed (", "MemoryMarshal", "Unsafe.",
        };

        private static string LinkFolder([CallerFilePath] string thisFile = "")
        {
            // .../core/tests/Link.Tests/LinkSandboxRulesTests.cs  ->  .../core/src/Link
            string core = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile), "..", ".."));
            return Path.Combine(core, "src", "Link");
        }

        private static string CodeOnly(string source)
        {
            // Drop comments so that words like "threads" in an explanation do not count.
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            source = Regex.Replace(source, @"//[^\n]*", " ");
            return source;
        }

        [Fact]
        public void The_link_files_exist()
        {
            string folder = LinkFolder();
            Assert.True(Directory.Exists(folder), "missing " + folder);
            Assert.True(File.Exists(Path.Combine(folder, "LinkDecoder.cs")));
            Assert.True(File.Exists(Path.Combine(folder, "LinkEncoder.cs")));
            Assert.True(File.Exists(Path.Combine(folder, "LinkTypes.cs")));
            Assert.True(File.Exists(Path.Combine(folder, "OpenVrSpace.cs")));
        }

        [Fact]
        public void No_link_file_uses_anything_the_sandbox_refuses()
        {
            foreach (string file in Directory.GetFiles(LinkFolder(), "*.cs", SearchOption.AllDirectories))
            {
                string code = CodeOnly(File.ReadAllText(file));
                foreach (string word in Forbidden)
                {
                    Assert.False(code.Contains(word, StringComparison.Ordinal),
                        Path.GetFileName(file) + " uses \"" + word + "\", which s&box game code may not.");
                }
            }
        }

        [Fact]
        public void Link_files_only_use_the_core_aliases_for_vectors()
        {
            // Inside s&box the bare names Vector3 and Quaternion mean s&box's own types (see Aliases.cs).
            foreach (string file in Directory.GetFiles(LinkFolder(), "*.cs", SearchOption.AllDirectories))
            {
                string code = CodeOnly(File.ReadAllText(file));
                Assert.DoesNotMatch(@"\bVector[234]\b", code);
                Assert.DoesNotMatch(@"\bQuaternion\b", code);
                Assert.DoesNotMatch(@"\bMatrix4x4\b", code);
            }
        }

        [Fact]
        public void The_decoder_reads_numbers_only_with_invariant_Parse()
        {
            string code = CodeOnly(File.ReadAllText(Path.Combine(LinkFolder(), "LinkDecoder.cs")));

            // Every Parse call names the invariant culture.
            MatchCollection parses = Regex.Matches(code, @"\b(float|double|int|long|decimal|Convert)\s*\.\s*\w*Parse\w*\s*\(([^)]*)\)");
            Assert.True(parses.Count >= 3, "expected the decoder to parse numbers");
            foreach (Match m in parses)
            {
                Assert.True(m.Groups[1].Value == "float" || m.Groups[1].Value == "double", "unexpected parser: " + m.Value);
                Assert.Contains("Invariant", m.Groups[2].Value);
            }

            // And it needs nothing beyond these namespaces.
            foreach (Match m in Regex.Matches(code, @"^\s*using\s+([\w.]+)\s*;", RegexOptions.Multiline))
            {
                string ns = m.Groups[1].Value;
                Assert.True(ns == "System" || ns == "System.Collections.Generic" || ns == "System.Globalization",
                    "LinkDecoder.cs uses namespace " + ns);
            }
            Assert.DoesNotContain("StringBuilder", code);
            Assert.DoesNotContain("LinkEncoder", code);
        }
    }
}
