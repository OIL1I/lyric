using Lyric.Core;

namespace Lyric.Ir
{
    public enum IrScalar
    {
        I8, I16, I32, I64,
        U8, U16, U32, U64,
        F32, F64,
        Bool, Char, String, Void
    }

    /// <summary>
    /// The base of the IR types.
    /// </summary>
    public abstract record IrType
    {
        /// <summary>
        /// Type equality of two IR types. The <c>default</c> throw is deliberate and the same
        /// convention as in <see cref="TypeLowering.Lower"/> ("not lowerable in current version") and
        /// <c>IrPrinter.TypeStr</c> ("not printable"): a total function over today's type universe
        /// that speaks up as soon as it grows. When a composite type — array, tuple, reference — is
        /// added, a case has to go here, and the throw names the place.
        /// </summary>
        /// <remarks>A <c>default</c> returning <c>false</c> would be the worse choice: the verifier
        /// compares types at about twenty places and would report a flood of false type mismatches on
        /// the first non-scalar type, which would look like an IR bug rather than a comparison
        /// bug.</remarks>
        public static bool Equal(IrType a, IrType b)
        {
            switch (a, b)
            {
                case (IrScalarType x, IrScalarType y):
                    return x.Kind == y.Kind;
                case (IrRefType x, IrRefType y):
                    return x.Type == y.Type;
                case (IrArrayType x, IrArrayType y):
                    return Equal(x.Element, y.Element);
                case (IrOptionalType x, IrOptionalType y):
                    return Equal(x.Inner, y.Inner);
                case (IrEnumType x, IrEnumType y):
                    return x.Type == y.Type;
                case (IrInterfaceType x, IrInterfaceType y):
                    return x.Type == y.Type;
                case (IrStructType x, IrStructType y):
                    return x.Type == y.Type;
                case (IrHostType x, IrHostType y):
                    // By name, because there is no id. Two host types are the same when the host
                    // registered them under the same name; the module knows nothing more about them.
                    return string.Equals(x.Name, y.Name, StringComparison.Ordinal);
                case (IrFunctionType x, IrFunctionType y):
                    // Structural, and it terminates: a function type can contain itself only through a
                    // named type, and that one compares by id.
                    return x.Parameters.Length == y.Parameters.Length
                           && Equal(x.Return, y.Return)
                           && x.Parameters.Zip(y.Parameters).All(pair => Equal(pair.First, pair.Second));
                case (IrScalarType or IrRefType or IrArrayType or IrOptionalType or IrEnumType
                          or IrInterfaceType or IrStructType or IrFunctionType,
                      IrScalarType or IrRefType or IrArrayType or IrOptionalType or IrEnumType
                          or IrInterfaceType or IrStructType or IrFunctionType):
                    return false; // different kinds: comparable, merely unequal
                default:
                    throw new InternalCompilationException(
                        $"ir-type: cannot compare {a.GetType().Name} with {b.GetType().Name}");
            }
        }
    }

    public sealed record IrScalarType(IrScalar Kind) : IrType;

    /// <summary>
    /// A reference to an instance of the type <see cref="Type"/>, a <c>class</c>. Assignment copies
    /// the reference, not the object.
    ///
    /// <para>ONLY THE ID, NOT THE LAYOUT. The field list stands once in <c>IrModule.Types</c>. If the
    /// type carried it, <see cref="IrType.Equal"/> would have to compare structurally, and would run
    /// into an infinite loop on <c>class Node { next: Node }</c>. This way equality is an <c>int</c>
    /// comparison and recursion is free.</para>
    /// </summary>
    /// Endlosschleife. So ist Gleichheit ein <c>int</c>-Vergleich und Rekursion kostenlos.</para>
    /// </summary>
    public sealed record IrRefType(TypeId Type) : IrType;

    /// <summary>
    /// A growing array (<c>T[]</c>). Like <see cref="IrRefType"/> a reference: assignment shares the
    /// array, it does not copy it.
    ///
    /// <para>THE ELEMENT TYPE IS INLINE rather than a table index, unlike for a class. That works
    /// because an array type cannot be recursive: <c>int[][]</c> is finitely deep, a
    /// <c>class Node { next: Node }</c> is not. Where no recursion threatens, the indirection is pure
    /// cost.</para>
    /// </summary>
    public sealed record IrArrayType(IrType Element) : IrType;

