using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// The negative cases of the runner contract and of the role separation.
///
/// <para>Every case checks the EXIT CODE AND THE DIAGNOSTIC CODE rather than the message text: codes
/// are stable identifiers, texts are not. A test hanging on the wording goes red at the next rewording
/// without anything being broken.</para>
/// </summary>
public sealed class ProtocolTests
{
    [Fact]
    public void Lyrvm_refuses_source_files()
    {
        // No silent reach-through to the compiler: a runtime that eats source is no runtime any more, and
        // the separation would be soft again within two weeks.
        var result = Toolchain.Lyrvm("run", Toolchain.Example("hello.lyr"));

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(CliDiagnostics.WrongFileKind, result.Err);
        Assert.Equal("", result.Out);
    }

    [Fact]
    public void Lyrc_has_no_run_command()
    {
        var result = Toolchain.Lyrc("run", Toolchain.Example("hello.lyr"));

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(CliDiagnostics.UnknownCommand, result.Err);
    }

    [Fact]
    public void Driver_has_no_compiler_internals()
    {
        // 'lower', 'parse' and 'tokenize' are compiler internals and live in lyrc. Mirroring them in the
        // driver would be the first step back to the monolith.
        foreach (var command in new[] { "lower", "parse", "tokenize" })
        {
            var result = Toolchain.Lyric(command, Toolchain.Example("hello.lyr"));
            Assert.Equal(ExitCodes.Usage, result.ExitCode);
            Assert.Contains(CliDiagnostics.UnknownCommand, result.Err);
        }
    }

    [Fact]
    public void Missing_file_argument_is_a_usage_error_everywhere()
    {
        Assert.Equal(ExitCodes.Usage, Toolchain.Lyrc("build").ExitCode);
        Assert.Equal(ExitCodes.Usage, Toolchain.Lyrvm("run").ExitCode);
        Assert.Equal(ExitCodes.Usage, Toolchain.Lyric("run").ExitCode);

        Assert.Contains(CliDiagnostics.MissingArgument, Toolchain.Lyric("run").Err);
    }

    [Fact]
    public void Unreadable_module_fails_to_load_rather_than_to_parse()
    {
        var result = Toolchain.Lyrvm("run", Path.Combine(Toolchain.RepositoryRoot, "nope.lyrbc"));

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains(CliDiagnostics.FileUnreadable, result.Err);
    }

    [Fact]
    public void Corrupt_module_is_rejected_at_load_time()
    {
        // Checked at load time rather than at the call, so none of the program output may appear before
        // it fails.
        using var module = Toolchain.Temp(".lyrbc");
        File.WriteAllBytes(module.Path, "not a lyric module at all"u8.ToArray());

        var result = Toolchain.Lyrvm("run", module.Path);

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.NotEqual("", result.Err);
        Assert.Equal("", result.Out);
    }

    [Fact]
    public void Truncated_module_is_rejected_without_crashing()
    {
        using var full = Toolchain.Temp(".lyrbc");
        using var cut = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("arith.lyr"), "-o", full.Path);

        var bytes = File.ReadAllBytes(full.Path);
        File.WriteAllBytes(cut.Path, bytes[..(bytes.Length / 2)]);

        var result = Toolchain.Lyrvm("run", cut.Path);

