using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Lowert <b>eine</b> Funktion vom typgeprüften AST in eine <see cref="IrFunction"/>. Ein Objekt pro
/// Funktion (wie <c>IrVerifier.FunctionVerifier</c>), weil Slots, Blöcke und Loop-Stack
/// funktionslokal sind und mit der Funktion sterben.
///
/// <para><b>Statements liefern <c>bool</c>: „fällt der Kontrollfluss durch?"</b> Das ist die
/// tragende Signatur-Entscheidung. Ohne sie kann man nicht entscheiden, ob ein Merge-Block
/// angelegt werden darf — und ein Merge-Block ohne Prädecessoren ist unerreichbar, was der
/// Verifier ablehnt (bewusst: kein <c>SimplifyCfg</c>-Pass in v1). Aus demselben Grund bricht
/// <see cref="LowerStatements"/> ab, sobald ein Statement nicht durchfällt: Code nach einem
/// <c>return</c> darf keinen Block erzeugen.</para>
///
/// <para><b>Werte über Blockgrenzen laufen durch Locals, nicht durch Temps.</b> Ein Temp wird
/// genau einmal definiert, kann also nicht „das Ergebnis aus zwei Zweigen" tragen. if-Ausdruck
/// und <c>&amp;&amp;</c>/<c>||</c> legen darum ein synthetisches Local an, schreiben in beiden Zweigen
/// hinein und lesen es im Merge-Block. Genau deshalb braucht diese IR kein <c>Phi</c>: das
/// Ziel ist eine Stack-VM mit Local-Slots (ADR-001), ein Phi müsste beim Emittieren ohnehin
/// wieder zu Store/Load werden.</para>
///
/// <para>Was P4 <b>nicht</b> lowert (die IR kann es nicht ausdrücken): Nullable, struct/class/enum,
/// Arrays, Tupel, <c>match</c>, <c>for-in</c>, Lambdas, Exceptions, <c>defer</c>, Coroutinen,
/// Generics, Stdlib-/Extern-Calls, f-Strings. Das ist gültiges Lyric, also eine <b>Diagnose</b>
/// (<c>LYR-IR0001</c>) mit Datei/Zeile/Spalte und kein Absturz — siehe
/// <see cref="UnsupportedConstructException"/>. Interne Inkonsistenzen bleiben davon getrennt und
/// werfen weiterhin <see cref="InternalCompilationException"/>.</para>
/// </summary>
internal sealed class FunctionLowerer
{
    private static readonly IrType VoidType = new IrScalarType(IrScalar.Void);
    private static readonly IrType BoolType = new IrScalarType(IrScalar.Bool);

    private readonly FunctionDecl _decl;
    private readonly string _name;
    private readonly TypeResult _types;
    private readonly IReadOnlyDictionary<FunctionSymbol, FunctionId> _functions;
    private readonly ImportTable _imports;
    private readonly TypeTable _typeTable;

    /// <summary>Slot des Empfängers (immer 0), oder <c>null</c> bei freier bzw. static-Funktion.</summary>
    private readonly LocalId? _thisSlot;
    private readonly IrType? _thisType;

    /// <summary>Typargumente der Instanz, für die gelowert wird. In P4 immer leer — der Haken sitzt
    /// in <see cref="LowerType"/>, damit die Worklist-Monomorphisierung später nur die Map füllen
    /// muss und nicht den ganzen Ausdrucks-Pfad umbauen.</summary>
    private readonly IReadOnlyDictionary<GenericParamSymbol, LyrType> _substitution;

    /// <summary>
    /// Temps, die einen <b>frisch gebauten</b> Wert halten — Ergebnis eines <c>newobj</c> oder
    /// eines Aufrufs.
    ///
    /// <para>Nur fuer Structs relevant, und dort die Grenze zwischen richtig und verschwenderisch:
    /// ein Wert, den niemand sonst haelt, muss beim Binden nicht kopiert werden. Ohne diese
    /// Unterscheidung bekaeme jedes <c>let p = P { … };</c> ein <c>structcopy</c> direkt hinter
    /// sein <c>newobj</c> — korrekt, aber offensichtlich sinnlos, und in jeder Disassembly zu
    /// sehen.</para>
    /// </summary>
    private readonly HashSet<TempId> _fresh = new();

    private readonly SlotAllocator _slots = new();
    private readonly List<IrBlock> _blocks = new();
    private readonly BlockBuilder _b;
    private readonly Stack<LoopScope> _loops = new();
    private readonly IrType _returnType;

    public FunctionLowerer(FunctionDecl decl, string name, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions,
        ImportTable imports,
        TypeTable typeTable,
        IReadOnlyDictionary<GenericParamSymbol, LyrType> substitution,
        TypeSymbol? receiver = null)
    {
        _decl = decl;
        _name = name;
        _types = types;
        _functions = functions;
        _imports = imports;
        _typeTable = typeTable;
        _substitution = substitution;
        _b = new BlockBuilder(_blocks);
        _returnType = LowerDeclaredReturnType();

        // Der Empfänger ist Parameter 0 und wird VOR den deklarierten Parametern belegt — die
        // Parameter-Konvention der IR ist positionsbasiert, ein späterer Slot wäre ein
        // Falsch-Slot-Read in der VM. Denselben Weg geht CIL mit 'this'.
        if (receiver is not null)
        {
            // Ein Enum-Empfänger ist der Enum-Typ, nicht eine seiner Varianten — welche vorliegt,
            // entscheidet erst das 'match' im Rumpf.
            // In einer Interface-Default-Methode ist 'this' der Interface-Typ selbst — welche
            // Implementierung dahintersteckt, weiss erst die Laufzeit. Ein 'this.foo()' darin wird
            // damit zu einem callvirt, und das ist die richtige Antwort.
            _thisType = receiver.Kind switch
            {
                TypeSymbolKind.Enum => _typeTable.EnumOf(receiver),
                TypeSymbolKind.Interface => _typeTable.InterfaceOf(receiver),
                // Der Empfaenger einer struct-Methode ist der Wert selbst. Dass er eine Kopie ist,
                // hat der Aufrufer erledigt — 'mut fn' mutiert damit nur diese Kopie, genau wie
                // Sprache.md §3.2 es beschreibt.
                TypeSymbolKind.Struct => _typeTable.StructOf(receiver),
                _ => _typeTable.RefTo(receiver),
            };
            _thisSlot = _slots.Declare("this", _thisType);
        }

        // Parameter-Konvention: die ersten ParamCount Locals SIND die Parameter, in Reihenfolge.
        // Ohne sie trägt die IR nirgends Parameter-Typen und ein Call wäre nicht typprüfbar.
        foreach (var p in decl.Parameters)
        {
            if (p.IsParams) throw NotSupported($"variadic parameter '{p.Name}'", p.Span);
            if (p.Default is not null) throw NotSupported($"default value for '{p.Name}'", p.Span);

            if (_types.RefOf(p) is not ParameterSymbol ps)
                throw Bug($"parameter '{p.Name}' was not bound by the type checker");
            _slots.DeclareFor(ps, LowerType(ps.Type, p.Span));
        }
    }

    public IrFunction Run()
    {
        if (_decl.Body is null) throw Bug("function has no body");

        if (LowerStatements(_decl.Body))
        {
            // Der Kontrollfluss ist aus dem Body gelaufen. Bei void ist das der Normalfall und
            // braucht das implizite 'ret'. Bei non-void hat die Return-Coverage der Sema
            // (LYR-SEM0017) bewiesen, dass jeder Pfad returnt — hierher kommt man dann nur über
            // einen divergierenden Konstrukt wie 'while (true) { }', dessen Exit-Kante nie
            // feuert. 'unreachable' ist genau dessen ehrliche Kodierung.
            _b.Seal(IsVoid(_returnType)
                ? new Return(null, _decl.Body.Span)
                : new Unreachable(_decl.Body.Span));
        }

        // Der Empfänger zählt als Parameter — er belegt Slot 0 und wird an der Aufrufstelle als
        // Argument 0 übergeben. Ohne ihn hier wäre die Parameter-Konvention verletzt, und der
        // Verifier meldet genau das ("call passes 2 arg(s), expected 1").
        return new IrFunction(_name, _returnType, _decl.Parameters.Length + (_thisSlot is null ? 0 : 1),
            _slots.Locals, _slots.Temps, _blocks) { Entry = new BlockId(0) };
    }

    // ------------------------------------------------------------------ Statements

    /// <summary>Lowert die Statements eines Blocks. Liefert false, sobald der Kontrollfluss
    /// endet — die restlichen Statements sind dann unerreichbar und werden verworfen.</summary>
    private bool LowerStatements(Block block)
    {
        foreach (var stmt in block.Statements)
            if (!LowerStmt(stmt)) return false;
        return true;
    }

    /// <summary>true = Kontrollfluss fällt durch, false = Block ist versiegelt.</summary>
    private bool LowerStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case Block b: return LowerStatements(b);
            case BindingStmt b: return LowerBinding(b);
            case ExprStmt e: LowerExprOrVoid(e.Expr); return true;
            case IfStmt s: return LowerIf(s);
            case WhileStmt s: return LowerWhile(s);
            case DoWhileStmt s: return LowerDoWhile(s);
            case ReturnStmt s: return LowerReturn(s);
            case BreakStmt s: return LowerBreak(s);
            case ContinueStmt s: return LowerContinue(s);

            case ForInStmt s: throw NotSupported("'for-in' (needs Iterator)", s.Span);
            case MatchStmt s: LowerMatch(s.Scrutinee, s.Arms, null, s.Span); return true;
            case TryStmt s: throw NotSupported("'try'/'catch'", s.Span);
            case ThrowStmt s: throw NotSupported("'throw'", s.Span);
            case DeferStmt s: throw NotSupported("'defer'", s.Span);
            case YieldStmt s: throw NotSupported("'yield' (coroutine state machine)", s.Span);
            case ErrorStmt s: throw Bug($"error statement reached lowering at {s.Span}");

