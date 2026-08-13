using Lyric.AST;
using Lyric.Core;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Builds the FACTORY of a coroutine: the function that stands under the written name and yields a
/// state object.
///
/// <para>It is tiny and does exactly three things: allocate the object, write the parameters into it,
/// return it. The re-entry point in field 0 stays at 0 — "not started yet" — because a freshly
/// allocated object has its fields zeroed.</para>
///
/// <para>A file of its own rather than a mode in the FunctionLowerer, because the factory lowers no
/// written code. It has no body, no expressions and no control flow; housing it in the big lowerer
/// would mean a branch there that uses none of its machinery.</para>
/// </summary>
internal static class CoroutineFactory
{
    public static IrFunction Build(FunctionDecl decl, string name, TypeId state, IrType yieldType,
        FunctionId body, IrType[] parameterTypes, bool hasReceiver, IrType? receiverType, Span span)
    {
        var slots = new SlotAllocator();
        var blocks = new List<IrBlock>();
        var builder = new BlockBuilder(blocks); // creates bb0 and points the cursor at it

        // The factory's parameters are the coroutine's, in the same order, so a caller does nothing
        // different from any other function.
        if (hasReceiver && receiverType is not null) slots.Declare("this", receiverType);
        for (var i = 0; i < decl.Parameters.Length; i++)
            slots.Declare(decl.Parameters[i].Name, parameterTypes[i]);

        var stateType = new IrRefType(state);
        var instance = slots.NewTemp(stateType);
        builder.Emit(new NewObject(instance, state, stateType, span));

        // Field 0 is the re-entry point and stays 0. Behind it come 'this' first, then the parameters —
        // the same order the body lowerer uses when allocating.
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

        // The return value is a CLOSURE over the state object: a fat pointer of object reference and
        // body index. That makes 'resume co' an ordinary 'callind', needing neither an opcode nor a
        // value type of its own.
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
