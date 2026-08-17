using Lyric.AST;
using Lyric.Core;
using Lyric.Lsp.Protocol;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// What a file declares, in the shape an editor draws an outline from.
///
/// <para>A walk over the syntax and nothing else. The binding and type tables are not consulted,
/// which is not only cheap but the property that matters: an outline is read WHILE the file is
/// broken, and one that empties out on a type error goes dark exactly when it is wanted. The test
/// that pins this compiles a program with a type error and still expects the full outline.</para>
///
/// <para>Every entry needs two ranges, and both are already there: a declaration's own span is what
/// the editor reveals, its name span is what the cursor lands on. The protocol requires the second
/// to be enclosed by the first, and that holds by construction rather than by a check here.</para>
///
/// <para>Only the NESTED form is produced. The protocol's flat alternative is deprecated, has no
/// notion of children, and needs a container name per entry; supporting both would be two answer
/// shapes with different semantics rather than one with a flag. A client that does not announce
/// <c>hierarchicalDocumentSymbolSupport</c> is answered with nothing — no current editor is in that
/// position, and an outline that is absent beats one that is malformed.</para>
/// </summary>
public static class DocumentSymbolProvider
{
    /// <summary>
    /// The outline of a module, in SOURCE order.
    ///
    /// <para>Not sorted: the order declarations stand in is information the author put there, and an
    /// editor that wants them alphabetical can sort a list it was given in file order — the other
    /// direction is not available.</para>
    /// </summary>
    public static IReadOnlyList<DocumentSymbol> Of(SourceManager sources, Module module) =>
        Symbols(sources, module.Declarations, inTypeBody: false);

    private static IReadOnlyList<DocumentSymbol> Symbols(
        SourceManager sources, IEnumerable<Node> nodes, bool inTypeBody)
    {
        var symbols = new List<DocumentSymbol>();
        foreach (var node in nodes)
            if (Symbol(sources, node, inTypeBody) is { } symbol)
                symbols.Add(symbol);
        return symbols;
    }

    private static DocumentSymbol? Symbol(SourceManager sources, Node node, bool inTypeBody)
    {
        // An extend block has no name of its own and is the one container that is not an
        // INamedDecl. Handled first, because everything below reads a name.
        if (node is ExtendDecl extend)
            return Build(sources, TargetName(extend), SymbolKind.Namespace, extend.Span,
                extend.Target.Span, Symbols(sources, extend.Methods, inTypeBody: true));

        // What a file OFFERS is what belongs in its outline. An import says what the file needs, a
        // parameter and a local exist inside one declaration and are not offered to anyone — and an
        // outline holding every loop variable is a list nothing can be found in.
        if (node is not INamedDecl named) return null;

        var (kind, children) = Classify(sources, node, inTypeBody);

        return Build(sources, named.Name, kind, named.Span, named.NameSpan, children);
    }

    /// <summary>
    /// The protocol's kind for a declaration, and the declarations nested inside it.
    ///
    /// <para><see cref="SymbolKind"/> is a closed enum written for other languages, so three of
    /// these are a choice rather than a translation. Each says which.</para>
    /// </summary>
    private static (SymbolKind Kind, IReadOnlyList<DocumentSymbol>? Children) Classify(
        SourceManager sources, Node node, bool inTypeBody) => node switch
    {
        StructDecl s => (SymbolKind.Struct, Symbols(sources, s.Members, inTypeBody: true)),
        ClassDecl c => (SymbolKind.Class, Symbols(sources, c.Members, inTypeBody: true)),
        InterfaceDecl i => (SymbolKind.Interface, Symbols(sources, i.Members, inTypeBody: true)),

        EnumDecl e => (SymbolKind.Enum,
            Symbols(sources, e.Variants.Cast<Node>().Concat(e.Methods), inTypeBody: true)),

        EnumVariant => (SymbolKind.EnumMember, null),
        FieldDecl => (SymbolKind.Field, null),

        // A global is 'let' only per the grammar, and a 'static let' is the same binding in a type
        // body. Both are constants in every sense the protocol has a word for.
        GlobalBindingDecl or StaticBindingDecl => (SymbolKind.Constant, null),

        // CHOICE: the enum has no kind for an alias. 'Class' is wrong about what an alias is and
        // right about what it does — it puts a name on a type — and the alternatives say less.
        TypeAliasDecl => (SymbolKind.Class, null),

        // CHOICE: a function in a type body is a method, one at the top level is not.
        //
        // Taken from the WALK and not from the declaration. Nothing on a FunctionDecl distinguishes
        // the two: a body of ';' is a native at the top level and an abstract member inside an
        // interface, and 'mut' is legal on neither exclusively. The caller knows which list it is
        // descending, and that is the only place the answer exists.
        FunctionDecl => (inTypeBody ? SymbolKind.Method : SymbolKind.Function, null),

        // Total over the declarations that reach here: an added one is a kind to choose, not a
        // default to inherit silently.
        _ => throw new ArgumentOutOfRangeException(
            nameof(node), node.GetType().Name, "no symbol kind for this declaration"),
    };

    /// <summary>The type an extend block extends, as the text that stands there.</summary>
    private static string TargetName(ExtendDecl extend) => extend.Target switch
    {
        NamedType named => $"extend {string.Join('.', named.Path)}",
        _ => "extend",
    };

    private static DocumentSymbol Build(SourceManager sources, string name, SymbolKind kind,
        Span span, Span nameSpan, IReadOnlyList<DocumentSymbol>? children) => new()
        {
            Name = name,
            Kind = kind,
            Range = SpanMapper.ToRange(sources, span),
            SelectionRange = SpanMapper.ToRange(sources, nameSpan),

            // Absent rather than empty — a client draws an expander for an empty array.
            Children = children is { Count: > 0 } ? children : null,
        };
}
