using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// A syntactic <see cref="TypeNode"/> to an <see cref="IrType"/>.
///
/// <para>Needed because the sema does not put the resolved signature types into <c>TypeResult</c>,
/// which holds expression types only. For return types and the parameters of native declarations the
/// lowering has to read the node itself.</para>
///
/// <para>Builtins are <see cref="NamedType"/> with a single-element path. On top of that comes
/// <c>T[]</c> over a primitive — <c>split</c> yields <c>string[]</c>, <c>toChars</c> yields
/// <c>char[]</c>. The line stays sharp: unlike a class, an array has NO LAYOUT the host would have to
/// know. Everything else reports as a scope boundary rather than silently yielding something
/// wrong.</para>
/// </summary>
internal static class DeclaredTypes
{
    private static readonly IrType VoidType = new IrScalarType(IrScalar.Void);

    public static IrType Lower(TypeNode? node, Func<TypeNode, string?>? hostType = null)
    {
        if (node is null) return VoidType; // a missing return type means void

        if (node is NamedType { Path.Length: 1, TypeArguments.Length: 0 } named
            && TypeFacts.FromBuiltinName(named.Path[0]) is { } primitive)
            return TypeLowering.Lower(primitive);

        // '?T' in a native signature: 'readText' yields '?string', and so does 'env'. A failure that is
        // an ordinary state of the world belongs in the return value rather than in an exception, and
        // for that the type has to be expressible.
        if (node is NullableType option) return new IrOptionalType(Lower(option.Inner, hostType));

        // A HOST type. It is the one kind of non-primitive a native signature may name, precisely
        // because the host knows its layout and the module does not.
        if (hostType?.Invoke(node) is { } name) return new IrHostType(name);

        // 'T[]' in a native signature. The element type stays primitive: an array of objects would
        // require the host to know a module layout.
        if (node is ArrayType { Size: null } array
            && array.Element is NamedType { Path.Length: 1, TypeArguments.Length: 0 } element
            && TypeFacts.FromBuiltinName(element.Path[0]) is { } elementPrimitive)
            return new IrArrayType(TypeLowering.Lower(elementPrimitive));

        throw new UnsupportedConstructException(
            "non-primitive type in a declared signature is not supported by this compiler version yet",
            node.Span);
    }
}
