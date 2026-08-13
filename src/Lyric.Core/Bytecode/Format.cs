namespace Lyric.Bytecode;

/// <summary>
/// The constants of the <c>.lyrbc</c> format: magic, version, section ids, type tags and opcodes.
/// A test binds <c>docs/Bytecode.md</c> to this file so the two cannot drift.
/// </summary>
public static class Format
{
    /// <summary>"LYRB" — four bytes, not interpreted as text.</summary>
    public static ReadOnlySpan<byte> Magic => "LYRB"u8;

    /// <summary>An unknown major version is rejected, an unknown minor tolerated, because a new
    /// minor may only add skippable sections. Before v1.0 the major may change freely.</summary>
    public const ushort VersionMajor = 3;
    public const ushort VersionMinor = 0;
}

/// <summary>
/// Section ids. Every section carries its byte length and an unknown id is skipped, which is what
/// makes the source map strippable and the format extensible without a major bump. Sections appear
/// at most once and in ascending id order.
/// </summary>
public enum SectionId : byte
{
    Capabilities = 1,

    /// <summary>The constant pool. Strings only: as a LEB128 immediate a number is no larger than
    /// a pool index and saves the indirection.</summary>
    Strings = 2,

    /// <summary>Layouts of composite types: name, field count, field types. The field index is
    /// the position in the field list; field names are not in the bytecode. Through the index a
    /// recursive type is encodable, which structurally it would not be.</summary>
    Types = 3,

    /// <summary>Host and native functions with a symbolic name and signature.</summary>
    Imports = 4,

    Functions = 5,

    /// <summary>Optional and strippable: PC to file and line.</summary>
    SourceMap = 6,

    /// <summary>Entry point: the <c>uleb128</c> index of the function a runtime calls. Absent for
    /// a library module. Without the section a runtime would have to guess the entry point from a
    /// naming convention.</summary>
    Start = 7,

    /// <summary>
    /// Interface implementations: which function fills which method slot of which interface for
    /// which type. The vtable rows <c>callvirt</c> takes its target from.
    ///
    /// <para>Its own section rather than a field in the layout entry: a new minor may only add
    /// skippable sections, and an extra field would change an existing section's shape.</para>
    /// </summary>
    Impls = 8,

    /// <summary>
    /// Protected regions per function: which block range is covered by which handler.
    ///
    /// <para>Its own section for the same reason as Impls: a field in the function header would
    /// change an existing section's shape.</para>
    /// </summary>
    Handlers = 9,

    /// <summary>
    /// Global slots — module-level <c>let</c> and <c>static let</c> — with the function that fills
    /// them.
    ///
    /// <para>A function rather than stored values: an initializer is an expression, and storing it
    /// as a value would only work for scalars.</para>
    /// </summary>
    Globals = 10,
}

/// <summary>
/// Type tags, one byte. Values from 0x40 are reserved for composite types, so adding one does not
/// shift the existing tags.
/// </summary>
public enum TypeTag : byte
{
    I8 = 0x01, I16 = 0x02, I32 = 0x03, I64 = 0x04,
    U8 = 0x05, U16 = 0x06, U32 = 0x07, U64 = 0x08,
    F32 = 0x09, F64 = 0x0A,
    Bool = 0x0B, Char = 0x0C, String = 0x0D,
    Void = 0x0E,

    /// <summary>A reference to a Types entry; a <c>uleb128</c> index follows. Assignment copies
    /// the reference, not the object. Value semantics get their own tag, so the bytecode says
    /// whether an assignment copies.</summary>
    Ref = 0x40,

    /// <summary>An array; the element type follows inline rather than as a table index, which
    /// works because an array type cannot be recursive.</summary>
    Array = 0x41,

    /// <summary>An optional (<c>?T</c>); the inner type follows inline. Not nestable: <c>??T</c>
    /// does not exist, or "no value" would be ambiguous.</summary>
    Optional = 0x42,

    /// <summary>An enum; a <c>uleb128</c> index into the Types section follows. Through an index
    /// rather than inline, because an enum has a declaration and may be recursive.</summary>
    Enum = 0x43,

    /// <summary>
    /// An interface type; a <c>uleb128</c> index into an interface entry follows.
    ///
    /// <para>A value of this type is a fat pointer: the object plus its concrete type index.</para>
    /// </summary>
    Interface = 0x44,

    /// <summary>
    /// A <c>struct</c> with value semantics; a <c>uleb128</c> index into a struct entry follows.
    ///
    /// <para>Its own tag beside <see cref="Ref"/>, so the bytecode says whether an assignment
    /// copies.</para>
    /// </summary>
    Struct = 0x45,

    /// <summary>
    /// A function value: <c>fn(A, B) -&gt; R</c>. Encoded structurally — parameter count, parameter
    /// types, return type — because it is the one composite type without a table entry: it has no
    /// declaration to hang an id on, and two identically shaped function types are the same type.
    /// </summary>
    Fn = 0x46,

