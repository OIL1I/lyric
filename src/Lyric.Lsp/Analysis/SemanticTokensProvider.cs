using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// What every name in a file IS, as the wire wants it: the flat integer stream of the protocol.
///
/// <para>The TextMate grammar guesses from spelling; these tokens come from the same two tables
/// every other answer uses, so <c>Point</c> is a type because the compiler resolved it to one, in
/// an annotation, an initializer and an attribute alike. Only names are classified — keywords,
/// literals and comments stay with the grammar, which already knows them lexically and needs no
/// compiler to.</para>
///
/// <para>Full document only. No delta and no range form: a delta is an optimization against a
/// measured cost, and the measured cost of the full form is the walk below over one file — not a
/// number that buys bookkeeping. The verse of the slice rule: nothing speculative.</para>
///
/// <para>A name that classifies to nothing — an unresolved identifier, an error symbol — gets NO
/// token rather than a guessed one: uncolored is visibly "the compiler does not know", which is
/// exactly the truth.</para>
/// </summary>
public static class SemanticTokensProvider
{
    /// <summary>The legend, in wire order. Announced once at initialize; the data below indexes
    /// into it, so the order here is part of the protocol this server speaks.</summary>
    public static readonly IReadOnlyList<string> TokenTypes =
    [
        "namespace",     // 0: a module
        "type",          // 1: struct, class, alias, builtin
        "enum",          // 2
        "interface",     // 3
        "typeParameter", // 4
        "parameter",     // 5
        "variable",      // 6: locals and globals
        "property",      // 7: fields
        "enumMember",    // 8: variants
        "function",      // 9
        "method",        // 10: a function living in a type's member table
    ];

    public static readonly IReadOnlyList<string> TokenModifiers =
    [
        "declaration", // bit 0: this token IS the declaration's name
        "static",      // bit 1: a member without a receiver
        "readonly",    // bit 2: a 'let' binding
    ];

    private const int Declaration = 1 << 0;
    private const int Static = 1 << 1;
    private const int Readonly = 1 << 2;

    public static IReadOnlyList<int> Of(
        SemanticModel model, Module root, FileId file, SourceManager sources)
    {
        var methods = MethodSymbols(model);
        var declared = DeclaredInFile(model, root);

        var tokens = new List<(int Line, int Character, int Length, int Type, int Modifiers)>();

        foreach (var node in Tree(root))
        {
            if (NameSpans.Of(node) is not { } span || span.File != file || span.IsEmpty) continue;

            var symbol = model.Types.RefOf(node)
                ?? model.Binding.Resolve(node)
                ?? (declared.TryGetValue(node, out var own) ? own : null);
            if (symbol is null) continue;

            symbol = ReferenceProvider.Target(symbol);
            if (Classify(symbol, methods) is not { } type) continue;

            var modifiers = Modifiers(symbol, node);
            var start = SpanMapper.ToRange(sources, span).Start;
            tokens.Add((start.Line, start.Character, span.Length, type, modifiers));
        }

        // The import clauses, as in the rename: 'import util { value }' declares a binding rather
        // than using the target, so no table carries it — but the name deserves the target's
        // color, or every import list reads as plain text.
        foreach (var decl in root.Declarations)
        {
            if (decl is not ImportDecl { Clause: ImportSelective selective } import) continue;
            if (ModuleOf(model, root) is not { } module) continue;

            for (var i = 0; i < selective.Names.Length && i < selective.NameSpans.Length; i++)
            {
                if (module.Members.LookupLocal(selective.Names[i]) is not ImportBindingSymbol
                    binding) continue;
                if (!ReferenceEquals(binding.Declaration, import)) continue;

                var target = ReferenceProvider.Target(binding);
                if (Classify(target, methods) is not { } type) continue;

                var span = selective.NameSpans[i];
                if (span.File != file || span.IsEmpty) continue;

                var start = SpanMapper.ToRange(sources, span).Start;
                tokens.Add((start.Line, start.Character, span.Length, type,
                    Modifiers(target, node: null)));
            }
        }

        return Encode(tokens);
    }

