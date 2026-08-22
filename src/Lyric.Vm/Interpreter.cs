using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Vm.Debugging;

namespace Lyric.Vm;

/// <summary>
/// What runs between two instructions. The loop is generic over it with a struct constraint, so
/// the JIT emits one specialization per policy: the release one inlines an empty method into
/// NOTHING — the hot path stays byte-identical to the loop before the hook existed — and only the
/// debug specialization pays for the checks it needs.
///
/// <para>One loop source, two machine-code bodies. A second, hand-copied debug loop would be
/// silently wrong at the first new opcode.</para>
/// </summary>
internal interface IExecutionPolicy
{
    /// <summary>
    /// Whether <see cref="BeforeInstruction"/> READS the frame it is handed.
    ///
    /// <para>The interpreter keeps the operand stack pointer in a local and only writes it back
    /// where something can observe it. A policy that merely counts does not observe it; a
    /// debugger does. Static, so that specializing this method for a policy STRUCT folds the
    /// check away entirely — the release path carries no branch for it.</para>
    /// </summary>
    static abstract bool ObservesFrame { get; }

    /// <summary>Called before each instruction. <c>frame.Ip</c> still points AT the instruction
    /// about to execute; <c>frames.Count</c> is the call depth below it.</summary>
    void BeforeInstruction(Stack<Interpreter.Frame> frames, Interpreter.Frame frame);
}

/// <summary>The production policy: nothing. Every method body is empty and disappears at JIT
/// time.</summary>
internal readonly struct ReleasePolicy : IExecutionPolicy
{
    public static bool ObservesFrame => false;

    public void BeforeInstruction(Stack<Interpreter.Frame> frames, Interpreter.Frame frame) { }
}

/// <summary>The debugging policy: hands every instruction boundary to the controller, which
/// checks breakpoints, stepping and pause requests there.</summary>
internal readonly struct DebugPolicy(DebugController controller) : IExecutionPolicy
{
    /// <summary>It does: the controller renders locals and the operand stack at a stop.</summary>
    public static bool ObservesFrame => true;

    public void BeforeInstruction(Stack<Interpreter.Frame> frames, Interpreter.Frame frame) =>
        controller.OnInstruction(frames, frame);
}

/// <summary>The metered policy: one instruction, one unit, and the run stops when the host's
/// budget is spent. Its own specialization rather than a flag in the release one, so an
/// unmetered run keeps the loop it had before budgets existed.</summary>
internal readonly struct BudgetPolicy(ExecutionBudget budget) : IExecutionPolicy
{
    /// <summary>It does not: charging a unit never looks at the frame.</summary>
    public static bool ObservesFrame => false;

    public void BeforeInstruction(Stack<Interpreter.Frame> frames, Interpreter.Frame frame) =>
        budget.Charge();
}

/// <summary>
/// Executes a loaded <see cref="BytecodeModule"/>.
///
/// <para>No safety checks in the hot path: the loader validated slot and block indices, call
/// targets, stack balance and maximum depth. What remains are the failures that are not statically
/// decidable — division by zero and runaway recursion.</para>
///
/// <para>Instructions are decoded once rather than re-read from the bytes on every pass, using
/// <see cref="CodeDecoder"/>, the same decoder the validator and the disassembler use.</para>
///
/// <para>An explicit frame stack rather than .NET recursion, so the CLR stack does not bound
/// Lyric recursion and an overflow is a diagnostic rather than a process abort.</para>
///
/// <para>Frames are pooled per function: a call rents, a return or a handled unwind recycles.
/// Before the pool, the three allocations behind a frame were half of all bytes a call-heavy
/// program allocated. The pool is not thread-safe, which is the VM's own contract: one thread,
/// coroutines are state machines and hold no frame across a yield.</para>
/// </summary>
public static class Interpreter
{
    /// <summary>The depth at which recursion counts as runaway. There is no tail-call
    /// optimization, so a missing base case surfaces here.</summary>
    private const int MaxCallDepth = 1024;

    /// <summary>Runs the start function and returns its value.</summary>
    public static LyrValue Run(BytecodeModule module, NativeRegistry? natives = null) =>
        Run(module, [], natives);

    /// <param name="arguments">The program arguments. They go into the <c>string[]</c> a
    /// <c>fn main(args: string[])</c> receives; a parameterless <c>main</c> ignores them.</param>
    public static LyrValue Run(BytecodeModule module, IReadOnlyList<string> arguments,
        NativeRegistry? natives = null, Capability granted = Capability.All) =>
        LoadedProgram.Load(module, natives, granted).RunEntry(arguments);

    /// <summary>The program arguments as a Lyric <c>string[]</c>: the same representation as any
    /// other array.</summary>
    internal static LyrValue ArgumentArray(IReadOnlyList<string> arguments)
    {
        var values = new LyrValue[arguments.Count];
        for (var i = 0; i < arguments.Count; i++) values[i] = LyrValue.FromString(arguments[i]);
        return LyrValue.FromObject(values);
    }

    internal static LyrValue Execute(Prepared[] prepared, int startIndex,
        IReadOnlyList<string> strings, IReadOnlyList<BytecodeTypeDef> types,
        DispatchTable dispatch, NativeRegistry.BoundNative[] natives, LyrValue[] globals,
        ArgumentPool arguments, LyrValue[]? entryArguments = null,
        BytecodeSourceMap? sourceMap = null, DebugController? debug = null,
        ExecutionBudget? budget = null)
    {
        var frames = new Stack<Frame>();
        var frame = prepared[startIndex].Rent();

        // The entry point receives its arguments in the parameter slots, the same convention as
        // any other call.
        if (entryArguments is not null)
            for (var i = 0; i < entryArguments.Length; i++) frame.Slots[i] = entryArguments[i];

        try
        {
            // Three JIT specializations of one loop; the release one carries no trace of a hook.
            // Debugging beats metering where a caller asks for both: a session parked at a
            // breakpoint would otherwise spend a budget on standing still, and nothing in the
            // toolchain combines them — the debugger runs a program, a budget runs foreign code.
            if (debug is not null)
                return Loop(prepared, strings, types, dispatch, natives, globals, arguments, frames,
                    ref frame, new DebugPolicy(debug));

            return budget is null
                ? Loop(prepared, strings, types, dispatch, natives, globals, arguments, frames,
                    ref frame, default(ReleasePolicy))
                : Loop(prepared, strings, types, dispatch, natives, globals, arguments, frames,
                    ref frame, new BudgetPolicy(budget));
        }
        catch (LyricPanic panic) when (panic.CallStack.Count == 0)
        {
            // The backtrace is attached here rather than at the throw site: the loop holds the
            // frame stack, an arithmetic operation does not.
            var stack = new List<string> { Describe(frame, sourceMap) };
            stack.AddRange(frames.Select(f => Describe(f, sourceMap)));
            throw panic.WithCallStack(stack);
        }
    }

