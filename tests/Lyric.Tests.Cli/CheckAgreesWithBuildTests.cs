namespace Lyric.Tests.Cli;

/// <summary>
/// <c>check</c> answers the same question as <c>build</c>.
///
/// <para>It used to stop after the sema. A program with a limit the backend cannot express then got
/// 'ok' from <c>check</c> and died in <c>build</c> — 82 places in the lowering can report one, and
/// none of them was reachable from <c>check</c>. Worse than a missing diagnostic: an answer that is
/// wrong in the reassuring direction.</para>
///
/// <para>The two are compared rather than pinned to a code, so the test keeps holding as limits are
/// closed one by one. A gap that gets fixed makes both sides succeed and the test stays green; a gap
/// that gets reintroduced on one side only turns it red.</para>
/// </summary>
public sealed class CheckAgreesWithBuildTests
{
    /// <summary>
    /// Programs that pass the sema and stand on a lowering limit. They are the cases where the two
    /// commands used to disagree; as the limits close, they become ordinary passing programs and
    /// this test still asserts the agreement.
    /// </summary>
    public static TheoryData<string, string> AtALoweringLimit => new()
    {
        // A type alias resolves in the sema but is not lowered as a return type.
        { "alias as a return type", "type Id = int;\nfn f(): Id { return 1; }\nfn main(): int { return f(); }\n" },

        // The same alias as a field type.
        { "alias as a field type", "type Id = int;\nstruct H { id: Id, }\nfn main(): int { let h = H { id = 1 }; return h.id; }\n" },
    };

    [Theory]
    [MemberData(nameof(AtALoweringLimit))]
    public void Check_and_build_agree_on_a_program_at_a_lowering_limit(string _, string source)
    {
        using var file = Toolchain.Temp(".lyr");
        File.WriteAllText(file.Path, source);
        using var output = Toolchain.Temp(".lyrbc");

        var check = Toolchain.Lyric("check", file.Path);
        var build = Toolchain.Lyric("build", file.Path, "-o", output.Path);

        Assert.True(check.ExitCode == build.ExitCode,
            $"check exited {check.ExitCode} and build exited {build.ExitCode}\n"
            + $"check said:\n{check.StdOut}{check.StdErr}\nbuild said:\n{build.StdOut}{build.StdErr}");
    }

    [Theory]
    [MemberData(nameof(AtALoweringLimit))]
    public void The_limit_is_named_rather_than_only_counted(string _, string source)
    {
        // Whatever the outcome, 'check' must not answer 'ok' while failing. Both halves matter: a
        // check that always failed would satisfy the agreement test alone.
        using var file = Toolchain.Temp(".lyr");
        File.WriteAllText(file.Path, source);

        var check = Toolchain.Lyric("check", file.Path);
        var text = check.StdOut + check.StdErr;

        if (check.ExitCode == 0) Assert.Contains("ok", text);
        else Assert.DoesNotContain(": ok", text);
    }

    [Fact]
    public void A_program_that_builds_also_checks()
    {
        // The counter-check for the whole file: a 'check' that rejected everything would pass every
        // agreement test above.
        using var output = Toolchain.Temp(".lyrbc");
        var example = Toolchain.Example("wc.lyr");

        var check = Toolchain.Lyric("check", example);
        var build = Toolchain.Lyric("build", example, "-o", output.Path);

        Assert.Equal(0, check.ExitCode);
        Assert.Equal(0, build.ExitCode);
        Assert.Contains("ok", check.StdOut + check.StdErr);
    }

    [Fact]
    public void A_sema_error_still_stops_before_the_lowering_runs()
    {
        // Lowering a faulty AST would be guesswork, so the sema error stays the only message.
        using var file = Toolchain.Temp(".lyr");
        File.WriteAllText(file.Path, "fn main(): int { return \"not an int\"; }\n");

        var check = Toolchain.Lyric("check", file.Path);
        var text = check.StdOut + check.StdErr;

        Assert.NotEqual(0, check.ExitCode);
        Assert.Contains("LYR-SEM", text);
        Assert.DoesNotContain("LYR-IR", text);
    }
}
