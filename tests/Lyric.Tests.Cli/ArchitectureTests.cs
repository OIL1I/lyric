using System.Text.RegularExpressions;
namespace Lyric.Tests.Cli;

/// <summary>
/// The architecture boundary between the binaries.
///
/// <para>The core statement of the binary split is a DEPENDENCY statement rather than a file
/// statement: <c>lyrvm</c> must contain nothing compiler-side and <c>lyrc</c> nothing runtime-side.
/// Before the cut the first direction was violated — <c>Lyric.Bytecode</c> referenced
/// <c>Lyric.Ir</c>, which referenced <c>Lyric.Sema</c>, so every runtime dragged the whole front-end
/// chain along.</para>
///
/// <para>The OUTPUT DIRECTORY is checked rather than the metadata: the honest question is "what lies
/// next to <c>lyrvm.exe</c> when I ship it". An unused assembly reference would not matter here, a
/// copied DLL does.</para>
///
/// <para>There are exactly three libraries, and that makes the statement sharper rather than weaker:
/// not "these eight files must not be there" but "it is exactly these and no other". A list of
/// prohibitions would have to be maintained for every new project.</para>
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>Everything between source and <c>.lyrbc</c>: lexer, parser, resolver, sema, IR,
    /// bytecode writer, pipeline. A runtime needs none of it: it gets finished bytes.</summary>
    private const string Frontend = "lyrfe.dll";

    /// <summary>The interpreter. A compiler executes nothing.</summary>
    private const string Runtime = "lyrrt.dll";

    /// <summary>Diagnostics, source management and the reading side of the format: the shared contract
    /// both sides need.</summary>
    private const string Shared = "lyrcore.dll";

    [Fact]
    public void Lyrvm_ships_exactly_the_shared_contract_and_the_interpreter()
    {
        // Stated positively, so the test also fails when someone EXTENDS the runtime by a third edge that
        // stands on no list of prohibitions.
        AssertShips("Lyrvm", Shared, Runtime, "lyrvm.dll");
    }

    [Fact]
    public void Lyrvm_ships_nothing_from_the_compiler()
    {
        Assert.DoesNotContain(Frontend, LyricAssemblies("Lyrvm"));
    }

    [Fact]
    public void Lyrc_ships_exactly_the_shared_contract_and_the_frontend()
    {
        AssertShips("Lyrc", Shared, Frontend, "lyrc.dll");
    }

    [Fact]
    public void Lyrc_ships_no_runtime()
    {
        // The other direction. It is less dramatic — a compiler with an interpreter would merely be fat
        // rather than contradictory — but it keeps the roles clean: 'lyrc' executes nothing, so it has
        // nothing to execute with.
        //
        // It is also the reason the reading side of the format lives in lyrcore rather than at the VM:
        // the bytecode writer needs the same opcodes and type tags. Were reading at the runtime, every
        // compiler build would drag the interpreter along and this test would fail.
        Assert.DoesNotContain(Runtime, LyricAssemblies("Lyrc"));
    }

    [Fact]
    public void The_driver_carries_neither_compiler_nor_runtime_of_its_own()
    {
        // The sharpest statement in this file: the driver COMPILES NOTHING and EXECUTES NOTHING. It
        // starts tools, so the tools lie next to it, but their libraries are not its own.
        var shipped = LyricAssemblies("Lyric.Cli");

        Assert.Contains(Shared, shipped);        // exit and diagnostic codes
        Assert.Contains("lyric.dll", shipped);   // itself
        Assert.Contains("lyrc.dll", shipped);    // the tools lie next to it,
        Assert.Contains("lyrvm.dll", shipped);   // because it looks for them there
        Assert.Contains("lyrrepl.dll", shipped);  // the REPL
        Assert.Contains("lyrbuild.dll", shipped); // the build runner
        Assert.Contains("lyrpack.dll", shipped);  // and the packer
    }

    [Fact]
    public void Every_tool_the_driver_dispatches_to_lies_next_to_it()
    {
        // The driver looks for its tools NEXT TO its own exe (Tool.Resolve). If one is missing it reports
        // that only at runtime, and to the user rather than to the developer. This list is the same as
        // Tool.All; when it grows, this test fails until the copy target in Lyric.Cli.csproj follows.
        var shipped = LyricAssemblies("Lyric.Cli");

        foreach (var tool in new[] { "lyrc.dll", "lyrvm.dll", "lyrrepl.dll", "lyrbuild.dll",
                     "lyrpack.dll" })
            Assert.Contains(tool, shipped);
    }

    [Fact]
    public void The_packer_ships_the_shared_contract_and_nothing_else()
    {
        // lyrpack packs modules: it neither compiles (that is lyrc, composed by 'lyric pack') nor
        // executes (that is the STUB it copies). Either library beside it would mean it started
        // doing one of the two.
        AssertShips("Lyrpack", Shared, "lyrpack.dll");
    }

    [Fact]
    public void The_stub_lies_next_to_the_packer_and_next_to_the_driver()
    {
        // The packer resolves its stub at stubs/<rid>/ beside its own executable (after --stub
        // and $LYRIC_STUB), and through the driver it RUNS from the driver's directory — so the
        // stub has to lie in both, or 'lyrpack' works and 'lyric pack' does not.
        var stub = Path.Combine("stubs",
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            OperatingSystem.IsWindows() ? "lyrstub.exe" : "lyrstub");

        Assert.True(File.Exists(Path.Combine(Toolchain.OutputDirectory("Lyrpack"), stub)));
        Assert.True(File.Exists(Path.Combine(Toolchain.OutputDirectory("Lyric.Cli"), stub)));
    }

    [Theory]
    [InlineData("Lyrrepl", "lyrrepl.dll")]
    [InlineData("Lyrbuild", "lyrbuild.dll")]
    public void The_tools_that_need_both_sides_are_these_two(string project, string assembly)
    {
        // The exceptions, stated explicitly rather than left as a gap. A REPL compiles AND executes and
        // the state has to live in between; a build runner runs a script and then compiles what the
        // script collected, which lives in the objects it was handed. 'lyric run' solves neither with
        // two subprocesses.
        //
        // That they have both libraries does not contradict the boundary: the edge separates the
        // LIBRARIES, it does not forbid using both. That they can be combined without softening the cut
        // is the proof that the cut lies cleanly, and the separation for lyrc and lyrvm holds unchanged.
        var shipped = LyricAssemblies(project);

        Assert.Contains(Shared, shipped);
        Assert.Contains(Frontend, shipped);
        Assert.Contains(Runtime, shipped);
        Assert.Contains(assembly, shipped);
    }

    [Fact]
    public void The_driver_has_no_reference_of_its_own_to_frontend_or_runtime()
    {
        // The libraries lyrfe and lyrrt lie in the directory, but because the TOOLS need them rather than
        // the driver. What binds it stands in its project file, and exactly one edge may stand there.
        var project = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "src", "Lyric.Cli",
            "Lyric.Cli.csproj"));

        // ReplaceLineEndings() first: the project files in the repository have mixed line endings, and a
        // split on Environment.NewLine would find nothing on Windows in an LF file — the test would then
        // be silently green, because the list is empty rather than right.
        var referenced = project.ReplaceLineEndings()
            .Split(Environment.NewLine, StringSplitOptions.TrimEntries)
            .Where(line => line.Contains("ProjectReference")
                           && !line.Contains("ReferenceOutputAssembly"))
            .ToArray();

        Assert.Single(referenced);
        Assert.Contains("Lyric.Core", referenced[0]);
    }

    [Fact]
    public void Compiling_binaries_carry_the_stdlib_and_the_runtime_does_not()
    {
        // The stdlib is source and is needed while compiling rather than while executing: a .lyrbc carries
        // its imports symbolically and the runtime binds them through the NativeRegistry. If stdlib/ lies
        // next to lyrvm, either the content rule is wired wrongly or the runtime does more than it should.
        Assert.True(Directory.Exists(Path.Combine(Toolchain.OutputDirectory("Lyrc"), "stdlib")));
        Assert.True(Directory.Exists(Path.Combine(Toolchain.OutputDirectory("Lyric.Cli"), "stdlib")));

        // The language server compiles, so it needs the source too. Without it every import in
        // every open file resolves to an opaque external symbol and the editor shows a clean
        // document that the compiler rejects.
        Assert.True(Directory.Exists(Path.Combine(Toolchain.OutputDirectory("Lyrls"), "stdlib")));

        Assert.False(Directory.Exists(Path.Combine(Toolchain.OutputDirectory("Lyrvm"), "stdlib")));
    }

    [Fact]
    public void The_language_server_ships_the_front_end_and_no_runtime()
    {
        // The same two edges 'lyrc' has, for the same reason: a server answers questions about
        // source and executes nothing. 'lyrls.dll' is the process, 'lyrlsp.dll' the protocol and
        // the analysis.
        AssertShips("Lyrls", Shared, Frontend, "lyrls.dll", "lyrlsp.dll");
    }

    [Fact]
    public void The_language_server_is_not_a_tool_of_the_driver()
    {
        // The driver starts short-lived tools and waits for them. A language server outlives the
        // editor's own startup and owns stdio for its whole lifetime, so the editor launches it
        // directly and 'lyric' knows nothing about it. Stated as a test because the natural next
        // step for someone adding a binary is to register it in Tool.All.
        Assert.DoesNotContain("lyrls.dll", LyricAssemblies("Lyric.Cli"));
    }

    /// <summary>Both sides are sorted: whichever order the file system yields is no statement about the
    /// architecture, and an expectation in ordinal order would be a riddle for the next reader.</summary>
    private static void AssertShips(string project, params string[] expected) =>
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            LyricAssemblies(project));

    /// <summary>
    /// The shipped Lyric assemblies of a binary, alphabetically.
    ///
    /// <para>Everything called <c>lyr</c> is captured, INCLUDING the unknown. During the move to three
    /// assemblies the DLLs of the old project names from an earlier build still lay in <c>bin/</c>; a
    /// comparison against a list of prohibitions would have missed them, an equality comparison fails on
    /// them. That is intended: what lies next to the exe gets shipped, no matter who put it there.</para>
    /// </summary>
    private static string[] LyricAssemblies(string project) =>
        Directory.GetFiles(Toolchain.OutputDirectory(project), "lyr*.dll")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// What the README prints as the shipping list is what <c>build/publish.proj</c> delivers.
    ///
    /// <para><c>lyrembed.dll</c> stood in the README as what a host references and landed in no shipment,
    /// because no binary references it and it was not in the publish list itself — a documented delivery
    /// item outside the artifact.</para>
    ///
    /// <para>The direction that catches the fault is checked: FROM THE README TO THE PUBLISH LIST. The
    /// other way round ("what publish.proj delivers, the README names") would have been true as well and
    /// would have noticed nothing.</para>
    /// </summary>
    [Fact]
    public void Everything_the_readme_ships_is_actually_published()
    {
        var readme = File.ReadAllText(Path.Combine(Toolchain.RepositoryRoot, "README.md"));

        // The fenced block after the shipping promise. The wording says what the TOOLCHAIN
        // contributes rather than what the directory holds: with a runtime identifier a
        // self-contained publish puts the whole .NET runtime beside it, and "nothing else" would
        // then be false.
        var block = Regex.Match(readme, @"What the toolchain itself contributes, and nothing else:\s*```(.*?)```",
            RegexOptions.Singleline);
        Assert.True(block.Success, "README no longer prints what a publish produces");

        var named = Regex.Matches(block.Groups[1].Value, @"\b([a-z.]+\.(?:dll|exe))\b")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(named);

        var produced = PublishedAssemblies();
        foreach (var file in named)
            Assert.Contains(Path.GetFileNameWithoutExtension(file), produced,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The assembly names a publish produces: the projects from <c>publish.proj</c> plus
    /// everything they reference transitively.</summary>
    private static IReadOnlyCollection<string> PublishedAssemblies()
    {
        var root = Toolchain.RepositoryRoot;
        var project = File.ReadAllText(Path.Combine(root, "build", "publish.proj"));

        // MSBuild writes '\' as the separator, on Linux too, where it is an ordinary character in a file
        // name. Without normalization this test finds NOTHING and is silently empty — green locally on
        // Windows, red in CI.
        // Both ways publish.proj delivers a project: the @(Binary) list into the shared root,
        // and a direct MSBuild call for anything with its own directory — the stub is the first
        // of those. The '.csproj' filter keeps the item reference '@(Binary)' out of the queue.
        var queue = new Queue<string>(Regex
            .Matches(project, @"(?:<Binary Include|<MSBuild Projects)=""([^""]+\.csproj)""")
            .Select(m => Path.GetFullPath(
                Path.Combine(root, "build", Normalize(m.Groups[1].Value)))));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (!seen.Add(path) || !File.Exists(path)) continue;

            var text = File.ReadAllText(path);
            var assembly = Regex.Match(text, @"<AssemblyName>([^<]+)</AssemblyName>");
            names.Add(assembly.Success
                ? assembly.Groups[1].Value
                : Path.GetFileNameWithoutExtension(path));

            foreach (Match reference in Regex.Matches(text, @"<ProjectReference Include=""([^""]+)"""))
                queue.Enqueue(Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(path)!, Normalize(reference.Groups[1].Value))));
        }

        // Without this line the test would be silently empty if the path resolution ever breaks again,
        // and "found nothing" would look like "all in order" as long as the README names nothing.
        Assert.NotEmpty(names);
        return names;
    }

    /// <summary>An MSBuild path to a platform path: the project files write Windows separators.
    /// </summary>
    private static string Normalize(string msbuildPath) =>
        msbuildPath.Replace('\\', Path.DirectorySeparatorChar)
                   .Replace('/', Path.DirectorySeparatorChar);
}
