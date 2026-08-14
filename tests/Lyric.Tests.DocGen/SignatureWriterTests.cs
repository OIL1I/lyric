using Lyric.AST;
using Lyric.Core;
using Lyric.DocGen.Extraction;
using Lyric.Parsing;

namespace Lyric.Tests.DocGen;

/// <summary>
/// The signature is the one place where the reference can be wrong without anything breaking, so it
/// is pinned form by form.
///
/// <para>The type tests come in two halves: the rendered TEXT, and that reading the text back yields
/// the SAME tree. The round trip is the sharper one — it catches a missing parenthesis that a text
/// comparison would happily accept because the expectation was written with the same mistake.</para>
/// </summary>
public class SignatureWriterTests
{
    private static Module ParseModule(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var module = new Parser(sm, id, de).ParseModule();
        Assert.False(de.HasErrors, "the fixture itself must parse");
        return module;
    }

    private static string FirstSignature(string source) => ParseModule(source).Declarations[0] switch
    {
        FunctionDecl f => SignatureWriter.Function(f),
        StructDecl s => SignatureWriter.Struct(s),
        ClassDecl c => SignatureWriter.Class(c),
        EnumDecl e => SignatureWriter.Enum(e),
        InterfaceDecl i => SignatureWriter.Interface(i),
        ExtendDecl x => SignatureWriter.Extend(x),
        TypeAliasDecl a => SignatureWriter.Alias(a),
        GlobalBindingDecl g => SignatureWriter.Binding(g.IsPublic, false, g.Binding),
        var other => throw new Xunit.Sdk.XunitException($"unexpected declaration {other.GetType().Name}"),
    };

    /// <summary>The type of the single parameter of the single function, parsed out of a wrapper.</summary>
    private static TypeNode ParseType(string type)
    {
        var f = Assert.IsType<FunctionDecl>(ParseModule($"fn f(a: {type}): void {{ }}").Declarations[0]);
        return f.Parameters[0].Type;
    }

    // ------------------------------------------------------------------ functions

    [Theory]
    [InlineData("pub fn sqrt(value: float): float;", "pub fn sqrt(value: float): float")]
    [InlineData("fn f(): void { }", "fn f(): void")]
    [InlineData("fn f() { }", "fn f()")]                                    // no return type written
    [InlineData("pub mut fn push(value: T): void { }", "pub mut fn push(value: T): void")]
    [InlineData("fn f(a: int, b: string): bool { }", "fn f(a: int, b: string): bool")]
    [InlineData("fn f(params xs: int[]): void { }", "fn f(params xs: int[]): void")]
    [InlineData("fn f(a: int = 5): void { }", "fn f(a: int = 5): void")]
    [InlineData("fn read(p: string): string throws IoError;", "fn read(p: string): string throws IoError")]
    [InlineData("fn read(p: string): string throws;", "fn read(p: string): string throws")]
    public void A_function_renders_as_written(string source, string expected) =>
        Assert.Equal(expected, FirstSignature(source));

    [Fact]
    public void Static_is_rendered_and_exists_only_in_a_type_body()
    {
        // 'static' is a member modifier; the top-level grammar has no place for it.
        var c = Assert.IsType<ClassDecl>(
            ParseModule("class C { pub static fn of(a: int): int { return a; } }").Declarations[0]);
        var m = Assert.IsType<FunctionDecl>(c.Members[0]);
        Assert.Equal("pub static fn of(a: int): int", SignatureWriter.Function(m));
    }

    [Theory]
    [InlineData("fn f<T>(a: T): T { }", "fn f<T>(a: T): T")]
    [InlineData("fn f<T :: [Ord<T>]>(a: T): T { }", "fn f<T :: [Ord<T>]>(a: T): T")]
    [InlineData("fn f<K :: [Hash<K>, Eq<K>], V>(): void { }", "fn f<K :: [Hash<K>, Eq<K>], V>(): void")]
    public void Generic_parameters_carry_their_constraints(string source, string expected) =>
        Assert.Equal(expected, FirstSignature(source));

    // ------------------------------------------------------------------ type declarations

    [Theory]
    [InlineData("pub struct P { x: int, }", "pub struct P")]
    [InlineData("struct P { x: int, }", "struct P")]
    [InlineData("pub class L<T> :: [Seq<T>] { x: int, }", "pub class L<T> :: [Seq<T>]")]
    [InlineData("pub enum E<T> { A, B(T); }", "pub enum E<T>")]
    [InlineData("pub interface I<T> { fn f(): T; }", "pub interface I<T>")]
    [InlineData("pub extend int :: [Eq] { fn f(): void { } }", "pub extend int :: [Eq]")]
    [InlineData("pub type Id = int;", "pub type Id = int")]
    public void A_type_declaration_renders_its_head(string source, string expected) =>
        Assert.Equal(expected, FirstSignature(source));

