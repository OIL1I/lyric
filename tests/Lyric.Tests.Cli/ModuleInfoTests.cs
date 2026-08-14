using System.Text.Json;
using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// <c>lyrvm info</c> and <c>disasm --function</c>.
///
/// <para>Contains the regression test for the start index fault this command uncovered — see
/// <see cref="Entry_point_index_lives_in_the_combined_index_space"/>.</para>
/// </summary>
public sealed class ModuleInfoTests
{
    /// <summary>
    /// REGRESSION. The Start section puts the entry index into the SHARED space: imports first, then
    /// functions — the same space <c>call</c> uses. The writer used to write the bare FunctionId.
    ///
    /// <para>Nobody noticed, because both readings coincide as soon as a module has no imports:
    /// <c>arith.lyr</c> ran correctly, and the round-trip test wrote and read with the same wrong reading.
    /// It was visible only in a disassembler line nobody read. A specification-faithful third-party
    /// runtime would have jumped into an import for <c>hello.lyr</c> — exactly the damage the format
    /// contract is written against.</para>
    ///
    /// <para>This test therefore checks a program WITH imports. Without them it is worthless.</para>
    /// </summary>
    [Fact]
    public void Entry_point_index_lives_in_the_combined_index_space()
    {
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("hello.lyr"), "-o", module.Path);

        var info = Toolchain.Lyrvm("info", module.Path, "--json");
        using var document = JsonDocument.Parse(info.Out);
        var root = document.RootElement;

        Assert.True(root.GetProperty("counts").GetProperty("imports").GetInt32() > 0,
            "this test is meaningless without imports — both readings coincide at zero");
        Assert.Equal("main.main", root.GetProperty("entry").GetString());
    }

    [Fact]
    public void Entry_point_is_named_in_the_text_output_too()
    {
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("enums.lyr"), "-o", module.Path);

        var info = Toolchain.Lyrvm("info", module.Path);

        Assert.Contains("entry           main.main", info.Out);
    }

    [Fact]
    public void Info_json_is_valid_and_reports_the_format_version()
    {
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("arith.lyr"), "-o", module.Path);

        var info = Toolchain.Lyrvm("info", module.Path, "--json");
        using var document = JsonDocument.Parse(info.Out);
        var format = document.RootElement.GetProperty("format");

        // From 'Format' rather than as a literal: the question is whether 'info' reports what the writer
        // writes, not whether someone updates two numbers at a version bump.
        Assert.Equal(Lyric.Bytecode.Format.VersionMajor, format.GetProperty("major").GetInt32());
        Assert.Equal(Lyric.Bytecode.Format.VersionMinor, format.GetProperty("minor").GetInt32());
    }

    [Fact]
    public void Info_and_disasm_agree_on_the_function_count()
    {
        // Two outputs from the same data source. If they drift apart, it is a reader bug.
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("enums.lyr"), "-o", module.Path);

        using var document = JsonDocument.Parse(Toolchain.Lyrvm("info", module.Path, "--json").Out);
        var fromInfo = document.RootElement.GetProperty("counts").GetProperty("functions").GetInt32();

        var fromDisasm = Toolchain.Lyrvm("disasm", module.Path).Out
            .Split('\n').Count(line => line.StartsWith("fn ", StringComparison.Ordinal));

        Assert.Equal(fromInfo, fromDisasm);
    }

    [Fact]
    public void Info_code_bytes_are_the_sum_of_the_functions()
    {
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("objects.lyr"), "-o", module.Path);

        using var document = JsonDocument.Parse(Toolchain.Lyrvm("info", module.Path, "--json").Out);
        var root = document.RootElement;

        var total = root.GetProperty("counts").GetProperty("codeBytes").GetInt32();
        var summed = root.GetProperty("functions").EnumerateArray()
            .Sum(fn => fn.GetProperty("codeBytes").GetInt32());

        Assert.Equal(summed, total);
    }

    [Fact]
    public void Info_refuses_a_source_file_like_every_other_lyrvm_command()
    {
        var result = Toolchain.Lyrvm("info", Toolchain.Example("hello.lyr"));

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(CliDiagnostics.WrongFileKind, result.Err);
    }

    // ---------------------------------------------------------------- disasm --function

    [Fact]
    public void Disasm_function_keeps_the_module_header_and_drops_the_rest()
    {
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("enums.lyr"), "-o", module.Path);

        var full = Toolchain.Lyrvm("disasm", module.Path).Out;
        var only = Toolchain.Lyrvm("disasm", module.Path, "--function", "main.main").Out;

        // The header stays: the instructions reference strings, types and imports by index.
        Assert.Contains("module (format", only);
        Assert.Contains("fn main.main ", only);
        Assert.DoesNotContain("fn main.Shape.area ", only);
        Assert.True(only.Length < full.Length);
    }

    [Fact]
    public void Disasm_with_an_unknown_function_is_an_error_not_an_empty_dump()
    {
        // Empty output would be the worse answer: it looks like "the function is empty".
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("arith.lyr"), "-o", module.Path);

        var result = Toolchain.Lyrvm("disasm", module.Path, "--function", "nope");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains(CliDiagnostics.UnknownFunction, result.Err);
    }
}
