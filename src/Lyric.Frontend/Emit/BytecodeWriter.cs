using Lyric.Bytecode.Encoding;
using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Bytecode;

/// <summary>
/// <see cref="IrModule"/> → <c>.lyrbc</c>-Bytes.
///
/// <para>DETERMINISTIC: the same input produces byte-identical output. For that
/// it takes three things: the string pool is built in first-use order rather than hash order, there
/// are no timestamps, and sections appear in ascending id order.</para>
///
/// <para>The build is two-staged like the lowering: first <see cref="StackScheduler"/> per function
/// (slots, stack placement, maximum depth), then emission. The split is needed because the function
/// header carries the slot count and the maximum depth BEFORE the code.</para>
/// </summary>
public static class BytecodeWriter
{
    /// <param name="sourceMap">Where the spans point. Without it no SourceMap section is written —
    /// line numbers cannot be produced from an <see cref="IrModule"/> alone, and a caller that has
    /// no sources has nothing to say about positions.</param>
    /// <param name="debugInfo">Whether the DebugInfo section (slot names) and the Names entries no
    /// attribute row demands are written. Stripping them leaves a valid module; a debugger then
    /// shows slot indices.</param>
    public static byte[] Write(IrModule module, SourceMapContext? sourceMap = null,
        bool debugInfo = true)
    {
        var strings = new StringPool();
        var layouts = new List<FunctionLayout>(module.Functions.Count);
        var positions = sourceMap is null ? null : new SourceMapBuilder(sourceMap);

        // Names are interned first, so the start of the pool depends stably on function order.
        // Type names belong here rather than in the Types section: the string pool is section 2 and
        // is written long before section 3.
        foreach (var type in module.Types) strings.Intern(type.Name);

        // Attribute string values for the same reason: section 11 references the pool, and the pool
        // is long serialized by then.
        foreach (var row in module.Attributes)
            foreach (var value in row.Values)
                if (value.Text is not null)
                    strings.Intern(value.Text);

        foreach (var function in module.Functions)
        {
            strings.Intern(function.Name);
            layouts.Add(StackScheduler.Schedule(function));
        }

        // The code is written before the header: it fills the string pool, which is an earlier
        // section.
        var bodies = new List<byte[]>(module.Functions.Count);
        for (var i = 0; i < module.Functions.Count; i++)
            bodies.Add(WriteFunction(module.Functions[i], layouts[i], strings, module.Imports.Count,
                positions));

        // The file names go into the pool here, for the same reason the type names do above: the
        // Strings section is serialized below, long before section 6 is written.
        positions?.InternNames(strings.Intern);

        // Slot names per function and global names, or null when debug info is stripped or
        // nothing is named. A list's length always matches the slot table it describes; a
        // compiler-created slot carries the empty string, which is the section's statement that a
        // person never bound it.
        var slotNames = debugInfo ? CollectSlotNames(module, layouts, strings) : null;
        var globalNames = debugInfo ? CollectGlobalNames(module, strings) : null;

        var writer = new ByteWriter();
        writer.Raw(Format.Magic);
        writer.U16(Format.VersionMajor);
        writer.U16(Format.VersionMinor);

        // What the program REQUIRES. What is granted is decided by the runtime at load time; only
        // the requirement is recorded here, in the module, so a host can judge foreign bytecode
        // without the compiler.
        WriteSection(writer, SectionId.Capabilities, s => s.ULeb((ulong)module.Capabilities));

        WriteSection(writer, SectionId.Strings, s =>
        {
            s.ULeb(strings.Count);
            foreach (var value in strings.InOrder) s.String(value);
        });

        // Must precede Imports and Functions: section ids ascend, and both may name reference types in
        // their signatures.
        if (module.Types.Count > 0)
            WriteSection(writer, SectionId.Types, s =>
            {
                s.ULeb(module.Types.Count);
                foreach (var type in module.Types)
                {
                    s.ULeb(strings.Intern(type.Name));
                    s.U8((byte)(type.IsEnum ? TypeKind.Enum
                        : type.IsInterface ? TypeKind.Interface
                        : type.IsStruct ? TypeKind.Struct
                        : TypeKind.Layout));

                    if (type.IsInterface)
                    {
                        // An interface carries no fields but slot names. They stand in the bytecode —
                        // unlike field names — because a disassembler could otherwise only show
                        // 'ty3#1', and a third-party runtime would have nothing to bind host
                        // implementations by.
                        s.ULeb(type.MethodSlots.Length);
                        foreach (var slot in type.MethodSlots) s.String(slot);
                        continue;
                    }

                    if (type.IsEnum)
                    {
                        // An enum carries no fields of its own; its variants do.
                        s.ULeb(type.Variants.Length);
                        foreach (var variant in type.Variants) s.ULeb(variant.Value);
                        continue;
                    }

                    s.ULeb(type.FieldTypes.Length);
                    foreach (var field in type.FieldTypes) WriteType(s, field);
                }
            });

        // Symbolic: name and signature, bound at load time. No addresses.
        WriteSection(writer, SectionId.Imports, s =>
        {
            s.ULeb(module.Imports.Count);
            foreach (var import in module.Imports)
            {
                s.String(import.Name);
                s.ULeb(import.ParamTypes.Length);
                foreach (var type in import.ParamTypes) WriteType(s, type);
                WriteType(s, import.ReturnType);
            }
        });

        WriteSection(writer, SectionId.Functions, s =>
        {
            s.ULeb(bodies.Count);
            foreach (var body in bodies) s.Raw(body);
        });

        // Strippable, and left out entirely when nothing was recorded: SourceMap is 6, between
        // Functions (5) and Start (7).
        if (positions is { IsEmpty: false })
            WriteSection(writer, SectionId.SourceMap, s => positions.WritePayload(s, strings.Intern));

        // Absent for library modules. Has to come after Functions: section ids ascend.
        //
        // The index runs into the SHARED space (imports first, then functions), the same one 'call'
        // uses.
        if (module.EntryFunction is { } entry)
            WriteSection(writer, SectionId.Start,
                s => s.ULeb(module.Imports.Count + entry.Value));

        // Interface implementations, last because section ids ascend strictly and Impls (8) comes
        // after Start (7).
        if (module.Impls.Count > 0)
            WriteSection(writer, SectionId.Impls, s =>
            {
                s.ULeb(module.Impls.Count);
                foreach (var impl in module.Impls)
                {
                    s.ULeb(impl.Type.Value);
                    s.ULeb(impl.Interface.Value);
                    s.ULeb(impl.Methods.Length);
                    // The function index in the SHARED space (imports first, then functions), the same
                    // as for 'call' and for the Start section. An import as a vtable entry is
                    // expressible; whether a runtime accepts one is its own business.
                    foreach (var method in impl.Methods)
                        s.ULeb(module.Imports.Count + method.Value);
                }
            });


        // The protected regions. Handlers is 9: after Impls (8) and before Globals (10), because
        // section ids ascend strictly.
        var handlers = module.Functions
            .SelectMany((fn, index) => fn.Handlers.Select(h => (Function: index, Handler: h)))
            .ToList();

        if (handlers.Count > 0)
            WriteSection(writer, SectionId.Handlers, s =>
            {
                s.ULeb(handlers.Count);
                foreach (var (function, h) in handlers)
                {
                    s.ULeb(function);
                    s.ULeb(h.Start.Value);
                    s.ULeb(h.End.Value);
                    s.U8((byte)(h.Kind == IrHandlerKind.Finally ? 1 : 0));
                    // -1 as "no type" or "no slot": written as uleb128 0, the real index
                    // incremented by one. A separate presence byte would cost a byte for the same
                    // statement.
                    s.ULeb(h.CatchType is { } t ? (ulong)(t.Value + 1) : 0UL);
                    s.ULeb(h.Handler.Value);
                    s.ULeb(h.Slot is { } slot ? (ulong)(slot.Value + 1) : 0UL);
                }
            });

        // The global slots together with their init function. Globals is 10 and therefore comes
        // last of all.
        if (module.Globals.Count > 0)
            WriteSection(writer, SectionId.Globals, s =>
            {
                s.ULeb(module.Globals.Count);
                foreach (var global in module.Globals) WriteType(s, global.Type);

                // 0 means no initializer; otherwise the index in the shared space, incremented.
                s.ULeb(module.GlobalInit is { } init
                    ? (ulong)(module.Imports.Count + init.Value + 1)
                    : 0UL);
            });

        if (module.Attributes.Count > 0)
            WriteSection(writer, SectionId.Attributes, s =>
            {
                s.ULeb(module.Attributes.Count);
                foreach (var row in module.Attributes)
                {
                    s.U8((byte)row.TargetKind);
                    s.ULeb(row.Target);
                    s.ULeb(row.Type.Value);
                    s.ULeb(row.Values.Length);
                    foreach (var value in row.Values)
                        WriteAttributeValue(s, strings, value);
                }
            });

        // Field names. The types an attribute row references are the REQUIRED entries: a host
        // reading '@Component struct Health' needs 'value' and 'max', or it has learned a shape it
        // cannot name. With debug info on, every named type joins them — a debugger expanding an
        // object needs the same names. Ascending by type index, so the output is deterministic.
        var referenced = new SortedSet<int>();
        foreach (var row in module.Attributes)
        {
            referenced.Add(row.Type.Value);
            if (row.TargetKind == IrAttributeTarget.Type) referenced.Add(row.Target);
        }
        if (debugInfo)
            for (var i = 0; i < module.Types.Count; i++) referenced.Add(i);
        referenced.RemoveWhere(t => module.Types[t].FieldNames.Length == 0);

        if (referenced.Count > 0)
            WriteSection(writer, SectionId.Names, s =>
            {
                s.ULeb(referenced.Count);
                foreach (var typeIndex in referenced)
                {
                    s.ULeb(typeIndex);
                    var names = module.Types[typeIndex].FieldNames;
                    s.ULeb(names.Length);
                    foreach (var name in names) s.String(name);
                }
            });

        // The slot and global names. Left out entirely when nothing anywhere is named — a module
        // of spilled temps only would carry a section saying nothing.
        if (slotNames is not null || globalNames is not null)
            WriteSection(writer, SectionId.DebugInfo, s =>
            {
                s.ULeb(module.Functions.Count);
                for (var f = 0; f < module.Functions.Count; f++)
                {
                    var names = slotNames?[f] ?? [];
                    s.ULeb(names.Length);
                    foreach (var name in names) s.ULeb(strings.Intern(name));
                }

                s.ULeb(globalNames?.Length ?? 0);
                foreach (var name in globalNames ?? []) s.ULeb(strings.Intern(name));
            });

        // The opaque names, for the types that got field names above and have an opaque field
        // among them. An opaque alias IS its underlying type everywhere below the sema, so a host
        // reading '@Saved class Holder { hero: Entity, stage: int }' sees two i64 and cannot
        // refuse the one that must not be saved. This section is the only trace it leaves — and it
        // is last because the ids ascend, not because anything reads it late.
        var opaque = new SortedSet<int>(referenced);
        opaque.RemoveWhere(t => module.Types[t].FieldOpaqueNames.Length == 0);

        if (opaque.Count > 0)
            WriteSection(writer, SectionId.OpaqueFields, s =>
            {
                s.ULeb(opaque.Count);
                foreach (var typeIndex in opaque)
                {
                    s.ULeb(typeIndex);
                    var names = module.Types[typeIndex].FieldOpaqueNames;
                    s.ULeb(names.Length);
                    foreach (var name in names) s.String(name);
                }
            });

        return writer.ToArray();
    }

