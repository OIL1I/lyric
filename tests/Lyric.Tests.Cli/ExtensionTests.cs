using System.Text.Json;

namespace Lyric.Tests.Cli;

/// <summary>
/// The VS Code manifest against what lies next to it.
///
/// <para>An extension manifest is a collection of paths and identifiers pointing at other files, and VS
/// Code checks none of it while loading: a wrong path simply means the colouring is missing. That is
/// the same kind of silent fault as documentation nobody re-reads, and these tests are the same answer
/// to it.</para>
/// </summary>
public sealed class ExtensionTests
{
    private static string Dir => Path.Combine(Toolchain.RepositoryRoot, "tooling", "vscode-lyric");

    private static JsonDocument Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "package.json")));

    [Fact]
    public void Every_path_in_the_manifest_exists()
    {
        // The most common fault in an extension manifest, and the quietest: VS Code loads the extension
        // anyway, it just colours nothing.
        using var manifest = Manifest();
        var contributes = manifest.RootElement.GetProperty("contributes");

        var paths = new List<string> { manifest.RootElement.GetProperty("main").GetString()! };
        paths.AddRange(contributes.GetProperty("languages").EnumerateArray()
            .Select(l => l.GetProperty("configuration").GetString()!));
        paths.AddRange(contributes.GetProperty("grammars").EnumerateArray()
            .Select(g => g.GetProperty("path").GetString()!));

        foreach (var relative in paths)
            Assert.True(File.Exists(Path.Combine(Dir, relative.TrimStart('.', '/'))),
                $"the manifest points at '{relative}', which does not exist");
    }

    [Fact]
    public void The_grammar_and_the_language_agree_on_the_scope()
    {
        // Two places name the same identifier: the manifest wires language and grammar together through
        // 'scopeName'. If they differ, nothing is coloured at all, without a message.
        using var manifest = Manifest();
        using var grammar = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Dir, "syntaxes", "lyric.tmLanguage.json")));

        var contributed = manifest.RootElement.GetProperty("contributes")
            .GetProperty("grammars").EnumerateArray().Single();

        Assert.Equal(grammar.RootElement.GetProperty("scopeName").GetString(),
            contributed.GetProperty("scopeName").GetString());

        Assert.Equal("lyric", contributed.GetProperty("language").GetString());
    }

    [Fact]
    public void The_extension_claims_the_file_extension_the_language_uses()
    {
        using var manifest = Manifest();
        var language = manifest.RootElement.GetProperty("contributes")
            .GetProperty("languages").EnumerateArray().Single();

        Assert.Contains(".lyr", language.GetProperty("extensions").EnumerateArray()
            .Select(e => e.GetString()));
    }

    [Fact]
    public void Every_command_and_keybinding_refers_to_a_declared_command()
    {
        // A key binding or menu entry pointing at a command that does not exist is a menu item that does
        // nothing.
        using var manifest = Manifest();
        var contributes = manifest.RootElement.GetProperty("contributes");

        var declared = contributes.GetProperty("commands").EnumerateArray()
            .Select(c => c.GetProperty("command").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        // 'command' may be missing from the manifest. The entry is then broken anyway, and the message
        // should say so rather than "null is not in the list".
        void MustBeDeclared(JsonElement entry)
        {
            var command = entry.GetProperty("command").GetString();
            Assert.NotNull(command);
            Assert.Contains(command, declared);
        }

        foreach (var binding in contributes.GetProperty("keybindings").EnumerateArray())
            MustBeDeclared(binding);

        foreach (var menu in contributes.GetProperty("menus").EnumerateObject())
            foreach (var item in menu.Value.EnumerateArray())
                MustBeDeclared(item);
    }

    [Fact]
    public void The_run_command_calls_the_driver_and_not_a_tool()
    {
        // The driver is the one command that compiles AND runs. Were the extension to call 'lyrc', the
        // user would get a .lyrbc rather than a run; were it to call 'lyrvm', it would fail on a source
        // file.
        // The CALL LINE is checked rather than the file: the comment beside it names 'lyrc' and 'lyrvm'
        // explicitly to explain why they are not it. A test scanning the whole file trips over exactly
        // that explanation.
        var call = File.ReadAllLines(Path.Combine(Dir, "extension.js"))
            .Single(line => line.Contains("sendText", StringComparison.Ordinal));

        Assert.Contains(" run ", call);
        Assert.DoesNotContain("lyrc", call);
        Assert.DoesNotContain("lyrvm", call);

        // The command is QUOTED and called with an '&' in PowerShell — both together, because each alone
        // is broken. Without the quoting 'C:\Program Files\lyric\lyric.exe' fails at 'C:\Program';
        // without the '&', '"lyric" run x' is a string literal in PowerShell that gets printed rather
        // than executed, so the run simply did not happen.
        //
        // That was the occasion for removing the quoting once. The test stands here so one half is not
        // sacrificed for the other again.
        Assert.Contains("quote(executable)", call, StringComparison.Ordinal);
        Assert.Contains("callPrefix()", call, StringComparison.Ordinal);
    }

    /// <summary>And the <c>&amp;</c> applies to PowerShell only: in cmd.exe and every POSIX shell it would
    /// be a syntax error.</summary>
    [Fact]
    public void The_call_operator_is_limited_to_powershell()
    {
        var code = File.ReadAllText(Path.Combine(Dir, "extension.js"));

        Assert.Contains("vscode.env.shell", code, StringComparison.Ordinal);
        Assert.Contains("pwsh", code, StringComparison.Ordinal);
        Assert.Contains("powershell", code, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsaved_file_is_written_before_running()
    {
        // The compiler reads from disk rather than from the editor buffer. Without the save
        // the previous version runs, and the user looks for the fault in their program rather than in
        // their editor.
        var code = File.ReadAllText(Path.Combine(Dir, "extension.js"));

        Assert.Contains("isDirty", code);
        Assert.Contains("save()", code);
    }
}
