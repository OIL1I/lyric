using Lyric.Bytecode.Encoding;
using Lyric.Core;

namespace Lyric.Bytecode;

/// <summary>
/// <c>.lyrbc</c>-Bytes → <see cref="BytecodeModule"/>, mit vollständiger Validierung <b>beim Laden</b>.
///
/// <para>Das ist ADR-013s Modell (von WASM übernommen): ein Modul wird beim Laden einmal komplett
/// geprüft und läuft danach ohne Sicherheitschecks. Jeder Fehler hier ist ein Grund, das Modul gar
/// nicht erst anzunehmen — deshalb bricht der Leser beim ersten Befund ab, anders als der
/// IR-Verifier, der sammelt. Bei einer kaputten Datei ist der zweite Befund meist Folge des ersten.</para>
///
/// <para>Der Leser ist die Stelle, an der nicht vertrauenswürdige Bytes ins System kommen. Er darf
/// auf keiner Eingabe mit einer .NET-Ausnahme aussteigen, sondern nur mit einer
/// <c>LYR-BC####</c>-Diagnose.</para>
/// </summary>
public static class BytecodeReader
{
    /// <summary>Liest und validiert. Liefert <c>null</c> und meldet <c>LYR-BC####</c>, wenn die
    /// Datei kein gültiges Modul ist.</summary>
    public static BytecodeModule? Read(byte[] bytes, DiagnosticEngine de)
    {
        try
        {
            return ReadOrThrow(bytes);
        }
        catch (MalformedBytecodeException ex)
        {
            // Kein Span: der Fehler sitzt in einer Binärdatei, nicht in Quelltext. Die
            // DiagnosticEngine rendert solche Diagnosen ohne Positionszeile.
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

        var previousId = -1;
        while (!reader.AtEnd)
        {
            var id = reader.U8();
            var length = reader.ULebAsCount();
            var payload = new ByteReader(reader.Raw(length));

            // Aufsteigend und höchstens einmal — das ist Teil der Determinismus-Zusage und
            // erlaubt einem Leser, in einem Durchlauf zu arbeiten.
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
                default: break; // unbekannt oder reserviert: überspringen, dafür ist die Länge da
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
        };

        Validate(module);
        return module;
    }

    private static IReadOnlyList<string> ReadStrings(ByteReader payload)
    {
        var count = payload.ULebAsCount();
        var values = new List<string>(Math.Min(count, 1024));
        for (var i = 0; i < count; i++) values.Add(payload.String());
        return values;
    }

    /// <summary>Ein Typ: Tag, bei einer Referenz gefolgt vom Typ-Index. Die <b>einzige</b> Lesestelle
    /// für Typen — Gegenstück zu <c>BytecodeWriter.WriteType</c>.</summary>
    private static BytecodeType ReadType(ByteReader payload)
    {
        var tag = payload.Tag();
        // Ref, Enum und Interface tragen ihren Tabellen-Index dahinter — anders als Array und
        // Optional, deren Elementtyp inline steht. Eine hier vergessene Tag-Art ist ein um
        // Bytes verschobener Strom, kein sauberer Fehler.
        if (tag is TypeTag.Ref or TypeTag.Enum or TypeTag.Interface or TypeTag.Struct)
            return new BytecodeType(tag, payload.ULebAsCount());
        // Der Elementtyp steht inline und rekursiv (Bytecode.md §3).
        if (tag is TypeTag.Array or TypeTag.Optional)
        {
            var inner = ReadType(payload);
            // ??T gibt es nicht: die Laufzeit-Darstellung unterscheidet "kein Wert" an der leeren
            // Referenz, und die kann nur eine Ebene tragen (Bytecode.md §5).
            if (tag == TypeTag.Optional && inner.Tag == TypeTag.Optional)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    "nested optional '??T' — optionals do not nest");

            return new BytecodeType(tag, -1) { Element = inner };
        }
        return BytecodeType.Scalar(tag);
    }

    /// <summary>
    /// Die Typ-Tabelle. Bereichsprüfungen der Feld-Referenzen laufen erst in
    /// <c>Validate</c>: ein Typ darf sich selbst und spätere Typen nennen (<c>class Node { next:
    /// Node }</c>), also ist beim Lesen des Feldes noch nicht bekannt, wie groß die Tabelle wird.
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
                // void hat keine Breite und keinen Nullwert — es ist kein Wert (Bytecode.md §3).
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

    /// <summary>
    /// Die Impls-Sektion: je Zeile Klasse, Interface, Slot-Anzahl, Funktionsindizes.
    /// </summary>
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

