using Lyric.Bytecode;

namespace Lyric.Vm;

/// <summary>
/// Führt ein geladenes <see cref="BytecodeModule"/> aus.
///
/// <para><b>Keine Sicherheitsprüfungen im heißen Pfad.</b> Der Loader hat beim Lesen alles
/// validiert — Slot- und Block-Indizes, Call-Ziele, Stack-Bilanz und maximale Tiefe
/// (<c>LYR-BC####</c>). Die Schleife darf sich darauf verlassen; das ist der Gegenwert für die
/// Load-Zeit-Validierung aus ADR-013. Was hier noch schiefgehen kann, sind Dinge, die statisch
/// nicht entscheidbar sind: Division durch Null und zu tiefe Rekursion.</para>
///
/// <para><b>Instruktionen werden einmal vordekodiert</b>, nicht bei jedem Durchlauf neu aus den
/// Bytes gelesen. In einer Schleife würde sonst jede Iteration dieselben LEB128-Operanden wieder
/// parsen. Dekodiert wird mit <see cref="CodeDecoder"/> — demselben, den Validator und
/// Disassembler benutzen, damit keine zweite Lesart entstehen kann.</para>
///
/// <para><b>Expliziter Frame-Stack statt .NET-Rekursion</b>: sonst begrenzte der CLR-Stack die
/// Lyric-Rekursionstiefe, und ein Stack-Overflow wäre ein Prozessabbruch statt einer Diagnose.</para>
/// </summary>
public static class Interpreter
{
    /// <summary>Ab hier gilt Rekursion als entlaufen. Lyric hat keine Endrekursions-Optimierung,
    /// also ist das die Form, in der sich eine fehlende Abbruchbedingung zeigt.</summary>
    private const int MaxCallDepth = 1024;

    /// <summary>Führt die Start-Funktion aus und liefert ihren Rückgabewert.</summary>
    public static LyrValue Run(BytecodeModule module, NativeRegistry? natives = null)
    {
        if (module.Start is not { } start)
            throw new LyricRuntimeException(VmDiagnostics.NoEntryPoint,
                "module has no start section — it is a library, not a program");

        // Start indiziert den gemeinsamen Raum (erst Imports, dann Funktionen), 'prepared' nur die
        // definierten Funktionen — siehe Bytecode.md §Start (Id 7). Ein Einstieg im Import-Bereich
        // waere ein Modul, dessen main eine Host-Funktion ist; der Loader laesst das durch, die
        // Runtime kann es nicht ausfuehren.
        var entry = start - module.Imports.Count;
        if (entry < 0)
            throw new LyricRuntimeException(VmDiagnostics.NoEntryPoint,
                $"start index {start} points into the import table — an entry point must be a "
                + "function defined in this module");

        var prepared = new Prepared[module.Functions.Count];
        for (var i = 0; i < prepared.Length; i++)
            prepared[i] = Prepared.From(module.Functions[i],
                module.Handlers.Where(h => h.Function == i).ToArray());

        // Bindung beim Laden: fehlt ein Native, wird das Modul abgelehnt, bevor eine
        // Instruktion laeuft.
        var bound = (natives ?? new NativeRegistry()).Bind(module);
        var dispatch = DispatchTable.Build(module);

        // Globale Slots. Ein String-Slot startet mit dem leeren String statt mit einer leeren
        // Referenz — dieselbe Regel wie bei Objektfeldern (§6.6): kein Wert ist je nicht da.
        var globals = new LyrValue[module.Globals.Count];
        for (var i = 0; i < globals.Length; i++)
            if (module.Globals[i].Tag == TypeTag.String) globals[i] = LyrValue.FromString(string.Empty);

        // Die Init-Funktion laeuft VOR dem Einstiegspunkt (Bytecode.md §Globals). Ihr Ergebnis
        // wird verworfen — sie ist void; was zaehlt, sind die Slots, die sie hinterlaesst.
        if (module.GlobalInit is { } init && init >= module.Imports.Count)
            Execute(prepared, init - module.Imports.Count, module.Strings, module.Types,
                dispatch, bound, globals);

        return Execute(prepared, entry, module.Strings, module.Types, dispatch, bound, globals);
    }