    /// <summary>
    /// A frame as it appears in a backtrace: the function name, and its position when the module
    /// carries a source map.
    ///
    /// <para>THE INSTRUCTION POINTER HAS ALREADY MOVED ON. The loop reads with <c>Ip++</c>, so the
    /// instruction that matters sits at <c>Ip - 1</c>. That is the faulting one in the innermost
    /// frame and the <c>call</c> in every frame below it — which is the call site, and therefore
    /// the right answer for both.</para>
    /// </summary>
    private static string Describe(Frame frame, BytecodeSourceMap? map)
    {
        var name = frame.Fn.Source.Name;
        if (map is null) return name;

        // A panic before the first instruction of a frame — a stack overflow is raised while the
        // frame is being pushed — has nothing to point at.
        var index = frame.Ip - 1;
        if (index < 0 || index >= frame.Fn.Instructions.Length) return name;

        var at = map.Locate(frame.Fn.Index, frame.Fn.Instructions[index].Offset);
        return at is null ? name : $"{name} ({at})";
    }

    private static LyrValue Loop<TPolicy>(Prepared[] prepared, IReadOnlyList<string> strings,
        IReadOnlyList<BytecodeTypeDef> types, DispatchTable dispatch,
        NativeRegistry.BoundNative[] natives, LyrValue[] globals, ArgumentPool arguments,
        Stack<Frame> frames, ref Frame frame, TPolicy policy)
        where TPolicy : struct, IExecutionPolicy
    {
        // The frame's two arrays, its instruction list and its stack pointer, in LOCALS.
        //
        // A 'frame.Push' was three dependent loads before a single value moved -- the frame
        // object, its Stack array, its Sp -- and 'slots[i]' two more. The JIT cannot hoist
        // them by itself: 'frame' is a class, and any call inside the loop could in principle
        // write through it, so every access has to be re-read. At two to four stack operations
        // per instruction, that chain of dependent loads IS the dispatch cost.
        //
        // The arrays never change while a frame runs, so they need no write-back at all. 'sp'
        // does, and every place below that changes 'frame' writes it out and reloads.
        var locals = frame.Slots;
        var stack = frame.Stack;
        var instructions = frame.Fn.Instructions;
        var sp = frame.Sp;

        while (true)
        {
            // Only a policy that READS the frame between instructions pays for the write-back,
            // and only the debugger does. 'TPolicy.ObservesFrame' is a constant once the JIT has
            // specialized this method for the policy struct, so the release path has no branch.
            if (TPolicy.ObservesFrame) frame.Sp = sp;

            policy.BeforeInstruction(frames, frame);

            ref readonly var instruction = ref instructions[frame.Ip++];

            switch (instruction.Opcode)
            {
                case Op.Const:
                    stack[sp++] = Constant(instruction, strings);
                    break;

                case Op.LoadLocal:
                    stack[sp++] = locals[(int)instruction.Immediate];
                    break;

                case Op.StoreLocal:
                    locals[(int)instruction.Immediate] = stack[--sp];
                    break;

                // As ldloc/stloc but module-wide. The index was checked at load time, so this is
                // an unchecked array access.
                case Op.LoadGlobal:
                    stack[sp++] = globals[(int)instruction.Immediate];
                    break;

                case Op.StoreGlobal:
                    globals[(int)instruction.Immediate] = stack[--sp];
                    break;

                case Op.Pop:
                    sp--;
                    break;

                case Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Rem or
                     Op.Shl or Op.Shr or Op.BitAnd or Op.BitOr or Op.BitXor:
                {
                    var rhs = stack[--sp];
                    var lhs = stack[--sp];
                    stack[sp++] = Binary(instruction.Opcode, instruction.Type, lhs, rhs);
                    break;
                }

                case Op.Lt or Op.Le or Op.Gt or Op.Ge or Op.Eq or Op.Ne:
                {
                    var rhs = stack[--sp];
                    var lhs = stack[--sp];
                    stack[sp++] = LyrValue.FromBool(Compare(instruction.Opcode, instruction.Type, lhs, rhs));
                    break;
                }

                case Op.Neg or Op.Not or Op.BitNot:
                    var unaryOperand = stack[--sp];
                    stack[sp++] = Unary(instruction.Opcode, instruction.TypeOrNull, unaryOperand);
                    break;

                case Op.Convert:
                    var convertFrom = stack[--sp];
                    stack[sp++] = Convert(instruction.Type, instruction.ToType, convertFrom);
                    break;

                case Op.Branch:
                    frame.Ip = frame.Fn.BlockStart[(int)instruction.Immediate];
                    break;

                case Op.CondBranch:
                    frame.Ip = frame.Fn.BlockStart[
                        (int)(stack[--sp].AsBool ? instruction.Immediate : instruction.Immediate2)];
                    break;

                // The fused arithmetic (3.6). Both operands are read before the destination is
                // written, which is what lets a destination be one of the sources -- 'i = i + 1'
                // is this instruction.
                case Op.BinLocals:
                    locals[instruction.SlotDest] = FusedResult(instruction,
                        locals[instruction.SlotA], locals[instruction.SlotB]);
                    break;

                case Op.BinConst:
                    locals[instruction.SlotDest] = FusedResult(instruction,
                        locals[instruction.SlotA], FusedConstant(instruction));
                    break;

                // The fused branches (3.6). Four dispatches in one, and the operands never reach
                // the operand stack: the loop test of every 'while' in the language is this
                // instruction.
                case Op.BranchCompare:
                {
                    var taken = Compare(instruction.Fused, instruction.Type,
                        locals[instruction.SlotA], locals[instruction.SlotB]);
                    frame.Ip = frame.Fn.BlockStart[
                        (int)(taken ? instruction.Immediate : instruction.Immediate2)];
                    break;
                }

                case Op.BranchCompareConst:
                {
                    var taken = Compare(instruction.Fused, instruction.Type,
                        locals[instruction.SlotA], FusedConstant(instruction));
                    frame.Ip = frame.Fn.BlockStart[
                        (int)(taken ? instruction.Immediate : instruction.Immediate2)];
                    break;
                }

                case Op.Call:
                {
                    // Shared index space: imports first, then defined functions. An import gets no
                    // frame; it runs in the host and returns immediately.
                    var index = (int)instruction.Immediate;
                    if (index < natives.Length)
                    {
                        var native = natives[index];
                        // Rented, not allocated: the buffer is loaned to the implementation for
                        // the duration of the call and recycled behind it. An implementation
                        // that throws abandons it — a lost pool entry, never a corrupt one.
                        var args = arguments.Rent(native.Arity);
                        for (var i = native.Arity - 1; i >= 0; i--) args[i] = stack[--sp];

                        var produced = native.Implementation(args);
                        arguments.Recycle(args);
                        if (native.ReturnsValue) stack[sp++] = produced;
                        break;
                    }

                    if (frames.Count >= MaxCallDepth)
                        throw new LyricPanic(VmDiagnostics.CallDepthExceeded,
                            $"call depth exceeded {MaxCallDepth} frames in '{frame.Fn.Source.Name}'");

                    var callee = prepared[index - natives.Length];
                    var next = callee.Rent();
                    // Arguments lie on the stack in call order, the first lowest.
                    for (var i = callee.Source.ParamCount - 1; i >= 0; i--) next.Slots[i] = stack[--sp];

                    frame.Sp = sp;
                    frames.Push(frame);
                    frame = next;
                    locals = frame.Slots;
                    stack = frame.Stack;
                    instructions = frame.Fn.Instructions;
                    sp = frame.Sp;
                    break;
                }

                // End of a finally region: unwinding continues where it was interrupted. On the
                // normal path this block is never entered; the defer bodies stand inline there.
                case Op.EndFinally:
                {
                    if (frame.UnwindType < 0)
                        throw new LyricRuntimeException(VmDiagnostics.UncaughtException,
                            "'endfinally' outside an unwind — a finally region was entered on the "
                            + "normal path, which the lowering never emits");

                    var pending = frame.Unwinding;
                    var pendingType = frame.UnwindType;
                    frame.Sp = sp;
                    if (!Resume(frames, ref frame, pending, pendingType))
                        throw new LyricPanic(VmDiagnostics.UncaughtException,
                            $"uncaught exception of type '{TypeName(types, pendingType)}'");

                    locals = frame.Slots;
                    stack = frame.Stack;
                    instructions = frame.Fn.Instructions;
                    sp = frame.Sp;
                    break;
                }

                case Op.Throw:
                {
                    // 0 means the type is only known at runtime; the value is then a fat pointer
                    // carrying its concrete type.
                    var thrown = stack[--sp];
                    var declared = (int)instruction.Immediate - 1;
                    var type = declared >= 0 ? declared : thrown.ConcreteType;

                    // A fresh throw: the search starts at the first handler and the throwing
                    // block.
                    frame.NextHandler = 0;
                    frame.UnwindBlock = BlockAt(frame, frame.Ip - 1);

                    frame.Sp = sp;
                    if (!Resume(frames, ref frame, thrown, type))
                        throw new LyricPanic(VmDiagnostics.UncaughtException,
                            $"uncaught exception of type '{TypeName(types, type)}'");

                    locals = frame.Slots;
                    stack = frame.Stack;
                    instructions = frame.Fn.Instructions;
                    sp = frame.Sp;
                    break;
                }

                // Value semantics. The compiler decided where to copy; here it is only copied.
                case Op.StructCopy:
                    var toCopy = stack[--sp];
                    stack[sp++] = CopyStruct(toCopy, types, (int)instruction.Immediate);
                    break;

                // An interface value is a fat pointer: the same object plus its concrete type
                // index in the unused bits. No allocation and no layout change.
                case Op.MakeInterface:
                    var boxed = stack[--sp];
                    stack[sp++] = LyrValue.FromInterface(boxed, (int)instruction.Immediate);
                    break;

                // Lowest immediate bit: is an environment on the stack? Without captures there is
                // none, and the closure is a bare function index.
                case Op.MakeClosure:
                {
                    var environment = (instruction.Immediate & 1) == 1 ? stack[--sp] : default;
                    stack[sp++] = LyrValue.FromClosure(environment, (int)(instruction.Immediate >> 1));
                    break;
                }

                // Call through a function value. The callee lies below its arguments, and its
                // environment is passed as argument 0 — the position a receiver occupies for a
                // method, so the frame is built the same way.
                case Op.CallIndirect:
                {
                    var argCount = (int)(instruction.Immediate >> 1);
                    var closure = stack[sp - 1 - (argCount)];
                    var index = closure.ClosureFunction;

                    if (index < natives.Length)
                    {
                        var native = natives[index];
                        var nativeArgs = arguments.Rent(native.Arity);
                        for (var i = native.Arity - 1; i >= 0; i--) nativeArgs[i] = stack[--sp];
                        sp--; // the closure value itself

                        var produced = native.Implementation(nativeArgs);
                        arguments.Recycle(nativeArgs);
                        if (native.ReturnsValue) stack[sp++] = produced;
                        break;
                    }

                    if (frames.Count >= MaxCallDepth)
                        throw new LyricPanic(VmDiagnostics.CallDepthExceeded,
                            $"call depth exceeded {MaxCallDepth} frames in '{frame.Fn.Source.Name}'");

                    var target = prepared[index - natives.Length];
                    var callFrame = target.Rent();

                    var offset = closure.HasEnvironment ? 1 : 0;
                    for (var i = argCount - 1; i >= 0; i--) callFrame.Slots[offset + i] = stack[--sp];

                    sp--; // the closure value
                    if (closure.HasEnvironment) callFrame.Slots[0] = LyrValue.FromObject(closure.AsObject);

                    frame.Sp = sp;
                    frames.Push(frame);
                    frame = callFrame;
                    locals = frame.Slots;
                    stack = frame.Stack;
                    instructions = frame.Fn.Instructions;
                    sp = frame.Sp;
                    break;
                }

                // The only dynamic dispatch of the language. The receiver is argument 0 and lies
                // lowest; the type it carries selects the row, the immediate the slot.
                case Op.CallVirt:
                {
                    var iface = (int)instruction.Immediate;
                    var slot = (int)instruction.Immediate2;

                    // The receiver lies below the arguments, reachable before the target is known
                    // only through the arity recorded in the table.
                    var receiver = stack[sp - 1 - (dispatch.ArityOf(iface, slot) - 1)];
                    var index = dispatch.Resolve(receiver.ConcreteType, iface, slot);

                    if (index < natives.Length)
                    {
                        var native = natives[index];
                        var args = arguments.Rent(native.Arity);
                        for (var i = native.Arity - 1; i >= 0; i--) args[i] = stack[--sp];

                        var produced = native.Implementation(args);
                        arguments.Recycle(args);
                        if (native.ReturnsValue) stack[sp++] = produced;
                        break;
                    }

                    if (frames.Count >= MaxCallDepth)
                        throw new LyricPanic(VmDiagnostics.CallDepthExceeded,
                            $"call depth exceeded {MaxCallDepth} frames in '{frame.Fn.Source.Name}'");

                    var callee = prepared[index - natives.Length];
                    var next = callee.Rent();
                    for (var i = callee.Source.ParamCount - 1; i >= 0; i--) next.Slots[i] = stack[--sp];

                    frame.Sp = sp;
                    frames.Push(frame);
                    frame = next;
                    locals = frame.Slots;
                    stack = frame.Stack;
                    instructions = frame.Fn.Instructions;
                    sp = frame.Sp;
                    break;
                }

                // An object is a slot array behind LyrValue.Ref, with no type tag in the value.
                // The loader checked that the type and field indices match, so a field access is
                // an unchecked array access.
                case Op.NewObject:
                    stack[sp++] = LyrValue.FromObject(NewInstance(types[(int)instruction.Immediate]));
                    break;

                case Op.LoadField:
                    var owner = stack[--sp];
                    stack[sp++] = owner.AsObject[(int)instruction.Immediate2];
                    break;

                case Op.StoreField:
                {
                    // The reference lies below the value, so the value comes off first.
                    var value = stack[--sp];
                    stack[--sp].AsObject[(int)instruction.Immediate2] = value;
                    break;
                }

                // Arrays share the representation of objects. The index is a runtime value and is
                // therefore checked here, unlike a field index.
                case Op.NewArray:
                {
                    var elements = new LyrValue[(int)instruction.Immediate];
                    for (var i = elements.Length - 1; i >= 0; i--) elements[i] = stack[--sp];
                    stack[sp++] = LyrValue.FromObject(elements);
                    break;
                }

                case Op.LoadElem:
                {
                    var at = stack[--sp].AsI64;
                    var array = stack[--sp].AsObject;
                    stack[sp++] = array[CheckedIndex(at, array.Length, frame)];
                    break;
                }

                case Op.StoreElem:
                {
                    var value = stack[--sp];
                    var at = stack[--sp].AsI64;
                    var array = stack[--sp].AsObject;
                    array[CheckedIndex(at, array.Length, frame)] = value;
                    break;
                }

                case Op.ArrayLen:
                    var measured = stack[--sp];
                    stack[sp++] = LyrValue.FromI64(measured.AsObject.Length);
                    break;

                case Op.ArrayConcat:
                {
                    var right = stack[--sp].AsObject;
                    var left = stack[--sp].AsObject;
                    var joined = new LyrValue[left.Length + right.Length];
                    left.CopyTo(joined, 0);
                    right.CopyTo(joined, left.Length);
                    stack[sp++] = LyrValue.FromObject(joined);
                    break;
                }

                case Op.ArrayRepeat:
                {
                    var count = stack[--sp].AsI64;
                    var source = stack[--sp].AsObject;
                    if (count < 0)
                        throw new LyricPanic(VmDiagnostics.IndexOutOfRange,
                            $"array repetition count {count} is negative");

                    var repeated = new LyrValue[source.Length * count];
                    for (var i = 0; i < count; i++) source.CopyTo(repeated, i * source.Length);
                    stack[sp++] = LyrValue.FromObject(repeated);
                    break;
                }

                // "No value" is an empty reference. For ?string, ?T[] and ?class that coincides
                // with the natural representation; only scalars need the marker LyrValue.Some
                // sets.
                case Op.OptNone:
                    stack[sp++] = LyrValue.None;
                    break;

                case Op.OptSome:
                    var wrapped = stack[--sp];
                    stack[sp++] = LyrValue.Some(wrapped);
                    break;

                case Op.OptIsSome:
                    var tested = stack[--sp];
                    stack[sp++] = LyrValue.FromBool(tested.IsSome);
                    break;

                case Op.OptGet:
                {
                    var option = stack[--sp];
                    if (!option.IsSome)
                        throw new LyricPanic(VmDiagnostics.NullDereference,
                            $"force-unwrapped a '?T' that had no value in '{frame.Fn.Source.Name}'");
                    stack[sp++] = option.Unwrap();
                    break;
                }

                // A variant is an ordinary object whose slot 0 carries its tag, so field access is
                // an ordinary ldfld and an enum needs no representation of its own.
                case Op.NewVariant:
                {
                    var layout = types[(int)instruction.Immediate];
                    var slots = new LyrValue[layout.FieldTypes.Count];
                    for (var i = slots.Length - 1; i >= 1; i--) slots[i] = stack[--sp];
                    slots[0] = LyrValue.FromI64(TagOf(types, (int)instruction.Immediate));
                    stack[sp++] = LyrValue.FromObject(slots);
                    break;
                }

                case Op.EnumTag:
                    var payload = stack[--sp];
                    stack[sp++] = payload.AsObject[0];
                    break;

                case Op.EnumAs:
                {
                    var value = stack[--sp];
                    var expected = TagOf(types, (int)instruction.Immediate);
                    if (value.AsObject[0].AsI64 != expected)
                        throw new LyricPanic(VmDiagnostics.WrongVariant,
                            $"expected variant '{types[(int)instruction.Immediate].Name}' " +
                            $"in '{frame.Fn.Source.Name}', found tag {value.AsObject[0].AsI64}");
                    stack[sp++] = value;
                    break;
                }

                case Op.Return or Op.ReturnValue:
                {
                    var result = instruction.Opcode == Op.ReturnValue ? stack[--sp] : default;
                    var returnsValue = frame.Fn.Source.ReturnType.Tag != TypeTag.Void;

                    // The result was read before the recycle clears the arrays; a LyrValue is a
                    // copy, so nothing points back into the dead frame.
                    var dead = frame;
                    if (frames.Count == 0)
                    {
                        dead.Fn.Recycle(dead);
                        return result;
                    }

                    frame = frames.Pop();
                    dead.Fn.Recycle(dead);
                    locals = frame.Slots;
                    stack = frame.Stack;
                    instructions = frame.Fn.Instructions;
                    sp = frame.Sp;
                    if (returnsValue) stack[sp++] = result;
                    break;
                }

                case Op.Unreachable:
                    throw new LyricPanic(VmDiagnostics.UnreachableExecuted,
                        $"reached an 'unreachable' instruction in '{frame.Fn.Source.Name}' — " +
                        "the compiler proved this point cannot be reached, so this is a compiler bug");

                default:
                    throw new LyricPanic(VmDiagnostics.UnreachableExecuted,
                        $"opcode {instruction.Opcode} is not implemented");
            }
        }
    }

