using Lyric.Bytecode;

namespace Lyric.Vm;

/// <summary>
/// A runtime value.
///
/// <para>It carries no type tag: every opcode carries one in the instruction stream, so the
/// interpreter knows statically what is on the stack.</para>
///
/// <para>Numbers live in <see cref="Bits"/> rather than on the heap. Only references need
/// <see cref="Ref"/>, and the .NET garbage collector owns their lifetime.</para>
///
/// <para>Integers are always widened to 64 bits: signed types sign-extended, unsigned types
/// zero-extended, so comparisons and division work directly on <c>long</c>/<c>ulong</c> without
/// knowing the width. <see cref="Normalize"/> restores the invariant after every operation.</para>
/// </summary>
public readonly struct LyrValue
{
    public readonly ulong Bits;
    public readonly object? Ref;

    private LyrValue(ulong bits, object? reference)
    {
        Bits = bits;
        Ref = reference;
    }

    public static LyrValue FromBits(ulong bits) => new(bits, null);
    public static LyrValue FromI64(long value) => new((ulong)value, null);
    public static LyrValue FromBool(bool value) => new(value ? 1UL : 0UL, null);
    public static LyrValue FromF64(double value) => new(BitConverter.DoubleToUInt64Bits(value), null);
    public static LyrValue FromF32(float value) => new(BitConverter.SingleToUInt32Bits(value), null);
    public static LyrValue FromString(string value) => new(0, value);

    /// <summary>
    /// A host object: an arbitrary .NET reference the VM never looks into.
    ///
    /// <para>It uses the same field as a <c>string</c>. No instruction reads inside it: a host
    /// type has no type-table entry and therefore no field an <c>ldfld</c> could name.</para>
    ///
    /// <para>The .NET garbage collector owns its lifetime; the object lives as long as a Lyric
    /// value reaches it.</para>
    /// </summary>
    public static LyrValue FromHostObject(object value) => new(0, value);

    /// <summary>An object reference: one slot per field. The instruction stream carries the type,
    /// and the loader has checked the type and field indices.</summary>
    public static LyrValue FromObject(LyrValue[] fields) => new(0, fields);

    /// <summary>
    /// Marker for "this optional holds a value" when the value itself is not a reference.
    ///
    /// <para>An object rather than a bit pattern: for <c>?int</c> there is no free pattern, since
    /// every <c>i64</c> is a valid number. Shared globally, so "some" costs no allocation.</para>
    /// </summary>
    private static readonly object SomeMarker = new();

    /// <summary>
    /// A value addressed through an interface: a fat pointer of the object (<see cref="Ref"/>) and
    /// the index of its concrete type (<see cref="Bits"/>).
    ///
    /// <para>An object carries no type tag, so a <c>callvirt</c> cannot recover the concrete class
    /// from it. Attaching the index to the value costs nothing, because <c>Bits</c> is unused next
    /// to a reference.</para>
    ///
    /// <para>A <c>?SomeInterface</c> works unchanged: the fat pointer holds a real reference,
    /// which serves as the presence marker.</para>
    /// </summary>
    public static LyrValue FromInterface(LyrValue instance, int concreteType) =>
        new((ulong)(uint)concreteType, instance.Ref);

    /// <summary>The concrete type index of an interface value: what <c>callvirt</c> looks up.
    /// </summary>
    public int ConcreteType => (int)(uint)Bits;

    /// <summary>
    /// A closure value: environment plus the index of the lifted function.
    ///
    /// <para>The same shape as <see cref="FromInterface"/>: the reference holds the environment
    /// and <c>Bits</c> is free beside it. A closure without captures holds no reference and costs
    /// no allocation.</para>
    ///
    /// <para>The index is stored incremented by one, so a closure over function 0 without an
    /// environment is not bit-identical to <see cref="None"/>.</para>
    /// </summary>
    public static LyrValue FromClosure(LyrValue environment, int function) =>
        new((ulong)(uint)(function + 1), environment.Ref);

    /// <summary>The function index of a closure value.</summary>
    public int ClosureFunction => (int)(uint)Bits - 1;

    /// <summary>Does this closure carry an environment? Without captures there is none.</summary>
    public bool HasEnvironment => Ref is not null;

    /// <summary>"No value" is an empty reference, uniformly for every <c>?T</c>.</summary>
    public static LyrValue None => default;

    /// <summary>Wraps a value. A reference carries itself; otherwise <see cref="SomeMarker"/>
    /// marks presence and the number stays in <see cref="Bits"/>.</summary>
    public static LyrValue Some(LyrValue value) =>
        value.Ref is not null ? value : new(value.Bits, SomeMarker);

    public bool IsSome => Ref is not null;

    /// <summary>Unwraps. The counterpart of <see cref="Some"/>: the marker disappears, a real
    /// reference stays.</summary>
    public LyrValue Unwrap() => ReferenceEquals(Ref, SomeMarker) ? FromBits(Bits) : this;

    public long AsI64 => (long)Bits;
    public ulong AsU64 => Bits;
    public bool AsBool => Bits != 0;
    public double AsF64 => BitConverter.UInt64BitsToDouble(Bits);
    public float AsF32 => BitConverter.UInt32BitsToSingle((uint)Bits);
    public string AsString => (string)(Ref ?? string.Empty);

    /// <summary>The field slots of an instance. Throws on a null reference.</summary>
    public LyrValue[] AsObject => (LyrValue[])(Ref
        ?? throw new InvalidOperationException("null object reference"));

    /// <summary>Restores the width invariant: truncate to the type's width, then widen back to 64
    /// bits according to its sign. Without it <c>add i8</c> of 200 + 100 would yield 300 instead of
    /// overflowing.</summary>
    public static ulong Normalize(TypeTag tag, ulong bits) => tag switch
    {
        TypeTag.I8 => (ulong)(long)(sbyte)bits,
        TypeTag.I16 => (ulong)(long)(short)bits,
        TypeTag.I32 => (ulong)(long)(int)bits,
        TypeTag.I64 => bits,
        TypeTag.U8 => (byte)bits,
        TypeTag.U16 => (ushort)bits,
        TypeTag.U32 => (uint)bits,
        TypeTag.U64 => bits,
        TypeTag.Bool => bits != 0 ? 1UL : 0UL,
        TypeTag.Char => CheckedCodepoint(bits),
        _ => bits,
    };

    /// <summary>
    /// A <c>char</c> result must be a Unicode code point.
    ///
    /// <para>The check sits in <see cref="Normalize"/>, the single path through which a scalar
    /// result is produced: arithmetic, negation, bitwise not, cast and constants all pass here. A
    /// <c>char</c> is therefore always valid, and everything consuming one may rely on it.</para>
    /// </summary>
    private static ulong CheckedCodepoint(ulong bits)
    {
        // Read signed: 'a' - 1000 is negative and must be seen as such, not as a large unsigned
        // number.
        var value = (long)bits;
        if (Core.Unicode.IsCodepoint(value)) return bits;

        throw new LyricPanic(VmDiagnostics.InvalidCodepoint,
            $"char value {value} is not a Unicode codepoint ({Core.Unicode.DescribeRange()})");
    }

    public static bool IsSigned(TypeTag tag) =>
        tag is TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64;

    /// <summary>
    /// Integer at the bytecode level, including <c>Char</c>.
    ///
    /// <para><c>TypeFacts.IsInteger</c> (on <c>LyrType</c>) and <c>IrVerifier.IsInteger</c> (on
    /// <c>IrType</c>) answer the same question for the other two representations; the three move
    /// together.</para>
    /// </summary>
    public static bool IsInteger(TypeTag tag) =>
        tag is TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64
            or TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64
            or TypeTag.Char;

    public static bool IsFloat(TypeTag tag) => tag is TypeTag.F32 or TypeTag.F64;
}