        // 1 rather than 101 and rather than a .NET stack trace: a broken module is a load error, not a
        // panic and certainly not a crash of the runtime.
        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.DoesNotContain("Unhandled exception", result.Err);
    }

    [Fact]
    public void Missing_foreign_runtime_is_reported_before_compiling()
    {
        var result = Toolchain.Lyric(
            "run", Toolchain.Example("hello.lyr"), "--vm", "/nonexistent/runtime");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains(CliDiagnostics.VmNotFound, result.Err);
    }

    [Fact]
    public void Vm_flag_without_a_path_is_a_usage_error()
    {
        var result = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--vm");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
    }

    [Fact]
    public void Program_arguments_reach_the_program()
    {
        // Point 4 of the contract is redeemed: everything after '--' belongs to the program. A
        // parameterless 'main' ignores it — no error, the same freedom every shell has.
        var ignored = Toolchain.Lyric("run", Toolchain.Example("hello.lyr"), "--", "a", "b");
        Assert.Equal(ExitCodes.Success, ignored.ExitCode);

        // And a 'main(args: string[])' really receives them.
        var received = Toolchain.Lyric("run", Toolchain.Example("greet.lyr"), "--", "a", "b");
        Assert.Equal(2, received.ExitCode);
    }

    [Fact]
    public void A_panic_exits_with_101_and_prints_a_backtrace()
    {
        // The case point 2 of the contract turns on: 101 is distinguishable from 'return 1;', and the
        // backtrace goes to stderr rather than to stdout.
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, """
            fn main(): int {
                var zero = 0;
                return 1 / zero;
            }
            """);

        var result = Toolchain.Lyric("run", source.Path);

        Assert.Equal(ExitCodes.Panic, result.ExitCode);
        Assert.Contains("panic", result.Err);
        Assert.Equal("", result.Out);
    }

    [Fact]
    public void A_panic_looks_the_same_through_a_foreign_runtime()
    {
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, """
            fn main(): int {
                var zero = 0;
                return 1 / zero;
            }
            """);

        var inProcess = Toolchain.Lyric("run", source.Path);
        var foreign = Toolchain.Lyric("run", source.Path, "--vm", Toolchain.LyrvmPath);

        Assert.Equal(ExitCodes.Panic, foreign.ExitCode);
        Assert.Equal(inProcess.ExitCode, foreign.ExitCode);
        Assert.Equal(inProcess.Err, foreign.Err);
    }

    [Fact]
    public void A_compile_error_never_reaches_the_runtime()
    {
        using var source = Toolchain.Temp(".lyr");
        File.WriteAllText(source.Path, "fn main(): int { return \"not an int\"; }");

        var result = Toolchain.Lyric("run", source.Path, "--vm", Toolchain.LyrvmPath);

        // 1 rather than 101: there was nothing to run.
        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains("LYR-SEM", result.Err);
    }

    [Fact]
    public void All_four_binaries_report_the_same_toolchain_version()
    {
        // lyrrepl was missing here for as long as it existed: a list called "all" that counts three does
        // not grow along.
        Assert.StartsWith("lyrc " + ToolchainVersion.Value, Toolchain.Lyrc("--version").Out.Trim());
        Assert.StartsWith("lyrvm " + ToolchainVersion.Value, Toolchain.Lyrvm("--version").Out.Trim());
        Assert.StartsWith("lyric " + ToolchainVersion.Value, Toolchain.Lyric("--version").Out.Trim());
        Assert.StartsWith("lyrrepl " + ToolchainVersion.Value,
            Toolchain.Run(Toolchain.LyrreplPath, ["--version"]).Out.Trim());
    }

    /// <summary>
    /// The version in MSBuild's <c>Version</c> property is the same as in the source.
    ///
    /// <para>It necessarily stands twice — MSBuild cannot read a C# constant. Two places with the same
    /// answer drift; here that shows, rather than a package appearing with a different number than the
    /// tool inside it prints.</para>
    /// </summary>
    [Fact]
    public void The_build_property_and_the_source_constant_agree()
    {
        var props = File.ReadAllText(
            Path.Combine(Toolchain.RepositoryRoot, "Directory.Build.props"));

        Assert.Contains($"<Version>{ToolchainVersion.Value}</Version>", props);
    }

    /// <summary>
    /// Every version printed in the README or the documentation as the OUTPUT OF A TOOL matches what the
    /// tool really prints.
    ///
    /// <para>Without this test the fault it prevents arises while writing the documentation: a plausible
    /// output is written down rather than produced. That is how <c>Lyric 0.9.0</c> got into both files
    /// while the REPL printed <c>0.0.1-dev</c>.</para>
    /// </summary>
    [Theory]
    [InlineData("README.md")]
    public void Printed_versions_in_the_docs_are_the_real_one(string document)
    {
        var text = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, document));

        var printed = System.Text.RegularExpressions.Regex
            .Matches(text, @"^Lyric (\S+) — :help",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        // Without this line the test is green when the regex matches nothing, and would find nothing if
        // someone reworded the example. A test that silently checks nothing is worse than none: it says
        // "checked".
        Assert.NotEmpty(printed);
        Assert.All(printed, version => Assert.Equal(ToolchainVersion.Value, version));
    }
}