    // ------------------------------------------------------------------ Operationen

    /// <summary>
    /// A fresh instance: one slot per field, each at the zero value of its type.
    ///
    /// <para>No field is ever uninitialized. For numbers, bool and char the zero value is the zero
    /// bit pattern; <c>string</c> needs the empty string, because <c>Ref == null</c> would
    /// otherwise read as a null reference.</para>
    /// </summary>
    /// <summary>An element index is a runtime value and is checked here, unlike the type and
    /// field indices the loader handled. A violation is a panic.</summary>
    private static int CheckedIndex(long index, int length, Frame frame)
    {
        if (index >= 0 && index < length) return (int)index;

        throw new LyricPanic(VmDiagnostics.IndexOutOfRange,
            $"index {index} is outside an array of length {length} in '{frame.Fn.Source.Name}'");
    }

    /// <summary>The tag of a variant: its index in its enum's variant list. Looked up at load
    /// time rather than carried in the bytecode.</summary>
    private static long TagOf(IReadOnlyList<BytecodeTypeDef> types, int variant)
    {
        for (var i = 0; i < types.Count; i++)
        {
            var variants = types[i].Variants;
            for (var at = 0; at < variants.Count; at++)
                if (variants[at] == variant) return at;
        }

        throw new LyricRuntimeException(VmDiagnostics.WrongVariant,
            $"type '{types[variant].Name}' is not a variant of any enum");
    }

