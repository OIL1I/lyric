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
        if (tag == TypeTag.Ref) return new BytecodeType(tag, payload.ULebAsCount());
        // Der Elementtyp steht inline und rekursiv (Bytecode.md §3).
        if (tag == TypeTag.Array) return new BytecodeType(tag, -1) { Element = ReadType(payload) };
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

            types.Add(new BytecodeTypeDef { Name = strings[nameIndex], FieldTypes = fields });
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
            while (type.IsArray && type.Element is { } inner) type = inner;

            if (type.IsRef && (type.TypeIndex < 0 || type.TypeIndex >= module.Types.Count))
                throw new MalformedBytecodeException(BytecodeDiagnostics.IndexOutOfRange,
                    $"{where}: type index {type.TypeIndex} is outside {module.Types.Count} type(s)");
        }

        for (var i = 0; i < module.Types.Count; i++)
            for (var f = 0; f < module.Types[i].FieldTypes.Count; f++)
                Check(module.Types[i].FieldTypes[f], $"type '{module.Types[i].Name}' field {f}");

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
                case Op.NewObject or Op.LoadField or Op.StoreField
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
                var (pops, pushes) = CodeDecoder.StackEffect(instruction, arity, returnsValue);

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