    /// <summary>
    /// A host object; a <c>uleb128</c> index into the string pool with the registered type name
    /// follows.
    ///
    /// <para>Both this and <see cref="Ref"/> are references, but their layouts belong to opposite
    /// sides: a <see cref="Ref"/> layout is known to the module, a <see cref="Host"/> layout to the
    /// host.</para>
    ///
    /// <para>A host type therefore has no entry in the type table, so a field access against one is
    /// not encodable at all rather than merely forbidden.</para>
    ///
    /// <para>The name travels with it so the runtime can check at binding time that a native means
    /// the same host type; two host types are otherwise indistinguishable.</para>
    /// </summary>
    Host = 0x47,
}

/// <summary>The kind of a Types entry. The variants of an enum are <see cref="Layout"/> entries
/// themselves; the enum only names their indices.</summary>
public enum TypeKind : byte
{
    Layout = 0,
    Enum = 1,

    /// <summary>An interface. It carries no fields but the names of its method slots; the index
    /// in that list is the slot <c>callvirt</c> addresses.</summary>
    Interface = 2,

    /// <summary>
    /// A <c>struct</c>: the field layout of <see cref="Layout"/> with value semantics.
    ///
    /// <para>Its own kind rather than only a different tag at the use site, so the loader can check
    /// <c>structcopy</c> against the entry. Being a value type is a property of the declaration.
    /// </para>
    /// </summary>
    Struct = 3,
}

/// <summary>
/// Opcodes, one byte.
///
/// <para>One opcode per operation with the type as a tag byte behind it, rather than one opcode per
/// (operation × type). The tag is in the instruction stream, not in the runtime value, so dispatch
/// stays static.</para>
///
/// <para>Jump targets are block indices, not byte offsets. The function header carries the block
/// offset table, so the loader checks a target with <c>index &lt; blockCount</c> instead of
/// verifying a byte offset against instruction boundaries.</para>
/// </summary>
public enum Op : byte
{
    /// <summary><c>const &lt;type&gt; &lt;immediate&gt;</c> — the immediate depends on the type:
    /// integers as the uleb128 two's-complement bit pattern, f32/f64 as the IEEE-754 bit pattern,
    /// bool as one byte, char as a uleb128 code point, string as a uleb128 pool index.</summary>
    Const = 0x01,

    LoadLocal = 0x02,  // ldloc <uleb128 slot>
    StoreLocal = 0x03, // stloc <uleb128 slot>
    Pop = 0x04,        // discards the topmost value, such as a discarded call result

    Add = 0x10, Sub = 0x11, Mul = 0x12, Div = 0x13, Rem = 0x14,
    Shl = 0x15, Shr = 0x16, BitAnd = 0x17, BitOr = 0x18, BitXor = 0x19,

    /// <summary>Comparisons: the tag names the operand type, the result is always bool.</summary>
    Lt = 0x20, Le = 0x21, Gt = 0x22, Ge = 0x23, Eq = 0x24, Ne = 0x25,

    Neg = 0x30,
    /// <summary>Logical not, without a type tag: only bool is valid. The one exception to the tag
    /// rule.</summary>
    Not = 0x31,
    BitNot = 0x32,

    /// <summary><c>conv &lt;from&gt; &lt;to&gt;</c> — numeric to numeric only.</summary>
    Convert = 0x33,

    /// <summary><c>call &lt;uleb128 index&gt;</c> into the shared index space: imports first, then
    /// defined functions.</summary>
    Call = 0x40,

    Return = 0x41,      // ret     — void
    ReturnValue = 0x42, // retval: takes the topmost value
    Branch = 0x43,      // br <uleb128 block>
    CondBranch = 0x44,  // condbr <uleb128 ifTrue> <uleb128 ifFalse>
    Unreachable = 0x45,

    /// <summary><c>newobj &lt;uleb128 type&gt;</c> — allocates an instance with every field at its zero value.</summary>
    NewObject = 0x50,

    /// <summary><c>ldfld &lt;uleb128 type&gt; &lt;uleb128 field&gt;</c> — replaces the reference
    /// with the field value.</summary>
    LoadField = 0x51,

    /// <summary><c>stfld &lt;uleb128 type&gt; &lt;uleb128 field&gt;</c> — takes the reference and
    /// the value, with the reference below the value.
    ///
    /// <para>The type index is redundant at runtime and present so the loader can check the
    /// field index against a layout without a data-flow analysis.</para></summary>
    StoreField = 0x52,

    /// <summary><c>newarr &lt;elementType&gt; &lt;uleb128 count&gt;</c> — takes <c>count</c> values
    /// off the stack, the first element lowest, so an array literal is one instruction rather
    /// <c>count</c> Stores.</summary>
    NewArray = 0x58,