    /// <summary>
    /// An independent copy of a <c>struct</c> value.
    ///
    /// <para>Recursive across nested structs, shallow across everything else: a field of class,
    /// array or interface type carries a reference and shares it, while a struct field is itself a
    /// value and is copied.</para>
    ///
    /// <para>The recursion terminates without cycle detection, because a struct cannot contain
    /// itself (<c>LYR-SEM0056</c>).</para>
    /// </summary>
    private static LyrValue CopyStruct(LyrValue value, IReadOnlyList<BytecodeTypeDef> types,
        int typeIndex)
    {
        if (value.Ref is not LyrValue[] source) return value;

        var type = types[typeIndex];
        var copy = new LyrValue[source.Length];
        System.Array.Copy(source, copy, source.Length);

        for (var i = 0; i < type.FieldTypes.Count && i < copy.Length; i++)
            if (type.FieldTypes[i].Tag == TypeTag.Struct)
                copy[i] = CopyStruct(copy[i], types, type.FieldTypes[i].TypeIndex);

        return LyrValue.FromObject(copy);
    }

    /// <summary>
    /// Finds the handler for a thrown value and moves control there.
    ///
    /// <para>Inside out: the handlers of the current frame first, then, after discarding the
    /// frame, those of the caller. <see cref="IrFunction.Handlers"/> is already innermost-first,
    /// so the first matching entry wins.</para>
    ///
    /// <para>The type comparison is equality, not a subtype test: the language has no inheritance.
    /// A catch-all (<c>CatchType &lt; 0</c>) matches everything.</para>
    ///
    /// <para>Returns <c>false</c> when no frame has a handler; the exception then leaves the entry
    /// point.</para>
    /// </summary>
    private static bool Resume(Stack<Frame> frames, ref Frame frame, LyrValue thrown, int type)
    {
        while (true)
        {
            for (var i = frame.NextHandler; i < frame.Fn.Handlers.Length; i++)
            {
                var handler = frame.Fn.Handlers[i];
                if (frame.UnwindBlock < handler.Start || frame.UnwindBlock >= handler.End) continue;

                // The stack is empty at every block boundary, so the intermediate values of the
                // abandoned expression are discarded here.
                frame.ClearStack();

                if (handler.IsFinally)
                {
                    // Clean up, then continue the search. The state hangs off the frame and dies
                    // with it; a second throw from the finally body replaces it.
                    frame.Unwinding = thrown;
                    frame.UnwindType = type;
                    frame.NextHandler = i + 1;
                    frame.Ip = frame.Fn.BlockStart[handler.Handler];
                    return true;
                }

                if (handler.CatchType >= 0 && handler.CatchType != type) continue;

                // A typed catch knows the type statically and takes the bare reference. A
                // catch-all binds 'Throwable', an interface type, which needs a fat pointer; only
                // this place knows the concrete type to build it from.
                if (handler.Slot >= 0)
                    frame.Slots[handler.Slot] = handler.CatchType >= 0
                        ? thrown
                        : LyrValue.FromInterface(thrown, type);
                frame.UnwindType = -1;
                frame.Ip = frame.Fn.BlockStart[handler.Handler];
                return true;
            }

            if (frames.Count == 0) return false;

            // One level out: the search restarts there, with the call site as the origin block.
            // The frame searched through is dead — a caught exception never returns into it — and
            // goes back to its pool. The 'thrown' value is a parameter copy, not a read from it.
            var dead = frame;
            frame = frames.Pop();
            dead.Fn.Recycle(dead);
            frame.NextHandler = 0;
            frame.UnwindBlock = BlockAt(frame, frame.Ip - 1);
        }
    }