    /// <summary>The global slots' names, or <c>null</c> when there are none or none is visible —
    /// the same rules as for locals.</summary>
    private static string[]? CollectGlobalNames(IrModule module, StringPool strings)
    {
        if (module.Globals.Count == 0) return null;

        var named = false;
        var names = new string[module.Globals.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = Visible(module.Globals[i].Name);
            named |= names[i].Length != 0;
        }

        if (!named) return null;
        foreach (var name in names) strings.Intern(name);
        return names;
    }

    /// <summary>A source-derived name survives; a compiler-created one (<c>$…</c> from the
    /// lowering, <c>__inl_…</c> from the inliner) becomes the empty string.</summary>
    private static string Visible(string name) =>
        name.StartsWith('$') || name.StartsWith("__inl_", StringComparison.Ordinal) ? "" : name;

    /// <summary>
    /// The name of every slot of every function: the IR local's name where the slot is one, the
    /// empty string for spilled temps and for locals the compiler made (<c>$…</c> from the
    /// lowering, <c>__inl_…</c> from the inliner). A scalar-replacement name (<c>v.x</c>) is kept:
    /// it derives from a source binding, and showing the pieces is what makes the replaced
    /// variable inspectable at all.
    ///
    /// <para>Returns <c>null</c> when not a single slot is named; the section is then left out.
    /// Per function the list is either empty (nothing named there) or full length, the same
    /// either-or the format demands. The names are interned HERE because the Strings section is
    /// serialized long before section 13 is written.</para>
    /// </summary>
    private static string[][]? CollectSlotNames(IrModule module,
        IReadOnlyList<FunctionLayout> layouts, StringPool strings)
    {
        var perFunction = new string[module.Functions.Count][];
        var anything = false;

        for (var f = 0; f < module.Functions.Count; f++)
        {
            var function = module.Functions[f];
            var slotCount = layouts[f].SlotTypes.Count;

            var named = false;
            var names = new string[slotCount];
            for (var slot = 0; slot < slotCount; slot++)
            {
                names[slot] = slot < function.Locals.Count
                    ? Visible(function.Locals[slot].Name)
                    : "";
                named |= names[slot].Length != 0;
            }

            perFunction[f] = named ? names : [];
            anything |= named;
        }

        if (!anything) return null;

        foreach (var names in perFunction)
            foreach (var name in names)
                strings.Intern(name);
        return perFunction;
    }

