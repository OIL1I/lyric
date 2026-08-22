using Lyric.Bytecode.Encoding;
using Lyric.Core;

namespace Lyric.Bytecode;

/// <summary>
/// <c>.lyrbc</c> bytes to a <see cref="BytecodeModule"/>, validated completely at load time.
///
/// <para>A module is checked once while loading and runs afterwards without safety checks. Every
/// failure here is a reason not to accept the module, so the reader stops at the first finding
/// rather than collecting: in a broken file the second is usually a consequence of the first.
/// </para>
///
/// <para>This is where untrusted bytes enter the system. No input may produce a .NET exception,
/// only a <c>LYR-BC####</c> diagnostic.</para>
/// </summary>
public static class BytecodeReader
{
    /// <summary>Reads and validates. Returns <c>null</c> and reports <c>LYR-BC####</c> when the
    /// file is not a valid module.</summary>
    public static BytecodeModule? Read(byte[] bytes, DiagnosticEngine de)
    {
        try
        {
            return ReadOrThrow(bytes);
        }
        catch (MalformedBytecodeException ex)
        {
            // No span: the failure is in a binary file, and such diagnostics render without a
            // position line.
            de.Report(ex.Code, Severity.Error, default, ex.Message);
            return null;
        }
    }

    public static BytecodeModule ReadOrThrow(byte[] bytes)
    {
        var reader = new ByteReader(bytes);
        reader.ExpectMagic();

        var major = reader.U16();
        var minor = reader.U16();
        if (major != Format.VersionMajor)
            throw new MalformedBytecodeException(BytecodeDiagnostics.UnsupportedVersion,
                $"bytecode major version {major} is not supported (this build reads {Format.VersionMajor})");

        ulong capabilities = 0;
        IReadOnlyList<string> strings = Array.Empty<string>();
        IReadOnlyList<BytecodeTypeDef> types = Array.Empty<BytecodeTypeDef>();
        IReadOnlyList<BytecodeImport> imports = Array.Empty<BytecodeImport>();
        IReadOnlyList<BytecodeFunction> functions = Array.Empty<BytecodeFunction>();
        int? start = null;
        IReadOnlyList<BytecodeImpl> impls = Array.Empty<BytecodeImpl>();
        IReadOnlyList<BytecodeHandler> handlers = Array.Empty<BytecodeHandler>();
        IReadOnlyList<BytecodeType> globals = Array.Empty<BytecodeType>();
        int? globalInit = null;
        BytecodeSourceMap? sourceMap = null;
        IReadOnlyList<BytecodeAttribute> attributes = Array.Empty<BytecodeAttribute>();
        IReadOnlyList<BytecodeFieldNames> fieldNames = Array.Empty<BytecodeFieldNames>();
        IReadOnlyList<BytecodeOpaqueFields> opaqueFields = Array.Empty<BytecodeOpaqueFields>();
        IReadOnlyList<IReadOnlyList<string>>? slotNames = null;
        IReadOnlyList<string> globalNames = Array.Empty<string>();

        var previousId = -1;
        while (!reader.AtEnd)
        {
            var id = reader.U8();
            var length = reader.ULebAsCount();
            var payload = new ByteReader(reader.Raw(length));

            // Ascending and at most once, which lets a reader work in a single pass.
            if (id <= previousId)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"section id {id} is out of order (previous was {previousId})");
            previousId = id;

            switch ((SectionId)id)
            {
                case SectionId.Capabilities: capabilities = payload.ULeb(); break;
                case SectionId.Strings: strings = ReadStrings(payload); break;
                case SectionId.Types: types = ReadTypes(payload, strings); break;
                case SectionId.Imports: imports = ReadImports(payload); break;
                case SectionId.Functions: functions = ReadFunctions(payload, strings); break;
                // Reads after Functions, which the ids guarantee: the rows are checked against the
                // code they point into.
                case SectionId.SourceMap: sourceMap = ReadSourceMap(payload, strings, functions); break;
                case SectionId.Start: start = payload.ULebAsCount(); break;
                case SectionId.Impls: impls = ReadImpls(payload); break;
                case SectionId.Handlers: handlers = ReadHandlers(payload); break;
                case SectionId.Globals:
                {
                    var count = payload.ULebAsCount();
                    var slots = new List<BytecodeType>(Math.Min(count, 4096));
                    for (var i = 0; i < count; i++) slots.Add(ReadType(payload));
                    globals = slots;

                    var init = payload.ULebAsCount();
                    globalInit = init == 0 ? null : init - 1;
                    break;
                }
                // Both read after Types and Functions, which the ascending ids guarantee: every
                // index a row carries is checked against the table it points into.
                case SectionId.Attributes:
                    attributes = ReadAttributes(payload, strings, types, functions.Count);
                    break;
                case SectionId.Names: fieldNames = ReadFieldNames(payload, types); break;
                case SectionId.OpaqueFields: opaqueFields = ReadOpaqueFields(payload, types); break;
                // Reads after Functions and Globals, which the ids guarantee: each name list is
                // checked against the slot table it describes.
                case SectionId.DebugInfo:
                    (slotNames, globalNames) =
                        ReadDebugInfo(payload, strings, functions, globals.Count);
                    break;
                // Unknown or reserved: skipped, which is what the length is for. The payload has to
                // be consumed rather than merely ignored, or the trailing-byte check below rejects
                // exactly the section it is meant to let through — and with it the forward
                // compatibility a new minor version rests on.
                default:
                    payload.Skip(payload.Remaining);
                    break;
            }

            if (!payload.AtEnd)
                throw new MalformedBytecodeException(BytecodeDiagnostics.Truncated,
                    $"section {id} has {payload.Remaining} trailing byte(s)");
        }

