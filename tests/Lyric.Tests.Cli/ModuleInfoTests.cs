using System.Text.Json;
using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// <c>lyrvm info</c> und <c>disasm --function</c>.
///
/// <para>Enthaelt den Regressionstest fuer den Start-Index-Fehler, den dieses Kommando aufgedeckt
/// hat — siehe <see cref="Entry_point_index_lives_in_the_combined_index_space"/>.</para>
/// </summary>
public sealed class ModuleInfoTests
{
    /// <summary>
    /// <b>Regression.</b> <c>docs/Bytecode.md</c> §Start (Id 7) legt den Einstiegs-Index in den
    /// <b>gemeinsamen</b> Raum: erst Importe, dann Funktionen — derselbe Raum, den <c>call</c>
    /// benutzt. Der Writer schrieb bis 2026-08-05 die nackte FunctionId.
    ///
    /// <para>Es fiel niemandem auf, weil beide Lesarten zusammenfallen, sobald ein Modul keine
    /// Importe hat: <c>arith.lyr</c> lief korrekt, und der Round-Trip-Test schrieb und las mit
    /// derselben falschen Lesart. Sichtbar war es nur an einer Disassembler-Zeile, die niemand
    /// gelesen hat. Eine spec-treue Fremd-Runtime waere bei <c>hello.lyr</c> in einen Import
    /// gesprungen — genau der Schaden, gegen den ADR-013 geschrieben ist.</para>
    ///
    /// <para>Deshalb prueft dieser Test ein Programm <b>mit</b> Importen. Ohne sie ist er wertlos.</para>
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

        // Aus 'Format' und nicht als Literal: die Frage ist, ob 'info' meldet, was der Writer
        // schreibt — nicht, ob jemand beim Bump zwei Zahlen nachzieht. Genau diese Sorte Literal
        // hat in dieser Sitzung schon dreimal einen Test rot gemacht, ohne dass etwas kaputt war.
        Assert.Equal(Lyric.Bytecode.Format.VersionMajor, format.GetProperty("major").GetInt32());
        Assert.Equal(Lyric.Bytecode.Format.VersionMinor, format.GetProperty("minor").GetInt32());
    }

    [Fact]
    public void Info_and_disasm_agree_on_the_function_count()
    {
        // Zwei Ausgaben derselben Datenquelle. Laufen sie auseinander, ist es ein Reader-Bug.
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

        // Der Kopf bleibt: die Instruktionen verweisen per Index auf Strings, Typen und Importe.
        Assert.Contains("module (format", only);
        Assert.Contains("fn main.main ", only);
        Assert.DoesNotContain("fn main.Shape.area ", only);
        Assert.True(only.Length < full.Length);
    }

    [Fact]
    public void Disasm_with_an_unknown_function_is_an_error_not_an_empty_dump()
    {
        // Leere Ausgabe waere die schlechtere Antwort: sie sieht aus wie "die Funktion ist leer".
        using var module = Toolchain.Temp(".lyrbc");
        Toolchain.Lyrc("build", Toolchain.Example("arith.lyr"), "-o", module.Path);

        var result = Toolchain.Lyrvm("disasm", module.Path, "--function", "nope");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains(CliDiagnostics.UnknownFunction, result.Err);
    }
}