    /// <summary>Die Handlers-Sektion: je Zeile Funktion, Blockbereich, Art, Typ, Handler, Slot.</summary>
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
                // 0 heisst "keiner"; der echte Index steht um eins erhoeht im Strom.
                CatchType = catchType - 1, Handler = handler, Slot = slot - 1,
            });
        }

        return handlers;
    }

    /// <summary>Prüfungen, die erst gehen, wenn alles gelesen ist — Call-Ziele brauchen die
    /// Signaturen anderer Funktionen, Sprungziele die Blockanzahl.</summary>
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

    /// <summary>Jede Referenz in einer Signatur oder einem Layout zeigt in die Typ-Tabelle. Läuft
    /// über alle Typen auf einmal, weil Vorwärts- und Selbstverweise erlaubt sind — beim Lesen war
    /// die endgültige Größe der Tabelle noch nicht bekannt.</summary>
    private static void ValidateTypeReferences(BytecodeModule module)
    {
        void Check(BytecodeType type, string where)
        {
            // Ein Array-Typ trägt seinen Elementtyp inline; eine Referenz darin muss genauso in
            // die Tabelle zeigen wie eine direkte.
            while ((type.IsArray || type.IsOptional) && type.Element is { } inner) type = inner;

            if (type.IsRef && (type.TypeIndex < 0 || type.TypeIndex >= module.Types.Count))
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"{where}: type index {type.TypeIndex} is outside {module.Types.Count} type(s)");
        }

        for (var i = 0; i < module.Types.Count; i++)
        {
            for (var f = 0; f < module.Types[i].FieldTypes.Count; f++)
                Check(module.Types[i].FieldTypes[f], $"type '{module.Types[i].Name}' field {f}");

            // Eine Variante muss ein Layout sein und einen Tag-Slot haben — sonst laesen ldfld
            // und enumas gegen ein Layout, das es nicht gibt.
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

                // ADR-013s Kern: Typ- und Feldindex werden hier geprüft, damit der Feldzugriff zur
                // Laufzeit ein Array-Zugriff ohne Prüfung sein darf.
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

                // mkiface: das zweite Immediate ist das Interface, und es muss eines sein — mit
                // einer Impl-Zeile fuer genau dieses Paar. Ohne die Pruefung entstuende ein
                // Interface-Wert, dessen Dispatch spaeter ins Leere liefe.
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

                // Ein structcopy auf einem Referenztyp waere ein stiller Semantikbruch: die
                // Laufzeit kopierte klaglos ein Slot-Array, das geteilt gehoert.
                case Op.StructCopy when !module.Types[(int)instruction.Immediate].IsStruct:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: structcopy targets " +
                        $"'{module.Types[(int)instruction.Immediate].Name}', which is not a struct");

                case Op.LoadGlobal or Op.StoreGlobal
                    when instruction.Immediate >= (ulong)module.Globals.Count:
                    throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                        $"function '{function.Name}' at {instruction.Offset}: global index " +
                        $"{instruction.Immediate} is outside {module.Globals.Count} global(s)");

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
    /// Die vtable-Zeilen (ADR-013: geprueft beim Laden, nicht beim Aufruf). Danach darf
    /// <c>callvirt</c> ein Array-Zugriff ohne Pruefung sein.
    /// </summary>
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

    /// <summary>
    /// Die geschuetzten Regionen (ADR-013: geprueft beim Laden). Danach darf das Abwickeln zur
    /// Laufzeit ohne Pruefung durch die Tabelle laufen.
    /// </summary>
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

            // Ein Handler in seinem eigenen Bereich waere eine Endlosschleife beim Abwickeln.
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

    /// <summary>
    /// Globale Slots und ihre Init-Funktion (ADR-013: geprueft beim Laden).
    /// </summary>
    private static void ValidateGlobals(BytecodeModule module)
    {
        foreach (var global in module.Globals)
            if (global.Tag == TypeTag.Void)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    "a global has type void; void is not a value (§3)");

        var callable = module.Imports.Count + module.Functions.Count;
        if (module.GlobalInit is { } init && (init < 0 || init >= callable))
            throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                $"global initializer {init} is outside {callable} callable(s)");

        // Slots ohne Fueller waeren uninitialisiert, und jeder Wert in Lyric hat einen (§6.6).
        if (module.Globals.Count > 0 && module.GlobalInit is null)
            throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                $"module declares {module.Globals.Count} global(s) but no initializer");
    }

    /// <summary>
    /// Die tragende Invariante des Formats: <b>der Operanden-Stack ist an jeder Blockgrenze leer</b>.
    /// Werte, die Blöcke überqueren, laufen durch Local-Slots. Das macht die Tiefe statisch
    /// bestimmbar — die VM kann ihren Frame beim Laden dimensionieren und braucht zur Laufzeit
    /// keine Überlauf-Prüfung.
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

                // newvariant nimmt die Nutzfelder seiner Variante; Slot 0 ist das Tag und wird
                // nicht vom Stack genommen.
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
        // callvirt: die Signatur steht am Interface-Slot. Welche Implementierung laeuft, ist
        // dynamisch — aber alle haben dieselbe Form, sonst waere die Konformanz verletzt. Also
        // reicht irgendeine Impl-Zeile fuer dieses Interface, um Arity und Rueckgabe zu kennen.
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

        // Gemeinsamer Indexraum: erst Imports, dann definierte Funktionen (WASM-Modell).
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
