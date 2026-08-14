using System.Globalization;
using System.Text;
using Lyric.AST;

namespace Lyric.DocGen.Extraction;

/// <summary>
/// Renders declarations back into the source form a reader recognises.
///
/// <para>The single place where a signature is formatted. Types are rendered so that reading the
/// result back yields the same tree: <see cref="Element"/> parenthesises where the postfix
/// <c>[]</c> would otherwise bind to the wrong node.</para>
/// </summary>
public static class SignatureWriter
{
    // ------------------------------------------------------------------ declarations

    public static string Function(FunctionDecl d)
    {
        var sb = new StringBuilder();
        if (d.IsPublic) sb.Append("pub ");
        if (d.IsStatic) sb.Append("static ");
        if (d.IsMut) sb.Append("mut ");
        sb.Append("fn ").Append(d.Name).Append(Generics(d.Generics)).Append('(');

        for (var i = 0; i < d.Parameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Parameter(d.Parameters[i]));
        }

        sb.Append(')');
        if (d.ReturnType is not null) sb.Append(": ").Append(Type(d.ReturnType));

        if (d.Throws is not null)
        {
            sb.Append(" throws");
            if (d.Throws.Type is not null) sb.Append(' ').Append(Type(d.Throws.Type));
        }

        return sb.ToString();
    }

    public static string Parameter(Param p)
    {
        var sb = new StringBuilder();
        if (p.IsParams) sb.Append("params ");
        sb.Append(p.Name).Append(": ").Append(Type(p.Type));
        if (p.Default is not null) sb.Append(" = ").Append(Constant(p.Default));
        return sb.ToString();
    }

    public static string Struct(StructDecl d) =>
        TypeHead(d.IsPublic, "struct", d.Name, d.Generics, d.Interfaces);

    public static string Class(ClassDecl d) =>
        TypeHead(d.IsPublic, "class", d.Name, d.Generics, d.Interfaces);

    public static string Enum(EnumDecl d) =>
        TypeHead(d.IsPublic, "enum", d.Name, d.Generics, d.Interfaces);

    public static string Interface(InterfaceDecl d) =>
        TypeHead(d.IsPublic, "interface", d.Name, d.Generics, []);

    public static string Extend(ExtendDecl d)
    {
        var sb = new StringBuilder();
        if (d.IsPublic) sb.Append("pub ");
        sb.Append("extend ").Append(Type(d.Target)).Append(Conformance(d.Interfaces));
        return sb.ToString();
    }

    public static string Field(FieldDecl d) => $"{d.Name}: {Type(d.Type)}";

    /// <summary>A module <c>let</c> or a type-bound <c>static let</c>. The initializer is shown only
    /// when it is a constant; an arbitrary expression belongs in the body, not in a signature.
    /// </summary>
    public static string Binding(bool isPublic, bool isStatic, BindingStmt b)
    {
        var sb = new StringBuilder();
        if (isPublic) sb.Append("pub ");
        if (isStatic) sb.Append("static ");
        sb.Append(b.IsMutable ? "var " : "let ").Append(b.Name);
        if (b.Type is not null) sb.Append(": ").Append(Type(b.Type));
        if (b.Initializer is not null && Constant(b.Initializer) is { } value and not Elided)
            sb.Append(" = ").Append(value);
        return sb.ToString();
    }

    public static string Alias(TypeAliasDecl d) =>
        (d.IsPublic ? "pub " : "") + $"type {d.Name} = {Type(d.Aliased)}";

    public static string Variant(EnumVariant v)
    {
        if (v.TupleFields is { } tuple)
            return $"{v.Name}({string.Join(", ", tuple.Select(Type))})";

        if (v.StructFields is { } fields)
            return $"{v.Name} {{ {string.Join(", ", fields.Select(Field))} }}";

        return v.Name; // unit variant
    }

    // ------------------------------------------------------------------ types

    public static string Type(TypeNode t) => t switch
    {
        NamedType n => string.Join(".", n.Path) + Arguments(n.TypeArguments),
        // '?' is a prefix over everything that follows, so its inner type never needs parentheses.
        NullableType n => "?" + Type(n.Inner),
        ArrayType a => Element(a.Element) + (a.Size is null ? "[]" : $"[{a.Size.Value}]"),
        TupleType t2 => "(" + string.Join(", ", t2.Elements.Select(Type)) + ")",
        FunctionType f => $"fn({string.Join(", ", f.Parameters.Select(Type))}) -> {Type(f.ReturnType)}",
        ErrorType => "<error>",
        _ => "<unknown>",
    };

    /// <summary>
    /// The element of an array, parenthesised where reading it back would bind differently.
    ///
    /// <para><c>?T[]</c> reads as an optional array, so an array OF optionals has to be written
    /// <c>(?T)[]</c>. A function type is open to the right, so <c>fn(A) -&gt; R[]</c> would return an
    /// array; an array of function values is <c>(fn(A) -&gt; R)[]</c>.</para>
    /// </summary>
    private static string Element(TypeNode t) =>
        t is NullableType or FunctionType ? $"({Type(t)})" : Type(t);

    public static string Generics(GenericParam[] generics)
    {
        if (generics.Length == 0) return "";
        var parts = generics.Select(g => g.Constraints.Length == 0
            ? g.Name
            : $"{g.Name} :: [{string.Join(", ", g.Constraints.Select(Type))}]");
        return "<" + string.Join(", ", parts) + ">";
    }

    private static string Arguments(TypeNode[] arguments) =>
        arguments.Length == 0 ? "" : "<" + string.Join(", ", arguments.Select(Type)) + ">";

    private static string Conformance(TypeNode[] interfaces) =>
        interfaces.Length == 0 ? "" : " :: [" + string.Join(", ", interfaces.Select(Type)) + "]";

    private static string TypeHead(bool isPublic, string keyword, string name,
        GenericParam[] generics, TypeNode[] interfaces) =>
        (isPublic ? "pub " : "") + keyword + " " + name + Generics(generics) + Conformance(interfaces);

    // ------------------------------------------------------------------ constants

    /// <summary>Stands for an initializer that is not a constant.</summary>
    public const string Elided = "…";

    /// <summary>
    /// Renders a default value or an initializer, as far as it is a constant.
    ///
    /// <para>Anything else becomes <see cref="Elided"/> rather than a rendered expression: a
    /// signature states THAT there is a default, and a full expression printer would be a second
    /// rendering of the AST beside <c>AstDumper</c>.</para>
    /// </summary>
    public static string Constant(Expr e) => e switch
    {
        IntLiteralExpr i => i.Value.ToString(CultureInfo.InvariantCulture),
        FloatLiteralExpr f => f.Value.ToString("R", CultureInfo.InvariantCulture),
        BoolLiteralExpr b => b.Value ? "true" : "false",
        StringLiteralExpr s => "\"" + Escape(s.Value) + "\"",
        CharLiteralExpr c => "'" + Escape(char.ConvertFromUtf32(c.CodePoint)) + "'",
        NullLiteralExpr => "null",
        IdentifierExpr id => id.Name,
        UnaryExpr { Operator: UnaryOp.Neg } u when Constant(u.Operand) != Elided
            => "-" + Constant(u.Operand),
        ArrayLitExpr { Elements.Length: 0 } => "[]",
        _ => Elided,
    };

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\'' => "\\'",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
