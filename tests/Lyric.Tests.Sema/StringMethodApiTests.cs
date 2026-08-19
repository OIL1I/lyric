using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// The string method API (v1.15): one name, one form. The methods come with any import of
/// <c>std.string</c>, the free forms warn as deprecated, and an import whose EXTENSIONS are used
/// no longer counts as unused — <c>import std.string as strings;</c> exists exactly for
/// <c>s.trim()</c>.
/// </summary>
public class StringMethodApiTests
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };

        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    [Fact]
    public void The_methods_come_with_an_aliased_module_import()
    {
        var de = Check(
            """
            import std.string as strings;

            fn main(): int {
                return "  hi  ".trim().length();
            }
            """);
        Assert.False(de.HasErrors, string.Join("\n", de.Diagnostics));
        Assert.Empty(de.Diagnostics); // and no unused-import warning: the extensions ARE the use
    }

    [Fact]
    public void An_unused_module_import_still_warns()
    {
        // The counter-check: the extension rule must not swallow the honest case.
        var de = Check(
            """
            import std.string as strings;

            fn main(): int {
                return 0;
            }
            """);
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0072");
    }

    [Fact]
    public void The_free_forms_warn_as_deprecated()
    {
        var de = Check(
            """
            import std.string { trim, length };

            fn main(): int {
                return length(trim("  x  "));
            }
            """);
        Assert.False(de.HasErrors);
        Assert.Equal(2, de.Diagnostics.Count(d => d.Code == "LYR-SEM0076"));
    }

    [Fact]
    public void Concat_and_repeat_stay_undeprecated()
    {
        // They back '+' and '*'; deprecating them would warn on compiler-written calls.
        var de = Check(
            """
            import std.string { concat, repeat };

            fn main(): int {
                let joined = concat("a", "b") + repeat("c", 2);
                return joined.length();
            }
            """);
        Assert.False(de.HasErrors);
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0076");
    }
}
