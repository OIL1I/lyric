using Lyric.Core;
namespace Lyric.Ir;

public record struct IrLocal(LocalId Id, string Name, IrType Type);
public record struct IrTemp(TempId Id, IrType Type);

public class IrBlock(BlockId Id, List<IrOp> Insts)
{
    public BlockId Id { get; init; } = Id;
    public List<IrOp> Insts { get; init; } = Insts;

    /// <summary>The terminator sits beside <see cref="Insts"/> rather than as the last element in it:
    /// <see cref="IrOp"/> and <see cref="IrTerminator"/> are separate types, which makes "terminator
    /// in the middle of a block" unrepresentable rather than something to be checked. <c>null</c> is
    /// allowed only while building — a finished block has a terminator.</summary>
    public IrTerminator? Terminator { get; set; } = null;
}

/// <summary>
/// A function in the mid-level IR.
///
/// <para>INVARIANTS, enforced by <c>IrVerifier</c>:</para>
/// <list type="bullet">
/// <item>PARAMETER CONVENTION: the first <see cref="ParamCount"/> entries in <see cref="Locals"/> ARE
/// the parameters, in declaration order. Without it the IR carries parameter types nowhere and a
/// <c>Call</c> is not type-checkable — the verifier fetches the expected argument types exactly
/// there.</item>
/// <item>DENSE ID TABLES: <c>Locals[i].Id.Value == i</c>, likewise for <see cref="Temps"/> and
/// <see cref="Blocks"/>. The id is the slot or jump index in the later bytecode; a gap or a
/// permutation shows up as a wrong-slot read in the VM.</item>
/// <item><see cref="Entry"/> is <c>Blocks[0].Id</c> and has no predecessors — it is the only place
/// for parameter setup, and a jump back into it would repeat that.</item>
/// <item>Every temp is defined EXACTLY ONCE (SSA-light). On that rests the equivalence of
/// "available on every path" and "the definition dominates the use".</item>
/// </list>
/// </summary>
/// <summary>What a protected region does when something is thrown inside it.</summary>
public enum IrHandlerKind
{
    /// <summary>Catches one type, or everything when <c>CatchType</c> is absent, and continues.</summary>
    Catch,

    /// <summary>Runs while unwinding and hands on afterwards; the carrier of <c>defer</c>.</summary>
    Finally,
}

/// <summary>
/// A protected region: the blocks <c>[Start, End)</c> are covered.
///
/// <para>BLOCK INDICES RATHER THAN BYTE RANGES, the same decision as for the jump targets: a range is
/// checked with two comparisons against the block count, instead of verifying byte offsets against
/// instruction boundaries.</para>
///
/// <para>THE CAUGHT VALUE GOES INTO <see cref="Slot"/>, NOT ONTO THE STACK. CIL pushes it onto the
/// operand stack when entering the handler; that would not work here, because the stack is empty at
/// every block boundary. Through a slot the invariant stays intact and the handler block starts like
/// any other.</para>
/// </summary>
/// <param name="CatchType">The caught type, or <c>null</c> for a catch-all. Always <c>null</c> for
/// <see cref="IrHandlerKind.Finally"/>.</param>
/// <param name="Slot">Where the caught value goes. <c>null</c> for <c>finally</c> and for a
/// <c>catch (_)</c> without a binding.</param>
public record struct IrHandler(BlockId Start, BlockId End, IrHandlerKind Kind,
    TypeId? CatchType, BlockId Handler, LocalId? Slot);

public class IrFunction(string Name, IrType ReturnType, int ParamCount, List<IrLocal> Locals, List<IrTemp> Temps, List<IrBlock> Blocks)
{
    public string Name { get; init; } = Name;
    public IrType ReturnType { get; init; } = ReturnType;

    /// <summary>The number of parameters. The first <c>ParamCount</c> entries in
    /// <see cref="Locals"/> are those parameters, in order — see the class documentation.</summary>
    public int ParamCount { get; init; } = ParamCount;

    /// <summary>The function's named slots: first the <see cref="ParamCount"/> parameters, then the
    /// local bindings. Densely indexed through <see cref="LocalId"/>.</summary>
    public List<IrLocal> Locals { get; init; } = Locals;

    /// <summary>The authority for the type of every temp. The <c>Type</c> fields on the instructions
    /// are copies for the printer; a divergence from this is a bug.</summary>
    public List<IrTemp> Temps { get; init; } = Temps;

    /// <summary>
    /// The protected regions of this function, INNERMOST FIRST.
    ///
    /// <para>The order is the contract: while unwinding, the runtime takes the first entry whose range
    /// covers the fault site and whose type matches. For nested try blocks the list decides, not an
    /// arithmetic over range sizes.</para>
    /// </summary>
    public List<IrHandler> Handlers { get; init; } = new();

    public List<IrBlock> Blocks { get; init; } = Blocks;
    public BlockId Entry { get; set; }
}