    /// <summary>The block the instruction at <paramref name="index"/> belongs to. On a throw the
    /// pointer already stands past the throwing instruction.</summary>
    private static int BlockAt(Frame frame, int index)
    {
        var at = Math.Max(0, index);
        return frame.Fn.BlockOfInstruction.Length > at ? frame.Fn.BlockOfInstruction[at] : -1;
    }

    private static string TypeName(IReadOnlyList<BytecodeTypeDef> types, int index) =>
        index >= 0 && index < types.Count ? types[index].Name : $"ty{index}";

    private static LyrValue[] NewInstance(BytecodeTypeDef type)
    {
        var slots = new LyrValue[type.FieldTypes.Count];
        for (var i = 0; i < slots.Length; i++)
            if (type.FieldTypes[i].Tag == TypeTag.String)
                slots[i] = LyrValue.FromString(string.Empty);
        return slots;
    }

    /// <summary>What a fused binary form computes. A comparison leaves a bool, everything else
    /// leaves a value of the operand type — the same split the unfused pair has, decided by the
    /// operation the instruction carries rather than by its own type.</summary>
    private static LyrValue FusedResult(in VmInstruction instruction, LyrValue lhs, LyrValue rhs) =>
        instruction.Fused is Op.Lt or Op.Le or Op.Gt or Op.Ge or Op.Eq or Op.Ne
            ? LyrValue.FromBool(Compare(instruction.Fused, instruction.Type, lhs, rhs))
            : Binary(instruction.Fused, instruction.Type, lhs, rhs);