    private static LyrValue Execute(Prepared[] prepared, int startIndex,
        IReadOnlyList<string> strings, IReadOnlyList<BytecodeTypeDef> types,
        DispatchTable dispatch, NativeRegistry.BoundNative[] natives, LyrValue[] globals)
    {
        var frames = new Stack<Frame>();
        var frame = Frame.For(prepared[startIndex]);

        try
        {
            return Loop(prepared, strings, types, dispatch, natives, globals, frames, ref frame);
        }
        catch (LyricPanic panic) when (panic.CallStack.Count == 0)
        {
            // Der Backtrace wird hier angehängt, nicht an der Wurfstelle: eine Rechenoperation
            // kennt ihren Aufrufer nicht, die Schleife dagegen hält den ganzen Frame-Stack.
            var stack = new List<string> { frame.Fn.Source.Name };
            stack.AddRange(frames.Select(f => f.Fn.Source.Name));
            throw panic.WithCallStack(stack);
        }
    }

    private static LyrValue Loop(Prepared[] prepared, IReadOnlyList<string> strings,
        IReadOnlyList<BytecodeTypeDef> types, DispatchTable dispatch,
        NativeRegistry.BoundNative[] natives, LyrValue[] globals, Stack<Frame> frames,
        ref Frame frame)
    {
        while (true)
        {
            var instruction = frame.Fn.Instructions[frame.Ip++];

            switch (instruction.Opcode)
            {
                case Op.Const:
                    frame.Push(Constant(instruction, strings));
                    break;

                case Op.LoadLocal:
                    frame.Push(frame.Slots[(int)instruction.Immediate]);
                    break;

                case Op.StoreLocal:
                    frame.Slots[(int)instruction.Immediate] = frame.Pop();
                    break;

                // Wie ldloc/stloc, nur modulweit. Der Index ist beim Laden geprueft (ADR-013),
                // also ist auch das ein Array-Zugriff ohne Pruefung.
                case Op.LoadGlobal:
                    frame.Push(globals[(int)instruction.Immediate]);
                    break;

                case Op.StoreGlobal:
                    globals[(int)instruction.Immediate] = frame.Pop();
                    break;

                case Op.Pop:
                    frame.Pop();
                    break;

                case Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Rem or
                     Op.Shl or Op.Shr or Op.BitAnd or Op.BitOr or Op.BitXor:
                {
                    var rhs = frame.Pop();
                    var lhs = frame.Pop();
                    frame.Push(Binary(instruction.Opcode, instruction.Type!.Value, lhs, rhs));
                    break;
                }

                case Op.Lt or Op.Le or Op.Gt or Op.Ge or Op.Eq or Op.Ne:
                {
                    var rhs = frame.Pop();
                    var lhs = frame.Pop();
                    frame.Push(LyrValue.FromBool(
                        Compare(instruction.Opcode, instruction.Type!.Value, lhs, rhs)));
                    break;
                }

                case Op.Neg or Op.Not or Op.BitNot:
                    frame.Push(Unary(instruction.Opcode, instruction.Type, frame.Pop()));
                    break;

                case Op.Convert:
                    frame.Push(Convert(instruction.Type!.Value, instruction.ToType!.Value, frame.Pop()));
                    break;

                case Op.Branch:
                    frame.Ip = frame.Fn.BlockStart[(int)instruction.Immediate];
                    break;

                case Op.CondBranch:
                    frame.Ip = frame.Fn.BlockStart[
                        (int)(frame.Pop().AsBool ? instruction.Immediate : instruction.Immediate2)];
                    break;

                case Op.Call:
                {
                    // Gemeinsamer Indexraum: erst Imports, dann definierte Funktionen (ADR-013).
                    // Ein Import bekommt keinen Frame — er läuft im Host und kehrt sofort zurück.
                    var index = (int)instruction.Immediate;
                    if (index < natives.Length)
                    {
                        var native = natives[index];
                        var args = new LyrValue[native.Arity];
                        for (var i = native.Arity - 1; i >= 0; i--) args[i] = frame.Pop();

                        var produced = native.Implementation(args);
                        if (native.ReturnsValue) frame.Push(produced);
                        break;
                    }

                    if (frames.Count >= MaxCallDepth)
                        throw new LyricPanic(VmDiagnostics.CallDepthExceeded,
                            $"call depth exceeded {MaxCallDepth} frames in '{frame.Fn.Source.Name}'");

                    var callee = prepared[index - natives.Length];
                    var next = Frame.For(callee);
                    // Argumente liegen in Aufrufreihenfolge auf dem Stack, das erste zuunterst.
                    for (var i = callee.Source.ParamCount - 1; i >= 0; i--) next.Slots[i] = frame.Pop();

                    frames.Push(frame);
                    frame = next;
                    break;
                }

                // Ende einer finally-Region: die Aufraeumarbeit ist getan, die Abwicklung geht
                // weiter, wo sie unterbrochen wurde. Auf dem normalen Pfad wird dieser Block nie
                // betreten — dort stehen die defer-Rumpfe inline.
                case Op.EndFinally:
                {
                    if (frame.UnwindType < 0)
                        throw new LyricRuntimeException(VmDiagnostics.UncaughtException,
                            "'endfinally' outside an unwind — a finally region was entered on the "
                            + "normal path, which the lowering never emits");

                    var pending = frame.Unwinding;
                    var pendingType = frame.UnwindType;
                    if (!Resume(frames, ref frame, pending, pendingType))
                        throw new LyricPanic(VmDiagnostics.UncaughtException,
                            $"uncaught exception of type '{TypeName(types, pendingType)}'");
                    break;
                }

                case Op.Throw:
                {
                    // 0 heisst "steht erst zur Laufzeit fest" — dann ist der Wert ein Fat Pointer
                    // und traegt seinen konkreten Typ selbst (P3).
                    var thrown = frame.Pop();
                    var declared = (int)instruction.Immediate - 1;
                    var type = declared >= 0 ? declared : thrown.ConcreteType;

                    // Frischer Wurf: die Suche beginnt beim ersten Handler und beim Block, in
                    // dem geworfen wurde.
                    frame.NextHandler = 0;
                    frame.UnwindBlock = BlockAt(frame, frame.Ip - 1);

                    if (!Resume(frames, ref frame, thrown, type))
                        throw new LyricPanic(VmDiagnostics.UncaughtException,
                            $"uncaught exception of type '{TypeName(types, type)}'");
                    break;
                }

                // Wert-Semantik (Sprache.md §3.2). Der Compiler hat entschieden, WO kopiert wird;
                // hier wird nur noch kopiert.
                case Op.StructCopy:
                    frame.Push(CopyStruct(frame.Pop(), types, (int)instruction.Immediate));
                    break;

                // Ein Interface-Wert ist ein Fat Pointer: dasselbe Objekt, plus der konkrete
                // Typindex in den ungenutzten Bits. Keine Allokation, keine Layout-Aenderung —
                // und ein Objekt, das nie ueber ein Interface laeuft, zahlt gar nichts.
                case Op.MakeInterface:
                    frame.Push(LyrValue.FromInterface(frame.Pop(), (int)instruction.Immediate));
                    break;

                // Unterstes Immediate-Bit: liegt ein Environment auf dem Stack? Ohne Captures
                // nicht — dann ist die Closure reiner Funktionsindex (Bytecode.md §Closures).
                case Op.MakeClosure:
                {
                    var environment = (instruction.Immediate & 1) == 1 ? frame.Pop() : default;
                    frame.Push(LyrValue.FromClosure(environment, (int)(instruction.Immediate >> 1)));
                    break;
                }

                // Aufruf ueber einen Funktionswert. Der Aufgerufene liegt UNTER seinen Argumenten;
                // sein Environment wird als Argument 0 vorangestellt — genau die Position, die bei
                // einer Methode der Empfaenger belegt (ADR-014). Deshalb braucht dieser Fall
                // keinen eigenen Frame-Aufbau, nur eine andere Herkunft des Index.
                case Op.CallIndirect:
                {
                    var argCount = (int)(instruction.Immediate >> 1);
                    var closure = frame.Peek(argCount);
                    var index = closure.ClosureFunction;

                    if (index < natives.Length)
                    {
                        var native = natives[index];
                        var nativeArgs = new LyrValue[native.Arity];
                        for (var i = native.Arity - 1; i >= 0; i--) nativeArgs[i] = frame.Pop();
                        frame.Pop(); // der Closure-Wert selbst

                        var produced = native.Implementation(nativeArgs);
                        if (native.ReturnsValue) frame.Push(produced);
                        break;
                    }

                    if (frames.Count >= MaxCallDepth)
                        throw new LyricPanic(VmDiagnostics.CallDepthExceeded,
                            $"call depth exceeded {MaxCallDepth} frames in '{frame.Fn.Source.Name}'");

                    var target = prepared[index - natives.Length];
                    var callFrame = Frame.For(target);

                    var offset = closure.HasEnvironment ? 1 : 0;
                    for (var i = argCount - 1; i >= 0; i--) callFrame.Slots[offset + i] = frame.Pop();

                    frame.Pop(); // der Closure-Wert
                    if (closure.HasEnvironment) callFrame.Slots[0] = LyrValue.FromObject(closure.AsObject);

                    frames.Push(frame);
                    frame = callFrame;
                    break;
                }

                // Der einzige dynamische Dispatch der Sprache. Empfaenger ist Argument 0 und liegt
                // zuunterst; sein mitgefuehrter Typ waehlt die Zeile, das Immediate den Slot.
                case Op.CallVirt:
                {
                    var iface = (int)instruction.Immediate;
                    var slot = (int)instruction.Immediate2;

                    // Der Empfaenger liegt unter den Argumenten. Ihn zu erreichen, bevor die
                    // Zielfunktion feststeht, geht nur ueber die vorab bekannte Arity.
                    var receiver = frame.Peek(dispatch.ArityOf(iface, slot) - 1);
                    var index = dispatch.Resolve(receiver.ConcreteType, iface, slot);

                    if (index < natives.Length)
                    {
                        var native = natives[index];
                        var args = new LyrValue[native.Arity];
                        for (var i = native.Arity - 1; i >= 0; i--) args[i] = frame.Pop();

                        var produced = native.Implementation(args);
                        if (native.ReturnsValue) frame.Push(produced);
                        break;
                    }

                    if (frames.Count >= MaxCallDepth)
                        throw new LyricPanic(VmDiagnostics.CallDepthExceeded,
                            $"call depth exceeded {MaxCallDepth} frames in '{frame.Fn.Source.Name}'");

                    var callee = prepared[index - natives.Length];
                    var next = Frame.For(callee);
                    for (var i = callee.Source.ParamCount - 1; i >= 0; i--) next.Slots[i] = frame.Pop();

                    frames.Push(frame);
                    frame = next;
                    break;
                }

                // Ein Objekt ist ein Slot-Array hinter LyrValue.Ref — kein Typ-Tag im Wert. Der
                // Instruktionsstrom weiß statisch, was vorliegt, also ist ein Feldzugriff ein
                // Array-Zugriff. Dass Typ- und Feldindex passen, hat der Loader geprüft (ADR-013);
                // hier findet deshalb keine Prüfung mehr statt.
                case Op.NewObject:
                    frame.Push(LyrValue.FromObject(NewInstance(types[(int)instruction.Immediate])));
                    break;

                case Op.LoadField:
                    frame.Push(frame.Pop().AsObject[(int)instruction.Immediate2]);
                    break;

                case Op.StoreField:
                {
                    // Bytecode.md §5: die Referenz liegt UNTER dem Wert, also kommt der Wert zuerst
                    // herunter.
                    var value = frame.Pop();
                    frame.Pop().AsObject[(int)instruction.Immediate2] = value;
                    break;
                }

                // Arrays: dieselbe Darstellung wie Objekte (LyrValue[] hinter Ref) — ein Objekt ist
                // ein Array mit Namen für seine Slots. Der Index ist hier aber ein LAUFZEITWERT und
                // deshalb, anders als ein Feldindex, zur Laufzeit zu prüfen: ADR-013 lässt beim
                // Laden nur prüfen, was der Compiler festgelegt hat.
                case Op.NewArray:
                {
                    var elements = new LyrValue[(int)instruction.Immediate];
                    for (var i = elements.Length - 1; i >= 0; i--) elements[i] = frame.Pop();
                    frame.Push(LyrValue.FromObject(elements));
                    break;
                }

                case Op.LoadElem:
                {
                    var at = frame.Pop().AsI64;
                    var array = frame.Pop().AsObject;
                    frame.Push(array[CheckedIndex(at, array.Length, frame)]);
                    break;
                }

                case Op.StoreElem:
                {
                    var value = frame.Pop();
                    var at = frame.Pop().AsI64;
                    var array = frame.Pop().AsObject;
                    array[CheckedIndex(at, array.Length, frame)] = value;
                    break;
                }

                case Op.ArrayLen:
                    frame.Push(LyrValue.FromI64(frame.Pop().AsObject.Length));
                    break;

                case Op.ArrayConcat:
                {
                    var right = frame.Pop().AsObject;
                    var left = frame.Pop().AsObject;
                    var joined = new LyrValue[left.Length + right.Length];
                    left.CopyTo(joined, 0);
                    right.CopyTo(joined, left.Length);
                    frame.Push(LyrValue.FromObject(joined));
                    break;
                }

                case Op.ArrayRepeat:
                {
                    var count = frame.Pop().AsI64;
                    var source = frame.Pop().AsObject;
                    if (count < 0)
                        throw new LyricPanic(VmDiagnostics.IndexOutOfRange,
                            $"array repetition count {count} is negative");

                    var repeated = new LyrValue[source.Length * count];
                    for (var i = 0; i < count; i++) source.CopyTo(repeated, i * source.Length);
                    frame.Push(LyrValue.FromObject(repeated));
                    break;
                }

                // Optionals: "kein Wert" ist eine leere Referenz (Bytecode.md §5). Fuer ?string,
                // ?T[] und ?Klasse faellt das mit der natuerlichen Darstellung zusammen; nur
                // Skalare brauchen den Marker, den LyrValue.Some setzt.
                case Op.OptNone:
                    frame.Push(LyrValue.None);
                    break;

                case Op.OptSome:
                    frame.Push(LyrValue.Some(frame.Pop()));
                    break;

                case Op.OptIsSome:
                    frame.Push(LyrValue.FromBool(frame.Pop().IsSome));
                    break;

                case Op.OptGet:
                {
                    var option = frame.Pop();
                    if (!option.IsSome)
                        throw new LyricPanic(VmDiagnostics.NullDereference,
                            $"force-unwrapped a '?T' that had no value in '{frame.Fn.Source.Name}'");
                    frame.Push(option.Unwrap());
                    break;
                }

                // Enums: eine Variante ist ein gewoehnliches Objekt, dessen Slot 0 ihr Tag traegt.
                // Der Feldzugriff danach ist ein normales ldfld — deshalb braucht ein Enum keine
                // eigene Wertdarstellung.
                case Op.NewVariant:
                {
                    var layout = types[(int)instruction.Immediate];
                    var slots = new LyrValue[layout.FieldTypes.Count];
                    for (var i = slots.Length - 1; i >= 1; i--) slots[i] = frame.Pop();
                    slots[0] = LyrValue.FromI64(TagOf(types, (int)instruction.Immediate));
                    frame.Push(LyrValue.FromObject(slots));
                    break;
                }

                case Op.EnumTag:
                    frame.Push(frame.Pop().AsObject[0]);
                    break;

                case Op.EnumAs:
                {
                    var value = frame.Pop();
                    var expected = TagOf(types, (int)instruction.Immediate);
                    if (value.AsObject[0].AsI64 != expected)
                        throw new LyricPanic(VmDiagnostics.WrongVariant,
                            $"expected variant '{types[(int)instruction.Immediate].Name}' " +
                            $"in '{frame.Fn.Source.Name}', found tag {value.AsObject[0].AsI64}");
                    frame.Push(value);
                    break;
                }

                case Op.Return or Op.ReturnValue:
                {
                    var result = instruction.Opcode == Op.ReturnValue ? frame.Pop() : default;
                    var returnsValue = frame.Fn.Source.ReturnType.Tag != TypeTag.Void;

                    if (frames.Count == 0) return result;

                    frame = frames.Pop();
                    if (returnsValue) frame.Push(result);
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
    /// Eine frische Instanz: ein Slot je Feld, jeder auf dem Nullwert seines Typs.
    ///
    /// <para>Kein Feld ist je „uninitialisiert" — <c>.lyrbc</c> ist ein plattformneutraler Vertrag
    /// (ADR-013), und „undefiniert wie in C" ist an keiner Stelle zulässig (Sprache.md §6.6). Für
    /// Zahlen, bool und char ist der Nullwert das Nullbitmuster und damit gratis; nur
    /// <c>string</c> braucht die leere Zeichenkette, weil <c>Ref == null</c> sonst als
    /// Null-Referenz durchginge.</para>
    /// </summary>
    /// <summary>
    /// Ein Element-Index ist ein Laufzeitwert und deshalb hier zu prüfen — anders als Typ- und
    /// Feldindizes, die der Loader erledigt hat (ADR-013). Eine Verletzung ist ein
    /// <c>panic</c> (Sprache.md §9): das Programm hat sich verrechnet, der Compiler hat nichts
    /// falsch gemacht.
    /// </summary>
    private static int CheckedIndex(long index, int length, Frame frame)
    {
        if (index >= 0 && index < length) return (int)index;

        throw new LyricPanic(VmDiagnostics.IndexOutOfRange,
            $"index {index} is outside an array of length {length} in '{frame.Fn.Source.Name}'");
    }

    /// <summary>Das Tag einer Variante: ihr Index in der Variantenliste ihres Enums
    /// (Bytecode.md §2). Beim Laden gesucht statt im Bytecode mitgeführt — es ist redundant, und
    /// eine zweite Quelle könnte driften.</summary>
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
    /// Eine unabhaengige Kopie eines <c>struct</c>-Wertes.
    ///
    /// <para><b>Rekursiv ueber verschachtelte Structs, flach ueber alles andere.</b> Ein Feld vom
    /// Typ <c>class</c>, <c>T[]</c> oder <c>dyn</c> traegt eine Referenz, und die wird
    /// <i>geteilt</i>: kopiert wird der Wert, nicht die Welt dahinter. Ein Feld vom Typ
    /// <c>struct</c> ist dagegen selbst ein Wert und muss mitkopiert werden, sonst saehe man die
    /// Aenderung an <c>a.inner.x</c> auch bei <c>b</c>.</para>
    ///
    /// <para>Die Rekursion terminiert ohne Zyklen-Erkennung, weil ein struct sich nicht selbst
    /// enthalten kann — es waere unendlich gross, und die Sema lehnt es als <c>LYR-SEM0056</c> ab.
    /// Genau deshalb ist diese Pruefung dort keine Bequemlichkeit.</para>
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
    /// Sucht den Handler fuer einen geworfenen Wert und setzt den Kontrollfluss dorthin.
    ///
    /// <para>Von innen nach aussen: erst die Handler des aktuellen Frames, dann — nach dem
    /// Verwerfen des Frames — die des Aufrufers. <see cref="IrFunction.Handlers"/> steht bereits
    /// innerste-zuerst, also gewinnt der erste passende Eintrag; eine Bereichsgroessen-Rechnung
    /// braucht es nicht.</para>
    ///
    /// <para><b>Typvergleich ist Gleichheit</b>, kein Untertyp-Test. Lyric hat keine Inheritance
    /// (ADR-003): eine Klasse ist genau ihr Typ. Ein catch-all (<c>CatchType &lt; 0</c>) faengt
    /// alles.</para>
    ///
    /// <para>Liefert <c>false</c>, wenn kein Frame einen Handler hat — dann verlaesst die
    /// Exception den Einstiegspunkt.</para>
    /// </summary>
    private static bool Resume(Stack<Frame> frames, ref Frame frame, LyrValue thrown, int type)
    {
        while (true)
        {
            for (var i = frame.NextHandler; i < frame.Fn.Handlers.Length; i++)
            {
                var handler = frame.Fn.Handlers[i];
                if (frame.UnwindBlock < handler.Start || frame.UnwindBlock >= handler.End) continue;

                // Der Stack ist an jeder Blockgrenze leer (Bytecode.md §4) — beim Sprung in einen
                // Handler muss er das auch sein, sonst blieben Zwischenwerte des abgebrochenen
                // Ausdrucks liegen.
                frame.ClearStack();

                if (handler.IsFinally)
                {
                    // Aufraeumen, dann weitersuchen. Der Zustand haengt am FRAME, nicht an einer
                    // Seitenstruktur: er stirbt mit ihm, und ein zweiter Wurf aus dem
                    // finally-Rumpf ueberschreibt ihn — was richtig ist, denn dann gilt der neue.
                    frame.Unwinding = thrown;
                    frame.UnwindType = type;
                    frame.NextHandler = i + 1;
                    frame.Ip = frame.Fn.BlockStart[handler.Handler];
                    return true;
                }

                if (handler.CatchType >= 0 && handler.CatchType != type) continue;

                if (handler.Slot >= 0) frame.Slots[handler.Slot] = thrown;
                frame.UnwindType = -1;
                frame.Ip = frame.Fn.BlockStart[handler.Handler];
                return true;
            }

            if (frames.Count == 0) return false;

            // Eine Ebene nach aussen: dort faengt die Suche von vorn an, und der Ursprungsblock
            // ist der Aufrufort.
            frame = frames.Pop();
            frame.NextHandler = 0;
            frame.UnwindBlock = BlockAt(frame, frame.Ip - 1);
        }
    }

    /// <summary>Der Block, in dem die Instruktion an <paramref name="index"/> steht. Der
    /// Zeiger steht beim Wurf schon hinter der werfenden Instruktion.</summary>
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

    private static LyrValue Constant(BytecodeInstruction instruction,
        IReadOnlyList<string> strings) => instruction.Type switch
    {
        TypeTag.F32 => LyrValue.FromF32((float)instruction.FloatValue),
        TypeTag.F64 => LyrValue.FromF64(instruction.FloatValue),
        TypeTag.Bool => LyrValue.FromBool(instruction.BoolValue),
        TypeTag.String => LyrValue.FromString(strings[(int)instruction.Immediate]),
        // Ganzzahlen und char: das Immediate IST schon das Bitmuster, muss aber auf die
        // Breiten-Invariante gebracht werden (i8 kommt als 0x00..0xFF an).
        _ => LyrValue.FromBits(LyrValue.Normalize(instruction.Type!.Value, instruction.Immediate)),
    };

    private static LyrValue Binary(Op op, TypeTag tag, LyrValue lhs, LyrValue rhs)
    {
        if (tag == TypeTag.F64) return LyrValue.FromF64(FloatOp(op, lhs.AsF64, rhs.AsF64));
        // f32 muss in einfacher Genauigkeit gerechnet werden, nicht in doppelter und dann gerundet.
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
                    // MinValue / -1 überläuft im Zweierkomplement; .NET wirft dabei. Lyric wickelt
                    // um wie jede andere Ganzzahl-Operation auch.
                    if (b == -1) { result = op == Op.Div ? unchecked((ulong)(-a)) : 0UL; break; }
                    result = op == Op.Div ? unchecked((ulong)(a / b)) : unchecked((ulong)(a % b));
                }
                else
                {
                    result = op == Op.Div ? lhs.Bits / rhs.Bits : lhs.Bits % rhs.Bits;
                }
                break;
            }

            // Sprache.md §6.5: der Schiebebetrag wird modulo der OPERANDENBREITE genommen, nicht
            // modulo 64. Bei 64 zu maskieren und danach auf die Zielbreite zu normalisieren wäre
            // eine Mischform: `1 << 9` ergäbe bei int8 dann 0, bei int64 aber 2 — dieselbe Regel
            // mit verschiedenem Ergebnis je nach Typ.
            case Op.Shl: result = unchecked(lhs.Bits << ShiftCount(tag, rhs.Bits)); break;
            case Op.Shr:
                result = signed
                    ? unchecked((ulong)(lhs.AsI64 >> ShiftCount(tag, rhs.Bits))) // arithmetisch
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

    /// <summary>Schiebebetrag modulo Operandenbreite (Sprache.md §6.5) — dieselbe Regel wie in
    /// C#, Java und WASM.</summary>
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
        Op.Div => a / b,   // IEEE: durch Null ergibt Inf/NaN, kein Fehler
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

        // bool und char vergleichen sich wie vorzeichenlose Ganzzahlen — nur eq/ne sind gültig,
        // das hat der Verifier schon durchgesetzt.
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
    /// Fließkomma → Ganzzahl: abschneiden Richtung Null, außerhalb des Wertebereichs auf die
    /// Grenze klemmen, NaN auf 0. Das ist WASMs <c>trunc_sat</c>-Verhalten.
    ///
    /// <para>Die Alternative wäre, es undefiniert zu lassen wie C — dann liefert dieselbe
    /// <c>.lyrbc</c>-Datei auf zwei Runtimes verschiedene Ergebnisse, und ADR-013s Versprechen
    /// einer zweiten Implementierung wäre nichts wert. <b>Steht noch nicht in Sprache.md</b> und
    /// gehört dort hinein.</para>
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

    /// <summary>Eine Funktion, einmal dekodiert. Der Sprung auf einen Block wird damit zu einem
    /// Array-Zugriff statt zu einer Suche im Byte-Strom.</summary>
    private sealed class Prepared
    {
        public required BytecodeFunction Source { get; init; }
        public required BytecodeInstruction[] Instructions { get; init; }
        public required int[] BlockStart { get; init; }

        /// <summary>Die geschuetzten Regionen dieser Funktion, innerste zuerst.</summary>
        public BytecodeHandler[] Handlers { get; init; } = [];

        /// <summary>Zu welchem Block gehoert die Instruktion an Index <c>i</c>?
        ///
        /// <para>Handler-Bereiche sind Block-Bereiche, der Frame kennt aber nur seinen
        /// Instruktionszeiger. Die Zuordnung einmal beim Laden zu bauen macht die Handler-Suche
        /// zu einem Array-Zugriff statt einer Suche in der Block-Offset-Tabelle.</para></summary>
        public int[] BlockOfInstruction { get; init; } = [];

        public static Prepared From(BytecodeFunction function, BytecodeHandler[] handlers)
        {
            var instructions = CodeDecoder.Decode(function.Code).ToArray();
            var indexByOffset = new Dictionary<int, int>(instructions.Length);
            for (var i = 0; i < instructions.Length; i++) indexByOffset[instructions[i].Offset] = i;

            var blockStart = new int[function.BlockOffsets.Count];
            for (var b = 0; b < blockStart.Length; b++)
                blockStart[b] = indexByOffset[function.BlockOffsets[b]];

            // Umkehrung der Block-Tabelle: jede Instruktion bekommt ihren Block. Einmal beim
            // Laden, damit die Handler-Suche zur Laufzeit ein Array-Zugriff ist.
            var blockOf = new int[instructions.Length];
            for (var b = 0; b < blockStart.Length; b++)
            {
                var upTo = b + 1 < blockStart.Length ? blockStart[b + 1] : instructions.Length;
                for (var i = blockStart[b]; i < upTo; i++) blockOf[i] = b;
            }

            return new Prepared
            {
                Source = function, Instructions = instructions, BlockStart = blockStart,
                Handlers = handlers, BlockOfInstruction = blockOf,
            };
        }
    }

    private sealed class Frame
    {
        public required Prepared Fn { get; init; }
        public required LyrValue[] Slots { get; init; }
        public required LyrValue[] Stack { get; init; }
        public int Sp;
        public int Ip;

        /// <summary>Die Exception, die gerade durch diesen Frame laeuft — <c>UnwindType &lt; 0</c>
        /// heisst „keine".
        ///
        /// <para>Gebraucht wird das nur zwischen dem Betreten einer <c>finally</c>-Region und
        /// ihrem <c>endfinally</c>: solange laeuft gewoehnlicher Code, aber die Abwicklung ist
        /// nicht abgeschlossen.</para></summary>
        public LyrValue Unwinding;

        public int UnwindType = -1;

        /// <summary>Ab welchem Handler die Suche nach dem <c>endfinally</c> weitergeht. Ohne den
        /// Index faende dieselbe finally-Region sich selbst wieder.</summary>
        public int NextHandler;

        /// <summary>Der Block, in dem geworfen wurde. Bleibt ueber ein <c>finally</c> hinweg
        /// stehen: die Handler-Bereiche sprechen ueber den Ursprung, nicht ueber die Stelle, an
        /// der die Aufraeumarbeit endet.</summary>
        public int UnwindBlock;

        /// <summary>Block 0 ist der Einstieg — die IR garantiert <c>Entry == bb0</c>, und das
        /// Format schreibt es fest.</summary>
        public static Frame For(Prepared fn) => new()
        {
            Fn = fn,
            Slots = new LyrValue[fn.Source.SlotTypes.Count],
            Stack = new LyrValue[Math.Max(fn.Source.MaxStack, 1)],
            Ip = fn.BlockStart[0],
        };

        public void Push(LyrValue value) => Stack[Sp++] = value;
        public LyrValue Pop() => Stack[--Sp];

        /// <summary>Leert den Operanden-Stack. Beim Sprung in einen Handler noetig: der
        /// abgebrochene Ausdruck kann Zwischenwerte hinterlassen haben, und ein Handler-Block
        /// faengt wie jeder Block mit leerem Stack an.</summary>
        public void ClearStack() => Sp = 0;

        /// <summary>Liest ohne zu entnehmen; <paramref name="depth"/> 0 ist das oberste Element.
        /// Gebraucht von <c>callvirt</c>, das seinen Empfaenger unter den Argumenten findet.</summary>
        public LyrValue Peek(int depth) => Stack[Sp - 1 - depth];
    }
}