    /// <summary>One attribute value: the field's tag, then the payload in the encoding the
    /// <c>const</c> opcode uses — integers and chars as uleb of the widened bits, floats as their
    /// IEEE pattern, bool one byte, a string through the pool.</summary>
    private static void WriteAttributeValue(ByteWriter s, StringPool strings, IrAttributeValue value)
    {
        var tag = TagOf(value.Type);
        s.Tag(tag);
        switch (tag)
        {
            case TypeTag.String:
                s.ULeb(strings.Intern(value.Text!)); // pre-interned above, so no new pool entry
                break;
            case TypeTag.Bool:
                s.U8((byte)value.Bits);
                break;
            // The IR carries every float as the DOUBLE pattern; the field's tag decides the width
            // on disk, the same narrowing the const opcode applies.
            case TypeTag.F32:
                s.F32((float)BitConverter.UInt64BitsToDouble(value.Bits));
                break;
            case TypeTag.F64:
                s.F64(BitConverter.UInt64BitsToDouble(value.Bits));
                break;
            case TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64
                or TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64
                or TypeTag.Char:
                s.ULeb(value.Bits);
                break;
            // An enum value is its VARIANT TAG — the index in the enum's variant list, the same
            // number slot 0 of a variant carries at runtime. Which enum is not repeated here: the
            // field's own type says it, and the reader resolves the name from there (3.4).
            case TypeTag.Enum:
                s.ULeb(value.Bits);
                break;
            default:
                throw new InternalCompilationException(
                    $"bytecode: attribute value of type {tag} is not encodable");
        }
    }