    /// <summary>
    /// The immediate of a fused constant shape, rebuilt exactly as <c>const</c> would build it: an
    /// f32 stays single precision, and an integer is brought to its width invariant.
    ///
    /// <para>A shortcut here would make <c>i &lt; 1</c> mean something else for an int8 than for
    /// an int64 — the constant arrives as 0x00..0xFF and would compare as 255 where it means -1.
    /// </para>
    /// </summary>
    private static LyrValue FusedConstant(in VmInstruction instruction) => instruction.Type switch
    {
        TypeTag.F32 => LyrValue.FromF32((float)instruction.FloatValue),
        TypeTag.F64 => LyrValue.FromF64(instruction.FloatValue),
        TypeTag.Bool => LyrValue.FromBool(instruction.BoolValue),
        _ => LyrValue.FromBits(LyrValue.Normalize(instruction.Type, instruction.ConstBits)),
    };

    private static LyrValue Constant(in VmInstruction instruction,
        IReadOnlyList<string> strings) => instruction.Type switch
    {
        TypeTag.F32 => LyrValue.FromF32((float)instruction.FloatValue),
        TypeTag.F64 => LyrValue.FromF64(instruction.FloatValue),
        TypeTag.Bool => LyrValue.FromBool(instruction.BoolValue),
        TypeTag.String => LyrValue.FromString(strings[(int)instruction.Immediate]),
        // For integers and char the immediate is already the bit pattern, but has to be brought
        // to the width invariant (i8 arrives as 0x00..0xFF).
        _ => LyrValue.FromBits(LyrValue.Normalize(instruction.Type, instruction.Immediate)),
    };

    private static LyrValue Binary(Op op, TypeTag tag, LyrValue lhs, LyrValue rhs)
    {
        if (tag == TypeTag.F64) return LyrValue.FromF64(FloatOp(op, lhs.AsF64, rhs.AsF64));
        // f32 is computed in single precision, not in double and then rounded.
        if (tag == TypeTag.F32) return LyrValue.FromF32((float)FloatOp(op, lhs.AsF32, rhs.AsF32));

        var signed = LyrValue.IsSigned(tag);
        ulong result;

        switch (op)
        {
            case Op.Add: result = unchecked(lhs.Bits + rhs.Bits); break;
            case Op.Sub: result = unchecked(lhs.Bits - rhs.Bits); break;
            case Op.Mul: result = unchecked(lhs.Bits * rhs.Bits); break;

            case Op.Div or Op.Rem:
            {
                if (rhs.Bits == 0)
                    throw new LyricPanic(VmDiagnostics.DivisionByZero,
                        op == Op.Div ? "division by zero" : "remainder by zero");

                if (signed)
                {
                    var a = lhs.AsI64;
                    var b = rhs.AsI64;
                    // MinValue / -1 overflows in two's complement and .NET throws; Lyric wraps as
                    // every other integer operation does.
                    if (b == -1) { result = op == Op.Div ? unchecked((ulong)(-a)) : 0UL; break; }
                    result = op == Op.Div ? unchecked((ulong)(a / b)) : unchecked((ulong)(a % b));
                }
                else
                {
                    result = op == Op.Div ? lhs.Bits / rhs.Bits : lhs.Bits % rhs.Bits;
                }
                break;
            }

            // The shift amount is taken modulo the operand width, not modulo 64: masking at 64
            // and normalizing afterwards would make `1 << 9` yield 0 for int8 and 2 for int64.
            case Op.Shl: result = unchecked(lhs.Bits << ShiftCount(tag, rhs.Bits)); break;
            case Op.Shr:
                result = signed
                    ? unchecked((ulong)(lhs.AsI64 >> ShiftCount(tag, rhs.Bits))) // arithmetic
                    : lhs.Bits >> ShiftCount(tag, rhs.Bits);                     // logisch
                break;

            case Op.BitAnd: result = lhs.Bits & rhs.Bits; break;
            case Op.BitOr: result = lhs.Bits | rhs.Bits; break;
            case Op.BitXor: result = lhs.Bits ^ rhs.Bits; break;

            default: throw new LyricPanic(VmDiagnostics.UnreachableExecuted,
                $"binary opcode {op} is not implemented");
        }

        return LyrValue.FromBits(LyrValue.Normalize(tag, result));
    }

