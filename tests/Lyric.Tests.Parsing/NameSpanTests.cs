using System.Reflection;
using System.Runtime.CompilerServices;
using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Where a declaration writes its name.
///
/// <para>Two kinds of test. The synthetic ones fix the offsets per node type, one at a time. The
/// ones over real source fix the two invariants the consumers rely on — the name lies inside the
/// declaration, and the text at the name span is the name — across everything the repository holds,
/// which is the only way to reach every node type in the combinations they actually occur in.</para>
/// </summary>
public sealed class NameSpanTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Parses a source string and returns the module together with its text, so a test can
    /// compare a span against what stands at it.</summary>
    private static (Lyric.AST.Module Module, string Text, DiagnosticEngine Diagnostics) Parse(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("names.lyr", source);
        var de = new DiagnosticEngine(sm);
        return (new Parser(sm, id, de).ParseModule(), source, de);
    }

    /// <summary>Every named declaration in the tree, in source order.</summary>
    private static List<INamedDecl> Named(Node root)
    {
        var found = new List<INamedDecl>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is INamedDecl named) found.Add(named);
            foreach (var child in AstChildren.Of(node)) stack.Push(child);
        }
        return found;
    }

    /// <summary>The one named declaration of the given type, when a fixture holds exactly one.
    /// </summary>
    private static (string At, INamedDecl Decl) Single<T>(string source) where T : Node
    {
        var (module, text, de) = Parse(source);
        Assert.False(de.HasErrors);

        var decl = Assert.Single(Named(module).OfType<T>()) as INamedDecl;
        Assert.NotNull(decl);
        return (text.Substring(decl.NameSpan.Start, decl.NameSpan.Length), decl);
    }

    // ------------------------------------------------------------------ per node type

    [Fact]
    public void A_function_names_itself_and_not_its_body()
    {
        var (at, decl) = Single<FunctionDecl>("fn twice(n: int): int {\n    return n * 2;\n}\n");

        Assert.Equal("twice", at);
        Assert.Equal(3, decl.NameSpan.Start);

        // The declaration reaches past the name by the whole body; that difference is the reason
        // the second span exists.
        Assert.True(decl.Span.End > decl.NameSpan.End);
    }

    [Fact]
    public void A_struct_names_itself()
    {
        var (at, _) = Single<StructDecl>("struct Point { x: int, y: int, }\n");
        Assert.Equal("Point", at);
    }

    [Fact]
    public void A_class_names_itself()
    {
        var (at, _) = Single<ClassDecl>("class Builder { count: int, }\n");
        Assert.Equal("Builder", at);
    }

    [Fact]
    public void An_interface_names_itself()
    {
        var (at, _) = Single<InterfaceDecl>("interface Shape { fn area(): float; }\n");
        Assert.Equal("Shape", at);
    }

    [Fact]
    public void An_enum_and_its_variants_name_themselves()
    {
        var (module, text, de) = Parse("enum Shape {\n    Circle(float),\n    Square(float),\n}\n");
        Assert.False(de.HasErrors);

        var e = Assert.Single(Named(module).OfType<EnumDecl>());
        Assert.Equal("Shape", text.Substring(e.NameSpan.Start, e.NameSpan.Length));

        var variants = Named(module).OfType<EnumVariant>()
            .OrderBy(v => v.NameSpan.Start)
            .Select(v => text.Substring(v.NameSpan.Start, v.NameSpan.Length))
            .ToArray();

        Assert.Equal(["Circle", "Square"], variants);
    }

    [Fact]
    public void A_field_names_itself_and_not_its_type()
    {
        var (at, decl) = Single<FieldDecl>("struct P { count: int = 0, }\n");

        Assert.Equal("count", at);

        // The field's own span runs through the default value; only the name is the name.
        Assert.True(decl.Span.End > decl.NameSpan.End);
    }

    [Fact]
    public void A_parameter_names_itself()
    {
        var (at, _) = Single<Param>("fn f(value: int): int { return value; }\n");
        Assert.Equal("value", at);
    }

    [Fact]
    public void A_lambda_parameter_names_itself()
    {
        var (at, _) = Single<LambdaParam>(
            "fn f(): int {\n    let double = (n: int) => n * 2;\n    return double(2);\n}\n");

        Assert.Equal("n", at);
    }

    [Fact]
    public void A_type_parameter_names_itself_and_not_its_constraints()
    {
        var (at, decl) = Single<GenericParam>(
            "interface Eq { fn eq(): bool; }\nfn f<T :: [Eq]>(v: T): bool { return true; }\n");

        Assert.Equal("T", at);
        Assert.True(decl.Span.End > decl.NameSpan.End);
    }

    [Fact]
    public void A_type_alias_names_itself()
    {
        var (at, _) = Single<TypeAliasDecl>("type Count = int;\n");
        Assert.Equal("Count", at);
    }

    [Fact]
    public void A_binding_names_itself_and_not_the_let()
    {
        var (at, decl) = Single<BindingStmt>("fn f(): int {\n    let total = 1;\n    return total;\n}\n");

        Assert.Equal("total", at);

        // The statement starts at 'let', four characters earlier.
        Assert.True(decl.NameSpan.Start > decl.Span.Start);
    }

    [Fact]
    public void A_loop_variable_names_itself_and_not_the_loop()
    {
        var (at, decl) = Single<ForInStmt>(
            "fn f(): int {\n    var t = 0;\n    for (n in 0..10) {\n        t = t + n;\n    }\n    return t;\n}\n");

        Assert.Equal("n", at);

        // The loop reaches through its body, which is what made the whole-declaration answer
        // useless here rather than merely imprecise.
        Assert.True(decl.Span.End > decl.NameSpan.End + 10);
    }

    [Fact]
    public void A_catch_binding_names_itself_and_not_the_clause()
    {
        var (at, decl) = Single<CatchClause>(
            "fn f(): int {\n    try {\n        return 1;\n    } catch (e) {\n        return 0;\n    }\n}\n");

        Assert.Equal("e", at);
        Assert.True(decl.Span.End > decl.NameSpan.End);
    }

    [Fact]
    public void A_wildcard_catch_reports_the_underscore_it_is_written_as()
    {
        // '_' binds nothing and the sema builds no symbol for it, so nothing ever jumps here. The
        // span still covers the token, which is what keeps the two invariants below total.
        var (at, decl) = Single<CatchClause>(
            "fn f(): int {\n    try {\n        return 1;\n    } catch (_) {\n        return 0;\n    }\n}\n");

        Assert.Equal("_", at);
        Assert.Equal("_", decl.Name);
        Assert.Null(((CatchClause)decl).BindingName);
    }

    // ------------------------------------------------------------------ recovery

    [Fact]
    public void A_declaration_whose_name_is_missing_keeps_a_usable_span()
    {
        // Recovery must not produce a default span: its FileId is invalid, and a consumer that
        // checks for that would silently drop the declaration rather than report where it stands.
        var (module, _, de) = Parse("fn (): int { return 1; }\n");
        Assert.True(de.HasErrors);

        foreach (var decl in Named(module))
        {
            Assert.True(decl.NameSpan.File.IsValid, $"{decl.GetType().Name} has no file");
            Assert.True(decl.NameSpan.Start >= decl.Span.Start && decl.NameSpan.End <= decl.Span.End,
                $"{decl.GetType().Name}: {decl.NameSpan} is not inside {decl.Span}");
        }
    }

    // ------------------------------------------------------------------ totality

    /// <summary>
    /// Node types that carry a <c>string Name</c> and are NOT declarations.
    ///
    /// <para>Each is a USE of a name rather than the place it is introduced, or a node whose own
    /// span is already the name and needs nothing added. The list is short and every entry states
    /// which of the two it is; a new node with a name is a red test until it is classified.</para>
    /// </summary>
    private static readonly HashSet<string> NotDeclarations =
    [
        nameof(IdentifierExpr),   // a use
        nameof(AtIdentifierExpr), // a use
        nameof(MemberExpr),       // a use
        nameof(StructInitField),  // a use: the field it assigns is declared on the type
        nameof(FieldPattern),     // its span is the name in the form that binds
        nameof(BindingPattern),   // its span is the name
    ];

    public static TheoryData<Type> NodeTypesWithAName()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(Node).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsAssignableTo(typeof(Node))) continue;
            if (type.Namespace != typeof(Node).Namespace) continue;
            if (type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance) is null) continue;
            data.Add(type);
        }
        return data;
    }

    /// <summary>
    /// A node that carries a name either records where the name stands or is listed as something
    /// that does not declare one.
    ///
    /// <para>Reflection rather than a maintained list of implementors: a list would be the second
    /// description of the same set and would drift from the first.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(NodeTypesWithAName))]
    public void Every_node_with_a_name_declares_it_or_is_excluded(Type type)
    {
        if (NotDeclarations.Contains(type.Name))
        {
            // Asserted rather than skipped: an entry that stops being true has to say so, or the
            // list becomes a place where an exclusion outlives its reason.
            Assert.False(type.IsAssignableTo(typeof(INamedDecl)),
                $"{type.Name} implements INamedDecl and is listed in {nameof(NotDeclarations)}. "
                + "Remove it from the list.");
            return;
        }

        Assert.True(type.IsAssignableTo(typeof(INamedDecl)),
            $"{type.Name} carries a Name but no NameSpan. Implement INamedDecl, or add it to "
            + $"{nameof(NotDeclarations)} with the reason it declares nothing.");
    }

    // ------------------------------------------------------------------ real input

    public static TheoryData<string> RealSources()
    {
        var data = new TheoryData<string>();
        foreach (var directory in new[] { "stdlib", "examples" })
            foreach (var file in Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), directory), "*.lyr", SearchOption.AllDirectories))
                data.Add(file);
        return data;
    }

    /// <summary>
    /// The two invariants over every file the repository ships.
    ///
    /// <para>The containment is what the protocol demands of a selection inside a range. The text
    /// comparison is what catches a span that is off by one or taken from the wrong token — an
    /// error the containment alone would not see, because a wrong span inside the declaration is
    /// still inside it.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(RealSources))]
    public void Every_declaration_in_a_shipped_file_points_at_its_own_name(string path)
    {
        var text = File.ReadAllText(path);
        var sm = new SourceManager();
        var id = sm.AddVirtual(Path.GetFileName(path), text);
        var de = new DiagnosticEngine(sm);
        var module = new Parser(sm, id, de).ParseModule();

        Assert.False(de.HasErrors, $"{path} does not parse");

        var declarations = Named(module);
        Assert.NotEmpty(declarations);

        foreach (var decl in declarations)
        {
            Assert.True(decl.NameSpan.Start >= decl.Span.Start && decl.NameSpan.End <= decl.Span.End,
                $"{path}: {decl.GetType().Name} '{decl.Name}' has {decl.NameSpan} outside {decl.Span}");

            Assert.Equal(decl.Name, text.Substring(decl.NameSpan.Start, decl.NameSpan.Length));
        }
    }
}
