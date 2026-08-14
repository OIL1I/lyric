using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Lsp.Analysis;

/// <summary>What hover found: the text to show and the range it is about.</summary>
public sealed record HoverResult(string Markdown, Span Span);

/// <summary>
/// What the compiler knows about the thing under the cursor.
///
/// <para>The type is rendered with <see cref="TypeFacts.Display"/>, the same function the
/// diagnostics use. A second renderer would be a second answer to "what is this type called", and
/// the two would drift the first time a type gained a form.</para>
///
/// <para>There is deliberately NO documentation in the result. <c>///</c> is a token kind that
/// reaches no AST node, so there is nothing to read; composing a summary from the signature would
/// be the server writing documentation rather than showing it.</para>
/// </summary>
public static class HoverProvider
{
    public static HoverResult? At(SemanticModel model, FileId file, int offset)
    {
        var path = NodeFinder.PathAt(model.Entry, file, offset);

        // From the inside out: the innermost node is the most specific answer, but not every node
        // carries one — a literal knows its type, the argument list around it does not.
        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (Describe(model, path[i]) is { } markdown)
                return new HoverResult(markdown, path[i].Span);
        }

        return null;
    }

    private static string? Describe(SemanticModel model, Node node)
    {
        // Two tables, because they answer for different nodes: the sema binds expressions and the
        // declarations it walks, the resolver binds names in TYPE position.
        var symbol = model.Types.RefOf(node) ?? model.Binding.Resolve(node);

        var type = node is Expr expression ? model.Types.TypeOf(expression) : null;
        if (type is not null && !IsShowable(type)) type = null;

        if (symbol is not null && Signature(model, symbol, type) is { } signature)
            return Code(signature);

        // No symbol, but a type: an operator, a literal, an index expression. Worth showing — "what
        // is this subexpression" is what hover is asked most often.
        return type is null ? null : Code(TypeFacts.Display(type));
    }

    /// <summary>
    /// The declaration line for a symbol, in the language's own syntax where it has one.
    ///
    /// <para><paramref name="type"/> is what the sema gave the node under the cursor, when it was
    /// an expression. It is preferred over anything the declaration says, because at a use site of
    /// a generic it is SUBSTITUTED: the parameter reads <c>int</c> where the declaration says
    /// <c>T</c>.</para>
    ///
    /// <para><c>null</c> when nothing useful can be said, so the caller falls back to the plain
    /// type or keeps looking outwards.</para>
    /// </summary>
    private static string? Signature(SemanticModel model, Symbol symbol, LyrType? type) => symbol switch
    {
        LocalSymbol local =>
            $"{(local.IsMutable ? "var" : "let")} {local.Name}: {TypeFacts.Display(local.Type)}",

        ParameterSymbol parameter =>
            $"{parameter.Name}: {TypeFacts.Display(parameter.Type)}",

        GlobalSymbol global =>
            $"let {global.Name}: {TypeFacts.Display(model.Types.TypeOfGlobal(global))}",

        // The type parameters stay in the signature even when the sema handed over an FnType,
        // because that type is the DECLARED one: at a call site of 'id<T>(v: T)' it still reads
        // 'T'. The substitution the sema performed lives in its own private function, and a second
        // one here would be a second answer to what 'T' became. Showing the declaration in full is
        // true; showing it half-substituted would not be.
        FunctionSymbol function => type is FnType fn
            ? $"fn {function.Name}{Generics(function.Generics)}"
              + $"({string.Join(", ", fn.Parameters.Select(TypeFacts.Display))})"
              + $" -> {TypeFacts.Display(fn.Return)}"
            : $"fn {function.Name}{Generics(function.Generics)}",

        TypeSymbol declared => $"{Keyword(declared.Kind)} {declared.Name}{Generics(declared.Generics)}",

        EnumVariantSymbol variant => Named(variant.Name, type),

        FieldSymbol field => Named(field.Name, type),

        ModuleSymbol module => $"module {module.FullName}",

        GenericParamSymbol generic => generic.Constraints.Length == 0
            ? generic.Name
            : $"{generic.Name} :: [{generic.Constraints.Length}]",

        ImportBindingSymbol import => Signature(model, import.Target, type),

        ExternalSymbol external => $"{external.Name}  // from {string.Join('.', external.SourcePath)}",

        // A name that did not resolve. Saying nothing is right: the diagnostic on the same span
        // already says what is wrong, and a hover would repeat it as though it were a fact about
        // the program.
        ErrorSymbol => null,

        _ => null,
    };

    private static string Named(string name, LyrType? type) =>
        type is null ? name : $"{name}: {TypeFacts.Display(type)}";

    private static string Generics(GenericParamSymbol[] generics) =>
        generics.Length == 0 ? "" : $"<{string.Join(", ", generics.Select(g => g.Name))}>";

    private static string Keyword(TypeSymbolKind kind) => kind switch
    {
        TypeSymbolKind.Struct => "struct",
        TypeSymbolKind.Class => "class",
        TypeSymbolKind.Enum => "enum",
        TypeSymbolKind.Interface => "interface",
        TypeSymbolKind.Alias => "type",
        TypeSymbolKind.Builtin => "builtin",

        // Total over today's kinds: a new one is a word to choose, not a case to guess at.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no keyword for this kind"),
    };

    /// <summary>
    /// Is this type worth putting in front of a user?
    ///
    /// <para><c>ErrorType</c> means "already reported here" and nothing else, so showing it would
    /// answer a question with the fact that answering failed. <c>NullType</c> and <c>NeverType</c>
    /// are internal and not nameable in the language.</para>
    /// </summary>
    private static bool IsShowable(LyrType type) =>
        type is not (Lyric.Sema.ErrorType or NullType or NeverType);

    private static string Code(string signature) => $"```lyric\n{signature}\n```";
}
