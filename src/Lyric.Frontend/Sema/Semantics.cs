using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>The overall sema entry point: type checking and the flow analyses through the
/// TypeChecker, plus the structural rules. The resolver runs before it and supplies the
/// <see cref="BindingResult"/>.</summary>
public static class Semantics
{
    public static TypeResult Analyze(Compilation compilation, BindingResult binding, DiagnosticEngine de)
    {
        var types = new TypeChecker(compilation, binding, de).Check();
        new SemaRules(compilation, binding, types, de).Run();
        new ExceptionAnalyzer(compilation, binding, types, de).Run(); // throws propagation
        return types;
    }
}
