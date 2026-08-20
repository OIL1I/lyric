using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// The §11 contract of the specification, item 3: the native-import set a runtime must
/// implement is EXACTLY what the shipped <c>stdlib/</c> declares as bodiless functions —
/// binding is symbolic by name.
///
/// <para>A textual ratchet, deliberately: the registry registers through loops and string
/// concatenation, so no static inspection of the C# can enumerate it — but the registry can be
/// ASKED. A bodiless top-level <c>fn</c> in a stdlib file is a native declaration (anywhere
/// else it is <c>LYR-SEM0051</c>), members are indented, so column zero separates the two.
/// The same pattern as the opcode-coverage test against the bytecode chapter.</para>
/// </summary>
public partial class RegistryContractTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [GeneratedRegex(@"^(?:pub )?fn (\w+)\([^)]*\)(?:\s*:\s*[^;{]+)?;\s*$", RegexOptions.Multiline)]
    private static partial Regex NativeDecl();

    [GeneratedRegex(@"^module\s+([\w.]+);", RegexOptions.Multiline)]
    private static partial Regex ModuleHeader();

    [Fact]
    public void Every_declared_native_is_bound_by_the_default_registry()
    {
        var registry = NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null);
        var unbound = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "stdlib"), "*.lyr", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (ModuleHeader().Match(text) is not { Success: true } header) continue;
            var module = header.Groups[1].Value;

            // 'std.build' is HOST-bound: a build script runs inside lyrbuild, which registers
            // the module's natives itself (the same way an embedding host registers its SDK).
            // The default registry deliberately does not carry them — §11 names the exception.
            if (module == "std.build") continue;

            foreach (Match decl in NativeDecl().Matches(text))
            {
                var name = $"{module}.{decl.Groups[1].Value}";
                if (!registry.Binds(name)) unbound.Add(name);
            }
        }

        Assert.True(unbound.Count == 0,
            "stdlib declares natives the default registry does not bind — a program importing "
            + "them dies as LYR-VM0005 at load:\n" + string.Join("\n", unbound));
    }
}
