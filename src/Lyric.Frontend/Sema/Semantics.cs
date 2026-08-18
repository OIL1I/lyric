using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>The overall sema entry point: type checking and the flow analyses through the
/// TypeChecker, plus the structural rules. The resolver runs before it and supplies the
/// <see cref="BindingResult"/>.</summary>
public static class Semantics
{
    /// <param name="singleProgram">Whether this compilation is one executable. The entry contract
    /// allows exactly one 'main' per executable; a workspace compilation holds several programs at
    /// once, and there a second 'main' is another program rather than a duplicate. The shape rule
    /// for 'main' applies either way.</param>
    public static TypeResult Analyze(Compilation compilation, BindingResult binding,
        DiagnosticEngine de, bool singleProgram = true)
    {
        var types = new TypeChecker(compilation, binding, de).Check();
        new SemaRules(compilation, binding, types, de, singleProgram).Run();
        new ExceptionAnalyzer(compilation, binding, types, de).Run(); // throws propagation
        return types;
    }
}
