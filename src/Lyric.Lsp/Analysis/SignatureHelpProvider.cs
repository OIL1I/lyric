using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Lsp.Protocol;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// Which parameter the cursor is on, inside which call.
///
/// <para>Off the CURRENT buffer, like completion and for the same reason: the request came from
/// typing <c>(</c> or <c>,</c>, and the model that keystroke invalidated is the one that would
/// answer about the text before it. The editor's paren auto-close keeps the call parseable in the
/// ordinary case; where the text around the cursor does not parse into a call, the answer is
/// null and the popup simply does not open.</para>
///
/// <para>The label is the DECLARED signature, sliced from the source of the declaration itself:
/// parameter names and types exactly as written, which also makes each parameter label a
/// substring of the whole — the property the client's highlight needs. Generics stay as declared;
/// the substitution is private to the type checker, the same limit hover records.</para>
/// </summary>
public static class SignatureHelpProvider
{
    public static SignatureHelp? At(string path, string text, int offset, CompilerOptions options)
    {
        if (offset < 0 || offset > text.Length) return null;

        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, text), options);
        if (result.Model is not { } model) return null;

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        if (!file.IsValid) return null;

        var nodes = NodeFinder.PathAt(model.Entry, file, offset);

        // The innermost call whose ARGUMENT REGION holds the cursor: behind the callee, so the
        // cursor on the callee's own name asks hover's question, not this one.
        CallExpr? call = null;
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i] is CallExpr candidate && offset > candidate.Callee.Span.End)
            {
                call = candidate;
                break;
            }
        }
        if (call is null) return null;

        var symbol = model.Types.RefOf(call.Callee) ?? model.Binding.Resolve(call.Callee);
        if (symbol is not null) symbol = ReferenceProvider.Target(symbol);

        var signature = symbol is FunctionSymbol { Declaration: FunctionDecl declaration }
            ? Declared(result.Sources, declaration)
            : FromType(call, model);
        if (signature is null) return null;

        return new SignatureHelp
        {
            Signatures = [signature],
            ActiveSignature = 0,
            ActiveParameter = ActiveParameter(call, offset),
        };
    }

    /// <summary>The argument the cursor stands in — or the one it is about to start, which is why
    /// a position past every parsed argument counts one further.</summary>
    private static int ActiveParameter(CallExpr call, int offset)
    {
        var active = 0;
        foreach (var argument in call.Arguments)
        {
            if (offset <= argument.Span.End) break;
            active++;
        }
        return active;
    }

    /// <summary>The declaration as written: name, generics, each parameter sliced from its own
    /// span, the return type from its.</summary>
    private static SignatureInformation Declared(SourceManager sources, FunctionDecl declaration)
    {
        var parameters = declaration.Parameters
            .Select(parameter => Flat(sources.Slice(parameter.Span).ToString()))
            .ToArray();

        var generics = declaration.Generics.Length == 0
            ? ""
            : "<" + string.Join(", ",
                declaration.Generics.Select(g => Flat(sources.Slice(g.Span).ToString()))) + ">";

        var label = $"fn {declaration.Name}{generics}({string.Join(", ", parameters)})";
        if (declaration.ReturnType is { } returnType)
            label += $": {Flat(sources.Slice(returnType.Span).ToString())}";

        return new SignatureInformation
        {
            Label = label,
            Parameters = parameters
                .Select(parameter => new ParameterInformation { Label = parameter })
                .ToArray(),
        };
    }

    /// <summary>
    /// A callee that is not a declared function — a lambda in a local, a parameter of function
    /// type. The type knows the shape but not the names, so the label shows the types and no
    /// parameter is highlighted: half an answer honestly, rather than invented names.
    /// </summary>
    private static SignatureInformation? FromType(CallExpr call, SemanticModel model)
    {
        if (model.Types.TypeOf(call.Callee) is not FnType fn) return null;

        var label = $"fn({string.Join(", ", fn.Parameters.Select(TypeFacts.Display))})"
            + $" -> {TypeFacts.Display(fn.Return)}";

        return new SignatureInformation { Label = label, Parameters = [] };
    }

    /// <summary>A declaration may wrap over lines; a popup label should not.</summary>
    private static string Flat(string text) =>
        string.Join(' ', text.Split(['\r', '\n', '\t', ' '],
            StringSplitOptions.RemoveEmptyEntries));
}
