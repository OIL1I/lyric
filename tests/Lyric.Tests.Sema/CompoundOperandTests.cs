using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// A compound assignment checks its OPERATOR, not only its assignability.
///
/// <para>The bug these pin: <c>p += p</c> on a struct passed the checker with no diagnostic at all.
/// The only rule on a compound was "the right operand must be assignable to the left" — which holds
/// for any value of the target's own type — and the operator itself was never examined. The lowering
/// then emitted an integer <c>add</c> over two references: the <c>s += "x"</c> bug of v1.1.0, one
/// type over, invisible in a release build where the IR verifier does not run.</para>
///
/// <para>The fix checks the synthesized binary <c>target op value</c> through <c>CheckBinary</c> —
/// the SAME rules as the written form, not a second copy of them. Whatever <c>a = a + b</c> says,
/// <c>a += b</c> says too.</para>
/// </summary>
public class CompoundOperandTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
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

    private static void AssertClean(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            "expected this to check clean, but got:\n"
            + string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    // ------------------------------------------------------------------ the hole

    [Fact]
    public void A_struct_plus_assign_is_rejected_like_the_written_form()
    {
        // 'p + p' has been a diagnostic forever; 'p += p' was silence and a miscompile.
        var de = Check("""
            struct P { x: int, }

            fn main(): int {
                var p = P { x = 1 };
                p += p;
                return p.x;
            }
            """);

        Assert.True(de.HasErrors);
    }

    [Fact]
    public void A_bitwise_compound_on_strings_is_rejected()
    {
        // The same hole, another operator family: 's' is assignable to 's', and '&' was never asked.
        var de = Check("""
            fn main(): int {
                var s = "a";
                s &= s;
                return 0;
            }
            """);

        Assert.True(de.HasErrors);
    }

    [Fact]
    public void A_shift_compound_on_a_float_is_rejected()
    {
        var de = Check("""
            fn main(): int {
                var f = 1.5;
                f <<= f;
                return 0;
            }
            """);

        Assert.True(de.HasErrors);
    }

    // ------------------------------------------------------------------ what must keep working

    [Fact]
    public void The_legal_compounds_stay_legal()
    {
        // The v1.1.0 fixes live here: string '+=' and array '+=' lower to their calls and
        // instructions, numeric compounds to their opcodes. The operator check must change none
        // of them.
        AssertClean("""
            fn main(): int {
                var i = 1;
                i += 2;
                i *= 3;
                i %= 5;
                i <<= 1;
                var f = 1.5;
                f += 1;
                var s = "a";
                s += "b";
                var xs = [1, 2];
                xs += [3];
                return i;
            }
            """);
    }

    [Fact]
    public void The_repeat_compound_became_legal_with_the_fix()
    {
        // 's *= 3' used to be rejected by the assignability rule alone (an int is not a string) —
        // an accident the old VM test recorded as "no compound form YET". Typed as the binary it
        // carries, 's * 3' is a string and assignable to 's', and the lowering has handled it since
        // v1.1.0. The reversal is deliberate, not collateral.
        AssertClean("""
            fn main(): int {
                var s = "a";
                s *= 3;
                var xs = [1];
                xs *= 2;
                return 0;
            }
            """);
    }
}