    /// <summary>A section is id, byte length, content. The length lets a reader skip an unknown
    /// section, which is the mechanism behind a strippable source map.</summary>
    private static void WriteSection(ByteWriter writer, SectionId id, Action<ByteWriter> body)
    {
        var payload = new ByteWriter();
        body(payload);
        var bytes = payload.ToArray();

        writer.U8((byte)id);
        writer.ULeb(bytes.Length);
        writer.Raw(bytes);
    }

    private static byte[] WriteFunction(IrFunction function, FunctionLayout layout, StringPool strings,
        int importCount, SourceMapBuilder? positions)
    {
        var code = new ByteWriter();
        var blockOffsets = new int[function.Blocks.Count];

        // Every function opens its own row list, including one that ends up empty: the section
        // carries one entry per function and finds them by position.
        positions?.BeginFunction();

        foreach (var block in function.Blocks)
        {
            blockOffsets[block.Id.Value] = code.Position;

            // Which runs of operations become one instruction (3.6). The plan is computed per
            // block and consulted here; an empty plan emits exactly what this loop emitted before.
            var plan = Fusion.Of(function, block, layout);

            for (var i = 0; i < block.Insts.Count; i++)
            {
                // Recorded BEFORE the instruction, so the slot loads that belong to it fall under
                // its position rather than under the one before. A fusion takes the position of
                // the FIRST operation it replaces, which is where the expression starts and the
                // line a debugger stops on.
                positions?.At(code.Position, block.Insts[i].Span);

                if (plan.At.TryGetValue(i, out var fused))
                {
                    WriteFused(code, strings, fused);
                    i += fused.Consumed - 1;
                    continue;
                }

                WriteOp(code, function, layout, strings, block.Insts[i], importCount);
            }

            if (plan.EndsBlock) continue; // the last fusion stands for the terminator too

            positions?.At(code.Position, block.Terminator!.Span);
            WriteTerminator(code, layout, block.Terminator!);
        }

        var codeBytes = code.ToArray();
        var writer = new ByteWriter();

        writer.ULeb(strings.Intern(function.Name));
        writer.ULeb(function.ParamCount);
        WriteType(writer, function.ReturnType);

        writer.ULeb(layout.SlotTypes.Count);
        foreach (var type in layout.SlotTypes) WriteType(writer, type);

        writer.ULeb(layout.MaxStack);

        writer.ULeb(blockOffsets.Length);
        foreach (var offset in blockOffsets) writer.ULeb(offset);

        writer.ULeb(codeBytes.Length);
        writer.Raw(codeBytes);
        return writer.ToArray();
    }