    /// <summary>Shift amount modulo the operand width.</summary>
    private static int ShiftCount(TypeTag tag, ulong count) =>
        (int)(count & (ulong)(BitWidth(tag) - 1));

    private static int BitWidth(TypeTag tag) => tag switch
    {
        TypeTag.I8 or TypeTag.U8 => 8,
        TypeTag.I16 or TypeTag.U16 => 16,
        TypeTag.I32 or TypeTag.U32 => 32,
        _ => 64,
    };

    private static double FloatOp(Op op, double a, double b) => op switch
    {
        Op.Add => a + b,
        Op.Sub => a - b,
        Op.Mul => a * b,
        Op.Div => a / b,   // IEEE: division by zero yields Inf or NaN, not an error
        Op.Rem => a % b,
        _ => throw new LyricPanic(VmDiagnostics.UnreachableExecuted,
            $"opcode {op} is not valid on floating point values"),
    };

    private static bool Compare(Op op, TypeTag tag, LyrValue lhs, LyrValue rhs)
    {
        if (tag == TypeTag.String)
        {
            var equal = string.Equals(lhs.AsString, rhs.AsString, StringComparison.Ordinal);
            return op == Op.Eq ? equal : !equal;
        }

        if (LyrValue.IsFloat(tag))
        {
            double a = tag == TypeTag.F32 ? lhs.AsF32 : lhs.AsF64;
            double b = tag == TypeTag.F32 ? rhs.AsF32 : rhs.AsF64;
            return op switch
            {
                Op.Lt => a < b, Op.Le => a <= b, Op.Gt => a > b, Op.Ge => a >= b,
                Op.Eq => a == b, Op.Ne => a != b,
                _ => false,
            };
        }

        if (LyrValue.IsSigned(tag))
        {
            var a = lhs.AsI64;
            var b = rhs.AsI64;
            return op switch
            {
                Op.Lt => a < b, Op.Le => a <= b, Op.Gt => a > b, Op.Ge => a >= b,
                Op.Eq => a == b, Op.Ne => a != b,
                _ => false,
            };
        }

        // bool and char compare as unsigned integers; only eq/ne are valid, which the verifier
        // has already enforced.
        return op switch
        {
            Op.Lt => lhs.Bits < rhs.Bits, Op.Le => lhs.Bits <= rhs.Bits,
            Op.Gt => lhs.Bits > rhs.Bits, Op.Ge => lhs.Bits >= rhs.Bits,
            Op.Eq => lhs.Bits == rhs.Bits, Op.Ne => lhs.Bits != rhs.Bits,
            _ => false,
        };
    }

    private static LyrValue Unary(Op op, TypeTag? tag, LyrValue operand) => op switch
    {
        Op.Not => LyrValue.FromBool(!operand.AsBool),
        Op.Neg when tag == TypeTag.F64 => LyrValue.FromF64(-operand.AsF64),
        Op.Neg when tag == TypeTag.F32 => LyrValue.FromF32(-operand.AsF32),
        Op.Neg => LyrValue.FromBits(LyrValue.Normalize(tag!.Value, unchecked(0UL - operand.Bits))),
        Op.BitNot => LyrValue.FromBits(LyrValue.Normalize(tag!.Value, ~operand.Bits)),
        _ => throw new LyricPanic(VmDiagnostics.UnreachableExecuted,
            $"unary opcode {op} is not implemented"),
    };

    private static LyrValue Convert(TypeTag from, TypeTag to, LyrValue value)
    {
        if (LyrValue.IsInteger(from) && LyrValue.IsInteger(to))
            return LyrValue.FromBits(LyrValue.Normalize(to, value.Bits));

        if (LyrValue.IsInteger(from) && LyrValue.IsFloat(to))
        {
            var asDouble = LyrValue.IsSigned(from) ? value.AsI64 : (double)value.AsU64;
            return to == TypeTag.F32 ? LyrValue.FromF32((float)asDouble) : LyrValue.FromF64(asDouble);
        }

        if (LyrValue.IsFloat(from) && LyrValue.IsInteger(to))
            return LyrValue.FromBits(LyrValue.Normalize(to,
                FloatToInt(from == TypeTag.F32 ? value.AsF32 : value.AsF64, to)));

        // float <-> float
        var source = from == TypeTag.F32 ? value.AsF32 : value.AsF64;
        return to == TypeTag.F32 ? LyrValue.FromF32((float)source) : LyrValue.FromF64(source);
    }

    /// <summary>
    /// Float to integer: truncate towards zero, clamp outside the range, NaN to 0. This is WASM's
    /// <c>trunc_sat</c> behaviour, defined rather than left to the platform so the same
    /// <c>.lyrbc</c> gives the same result on every runtime.
    /// </summary>
    private static ulong FloatToInt(double value, TypeTag to)
    {
        if (double.IsNaN(value)) return 0;
        var truncated = Math.Truncate(value);

        if (to == TypeTag.I64)
            return truncated <= -9223372036854775808.0 ? unchecked((ulong)long.MinValue)
                 : truncated >= 9223372036854775808.0 ? unchecked((ulong)long.MaxValue)
                 : unchecked((ulong)(long)truncated);

        if (to == TypeTag.U64)
            return truncated <= 0 ? 0UL
                 : truncated >= 18446744073709551616.0 ? ulong.MaxValue
                 : (ulong)truncated;

        var (min, max) = to switch
        {
            TypeTag.I8 => (-128.0, 127.0),
            TypeTag.I16 => (-32768.0, 32767.0),
            TypeTag.I32 => (-2147483648.0, 2147483647.0),
            TypeTag.U8 => (0.0, 255.0),
            TypeTag.U16 => (0.0, 65535.0),
            TypeTag.U32 => (0.0, 4294967295.0),
            _ => (0.0, 0.0),
        };

        var clamped = Math.Clamp(truncated, min, max);
        return LyrValue.IsSigned(to) ? unchecked((ulong)(long)clamped) : (ulong)clamped;
    }

