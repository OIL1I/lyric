using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Lsp.Protocol;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// What can follow a <c>.</c>.
///
/// <para>The text at the cursor does not parse — <c>foo.</c> is not an expression — so the question
/// is asked of a program that does. A synthetic identifier is inserted at the cursor and the buffer
/// compiled again: <c>foo.</c> becomes <c>foo.__lyric_completion__</c>, which parses as a member
/// access whose member does not exist. The unknown member is reported and discarded; what the
/// request needs is the RECEIVER, and that is resolved either way.</para>
///
/// <para>The marker is inserted and nothing else is moved, which covers the cursor sitting mid-name
/// as well: <c>foo.ba</c> becomes <c>foo.ba__lyric_completion__</c>, still a member access on
/// <c>foo</c>. The member name is nonsense in both cases and is never read.</para>
///
/// <para>An error-tolerant parser would answer the same question without the second compile. It
/// would also be a rebuild of the one component 438 tests hang on, to save 7 to 16 milliseconds on a
/// keystroke the user made deliberately.</para>
///
/// <para>Which members a type has is asked of <see cref="MemberFacts"/> rather than assembled here:
/// a list put together in the server would miss the extension methods and interface defaults that
/// the type checker accepts, and a completion list that omits a callable method teaches the reader
/// that it does not exist.</para>
/// </summary>
public static class CompletionProvider
{
    /// <summary>
    /// A name no source contains. It reaches no user: the compile it belongs to publishes nothing,
    /// and no label is built from the member name.
    /// </summary>
    internal const string Marker = "__lyric_completion__";

    /// <summary>
    /// The completions at an offset in a buffer, or <c>null</c> when the position is not a member
    /// access.
    /// </summary>
    /// <param name="text">The buffer as the editor holds it, WITHOUT the marker.</param>
    public static IReadOnlyList<CompletionItem>? At(
        string path, string text, int offset, CompilerOptions options)
    {
        if (offset < 0 || offset > text.Length) return null;

        var marked = text.Insert(offset, Marker);
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, marked), options);
        if (result.Model is not { } model) return null;

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        if (!file.IsValid) return null;

        // The marker's own position: the member name now starts where the cursor was.
        if (MemberAt(model, file, offset) is not { } member) return null;

        var receiver = model.Types.TypeOf(member.Target);
        var from = EnclosingModule(model, file);

        return receiver switch
        {
            NonValueType { Symbol: TypeSymbol type } =>
                Items(model, MemberFacts.OfType(model.Compilation, type, from), type.Name),

            NonValueType { Symbol: ModuleSymbol module } =>
                Items(model, MemberFacts.OfModule(module, from).Select(
                    s => new MemberCandidate(s, MemberSource.Own)), module.FullName),

            _ => InstanceItems(model, receiver, from),
        };
    }

    /// <summary>The member access the marker landed in, found by walking down to the marker's
    /// position and taking the nearest enclosing member expression.</summary>
    private static MemberExpr? MemberAt(SemanticModel model, FileId file, int offset)
    {
        var path = NodeFinder.PathAt(model.Entry, file, offset);
        for (var i = path.Count - 1; i >= 0; i--)
            if (path[i] is MemberExpr member)
                return member;

        return null;
    }

    /// <summary>
    /// The members reachable through a value.
    ///
    /// <para>A named type and a generic instance both carry their definition. A primitive and an
    /// array carry none, and their members are the extensions on the builtin symbol — which is where
    /// the string methods of this standard library live, so leaving them out would empty the list
    /// exactly where it is used most.</para>
    /// </summary>
    private static IReadOnlyList<CompletionItem>? InstanceItems(
        SemanticModel model, LyrType receiver, ModuleSymbol? from)
    {
        var definition = receiver switch
        {
            NamedRef named => named.Symbol,
            GenericInstance generic => generic.Definition,
            PrimitiveType primitive =>
                model.Compilation.Builtins.LookupLocal(TypeFacts.Display(primitive)) as TypeSymbol,
            _ => null,
        };

        if (definition is null) return null;

        return Items(
            model,
            MemberFacts.OfInstance(model.Compilation, model.Binding, definition, from),
            definition.Name);
    }

    private static IReadOnlyList<CompletionItem> Items(
        SemanticModel model, IEnumerable<MemberCandidate> candidates, string owner)
    {
        var items = new List<CompletionItem>();
        foreach (var candidate in candidates)
        {
            items.Add(new CompletionItem
            {
                Label = candidate.Symbol.Name,
                Kind = KindOf(candidate.Symbol),
                Detail = Detail(candidate, owner),
                Documentation = Documentation(model, candidate.Symbol),
            });
        }
        return items;
    }

    /// <summary>Where the member comes from, which is what tells two of the same name apart before
    /// anything is opened.</summary>
    private static string Detail(MemberCandidate candidate, string owner) => candidate.Source switch
    {
        MemberSource.Extension => $"extension on {owner}",
        MemberSource.InterfaceDefault => "interface default",
        _ => owner,
    };

    private static MarkupContent? Documentation(SemanticModel model, Symbol symbol)
    {
        if (symbol.Declaration is not { } declaration) return null;
        if (model.Documentation.Of(declaration) is not { } text) return null;
        return new MarkupContent { Value = text };
    }

    /// <summary>The same closed protocol enum the outline maps into, with the same choices.</summary>
    private static CompletionItemKind KindOf(Symbol symbol) => symbol switch
    {
        FieldSymbol => CompletionItemKind.Field,
        FunctionSymbol { IsStatic: false } => CompletionItemKind.Method,
        FunctionSymbol => CompletionItemKind.Function,
        EnumVariantSymbol => CompletionItemKind.EnumMember,
        GlobalSymbol => CompletionItemKind.Constant,
        ModuleSymbol => CompletionItemKind.Module,

        TypeSymbol { Kind: TypeSymbolKind.Struct } => CompletionItemKind.Struct,
        TypeSymbol { Kind: TypeSymbolKind.Enum } => CompletionItemKind.Enum,
        TypeSymbol { Kind: TypeSymbolKind.Interface } => CompletionItemKind.Interface,
        TypeSymbol => CompletionItemKind.Class,

        _ => CompletionItemKind.Variable,
    };

    /// <summary>The module the edited file is, so extension visibility is judged from where the
    /// reader stands.</summary>
    private static ModuleSymbol? EnclosingModule(SemanticModel model, FileId file)
    {
        foreach (var module in model.Compilation.Modules)
            if (model.Compilation.AstOf(module).Span.File == file)
                return module;

        return null;
    }
}
