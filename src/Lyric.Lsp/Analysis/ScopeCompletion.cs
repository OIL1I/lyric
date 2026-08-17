using Lyric.AST;
using Lyric.Compiler;
using Lyric.Lsp.Protocol;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// The names a position can see: locals, parameters, type parameters, what the module declares and
/// imports, and the builtins.
///
/// <para>The scope is read off the SYNTAX. The sema builds its <see cref="SymbolTable"/> chains while
/// it checks and drops them, so there is no scope to ask at a position — but the shape of a scope is
/// the shape of the tree, and the symbol behind each declaration is in the reference table already:
/// the definite-assignment analysis binds a binding, a parameter and each pattern name to the symbol
/// it declares. <see cref="ReferenceProvider"/> has to filter those entries out; here they are
/// exactly the answer.</para>
///
/// <para><b>The cost is that the scoping rules are known in two places.</b> The sema decides them
/// while checking, and this walk repeats them. Keeping the sema's chains alive instead would be the
/// better answer as soon as there is a second consumer; today there is one.</para>
/// </summary>
public static class ScopeCompletion
{
    /// <summary>
    /// The names visible at <paramref name="offset"/>, innermost first.
    ///
    /// <para>Never <c>null</c>: a position inside a program always sees something, if only the
    /// builtins, and an empty answer would read as "this is not a place for names".</para>
    /// </summary>
    public static IReadOnlyList<CompletionItem> At(
        SemanticModel model, IReadOnlyList<Node> nodes, int offset, ModuleSymbol? from)
    {
        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Inner to outer, so a local shadows the parameter of the same name rather than appearing
        // beside it — which is what the resolver does with its scope chain.
        for (var i = nodes.Count - 1; i >= 0; i--)
            foreach (var symbol in Declared(model, nodes[i], offset))
                Add(model, items, seen, symbol, Detail(nodes[i]));

        if (from is not null)
            foreach (var symbol in from.Members.Symbols)
                Add(model, items, seen, symbol, from.FullName);

        foreach (var symbol in model.Compilation.Builtins.Symbols)
            Add(model, items, seen, symbol, "builtin");

        return items;
    }

    /// <summary>What one level of the tree contributes.</summary>
    private static IEnumerable<Symbol> Declared(SemanticModel model, Node node, int offset)
    {
        switch (node)
        {
            case Block block:
                // Only what stands BEFORE the cursor. A binding is not visible in its own
                // initializer — 'let x = x;' names something else or nothing — and the end of the
                // statement is where it starts to be.
                foreach (var statement in block.Statements)
                {
                    if (statement.Span.End > offset) break;
                    foreach (var symbol in BoundBy(model, statement)) yield return symbol;
                }
                break;

            case FunctionDecl function:
                foreach (var parameter in function.Parameters)
                    if (model.Types.RefOf(parameter) is { } symbol) yield return symbol;

                // Type parameters are declared into a scope and bound to no node, so they are
                // reached through the function's own symbol instead.
                if (NodeFinder.DeclaredSymbol(model, function) is FunctionSymbol declared)
                    foreach (var generic in declared.Generics) yield return generic;
                break;

            case StructDecl or ClassDecl or EnumDecl:
                if (NodeFinder.DeclaredSymbol(model, node) is TypeSymbol type)
                    foreach (var generic in type.Generics) yield return generic;
                break;

            case LambdaExpr lambda:
                foreach (var parameter in lambda.Parameters)
                    if (model.Types.RefOf(parameter) is { } symbol) yield return symbol;
                break;

            // A loop or catch variable belongs to the body, not to the head: in
            // 'for (n in ns)' the iterable is evaluated where 'n' does not yet exist.
            case ForInStmt loop when Covers(loop.Body, offset):
                if (model.Types.RefOf(loop) is { } loopVar) yield return loopVar;
                break;

            case CatchClause catchClause when Covers(catchClause.Body, offset):
                if (model.Types.RefOf(catchClause) is { } caught) yield return caught;
                break;

            case MatchArm arm when Covers(arm.Body, offset) || Covers(arm.Guard, offset):
                foreach (var symbol in PatternNames(model, arm.Pattern)) yield return symbol;
                break;
        }
    }