    private static void WriteOp(ByteWriter code, IrFunction function, FunctionLayout layout,
        StringPool strings, IrOp op, int importCount)
    {
        LoadSlotOperands(code, layout, IrShape.OperandsOf(op));

        switch (op)
        {
            case Const c:
                code.Opcode(Op.Const);
                code.Tag(TagOf(c.Type));
                WriteConstImmediate(code, strings, c);
                break;

            case BinOp b:
                code.Opcode(BinOpcode(b.Kind));
                // The tag names the OPERAND type. For a comparison b.Type is bool, but the VM has
                // to know what it compares: i64 and u64 are different machine operations.
                code.Tag(TagOf(function.Temps[b.Lhs.Value].Type));
                break;

            case UnOp u:
                switch (u.Kind)
                {
                    case IrUnKind.Neg: code.Opcode(Op.Neg); code.Tag(TagOf(u.Type)); break;
                    case IrUnKind.BitNot: code.Opcode(Op.BitNot); code.Tag(TagOf(u.Type)); break;
                    case IrUnKind.Not: code.Opcode(Op.Not); break; // bool only, so no tag is needed
                    default: throw new InternalCompilationException($"bytecode: unknown unop {u.Kind}");
                }
                break;

            case Lyric.Ir.Convert cv: // qualified: it collides with System.Convert
                code.Opcode(Op.Convert);
                code.Tag(TagOf(cv.From));
                code.Tag(TagOf(cv.To));
                break;

            case LoadLocal l:
                code.Opcode(Op.LoadLocal);
                code.ULeb(l.Local.Value); // the IR LocalId is the slot index; the first n slots are the locals
                break;

            case StoreLocal s:
                code.Opcode(Op.StoreLocal);
                code.ULeb(s.Local.Value);
                break;

            case CallImport k:
                // A shared index space: imports first, then functions. The arithmetic sits here,
                // because the convention lives here; the IR keeps the two apart deliberately.
                code.Opcode(Op.Call);
                code.ULeb(k.Target.Value);
                break;

            case Call k:
                code.Opcode(Op.Call);
                code.ULeb(importCount + k.Target.Value);
                break;

            case NewObject n:
                code.Opcode(Op.NewObject);
                code.ULeb(n.Type.Value);
                break;

            case LoadField f:
                code.Opcode(Op.LoadField);
                code.ULeb(f.Type.Value);
                code.ULeb(f.Field.Value);
                break;

            case StoreField f:
                code.Opcode(Op.StoreField);
                code.ULeb(f.Type.Value);
                code.ULeb(f.Field.Value);
                break;

            case NewArray a:
                code.Opcode(Op.NewArray);
                WriteType(code, a.Element);
                code.ULeb(a.Elements.Length);
                break;

            case OptNone n:
                code.Opcode(Op.OptNone);
                WriteType(code, n.Inner);
                break;

            case OptSome s:
                code.Opcode(Op.OptSome);
                WriteType(code, s.Inner);
                break;

            case OptIsSome: code.Opcode(Op.OptIsSome); break;
            case OptGet: code.Opcode(Op.OptGet); break;

            case NewVariant v:
                code.Opcode(Op.NewVariant);
                code.ULeb(v.Variant.Value);
                break;

            case EnumTag: code.Opcode(Op.EnumTag); break;

            case MakeInterface m:
                code.Opcode(Op.MakeInterface);
                code.ULeb(m.Concrete.Value);
                code.ULeb(m.Interface.Value);
                break;

            case CallVirt c:
                code.Opcode(Op.CallVirt);
                code.ULeb(c.Interface.Value);
                code.ULeb(c.Slot);
                break;

            case StructCopy c:
                code.Opcode(Op.StructCopy);
                code.ULeb(c.Type.Value);
                break;

            case MakeClosure m:
                code.Opcode(Op.MakeClosure);
                // The target index in the shared call index space (imports first, then functions),
                // the same arithmetic as for 'call'. The LOWEST BIT says whether an environment is
                // on the stack: a reader must know the stack effect at load time, and a closure
                // without captures has none.
                code.ULeb(((ulong)(importCount + m.Target.Value) << 1)
                          | (m.Environment is null ? 0UL : 1UL));
                break;

            case CallIndirect c:
                code.Opcode(Op.CallIndirect);
                // Argument count without the callee; the lowest bit says whether it yields a value.
                // The same encoding as mkclosure and for the same reason: 'call' has both in its
                // target signature, and here there is none.
                code.ULeb(((ulong)c.Args.Length << 1) | (c.Dest is null ? 0UL : 1UL));
                break;

            case LoadGlobal l:
                code.Opcode(Op.LoadGlobal);
                code.ULeb(l.Global.Value);
                break;

            case StoreGlobal g:
                code.Opcode(Op.StoreGlobal);
                code.ULeb(g.Global.Value);
                break;

            case EnumAs a:
                code.Opcode(Op.EnumAs);
                code.ULeb(a.Variant.Value);
                break;

            case LoadElem: code.Opcode(Op.LoadElem); break;
            case StoreElem: code.Opcode(Op.StoreElem); break;
            case ArrayLen: code.Opcode(Op.ArrayLen); break;
            case ArrayConcat: code.Opcode(Op.ArrayConcat); break;
            case ArrayRepeat: code.Opcode(Op.ArrayRepeat); break;

            default:
                throw new InternalCompilationException($"bytecode: unhandled op {op.GetType().Name}");
        }

        StoreOrDiscardDest(code, layout, IrShape.DestOf(op));
    }