    // ------------------------------------------------------------------ Frames

    /// <summary>A function, decoded once, so a jump to a block is an array access rather than a
    /// search through the byte stream.</summary>
    internal sealed class Prepared
    {
        public required BytecodeFunction Source { get; init; }
        public required VmInstruction[] Instructions { get; init; }
        public required int[] BlockStart { get; init; }

        /// <summary>This function's index in the module. The source map is keyed by it, and a
        /// <see cref="BytecodeFunction"/> does not know where it sits.</summary>
        public required int Index { get; init; }

        /// <summary>The protected regions of this function, innermost first.</summary>
        public BytecodeHandler[] Handlers { get; init; } = [];

        /// <summary>Which block the instruction at index <c>i</c> belongs to.
        ///
        /// <para>Handler ranges are block ranges while a frame holds an instruction pointer.
        /// Building the mapping once at load time makes the handler search an array access.</para>
        /// </summary>
        public int[] BlockOfInstruction { get; init; } = [];

        public static Prepared From(BytecodeFunction function, BytecodeHandler[] handlers, int index)
        {
            var decoded = CodeDecoder.Decode(function.Code).ToArray();

            // Flattened here, once, into the interpreter's own shape -- see VmInstruction.
            var instructions = new VmInstruction[decoded.Length];
            for (var i = 0; i < decoded.Length; i++) instructions[i] = new VmInstruction(decoded[i]);
            var indexByOffset = new Dictionary<int, int>(decoded.Length);
            for (var i = 0; i < decoded.Length; i++) indexByOffset[decoded[i].Offset] = i;

            var blockStart = new int[function.BlockOffsets.Count];
            for (var b = 0; b < blockStart.Length; b++)
                blockStart[b] = indexByOffset[function.BlockOffsets[b]];

            // Inverts the block table so every instruction knows its block.
            var blockOf = new int[instructions.Length];
            for (var b = 0; b < blockStart.Length; b++)
            {
                var upTo = b + 1 < blockStart.Length ? blockStart[b + 1] : instructions.Length;
                for (var i = blockStart[b]; i < upTo; i++) blockOf[i] = b;
            }

            return new Prepared
            {
                Source = function, Instructions = instructions, BlockStart = blockStart,
                Handlers = handlers, BlockOfInstruction = blockOf, Index = index,
            };
        }

        // -------------------------------------------------------------- frame pool
        //
        // A frame and its two arrays are sized by their function, so each function keeps its own
        // free list and a rented frame always fits. The list is intrusive (Frame.Next) and
        // unbounded: its depth is bounded by the deepest simultaneous recursion ever seen, the
        // same order the CLR stack would have paid.
        //
        // A panic abandons its frames to the GC instead of recycling them — the backtrace is
        // built from them after the loop has left. That loses pool entries, never correctness;
        // the next call allocates fresh ones.

        private Frame? _free;

        /// <summary>A frame for this function, reset to its entry state. Slots and stack are
        /// zeroed — <see cref="Recycle"/> did that, so the rent path stays two reads.</summary>
        public Frame Rent()
        {
            var frame = _free;
            if (frame is null)
                return new Frame
                {
                    Fn = this,
                    Slots = new LyrValue[Source.SlotTypes.Count],
                    Stack = new LyrValue[Math.Max(Source.MaxStack, 1)],
                    Ip = BlockStart[0],
                };

            _free = frame.Next;
            frame.Next = null;
            frame.Ip = BlockStart[0];
            frame.Sp = 0;
            frame.Unwinding = default;
            frame.UnwindType = -1;
            frame.NextHandler = 0;
            frame.UnwindBlock = 0;
            return frame;
        }

        /// <summary>Takes a dead frame back. The arrays are cleared HERE rather than at rent, so
        /// a pooled frame holds no reference alive between two calls.</summary>
        public void Recycle(Frame frame)
        {
            System.Array.Clear(frame.Slots);
            System.Array.Clear(frame.Stack);
            frame.Next = _free;
            _free = frame;
        }
    }

    internal sealed class Frame
    {
        public required Prepared Fn { get; init; }
        public required LyrValue[] Slots { get; init; }
        public required LyrValue[] Stack { get; init; }
        public int Sp;
        public int Ip;

        /// <summary>The free-list link while the frame sits in its function's pool.</summary>
        public Frame? Next;

        /// <summary>The exception currently unwinding through this frame; <c>UnwindType &lt; 0</c>
        /// means none.
        ///
        /// <para>Needed only between entering a <c>finally</c> region and its <c>endfinally</c>,
        /// where ordinary code runs while the unwind is still in progress.</para></summary>
        public LyrValue Unwinding;

        public int UnwindType = -1;

        /// <summary>Which handler the search resumes at after the <c>endfinally</c>. Without the
        /// index the same finally region would find itself again.</summary>
        public int NextHandler;

        /// <summary>The block the throw came from. It survives a <c>finally</c>, because handler
        /// ranges talk about the origin.</summary>
        public int UnwindBlock;

        public void Push(LyrValue value) => Stack[Sp++] = value;
        public LyrValue Pop() => Stack[--Sp];

        /// <summary>Empties the operand stack, needed when jumping into a handler: a handler
        /// block starts empty like every other block.</summary>
        public void ClearStack() => Sp = 0;

        /// <summary>Reads without popping; <paramref name="depth"/> 0 is the top element. Used by
        /// <c>callvirt</c>, whose receiver lies below the arguments.</summary>
        public LyrValue Peek(int depth) => Stack[Sp - 1 - depth];
    }
}