    /// <summary>The relative quintuples of the protocol: line delta, character delta (absolute on
    /// a new line), length, type index, modifier bits.</summary>
    private static List<int> Encode(
        List<(int Line, int Character, int Length, int Type, int Modifiers)> tokens)
    {
        tokens.Sort((a, b) => a.Line != b.Line
            ? a.Line.CompareTo(b.Line)
            : a.Character.CompareTo(b.Character));

        var data = new List<int>(tokens.Count * 5);
        var (previousLine, previousCharacter) = (0, 0);

        foreach (var token in tokens)
        {
            // Two classifications at one spot would corrupt the stream's deltas; the first wins.
            if (data.Count > 0 && token.Line == previousLine
                && token.Character == previousCharacter)
                continue;

            var deltaLine = token.Line - previousLine;
            data.Add(deltaLine);
            data.Add(deltaLine == 0 ? token.Character - previousCharacter : token.Character);
            data.Add(token.Length);
            data.Add(token.Type);
            data.Add(token.Modifiers);

            (previousLine, previousCharacter) = (token.Line, token.Character);
        }

        return data;
    }

    private static int? Classify(Symbol symbol, HashSet<Symbol> methods) => symbol switch
    {
        ModuleSymbol => 0,
        TypeSymbol type => type.Kind switch
        {
            TypeSymbolKind.Enum => 2,
            TypeSymbolKind.Interface => 3,
            _ => 1,
        },
        GenericParamSymbol => 4,
        ParameterSymbol => 5,
        LocalSymbol => 6,
        GlobalSymbol => 6,
        FieldSymbol => 7,
        EnumVariantSymbol => 8,
        FunctionSymbol function => methods.Contains(function) ? 10 : 9,

        // An unresolved name or a recovery symbol: no color is the honest color.
        _ => null,
    };

    private static int Modifiers(Symbol symbol, Node? node)
    {
        var modifiers = 0;
        if (node is not null && ReferenceEquals(symbol.Declaration, node)) modifiers |= Declaration;
        if (symbol is FunctionSymbol { IsStatic: true }) modifiers |= Static;
        if (symbol is LocalSymbol { IsMutable: false }) modifiers |= Readonly;
        return modifiers;
    }

    /// <summary>Every function living in a type's member table, across the compilation — the line
    /// between "function" and "method", drawn where the language draws it.</summary>
    private static HashSet<Symbol> MethodSymbols(SemanticModel model)
    {
        var methods = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);

        foreach (var module in model.Compilation.Modules)
            foreach (var member in module.Members.Symbols)
                if (member is TypeSymbol type)
                    foreach (var inner in type.Members.Symbols)
                        if (inner is FunctionSymbol function)
                            methods.Add(function);

        return methods;
    }

    /// <summary>
    /// Declaration node to symbol, for the declarations the tables do NOT bind: a function, a
    /// type, a field, a variant, a generic parameter. Locals and parameters arrive through the
    /// tables — the definite-assignment analysis binds them to themselves.
    /// </summary>
    private static Dictionary<Node, Symbol> DeclaredInFile(SemanticModel model, Module root)
    {
        var map = new Dictionary<Node, Symbol>(ReferenceEqualityComparer.Instance);
        if (ModuleOf(model, root) is not { } module) return map;

        foreach (var symbol in module.Members.Symbols)
        {
            Put(map, symbol);

            if (symbol is TypeSymbol type)
            {
                foreach (var generic in type.Generics) Put(map, generic);
                foreach (var member in type.Members.Symbols)
                {
                    Put(map, member);
                    if (member is FunctionSymbol method)
                        foreach (var generic in method.Generics) Put(map, generic);
                }
            }

            if (symbol is FunctionSymbol function)
                foreach (var generic in function.Generics) Put(map, generic);
        }

        return map;

        static void Put(Dictionary<Node, Symbol> map, Symbol symbol)
        {
            if (symbol.Declaration is { } declaration) map.TryAdd(declaration, symbol);
        }
    }

    private static ModuleSymbol? ModuleOf(SemanticModel model, Module root)
    {
        foreach (var module in model.Compilation.Modules)
            if (ReferenceEquals(model.Compilation.AstOf(module), root))
                return module;

        return null;
    }

    /// <summary>The whole tree, depth first. Order does not matter — the encoder sorts.</summary>
    private static IEnumerable<Node> Tree(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in AstChildren.Of(node)) stack.Push(child);
        }
    }
}