            default: throw Bug($"unhandled statement {stmt.GetType().Name}");
        }
    }

    private bool LowerBinding(BindingStmt binding)
    {
        if (_types.RefOf(binding) is not LocalSymbol local)
            throw Bug($"binding '{binding.Name}' was not bound by the type checker");

        var type = LowerType(local.Type, binding.Span);
        var slot = _slots.DeclareFor(local, type);

        // Ohne Initializer bleibt der Slot ungeschrieben: die Definite-Assignment-Analyse hat
        // bewiesen, dass jeder Read eine Zuweisung sieht.
        if (binding.Initializer is not null)
            _b.Emit(new StoreLocal(slot, LowerExprAs(binding.Initializer, type), binding.Span));

        return true;
    }

    private bool LowerReturn(ReturnStmt stmt)
    {
        if (stmt.Value is null)
        {
            _b.Seal(new Return(null, stmt.Span));
            return false;
        }

        _b.Seal(new Return(LowerExprAs(stmt.Value, _returnType), stmt.Span));
        return false;
    }

    private bool LowerBreak(BreakStmt stmt)
    {
        if (_loops.Count == 0) throw Bug($"'break' outside a loop at {stmt.Span}");
        _b.Seal(new Branch(_loops.Peek().BreakTarget, stmt.Span));
        return false;
    }

    private bool LowerContinue(ContinueStmt stmt)
    {
        if (_loops.Count == 0) throw Bug($"'continue' outside a loop at {stmt.Span}");
        _b.Seal(new Branch(_loops.Peek().ContinueTarget, stmt.Span));
        return false;
    }

    /// <summary>
    /// Der Merge-Block wird erst angelegt, wenn mindestens ein Zweig durchfällt. Bei
    /// <c>if (c) { return 1; } else { return 2; }</c> entsteht keiner — er hätte keine
    /// Prädecessoren und der Verifier würde ihn als unerreichbar melden.
    /// </summary>
    private bool LowerIf(IfStmt stmt)
    {
        var condition = LowerExpr(stmt.Condition);
        var thenBlock = _b.NewBlock();

        if (stmt.Else is null)
        {
            // Ohne else ist der false-Zweig der Merge-Block; er ist über die false-Kante
            // garantiert erreichbar und darf deshalb sofort entstehen.
            var merge = _b.NewBlock();
            _b.Seal(new CondBranch(condition, thenBlock, merge, stmt.Span));

            _b.SwitchTo(thenBlock);
            if (LowerStatements(stmt.Then)) _b.Seal(new Branch(merge, stmt.Then.Span));

            _b.SwitchTo(merge);
            return true;
        }

        var elseBlock = _b.NewBlock();
        _b.Seal(new CondBranch(condition, thenBlock, elseBlock, stmt.Span));

        _b.SwitchTo(thenBlock);
        var thenFallsThrough = LowerStatements(stmt.Then);
        var thenExit = _b.CurrentId; // nach verschachteltem Kontrollfluss nicht mehr thenBlock

        _b.SwitchTo(elseBlock);
        var elseFallsThrough = LowerStmt(stmt.Else); // Block oder else-if
        var elseExit = _b.CurrentId;

        if (!thenFallsThrough && !elseFallsThrough) return false;

        var mergeBlock = _b.NewBlock();
        if (thenFallsThrough) _b.SealBlock(thenExit, new Branch(mergeBlock, stmt.Then.Span));
        if (elseFallsThrough) _b.SealBlock(elseExit, new Branch(mergeBlock, stmt.Else.Span));

        _b.SwitchTo(mergeBlock);
        return true;
    }

    private bool LowerWhile(WhileStmt stmt)
    {
        var condBlock = _b.NewBlock();
        _b.Seal(new Branch(condBlock, stmt.Span));

        _b.SwitchTo(condBlock);
        var condition = LowerExpr(stmt.Condition);
        var condExit = _b.CurrentId; // die Bedingung kann selbst Blöcke erzeugt haben (&&, ||)

        var bodyBlock = _b.NewBlock();
        var exitBlock = _b.NewBlock(); // muss vor dem Body stehen: 'break' braucht sein Ziel
        _b.SealBlock(condExit, new CondBranch(condition, bodyBlock, exitBlock, stmt.Condition.Span));

        _b.SwitchTo(bodyBlock);
        _loops.Push(new LoopScope(ContinueTarget: condBlock, BreakTarget: exitBlock));
        if (LowerStatements(stmt.Body)) _b.Seal(new Branch(condBlock, stmt.Body.Span));
        _loops.Pop();

        _b.SwitchTo(exitBlock);
        return true; // über die false-Kante der Bedingung immer erreichbar
    }

    private bool LowerDoWhile(DoWhileStmt stmt)
    {
        var bodyBlock = _b.NewBlock();
        var condBlock = _b.NewBlock(); // 'continue' springt zur Bedingung, nicht an den Body-Anfang
        var exitBlock = _b.NewBlock();
        _b.Seal(new Branch(bodyBlock, stmt.Span));

        _b.SwitchTo(bodyBlock);
        _loops.Push(new LoopScope(ContinueTarget: condBlock, BreakTarget: exitBlock));
        if (LowerStatements(stmt.Body)) _b.Seal(new Branch(condBlock, stmt.Body.Span));
        _loops.Pop();

        _b.SwitchTo(condBlock);
        var condition = LowerExpr(stmt.Condition);
        _b.Seal(new CondBranch(condition, bodyBlock, exitBlock, stmt.Condition.Span));

        _b.SwitchTo(exitBlock);
        return true;
    }

    // ------------------------------------------------------------------ Ausdrücke

    private TempId LowerExpr(Expr expr) =>
        LowerExprOrVoid(expr) ?? throw Bug($"expression at {expr.Span} produced no value");

    /// <summary>Liefert null nur für den Aufruf einer void-Funktion — der einzige Ausdruck ohne
    /// Wert. Sonst immer ein Temp.</summary>
    private TempId? LowerExprOrVoid(Expr expr) => expr switch
    {
        IntLiteralExpr e => LowerIntLiteral(e),
        FloatLiteralExpr e => LowerFloatLiteral(e),
        BoolLiteralExpr e => EmitConst(new BoolConst(e.Value), TypeOfExpr(e), e.Span),
        CharLiteralExpr e => EmitConst(new CharConst(e.CodePoint), TypeOfExpr(e), e.Span),
        StringLiteralExpr e => EmitConst(new StringConst(e.Value), TypeOfExpr(e), e.Span),
        IdentifierExpr e => LowerIdentifier(e),
        UnaryExpr e => LowerUnary(e),
        PostfixExpr e => LowerPostfix(e),
        BinaryExpr e => LowerBinary(e),
        AssignExpr e => LowerAssign(e),
        CastExpr e => LowerCast(e),
        CallExpr e => LowerCall(e),
        IfExpr e => LowerIfExpr(e),

        InterpolatedStringExpr e => LowerInterpolatedString(e),

        NullLiteralExpr e => LowerNull(e),
        LambdaExpr e => throw NotSupported("lambda (needs closure lifting)", e.Span),
        MatchExpr e => LowerMatch(e.Scrutinee, e.Arms, TypeOfExpr(e), e.Span)
                       ?? throw Bug($"match expression produced no value at {e.Span}"),
        MemberExpr e => LowerFieldRead(e),
        IndexExpr e => LowerIndexRead(e),
        ArrayLitExpr e => LowerArrayLiteral(e),
        TupleLitExpr e => throw NotSupported("tuple literal", e.Span),
        StructInitExpr e => LowerObjectInit(e),
        RangeExpr e => throw NotSupported("range expression", e.Span),
        ResumeExpr e => throw NotSupported("'resume'", e.Span),
        ThisExpr e => LowerThis(e),
        AtIdentifierExpr e => throw NotSupported($"attribute '{e.Name}'", e.Span),
        ErrorExpr e => throw Bug($"error expression reached lowering at {e.Span}"),

        _ => throw Bug($"unhandled expression {expr.GetType().Name}")
    };

    private TempId LowerIntLiteral(IntLiteralExpr expr)
    {
        var type = TypeOfExpr(expr);

        // Ein untypisiertes Ganzzahl-Literal in Float-Kontext IST ein Float-Wert, es wird nicht
        // konvertiert (Sprache.md §6.5). `let f: float = 5;` muss also ein FloatConst werden —
        // ein IntConst mit Float-Typ wäre malformed, und der Verifier sagt das auch.
        if (type is IrScalarType { Kind: IrScalar.F32 or IrScalar.F64 })
            return EmitConst(new FloatConst(expr.Value), type, expr.Span);

        // Die Kodierung von IntConst ist Zweierkomplement, nullerweitert auf 64 Bit. Der Parser
        // liefert die Magnitude; ein Minuszeichen ist ein eigener UnaryExpr(Neg).
        return EmitConst(new IntConst(expr.Value), type, expr.Span);
    }

    private TempId LowerFloatLiteral(FloatLiteralExpr expr)
    {
        var type = TypeOfExpr(expr);
        // f32 muss hier verengt werden: ein Const vom Typ f32, dessen Wert kein f32-Wert ist,
        // wäre malformed (und der Verifier meldet es). Die Verengung gehört ins Lowering, damit
        // der Wert im Bytecode deterministisch derselbe ist.
        var value = type is IrScalarType { Kind: IrScalar.F32 } ? (float)expr.Value : expr.Value;
        return EmitConst(new FloatConst(value), type, expr.Span);
    }

    private TempId LowerIdentifier(IdentifierExpr expr)
    {
        var symbol = _types.RefOf(expr) ?? throw Bug($"identifier '{expr.Name}' is unbound");
        if (!_slots.TryLookup(symbol, out var slot))
            throw NotSupported($"reference to '{expr.Name}' (only parameters and locals)", expr.Span);

        var type = _slots.TypeOfLocal(slot);
        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(dest, slot, type, expr.Span));

        // Flow-Narrowing (§7): nach `if (x != null)` sagt die Sema für x den Typ T, der SLOT hält
        // aber weiter ?T — die Einengung ist eine Aussage über den Kontrollfluss, keine über den
        // Speicher. Genau hier wird sie eingelöst: der Lowerer packt aus, wo die Sema T erwartet.
        //
        // Dass das sicher ist, hat die Sema bewiesen — sie engt nur ein, wo sie null ausgeschlossen
        // hat. Das optget kann deshalb nie panicken; es ist die Materialisierung eines schon
        // geführten Beweises.
        if (type is IrOptionalType option && TypeOfExpr(expr) is not IrOptionalType)
        {
            var narrowed = _slots.NewTemp(option.Inner);
            _b.Emit(new OptGet(narrowed, dest, option.Inner, expr.Span));
            return narrowed;
        }

        return dest;
    }

    private TempId LowerUnary(UnaryExpr expr)
    {
        if (expr.Operator is UnaryOp.PreInc or UnaryOp.PreDec)
            return LowerIncDec(expr.Operand, expr.Operator is UnaryOp.PreInc,
                yieldOldValue: false, expr.Span);

        var operand = LowerExpr(expr.Operand);
        var type = TypeOfExpr(expr);
        var dest = _slots.NewTemp(type);
        _b.Emit(new UnOp(dest, IrUnKindExtensions.FromAst(expr.Operator), type, operand, expr.Span));
        return dest;
    }

    private TempId LowerPostfix(PostfixExpr expr) => expr.Operator switch
    {
        PostfixOp.Inc => LowerIncDec(expr.Operand, increment: true, yieldOldValue: true, expr.Span),
        PostfixOp.Dec => LowerIncDec(expr.Operand, increment: false, yieldOldValue: true, expr.Span),
        PostfixOp.ForceUnwrap => LowerForceUnwrap(expr.Operand, expr.Span),
        _ => throw Bug($"unhandled postfix operator {expr.Operator}")
    };

    /// <summary><c>++</c>/<c>--</c> in beiden Stellungen: Prefix liefert den neuen Wert, Postfix den
    /// alten. Beide schreiben denselben Store.</summary>
    private TempId LowerIncDec(Expr target, bool increment, bool yieldOldValue, Span span)
    {
        var slot = ResolveLocalTarget(target, "increment/decrement");
        var type = _slots.TypeOfLocal(slot);

        var oldValue = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(oldValue, slot, type, span));

        var one = EmitConst(OneFor(type, span), type, span);
        var newValue = _slots.NewTemp(type);
        _b.Emit(new BinOp(newValue, increment ? IrBinKind.Add : IrBinKind.Sub, type,
            oldValue, one, span));
        _b.Emit(new StoreLocal(slot, newValue, span));

        return yieldOldValue ? oldValue : newValue;
    }

    private TempId LowerBinary(BinaryExpr expr)
    {
        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr)
            return LowerShortCircuit(expr);
        if (expr.Operator is BinaryOp.Coalesce)
            return LowerCoalesce(expr);
        if (TryLowerNullTest(expr) is { } nullTest)
            return nullTest;

        var kind = IrBinKindExtensions.FromAst(expr.Operator);
        var lhs = LowerExpr(expr.Left);
        var rhs = LowerExpr(expr.Right);
        var type = TypeOfExpr(expr);

        // xs + ys und xs * n sind eingebaute Sprachsemantik (Sprache.md §6.5), aber KEIN BinOp:
        // der add-Opcode bliebe sonst polymorph und müsste zur Laufzeit Typ-Dispatch machen —
        // dieselbe Begründung wie bei string + string, nur mit eigener Instruktion statt Call.
        if (type is IrArrayType result)
        {
            var built = _slots.NewTemp(type);
            _b.Emit(kind switch
            {
                IrBinKind.Add => new ArrayConcat(built, lhs, rhs, result.Element, expr.Span),
                IrBinKind.Mul => new ArrayRepeat(built, lhs, rhs, result.Element, expr.Span),
                _ => throw NotSupported($"'{IrNames.Bin(kind)}' on arrays", expr.Span),
            });
            return built;
        }

        // Sprache.md §6.5 überlädt '+' und '*' für string; das ist eingebaute Semantik, aber KEIN
        // BinOp — sonst wäre der add-Opcode polymorph und müsste zur Laufzeit Typ-Dispatch machen
        // (gegen ADR-013). Es lowert zu einem Call, und der braucht ein Ziel, das es noch nicht gibt.
        if (!kind.IsComparison() && type is IrScalarType { Kind: IrScalar.String })
            throw NotSupported("string concatenation/repetition (lowers to a call)", expr.Span);

        var dest = _slots.NewTemp(type);
        _b.Emit(new BinOp(dest, kind, type, lhs, rhs, expr.Span));
        return dest;
    }

    /// <summary>
    /// <c>a &amp;&amp; b</c> / <c>a || b</c>: der rechte Operand darf nur bedingt laufen, also
    /// Kontrollfluss. Das Ergebnis fließt über ein synthetisches Local, weil ein Temp nur einmal
    /// definiert werden darf.
    /// </summary>
    private TempId LowerShortCircuit(BinaryExpr expr)
    {
        var isAnd = expr.Operator is BinaryOp.LogicalAnd;
        var slot = _slots.DeclareSynthetic(isAnd ? "and" : "or", BoolType);

        var left = LowerExpr(expr.Left);
        _b.Emit(new StoreLocal(slot, left, expr.Left.Span));

        var rhsBlock = _b.NewBlock();
        var mergeBlock = _b.NewBlock();
        // '&&' wertet rechts nur bei true aus, '||' nur bei false — die Kanten sind getauscht.
        _b.Seal(isAnd
            ? new CondBranch(left, rhsBlock, mergeBlock, expr.Span)
            : new CondBranch(left, mergeBlock, rhsBlock, expr.Span));

        _b.SwitchTo(rhsBlock);
        var right = LowerExpr(expr.Right);
        _b.Emit(new StoreLocal(slot, right, expr.Right.Span));
        _b.Seal(new Branch(mergeBlock, expr.Right.Span));

        _b.SwitchTo(mergeBlock);
        var dest = _slots.NewTemp(BoolType);
        _b.Emit(new LoadLocal(dest, slot, BoolType, expr.Span));
        return dest;
    }

    /// <summary>Wie <see cref="LowerShortCircuit"/>, nur mit zwei schreibenden Zweigen. Beide
    /// Zweige sind Ausdrücke und liefern garantiert einen Wert (Sprache.md §6.2), fallen also
    /// immer durch — anders als beim if-<i>Statement</i> braucht es hier keine Fallunterscheidung.</summary>
    private TempId LowerIfExpr(IfExpr expr)
    {
        var type = TypeOfExpr(expr);
        var slot = _slots.DeclareSynthetic("if", type);

        var condition = LowerExpr(expr.Condition);
        var thenBlock = _b.NewBlock();
        var elseBlock = _b.NewBlock();
        _b.Seal(new CondBranch(condition, thenBlock, elseBlock, expr.Span));

        _b.SwitchTo(thenBlock);
        _b.Emit(new StoreLocal(slot, LowerExpr(expr.Then), expr.Then.Span));
        var thenExit = _b.CurrentId;

        _b.SwitchTo(elseBlock);
        _b.Emit(new StoreLocal(slot, LowerExpr(expr.Else), expr.Else.Span));
        var elseExit = _b.CurrentId;

        var mergeBlock = _b.NewBlock();
        _b.SealBlock(thenExit, new Branch(mergeBlock, expr.Then.Span));
        _b.SealBlock(elseExit, new Branch(mergeBlock, expr.Else.Span));

        _b.SwitchTo(mergeBlock);
        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(dest, slot, type, expr.Span));
        return dest;
    }

    private TempId LowerAssign(AssignExpr expr)
    {
        if (expr.Target is MemberExpr member) return LowerFieldAssign(member, expr);
        if (expr.Target is IndexExpr indexed) return LowerElementAssign(indexed, expr);

        var slot = ResolveLocalTarget(expr.Target, "assignment");

        if (expr.Operator is null)
        {
            // Der Slot-Typ ist die erwartete Form — sonst landete bei 'var d: Damageable; d = p;'
            // eine nackte Klassenreferenz in einem Interface-Slot.
            var value = LowerExprAs(expr.Value, _slots.TypeOfLocal(slot));
            _b.Emit(new StoreLocal(slot, value, expr.Span));
            return value;
        }

        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr or BinaryOp.Coalesce)
            throw NotSupported("short-circuit or coalescing assignment", expr.Span);

        var type = _slots.TypeOfLocal(slot);
        var current = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(current, slot, type, expr.Target.Span));

        var operand = LowerExpr(expr.Value);
        var result = _slots.NewTemp(type);
        _b.Emit(new BinOp(result, IrBinKindExtensions.FromAst(expr.Operator.Value), type,
            current, operand, expr.Span));
        _b.Emit(new StoreLocal(slot, result, expr.Span));
        return result;
    }

    /// <summary>
    /// <c>obj.f = v</c> und <c>obj.f += v</c>.
    ///
    /// <para><b>Das Objekt wird genau einmal ausgewertet.</b> Bei <c>+=</c> ist das der Unterschied
    /// zwischen richtig und falsch, sobald der Ziel-Ausdruck Seiteneffekte hat: <c>next().f += 1</c>
    /// darf <c>next()</c> nicht zweimal rufen. Deshalb wird die Referenz einmal in ein Temp gelowert
    /// und für Lesen und Schreiben wiederverwendet.</para>
    /// </summary>
    private TempId LowerFieldAssign(MemberExpr member, AssignExpr expr)
    {
        var (obj, type, field, fieldType) = ResolveFieldAccess(member);

        if (expr.Operator is null)
        {
            var assigned = LowerExprAs(expr.Value, fieldType);
            _b.Emit(new StoreField(obj, type, field, assigned, expr.Span));
            return assigned;
        }

        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr or BinaryOp.Coalesce)
            throw NotSupported("short-circuit or coalescing assignment", expr.Span);

        if (fieldType is IrScalarType { Kind: IrScalar.String })
            throw NotSupported("string concatenation/repetition (lowers to a call)", expr.Span);

        var current = _slots.NewTemp(fieldType);
        _b.Emit(new LoadField(current, obj, type, field, fieldType, member.Span));

        var operand = LowerExpr(expr.Value);
        var result = _slots.NewTemp(fieldType);
        _b.Emit(new BinOp(result, IrBinKindExtensions.FromAst(expr.Operator.Value), fieldType,
            current, operand, expr.Span));
        _b.Emit(new StoreField(obj, type, field, result, expr.Span));
        return result;
    }

    /// <summary><c>this</c> ist der Slot 0. Dass er existiert, hat die Sema geprüft
    /// (<c>LYR-SEM0008</c> in einer static-Methode) — hier ist ein fehlender Slot ein Bug.</summary>
    private TempId LowerThis(ThisExpr expr)
    {
        if (_thisSlot is not { } slot || _thisType is not { } type)
            throw Bug($"'this' reached lowering outside an instance method at {expr.Span}");

        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(dest, slot, type, expr.Span));
        return dest;
    }

    /// <summary>
    /// <c>xs[i] = v</c> und <c>xs[i] += v</c>.
    ///
    /// <para>Array <b>und</b> Index werden genau einmal ausgewertet — bei <c>+=</c> ist das der
    /// Unterschied zwischen richtig und falsch, sobald einer von beiden Seiteneffekte hat:
    /// <c>xs[next()] += 1</c> darf <c>next()</c> nicht zweimal rufen.</para>
    /// </summary>
    private TempId LowerElementAssign(IndexExpr indexed, AssignExpr expr)
    {
        var (array, index, element) = ResolveIndexAccess(indexed);

        if (expr.Operator is null)
        {
            var assigned = LowerExpr(expr.Value);
            _b.Emit(new StoreElem(array, index, assigned, expr.Span));
            return assigned;
        }

        if (expr.Operator is BinaryOp.LogicalAnd or BinaryOp.LogicalOr or BinaryOp.Coalesce)
            throw NotSupported("short-circuit or coalescing assignment", expr.Span);

        if (element is IrScalarType { Kind: IrScalar.String } or IrArrayType)
            throw NotSupported("compound assignment on strings or arrays (lowers to a call)", expr.Span);

        var current = _slots.NewTemp(element);
        _b.Emit(new LoadElem(current, array, index, element, indexed.Span));

        var operand = LowerExpr(expr.Value);
        var result = _slots.NewTemp(element);
        _b.Emit(new BinOp(result, IrBinKindExtensions.FromAst(expr.Operator.Value), element,
            current, operand, expr.Span));
        _b.Emit(new StoreElem(array, index, result, expr.Span));
        return result;
    }

    // ------------------------------------------------------------------ Enums (§3.4)

    /// <summary>Der Enum-Typ, zu dem ein Wert gehört — oder eine Scope-Grenze.</summary>
    private (TypeSymbol Symbol, IrEnumType Type) RequireEnum(Expr expr)
    {
        if (_types.TypeOf(expr) is not NamedRef { Symbol.Kind: TypeSymbolKind.Enum } named)
            throw NotSupported($"'{TypeFacts.Display(_types.TypeOf(expr))}' is not an enum", expr.Span);

        return (named.Symbol, _typeTable.EnumOf(named.Symbol));
    }

    /// <summary><c>Shape.Circle(2.0)</c> und <c>Shape.Empty</c> — eine Tuple- bzw. Unit-Variante.
    /// Die Struct-Form <c>Triangle { a = … }</c> läuft über <see cref="LowerObjectInit"/>.</summary>
    private TempId LowerVariantCall(MemberExpr callee, Expr[] arguments, Span span)
    {
        var (symbol, type) = (RefEnumSymbol(callee.Target, callee.Span), (IrEnumType?)null);
        var enumType = _typeTable.EnumOf(symbol);
        var variant = _typeTable.VariantOf(symbol, callee.Member, span);

        var fields = new TempId[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) fields[i] = LowerExpr(arguments[i]);

        var dest = _slots.NewTemp(enumType);
        _b.Emit(new NewVariant(dest, variant, enumType.Type, fields, span));
        return dest;
    }

    /// <summary><c>Shape.Tri { a = 3, b = 4 }</c>. Wie beim Objekt-Literal wird in
    /// <b>Layout</b>-Reihenfolge geschrieben, ausgewertet aber in Quelltext-Reihenfolge — nur dass
    /// Slot 0 das Tag ist und die Nutzfelder bei 1 beginnen.</summary>
    private TempId LowerStructVariant(StructInitExpr expr, TypeSymbol owner)
    {
        var variantName = expr.Path[^1];
        var variant = _typeTable.VariantOf(owner, variantName, expr.Span);
        var layout = _typeTable.Defs[variant.Value];
        var enumType = _typeTable.EnumOf(owner);

        var values = new Dictionary<string, TempId>(StringComparer.Ordinal);
        foreach (var field in expr.Fields) values[field.Name] = LowerExpr(field.Value);

        var fields = new TempId[layout.FieldNames.Length - 1];
        for (var i = 1; i < layout.FieldNames.Length; i++)
        {
            if (!values.TryGetValue(layout.FieldNames[i], out var value))
                throw NotSupported($"initializer omits field '{layout.FieldNames[i]}'", expr.Span);
            fields[i - 1] = value;
        }

        var dest = _slots.NewTemp(enumType);
        _b.Emit(new NewVariant(dest, variant, enumType.Type, fields, expr.Span));
        return dest;
    }

    private TypeSymbol RefEnumSymbol(Expr target, Span span)
    {
        var bound = _types.RefOf(target);
        if (bound is ImportBindingSymbol import) bound = import.Target;
        if (bound is TypeSymbol { Kind: TypeSymbolKind.Enum } symbol) return symbol;

        throw NotSupported("variant construction on something that is not an enum", span);
    }

    /// <summary>
    /// <c>match</c> als Ausdruck und als Statement — derselbe Code, nur der Ergebnis-Slot fehlt im
    /// Statement-Fall.
    ///
    /// <para><b>Kein Sprungtabellen-Opcode.</b> Gelesen wird das Tag, verglichen wird mit einer
    /// Konstante, verzweigt wird wie überall sonst. Eine Sprungtabelle wäre eine Optimierung —
    /// die Semantik ist eine Kette von Vergleichen, und die Exhaustivität hat die Sema bereits
    /// bewiesen (<c>LYR-SEM0050</c>), weshalb der letzte Arm ohne Fallback auskommt.</para>
    /// </summary>
    private TempId? LowerMatch(Expr scrutinee, MatchArm[] arms, IrType? resultType, Span span)
    {
        var (symbol, enumType) = RequireEnum(scrutinee);
        var value = LowerExpr(scrutinee);
        var slot = resultType is null ? (LocalId?)null : _slots.DeclareSynthetic("match", resultType);

        var tag = _slots.NewTemp(new IrScalarType(IrScalar.I64));
        _b.Emit(new EnumTag(tag, value, span));

        var merge = _b.NewBlock();
        var reachesMerge = false;

        for (var i = 0; i < arms.Length; i++)
        {
            var arm = arms[i];
            if (arm.Guard is not null) throw NotSupported("a match guard", arm.Span);

            var body = _b.NewBlock();
            // Der letzte Arm braucht keine Prüfung: die Sema hat Exhaustivität bewiesen. Ein
            // Vergleich hier wäre ein unerreichbarer else-Block, und den lehnt der Verifier ab.
            if (i < arms.Length - 1)
            {
                var next = _b.NewBlock();
                var expected = EmitConst(new IntConst((ulong)TagOfPattern(symbol, arm.Pattern)),
                    new IrScalarType(IrScalar.I64), arm.Span);
                // Das Type-Feld eines Vergleichs ist sein ERGEBNIS-Typ (bool); den Operandentyp
                // schlägt der Emitter in der Temp-Tabelle nach, weil signed/unsigned verschiedene
                // Opcodes sind.
                var matches = _slots.NewTemp(BoolType);
                _b.Emit(new BinOp(matches, IrBinKind.Eq, BoolType, tag, expected, arm.Span));
                _b.Seal(new CondBranch(matches, body, next, arm.Span));
                _b.SwitchTo(body);
                if (LowerArm(arm, symbol, value, slot, resultType)) { _b.Seal(new Branch(merge, arm.Span)); reachesMerge = true; }
                _b.SwitchTo(next);
            }
            else
            {
                _b.Seal(new Branch(body, arm.Span));
                _b.SwitchTo(body);
                if (LowerArm(arm, symbol, value, slot, resultType)) { _b.Seal(new Branch(merge, arm.Span)); reachesMerge = true; }
            }
        }

        if (!reachesMerge) throw NotSupported("a match where no arm falls through", span);

        _b.SwitchTo(merge);
        if (slot is not { } result || resultType is null) return null;

        var dest = _slots.NewTemp(resultType);
        _b.Emit(new LoadLocal(dest, result, resultType, span));
        return dest;
    }

    /// <summary>Bindet die Felder der Variante und lowert den Rumpf. Liefert „fällt durch".</summary>
    private bool LowerArm(MatchArm arm, TypeSymbol symbol, TempId value, LocalId? slot, IrType? resultType)
    {
        BindPatternFields(arm.Pattern, symbol, value);

        if (arm.Body is Expr expr)
        {
            var produced = resultType is null ? LowerExprOrVoid(expr) : LowerExprAs(expr, resultType);
            if (slot is { } target && produced is { } v) _b.Emit(new StoreLocal(target, v, arm.Span));
            return true;
        }

        return LowerStatements((Block)arm.Body);
    }

    /// <summary>Der Tag, auf den ein Muster passt. Eine Unit-Variante parst als
    /// <see cref="BindingPattern"/> — ob sie eine Bindung oder eine Variante ist, weiß erst die
    /// Sema, und die hat es entschieden.</summary>
    private int TagOfPattern(TypeSymbol symbol, Pattern pattern) => pattern switch
    {
        VariantPattern v => _typeTable.TagOf(symbol, v.Path[^1], v.Span),
        BindingPattern b when _types.RefOf(pattern) is EnumVariantSymbol
            => _typeTable.TagOf(symbol, b.Name, b.Span),
        WildcardPattern => throw NotSupported("'_' anywhere but in the last arm", pattern.Span),
        _ => throw NotSupported($"a {pattern.GetType().Name} in a match over an enum", pattern.Span),
    };

    /// <summary>Zerlegt eine Variante: <c>enumas</c> engt ein, danach ist jedes Feld ein
    /// gewöhnliches <c>ldfld</c> mit dem Layout der Variante.</summary>
    private void BindPatternFields(Pattern pattern, TypeSymbol symbol, TempId value)
    {
        if (pattern is not VariantPattern variant) return;
        if (variant.TupleElements is null && variant.StructFields is null) return;

        var variantType = _typeTable.VariantOf(symbol, variant.Path[^1], variant.Span);
        var narrowed = _slots.NewTemp(new IrRefType(variantType));
        _b.Emit(new EnumAs(narrowed, value, variantType, variant.Span));

        var layout = _typeTable.Defs[variantType.Value];

        if (variant.TupleElements is { } elements)
            for (var i = 0; i < elements.Length; i++)
                BindOne(elements[i], narrowed, variantType, new FieldId(i + 1), layout.FieldTypes[i + 1]);

        if (variant.StructFields is { } fields)
            foreach (var field in fields)
            {
                var index = Array.IndexOf(layout.FieldNames, field.Name);
                if (index < 0) throw NotSupported($"unknown field '{field.Name}' in a pattern", field.Span);

                // Kurzform `{ a, b }`: kein Untermuster, der Feldname IST die Bindung. Die Sema
                // hat sie an ein LocalSymbol gebunden — an den FieldPattern-Knoten selbst.
                BindOne(field.Pattern ?? (Node)field, narrowed, variantType,
                    new FieldId(index), layout.FieldTypes[index], field.Name, field.Span);
            }
    }

    private void BindOne(Node? sub, TempId obj, TypeId variantType, FieldId field, IrType type,
        string? shorthandName = null, Span shorthandSpan = default)
    {
        // Nur Bindungen und '_' — verschachtelte Muster brauchen rekursive Dekomposition und sind
        // eine eigene Ausbaustufe.
        if (sub is null or WildcardPattern) return;

        var name = sub is BindingPattern binding ? binding.Name : shorthandName;
        if (name is null)
            throw NotSupported($"a nested {sub.GetType().Name} in a pattern", sub.Span);

        // Die Sema hat der Muster-Bindung schon ein LocalSymbol gegeben — über dasselbe Symbol
        // findet LowerIdentifier den Slot später wieder. Ein eigener Namensraum hier wäre eine
        // zweite Wahrheit über Scoping.
        if (_types.RefOf(sub) is not LocalSymbol local)
            throw Bug($"pattern binding '{name}' was not bound by the type checker");

        var span = sub.Span == default ? shorthandSpan : sub.Span;
        var slot = _slots.DeclareFor(local, type);
        var loaded = _slots.NewTemp(type);
        _b.Emit(new LoadField(loaded, obj, variantType, field, type, span));
        _b.Emit(new StoreLocal(slot, loaded, span));
    }

    // ------------------------------------------------------------------ Optionals (§7)

    /// <summary>
    /// Ein Ausdruck an einer Position mit <b>erwartetem Typ</b>. Zwei Dinge passieren nur hier:
    /// <c>null</c> bekommt seinen Typ (es hat keinen eigenen — die Sema gibt ihm <c>NullType</c>),
    /// und ein <c>T</c> wird zu <c>?T</c> verpackt, weil §6.5 die Richtung implizit erlaubt.
    /// </summary>
    private TempId LowerExprAs(Expr expr, IrType expected)
    {
        if (expr is NullLiteralExpr)
        {
            if (expected is not IrOptionalType option)
                throw NotSupported("'null' outside an optional context", expr.Span);

            var none = _slots.NewTemp(expected);
            _b.Emit(new OptNone(none, option.Inner, expr.Span));
            return none;
        }

        return Coerce(LowerExpr(expr), TypeOfExpr(expr), expected, expr.Span);
    }

    /// <summary>Ein <c>null</c> ohne erwarteten Typ. Sollte nie vorkommen — jede Position, an der
    /// <c>null</c> gültig ist, kennt ihren Zieltyp und geht über <see cref="LowerExprAs"/>.</summary>
    private TempId LowerNull(NullLiteralExpr expr) =>
        throw NotSupported("'null' in a position without an expected type", expr.Span);

    /// <summary>
    /// <c>x != null</c> und <c>x == null</c> sind <b>keine</b> Vergleiche, sondern die Frage nach
    /// der Anwesenheit eines Wertes — genau das, was <c>optissome</c> beantwortet. Ein echter
    /// Vergleich würde einen <c>null</c>-Wert auf den Stack verlangen, und den gibt es nicht:
    /// „kein Wert" ist eine leere Referenz, kein Operand.
    /// </summary>
    private TempId? TryLowerNullTest(BinaryExpr expr)
    {
        if (expr.Operator is not (BinaryOp.Eq or BinaryOp.Ne)) return null;

        var (option, _) = expr.Right is NullLiteralExpr ? (expr.Left, expr.Right)
            : expr.Left is NullLiteralExpr ? (expr.Right, expr.Left)
            : (null, null);
        if (option is null) return null;

        var value = LowerExpr(option);
        if (TypeOfExpr(option) is not IrOptionalType) throw NotSupported("null test on a non-optional", expr.Span);

        var isSome = _slots.NewTemp(BoolType);
        _b.Emit(new OptIsSome(isSome, value, expr.Span));
        if (expr.Operator is BinaryOp.Ne) return isSome;

        // '== null' ist die Verneinung. 'not' ist der einzige Opcode ohne Typ-Tag — nur bool.
        var isNone = _slots.NewTemp(BoolType);
        _b.Emit(new UnOp(isNone, IrUnKind.Not, BoolType, isSome, expr.Span));
        return isNone;
    }

    /// <summary>
    /// Passt einen Wert an den Typ an, den seine Position erwartet. Zwei implizite Uebergaenge
    /// kennt die Sprache, und beide werden hier materialisiert:
    ///
    /// <para><c>T</c> → <c>?T</c> ist implizit (Sprache.md §6.5) und wird zu <c>optsome</c>.</para>
    ///
    /// <para>Ein Klassen- oder Enum-Wert → sein Interface wird zu <c>mkiface</c>: der Interface-Wert
    /// ist ein Fat Pointer, der den konkreten Typ mitfuehrt, und der steht genau hier zur
    /// Compile-Zeit fest. Spaeter — am <c>callvirt</c> — weiss niemand mehr, welche Klasse es war,
    /// weil ein Objekt kein Typ-Tag traegt (M6/P1).</para>
    ///
    /// <para>Die Reihenfolge ist nicht beliebig: bei <c>?SomeInterface</c> muss erst das Interface
    /// entstehen und dann das Optional darum, sonst verpackte man eine Klassenreferenz und
    /// <c>optget</c> lieferte etwas, worauf kein <c>callvirt</c> laufen kann.</para>
    /// </summary>
    private TempId Coerce(TempId value, IrType from, IrType to, Span span)
    {
        var target = to is IrOptionalType outer ? outer.Inner : to;
        var source = from is IrOptionalType inner ? inner.Inner : from;

        // Wert-Semantik (Sprache.md §3.2). Der Bindepunkt ist die Stelle, an der ein struct-Wert
        // eine neue Heimat bekommt — genau dort wird kopiert, und nur dort. Ein frisch gebauter
        // Wert braucht es nicht: er hat noch keinen anderen Besitzer, von dem er sich loesen
        // muesste.
        if (target is IrStructType value_ && from is not IrOptionalType && !_fresh.Contains(value))
            value = CopyStructValue(value, value_, span);

        if (target is IrInterfaceType iface && source is not IrInterfaceType
            && from is not IrOptionalType)
        {
            // Auch der Weg hinter ein Interface ist ein Bindepunkt: ein struct wird dabei
            // kopiert, sonst teilte der Interface-Wert das Slot-Array mit seiner Quelle und eine
            // Mutation ueber das Interface schluege auf das Original durch.
            if (source is IrStructType boxed && !_fresh.Contains(value))
                value = CopyStructValue(value, boxed, span);

            value = MakeInterfaceValue(value, source, iface, span);
            from = iface;
        }

        if (to is not IrOptionalType option || from is IrOptionalType) return value;

        var dest = _slots.NewTemp(to);
        _b.Emit(new OptSome(dest, value, option.Inner, span));
        return dest;
    }

    /// <summary>
    /// Legt eine unabhaengige Kopie eines struct-Wertes an.
    ///
    /// <para>Die Kopie ist zur Laufzeit rekursiv ueber verschachtelte Structs und flach ueber
    /// alles andere: ein Feld vom Typ <c>class</c> oder <c>T[]</c> traegt eine Referenz, und die
    /// wird geteilt. Kopiert wird der Wert, nicht die Welt dahinter.</para>
    /// </summary>
    private TempId CopyStructValue(TempId value, IrStructType type, Span span)
    {
        var dest = _slots.NewTemp(type);
        _b.Emit(new StructCopy(dest, value, type.Type, span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>Hebt eine Objektreferenz auf ihren Interface-Typ.</summary>
    private TempId MakeInterfaceValue(TempId value, IrType concrete, IrInterfaceType iface,
        Span span)
    {
        var concreteId = concrete switch
        {
            IrRefType r => r.Type,
            IrStructType v => v.Type,
            IrEnumType e => e.Type,
            _ => throw NotSupported(
                "a value of this type cannot be used through an interface "
                + "(only classes, structs and enums)",
                span),
        };

        var dest = _slots.NewTemp(iface);
        _b.Emit(new MakeInterface(dest, value, concreteId, iface.Type, span));
        return dest;
    }

    private TempId LowerForceUnwrap(Expr operand, Span span)
    {
        var value = LowerExpr(operand);
        if (TypeOfExpr(operand) is not IrOptionalType option)
            throw NotSupported("'!' on a non-optional", span);

        var dest = _slots.NewTemp(option.Inner);
        _b.Emit(new OptGet(dest, value, option.Inner, span));
        return dest;
    }

    /// <summary>
    /// <c>a ?? b</c> — die rechte Seite wird <b>nur</b> ausgewertet, wenn links kein Wert steht.
    /// Deshalb Verzweigung statt Instruktion, genau wie bei <c>&amp;&amp;</c> und <c>||</c>: eine
    /// Stack-Maschine kann keinen unausgewerteten Ausdruck transportieren.
    /// </summary>
    private TempId LowerCoalesce(BinaryExpr expr)
    {
        var type = TypeOfExpr(expr);
        var slot = _slots.DeclareSynthetic("coalesce", type);

        var option = LowerExpr(expr.Left);
        if (TypeOfExpr(expr.Left) is not IrOptionalType left)
            throw NotSupported("'??' on a non-optional", expr.Span);

        var test = _slots.NewTemp(BoolType);
        _b.Emit(new OptIsSome(test, option, expr.Span));

        var whenSome = _b.NewBlock();
        var whenNone = _b.NewBlock();
        var merge = _b.NewBlock();
        _b.Seal(new CondBranch(test, whenSome, whenNone, expr.Span));

        _b.SwitchTo(whenSome);
        var unwrapped = _slots.NewTemp(left.Inner);
        _b.Emit(new OptGet(unwrapped, option, left.Inner, expr.Span));
        _b.Emit(new StoreLocal(slot, Coerce(unwrapped, left.Inner, type, expr.Span), expr.Span));
        _b.Seal(new Branch(merge, expr.Span));

        _b.SwitchTo(whenNone);
        var fallback = LowerExpr(expr.Right);
        _b.Emit(new StoreLocal(slot, Coerce(fallback, TypeOfExpr(expr.Right), type, expr.Span), expr.Span));
        _b.Seal(new Branch(merge, expr.Span));

        _b.SwitchTo(merge);
        var dest = _slots.NewTemp(type);
        _b.Emit(new LoadLocal(dest, slot, type, expr.Span));
        return dest;
    }

    // ------------------------------------------------------------------ Arrays (ADR-016)

    /// <summary><c>[a, b, c]</c> — eine Instruktion, nicht drei Stores. Die Werte liegen beim
    /// <c>newarr</c> in Quelltext-Reihenfolge auf dem Stack.</summary>
    private TempId LowerArrayLiteral(ArrayLitExpr expr)
    {
        if (TypeOfExpr(expr) is not IrArrayType type)
            throw NotSupported("array literal of a non-array type", expr.Span);

        var elements = new TempId[expr.Elements.Length];
        for (var i = 0; i < expr.Elements.Length; i++) elements[i] = LowerExpr(expr.Elements[i]);

        var dest = _slots.NewTemp(type);
        _b.Emit(new NewArray(dest, type.Element, elements, expr.Span));
        return dest;
    }

    private TempId LowerIndexRead(IndexExpr expr)
    {
        var (array, index, element) = ResolveIndexAccess(expr);
        var dest = _slots.NewTemp(element);
        _b.Emit(new LoadElem(dest, array, index, element, expr.Span));
        return dest;
    }

    /// <summary>Gemeinsamer Teil von Lesen und Schreiben. <c>[i]</c> ist heute nur auf <c>T[]</c>
    /// gültig; für alles andere sieht ADR-016 das <c>Indexable&lt;T&gt;</c>-Interface vor, das
    /// Interfaces (P3) und Generics (P8) voraussetzt.</summary>
    private (TempId Array, TempId Index, IrType Element) ResolveIndexAccess(IndexExpr expr)
    {
        if (TypeOfExpr(expr.Target) is not IrArrayType array)
            throw NotSupported($"indexing a '{TypeFacts.Display(_types.TypeOf(expr.Target))}' " +
                               "(only arrays; other containers need the Indexable interface)",
                expr.Span);

        var target = LowerExpr(expr.Target);
        var index = LowerExpr(expr.Index);
        return (target, index, array.Element);
    }

    private TempId LowerArrayLength(MemberExpr expr)
    {
        var array = LowerExpr(expr.Target);
        var dest = _slots.NewTemp(new IrScalarType(IrScalar.I64));
        _b.Emit(new ArrayLen(dest, array, expr.Span));
        return dest;
    }

    // ------------------------------------------------------------------ Objekte (Sprache.md §3.3)

    /// <summary>
    /// <c>Account { owner = a, balance = b }</c> → ein <c>newobj</c> und ein <c>storefield</c> je
    /// Feld.
    ///
    /// <para><b>Geschrieben wird in Deklarations-, nicht in Schreibreihenfolge.</b> Die
    /// Initialisierer dürfen im Quelltext beliebig stehen; das Layout ist aber die Deklaration, und
    /// nur eine feste Ordnung macht den Bytecode deterministisch (ADR-013). Die Werte werden
    /// trotzdem in <b>Quelltext</b>-Reihenfolge ausgewertet — bei Seiteneffekten ist das die
    /// Reihenfolge, die der Leser erwartet.</para>
    /// </summary>
    private TempId LowerObjectInit(StructInitExpr expr)
    {
        // 'Shape.Tri { a = 3, b = 4 }' — eine Struct-Variante. Sieht aus wie ein Objekt-Literal,
        // ist aber eine Varianten-Konstruktion und geht deshalb über newvariant.
        if (_types.TypeOf(expr) is NamedRef { Symbol.Kind: TypeSymbolKind.Enum } owner)
            return LowerStructVariant(expr, owner.Symbol);

        if (_types.TypeOf(expr) is not NamedRef
            { Symbol.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct } named)
            throw NotSupported($"initializer for '{TypeFacts.Display(_types.TypeOf(expr))}' " +
                               "(only classes and structs are lowered)", expr.Span);

        var type = _typeTable.Intern(named.Symbol);
        var layout = _typeTable.Defs[type.Value];

        // Erst alle Werte auswerten (Quelltext-Reihenfolge), dann in Layout-Reihenfolge schreiben.
        var values = new Dictionary<string, TempId>(StringComparer.Ordinal);
        foreach (var field in expr.Fields)
        {
            if (values.ContainsKey(field.Name))
                throw Bug($"duplicate initializer for '{field.Name}' reached lowering");
            // An den deklarierten Feldtyp anpassen: ein Feld vom Typ eines Interfaces nimmt eine
            // Klasse nur als Fat Pointer auf.
            var fieldIndex = Array.IndexOf(layout.FieldNames, field.Name);
            values[field.Name] = fieldIndex >= 0
                ? LowerExprAs(field.Value, layout.FieldTypes[fieldIndex])
                : LowerExpr(field.Value);
        }

        // Ein fehlendes Feld hätte seinen Nullwert — aber die Sema kennt heute keine Regel, die das
        // erlaubt oder verbietet, und stillschweigend 0 einzusetzen wäre geraten. Lieber melden.
        foreach (var name in layout.FieldNames)
            if (!values.ContainsKey(name))
                throw NotSupported($"initializer omits field '{name}' (field defaults are not " +
                                   "supported by this compiler version yet)", expr.Span);

        // Ein struct-Wert ist zur Laufzeit dasselbe Slot-Array wie ein Klassenobjekt — 'newobj'
        // taugt fuer beides. Der Unterschied steckt allein in den Bindepunkten.
        IrType result = _typeTable.IsStruct(type)
            ? new IrStructType(type)
            : new IrRefType(type);
        var dest = _slots.NewTemp(result);
        _b.Emit(new NewObject(dest, type, result, expr.Span));

        for (var i = 0; i < layout.FieldNames.Length; i++)
            _b.Emit(new StoreField(dest, type, new FieldId(i), values[layout.FieldNames[i]], expr.Span));

        // Frisch gebaut: dieser Wert gehoert noch niemandem, eine Kopie beim Binden waere Ballast.
        _fresh.Add(dest);
        return dest;
    }

    private TempId LowerFieldRead(MemberExpr expr)
    {
        // 'Shape.Empty' — eine Unit-Variante. Sie sieht aus wie ein Member-Zugriff, ist aber eine
        // Konstruktion ohne Argumente.
        if (_types.RefOf(expr) is EnumVariantSymbol)
            return LowerVariantCall(expr, [], expr.Span);

        // '.length' auf einem Array ist eingebaut (ADR-016), kein Feld und keine Methode.
        if (expr.Member == "length" && TypeOfExpr(expr.Target) is IrArrayType)
            return LowerArrayLength(expr);

        var (obj, type, field, fieldType) = ResolveFieldAccess(expr);
        var dest = _slots.NewTemp(fieldType);
        _b.Emit(new LoadField(dest, obj, type, field, fieldType, expr.Span));
        return dest;
    }

    /// <summary>Gemeinsamer Teil von Lesen und Schreiben: das Objekt auswerten und Typ, Feldindex
    /// und Feldtyp bestimmen.</summary>
    private (TempId Object, TypeId Type, FieldId Field, IrType FieldType) ResolveFieldAccess(MemberExpr expr)
    {
        // 'P.ZERO' ist keine Feld-, sondern eine Konstanten-Lesung. Sie hängt an derselben Lücke
        // wie ein Modul-'let': Konstanten werden noch nirgends gelowert. Das hier zu benennen ist
        // ehrlicher als eine Meldung über einen Member-Zugriff auf '<?>'.
        if (_types.RefOf(expr) is GlobalSymbol)
            throw NotSupported($"reading the constant '{expr.Member}' " +
                               "(constants are not lowered yet, module-level 'let' neither)", expr.Span);

        if (_types.TypeOf(expr.Target) is not NamedRef
            { Symbol.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct } named)
            throw NotSupported($"member access '.{expr.Member}' on " +
                               $"'{TypeFacts.Display(_types.TypeOf(expr.Target))}'", expr.Span);

        var obj = LowerExpr(expr.Target);
        var type = _typeTable.Intern(named.Symbol);
        var field = _typeTable.FieldOf(named.Symbol, expr.Member, expr.Span);
        return (obj, type, field, _typeTable.Defs[type.Value].FieldTypes[field.Value]);
    }

    private TempId LowerCast(CastExpr expr)
    {
        var from = TypeOfExpr(expr.Operand);
        var to = TypeOfExpr(expr);
        var operand = LowerExpr(expr.Operand);

        // 'x as int' bei x: int ist legales Lyric, ergibt aber keinen sinnvollen Opcode. Das
        // Lowering elidiert die Identität — der Verifier lehnt sie ab.
        if (IrType.Equal(from, to)) return operand;

        var dest = _slots.NewTemp(to);
        _b.Emit(new Lyric.Ir.Convert(dest, from, to, operand, expr.Span));
        return dest;
    }

    private TempId? LowerCall(CallExpr expr)
    {
        // Der Empfänger ist Parameter 0 (ADR-014). Bei `p.get()` wird 'p' also zum ersten Argument;
        // bei `P.new(…)` gibt es keinen, und der Aufruf ist ein gewöhnlicher Call. Beide Formen
        // laufen danach durch denselben Pfad — der Unterschied steckt allein in der Argumentliste.
        TempId? receiver = null;
        string calleeName;
        Symbol? bound;

        switch (expr.Callee)
        {
            // Empfaenger ist ein Interface-Wert: welche Implementierung laeuft, steht erst zur
            // Laufzeit fest. Das ist der einzige dynamische Dispatch der Sprache.
            case MemberExpr member
                when _types.TypeOf(member.Target) is NamedRef
                     { Symbol.Kind: TypeSymbolKind.Interface } iface:
                return LowerVirtualCall(member, iface.Symbol, expr);

            case MemberExpr member
                when _types.TypeOf(member.Target) is NamedRef
                     { Symbol.Kind: TypeSymbolKind.Class or TypeSymbolKind.Struct
                         or TypeSymbolKind.Enum }:
                calleeName = member.Member;
                bound = _types.RefOf(member);
                receiver = LowerExpr(member.Target);
                break;

            // Shape.Circle(2.0) — eine Tuple-Variante. Kein Call, sondern eine Konstruktion.
            case MemberExpr member when _types.RefOf(member) is EnumVariantSymbol:
                return LowerVariantCall(member, expr.Arguments, expr.Span);

            case MemberExpr member: // Typ- oder Modul-Ziel: P.new(…), console.println(…)
                calleeName = member.Member;
                bound = _types.RefOf(member);
                break;

            case IdentifierExpr callee:
                calleeName = callee.Name;
                bound = _types.RefOf(callee);
                break;

            default:
                throw NotSupported("call target (only functions and methods)", expr.Callee.Span);
        }

        // Ein selektiver Import bindet über ein ImportBindingSymbol; das eigentliche Ziel liegt
        // darunter. Ohne das Auspacken sieht `import std.io.console { println };` anders aus als
        // ein Aufruf im selben Modul, obwohl es dieselbe Funktion ist.
        if (bound is ImportBindingSymbol binding) bound = binding.Target;

        if (bound is not FunctionSymbol symbol)
            throw NotSupported($"call to '{calleeName}' (not a function or method)", expr.Span);

        // Nativ hinterlegt (Stdlib): eigener Instruktionstyp, eigener Indexraum.
        if (_imports.IsNative(symbol))
            return LowerImportCall(_imports.Intern(symbol), expr.Arguments, expr.Span);

        if (!_functions.TryGetValue(symbol, out var target))
            throw NotSupported($"call to '{calleeName}' (external, generic or bodiless)", expr.Span);

        // Default-Argumente und 'params' würden hier Argumente materialisieren müssen; solange das
        // fehlt, ist eine Arity-Abweichung kein IR-Bug, sondern eine Lücke im Lowering.
        if (symbol.Declaration is FunctionDecl declaration
            && declaration.Parameters.Length != expr.Arguments.Length)
            throw NotSupported($"call to '{calleeName}' with default or variadic arguments", expr.Span);

        // Der Empfänger steht vorn — die Reihenfolge ist die Parameter-Konvention der IR und
        // muss zu der passen, in der FunctionLowerer die Slots angelegt hat.
        var offset = receiver is null ? 0 : 1;
        var args = new TempId[expr.Arguments.Length + offset];
        if (receiver is { } self) args[0] = self;
        for (var i = 0; i < expr.Arguments.Length; i++)
            args[i + offset] = LowerArgument(expr.Arguments[i], symbol, i);

        var returnType = TypeOfExpr(expr);
        if (IsVoid(returnType))
        {
            _b.Emit(new Call(null, target, args, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(returnType);
        _b.Emit(new Call(dest, target, args, expr.Span));
        _fresh.Add(dest);
        return dest;
    }

    /// <summary>
    /// Ein Argument, angepasst an den <b>deklarierten</b> Parametertyp.
    ///
    /// <para>Ohne diesen Schritt bliebe eine Klasse eine Klasse, auch wenn der Parameter ein
    /// Interface ist — und der Aufgerufene bekaeme eine nackte Referenz statt eines Fat Pointers.
    /// Der Verifier faengt das, aber als Typ-Mismatch tief im Callee statt als das, was es ist:
    /// eine fehlende Coercion am Aufrufort.</para>
    /// </summary>
    private TempId LowerArgument(Expr argument, FunctionSymbol callee, int index)
    {
        if (callee.Declaration is not FunctionDecl decl || index >= decl.Parameters.Length)
            return LowerExpr(argument);

        // Nur das Lowern des Parametertyps wird abgeschirmt — ein Typ, den dieser Compiler-Stand
        // nicht kennt, meldet ohnehin gleich die Funktion selbst, und hier doppelt zu klagen waere
        // Laerm. Die Coercion steht BEWUSST ausserhalb: als sie mit im try lag, verschluckte der
        // catch ein fehlendes 'mkiface' und machte aus einer Diagnose malformed IR — der Fehler
        // tauchte dann als Verifier-Befund tief im Aufrufer auf.
        IrType expected;
        try
        {
            expected = _typeTable.Lower(decl.Parameters[index].Type);
        }
        catch (UnsupportedConstructException)
        {
            return LowerExpr(argument);
        }

        return LowerExprAs(argument, expected);
    }

    /// <summary>
    /// Der dynamische Aufruf. Der Empfaenger ist ein Interface-Wert und traegt seinen konkreten
    /// Typ mit sich; die Runtime schlaegt damit in der vtable nach.
    ///
    /// <para>Der Slot statt des Namens ist dieselbe Entscheidung wie beim Feldindex (P1): Lyric ist
    /// statisch typisiert und kennt kein Monkey-Patching, also steht die Position zur Compile-Zeit
    /// fest. Ein Namens-Lookup mit Inline-Cache loeste ein Problem, das diese Sprache nicht hat.</para>
    /// </summary>
    private TempId? LowerVirtualCall(MemberExpr member, TypeSymbol iface, CallExpr expr)
    {
        var interfaceType = _typeTable.InterfaceOf(iface);
        var slot = _typeTable.SlotOf(iface, member.Member, member.Span);

        var args = new TempId[expr.Arguments.Length + 1];
        args[0] = LowerExpr(member.Target);

        // Die Signatur steht am Interface, nicht an einer Implementierung — sie ist der Vertrag.
        var declaration = iface.Members.LookupLocal(member.Member) is FunctionSymbol method
            ? method.Declaration as FunctionDecl
            : null;

        for (var i = 0; i < expr.Arguments.Length; i++)
            args[i + 1] = declaration is not null && i < declaration.Parameters.Length
                ? LowerExprAs(expr.Arguments[i], _typeTable.Lower(declaration.Parameters[i].Type))
                : LowerExpr(expr.Arguments[i]);

        var returnType = TypeOfExpr(expr);
        if (IsVoid(returnType))
        {
            _b.Emit(new CallVirt(null, interfaceType.Type, slot, args, returnType, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(returnType);
        _b.Emit(new CallVirt(dest, interfaceType.Type, slot, args, returnType, expr.Span));
        return dest;
    }

    /// <summary>Aufruf einer nativ hinterlegten Funktion. Die Signatur kommt aus der Import-Tabelle,
    /// nicht aus einer Funktion — ein Import hat keinen Rumpf.</summary>
    private TempId? LowerImportCall(ImportId target, Expr[] arguments, Span span)
    {
        var import = _imports.Used[target.Value];
        if (arguments.Length != import.ParamTypes.Length)
            throw NotSupported($"call to '{import.Name}' with default or variadic arguments", span);

        var args = new TempId[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) args[i] = LowerExpr(arguments[i]);

        if (IsVoid(import.ReturnType))
        {
            _b.Emit(new CallImport(null, target, args, span));
            return null;
        }

        var dest = _slots.NewTemp(import.ReturnType);
        _b.Emit(new CallImport(dest, target, args, span));
        return dest;
    }

    /// <summary>Direkter Aufruf eines Runtime-Helfers über seinen festen Namen — das f-String-
    /// Lowering referenziert <c>std.string.concat</c>, ohne dass jemand <c>std.string</c>
    /// importiert hätte. Dasselbe Modell wie Roslyns Verweis auf <c>String.Concat</c>.</summary>
    private TempId CallHelper(string name, Span span, params TempId[] args)
    {
        if (!_imports.TryFind(name, out var import))
            throw NotSupported($"the f-string helper '{name}' (is the standard library on the module path?)", span);

        var target = _imports.Intern(import);
        var dest = _slots.NewTemp(import.ReturnType);
        _b.Emit(new CallImport(dest, target, args, span));
        return dest;
    }

    /// <summary>
    /// f-String → Kette aus <c>concat</c> und den <c>fromXxx</c>-Wandlern. Keine Arrays, keine
    /// Varargs — beides kann die IR nicht, und so braucht sie es auch nicht. Roslyn macht es für
    /// <c>$"…"</c> ohne Format-Spec genauso.
    /// </summary>
    private TempId LowerInterpolatedString(InterpolatedStringExpr expr)
    {
        var stringType = new IrScalarType(IrScalar.String);
        var parts = new List<TempId>();
        var pendingText = new System.Text.StringBuilder();

        void FlushText(Span span)
        {
            if (pendingText.Length == 0) return;
            parts.Add(EmitConst(new StringConst(pendingText.ToString()), stringType, span));
            pendingText.Clear();
        }

        foreach (var segment in expr.Segments)
        {
            switch (segment)
            {
                case InterpText text:
                    // Der Parser speichert die Textstücke roh (siehe InterpText) — hier werden die
                    // Escapes aufgelöst. Benachbarte Stücke sammeln sich zu einer Konstante.
                    pendingText.Append(Escapes.Resolve(text.Text));
                    break;

                case InterpHole hole:
                    if (hole.FormatSpec is not null)
                        throw NotSupported($"format spec '{hole.FormatSpec}' (std.fmt arrives with M8)",
                            hole.Span);

                    FlushText(hole.Span);
                    parts.Add(ToStringValue(hole.Expr));
                    break;

                default:
                    throw Bug($"unhandled interpolation segment {segment.GetType().Name}");
            }
        }
        FlushText(expr.Span);

        if (parts.Count == 0) return EmitConst(new StringConst(string.Empty), stringType, expr.Span);

        var result = parts[0];
        for (var i = 1; i < parts.Count; i++)
            result = CallHelper("std.string.concat", expr.Span, result, parts[i]);
        return result;
    }

    /// <summary>Ein Loch im f-String als string. Strings bleiben, wie sie sind; alles andere geht
    /// durch den passenden Wandler — die Namen unterscheiden nach Quelltyp, weil Lyric kein
    /// Overloading hat.</summary>
    private TempId ToStringValue(Expr expr)
    {
        var value = LowerExpr(expr);
        var type = TypeOfExpr(expr);
        if (type is not IrScalarType scalar)
            throw NotSupported($"interpolating a non-scalar value", expr.Span);

        return scalar.Kind switch
        {
            IrScalar.String => value,
            IrScalar.Bool => CallHelper("std.string.fromBool", expr.Span, value),
            IrScalar.Char => CallHelper("std.string.fromChar", expr.Span, value),
            IrScalar.F32 or IrScalar.F64 => CallHelper("std.string.fromFloat", expr.Span, value),
            _ when IsIntegerScalar(scalar.Kind) => CallHelper("std.string.fromInt", expr.Span, value),
            _ => throw NotSupported($"interpolating a non-scalar value", expr.Span),
        };
    }

    // ------------------------------------------------------------------ Helfer

    private TempId EmitConst(IrConstValue value, IrType type, Span span)
    {
        var dest = _slots.NewTemp(type);
        _b.Emit(new Const(dest, type, value, span));
        return dest;
    }

    private LocalId ResolveLocalTarget(Expr target, string what)
    {
        if (target is not IdentifierExpr identifier)
            throw NotSupported($"{what} target (only parameters and locals)", target.Span);

        var symbol = _types.RefOf(identifier) ?? throw Bug($"identifier '{identifier.Name}' is unbound");
        if (!_slots.TryLookup(symbol, out var slot))
            throw NotSupported($"{what} of '{identifier.Name}'", target.Span);

        return slot;
    }

    private static IrConstValue OneFor(IrType type, Span span) => type switch
    {
        IrScalarType { Kind: IrScalar.F32 or IrScalar.F64 } => new FloatConst(1.0),
        IrScalarType s when IsIntegerScalar(s.Kind) => new IntConst(1),
        _ => throw NotSupported("increment/decrement on a non-numeric type", span)
    };

    private static bool IsIntegerScalar(IrScalar kind) => kind is
        IrScalar.I8 or IrScalar.I16 or IrScalar.I32 or IrScalar.I64 or
        IrScalar.U8 or IrScalar.U16 or IrScalar.U32 or IrScalar.U64;

    private static bool IsVoid(IrType type) => type is IrScalarType { Kind: IrScalar.Void };

    private IrType TypeOfExpr(Expr expr) => LowerType(_types.TypeOf(expr), expr.Span);

    /// <summary>
    /// Sema-Typ → IR-Typ. Hier sitzt der Substitutions-Haken der Monomorphisierung: ein
    /// Typ-Parameter wird über die Instanz-Substitution aufgelöst. In P4 ist die Map immer leer,
    /// der Zweig also inaktiv — aber er sitzt an der einzigen Stelle, die ihn braucht, statt
    /// später über den gesamten Ausdrucks-Pfad nachgerüstet werden zu müssen.
    /// </summary>
    private IrType LowerType(LyrType type, Span span)
    {
        if (type is TypeParamType parameter)
        {
            if (!_substitution.TryGetValue(parameter.Param, out var bound))
                throw NotSupported($"type parameter '{parameter.Param.Name}'", span);
            type = bound;
        }

        // Eine Klasse wird zur Referenz auf ihren Tabellen-Eintrag; das Layout entsteht dabei
        // einmalig in der TypeTable. Alles andere (Struct, Enum, Array, Tupel, …) ist weiterhin
        // außerhalb dieses Compiler-Stands.
        if (type is NamedRef { Symbol.Kind: TypeSymbolKind.Class } named)
            return _typeTable.RefTo(named.Symbol);

        if (type is NamedRef { Symbol.Kind: TypeSymbolKind.Struct } valueType)
            return _typeTable.StructOf(valueType.Symbol);

        if (type is NamedRef { Symbol.Kind: TypeSymbolKind.Enum } enumType)
            return _typeTable.EnumOf(enumType.Symbol);

        // Ein Interface als Werttyp: Lyrics 'dyn'. Zur Laufzeit ein Fat Pointer aus Objekt und
        // konkretem Typindex — siehe IrInterfaceType.
        if (type is NamedRef { Symbol.Kind: TypeSymbolKind.Interface } interfaceType)
            return _typeTable.InterfaceOf(interfaceType.Symbol);

        // T[] mit fester Länge (ADR-016). Der Elementtyp steht inline; T[N] gibt es nicht mehr.
        if (type is ArrayOf array)
            return new IrArrayType(LowerType(array.Element, span));

        // ?T (Sprache.md §7). Nicht schachtelbar — die Sema kollabiert ??T bereits, hier ist es
        // trotzdem eine Grenze statt einer stillen Annahme.
        if (type is Optional optional)
        {
            var inner = LowerType(optional.Inner, span);
            if (inner is IrOptionalType)
                throw NotSupported("a nested optional '??T' (optionals do not nest)", span);
            return new IrOptionalType(inner);
        }

        if (type is not PrimitiveType)
            throw NotSupported($"type '{TypeFacts.Display(type)}'", span);

        return TypeLowering.Lower(type);
    }

    /// <summary>Der Rückgabetyp kommt aus dem syntaktischen <see cref="TypeNode"/>, weil die Sema
    /// ihn nicht in <see cref="TypeResult"/> ablegt. Die Auflösung liegt in der
    /// <see cref="TypeTable"/> — dieselbe Stelle, die auch Feld- und Parametertypen auflöst, damit
    /// eine Fabrik <c>static fn new(): P</c> denselben Typ liefert wie ein Feld vom Typ
    /// <c>P</c>.</summary>
    private IrType LowerDeclaredReturnType() =>
        _decl.ReturnType is null ? VoidType : _typeTable.Lower(_decl.ReturnType);

    /// <summary>Scope-Grenze: gültiges Lyric, für das der Backend-Teil noch fehlt. Wird von
    /// <see cref="ModuleLowerer"/> zu einer <c>LYR-IR0001</c>-Diagnose mit Datei/Zeile/Spalte —
    /// deshalb hier keine Position in den Text schreiben, die rendert die DiagnosticEngine.</summary>
    private static UnsupportedConstructException NotSupported(string what, Span span) =>
        new($"{what} is not supported by this compiler version yet", span);

    /// <summary>Interne Inkonsistenz — der Compiler ist kaputt, nicht der Quelltext.</summary>
    private InternalCompilationException Bug(string message) =>
        new($"lowering: {message} (in '{_name}')");
}