    /// <summary>
    /// <c>?T</c>. As with the array the inner type is inline; an optional cannot be recursive either.
    ///
    /// <para>NOT NESTABLE: there is no <c>??T</c>. The runtime representation distinguishes "no value"
    /// by the empty reference, and that can carry one level only.</para>
    /// </summary>
    public sealed record IrOptionalType(IrType Inner) : IrType;

    /// <summary>
    /// An enum. Like <see cref="IrRefType"/> it goes through a <see cref="TypeId"/> rather than being
    /// inline: an enum has a declaration and may be recursive
    /// (<c>enum Tree { Leaf, Node(Tree, Tree) }</c>).
    ///
    /// <para>At runtime a value of this type is an instance of ONE variant; which one stands in its
    /// slot 0. Every variant has its own layout — see <c>docs/Bytecode.md</c>.</para>
    /// </summary>
    public sealed record IrEnumType(TypeId Type) : IrType;

/// <summary>
/// A value addressed through an interface.
///
/// <para>Like <see cref="IrRefType"/> it carries only its id, not its method list; otherwise
/// <c>IrType.Equal</c> would have to compare structurally and would loop forever on an interface that
/// names itself in a signature.</para>
///
/// <para>At runtime this is no mere pointer but a fat pointer of object and concrete type index —
/// <c>LyrValue</c> has both fields anyway, and for a reference <c>Bits</c> is unused. An interface
/// value therefore costs no allocation, and an object that never goes through an interface pays
/// nothing. The alternative, a type tag in slot 0 of every object, would shift every field index and
/// cost every object one word.</para>
/// </summary>
public sealed record IrInterfaceType(TypeId Type) : IrType;

/// <summary>
/// A <c>struct</c>: the same layout as <see cref="IrRefType"/>, but with VALUE SEMANTICS. Assignment
/// copies.
///
/// <para>As with a class the type carries only its id. Here that is even mandatory: a struct must not
/// contain itself — it would be infinitely large — and the sema rejects that as
/// <c>LYR-SEM0056</c>.</para>
///
/// <para>AT RUNTIME IT IS THE SAME SLOT ARRAY AS A CLASS OBJECT. The difference is not in the
/// representation but in the instructions: a <c>structcopy</c> stands at every binding point. The
/// alternative — embedding struct fields into the slots of the enclosing object, as C# and Rust do —
/// needs field access over sub-ranges and therefore a different layout model; it is a later,
/// format-neutral optimization (scalar replacement), not a prerequisite for correctness.</para>
/// </summary>
public sealed record IrStructType(TypeId Type) : IrType;

/// <summary>
/// A HOST OBJECT: a reference whose layout the HOST knows and the module does not.
///
/// <para>The contrast with <see cref="IrRefType"/> is the whole point. Both are references; with
/// <c>IrRefType</c> the module knows the layout and the host stays out, here it is the other way
/// round. This type therefore carries NO <c>TypeId</c> but a name: there is no type table entry,
/// because there is no layout that could stand in it.</para>
///
/// <para>The promise "no <c>ldfld</c> is ever emitted against a host type" is thereby STRUCTURAL
/// rather than checked: without a field list a field access is not even encodable.</para>
///
/// <para>At runtime it is an ordinary <c>LyrValue.Ref</c>, just like a <c>string</c>, which lives
/// there without the VM ever looking inside.</para>
/// </summary>
public sealed record IrHostType(string Name) : IrType;

/// <summary>
/// A FUNCTION VALUE: what stands in <c>fn(int) -> bool</c>, and what a closure is.
///
/// <para>AT RUNTIME A FAT POINTER of environment object and function index — the same build as
/// <see cref="IrInterfaceType"/> and for the same reason: <c>LyrValue</c> has both fields anyway, so
/// a function value costs no allocation beyond its environment. A closure without captures has none
/// and is therefore a pure index.</para>
///
/// <para>The type carries its signature STRUCTURALLY, unlike every named type here. It has to:
/// <c>fn(int) -> bool</c> has no declaration an id could hang on, and two identically shaped function
/// types from different modules are the same type. The comparison still terminates, because recursion
/// is possible only through a named type.</para>
/// </summary>
public sealed record IrFunctionType(IrType[] Parameters, IrType Return) : IrType;


}