    [Theory]
    [InlineData("A", "A")]
    [InlineData("A(int)", "A(int)")]
    [InlineData("A(int, string)", "A(int, string)")]
    [InlineData("A { x: int, y: bool }", "A { x: int, y: bool }")]
    public void An_enum_variant_renders_by_its_shape(string variant, string expected)
    {
        var e = Assert.IsType<EnumDecl>(ParseModule($"enum E {{ {variant}, }}").Declarations[0]);
        Assert.Equal(expected, SignatureWriter.Variant(e.Variants[0]));
    }

    // ------------------------------------------------------------------ bindings

    [Theory]
    [InlineData("pub let pi: float = 3.14;", "pub let pi: float = 3.14")]
    [InlineData("let n: int = 42;", "let n: int = 42")]
    [InlineData("pub let s: string = \"hi\";", "pub let s: string = \"hi\"")]
    [InlineData("pub let b: bool = true;", "pub let b: bool = true")]
    public void A_binding_shows_a_constant_initializer(string source, string expected) =>
        Assert.Equal(expected, FirstSignature(source));

    [Fact]
    public void A_computed_initializer_is_dropped_rather_than_rendered()
    {
        // A signature says THAT there is a value, not how it is computed; the body says that.
        Assert.Equal("pub let n: int", FirstSignature("pub let n: int = 1 + 2;"));
    }

    // ------------------------------------------------------------------ types

    [Theory]
    [InlineData("int", "int")]
    [InlineData("std.io.File", "std.io.File")]
    [InlineData("Map<K, V>", "Map<K, V>")]
    [InlineData("Map<K, List<V>>", "Map<K, List<V>>")]
    [InlineData("int[]", "int[]")]
    [InlineData("int[4]", "int[4]")]
    [InlineData("int[][]", "int[][]")]
    [InlineData("?int", "?int")]
    [InlineData("?int[]", "?int[]")]
    [InlineData("(?int)[]", "(?int)[]")]
    [InlineData("(int, string)", "(int, string)")]
    [InlineData("fn(int, string) -> bool", "fn(int, string) -> bool")]
    [InlineData("fn() -> void", "fn() -> void")]
    [InlineData("?fn(int) -> int", "?fn(int) -> int")]
    [InlineData("(fn(int) -> int)[]", "(fn(int) -> int)[]")]
    public void A_type_renders_as_written(string type, string expected) =>
        Assert.Equal(expected, SignatureWriter.Type(ParseType(type)));

    [Theory]
    [InlineData("int")]
    [InlineData("Map<K, List<V>>")]
    [InlineData("int[][]")]
    [InlineData("?int[]")]
    [InlineData("(?int)[]")]
    [InlineData("(int, string)")]
    [InlineData("fn(int, string) -> bool")]
    [InlineData("?fn(int) -> int")]
    [InlineData("(fn(int) -> int)[]")]
    [InlineData("(fn(int) -> int, ?bool)")]
    public void Rendering_a_type_and_reading_it_back_gives_the_same_tree(string type)
    {
        var once = SignatureWriter.Type(ParseType(type));
        var twice = SignatureWriter.Type(ParseType(once));
        Assert.Equal(once, twice);

        // Not only stable but EQUAL as a tree: a rendering that consistently loses the same
        // parenthesis would be stable and still wrong.
        Assert.Equal(Shape(ParseType(type)), Shape(ParseType(once)));
    }

    /// <summary>The structure of a type, free of spans, so two trees compare by shape.</summary>
    private static string Shape(TypeNode t) => t switch
    {
        NamedType n => $"named({string.Join(".", n.Path)}{(n.TypeArguments.Length == 0
            ? "" : "[" + string.Join(",", n.TypeArguments.Select(Shape)) + "]")})",
        NullableType n => $"opt({Shape(n.Inner)})",
        ArrayType a => $"arr({Shape(a.Element)},{a.Size?.Value.ToString() ?? "-"})",
        TupleType t2 => $"tup({string.Join(",", t2.Elements.Select(Shape))})",
        FunctionType f => $"fn({string.Join(",", f.Parameters.Select(Shape))}->{Shape(f.ReturnType)})",
        _ => "error",
    };

    [Fact]
    public void An_array_of_optionals_is_not_an_optional_array()
    {
        // The one case where a missing parenthesis silently changes the meaning. Both directions
        // stand here, because a renderer that always parenthesises would pass one of them alone.
        Assert.Equal("(?int)[]", SignatureWriter.Type(ParseType("(?int)[]")));
        Assert.Equal("?int[]", SignatureWriter.Type(ParseType("?int[]")));
        Assert.NotEqual(Shape(ParseType("(?int)[]")), Shape(ParseType("?int[]")));
    }
}
