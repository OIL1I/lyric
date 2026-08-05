using Lyric.Core;

namespace Lyric.Ir;

//Ir Consts
public abstract record IrConstValue;
public sealed record IntConst(ulong Value) : IrConstValue;
public sealed record FloatConst(double Value) : IrConstValue;
public sealed record BoolConst(bool Value) : IrConstValue;
public sealed record CharConst(int CodePoint) : IrConstValue;
public sealed record StringConst(string Value) : IrConstValue;

public abstract record IrInst(Span Span);
public abstract record IrOp(Span Span) : IrInst(Span);
public abstract record IrTerminator(Span Span) : IrInst(Span);

//Ir Op
public sealed record Const(TempId Dest, IrType Type, IrConstValue Value, Span Span) : IrOp(Span);
public sealed record BinOp(TempId Dest, IrBinKind Kind, IrType Type, TempId Lhs, TempId Rhs, Span Span) : IrOp(Span);
public sealed record UnOp(TempId Dest, IrUnKind Kind, IrType Type, TempId Operand, Span Span) : IrOp(Span);
public sealed record Convert(TempId Dest, IrType From, IrType To, TempId Operand, Span Span) : IrOp(Span);
public sealed record LoadLocal(TempId Dest, LocalId Local, IrType Type, Span Span) : IrOp(Span);
public sealed record StoreLocal(LocalId Local, TempId Value, Span Span) : IrOp(Span);
public sealed record Call(TempId? Dest, FunctionId Target, TempId[] Args, Span Span) : IrOp(Span); // Dest == null -> gwd. void

// Aufruf einer nativ hinterlegten Funktion (Stdlib). Eigene Instruktion statt eines gemeinsamen
// Index-Raums mit Call: in der IR sind das zwei verschiedene Dinge — eines hat einen Rumpf, das
// andere nicht —, und der Verifier prüft jedes gegen seine eigene Tabelle. Die Index-Arithmetik
// gehört dorthin, wo die Konvention lebt: in den Bytecode-Writer.
public sealed record CallImport(TempId? Dest, ImportId Target, TempId[] Args, Span Span) : IrOp(Span);

// Objekte (Sprache.md §3.3). Der Feldzugriff läuft über den Index, nicht über den Namen: Lyric ist
// statisch typisiert und kennt kein Monkey-Patching, also steht der Index zur Compile-Zeit fest.
// Namens-Lookup mit Inline-Cache (CPython, Ruby) löst ein Problem, das diese Sprache nicht hat.
// Der Typ steht an jeder der drei Instruktionen, obwohl das Objekt ihn kennt — nur so kann der
// Bytecode-Leser den Feldindex beim Laden gegen ein Layout prüfen, ohne Datenfluss-Analyse.
public sealed record NewObject(TempId Dest, TypeId Type, Span Span) : IrOp(Span);
public sealed record LoadField(TempId Dest, TempId Object, TypeId Type, FieldId Field, IrType FieldType, Span Span) : IrOp(Span);
public sealed record StoreField(TempId Object, TypeId Type, FieldId Field, TempId Value, Span Span) : IrOp(Span);

// Arrays (Sprache.md §4). Ein Element-Index ist ein LAUFZEITWERT — anders als Feld- und Typ-Indizes
// ist er beim Laden nicht prüfbar. Eine Verletzung ist deshalb ein panic (§9) und kein
// Ladefehler: „das Programm hat sich verrechnet" ist etwas anderes als „der Compiler hat Unsinn
// erzeugt", und nur das Zweite gehört in die Load-Zeit-Validierung.
public sealed record NewArray(TempId Dest, IrType Element, TempId[] Elements, Span Span) : IrOp(Span);
public sealed record LoadElem(TempId Dest, TempId Array, TempId Index, IrType Element, Span Span) : IrOp(Span);
public sealed record StoreElem(TempId Array, TempId Index, TempId Value, Span Span) : IrOp(Span);
public sealed record ArrayLen(TempId Dest, TempId Array, Span Span) : IrOp(Span);

// xs + ys und xs * n (Sprache.md §6.5) — eingebaute Sprachsemantik, keine Bibliothek. Beide
// liefern ein NEUES Array: T[] waechst nicht (ADR-016).
public sealed record ArrayConcat(TempId Dest, TempId Left, TempId Right, IrType Element, Span Span) : IrOp(Span);
public sealed record ArrayRepeat(TempId Dest, TempId Array, TempId Count, IrType Element, Span Span) : IrOp(Span);

// Optionals (Sprache.md §7). '??', '??=' und '?.' stehen NICHT hier: sie werten ihre rechte Seite
// nur bedingt aus und lowern zu Verzweigungen ueber OptIsSome — wie && und ||.
public sealed record OptNone(TempId Dest, IrType Inner, Span Span) : IrOp(Span);
public sealed record OptSome(TempId Dest, TempId Value, IrType Inner, Span Span) : IrOp(Span);
public sealed record OptIsSome(TempId Dest, TempId Option, Span Span) : IrOp(Span);
public sealed record OptGet(TempId Dest, TempId Option, IrType Inner, Span Span) : IrOp(Span);

// Enums (Sprache.md §3.4). 'match' steht NICHT hier: es liest das Tag und verzweigt darueber wie
// jede andere Fallunterscheidung. Nach der Verzweigung engt EnumAs auf die Variante ein, und der
// Feldzugriff darauf ist ein gewoehnliches LoadField mit ihrem Layout — dieselbe Arbeitsteilung
// wie optissome/optget.
public sealed record NewVariant(TempId Dest, TypeId Variant, TypeId Enum, TempId[] Fields, Span Span) : IrOp(Span);
public sealed record EnumTag(TempId Dest, TempId Value, Span Span) : IrOp(Span);
public sealed record EnumAs(TempId Dest, TempId Value, TypeId Variant, Span Span) : IrOp(Span);

//Ir Terminator
public sealed record Return(TempId? Value, Span Span) : IrTerminator(Span); //Value == null -> void-return
public sealed record Branch(BlockId Target, Span Span) : IrTerminator(Span);
public sealed record CondBranch(TempId Cond, BlockId IfTrue, BlockId IfFalse, Span Span) : IrTerminator(Span);
public sealed record Unreachable(Span Span) : IrTerminator(Span);