    LoadElem = 0x59,  // ldelem  — Array, Index -> Element
    StoreElem = 0x5A, // stelem: array, index, value, with the reference lowest
    ArrayLen = 0x5B,  // arrlen: the length as an i64

    /// <summary><c>arrcat</c> and <c>arrrep</c> implement <c>xs + ys</c> and <c>xs * n</c>. Each
    /// produces a new array; a <c>T[]</c> does not grow.</summary>
    ArrayConcat = 0x5C,
    ArrayRepeat = 0x5D,

    /// <summary>
    /// Optionals. <c>??</c>, <c>??=</c> and <c>?.</c> have no opcodes of their own: they evaluate
    /// their right-hand side only conditionally and therefore lower to branches over
    /// <see cref="OptIsSome"/>, like <c>&amp;&amp;</c> and <c>||</c>. An opcode would have to carry
    /// an unevaluated expression.
    /// </summary>
    OptNone = 0x60,   // optnone <innerType>
    OptSome = 0x61,   // optsome <innerType>
    OptIsSome = 0x62, // optissome
    OptGet = 0x63,    // optget: the force unwrap 'expr!', which panics on "no value"

    /// <summary>
    /// Enums. <c>match</c> has no opcode: it reads the tag with <see cref="EnumTag"/> and
    /// branches on it like any other case distinction.
    ///
    /// <para>The same shape as the optional: <c>optissome</c> tests and <c>optget</c> resolves;
    /// here <c>enumtag</c> tests and <c>enumas</c> resolves.</para>
    /// </summary>
    NewVariant = 0x68, // newvariant <uleb128 variantType>
    EnumTag = 0x69,    // enumtag
    EnumAs = 0x6A,     // enumas <uleb128 variantType>: panics on a wrong tag

    // --- Interfaces (Format 2.1) -------------------------------------------------------------

    /// <summary><c>mkiface &lt;uleb128 concreteType&gt; &lt;uleb128 interfaceType&gt;</c> — hebt
    /// an object reference to its interface type. The concrete type is known at compile time and
    /// is attached to the value, so <c>callvirt</c> finds it later.
    ///
    /// <para>Both indices are present although the runtime needs only the first: it lets the
    /// loader check the implementation relation against the Impls section without a data-flow
    /// analysis.</para></summary>
    MakeInterface = 0x70,

    /// <summary><c>callvirt &lt;uleb128 interfaceType&gt; &lt;uleb128 slot&gt;</c> — calls the
    /// implementation of the slot on the receiver's concrete type. The receiver lies lowest, as in every
    /// method call, being parameter 0.</summary>
    CallVirt = 0x71,

    // --- Structs (Format 2.2) ----------------------------------------------------------------

    /// <summary><c>structcopy &lt;uleb128 structType&gt;</c> — takes a struct value and leaves an
    /// independent copy.
    ///
    /// <para>The copy is recursive across nested structs and shallow across everything else: a
    /// field of class or array type carries a reference, and that reference is shared.</para>
    ///
    /// <para>An explicit instruction rather than an implicit copy inside <c>stloc</c>, whose
    /// meaning would otherwise depend on the type of its target slot.</para></summary>
    /// the format unambiguous — the same decision as for <c>mkiface</c>.</para></summary>
    StructCopy = 0x72,

    // --- Exceptions (Format 2.3) -------------------------------------------------------------

    /// <summary><c>throw</c> — takes the value off the stack and begins unwinding. Terminator:
    /// nothing runs after it in the block.</summary>
    Throw = 0x73,

    /// <summary><c>endfinally</c> — end of a <c>finally</c> region; unwinding continues where it
    /// was interrupted.
    ///
    /// <para>The language has no <c>finally</c>; such a region arises only from <c>defer</c>. The
    /// format needs the carrier because "runs while unwinding too" is not otherwise
    /// expressible.</para></summary>
    EndFinally = 0x74,

    // --- Globals (Format 2.4) ------------------------------------------------------------------

    /// <summary><c>ldglobal &lt;uleb128 index&gt;</c> — reads a global slot.</summary>
    LoadGlobal = 0x75,

    /// <summary><c>stglobal &lt;uleb128 index&gt;</c> — writes a global slot.
    ///
    /// <para>Only the initializer uses it: globals are <c>let</c>, so there is no writer after
    /// initialization. The opcode is general because filling a slot is a write.</para></summary>
    StoreGlobal = 0x76,

    /// <summary>
    /// Builds a closure value from a function index (the immediate) and an environment.
    /// The same shape as <see cref="MakeInterface"/> is to <see cref="CallVirt"/>, and the same
    /// runtime representation: a fat pointer of reference and index.
    /// </summary>
    MakeClosure = 0x77,

    /// <summary>
    /// Calls a closure value. The immediate is the argument count without the environment,
    /// which the runtime passes as argument 0 when one is present.
    /// </summary>
    CallIndirect = 0x78,
}
