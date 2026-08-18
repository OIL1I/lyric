using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Lexing;
using Lyric.Resolver;

namespace Lyric.Lsp.Analysis;

/// <summary>What the editor selects before asking for the new name.</summary>
public sealed record RenameRange(Span Span, string Placeholder);

/// <summary>
/// Renaming a symbol: every place its name stands in the text, or the reason there is none.
///
/// <para>The edit set is the reference sites of ONE compilation, which is why this arrived after
/// the workspace: a rename over a buffer-scoped compilation silently misses the files that import
/// the buffer, and a rename that misses a site does not fail — it corrupts. Outside a project the
/// same danger remains in miniature, so there a rename is allowed only while every edit stays in
/// the requesting file.</para>
///
/// <para>Deliberately NOT built: collision checking. Whether the new name already means something
/// in some scope is exactly what the compiler decides, and it decides it milliseconds after the
/// edit lands, with the diagnostics pointing at the collision. A second scope simulation here
/// would be the second answer to the same question — the thing this project does not do.</para>
///
/// <para>Every refusal carries its reason. A rename that silently does nothing reads as a broken
/// editor; one that says "declared outside this project" reads as a rule.</para>
/// </summary>
public static class RenameProvider
{
    /// <summary>
    /// The range an editor should select, or the reason it must not offer a rename here.
    ///
    /// <para>The same gate the rename itself runs — anything else lets the user type a new name
    /// first and refuses after, which is the worse of the two moments to say no.</para>
    /// </summary>
    public static (RenameRange? Range, string? Refusal) Prepare(
        SemanticModel model, Module root, FileId file, int offset, bool projectWide)
    {
        var (edits, range, refusal) = Collect(model, root, file, offset, projectWide);
        return edits is null ? (null, refusal) : (range, null);
    }

    /// <summary>
    /// Every span to replace with the new name, or the reason there is none. The declaration's
    /// name is always among them — a rename that leaves the declaration is a different feature.
    /// </summary>
    public static (IReadOnlyList<ReferenceSite>? Edits, string? Refusal) Rename(
        SemanticModel model, Module root, FileId file, int offset, string newName, bool projectWide)
    {
        if (!IsIdentifier(newName))
            return (null, $"'{newName}' is not a legal identifier");

        var (edits, _, refusal) = Collect(model, root, file, offset, projectWide);
        return (edits, refusal);
    }

    private static (IReadOnlyList<ReferenceSite>? Edits, RenameRange? Range, string? Refusal)
        Collect(SemanticModel model, Module root, FileId file, int offset, bool projectWide)
    {
        if (ReferenceProvider.SymbolNodeAt(model, root, file, offset) is not { } found)
            return Refuse("there is nothing to rename here");

        var (symbol, node) = found;

        if (symbol is ModuleSymbol)
            return Refuse("a module is named by its file; rename the file");
        if (symbol.Declaration is not { } declaration)
            return Refuse($"'{symbol.Name}' is built into the language");
        if (!declaration.Span.File.IsValid)
            return Refuse($"'{symbol.Name}' has no source text to edit");

        var declaringModule = ModuleOf(model, declaration.Span.File);
        if (declaringModule is null || model.Compilation.IsNative(declaringModule))
            return Refuse($"'{symbol.Name}' is declared outside this project");

        if (declaration is not INamedDecl named)
            return Refuse($"renaming this form of declaration is not supported");

        var edits = new List<ReferenceSite> { new(named.NameSpan.File, named.NameSpan) };

        var visited = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var (use, bound) in model.Types.AllReferences.Concat(model.Binding.All))
        {
            if (!ReferenceEquals(ReferenceProvider.Target(bound), symbol)) continue;
            if (ReferenceEquals(symbol.Declaration, use)) continue;
            if (!visited.Add(use)) continue;

            var span = NameSpans.Of(use);
            if (span is null)
            {
                // Invalid recorded spans mark what the compiler synthesized — an operator use, a
                // built-in signature. The name stands in no text there, so there is nothing to
                // edit and nothing goes missing.
                if (use is MemberExpr or NamedType) continue;

                // An unknown form is different: the name IS in the text and this code cannot say
                // where. An edit set with a hole corrupts the program, so the whole rename is
                // refused — loudly, so the missing form gets built rather than worked around.
                return Refuse($"a use of '{symbol.Name}' has no recorded name position");
            }

            edits.Add(new ReferenceSite(span.Value.File, span.Value));
        }

        // The import clauses. 'import util { value }' declares a binding rather than using the
        // target, so no table above carries it — but the NAME stands in the text and must follow
        // the rename, or every importer breaks.
        foreach (var module in model.Compilation.Modules)
        {
            if (module.Members.LookupLocal(symbol.Name) is not ImportBindingSymbol binding) continue;
            if (!ReferenceEquals(ReferenceProvider.Target(binding), symbol)) continue;
            if (binding.Declaration is not ImportDecl { Clause: ImportSelective selective }) continue;

            var index = Array.IndexOf(selective.Names, symbol.Name);
            if (index >= 0 && index < selective.NameSpans.Length)
            {
                var span = selective.NameSpans[index];
                if (span.File.IsValid) edits.Add(new ReferenceSite(span.File, span));
            }
        }

        // A node can contribute the same span through both tables, and the declaration search can
        // meet a site again; the answer is a set of PLACES.
        var deduplicated = edits
            .DistinctBy(site => (site.File, site.Span.Start, site.Span.End))
            .ToList();

        // Outside a project the compilation is rooted at one buffer, and files this compilation
        // cannot see may import the edited one. Within one file nothing outside can break.
        if (!projectWide)
        {
            foreach (var site in deduplicated)
                if (site.File != file)
                    return Refuse(
                        "the file is not part of a project; a rename that would touch other files "
                        + "needs a lyric.json");
        }

        var range = new RenameRange(NameSpans.Of(node) ?? node.Span, symbol.Name);
        return (deduplicated, range, null);

        static (IReadOnlyList<ReferenceSite>?, RenameRange?, string?) Refuse(string why) =>
            (null, null, why);
    }

    /// <summary>The module a file of this compilation was parsed into.</summary>
    private static ModuleSymbol? ModuleOf(SemanticModel model, FileId file)
    {
        foreach (var module in model.Compilation.Modules)
            if (model.Compilation.AstOf(module).Span.File == file)
                return module;

        return null;
    }

    /// <summary>
    /// Whether the text is exactly one identifier, asked of THE lexer rather than of a second
    /// rule: a keyword lexes as its own token and an exotic character breaks the token apart, so
    /// "one identifier token covering everything" is precisely the property a rename needs.
    /// </summary>
    private static bool IsIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var sources = new Core.SourceManager();
        var diagnostics = new Core.DiagnosticEngine(sources);
        var lexer = new Lexer(sources, sources.AddVirtual("rename", text), diagnostics);

        var token = lexer.Next();
        return token.TokenKind == Lexing.TokenKind.Identifier
            && token.Span.Start == 0
            && token.Span.End == text.Length
            && lexer.Next().TokenKind == Lexing.TokenKind.Eof
            && !diagnostics.HasErrors;
    }
}
