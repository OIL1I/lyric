using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Die <b>monomorphisierten Instanzen</b> generischer Funktionen (Sprache.md §12).
///
/// <para>Pro konkretem Typargument-Tupel eine eigene <see cref="IrFunction"/>: <c>id&lt;int&gt;</c>
/// und <c>id&lt;string&gt;</c> sind zwei Funktionen, nicht eine mit einem Typ-Parameter. Damit
/// bleibt die IR <b>vollstaendig monomorph</b> — Verifier, Bytecode-Format und VM erfahren von
/// Generics nichts, sie sehen nur mehr Funktionen.</para>
///
/// <para><b>Warum monomorph und nicht generisch zur Laufzeit.</b> C# reifiziert Generics und
/// braucht dafuer einen JIT, der pro Instanziierung Code erzeugt; Java erased sie und bezahlt mit
/// Boxing an jeder Grenze. Beides setzt voraus, dass die Runtime Typen kennt — und ein Lyric-Wert
/// traegt kein Typ-Tag (ADR-013). Monomorphisierung ist deshalb nicht eine von drei Optionen,
/// sondern die einzige, die zu dieser VM passt. Rust und C++ machen es aus demselben Grund.</para>
///
/// <para>Der Preis ist Code-Duplikation pro Instanziierung. Er ist sichtbar und begrenzt: eine
/// Instanz je tatsaechlich benutztem Typ-Tupel, nicht je moeglichem.</para>
/// </summary>
internal sealed class InstanceTable
{
    /// <summary>Eine angeforderte, noch nicht gelowerte Instanz.</summary>
    private readonly record struct Pending(
        FunctionDecl Decl, string Name, FunctionId Id, TypeSymbol? Receiver,
        IReadOnlyDictionary<GenericParamSymbol, LyrType> Substitution);

    private readonly List<Pending> _pending = new();

    /// <summary>Was schon angefordert wurde — damit zwei Aufrufe von <c>id(7)</c> dieselbe Instanz
    /// bekommen und nicht zwei gleiche Funktionen entstehen.</summary>
    private readonly Dictionary<string, FunctionId> _byKey = new(StringComparer.Ordinal);

    /// <summary>Bis wohin schon gelowert wurde. Die Tabelle wird MEHRFACH geleert — eine
    /// Instanz kann ein Lambda anfordern, ein Lambda eine Instanz —, und ohne diese Marke
    /// entstuende bei jedem Durchgang alles noch einmal.</summary>
    private int _lowered;

    private readonly FunctionIds _ids;

    public InstanceTable(FunctionIds ids) => _ids = ids;

    public bool IsEmpty => _pending.Count == 0;

    /// <summary>
    /// Fordert eine Instanz an und liefert ihre Id — beim ersten Mal eine neue, danach dieselbe.
    ///
    /// <para>Die Id steht sofort fest, obwohl der Rumpf noch nicht gelowert ist. Genau das macht
    /// Rekursion moeglich: <c>fn depth&lt;T&gt;(n: int): int { return depth&lt;T&gt;(n - 1); }</c>
    /// fordert sich selbst an und findet die eigene Id schon vor.</para>
    /// </summary>
    public FunctionId Request(FunctionSymbol symbol, FunctionDecl decl, string baseName,
        TypeSymbol? receiver, IReadOnlyList<LyrType> typeArguments, TypeTable typeTable,
        Core.Span span)
    {
        if (symbol.Generics.Length != typeArguments.Count)
            throw new UnsupportedConstructException(
                $"call to '{baseName}' supplies {typeArguments.Count} type argument(s), "
                + $"but it declares {symbol.Generics.Length}", span);

        // Der Name IST der Schluessel: er enthaelt die Typargumente, ist damit eindeutig, und ein
        // Mensch kann in einer Disassembly ablesen, welche Instanz er vor sich hat.
        var name = $"{baseName}<{string.Join(", ", typeArguments.Select(TypeFacts.Display))}>";
        if (_byKey.TryGetValue(name, out var existing)) return existing;

        // Ein noch offener Typ-Parameter heisst, dass die Inferenz an der Aufrufstelle nicht
        // durchgekommen ist — dann gibt es keine Instanz, die man bauen koennte.
        for (var i = 0; i < typeArguments.Count; i++)
            if (typeArguments[i] is TypeParamType or Sema.ErrorType)
                throw new UnsupportedConstructException(
                    $"call to '{baseName}': type argument {i} is not concrete "
                    + $"('{TypeFacts.Display(typeArguments[i])}')", span);

        var substitution = new Dictionary<GenericParamSymbol, LyrType>(
            ReferenceEqualityComparer.Instance);
        for (var i = 0; i < symbol.Generics.Length; i++)
            substitution[symbol.Generics[i]] = typeArguments[i];

        var id = _ids.Next();
        _byKey[name] = id;
        _pending.Add(new Pending(decl, name, id, receiver, substitution));
        return id;
    }

    /// <summary>
    /// Lowert alle angeforderten Instanzen — <b>als Worklist</b>, weil eine Instanz beim Lowern
    /// weitere anfordern kann: <c>id&lt;T&gt;</c> ruft <c>wrap&lt;T&gt;</c>, und erst hier steht
    /// fest, welches <c>T</c> gemeint war.
    /// </summary>
    public List<(FunctionId Id, IrFunction Function)> LowerAll(TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, GlobalTable globals, LambdaTable lambdas)
    {
        var lowered = new List<(FunctionId, IrFunction)>();

        for (; _lowered < _pending.Count; _lowered++)
        {
            var p = _pending[_lowered];
            lowered.Add((p.Id, new FunctionLowerer(p.Decl, p.Name, types, functions, imports, typeTable,
                p.Substitution, globals, lambdas, this, p.Receiver).Run()));
        }

        return lowered;
    }
}