    private static void WriteTerminator(ByteWriter code, FunctionLayout layout, IrTerminator terminator)
    {
        LoadSlotOperands(code, layout, IrShape.OperandsOf(terminator));

        switch (terminator)
        {
            case Return r:
                code.Opcode(r.Value is null ? Op.Return : Op.ReturnValue);
                break;
            case Branch b:
                code.Opcode(Op.Branch);
                code.ULeb(b.Target.Value);
                break;
            case CondBranch c:
                code.Opcode(Op.CondBranch);
                code.ULeb(c.IfTrue.Value);
                code.ULeb(c.IfFalse.Value);
                break;
            case Unreachable:
                code.Opcode(Op.Unreachable);
                break;

            case Throw t:
                code.Opcode(Op.Throw);
                // 0 means the type is only known at runtime; the real index is incremented.
                code.ULeb(t.Concrete is { } thrown ? (ulong)(thrown.Value + 1) : 0UL);
                break;

            case EndFinally:
                code.Opcode(Op.EndFinally);
                break;
            default:
                throw new InternalCompilationException(
                    $"bytecode: unhandled terminator {terminator.GetType().Name}");
        }
    }

    /// <summary>Operands held in slots reach the stack through an <c>ldloc</c>. The scheduler
    /// guarantees that either ALL operands are already on the stack or NONE are; a mix would not be
    /// emittable, because an <c>ldloc</c> above an operand already there would destroy the
    /// order.</summary>
    private static void LoadSlotOperands(ByteWriter code, FunctionLayout layout,
        IReadOnlyList<TempId> operands)
    {
        if (operands.Count == 0) return;
        if (layout.Placements[operands[0]] == Placement.Stack) return;

        foreach (var operand in operands)
        {
            code.Opcode(Op.LoadLocal);
            code.ULeb(layout.TempSlots[operand]);
        }
    }

