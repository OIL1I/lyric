using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>Sema-Gesamteinstieg: Typprüfung + Flow-Analysen (via TypeChecker) und die
/// strukturellen Regeln. Der Resolver läuft davor (liefert das <see cref="BindingResult"/>).</summary>
public static class Semantics
{
    public static TypeResult Analyze(Compilation compilation, BindingResult binding, DiagnosticEngine de)
    {
        var types = new TypeChecker(compilation, binding, de).Check();
        new SemaRules(compilation, binding, types, de).Run();
        new ExceptionAnalyzer(compilation, binding, types, de).Run(); // throws-Propagation (§9)
        return types;
    }
}