    /// <summary>The names one statement introduces into the block around it.</summary>
    private static IEnumerable<Symbol> BoundBy(SemanticModel model, Stmt statement)
    {
        switch (statement)
        {
            case BindingStmt binding when model.Types.RefOf(binding) is { } symbol:
                yield return symbol;
                break;

            case DestructuringStmt destructuring:
                foreach (var symbol in PatternNames(model, destructuring.Pattern)) yield return symbol;
                break;
        }
    }

    /// <summary>Every name a pattern binds, nested ones included.</summary>
    private static IEnumerable<Symbol> PatternNames(SemanticModel model, Node? pattern)
    {
        if (pattern is null) yield break;

        var stack = new Stack<Node>();
        stack.Push(pattern);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            // A BindingPattern is a binding or a unit variant, and the sema decides which. The
            // table says so: a variant is bound to its EnumVariantSymbol and is not a name in scope.
            if (node is BindingPattern or FieldPattern
                && model.Types.RefOf(node) is LocalSymbol local)
                yield return local;

            foreach (var child in AstChildren.Of(node)) stack.Push(child);
        }
    }

    private static bool Covers(Node? node, int offset) =>
        node is not null && offset >= node.Span.Start && offset <= node.Span.End;

    /// <summary>Where a name comes from, so two of the same name are told apart in the list.</summary>
    private static string Detail(Node node) => node switch
    {
        FunctionDecl function => function.Name,
        LambdaExpr => "lambda",
        ForInStmt => "loop variable",
        CatchClause => "catch binding",
        MatchArm => "match arm",
        StructDecl or ClassDecl or EnumDecl => "type parameter",
        _ => "local",
    };

    private static void Add(
        SemanticModel model, List<CompletionItem> items, HashSet<string> seen, Symbol symbol, string detail)
    {
        // A module's own error sentinels stand for names that did not resolve; offering them would
        // put a failure into a list of things that exist.
        if (symbol is ErrorSymbol) return;
        if (!seen.Add(symbol.Name)) return;

        items.Add(new CompletionItem
        {
            Label = symbol.Name,
            Kind = KindOf(symbol),
            Detail = detail,
            Documentation = Documentation(model, symbol),
        });
    }

    private static MarkupContent? Documentation(SemanticModel model, Symbol symbol)
    {
        var target = symbol is ImportBindingSymbol import ? import.Target : symbol;
        if (target.Declaration is not { } declaration) return null;
        if (model.Documentation.Of(declaration) is not { } text) return null;
        return new MarkupContent { Value = text };
    }

    private static CompletionItemKind KindOf(Symbol symbol) => symbol switch
    {
        ImportBindingSymbol import => KindOf(import.Target),

        LocalSymbol or ParameterSymbol => CompletionItemKind.Variable,
        FunctionSymbol => CompletionItemKind.Function,
        GlobalSymbol => CompletionItemKind.Constant,
        ModuleSymbol => CompletionItemKind.Module,
        EnumVariantSymbol => CompletionItemKind.EnumMember,

        // A type parameter is a type as far as the reader is concerned; the protocol's own
        // TypeParameter kind exists but reads as a declaration site rather than a usable name.
        GenericParamSymbol => CompletionItemKind.Interface,

        TypeSymbol { Kind: TypeSymbolKind.Struct } => CompletionItemKind.Struct,
        TypeSymbol { Kind: TypeSymbolKind.Enum } => CompletionItemKind.Enum,
        TypeSymbol { Kind: TypeSymbolKind.Interface } => CompletionItemKind.Interface,
        TypeSymbol => CompletionItemKind.Class,

        _ => CompletionItemKind.Variable,
    };
}