    private static void StoreOrDiscardDest(ByteWriter code, FunctionLayout layout, TempId? dest)
    {
        if (dest is not { } temp) return;

        switch (layout.Placements[temp])
        {
            case Placement.Stack:
                break; // it stays; the next instruction consumes it
            case Placement.Slot:
                code.Opcode(Op.StoreLocal);
                code.ULeb(layout.TempSlots[temp]);
                break;
            case Placement.Discard:
                code.Opcode(Op.Pop);
                break;
        }
    }

    /// <summary>
    /// One fused instruction (3.6). The operands are slots and block indices, so nothing here
    /// touches the operand stack — which is the whole saving: the four instructions this replaces
    /// spent three of their dispatches moving a value onto the stack and off it again.
    /// </summary>
    private static void WriteFused(ByteWriter code, StringPool strings, FusedInstruction fused)
    {
        code.Opcode(fused.Opcode);
        code.U8((byte)fused.Kind);
        code.Tag(fused.Type);

        // The arithmetic forms name their destination first; the branches have none and go
        // straight to their operands.
        if (fused.SlotDest >= 0) code.ULeb(fused.SlotDest);
        code.ULeb(fused.SlotA);

        if (fused.Constant is { } constant) WriteScalarImmediate(code, strings, fused.Type, constant);
        else code.ULeb(fused.SlotB);

        if (fused.Opcode is Op.BranchCompare or Op.BranchCompareConst)
        {
            code.ULeb(fused.IfTrue);
            code.ULeb(fused.IfFalse);
        }
    }

    private static void WriteConstImmediate(ByteWriter code, StringPool strings, Const constant) =>
        WriteScalarImmediate(code, strings, TagOf(constant.Type), constant.Value);

    /// <summary>
    /// A constant's bytes, by the tag it is written under.
    ///
    /// <para>One encoding for constants wherever they stand — in a <c>const</c> instruction or
    /// inside a fused form. Two would eventually disagree about the one case that distinguishes
    /// them, which is the single-precision float.</para>
    /// </summary>
    private static void WriteScalarImmediate(ByteWriter code, StringPool strings, TypeTag tag,
        IrConstValue value)
    {
        switch (value)
        {
            // Two's complement, zero-extended to 64 bits, the same encoding as in the IR.
            case IntConst i: code.ULeb(i.Value); break;
            case FloatConst f when tag == TypeTag.F32: code.F32((float)f.Value); break;
            case FloatConst f: code.F64(f.Value); break;
            case BoolConst b: code.U8(b.Value ? (byte)1 : (byte)0); break;
            case CharConst c: code.ULeb((ulong)c.CodePoint); break;
            case StringConst s: code.ULeb(strings.Intern(s.Value)); break;
            default:
                throw new InternalCompilationException(
                    $"bytecode: unhandled const {value.GetType().Name}");
        }
    }

