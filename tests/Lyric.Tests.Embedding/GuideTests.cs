using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// Every Lyric snippet in the user guide compiles, and the embedding chapter names the API that
/// exists.
///
/// <para>The snippets are compiled through a <see cref="LangVm"/> configured exactly as the
/// embedding chapter describes, so the chapters that use the host module go through the same path
/// as every other chapter.</para>
/// </summary>
public class GuideTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string GuideDir => Path.Combine(RepoRoot(), "docs", "guide");

    public static TheoryData<string> Chapters()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(GuideDir, "*.md").OrderBy(f => f))
            data.Add(Path.GetFileName(file));
        return data;
    }

    private static string EmbeddingChapter() =>
        File.ReadAllText(Path.Combine(GuideDir, "14-embedding.md"));

    private static LangVm ConfiguredVm()
    {
        var vm = new LangVm(new HostOptions
        {
            StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),

            // The chapter describes an SDK in a native root. It exists here for the same reason the
            // host functions below do: the snippets compile against the VM the chapter describes,
            // so a documented shape that does not work is a red test rather than a wrong page.
            NativeRoots = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["engine"] = Path.Combine(RepoRoot(), "tests", "Lyric.Tests.Embedding", "guide-sdk"),
            },
        });

        vm.RegisterNative("engine.input.keyDown", (long key) => key == 32);

        vm.RegisterType<Player>("Player", t => t
            .Getter("name", (Player p) => p.Name)
            .Getter("health", (Player p) => p.Health)
            .Method("damage", (Player p, long amount) => p.Damage(amount), mutates: true));

        vm.RegisterFunction("playSound", (string _) => { });
        vm.RegisterFunction("random", (long limit) => limit);
        vm.RegisterFunction("hero", () => new Player("test"));
        return vm;
    }

    [Theory]
    [MemberData(nameof(Chapters))]
    public void Every_snippet_in_a_chapter_compiles(string chapter)
    {
        var text = File.ReadAllText(Path.Combine(GuideDir, chapter));
        var snippets = Regex.Matches(text, "```lyr\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(snippets);

        for (var i = 0; i < snippets.Length; i++)
        {
            var vm = ConfiguredVm();
            var index = i;
            var failure = Record.Exception(() => vm.Compile(snippets[index], "guide"));
            Assert.True(failure is null, $"{chapter} snippet {index} does not compile:\n{failure}");
        }
    }

    /// <summary>The guide has a chapter per topic; an empty directory would make the theory above
    /// pass without checking anything.</summary>
    [Fact]
    public void The_guide_has_chapters() =>
        Assert.True(Directory.GetFiles(GuideDir, "*.md").Length >= 10);

    [Theory]
    [InlineData("vm.Compile(File.ReadAllText")]
    [InlineData("builder.Field")]
    [InlineData(".Field(")]
    [InlineData("vm.Call<")]
    [InlineData("new Capabilities")]
    [InlineData("vm.Reload(")]
    public void The_embedding_chapter_does_not_promise_what_does_not_exist(string withdrawn) =>
        Assert.DoesNotContain(withdrawn, EmbeddingChapter(), StringComparison.Ordinal);

    [Theory]
    [InlineData("HostOptions")]
    [InlineData("RegisterFunction")]
    [InlineData("RegisterType")]
    [InlineData("Instantiate")]
    [InlineData("Reload()")]
    [InlineData("import host")]
    [InlineData("Attributes.OnFunctions")]
    [InlineData("Attributes.OnModule")]
    [InlineData("FieldsOf")]
    public void The_embedding_chapter_names_the_api_that_exists(string expected) =>
        Assert.Contains(expected, EmbeddingChapter(), StringComparison.Ordinal);

    /// <summary>The host type the embedding chapter registers, in the same shape.</summary>
    private sealed class Player(string name)
    {
        public string Name { get; } = name;
        public long Health { get; private set; } = 100;
        public void Damage(long amount) => Health -= amount;
    }
}
