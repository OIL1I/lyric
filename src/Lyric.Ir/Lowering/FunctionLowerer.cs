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
/// Generics, Stdlib-/Extern-Calls, f-Strings. Alles davon wirft mit Quellposition statt still
/// falschen Code zu erzeugen.</para>
/// </summary>
internal sealed class FunctionLowerer
{
    private static readonly IrType VoidType = new IrScalarType(IrScalar.Void);
    private static readonly IrType BoolType = new IrScalarType(IrScalar.Bool);

    private readonly FunctionDecl _decl;
    private readonly string _name;
    private readonly TypeResult _types;
    private readonly IReadOnlyDictionary<FunctionSymbol, FunctionId> _functions;

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
        IReadOnlyDictionary<GenericParamSymbol, LyrType> substitution)
    {
        _decl = decl;
        _name = name;
        _types = types;
        _functions = functions;
        _substitution = substitution;
        _b = new BlockBuilder(_blocks);
        _returnType = LowerDeclaredReturnType();

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

        return new IrFunction(_name, _returnType, _decl.Parameters.Length,
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
            _b.Emit(new StoreLocal(slot, LowerExpr(binding.Initializer), binding.Span));

        return true;
    }

    private bool LowerReturn(ReturnStmt stmt)
    {
        if (stmt.Value is null)
        {
            _b.Seal(new Return(null, stmt.Span));
            return false;
        }

        _b.Seal(new Return(LowerExpr(stmt.Value), stmt.Span));
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

        NullLiteralExpr e => throw NotSupported("'null' (needs a nullable IR type)", e.Span),
        InterpolatedStringExpr e => throw NotSupported("f-strings (lower to format calls)", e.Span),
        LambdaExpr e => throw NotSupported("lambdas (need closure lifting)", e.Span),
        MatchExpr e => throw NotSupported("'match' as an expression", e.Span),
        MemberExpr e => throw NotSupported($"member access '.{e.Member}'", e.Span),
        IndexExpr e => throw NotSupported("indexing", e.Span),
        ArrayLitExpr e => throw NotSupported("array literals", e.Span),
        TupleLitExpr e => throw NotSupported("tuple literals", e.Span),
        StructInitExpr e => throw NotSupported("struct initializers", e.Span),
        RangeExpr e => throw NotSupported("ranges", e.Span),
        ResumeExpr e => throw NotSupported("'resume'", e.Span),
        ThisExpr e => throw NotSupported("'this' (methods are not lowered yet)", e.Span),
        AtIdentifierExpr e => throw NotSupported($"attribute '{e.Name}'", e.Span),
        ErrorExpr e => throw Bug($"error expression reached lowering at {e.Span}"),

        _ => throw Bug($"unhandled expression {expr.GetType().Name}")
    };

    private TempId LowerIntLiteral(IntLiteralExpr expr)
    {
        var type = TypeOfExpr(expr);
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
        PostfixOp.ForceUnwrap => throw NotSupported("force-unwrap '!'", expr.Span),
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
            throw NotSupported("'??' (needs a nullable IR type)", expr.Span);

        var kind = IrBinKindExtensions.FromAst(expr.Operator);
        var lhs = LowerExpr(expr.Left);
        var rhs = LowerExpr(expr.Right);
        var type = TypeOfExpr(expr);

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
        if (expr.Callee is not IdentifierExpr callee)
            throw NotSupported("call target (only module-level functions)", expr.Callee.Span);

        if (_types.RefOf(callee) is not FunctionSymbol symbol)
            throw NotSupported($"call to '{callee.Name}' (not a module-level function)", expr.Span);

        if (!_functions.TryGetValue(symbol, out var target))
            throw NotSupported($"call to '{callee.Name}' (external, generic or bodiless)", expr.Span);

        // Default-Argumente und 'params' würden hier Argumente materialisieren müssen; solange das
        // fehlt, ist eine Arity-Abweichung kein IR-Bug, sondern eine Lücke im Lowering.
        if (symbol.Declaration is FunctionDecl declaration
            && declaration.Parameters.Length != expr.Arguments.Length)
            throw NotSupported($"call to '{callee.Name}' with default or variadic arguments", expr.Span);

        var args = new TempId[expr.Arguments.Length];
        for (var i = 0; i < expr.Arguments.Length; i++)
            args[i] = LowerExpr(expr.Arguments[i]);

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
        _ => throw new InternalCompilationException(
            $"lowering: increment/decrement on non-numeric type at {span}")
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

        if (type is not PrimitiveType)
            throw NotSupported($"type '{TypeFacts.Display(type)}'", span);

        return TypeLowering.Lower(type);
    }

    /// <summary>Der Rückgabetyp kommt aus dem syntaktischen <see cref="TypeNode"/>, weil die Sema
    /// ihn nicht in <see cref="TypeResult"/> ablegt. Builtins sind laut <c>Types.cs</c>
    /// <see cref="NamedType"/> mit einelementigem Pfad — mehr braucht P4 nicht.</summary>
    private IrType LowerDeclaredReturnType()
    {
        if (_decl.ReturnType is null) return VoidType;

        if (_decl.ReturnType is NamedType { Path.Length: 1, TypeArguments.Length: 0 } named
            && TypeFacts.FromBuiltinName(named.Path[0]) is { } primitive)
            return TypeLowering.Lower(primitive);

        throw NotSupported("non-primitive return type", _decl.ReturnType.Span);
    }

    private InternalCompilationException NotSupported(string what, Span span) =>
        new($"lowering: {what} is not supported yet — at {span} in '{_name}'. " +
            "P4 covers scalars, locals, module-local calls and structured control flow.");

    private InternalCompilationException Bug(string message) =>
        new($"lowering: {message} (in '{_name}')");
}
