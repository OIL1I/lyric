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
/// <param name="Result">Ob dieser Typ als Referenz oder als Wert gebunden wird — Kopie fuer den
/// Printer, die Temp-Tabelle bleibt die Autoritaet. Ohne sie druckte der Dump fuer ein struct
/// <c>&amp;ty0</c> statt <c>val ty0</c>: eine Zeile, die etwas anderes behauptet als das, was
/// ausgefuehrt wird, und beim Suchen eines Kopier-Bugs genau in die Irre fuehrt.</param>
public sealed record NewObject(TempId Dest, TypeId Type, IrType Result, Span Span) : IrOp(Span);
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

// Interfaces (P3). Dieselbe Arbeitsteilung wie bei Optionals und Enums: eine Instruktion
// materialisiert die Darstellung, eine konsumiert sie. MakeInterface heftet den konkreten Typ an
// eine Objektreferenz (er steht zur Compile-Zeit fest), CallVirt holt daran seine Zielfunktion.
// Ein 'downcast' gibt es bewusst nicht — Sprache.md kennt keinen, und ohne ihn braucht der
// Interface-Wert keine Laufzeit-Typpruefung.
public sealed record MakeInterface(TempId Dest, TempId Value, TypeId Concrete, TypeId Interface,
    Span Span) : IrOp(Span);

/// <param name="Slot">Index in die Methoden-Slots des Interfaces — nicht sein Name. Wie beim
/// Feldindex steht er zur Compile-Zeit fest, weil Lyric statisch typisiert ist und kein
/// Monkey-Patching kennt.</param>
/// <param name="ReturnType">Kopie fuer den Printer; die Temp-Tabelle bleibt die Autoritaet, und
/// dass beide uebereinstimmen, prueft der Verifier. Ohne sie liesse sich eine callvirt-Zeile nicht
/// aus der Instruktion allein formatieren — anders als bei <c>Call</c> gibt es keine Zielfunktion,
/// die man nach ihrem Rueckgabetyp fragen koennte.</param>
public sealed record CallVirt(TempId? Dest, TypeId Interface, int Slot, TempId[] Args,
    IrType ReturnType, Span Span) : IrOp(Span);

// Structs (P4). Die Wert-Semantik steckt vollstaendig in dieser einen Instruktion: das Lowering
// setzt sie an jeden Bindepunkt, an dem ein Struct-Wert aus einer bestehenden Stelle gelesen wird.
// Ein frisch gebauter Wert (newobj, Call-Ergebnis) braucht sie nicht — er gehoert noch niemandem.
public sealed record StructCopy(TempId Dest, TempId Value, TypeId Type, Span Span) : IrOp(Span);

// Globals (P5c). Wie LoadLocal/StoreLocal, nur modulweit statt frameweit — und geschrieben wird
// nur einmal, von der Init-Funktion.
public sealed record LoadGlobal(TempId Dest, GlobalId Global, IrType Type, Span Span) : IrOp(Span);
public sealed record StoreGlobal(GlobalId Global, TempId Value, Span Span) : IrOp(Span);

//Ir Terminator
public sealed record Return(TempId? Value, Span Span) : IrTerminator(Span); //Value == null -> void-return
public sealed record Branch(BlockId Target, Span Span) : IrTerminator(Span);
public sealed record CondBranch(TempId Cond, BlockId IfTrue, BlockId IfFalse, Span Span) : IrTerminator(Span);
public sealed record Unreachable(Span Span) : IrTerminator(Span);

// Exceptions (P5). 'throw' ist ein Terminator, kein Op: nach ihm laeuft in diesem Block nichts
// mehr, und das strukturell festzuhalten ist dieselbe Entscheidung wie bei 'return'.
/// <param name="Concrete">Der konkrete Typ des geworfenen Wertes, oder <c>null</c>, wenn er erst
/// zur Laufzeit feststeht (der Wert ist interface-typisiert und traegt ihn als Fat Pointer mit).
///
/// <para>Dass der statische Typ hier ueberhaupt reicht, ist eine Folge von ADR-003: Lyric hat
/// <b>keine Inheritance</b>. Eine Klasse ist genau ihr Typ, es gibt keine Untertypen — also ist
/// der Typ an der Wurfstelle derselbe, den ein <c>catch</c> vergleicht. In C# oder Java waere das
/// falsch und man braeuchte ein Tag im Objekt.</para></param>
public sealed record Throw(TempId Value, TypeId? Concrete, Span Span) : IrTerminator(Span);

/// <summary>
/// Ende einer <c>finally</c>-Region: die Abwicklung geht dort weiter, wo sie unterbrochen wurde.
///
/// <para><b>Lyric hat kein <c>finally</c></b> (ADR-009) — diese Region entsteht ausschliesslich aus
/// <c>defer</c>. Auf Bytecode-Ebene braucht „laeuft auch beim Abwickeln" aber genau diesen
/// Mechanismus; die Sprache bleibt bei einem Schluesselwort, das Format bekommt den Traeger dafuer.
/// </para>
/// </summary>
public sealed record EndFinally(Span Span) : IrTerminator(Span);