using Lyric.AST;
using Lyric.Core;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Baut die <b>Fabrik</b> einer Coroutine: die Funktion, die unter dem geschriebenen Namen steht
/// und ein Zustandsobjekt liefert (Sprache.md §8).
///
/// <para>Sie ist winzig und tut genau drei Dinge: Objekt anlegen, Parameter hineinschreiben,
/// zurueckgeben. Der Wiedereintrittspunkt in Feld 0 bleibt auf 0 — „noch nicht gestartet" — weil
/// ein frisch angelegtes Objekt seine Felder genullt bekommt.</para>
///
/// <para><b>Warum eine eigene Datei und kein Modus im FunctionLowerer</b>: die Fabrik lowert
/// keinen geschriebenen Code. Sie hat keinen Rumpf, keine Ausdruecke und keinen Kontrollfluss —
/// sie im grossen Lowerer unterzubringen hiesse, dort einen Zweig zu haben, der von dessen
/// gesamter Maschinerie nichts benutzt.</para>
/// </summary>
internal static class CoroutineFactory
{
    public static IrFunction Build(FunctionDecl decl, string name, TypeId state, IrType yieldType,
        FunctionId body, IrType[] parameterTypes, bool hasReceiver, IrType? receiverType, Span span)
    {
        var slots = new SlotAllocator();
        var blocks = new List<IrBlock>();
        var builder = new BlockBuilder(blocks); // legt bb0 an und setzt den Cursor darauf

        // Die Parameter der Fabrik sind die der Coroutine — in derselben Reihenfolge, damit ein
        // Aufrufer nichts anders macht als bei jeder anderen Funktion.
        if (hasReceiver && receiverType is not null) slots.Declare("this", receiverType);
        for (var i = 0; i < decl.Parameters.Length; i++)
            slots.Declare(decl.Parameters[i].Name, parameterTypes[i]);

        var stateType = new IrRefType(state);
        var instance = slots.NewTemp(stateType);
        builder.Emit(new NewObject(instance, state, stateType, span));

        // Feld 0 ist der Wiedereintrittspunkt und bleibt 0. Dahinter kommen erst 'this', dann die
        // Parameter — dieselbe Reihenfolge, die der Rumpf-Lowerer beim Anlegen benutzt.
        var field = 1;
        var slot = 0;

        if (hasReceiver && receiverType is not null)
        {
            var value = slots.NewTemp(receiverType);
            builder.Emit(new LoadLocal(value, new LocalId(slot), receiverType, span));
            builder.Emit(new StoreField(instance, state, new FieldId(field), value, span));
            field++;
            slot++;
        }

        for (var i = 0; i < decl.Parameters.Length; i++, field++, slot++)
        {
            var value = slots.NewTemp(parameterTypes[i]);
            builder.Emit(new LoadLocal(value, new LocalId(slot), parameterTypes[i], span));
            builder.Emit(new StoreField(instance, state, new FieldId(field), value, span));
        }

        // Der Rueckgabewert ist eine CLOSURE ueber dem Zustandsobjekt (ADR-018): Fat Pointer aus
        // Objektreferenz und Rumpf-Index. Damit ist 'resume co' ein gewoehnliches 'callind' und
        // braucht weder einen Opcode noch einen Wert-Typ fuer sich.
        var signature = new IrFunctionType([], yieldType);
        var closure = slots.NewTemp(signature);
        builder.Emit(new MakeClosure(closure, body, instance, signature, span));
        builder.Seal(new Return(closure, span));

        return new IrFunction(name, signature,
            decl.Parameters.Length + (hasReceiver ? 1 : 0), slots.Locals, slots.Temps, blocks)
        {
            Entry = new BlockId(0),
        };
    }
}