/// <summary>A natively backed function: the signature is declared in Lyric, the implementation lives
/// in the host and is bound by <see cref="Name"/> at load time.</summary>
public record struct IrImport(string Name, IrType[] ParamTypes, IrType ReturnType);

/// <summary>
/// The layout of a composite type. <see cref="FieldNames"/> is diagnostics only — only the types
/// reach the bytecode, and the field index IS the position in <see cref="FieldTypes"/>.
///
/// <para>Both lists have the same length; the verifier enforces that, because a divergence would
/// otherwise only surface in the printer as an index exception.</para>
/// </summary>
public record struct IrTypeDef(string Name, IrType[] FieldTypes, string[] FieldNames)
{
    /// <summary>
    /// The variants when this entry is an ENUM, empty otherwise. Every variant is itself an entry with
    /// its own layout; the enum names only their ids.
    ///
    /// <para>SLOT 0 OF EVERY VARIANT IS ITS TAG, the index into this list. The payload fields start at
    /// slot 1.</para>
    /// </summary>
    public TypeId[] Variants { get; init; } = [];

    /// <summary>
    /// The method slots when this entry is an INTERFACE, empty otherwise. The INDEX in this list is the
    /// slot <c>CallVirt</c> points at; the names are in it for disassemblers and diagnostics only.
    ///
    /// <para>The order comes from the declaration rather than from a symbol table, exactly as for the
    /// fields of a class and for the same reason: the slot is a contract, the enumeration order of a
    /// map is an implementation detail.</para>
    /// </summary>
    public string[] MethodSlots { get; init; } = [];

    /// <summary>Value semantics? A <c>struct</c> has the same field layout as a class; the difference
    /// is that every binding copies. A flag rather than an entry type of its own, because nothing about
    /// the layout itself changes.</summary>
    public bool IsStruct { get; init; }

    public bool IsEnum => Variants.Length > 0;

    public bool IsInterface => MethodSlots.Length > 0;
}

/// <summary>
/// A vtable row: the class <paramref name="Type"/> satisfies the interface
/// <paramref name="Interface"/>, slot by slot with <paramref name="Methods"/>.
///
/// <para>One entry per (class, interface) pair. <c>Methods</c> is as long as the interface's slot
/// list; a default-method slot carries the interface's own function, an overridden one the class's.
/// The resolution order — own member before default — is decided in the lowering, not at
/// runtime.</para>
/// </summary>
public record struct IrImpl(TypeId Type, TypeId Interface, FunctionId[] Methods);

/// <summary>A global slot. <paramref name="Name"/> is diagnostics only — only the type reaches the
/// bytecode, and the index is the identity.</summary>
public record struct IrGlobal(string Name, IrType Type);

public class IrModule(List<IrFunction> Functions)
{
    /// <summary>The native functions this module calls, only the ones actually used.
    /// <c>CallImport</c> references them by index.</summary>
    public List<IrImport> Imports { get; init; } = new();

    /// <summary>Which capabilities this module REQUIRES. What is granted is decided by the runtime at
    /// load time; the compiler writes only the requirement.
    ///
    /// <para>It stands IN the module rather than beside it, because a '.lyrbc' can come from elsewhere:
    /// a host loading foreign bytecode has to know without the compiler what the program wants to touch.
    /// </para></summary>
    public Capability Capabilities { get; init; } = Capability.None;

    /// <summary>The layouts of the composite types. <see cref="IrRefType"/>, <c>NewObject</c>,
    /// <c>LoadField</c> and <c>StoreField</c> reference them by <see cref="TypeId"/>; densely indexed
    /// like every table here.</summary>
    public List<IrTypeDef> Types { get; init; } = new();

    /// <summary>
    /// The global slots: module <c>let</c> and <c>static let</c>. The name is diagnostics, the INDEX is
    /// the contract, as everywhere here.
    /// </summary>
    public List<IrGlobal> Globals { get; init; } = new();

    /// <summary>
    /// The function that fills all globals, or <c>null</c> when there are none.
    ///
    /// <para>A runtime calls it BEFORE the entry point. The order inside is declaration order: a global
    /// may use an earlier one, not a later one.</para>
    /// </summary>
    public FunctionId? GlobalInit { get; set; }

    /// <summary>The vtable rows. They land as the Impls section in the bytecode; the runtime builds its
    /// dispatch table from them at load time, so <c>callvirt</c> is a lookup rather than a
    /// search.</summary>
    public List<IrImpl> Impls { get; init; } = new();

    /// <summary>Call targets reference the index into this list by <see cref="FunctionId"/>. Names have
    /// to be unique: they become the symbol names in the bytecode.</summary>
    public List<IrFunction> Functions { get; init; } = Functions;

    /// <summary>The entry point (<c>main</c>), or <c>null</c> for a pure library module. It moves into
    /// the bytecode as the Start section; without it a runtime would have to guess the entry from a
    /// naming convention.</summary>
    public FunctionId? EntryFunction { get; set; }
}
