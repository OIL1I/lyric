using Lyric.AST;
using Lyric.Core;
using Lyric.DocGen.Model;
using Lyric.Parsing;
using Module = Lyric.AST.Module;

namespace Lyric.DocGen.Extraction;

/// <summary>
/// Reads the standard library and returns its public surface as a <see cref="DocModel"/>.
///
/// <para>Parsing only. Signatures stand in the syntax — names, parameters, return types, generics,
/// constraints, variants — so neither the resolver nor the sema is needed.</para>
///
/// <para>Nothing is skipped silently: a file that does not parse aborts the run. A missing item in a
/// reference is worse than a failing build, because nobody sees it.</para>
/// </summary>
public static class StdlibExtractor
{
    /// <summary>
    /// Every <c>.lyr</c> under <paramref name="stdlibRoot"/>, in path order so two runs produce the
    /// same model.
    /// </summary>
    /// <param name="repoRoot">Base for the paths in <see cref="SourceRef"/>.</param>
    /// <exception cref="InvalidOperationException">A file did not parse.</exception>
    public static DocModel Extract(string stdlibRoot, string repoRoot)
    {
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);

        var files = Directory
            .GetFiles(stdlibRoot, "*.lyr", SearchOption.AllDirectories)
            .OrderBy(f => f.Replace('\\', '/'), StringComparer.Ordinal)
            .ToArray();

        var modules = new List<DocModule>();
        foreach (var file in files)
        {
            var id = sm.AddFromDisk(file);
            var parser = new Parser(sm, id, de);
            var ast = parser.ParseModule();

            if (de.HasErrors)
            {
                var report = new StringWriter();
                de.RenderText(report);
                throw new InvalidOperationException($"{file} did not parse:\n{report}");
            }

            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            modules.Add(new FileReader(parser, sm, id, relative).Read(ast));
        }

        return new DocModel(modules.ToArray());
    }

    /// <summary>
    /// One parsed file. Holds what every item needs — the doc table, the line lookup and the
    /// repository-relative path — so the walk carries a context instead of four parameters.
    /// </summary>
    private sealed class FileReader(Parser parser, SourceManager sm, FileId id, string file)
    {
        public DocModule Read(Module ast)
        {
            var path = ast.Header is null
                ? Path.GetFileNameWithoutExtension(file)
                : string.Join(".", ast.Header.Segments);

            var items = ast.Declarations.Select(Item).OfType<DocItem>().ToArray();
            var doc = ast.Header is null ? null : parser.DocOf(ast.Header);
            return new DocModule(path, doc, items);
        }

        /// <summary>
        /// One top-level declaration, or <c>null</c> when it is not on the public surface. An import
        /// never is: what a module uses is its business, not its contract.
        /// </summary>
        private DocItem? Item(Decl decl) => decl switch
        {
            FunctionDecl { IsPublic: true } f =>
                Make(ItemKind.Function, f.Name, SignatureWriter.Function(f), [], f),

            StructDecl { IsPublic: true } s =>
                Make(ItemKind.Struct, s.Name, SignatureWriter.Struct(s), TypeMembers(s.Members), s),

            ClassDecl { IsPublic: true } c =>
                Make(ItemKind.Class, c.Name, SignatureWriter.Class(c), TypeMembers(c.Members), c),

            EnumDecl { IsPublic: true } e =>
                Make(ItemKind.Enum, e.Name, SignatureWriter.Enum(e), EnumMembers(e), e),

            // Every member of an interface is part of the contract, so none is filtered.
            InterfaceDecl { IsPublic: true } i =>
                Make(ItemKind.Interface, i.Name, SignatureWriter.Interface(i), Methods(i.Members), i),

            ExtendDecl { IsPublic: true } x =>
                Make(ItemKind.Extend, SignatureWriter.Type(x.Target), SignatureWriter.Extend(x),
                    Methods(x.Methods), x),

            GlobalBindingDecl { IsPublic: true } g =>
                Make(ItemKind.Binding, g.Binding.Name, SignatureWriter.Binding(true, false, g.Binding),
                    [], g),

            TypeAliasDecl { IsPublic: true } a =>
                Make(ItemKind.Alias, a.Name, SignatureWriter.Alias(a), [], a),

            _ => null,
        };

        /// <summary>
        /// The body of a struct or class. A field has no visibility modifier and is therefore
        /// reachable; a method and a <c>static let</c> have one and are filtered by it.
        /// </summary>
        private DocItem[] TypeMembers(Decl[] members)
        {
            var items = new List<DocItem>();
            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldDecl f:
                        items.Add(Make(ItemKind.Field, f.Name, SignatureWriter.Field(f), [], f));
                        break;
                    case FunctionDecl { IsPublic: true } fn:
                        items.Add(Make(ItemKind.Method, fn.Name, SignatureWriter.Function(fn), [], fn));
                        break;
                    case StaticBindingDecl { IsPublic: true } sb:
                        items.Add(Make(ItemKind.Binding, sb.Binding.Name,
                            SignatureWriter.Binding(true, true, sb.Binding), [], sb));
                        break;
                }
            }
            return items.ToArray();
        }

        /// <summary>The variants first, then the methods — the order a reader wants, and the one the
        /// declaration itself keeps apart.</summary>
        private DocItem[] EnumMembers(EnumDecl e) =>
        [
            .. e.Variants.Select(v =>
                Make(ItemKind.Variant, v.Name, SignatureWriter.Variant(v), [], v)),
            .. Methods(e.Methods),
        ];

        private DocItem[] Methods(FunctionDecl[] methods) =>
            methods.Select(m => Make(ItemKind.Method, m.Name, SignatureWriter.Function(m), [], m))
                .ToArray();

        private DocItem Make(ItemKind kind, string name, string signature, DocItem[] members, Node node) =>
            new(kind, name, signature, parser.DocOf(node), members,
                new SourceRef(file, sm.Locate(id, node.Span.Start).Line));
    }
}
