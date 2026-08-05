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
            _thisSlot = _slots.Declare("this", _typeTable.RefTo(receiver));
            _thisType = _typeTable.RefTo(receiver);
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
            case MatchStmt s: throw NotSupported("'match'", s.Span);
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
        MatchExpr e => throw NotSupported("'match' as an expression", e.Span),
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
            var value = LowerExpr(expr.Value);
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
            var assigned = LowerExpr(expr.Value);
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

    /// <summary>Verpackt einen Wert, wenn die Position ein Optional erwartet und der Wert keins ist
    /// — <c>T</c> → <c>?T</c> ist implizit (Sprache.md §6.5).</summary>
    private TempId Coerce(TempId value, IrType from, IrType to, Span span)
    {
        if (to is not IrOptionalType option || from is IrOptionalType) return value;

        var dest = _slots.NewTemp(to);
        _b.Emit(new OptSome(dest, value, option.Inner, span));
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
        if (_types.TypeOf(expr) is not NamedRef { Symbol.Kind: TypeSymbolKind.Class } named)
            throw NotSupported($"initializer for '{TypeFacts.Display(_types.TypeOf(expr))}' " +
                               "(only classes are lowered)", expr.Span);

        var type = _typeTable.Intern(named.Symbol);
        var layout = _typeTable.Defs[type.Value];

        // Erst alle Werte auswerten (Quelltext-Reihenfolge), dann in Layout-Reihenfolge schreiben.
        var values = new Dictionary<string, TempId>(StringComparer.Ordinal);
        foreach (var field in expr.Fields)
        {
            if (values.ContainsKey(field.Name))
                throw Bug($"duplicate initializer for '{field.Name}' reached lowering");
            values[field.Name] = LowerExpr(field.Value);
        }

        // Ein fehlendes Feld hätte seinen Nullwert — aber die Sema kennt heute keine Regel, die das
        // erlaubt oder verbietet, und stillschweigend 0 einzusetzen wäre geraten. Lieber melden.
        foreach (var name in layout.FieldNames)
            if (!values.ContainsKey(name))
                throw NotSupported($"initializer omits field '{name}' (field defaults are not " +
                                   "supported by this compiler version yet)", expr.Span);

        var dest = _slots.NewTemp(new IrRefType(type));
        _b.Emit(new NewObject(dest, type, expr.Span));

        for (var i = 0; i < layout.FieldNames.Length; i++)
            _b.Emit(new StoreField(dest, type, new FieldId(i), values[layout.FieldNames[i]], expr.Span));

        return dest;
    }

    private TempId LowerFieldRead(MemberExpr expr)
    {
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

        if (_types.TypeOf(expr.Target) is not NamedRef { Symbol.Kind: TypeSymbolKind.Class } named)
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
            case MemberExpr member
                when _types.TypeOf(member.Target) is NamedRef { Symbol.Kind: TypeSymbolKind.Class }:
                calleeName = member.Member;
                bound = _types.RefOf(member);
                receiver = LowerExpr(member.Target);
                break;

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
            args[i + offset] = LowerExpr(expr.Arguments[i]);

        var returnType = TypeOfExpr(expr);
        if (IsVoid(returnType))
        {
            _b.Emit(new Call(null, target, args, expr.Span));
            return null;
        }

        var dest = _slots.NewTemp(returnType);
        _b.Emit(new Call(dest, target, args, expr.Span));
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