    private static Op BinOpcode(IrBinKind kind) => kind switch
    {
        IrBinKind.Add => Op.Add,
        IrBinKind.Sub => Op.Sub,
        IrBinKind.Mul => Op.Mul,
        IrBinKind.Div => Op.Div,
        IrBinKind.Rem => Op.Rem,
        IrBinKind.Shl => Op.Shl,
        IrBinKind.Shr => Op.Shr,
        IrBinKind.BitAnd => Op.BitAnd,
        IrBinKind.BitOr => Op.BitOr,
        IrBinKind.BitXor => Op.BitXor,
        IrBinKind.Lt => Op.Lt,
        IrBinKind.Le => Op.Le,
        IrBinKind.Gt => Op.Gt,
        IrBinKind.Ge => Op.Ge,
        IrBinKind.Eq => Op.Eq,
        IrBinKind.Ne => Op.Ne,
        _ => throw new InternalCompilationException($"bytecode: unknown binop {kind}")
    };

    /// <summary>A type in the bytecode: the tag, and for a composite the index behind it. The only
    /// place types are written; a tag alone is not a complete type, and a forgotten site would
    /// shift the stream by a byte.</summary>
    internal static void WriteType(ByteWriter w, IrType type)
    {
        w.Tag(TagOf(type));
        if (type is IrRefType r) w.ULeb(r.Type.Value);
        // The element type is inline and recursive: int[][] is 0x41 0x41 0x04.
        if (type is IrArrayType a) WriteType(w, a.Element);
        if (type is IrOptionalType o) WriteType(w, o.Inner);
        if (type is IrEnumType e) w.ULeb(e.Type.Value);
        if (type is IrInterfaceType i) w.ULeb(i.Type.Value);
        if (type is IrStructType v) w.ULeb(v.Type.Value);

        // The name is INLINE rather than a string-pool index: 'WriteType' is static and does not
        // know the pool. The same choice as for 'Fn', the other
        // composite type without a table entry.
        if (type is IrHostType h) w.String(h.Name);

        // Structural: the parameter count, the parameter types and the return type. The only composite
        // type without a table entry — it has no declaration to hang an id on.
        if (type is IrFunctionType f)
        {
            w.ULeb(f.Parameters.Length);
            foreach (var parameter in f.Parameters) WriteType(w, parameter);
            WriteType(w, f.Return);
        }
    }

    internal static TypeTag TagOf(IrType type) => type switch
    {
        IrScalarType s => s.Kind switch
        {
            IrScalar.I8 => TypeTag.I8,
            IrScalar.I16 => TypeTag.I16,
            IrScalar.I32 => TypeTag.I32,
            IrScalar.I64 => TypeTag.I64,
            IrScalar.U8 => TypeTag.U8,
            IrScalar.U16 => TypeTag.U16,
            IrScalar.U32 => TypeTag.U32,
            IrScalar.U64 => TypeTag.U64,
            IrScalar.F32 => TypeTag.F32,
            IrScalar.F64 => TypeTag.F64,
            IrScalar.Bool => TypeTag.Bool,
            IrScalar.Char => TypeTag.Char,
            IrScalar.String => TypeTag.String,
            IrScalar.Void => TypeTag.Void,
            _ => throw new InternalCompilationException($"bytecode: unknown scalar {s.Kind}")
        },
        IrRefType => TypeTag.Ref,
        IrArrayType => TypeTag.Array,
        IrOptionalType => TypeTag.Optional,
        IrEnumType => TypeTag.Enum,
        IrInterfaceType => TypeTag.Interface,
        IrStructType => TypeTag.Struct,
        IrFunctionType => TypeTag.Fn,
        IrHostType => TypeTag.Host,
        _ => throw new InternalCompilationException(
            $"bytecode: type not encodable: {type.GetType().Name}")
    };

    /// <summary>The constant pool for strings, in first-use order so the output is deterministic.
    /// </summary>
    private sealed class StringPool
    {
        private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
        private readonly List<string> _values = new();

        public int Count => _values.Count;
        public IReadOnlyList<string> InOrder => _values;

        public int Intern(string value)
        {
            if (_indices.TryGetValue(value, out var existing)) return existing;
            var index = _values.Count;
            _indices[value] = index;
            _values.Add(value);
            return index;
        }
    }
}