        var module = new BytecodeModule
        {
            VersionMajor = major,
            VersionMinor = minor,
            Capabilities = capabilities,
            Strings = strings,
            Types = types,
            Imports = imports,
            Functions = functions,
            Start = start,
            Impls = impls,
            Handlers = handlers,
            Globals = globals,
            GlobalInit = globalInit,
            SourceMap = sourceMap,
            Attributes = attributes,
            FieldNames = fieldNames,
            OpaqueFields = opaqueFields,
            SlotNames = slotNames,
            GlobalNames = globalNames,
        };

        Validate(module);
        return module;
    }

    /// <summary>
    /// The SourceMap section: a file table, then one row list per function.
    ///
    /// <para>Everything it points at is checked here, so a consumer can index without guarding: the
    /// file names against the pool, the row count against the function count, and every offset
    /// against the code it claims to describe.</para>
    /// </summary>
    /// <summary>
    /// Section 11: attribute rows. Everything a row points at is validated here, so a consumer can
    /// index without guarding — the same contract as for the source map.
    /// </summary>
    private static IReadOnlyList<BytecodeAttribute> ReadAttributes(ByteReader payload,
        IReadOnlyList<string> strings, IReadOnlyList<BytecodeTypeDef> types, int functionCount)
    {
        var count = payload.ULebAsCount();
        var rows = new List<BytecodeAttribute>(Math.Min(count, 1024));
        var seen = new HashSet<(byte, int, int)>();

        for (var i = 0; i < count; i++)
        {
            var kind = payload.U8();
            var target = payload.ULebAsCount();
            var type = payload.ULebAsCount();

            if (kind > (byte)AttributeTargetKind.Module)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"attribute row {i}: unknown target kind {kind}");

            switch ((AttributeTargetKind)kind)
            {
                case AttributeTargetKind.Function when target >= functionCount:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"attribute row {i}: function target {target} is out of range");
                case AttributeTargetKind.Type when target >= types.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"attribute row {i}: type target {target} is out of range");
                // The module is the file; a nonzero index would be a second meaning for the field.
                case AttributeTargetKind.Module when target != 0:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                        $"attribute row {i}: a module target carries index 0, not {target}");
            }

            if (type >= types.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"attribute row {i}: attribute type {type} is out of range");
            var def = types[type];
            if (!def.IsStruct)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"attribute row {i}: '{def.Name}' is not a struct — an attribute always is");

            if (!seen.Add((kind, target, type)))
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"attribute row {i}: '{def.Name}' sits on the same target twice");

            // The row is complete by contract: one value per field, in field order, each tagged
            // with the FIELD's type. Anything else and the position no longer names the field.
            var valueCount = payload.ULebAsCount();
            if (valueCount != def.FieldTypes.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"attribute row {i}: {valueCount} value(s) for {def.FieldTypes.Count} field(s) "
                    + $"of '{def.Name}'");

            var values = new List<BytecodeConstValue>(valueCount);
            for (var v = 0; v < valueCount; v++)
            {
                var value = ReadAttributeValue(payload, strings, types, def.FieldTypes[v], i);
                if (value.Tag != def.FieldTypes[v].Tag)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                        $"attribute row {i}: value {v} is {value.Tag}, field {v} of '{def.Name}' "
                        + $"is {def.FieldTypes[v].Tag}");
                values.Add(value);
            }

            rows.Add(new BytecodeAttribute
            {
                TargetKind = (AttributeTargetKind)kind, Target = target, Type = type,
                Values = values,
            });
        }
        return rows;
    }

    /// <param name="fieldType">The type of the field this value fills. Only the enum case needs
    /// it, and it needs it for both halves of the answer: which variants exist, and what the one
    /// named here is called.</param>
    private static BytecodeConstValue ReadAttributeValue(ByteReader payload,
        IReadOnlyList<string> strings, IReadOnlyList<BytecodeTypeDef> types,
        BytecodeType fieldType, int row)
    {
        var tag = payload.Tag();
        switch (tag)
        {
            // New in 3.4. The payload is the variant's tag; the enum is the field's own type, so
            // the name resolves here rather than in every consumer: a host reading a row gets
            // 'Stage.Physics' in Text and the tag in Bits, and needs to know neither table.
            case TypeTag.Enum:
            {
                var variant = payload.ULebAsCount();
                if (fieldType.Tag != TypeTag.Enum || fieldType.TypeIndex >= types.Count)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                        $"attribute row {row}: an enum value fills no enum field");

                var declaration = types[fieldType.TypeIndex];
                if (variant >= declaration.Variants.Count)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"attribute row {row}: variant {variant} is outside "
                        + $"'{declaration.Name}', which has {declaration.Variants.Count}");

                var entry = declaration.Variants[variant];
                if (entry >= types.Count)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"attribute row {row}: variant {variant} of '{declaration.Name}' points "
                        + $"outside the type table");

                return new BytecodeConstValue(tag)
                {
                    Bits = (ulong)variant,
                    Text = types[entry].Name,
                };
            }

            case TypeTag.String:
            {
                var index = payload.ULebAsCount();
                if (index >= strings.Count)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"attribute row {row}: string index {index} is out of the pool");
                return new BytecodeConstValue(tag) { Text = strings[index] };
            }
            case TypeTag.Bool:
                return new BytecodeConstValue(tag) { Bits = payload.U8() != 0 ? 1UL : 0UL };
            // F32 widens on read, so a consumer sees every float as the same 64-bit pattern.
            case TypeTag.F32:
                return new BytecodeConstValue(tag)
                {
                    Bits = BitConverter.DoubleToUInt64Bits(payload.F32()),
                };
            case TypeTag.F64:
                return new BytecodeConstValue(tag)
                {
                    Bits = BitConverter.DoubleToUInt64Bits(payload.F64()),
                };
            case TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64
                or TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64
                or TypeTag.Char:
                return new BytecodeConstValue(tag) { Bits = payload.ULeb() };
            default:
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"attribute row {row}: a value of type {tag} is not a literal");
        }
    }

    /// <summary>Section 12: field names, one entry per referenced type, count matching the
    /// layout. The names are inline rather than pooled, like an import's name: they occur
    /// once.</summary>
    private static IReadOnlyList<BytecodeFieldNames> ReadFieldNames(ByteReader payload,
        IReadOnlyList<BytecodeTypeDef> types)
    {
        var count = payload.ULebAsCount();
        var entries = new List<BytecodeFieldNames>(Math.Min(count, 1024));
        var seen = new HashSet<int>();

        for (var i = 0; i < count; i++)
        {
            var type = payload.ULebAsCount();
            if (type >= types.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"names entry {i}: type {type} is out of range");
            if (!seen.Add(type))
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"names entry {i}: type {type} appears twice");

            var nameCount = payload.ULebAsCount();
            if (nameCount != types[type].FieldTypes.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"names entry {i}: {nameCount} name(s) for {types[type].FieldTypes.Count} "
                    + $"field(s) of '{types[type].Name}'");

            var names = new List<string>(nameCount);
            for (var n = 0; n < nameCount; n++) names.Add(payload.String());
            entries.Add(new BytecodeFieldNames { Type = type, Names = names });
        }
        return entries;
    }

    /// <summary>
    /// Section 14: the opaque type name per field, in field order. Shaped exactly like the Names
    /// section and checked exactly like it — a partial list would name the wrong fields.
    ///
    /// <para>An empty string is the normal case and means "this field's type is not opaque". It
    /// costs a byte per field of a type that has at least one, which is the price of keeping the
    /// position the index.</para>
    /// </summary>
    private static IReadOnlyList<BytecodeOpaqueFields> ReadOpaqueFields(ByteReader payload,
        IReadOnlyList<BytecodeTypeDef> types)
    {
        var count = payload.ULebAsCount();
        var entries = new List<BytecodeOpaqueFields>(Math.Min(count, 1024));
        var seen = new HashSet<int>();

        for (var i = 0; i < count; i++)
        {
            var type = payload.ULebAsCount();
            if (type >= types.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"opaque names entry {i}: type {type} is out of range");
            if (!seen.Add(type))
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"opaque names entry {i}: type {type} appears twice");

            var nameCount = payload.ULebAsCount();
            if (nameCount != types[type].FieldTypes.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"opaque names entry {i}: {nameCount} name(s) for "
                    + $"{types[type].FieldTypes.Count} field(s) of '{types[type].Name}'");

            var names = new List<string>(nameCount);
            for (var n = 0; n < nameCount; n++) names.Add(payload.String());
            entries.Add(new BytecodeOpaqueFields { Type = type, Names = names });
        }
        return entries;
    }

    /// <summary>Section 13: one name list per function, then the global names. A count is either
    /// 0 or exactly the slot count it describes — the position IS the slot index, and a partial
    /// list would name the wrong slots.</summary>
    private static (IReadOnlyList<IReadOnlyList<string>>, IReadOnlyList<string>) ReadDebugInfo(
        ByteReader payload, IReadOnlyList<string> strings,
        IReadOnlyList<BytecodeFunction> functions, int globalCount)
    {
        var functionCount = payload.ULebAsCount();
        if (functionCount != functions.Count)
            throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                $"debug info covers {functionCount} function(s), the module has {functions.Count}");

        var perFunction = new List<IReadOnlyList<string>>(functionCount);
        for (var f = 0; f < functionCount; f++)
        {
            var nameCount = payload.ULebAsCount();
            if (nameCount == 0)
            {
                perFunction.Add([]);
                continue;
            }

            if (nameCount != functions[f].SlotTypes.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"debug info for '{functions[f].Name}' carries {nameCount} name(s) for " +
                    $"{functions[f].SlotTypes.Count} slot(s)");

            var names = new List<string>(nameCount);
            for (var i = 0; i < nameCount; i++)
            {
                var index = payload.ULebAsCount();
                if (index >= strings.Count)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"debug info for '{functions[f].Name}' names string {index}, " +
                        $"the pool holds {strings.Count}");
                names.Add(strings[index]);
            }
            perFunction.Add(names);
        }

        var globalNameCount = payload.ULebAsCount();
        if (globalNameCount != 0 && globalNameCount != globalCount)
            throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                $"debug info carries {globalNameCount} global name(s) for {globalCount} global(s)");

        var globalNames = new List<string>(globalNameCount);
        for (var i = 0; i < globalNameCount; i++)
        {
            var index = payload.ULebAsCount();
            if (index >= strings.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"debug info names string {index} for global {i}, " +
                    $"the pool holds {strings.Count}");
            globalNames.Add(strings[index]);
        }

        return (perFunction, globalNames);
    }

    private static BytecodeSourceMap ReadSourceMap(ByteReader payload, IReadOnlyList<string> strings,
        IReadOnlyList<BytecodeFunction> functions)
    {
        var fileCount = payload.ULebAsCount();
        var files = new List<string>(Math.Min(fileCount, 1024));
        for (var i = 0; i < fileCount; i++)
        {
            var index = payload.ULebAsCount();
            if (index >= strings.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"source map file {i} names string {index}, the pool holds {strings.Count}");
            files.Add(strings[index]);
        }

        var functionCount = payload.ULebAsCount();
        if (functionCount != functions.Count)
            throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                $"source map covers {functionCount} function(s), the module has {functions.Count}");

        var perFunction = new List<IReadOnlyList<BytecodeSourceRow>>(functionCount);
        for (var f = 0; f < functionCount; f++)
        {
            var rowCount = payload.ULebAsCount();
            var rows = new List<BytecodeSourceRow>(Math.Min(rowCount, 1024));
            var codeLength = functions[f].Code.Length;
            var offset = 0;

            for (var i = 0; i < rowCount; i++)
            {
                var delta = payload.ULebAsCount();

                // Only the first row may sit at the offset it starts from; afterwards a zero delta
                // would put two positions on one byte, and the bisection in Locate assumes an
                // ascent.
                if (i > 0 && delta == 0)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"source map row {i} of function {f} repeats offset {offset}");

                offset += delta;

                var fileIndex = payload.ULebAsCount();
                if (fileIndex >= files.Count)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"source map row {i} of function {f} names file {fileIndex} of {files.Count}");

                var line = payload.ULebAsCount();

                // A row marks where an instruction BEGINS, so the offset has to lie inside the code
                // rather than merely not past its end.
                if (offset >= codeLength)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"source map row {i} of function {f} is at offset {offset}, "
                        + $"outside its {codeLength}-byte code");

                rows.Add(new BytecodeSourceRow(offset, fileIndex, line));
            }

            perFunction.Add(rows);
        }

        return new BytecodeSourceMap { Files = files, Functions = perFunction };
    }

    private static IReadOnlyList<string> ReadStrings(ByteReader payload)
    {
        var count = payload.ULebAsCount();
        var values = new List<string>(Math.Min(count, 1024));
        for (var i = 0; i < count; i++) values.Add(payload.String());
        return values;
    }

    /// <summary>A type: the tag, followed by the type index for a reference. The only place types
    /// are read; the counterpart of <c>BytecodeWriter.WriteType</c>.</summary>
    private static BytecodeType ReadType(ByteReader payload)
    {
        var tag = payload.Tag();
        // Ref, Enum and Interface carry a table index; Array and Optional carry their element
        // type inline. A tag missed here shifts the stream by bytes.
        if (tag is TypeTag.Ref or TypeTag.Enum or TypeTag.Interface or TypeTag.Struct)
            return new BytecodeType(tag, payload.ULebAsCount());
        // A host type carries its name inline and no table index: it has no layout. The name is
        // enough to check at binding time that module and runtime mean the same type.
        if (tag is TypeTag.Host)
            return new BytecodeType(tag, -1) { HostName = payload.String() };
        // fn(A, B) -> R carries its signature inline: parameter count, parameter types, return.
        if (tag is TypeTag.Fn)
        {
            var count = payload.ULebAsCount();
            var parameters = new List<BytecodeType>(Math.Min(count, 256));
            for (var i = 0; i < count; i++) parameters.Add(ReadType(payload));
            return new BytecodeType(tag, -1) { Parameters = parameters, Element = ReadType(payload) };
        }

        // The element type is inline and recursive.
        if (tag is TypeTag.Array or TypeTag.Optional)
        {
            var inner = ReadType(payload);
            // ??T does not exist: the runtime marks "no value" by an empty reference, which
            // carries one level only.
            if (tag == TypeTag.Optional && inner.Tag == TypeTag.Optional)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    "nested optional '??T' — optionals do not nest");

            return new BytecodeType(tag, -1) { Element = inner };
        }
        return BytecodeType.Scalar(tag);
    }

    /// <summary>
    /// The type table. Range checks on field references happen in <c>Validate</c>: a type may name
    /// itself and later types, so while reading a field the final table size is not known.
    /// </summary>
    private static IReadOnlyList<BytecodeTypeDef> ReadTypes(ByteReader payload, IReadOnlyList<string> strings)
    {
        var count = payload.ULebAsCount();
        var types = new List<BytecodeTypeDef>(Math.Min(count, 1024));

        for (var i = 0; i < count; i++)
        {
            var nameIndex = payload.ULebAsCount();
            if (nameIndex >= strings.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"type {i}: name index {nameIndex} is outside the string pool ({strings.Count})");

            var kind = payload.U8();
            if (kind > (byte)TypeKind.Struct)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"type {i}: unknown kind {kind}");

            if (kind == (byte)TypeKind.Interface)
            {
                var slotCount = payload.ULebAsCount();
                if (slotCount == 0)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                        $"interface '{strings[nameIndex]}' declares no methods; there would be "
                        + "nothing to dispatch on");

                var slots = new List<string>(Math.Min(slotCount, 1024));
                for (var m = 0; m < slotCount; m++) slots.Add(payload.String());
                types.Add(new BytecodeTypeDef
                {
                    Name = strings[nameIndex], FieldTypes = [], MethodSlots = slots,
                });
                continue;
            }

            var isStruct = kind == (byte)TypeKind.Struct;

            if (kind == (byte)TypeKind.Enum)
            {
                var variantCount = payload.ULebAsCount();
                var variants = new List<int>(Math.Min(variantCount, 1024));
                for (var v = 0; v < variantCount; v++) variants.Add(payload.ULebAsCount());
                types.Add(new BytecodeTypeDef
                {
                    Name = strings[nameIndex], FieldTypes = [], Variants = variants,
                });
                continue;
            }

            var fieldCount = payload.ULebAsCount();
            var fields = new List<BytecodeType>(Math.Min(fieldCount, 1024));
            for (var f = 0; f < fieldCount; f++)
            {
                var type = ReadType(payload);
                // void has no width and no zero value; it is not a value.
                if (type.Tag == TypeTag.Void)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                        $"type '{strings[nameIndex]}': field {f} is void");
                fields.Add(type);
            }

            types.Add(new BytecodeTypeDef
            {
                Name = strings[nameIndex], FieldTypes = fields, IsStruct = isStruct,
            });
        }

        return types;
    }

    private static IReadOnlyList<BytecodeImport> ReadImports(ByteReader payload)
    {
        var count = payload.ULebAsCount();
        var imports = new List<BytecodeImport>(Math.Min(count, 1024));
        for (var i = 0; i < count; i++)
        {
            var name = payload.String();
            var paramCount = payload.ULebAsCount();
            var parameters = new List<BytecodeType>(Math.Min(paramCount, 256));
            for (var p = 0; p < paramCount; p++) parameters.Add(ReadType(payload));
            imports.Add(new BytecodeImport
            {
                Name = name, ParamTypes = parameters, ReturnType = ReadType(payload),
            });
        }
        return imports;
    }

    private static IReadOnlyList<BytecodeFunction> ReadFunctions(ByteReader payload,
        IReadOnlyList<string> strings)
    {
        var count = payload.ULebAsCount();
        var functions = new List<BytecodeFunction>(Math.Min(count, 4096));

        for (var i = 0; i < count; i++)
        {
            var nameIndex = payload.ULebAsCount();
            if (nameIndex >= strings.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"function {i}: name index {nameIndex} is outside the string pool ({strings.Count})");

            var paramCount = payload.ULebAsCount();
            var returnType = ReadType(payload);

            var slotCount = payload.ULebAsCount();
            var slotTypes = new List<BytecodeType>(Math.Min(slotCount, 4096));
            for (var s = 0; s < slotCount; s++) slotTypes.Add(ReadType(payload));

            if (paramCount > slotCount)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"function '{strings[nameIndex]}': {paramCount} parameters but only {slotCount} slots");

            var maxStack = payload.ULebAsCount();

            var blockCount = payload.ULebAsCount();
            var blockOffsets = new List<int>(Math.Min(blockCount, 4096));
            for (var b = 0; b < blockCount; b++) blockOffsets.Add(payload.ULebAsCount());

            var codeLength = payload.ULebAsCount();
            functions.Add(new BytecodeFunction
            {
                Name = strings[nameIndex],
                ParamCount = paramCount,
                ReturnType = returnType,
                SlotTypes = slotTypes,
                MaxStack = maxStack,
                BlockOffsets = blockOffsets,
                Code = payload.Raw(codeLength),
            });
        }

        return functions;
    }

    /// <summary>The Impls section: per row the type, the interface, the slot count and the
    /// function indices.</summary>
    private static IReadOnlyList<BytecodeImpl> ReadImpls(ByteReader payload)
    {
        var count = payload.ULebAsCount();
        var impls = new List<BytecodeImpl>(Math.Min(count, 4096));

        for (var i = 0; i < count; i++)
        {
            var type = payload.ULebAsCount();
            var iface = payload.ULebAsCount();
            var slotCount = payload.ULebAsCount();
            var methods = new List<int>(Math.Min(slotCount, 1024));
            for (var m = 0; m < slotCount; m++) methods.Add(payload.ULebAsCount());
            impls.Add(new BytecodeImpl { Type = type, Interface = iface, Methods = methods });
        }

        return impls;
    }

    /// <summary>The Handlers section: per row the function, block range, kind, type, handler and
    /// slot.</summary>
    private static IReadOnlyList<BytecodeHandler> ReadHandlers(ByteReader payload)
    {
        var count = payload.ULebAsCount();
        var handlers = new List<BytecodeHandler>(Math.Min(count, 4096));

        for (var i = 0; i < count; i++)
        {
            var function = payload.ULebAsCount();
            var start = payload.ULebAsCount();
            var end = payload.ULebAsCount();
            var kind = payload.U8();
            var catchType = payload.ULebAsCount();
            var handler = payload.ULebAsCount();
            var slot = payload.ULebAsCount();

            if (kind > 1)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"handler {i}: unknown kind {kind}");

            handlers.Add(new BytecodeHandler
            {
                Function = function, Start = start, End = end, Kind = kind,
                // 0 means none; the real index is stored incremented by one.
                CatchType = catchType - 1, Handler = handler, Slot = slot - 1,
            });
        }

        return handlers;
    }

    /// <summary>Checks that need the whole module: call targets need other functions' signatures,
    /// jump targets need the block count.</summary>
    private static void Validate(BytecodeModule module)
    {
        if (module.Start is { } start
            && (start < 0 || start >= module.Imports.Count + module.Functions.Count))
            throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                $"start function {start} is outside the callable index space " +
                $"({module.Imports.Count + module.Functions.Count})");

        ValidateTypeReferences(module);
        ValidateImpls(module);
        ValidateHandlers(module);
        ValidateGlobals(module);

        foreach (var function in module.Functions)
        {
            if (function.BlockOffsets.Count == 0)
                throw new MalformedBytecodeException(BytecodeDiagnostics.Truncated,
                    $"function '{function.Name}' has no blocks");

            var instructions = CodeDecoder.Decode(function.Code);
            var byOffset = instructions.ToDictionary(i => i.Offset);

            foreach (var offset in function.BlockOffsets)
                if (!byOffset.ContainsKey(offset))
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}': block offset {offset} is not an instruction boundary");

            ValidateOperands(module, function, instructions);
            ValidateStack(module, function, instructions);
        }
    }

    /// <summary>Every reference in a signature or layout points into the type table. Checked over
    /// all types at once, because forward and self references are allowed.</summary>
    private static void ValidateTypeReferences(BytecodeModule module)
    {
        void Check(BytecodeType type, string where)
        {
            // An array carries its element type inline; a reference inside it points into the
            // table like a direct one.
            while ((type.IsArray || type.IsOptional) && type.Element is { } inner) type = inner;

            if (type.IsRef && (type.TypeIndex < 0 || type.TypeIndex >= module.Types.Count))
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"{where}: type index {type.TypeIndex} is outside {module.Types.Count} type(s)");
        }

        for (var i = 0; i < module.Types.Count; i++)
        {
            for (var f = 0; f < module.Types[i].FieldTypes.Count; f++)
                Check(module.Types[i].FieldTypes[f], $"type '{module.Types[i].Name}' field {f}");

            // A variant must be a layout and must have a tag slot, or ldfld and enumas would read
            // against a layout that does not exist.
            foreach (var variant in module.Types[i].Variants)
            {
                if (variant < 0 || variant >= module.Types.Count)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"enum '{module.Types[i].Name}': variant index {variant} is outside " +
                        $"{module.Types.Count} type(s)");

                if (module.Types[variant].IsEnum || module.Types[variant].FieldTypes.Count == 0)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                        $"enum '{module.Types[i].Name}': variant {variant} is not a layout with a tag slot");
            }
        }

        foreach (var import in module.Imports)
        {
            for (var p = 0; p < import.ParamTypes.Count; p++)
                Check(import.ParamTypes[p], $"import '{import.Name}' parameter {p}");
            Check(import.ReturnType, $"import '{import.Name}' return type");
        }

        foreach (var function in module.Functions)
        {
            for (var s = 0; s < function.SlotTypes.Count; s++)
                Check(function.SlotTypes[s], $"function '{function.Name}' slot {s}");
            Check(function.ReturnType, $"function '{function.Name}' return type");
        }
    }

    private static void ValidateOperands(BytecodeModule module, BytecodeFunction function,
        IReadOnlyList<BytecodeInstruction> instructions)
    {
        var callable = module.Imports.Count + module.Functions.Count;

        foreach (var instruction in instructions)
        {
            ValidateArithmeticTag(function, instruction);

            switch (instruction.Opcode)
            {
                case Op.LoadLocal or Op.StoreLocal when instruction.Immediate >= (ulong)function.SlotTypes.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: local slot " +
                        $"{instruction.Immediate} is outside {function.SlotTypes.Count} slot(s)");

                case Op.Call when instruction.Immediate >= (ulong)callable:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: call target " +
                        $"{instruction.Immediate} is outside {callable} callable(s)");

                case Op.Const when instruction.Type == TypeTag.String
                                   && instruction.Immediate >= (ulong)module.Strings.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: string index " +
                        $"{instruction.Immediate} is outside the pool ({module.Strings.Count})");

                // The type and field indices are checked here so a field access at runtime is an
                // unchecked array access.
                case Op.NewObject or Op.LoadField or Op.StoreField or Op.NewVariant or Op.EnumAs
                    or Op.MakeInterface or Op.CallVirt or Op.StructCopy
                    when instruction.Immediate >= (ulong)module.Types.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: type index " +
                        $"{instruction.Immediate} is outside {module.Types.Count} type(s)");

                case Op.LoadField or Op.StoreField
                    when instruction.Immediate2 >= (ulong)module.Types[(int)instruction.Immediate].FieldTypes.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: field index " +
                        $"{instruction.Immediate2} is outside type " +
                        $"'{module.Types[(int)instruction.Immediate].Name}' " +
                        $"({module.Types[(int)instruction.Immediate].FieldTypes.Count} field(s))");

                // mkiface: the second immediate must be an interface with an Impls row for
                // exactly this pair, or the resulting value would dispatch nowhere.
                case Op.MakeInterface
                    when instruction.Immediate2 >= (ulong)module.Types.Count
                         || !module.Types[(int)instruction.Immediate2].IsInterface:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: mkiface target " +
                        $"{instruction.Immediate2} is not an interface");

                case Op.MakeInterface
                    when !module.Impls.Any(i => i.Type == (int)instruction.Immediate
                                                && i.Interface == (int)instruction.Immediate2):
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: " +
                        $"'{module.Types[(int)instruction.Immediate].Name}' has no impl row for " +
                        $"'{module.Types[(int)instruction.Immediate2].Name}'");

                case Op.CallVirt when !module.Types[(int)instruction.Immediate].IsInterface:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: callvirt target " +
                        $"{instruction.Immediate} is not an interface");

                case Op.CallVirt
                    when instruction.Immediate2 >=
                         (ulong)module.Types[(int)instruction.Immediate].MethodSlots.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: callvirt slot " +
                        $"{instruction.Immediate2} is outside interface " +
                        $"'{module.Types[(int)instruction.Immediate].Name}'");

                // A structcopy on a reference type would silently copy a slot array that is meant
                // to be shared.
                case Op.StructCopy when !module.Types[(int)instruction.Immediate].IsStruct:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: structcopy targets " +
                        $"'{module.Types[(int)instruction.Immediate].Name}', which is not a struct");

                // The target index sits from bit 1 upward; the lowest bit says whether an
                // environment is on the stack.
                case Op.MakeClosure
                    when (instruction.Immediate >> 1) >=
                         (ulong)(module.Imports.Count + module.Functions.Count):
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: mkclosure target " +
                        $"{instruction.Immediate >> 1} is outside " +
                        $"{module.Imports.Count + module.Functions.Count} callable(s)");

                case Op.LoadGlobal or Op.StoreGlobal
                    when instruction.Immediate >= (ulong)module.Globals.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: global index " +
                        $"{instruction.Immediate} is outside {module.Globals.Count} global(s)");

                // The fused arithmetic writes a slot and reads one or two; all three are checked
                // here so the interpreter reads and writes an array without asking.
                case Op.BinLocals or Op.BinConst
                    when instruction.SlotDest >= function.SlotTypes.Count
                         || instruction.SlotA >= function.SlotTypes.Count
                         || instruction.SlotB >= function.SlotTypes.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: local slot " +
                        $"{Math.Max(instruction.SlotDest, Math.Max(instruction.SlotA, instruction.SlotB))} " +
                        $"is outside {function.SlotTypes.Count} slot(s)");

                // The fused branches carry their operands as slots and their targets as block
                // indices; both are checked here so the interpreter reads an array without asking.
                case Op.BranchCompare or Op.BranchCompareConst
                    when instruction.SlotA >= function.SlotTypes.Count
                         || instruction.SlotB >= function.SlotTypes.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: local slot " +
                        $"{Math.Max(instruction.SlotA, instruction.SlotB)} is outside " +
                        $"{function.SlotTypes.Count} slot(s)");

                case Op.BranchCompare or Op.BranchCompareConst
                    when instruction.Immediate >= (ulong)function.BlockOffsets.Count
                         || instruction.Immediate2 >= (ulong)function.BlockOffsets.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: branch target is outside " +
                        $"{function.BlockOffsets.Count} block(s)");

                case Op.Branch when instruction.Immediate >= (ulong)function.BlockOffsets.Count:
                case Op.CondBranch when instruction.Immediate >= (ulong)function.BlockOffsets.Count
                                        || instruction.Immediate2 >= (ulong)function.BlockOffsets.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: branch target is outside " +
                        $"{function.BlockOffsets.Count} block(s)");
            }
        }
    }

    /// <summary>
    /// The type tag an arithmetic, bitwise or comparison opcode carries, against §5 of the format.
    ///
    /// <para>The rules were normative and enforced nowhere. That mattered, because the IR verifier —
    /// which does check them — only runs in a Debug build, so a lowering that emitted <c>add string</c>
    /// produced a module every release tool called valid and the interpreter evaluated as an integer
    /// addition of two references. This is the check that makes the reader the safety net the release
    /// pipeline assumes it is.</para>
    /// </summary>
    private static void ValidateArithmeticTag(BytecodeFunction function,
        BytecodeInstruction instruction)
    {
        if (instruction.Type is not { } tag) return;

        var ok = instruction.Opcode switch
        {
            Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Rem or Op.Neg => IsNumeric(tag),
            Op.Shl or Op.Shr or Op.BitAnd or Op.BitOr or Op.BitXor or Op.BitNot => IsInteger(tag),
            Op.Lt or Op.Le or Op.Gt or Op.Ge => IsNumeric(tag),
            // eq/ne additionally hold for the types with a defined identity.
            Op.Eq or Op.Ne => IsNumeric(tag) || tag is TypeTag.Bool or TypeTag.Char or TypeTag.String,
            // 'conv' is numeric on both ends, and the two ends have to differ.
            Op.Convert => IsNumeric(tag) && IsNumeric(instruction.ToType!.Value)
                          && tag != instruction.ToType.Value,

            // A fused form computes in one machine operation over a word, so it accepts what the
            // operation it carries accepts, minus the string: an eq over references is not one of
            // those, and a fused form has no room to call anything.
            Op.BranchCompare or Op.BranchCompareConst or Op.BinLocals or Op.BinConst =>
                instruction.Fused switch
                {
                    Op.Shl or Op.Shr or Op.BitAnd or Op.BitOr or Op.BitXor => IsInteger(tag),
                    Op.Eq or Op.Ne => IsNumeric(tag) || tag is TypeTag.Bool or TypeTag.Char,
                    _ => IsNumeric(tag),
                },
            _ => true,
        };

        if (!ok)
            throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                $"function '{function.Name}' at {instruction.Offset}: " +
                $"{Disassembler.Mnemonic(instruction.Opcode)} carries type tag 0x{(byte)tag:X2}, " +
                "which the operation does not accept");
    }

    /// <summary>
    /// <c>char</c> counts as an integer here, exactly as <c>IrVerifier.IsInteger</c> counts it.
    ///
    /// <para>The two have to answer the same question the same way, or the reader rejects what the
    /// compiler emits: <c>std.string.digitToChar</c> converts <c>i64</c> to <c>char</c>, and the code
    /// point arithmetic in <c>std.string</c> adds on <c>char</c> directly. §5 of the format describes
    /// the operand types as "numeric" and lists <c>char</c> away from the integers in §3, which reads
    /// as a narrower rule than the one in force. Following the document instead of the verifier would
    /// have made the standard library unloadable, so this mirrors the verifier — the divergence is
    /// real and belongs settled in the specification, not in a bug fix.</para>
    /// </summary>
    private static bool IsInteger(TypeTag tag) =>
        tag is >= TypeTag.I8 and <= TypeTag.U64 or TypeTag.Char;

    private static bool IsNumeric(TypeTag tag) =>
        IsInteger(tag) || tag is TypeTag.F32 or TypeTag.F64;

    /// <summary>The vtable rows, checked at load time so <c>callvirt</c> is an unchecked array
    /// access afterwards.</summary>
    private static void ValidateImpls(BytecodeModule module)
    {
        var callable = module.Imports.Count + module.Functions.Count;
        var seen = new HashSet<(int, int)>();

        foreach (var impl in module.Impls)
        {
            if (impl.Type < 0 || impl.Type >= module.Types.Count
                || impl.Interface < 0 || impl.Interface >= module.Types.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"impl row references type {impl.Type}/{impl.Interface} outside " +
                    $"{module.Types.Count} type(s)");

            var iface = module.Types[impl.Interface];
            if (!iface.IsInterface)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"impl row names '{iface.Name}' as an interface, but it is not one");

            if (module.Types[impl.Type].IsInterface)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"impl row makes interface '{module.Types[impl.Type].Name}' implement " +
                    $"'{iface.Name}'; interfaces do not implement interfaces");

            if (!seen.Add((impl.Type, impl.Interface)))
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"duplicate impl row for '{module.Types[impl.Type].Name}' :: '{iface.Name}'");

            if (impl.Methods.Count != iface.MethodSlots.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"impl row for '{module.Types[impl.Type].Name}' :: '{iface.Name}' has " +
                    $"{impl.Methods.Count} method(s) but the interface declares " +
                    $"{iface.MethodSlots.Count} slot(s)");

            foreach (var method in impl.Methods)
                if (method < 0 || method >= callable)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"impl row for '{module.Types[impl.Type].Name}' :: '{iface.Name}' " +
                        $"targets {method}, which is outside {callable} callable(s)");
        }
    }

    /// <summary>The protected regions, checked at load time so unwinding walks the table
    /// unchecked.</summary>
    private static void ValidateHandlers(BytecodeModule module)
    {
        foreach (var h in module.Handlers)
        {
            if (h.Function < 0 || h.Function >= module.Functions.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"handler names function {h.Function}, which is outside " +
                    $"{module.Functions.Count} function(s)");

            var blocks = module.Functions[h.Function].BlockOffsets.Count;
            if (h.Start < 0 || h.End > blocks || h.Start >= h.End)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"handler in '{module.Functions[h.Function].Name}': protected range " +
                    $"[{h.Start}, {h.End}) is not valid for {blocks} block(s)");

            if (h.Handler < 0 || h.Handler >= blocks)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"handler in '{module.Functions[h.Function].Name}': handler block " +
                    $"{h.Handler} is outside {blocks} block(s)");

            // A handler inside its own range would not terminate while unwinding.
            if (h.Handler >= h.Start && h.Handler < h.End)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"handler in '{module.Functions[h.Function].Name}': handler block " +
                    $"{h.Handler} lies inside its own protected range — unwinding would not terminate");

            if (h.CatchType >= module.Types.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"handler in '{module.Functions[h.Function].Name}': catch type {h.CatchType} " +
                    $"is outside {module.Types.Count} type(s)");

            if (h.Slot >= module.Functions[h.Function].SlotTypes.Count)
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"handler in '{module.Functions[h.Function].Name}': binds into slot {h.Slot}, " +
                    "which is outside the slot table");

            if (h.IsFinally && (h.CatchType >= 0 || h.Slot >= 0))
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"handler in '{module.Functions[h.Function].Name}': a finally region catches " +
                    "nothing and binds nothing");
        }
    }

    /// <summary>Global slots and their initializer, checked at load time.</summary>
    private static void ValidateGlobals(BytecodeModule module)
    {
        foreach (var global in module.Globals)
            if (global.Tag == TypeTag.Void)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    "a global has type void; void is not a value");

        var callable = module.Imports.Count + module.Functions.Count;
        if (module.GlobalInit is { } init && (init < 0 || init >= callable))
            throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                $"global initializer {init} is outside {callable} callable(s)");

        // Slots without an initializer would be uninitialized, and every value has one.
        if (module.Globals.Count > 0 && module.GlobalInit is null)
            throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                $"module declares {module.Globals.Count} global(s) but no initializer");
    }

    /// <summary>
    /// The format's invariant: the operand stack is empty at every block boundary, and values that
    /// cross blocks travel through local slots. Depth is therefore statically determined, so the
    /// VM sizes its frame at load time and needs no runtime overflow check.
    /// </summary>
    private static void ValidateStack(BytecodeModule module, BytecodeFunction function,
        IReadOnlyList<BytecodeInstruction> instructions)
    {
        var byOffset = new Dictionary<int, int>();
        for (var i = 0; i < instructions.Count; i++) byOffset[instructions[i].Offset] = i;

        foreach (var start in function.BlockOffsets)
        {
            var depth = 0;
            for (var i = byOffset[start]; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                var (arity, returnsValue) = CalleeShape(module, instruction);

                // newvariant takes its variant's payload fields; slot 0 is the tag and does not
                // come off the stack.
                var variantArity = instruction.Opcode == Op.NewVariant
                    ? module.Types[(int)instruction.Immediate].FieldTypes.Count - 1
                    : 0;

                var (pops, pushes) = CodeDecoder.StackEffect(instruction, arity, returnsValue, variantArity);

                if (depth < pops)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.StackDiscipline,
                        $"function '{function.Name}' at {instruction.Offset}: {instruction.Opcode} " +
                        $"needs {pops} value(s) but the stack holds {depth}");

                depth = depth - pops + pushes;
                if (depth > function.MaxStack)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.StackDiscipline,
                        $"function '{function.Name}' at {instruction.Offset}: stack depth {depth} " +
                        $"exceeds the declared maximum of {function.MaxStack}");

                if (!CodeDecoder.IsTerminator(instruction.Opcode)) continue;

                if (depth != 0)
                    throw new MalformedBytecodeException(BytecodeDiagnostics.StackDiscipline,
                        $"function '{function.Name}': block at {start} ends with {depth} value(s) " +
                        "on the stack, expected 0");
                break;
            }
        }
    }

    private static (int Arity, bool ReturnsValue) CalleeShape(BytecodeModule module,
        BytecodeInstruction instruction)
    {
        // callvirt: the signature belongs to the interface slot. Every implementation shares it,
        // so any Impls row for this interface gives the arity and the return kind.
        if (instruction.Opcode == Op.CallVirt)
        {
            var iface = (int)instruction.Immediate;
            var slot = (int)instruction.Immediate2;
            var row = module.Impls.FirstOrDefault(i => i.Interface == iface);
            if (row is null || slot >= row.Methods.Count) return (0, false);

            var target = row.Methods[slot];
            if (target < module.Imports.Count)
            {
                var native = module.Imports[target];
                return (native.ParamTypes.Count, native.ReturnType.Tag != TypeTag.Void);
            }

            var implementation = module.Functions[target - module.Imports.Count];
            return (implementation.ParamCount, implementation.ReturnType.Tag != TypeTag.Void);
        }

        if (instruction.Opcode != Op.Call) return (0, false);

        // Shared index space: imports first, then defined functions.
        var index = (int)instruction.Immediate;
        if (index < module.Imports.Count)
        {
            var import = module.Imports[index];
            return (import.ParamTypes.Count, import.ReturnType.Tag != TypeTag.Void);
        }

        var callee = module.Functions[index - module.Imports.Count];
        return (callee.ParamCount, callee.ReturnType.Tag != TypeTag.Void);
    }
}
