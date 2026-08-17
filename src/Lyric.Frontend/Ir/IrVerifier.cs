using System.Globalization;
using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// Checks an <see cref="IrModule"/> for well-formedness. The verifier is an assertion suite over the
/// IR data structure, NOT a type checker: it answers "is this IR well formed", not "is the user's
/// program correct". Language rules are enforced by the sema; repeating them here would be a parallel
/// mechanism. In particular the verifier does NOT check that locals are assigned before their first
/// read — the definite-assignment analysis proved that.
///
/// Every finding is therefore a COMPILER BUG in the lowering or the monomorphization, not a user
/// diagnostic, which is why the messages are plain text rather than <c>LYR-IR####</c> codes. The
/// <c>LYR-IR####</c> range stays reserved for real, user-visible lowering errors.
///
/// <para>COLLECTING RATHER THAN STOPPING AT THE FIRST FINDING: a lowering bug typically shows in
/// several symptoms (a wrong temp type causes a dest mismatch causes a return mismatch). Seeing all
/// of them at once points at the responsible place; the first alone does not.</para>
///
/// <para>PHASES AND BAIL-OUT: the checks build on one another — with a gap in the temp table every
/// type lookup misses, with duplicate block ids target resolution is guesswork, and with two
/// definitions per temp the availability analysis is meaningless. Hence four phases that abandon the
/// function on a fundamental error, while the next one is checked normally. The same principle as
/// <c>ErrorType</c> as poison in the sema: no follow-up errors.</para>
///
/// <para>The verifier runs after the lowering and before bytecode emission; always in tests and debug
/// builds, in release behind a flag, as LLVM's verifier does in assert builds.</para>
/// </summary>
public static class IrVerifier
{
    /// <summary>Checks the module and returns all findings; an empty list means well formed. The order
    /// is deterministic: declaration order in phases 0 and 1, reverse postorder in phases 2 and
    /// 3.</summary>
    public static IReadOnlyList<string> Verify(IrModule module)
    {
        var findings = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        VerifyTypes(module, findings);
        VerifyImpls(module, findings);

        // The init function is called by the runtime before the entry point; an index into nothing
        // would only show at load time.
        if (module.GlobalInit is { } init
            && (init.Value < 0 || init.Value >= module.Functions.Count))
            findings.Add($"global initializer {init} is out of range " +
                         $"(module has {module.Functions.Count} function(s))");

        // Globals without an initializer would be uninitialized slots, and every value in Lyric has
        // one. Either both exist or neither does.
        if (module.Globals.Count > 0 && module.GlobalInit is null)
            findings.Add($"module declares {module.Globals.Count} global(s) but no initializer");

        foreach (var function in module.Functions)
        {
            // Names are the symbol names in the bytecode. A collision is the canary for the
            // monomorphization: two instances falling onto the same mangled name would be a silent
            // wrong call.
            if (!seenNames.Add(function.Name))
                findings.Add($"{function.Name}: duplicate function name");

            new FunctionVerifier(module, function, findings).Run();
        }

        // The entry point becomes the Start section in the bytecode. An index into nothing would only
        // show at load time, so it is checked here, where it arises.
        if (module.EntryFunction is { } entry)
        {
            if (entry.Value < 0 || entry.Value >= module.Functions.Count)
                findings.Add($"entry function {entry} is out of range " +
                             $"(module has {module.Functions.Count} function(s))");
            else if (module.Functions[entry.Value] is { ParamCount: > 1 } tooMany)
            {
                findings.Add($"entry function {tooMany.Name} takes {tooMany.ParamCount} " +
                             "parameters; an entry point takes none or one 'string[]'");
            }
            else if (module.Functions[entry.Value] is { ParamCount: 1 } withArgs
                     && withArgs.Locals[0].Type is not IrArrayType
                     { Element: IrScalarType { Kind: IrScalar.String } })
            {
                // The runtime builds exactly ONE kind of argument. Finding something else here, it
                // would write a string[] into a slot expecting something different, which only shows
                // at runtime as a wrongly read value.
                findings.Add($"entry function {withArgs.Name} takes " +
                             "a parameter that is not 'string[]', which is the only one allowed");
            }
        }

        return findings;
    }

    /// <summary>
    /// Checks the type table before any function uses it. Pulled forward for the same reason the
    /// function phases have a bail-out: if an instruction runs against a broken layout, its finding is a
    /// consequence of the table error rather than its cause.
    ///
    /// <para>RECURSION IS EXPLICITLY ALLOWED — <c>class Node { next: Node }</c> is valid, forward
    /// declarations included. This loop therefore checks range bounds only and does not follow the field
    /// type; that is exactly why <see cref="IrRefType"/> carries only the id.</para>
    /// </summary>
    private static void VerifyTypes(IrModule module, List<string> findings)
    {
        for (var i = 0; i < module.Types.Count; i++)
        {
            var def = module.Types[i];
            var id = new TypeId(i);

            if (def.FieldTypes.Length != def.FieldNames.Length)
            {
                findings.Add($"type {id} '{def.Name}' has {def.FieldTypes.Length} field type(s) " +
                             $"but {def.FieldNames.Length} name(s)");
                continue;
            }

            // An enum entry carries no fields of its own; its variants do. Each has to be a layout and
            // must not be an enum itself.
            for (var v = 0; v < def.Variants.Length; v++)
            {
                var variant = def.Variants[v];
                if (variant.Value < 0 || variant.Value >= module.Types.Count)
                    findings.Add($"enum {id} '{def.Name}': variant {v} references type {variant}, " +
                                 $"which is out of range (module has {module.Types.Count} type(s))");
                else if (module.Types[variant.Value].IsEnum)
                    findings.Add($"enum {id} '{def.Name}': variant {v} is itself an enum");
                else if (module.Types[variant.Value].FieldTypes.Length == 0)
                    findings.Add($"enum {id} '{def.Name}': variant {v} has no tag slot");
            }

            if (def.IsEnum && def.FieldTypes.Length > 0)
                findings.Add($"enum {id} '{def.Name}' must not have fields of its own; " +
                             "its variants carry them");

            for (var f = 0; f < def.FieldTypes.Length; f++)
            {
                switch (def.FieldTypes[f])
                {
                    // void is a return type only. A void field would have no width and no zero value; it
                    // is not a value.
                    case IrScalarType { Kind: IrScalar.Void }:
                        findings.Add($"type {id} '{def.Name}': field {new FieldId(f)} " +
                                     $"'{def.FieldNames[f]}' is void");
                        break;

                    case IrRefType r when r.Type.Value < 0 || r.Type.Value >= module.Types.Count:
                        findings.Add($"type {id} '{def.Name}': field {new FieldId(f)} " +
                                     $"'{def.FieldNames[f]}' references type {r.Type}, which is out " +
                                     $"of range (module has {module.Types.Count} type(s))");
                        break;

                    // An array field carries its element type inline; a reference inside it has to point
                    // into the table just as a direct one does.
                    case IrArrayType arr when Innermost(arr) is IrRefType inner
                                              && (inner.Type.Value < 0 || inner.Type.Value >= module.Types.Count):
                        findings.Add($"type {id} '{def.Name}': field {new FieldId(f)} " +
                                     $"'{def.FieldNames[f]}' has element type {inner.Type}, which is " +
                                     $"out of range (module has {module.Types.Count} type(s))");
                        break;
                }
            }
        }
    }

    /// <summary>Peels off array layers: <c>int[][]</c> becomes <c>int</c>. Terminates, because an array
    /// type carries its element type inline and is therefore finitely deep.</summary>
    private static IrType Innermost(IrType type)
    {
        while (type is IrArrayType a) type = a.Element;
        return type;
    }

    /// <summary>Like <see cref="Verify"/>, but throws on findings. For call sites in the lowering that
    /// may assume well-formed IR.</summary>
    /// <remarks>Deliberately WITHOUT an IR dump in the message: <see cref="IrPrinter"/> throws on a
    /// missing terminator itself and would hide exactly the finding being reported.</remarks>
    /// <summary>
    /// The impl table: every row names existing types, its interface really is one, its class is not,
    /// the row has exactly as many entries as the interface has slots, every target function exists and
    /// takes a receiver, and no pair appears twice.
    ///
    /// <para>These rows become the vtable in the bytecode. An error in them is a call of the wrong
    /// function with the right arguments — the kind of bug that shows far from its cause.</para>
    /// </summary>
    private static void VerifyImpls(IrModule module, List<string> findings)
    {
        var seen = new HashSet<(int Type, int Interface)>();

        for (var i = 0; i < module.Impls.Count; i++)
        {
            var impl = module.Impls[i];
            var where = $"impl #{i} ({impl.Type} :: {impl.Interface})";

            if (impl.Type.Value < 0 || impl.Type.Value >= module.Types.Count
                || impl.Interface.Value < 0 || impl.Interface.Value >= module.Types.Count)
            {
                findings.Add($"{where}: references a type outside the table " +
                             $"(module has {module.Types.Count} type(s))");
                continue;
            }

            var iface = module.Types[impl.Interface.Value];
            if (!iface.IsInterface)
            {
                findings.Add($"{where}: {iface.Name} is not an interface");
                continue;
            }

            if (module.Types[impl.Type.Value].IsInterface)
            {
                findings.Add($"{where}: an interface cannot implement another interface");
                continue;
            }

            if (!seen.Add((impl.Type.Value, impl.Interface.Value)))
            {
                findings.Add($"{where}: duplicate impl row — the dispatch would be ambiguous");
                continue;
            }

            if (impl.Methods.Length != iface.MethodSlots.Length)
            {
                findings.Add($"{where}: has {impl.Methods.Length} method(s) but {iface.Name} " +
                             $"declares {iface.MethodSlots.Length} slot(s)");
                continue;
            }

            for (var slot = 0; slot < impl.Methods.Length; slot++)
            {
                var target = impl.Methods[slot];
                if (target.Value < 0 || target.Value >= module.Functions.Count)
                {
                    findings.Add($"{where}: slot {slot} ({iface.MethodSlots[slot]}) targets " +
                                 $"{target}, which is out of range");
                    continue;
                }

                // The receiver is parameter 0. A target function without parameters could not take it,
                // which would be a 'static' in a vtable.
                if (module.Functions[target.Value].ParamCount == 0)
                    findings.Add($"{where}: slot {slot} ({iface.MethodSlots[slot]}) targets " +
                                 $"{module.Functions[target.Value].Name}, which takes no receiver");
            }
        }
    }

    public static void VerifyOrThrow(IrModule module)
    {
        var findings = Verify(module);
        if (findings.Count == 0) return;

        throw new InternalCompilationException(
            $"ir-verifier: malformed IR ({findings.Count} finding(s))\n  " +
            string.Join("\n  ", findings));
    }

    /// <summary>
    /// The verification context of ONE function: it lives for the duration of its check and then dies.
    /// An object rather than static methods, because the derived tables (block map, predecessors,
    /// reachability, availability) are computed once and shared by all checks. Per function rather than
    /// per module, because temp, local and block ids start at 0 in every function and all tables are
    /// therefore function-local. The shape follows LLVM's <c>Verifier</c> and rustc's
    /// <c>CfgChecker</c> and <c>TypeChecker</c>.
    ///
    /// Traversal through a <c>switch</c> rather than a visitor, as in <see cref="IrPrinter"/> and for
    /// the same reason: the <c>default</c> throw forces completeness as soon as a new instruction is
    /// added. An unknown instruction type is NOT a finding but a throw: "the verifier is out of date" is
    /// a different class of bug than "the IR is broken".
    /// </summary>
    private sealed class FunctionVerifier
    {
        private readonly IrModule _module; // for resolving call targets only
        private readonly IrFunction _fn;
        private readonly List<string> _findings;

        // Phase 0
        private readonly Dictionary<TempId, string> _defSite = new();
        // Phase 1
        private readonly Dictionary<BlockId, IrBlock> _blockById = new();
        private readonly Dictionary<BlockId, List<BlockId>> _preds = new();
        private readonly Dictionary<BlockId, List<BlockId>> _succs = new();
        // Phase 2
        private readonly HashSet<BlockId> _reachable = new();
        private List<BlockId> _rpo = new();
        private readonly Dictionary<BlockId, HashSet<TempId>> _defs = new();
        private readonly Dictionary<BlockId, HashSet<TempId>> _availIn = new();
        private readonly Dictionary<BlockId, HashSet<TempId>> _availOut = new();

        public FunctionVerifier(IrModule module, IrFunction function, List<string> findings)
        {
            _module = module;
            _fn = function;
            _findings = findings;
        }

        public void Run()
        {
            if (!CheckTables()) return;
            if (!CheckHandlers()) return;
            if (!CheckCfgShape()) return;
            ComputeReachabilityAndAvailability();
            CheckInstructions();
        }

        /// <summary>
        /// The protected regions. Runs BEFORE the CFG check, because the reachability uses them as
        /// roots; a range into nothing would miss there.
        /// </summary>
        private bool CheckHandlers()
        {
            var ok = true;
            var count = _fn.Blocks.Count;

            for (var i = 0; i < _fn.Handlers.Count; i++)
            {
                var h = _fn.Handlers[i];
                var where = $"handler #{i}";

                if (h.Start.Value < 0 || h.End.Value > count || h.Start.Value >= h.End.Value)
                {
                    Report($"{where}: protected range [{h.Start}, {h.End}) is not a valid " +
                           $"block range (function has {count} block(s))");
                    ok = false;
                    continue;
                }

                if (h.Handler.Value < 0 || h.Handler.Value >= count)
                {
                    Report($"{where}: handler block {h.Handler} is out of range");
                    ok = false;
                    continue;
                }

                // A handler protecting itself would be an infinite loop while unwinding: its own throw
                // would find it again.
                if (h.Handler.Value >= h.Start.Value && h.Handler.Value < h.End.Value)
                {
                    Report($"{where}: handler block {h.Handler} lies inside its own protected " +
                           $"range [{h.Start}, {h.End}) — unwinding would not terminate");
                    ok = false;
                }

                if (h.Kind == IrHandlerKind.Finally && (h.CatchType is not null || h.Slot is not null))
                {
                    Report($"{where}: a finally region catches nothing and binds nothing");
                    ok = false;
                }

                if (h.Slot is { } slot && (slot.Value < 0 || slot.Value >= _fn.Locals.Count))
                {
                    Report($"{where}: binds into slot {slot}, which is outside the local table");
                    ok = false;
                }
            }

            return ok;
        }

        // ------------------------------------------------------------------ phase 0: tables

        /// <summary>Table invariants. Returns false when no lookup is safe from here on.</summary>
        private bool CheckTables()
        {
            var ok = true;

            // Dense tables are not cosmetic: the id IS the slot index in the bytecode. A gap or a
            // permutation shows up as a wrong-slot read in the VM.
            for (var i = 0; i < _fn.Locals.Count; i++)
            {
                var local = _fn.Locals[i];
                if (local.Id.Value != i)
                {
                    Report($"locals table not dense at index {N(i)}: found {local.Id}");
                    ok = false;
                }

                // There are no void values; void is a function return type only.
                if (IsVoid(local.Type))
                {
                    Report($"local {local.Id} ({local.Name}) has type void");
                    ok = false;
                }
            }

            for (var i = 0; i < _fn.Temps.Count; i++)
            {
                var temp = _fn.Temps[i];
                if (temp.Id.Value != i)
                {
                    Report($"temps table not dense at index {N(i)}: found {temp.Id}");
                    ok = false;
                }

                if (IsVoid(temp.Type))
                {
                    Report($"temp {temp.Id} has type void");
                    ok = false;
                }
            }

            // Convention: the first ParamCount locals ARE the parameters, in order. Without it the IR
            // carries no parameter types and a call is not type-checkable.
            if (_fn.ParamCount < 0 || _fn.ParamCount > _fn.Locals.Count)
            {
                Report($"paramCount {N(_fn.ParamCount)} out of range (locals: {N(_fn.Locals.Count)})");
                ok = false;
            }

            if (!ok) return false; // from here on everything hangs on TypeOf and LocalTypeOf

            // Exactly one definition per temp — the "SSA-light" promise, without which every def/use
            // argument in phase 3 is worthless. Runs over ALL blocks, unreachable ones included: a temp
            // defined twice is malformed in dead code too.
            foreach (var block in _fn.Blocks)
            {
                for (var i = 0; i < block.Insts.Count; i++)
                {
                    if (IrShape.DestOf(block.Insts[i]) is not { } dest) continue;

                    if (!IsKnownTemp(dest))
                    {
                        Report(block.Id, i, $"dest {dest} is not in the temp table");
                        ok = false;
                    }
                    else if (!_defSite.TryAdd(dest, $"{block.Id}: #{N(i)}"))
                    {
                        Report(block.Id, i, $"{dest} is defined more than once " +
                                            $"(first at {_defSite[dest]})");
                        ok = false;
                    }
                }
            }

            // A declared but never defined temp reserves a VM slot for nothing. The reverse case —
            // defined but never used — is legal: a discarded call result such as `foo();` for
            // `foo(): int`.
            foreach (var temp in _fn.Temps)
            {
                if (!_defSite.ContainsKey(temp.Id))
                {
                    Report($"{temp.Id} is declared in the temp table but never defined");
                    ok = false;
                }
            }

            return ok;
        }

        // ------------------------------------------------------------------ phase 1: CFG shape

        /// <summary>The CFG shape and the predecessor table. Returns false when reachability and
        /// availability would not be computable.</summary>
        private bool CheckCfgShape()
        {
            if (_fn.Blocks.Count == 0)
            {
                Report("no blocks");
                return false;
            }

            var ok = true;

            for (var i = 0; i < _fn.Blocks.Count; i++)
            {
                var block = _fn.Blocks[i];
                if (!_blockById.TryAdd(block.Id, block))
                {
                    Report($"duplicate block id {block.Id}");
                    return false; // without unique ids every target resolution is guesswork
                }

                if (block.Id.Value != i)
                {
                    Report($"block table not dense at index {N(i)}: found {block.Id}");
                    ok = false;
                }
            }

            if (!_blockById.ContainsKey(_fn.Entry))
            {
                Report($"entry block {_fn.Entry} does not exist");
                return false;
            }

            if (_fn.Entry != _fn.Blocks[0].Id)
            {
                Report($"entry is {_fn.Entry}, expected the first block {_fn.Blocks[0].Id}");
                ok = false;
            }

            foreach (var block in _fn.Blocks)
            {
                if (block.Terminator is null)
                {
                    Report($"{block.Id}: has no terminator");
                    return false; // no terminator means no successors, so reachability would lie
                }
            }

            if (!ok) return false;

            foreach (var block in _fn.Blocks)
            {
                _preds[block.Id] = new List<BlockId>();
                _succs[block.Id] = new List<BlockId>();
            }

            foreach (var block in _fn.Blocks)
            {
                foreach (var target in IrShape.SuccessorsOf(block.Terminator!))
                {
                    if (!_blockById.ContainsKey(target))
                    {
                        ReportTerm(block.Id, $"branches to unknown block {target}");
                        ok = false;
                        continue;
                    }

                    _succs[block.Id].Add(target);
                    _preds[target].Add(block.Id);
                }
            }

            if (!ok) return false;

            // The entry is the only place for parameter setup, and a jump back into it would repeat
            // that. No bail-out: availIn[entry] stays permanently empty, which is correct for the
            // analysis, since on the first pass no temp is defined there.
            if (_preds[_fn.Entry].Count > 0)
            {
                Report($"entry {_fn.Entry} has predecessors " +
                       string.Join(", ", _preds[_fn.Entry]));
            }

            return true;
        }

        // ---------------------------------------------- phase 2: reachability and availability

        private void ComputeReachabilityAndAvailability()
        {
            ComputeReachabilityAndOrder();

            foreach (var block in _fn.Blocks)
            {
                if (!_reachable.Contains(block.Id))
                    Report($"{block.Id}: unreachable from entry {_fn.Entry}");
            }

            ComputeAvailability();
        }

        /// <summary>An iterative DFS over the successors, collecting reachability and postorder.
        /// Iterative rather than recursive, because deeply nested blocks would blow the CLR
        /// stack.</summary>
        private void ComputeReachabilityAndOrder()
        {
            var postorder = new List<BlockId>();
            var stack = new Stack<(BlockId Block, int NextSuccessor)>();

            _reachable.Add(_fn.Entry);
            stack.Push((_fn.Entry, 0));

            // Handler blocks are additional roots. They have no predecessor in the CFG: they are
            // reached through the handler table while unwinding, not through a jump. Without anchoring
            // them here the verifier reports every catch block as unreachable, and the rule
            // "unreachable blocks are an error" would make exceptions impossible.
            foreach (var handler in _fn.Handlers)
            {
                if (handler.Handler.Value < 0 || handler.Handler.Value >= _fn.Blocks.Count) continue;
                if (_reachable.Add(handler.Handler)) stack.Push((handler.Handler, 0));
            }

            while (stack.Count > 0)
            {
                var (block, next) = stack.Pop();
                var successors = _succs[block];

                if (next < successors.Count)
                {
                    stack.Push((block, next + 1));
                    var child = successors[next];
                    if (_reachable.Add(child)) stack.Push((child, 0));
                }
                else
                {
                    postorder.Add(block);
                }
            }

            postorder.Reverse();
            _rpo = postorder; // reverse postorder: predecessors almost always before their block
        }

        /// <summary>
        /// Availability: which temps are already defined at the block entry on EVERY path? A forward
        /// data flow with intersection as the meet. With exactly one definition per temp (phase 0),
        /// "available on every path" is equivalent to "the definition dominates the use", which is why
        /// the verifier needs no dominator tree. That becomes interesting only once phi nodes exist.
        /// </summary>
        private void ComputeAvailability()
        {
            var allTemps = new HashSet<TempId>(_fn.Temps.Select(t => t.Id));

            foreach (var block in _rpo)
            {
                _defs[block] = new HashSet<TempId>();
                foreach (var op in _blockById[block].Insts)
                    if (IrShape.DestOf(op) is { } dest) _defs[block].Add(dest);
            }

            foreach (var block in _rpo)
            {
                // An optimistic TOP for everything except the entry: a loop header is intersected
                // through its back edge against a not-yet-final availOut first. Starting pessimistically
                // with the empty set would converge on sets that are too small and would hide real
                // use-before-def errors.
                _availIn[block] = block == _fn.Entry
                    ? new HashSet<TempId>() // parameters are LOCALS, not temps
                    : new HashSet<TempId>(allTemps);
                _availOut[block] = Union(_availIn[block], _defs[block]);
            }

            bool changed;
            do
            {
                changed = false;
                foreach (var block in _rpo)
                {
                    if (block != _fn.Entry)
                    {
                        var incoming = MeetOfPredecessors(block);
                        if (!incoming.SetEquals(_availIn[block]))
                        {
                            _availIn[block] = incoming;
                            changed = true;
                        }
                    }

                    var outgoing = Union(_availIn[block], _defs[block]);
                    if (!outgoing.SetEquals(_availOut[block]))
                    {
                        _availOut[block] = outgoing;
                        changed = true;
                    }
                }
            } while (changed); // monotonically shrinking sets over a finite base set, so it terminates
        }

        private HashSet<TempId> MeetOfPredecessors(BlockId block)
        {
            HashSet<TempId>? intersection = null;

            foreach (var pred in _preds[block])
            {
                // Unreachable predecessors have to go: their availOut never stabilized and would
                // distort the intersection.
                if (!_reachable.Contains(pred)) continue;

                if (intersection is null) intersection = new HashSet<TempId>(_availOut[pred]);
                else intersection.IntersectWith(_availOut[pred]);
            }

            // Cannot happen for reachable non-entry blocks, which by definition have a reachable
            // predecessor; the empty set is the conservative fallback.
            return intersection ?? new HashSet<TempId>();
        }

        private static HashSet<TempId> Union(HashSet<TempId> a, HashSet<TempId> b)
        {
            var result = new HashSet<TempId>(a);
            result.UnionWith(b);
            return result;
        }

        // -------------------------------------------------- phase 3: def/use and types

        private void CheckInstructions()
        {
            foreach (var blockId in _rpo) // reachable blocks only
            {
                var block = _blockById[blockId];

                // 'live' grows instruction by instruction, so "defined in the same block but textually
                // after the use" shows without a special case.
                var live = new HashSet<TempId>(_availIn[blockId]);

                for (var i = 0; i < block.Insts.Count; i++)
                {
                    var op = block.Insts[i];
                    if (CheckOperands(IrShape.OperandsOf(op), live, blockId, i))
                        CheckOpTypes(op, blockId, i);
                    if (IrShape.DestOf(op) is { } dest) live.Add(dest);
                }

                var terminator = block.Terminator!;
                if (CheckOperands(IrShape.OperandsOf(terminator), live, blockId, index: null))
                    CheckTerminatorTypes(terminator, blockId);
            }
        }

        /// <summary>Checks that every operand is a known temp already defined at this point. Returns
        /// false when an operand is unknown, in which case the type checks are not runnable, because the
        /// table lookup would miss.</summary>
        private bool CheckOperands(IReadOnlyList<TempId> operands, HashSet<TempId> live,
            BlockId block, int? index)
        {
            var usable = true;

            foreach (var operand in operands)
            {
                if (!IsKnownTemp(operand))
                {
                    ReportAt(block, index, $"uses {operand}, which is not in the temp table");
                    usable = false;
                }
                else if (!live.Contains(operand))
                {
                    var site = _defSite.TryGetValue(operand, out var where) ? $" (defined at {where})" : "";
                    ReportAt(block, index, $"uses {operand} before its definition{site}");
                }
            }

            return usable;
        }

        private void CheckOpTypes(IrOp op, BlockId block, int index)
        {
            switch (op)
            {
                case Const c: CheckConst(c, block, index); break;
                case BinOp b: CheckBinOp(b, block, index); break;
                case UnOp u: CheckUnOp(u, block, index); break;
                case Convert cv: CheckConvert(cv, block, index); break;
                case LoadLocal l: CheckLoadLocal(l, block, index); break;
                case StoreLocal s: CheckStoreLocal(s, block, index); break;
                case Call k: CheckCall(k, block, index); break;
                case CallImport k: CheckCallImport(k, block, index); break;
                case NewObject n: CheckNewObject(n, block, index); break;
                case LoadField f: CheckLoadField(f, block, index); break;
                case StoreField f: CheckStoreField(f, block, index); break;
                case NewArray a: CheckNewArray(a, block, index); break;
                case LoadElem e: CheckLoadElem(e, block, index); break;
                case StoreElem e: CheckStoreElem(e, block, index); break;
                case ArrayLen a: CheckArrayLen(a, block, index); break;
                case ArrayConcat c: CheckArrayConcat(c, block, index); break;
                case ArrayRepeat r: CheckArrayRepeat(r, block, index); break;
                case OptNone n: CheckOptNone(n, block, index); break;
                case OptSome s: CheckOptSome(s, block, index); break;
                case OptIsSome i: CheckOptIsSome(i, block, index); break;
                case OptGet g: CheckOptGet(g, block, index); break;
                case NewVariant v: CheckNewVariant(v, block, index); break;
                case EnumTag t: CheckEnumTag(t, block, index); break;
                case EnumAs a: CheckEnumAs(a, block, index); break;
                case MakeInterface m: CheckMakeInterface(m, block, index); break;
                case CallVirt c: CheckCallVirt(c, block, index); break;
                case StructCopy c: CheckStructCopy(c, block, index); break;
                case LoadGlobal l: CheckLoadGlobal(l, block, index); break;
                case StoreGlobal g: CheckStoreGlobal(g, block, index); break;
                case MakeClosure m: CheckMakeClosure(m, block, index); break;
                case CallIndirect c: CheckCallIndirect(c, block, index); break;
                default:
                    throw new InternalCompilationException(
                        $"ir-verifier: unhandled op {op.GetType().Name}");
            }
        }

        private void CheckConst(Const c, BlockId block, int index)
        {
            RequireDestType(c.Dest, c.Type, "const", block, index);

            if (c.Type is not IrScalarType scalar)
            {
                Report(block, index, $"const type {Show(c.Type)} is not a scalar");
                return;
            }

            if (!ConstKindMatches(c.Value, scalar.Kind))
            {
                Report(block, index, $"{ConstKindName(c.Value)} const does not match type {Show(c.Type)}");
                return;
            }

            switch (c.Value)
            {
                // The encoding of IntConst: two's complement, zero-extended to 64 bits. The value has to
                // fit into the declared width as a bit pattern, which catches a lowering that failed to
                // truncate or sign-extend a literal.
                case IntConst ic when !FitsWidth(ic.Value, scalar.Kind):
                    Report(block, index,
                        $"integer const {N(ic.Value)} does not fit the bit pattern of {Show(c.Type)}");
                    break;

                // An f32 const whose value is no f32 value means the lowering did not narrow.
                // Non-finite values (NaN, Inf) are representable in f32 and are exempt.
                case FloatConst fc when scalar.Kind == IrScalar.F32
                                        && double.IsFinite(fc.Value)
                                        && (float)fc.Value != fc.Value:
                    Report(block, index,
                        $"float const {fc.Value.ToString("R", CultureInfo.InvariantCulture)} " +
                        "is not exactly representable as f32");
                    break;

                case CharConst ch when !IsUnicodeScalarValue(ch.CodePoint):
                    Report(block, index,
                        $"char const {N(ch.CodePoint)} is not a Unicode scalar value");
                    break;
            }
        }

        private void CheckBinOp(BinOp b, BlockId block, int index)
        {
            var lhs = TypeOf(b.Lhs);
            var rhs = TypeOf(b.Rhs);

            // Arithmetic is strict: no implicit widening.
            if (!IrType.Equal(lhs, rhs))
            {
                Report(block, index,
                    $"operand types differ: {b.Lhs} is {Show(lhs)}, {b.Rhs} is {Show(rhs)}");
                return;
            }

            if (b.Kind.IsComparison())
            {
                // Comparisons yield bool. The operand type is NOT on the instruction but in the temp
                // table, where the emitter looks it up, because signed and unsigned are different
                // opcodes. Deliberately no second type field: that would be a third source of truth,
                // free to drift.
                if (!IsBool(b.Type) || !IsBool(TypeOf(b.Dest)))
                    Report(block, index,
                        $"comparison must produce bool, found type {Show(b.Type)} " +
                        $"and dest {b.Dest} of {Show(TypeOf(b.Dest))}");

                var ordering = b.Kind is not (IrBinKind.Eq or IrBinKind.Ne);
                if (ordering && !IsNumeric(lhs))
                    Report(block, index,
                        $"ordering comparison {IrNames.Bin(b.Kind)} on non-numeric type {Show(lhs)}");
                else if (!ordering && !IsEquatable(lhs))
                    Report(block, index, $"equality comparison on type {Show(lhs)}");

                return;
            }

            if (!IrType.Equal(b.Type, lhs) || !IrType.Equal(TypeOf(b.Dest), lhs))
                Report(block, index,
                    $"{IrNames.Bin(b.Kind)} result must have the operand type {Show(lhs)}, found type " +
                    $"{Show(b.Type)} and dest {b.Dest} of {Show(TypeOf(b.Dest))}");

            if (IsBitwiseOrShift(b.Kind))
            {
                if (!IsInteger(lhs))
                    Report(block, index, $"{IrNames.Bin(b.Kind)} on non-integer type {Show(lhs)}");
            }
            else if (!IsNumeric(lhs))
            {
                // string+string and T[]+T[] are built-in semantics but NO BinOp: they lower to a call or
                // an intrinsic. Otherwise the add opcode would be polymorphic and would have to dispatch
                // on the type at runtime.
                var hint = IsStringLike(lhs) && b.Kind is IrBinKind.Add or IrBinKind.Mul
                    ? " (string concatenation/repetition lowers to a call, not a binop)"
                    : "";
                Report(block, index, $"{IrNames.Bin(b.Kind)} on non-numeric type {Show(lhs)}{hint}");
            }
        }

        private void CheckUnOp(UnOp u, BlockId block, int index)
        {
            var operand = TypeOf(u.Operand);

            if (!IrType.Equal(u.Type, operand) || !IrType.Equal(TypeOf(u.Dest), operand))
                Report(block, index,
                    $"{IrNames.Un(u.Kind)} result must have the operand type {Show(operand)}, found type " +
                    $"{Show(u.Type)} and dest {u.Dest} of {Show(TypeOf(u.Dest))}");

            switch (u.Kind)
            {
                case IrUnKind.Neg when !IsNumeric(operand):
                    Report(block, index, $"neg on non-numeric type {Show(operand)}");
                    break;
                case IrUnKind.Not when !IsBool(operand):
                    Report(block, index, $"not on non-bool type {Show(operand)}");
                    break;
                case IrUnKind.BitNot when !IsInteger(operand):
                    Report(block, index, $"bitnot on non-integer type {Show(operand)}");
                    break;
            }
        }

        private void CheckConvert(Convert cv, BlockId block, int index)
        {
            var operand = TypeOf(cv.Operand);
            if (!IrType.Equal(cv.From, operand))
                Report(block, index,
                    $"convert declares from-type {Show(cv.From)} but {cv.Operand} is {Show(operand)}");

            RequireDestType(cv.Dest, cv.To, "convert", block, index);

            // 'as' converts between numeric types only.
            if (!IsNumeric(cv.From) || !IsNumeric(cv.To))
            {
                Report(block, index,
                    $"convert {Show(cv.From)} -> {Show(cv.To)} is not numeric<->numeric");
                return;
            }

            // The lowering elides identity conversions: `x as int` for x: int is legal Lyric but yields
            // no meaningful opcode.
            if (IrType.Equal(cv.From, cv.To))
                Report(block, index, $"identity convert {Show(cv.From)} -> {Show(cv.To)}");
        }

        private void CheckLoadLocal(LoadLocal l, BlockId block, int index)
        {
            if (LocalTypeOf(l.Local) is not { } localType)
            {
                Report(block, index, $"load from unknown local {l.Local}");
                return;
            }

            if (!IrType.Equal(l.Type, localType))
                Report(block, index,
                    $"load declares type {Show(l.Type)} but {l.Local} is {Show(localType)}");

            RequireDestType(l.Dest, l.Type, "load", block, index);
        }

        private void CheckStoreLocal(StoreLocal s, BlockId block, int index)
        {
            if (LocalTypeOf(s.Local) is not { } localType)
            {
                Report(block, index, $"store to unknown local {s.Local}");
                return;
            }

            var value = TypeOf(s.Value);
            if (!IrType.Equal(localType, value))
                Report(block, index,
                    $"store of {s.Value} ({Show(value)}) into {s.Local} ({Show(localType)})");
        }

        private void CheckCall(Call k, BlockId block, int index)
        {
            if (k.Target.Value < 0 || k.Target.Value >= _module.Functions.Count)
            {
                Report(block, index, $"call target {k.Target} is out of range " +
                                     $"(module has {N(_module.Functions.Count)} function(s))");
                return;
            }

            var callee = _module.Functions[k.Target.Value];

            if (k.Args.Length != callee.ParamCount)
            {
                Report(block, index, $"call to {callee.Name} passes {N(k.Args.Length)} arg(s), " +
                                     $"expected {N(callee.ParamCount)}");
            }
            else if (callee.ParamCount > callee.Locals.Count)
            {
                // The callee is malformed itself; that is reported when IT is checked. Here it just must
                // not miss.
                Report(block, index,
                    $"cannot check args: callee {callee.Name} has a malformed local table");
            }
            else
            {
                for (var i = 0; i < k.Args.Length; i++)
                {
                    var expected = callee.Locals[i].Type; // convention: the first N locals are the parameters
                    var actual = TypeOf(k.Args[i]);
                    if (!IrType.Equal(expected, actual))
                        Report(block, index, $"call to {callee.Name}: arg {N(i)} is {Show(actual)}, " +
                                             $"expected {Show(expected)}");
                }
            }

            var returnsVoid = IsVoid(callee.ReturnType);
            if (returnsVoid && k.Dest is { } unwanted)
                Report(block, index,
                    $"call to void function {callee.Name} must not have a dest (found {unwanted})");
            else if (!returnsVoid && k.Dest is null)
                Report(block, index,
                    $"call to {callee.Name} returning {Show(callee.ReturnType)} must have a dest");
            else if (k.Dest is { } dest && !IrType.Equal(callee.ReturnType, TypeOf(dest)))
                Report(block, index, $"call dest {dest} is {Show(TypeOf(dest))} but {callee.Name} " +
                                     $"returns {Show(callee.ReturnType)}");
        }

        /// <summary>Like <see cref="CheckCall"/>, but against the import table. An import has no body,
        /// so the signature comes from its declaration rather than from a function.</summary>
    private void CheckCallImport(CallImport k, BlockId block, int index)
    {
        if (k.Target.Value < 0 || k.Target.Value >= _module.Imports.Count)
        {
            Report(block, index, $"import target {k.Target} is out of range " +
                                 $"(module has {N(_module.Imports.Count)} import(s))");
            return;
        }

        var import = _module.Imports[k.Target.Value];

        if (k.Args.Length != import.ParamTypes.Length)
        {
            Report(block, index, $"call to import '{import.Name}' passes {N(k.Args.Length)} arg(s), " +
                                 $"expected {N(import.ParamTypes.Length)}");
        }
        else
        {
            for (var i = 0; i < k.Args.Length; i++)
            {
                var actual = TypeOf(k.Args[i]);
                if (!IrType.Equal(import.ParamTypes[i], actual))
                    Report(block, index, $"call to import '{import.Name}': arg {N(i)} is " +
                                         $"{Show(actual)}, expected {Show(import.ParamTypes[i])}");
            }
        }

        var returnsVoid = IsVoid(import.ReturnType);
        if (returnsVoid && k.Dest is { } unwanted)
            Report(block, index,
                $"call to void import '{import.Name}' must not have a dest (found {unwanted})");
        else if (!returnsVoid && k.Dest is null)
            Report(block, index,
                $"call to import '{import.Name}' returning {Show(import.ReturnType)} must have a dest");
        else if (k.Dest is { } dest && !IrType.Equal(import.ReturnType, TypeOf(dest)))
            Report(block, index, $"call dest {dest} is {Show(TypeOf(dest))} but import " +
                                 $"'{import.Name}' returns {Show(import.ReturnType)}");
    }

    /// <summary>Resolves a <see cref="TypeId"/> against the module table. <c>null</c> means the index
    /// points into nothing and was already reported; the caller then stops rather than continuing with a
    /// substitute layout and producing follow-up findings.</summary>
    /// <summary>
    /// <c>mkiface</c>: the source is a class or enum reference, the target an interface entry, and THERE
    /// IS A VTABLE ROW FOR EXACTLY THIS PAIR.
    ///
    /// <para>The last condition is the actual invariant. Without it an interface value would arise whose
    /// concrete type does not satisfy the interface at all, and the dispatch would run into nothing at
    /// the call, with an error that says nothing about the cause.</para>
    /// </summary>
    private void CheckMakeInterface(MakeInterface m, BlockId block, int index)
    {
        var concrete = TypeOf(m.Value) switch
        {
            IrRefType r => (TypeId?)r.Type,
            IrStructType v => v.Type,
            IrEnumType e => e.Type,
            _ => null,
        };

        if (concrete is null)
        {
            Report(block, index,
                $"mkiface expects a class, struct or enum value, found {Show(TypeOf(m.Value))}");
            return;
        }

        if (concrete.Value != m.Concrete)
        {
            Report(block, index, $"mkiface declares concrete type {m.Concrete} but its operand is " +
                                 $"{Show(TypeOf(m.Value))}");
            return;
        }

        if (ResolveType(m.Concrete, "mkiface", block, index) is null) return;
        if (ResolveType(m.Interface, "mkiface", block, index) is not { } iface) return;

        if (!iface.IsInterface)
        {
            Report(block, index, $"mkiface targets type {m.Interface} ({iface.Name}), " +
                                 "which is not an interface");
            return;
        }

        if (!_module.Impls.Any(i => i.Type == m.Concrete && i.Interface == m.Interface))
        {
            Report(block, index, $"mkiface lifts type {m.Concrete} to interface {m.Interface} " +
                                 $"({iface.Name}), but no impl row says it implements it");
            return;
        }

        RequireDestType(m.Dest, new IrInterfaceType(m.Interface), "mkiface", block, index);
    }

    /// <summary>
    /// <c>callvirt</c>: the receiver (argument 0) is a value of exactly this interface, and the slot lies
    /// within its method list.
    ///
    /// <para>The argument types are NOT checked against a target function, because there is none: which
    /// one runs is decided at runtime. The signature stands on the interface, and that all
    /// implementations satisfy it was checked by the sema. What the verifier holds here is the shape;
    /// the congruence of the vtable rows is checked by <see cref="CheckImpls"/>.</para>
    /// </summary>
    /// <summary>
    /// <c>mkclosure</c>: the fat pointer of target function and environment.
    ///
    /// <para>Checked is that the target exists, that the target type is a function type, and that the
    /// ARITY matches — the lifted function has one parameter more than the type, namely the environment
    /// at position 0. An error here would be a frame with the wrong slot count at runtime, so a
    /// wrong-slot read rather than a crash.</para>
    /// </summary>
    private void CheckMakeClosure(MakeClosure m, BlockId block, int index)
    {
        RequireDestType(m.Dest, m.Type, "mkclosure", block, index);

        if (m.Target.Value < 0 || m.Target.Value >= _module.Functions.Count)
        {
            Report(block, index, $"mkclosure targets {m.Target}, which is outside " +
                                 $"{N(_module.Functions.Count)} function(s)");
            return;
        }

        var target = _module.Functions[m.Target.Value];
        var expected = m.Type.Parameters.Length + (m.Environment is null ? 0 : 1);
        if (target.ParamCount != expected)
            Report(block, index,
                $"mkclosure targets {target.Name}, which takes {N(target.ParamCount)} " +
                $"parameter(s), but the closure type needs {N(expected)} " +
                (m.Environment is null ? "(no environment)" : "(including the environment)"));

        // An environment is an ordinary object, which is why it needs no special case in the type
        // system here.
        if (m.Environment is { } env && TypeOf(env) is not IrRefType)
            Report(block, index, $"mkclosure environment is {Show(TypeOf(env))}, expected a reference");
    }

    /// <summary>
    /// <c>callind</c>: a call through a function value.
    ///
    /// <para>The signature stands in the TYPE of the callee rather than in a declaration — that is the
    /// whole difference from <c>call</c>. Arity, parameter types and return type are checked against
    /// exactly that type.</para>
    /// </summary>
    private void CheckCallIndirect(CallIndirect c, BlockId block, int index)
    {
        if (TypeOf(c.Callee) is not IrFunctionType signature)
        {
            Report(block, index, $"callind callee is {Show(TypeOf(c.Callee))}, expected a function value");
            return;
        }

        if (c.Args.Length != signature.Parameters.Length)
        {
            Report(block, index, $"callind passes {N(c.Args.Length)} arg(s), " +
                                 $"but {Show(signature)} takes {N(signature.Parameters.Length)}");
            return;
        }

        for (var i = 0; i < c.Args.Length; i++)
            if (!IrType.Equal(TypeOf(c.Args[i]), signature.Parameters[i]))
                Report(block, index, $"callind argument {N(i)} is {Show(TypeOf(c.Args[i]))}, " +
                                     $"expected {Show(signature.Parameters[i])}");

        if (!IrType.Equal(c.ReturnType, signature.Return))
            Report(block, index, $"callind is annotated {Show(c.ReturnType)}, " +
                                 $"but {Show(signature)} returns {Show(signature.Return)}");

        if (c.Dest is { } dest) RequireDestType(dest, signature.Return, "callind", block, index);
    }

    private void CheckCallVirt(CallVirt c, BlockId block, int index)
    {
        if (c.Args.Length == 0)
        {
            Report(block, index, "callvirt has no receiver (argument 0 is the interface value)");
            return;
        }

        if (ResolveType(c.Interface, "callvirt", block, index) is not { } iface) return;

        if (!iface.IsInterface)
        {
            Report(block, index, $"callvirt targets type {c.Interface} ({iface.Name}), " +
                                 "which is not an interface");
            return;
        }

        if (c.Slot < 0 || c.Slot >= iface.MethodSlots.Length)
        {
            Report(block, index, $"callvirt slot {N(c.Slot)} is out of range for interface " +
                                 $"{c.Interface} ({iface.Name} has {N(iface.MethodSlots.Length)} slot(s))");
            return;
        }

        if (TypeOf(c.Args[0]) is not IrInterfaceType receiver || receiver.Type != c.Interface)
        {
            Report(block, index, $"callvirt receiver is {Show(TypeOf(c.Args[0]))}, " +
                                 $"expected interface {c.Interface}");
            return;
        }

        if (c.Dest is { } dest) RequireDestType(dest, c.ReturnType, "callvirt", block, index);
    }

    /// <summary>
    /// <c>structcopy</c>: source and destination are the same value type, and the entry really is one.
    ///
    /// <para>A <c>structcopy</c> on a class would not be an error the runtime notices: it would simply
    /// copy a slot array that ought to be shared — a silent break of semantics.</para>
    /// </summary>
    private void CheckStructCopy(StructCopy c, BlockId block, int index)
    {
        if (ResolveType(c.Type, "structcopy", block, index) is not { } layout) return;

        if (!layout.IsStruct)
        {
            Report(block, index, $"structcopy targets type {c.Type} ({layout.Name}), which is a " +
                                 "reference type — copying it would break sharing");
            return;
        }

        if (TypeOf(c.Value) is not IrStructType source || source.Type != c.Type)
        {
            Report(block, index, $"structcopy declares type {c.Type} but its operand is " +
                                 $"{Show(TypeOf(c.Value))}");
            return;
        }

        RequireDestType(c.Dest, new IrStructType(c.Type), "structcopy", block, index);
    }

    private void CheckLoadGlobal(LoadGlobal l, BlockId block, int index)
    {
        if (ResolveGlobal(l.Global, "ldglobal", block, index) is not { } global) return;

        if (!IrType.Equal(l.Type, global.Type))
            Report(block, index, $"ldglobal of {l.Global} is declared {Show(global.Type)} " +
                                 $"but the instruction says {Show(l.Type)}");
        else
            RequireDestType(l.Dest, global.Type, "ldglobal", block, index);
    }

    private void CheckStoreGlobal(StoreGlobal g, BlockId block, int index)
    {
        if (ResolveGlobal(g.Global, "stglobal", block, index) is not { } global) return;

        var actual = TypeOf(g.Value);
        if (!IrType.Equal(global.Type, actual))
            Report(block, index, $"stglobal into {g.Global} takes {Show(global.Type)}, " +
                                 $"but {g.Value} is {Show(actual)}");
    }

    private IrGlobal? ResolveGlobal(GlobalId id, string what, BlockId block, int index)
    {
        if (id.Value >= 0 && id.Value < _module.Globals.Count) return _module.Globals[id.Value];

        Report(block, index, $"{what} references global {id} which is out of range " +
                             $"(module has {N(_module.Globals.Count)} global(s))");
        return null;
    }

    private IrTypeDef? ResolveType(TypeId type, string what, BlockId block, int index)
    {
        if (type.Value >= 0 && type.Value < _module.Types.Count) return _module.Types[type.Value];

        Report(block, index, $"{what} references type {type} which is out of range " +
                             $"(module has {N(_module.Types.Count)} type(s))");
        return null;
    }

    /// <summary>Returns the declared field type, or <c>null</c> on a range or layout error. Also checks
    /// that the type list and the name list have the same length: a divergence would otherwise only
    /// surface in the printer as an index exception.</summary>
    private IrType? ResolveField(IrTypeDef def, TypeId type, FieldId field, string what,
        BlockId block, int index)
    {
        if (def.FieldTypes.Length != def.FieldNames.Length)
        {
            Report(block, index, $"type {type} '{def.Name}' has {N(def.FieldTypes.Length)} field " +
                                 $"type(s) but {N(def.FieldNames.Length)} name(s)");
            return null;
        }

        if (field.Value >= 0 && field.Value < def.FieldTypes.Length) return def.FieldTypes[field.Value];

        Report(block, index, $"{what} references field {field} of type {type} '{def.Name}', " +
                             $"which has {N(def.FieldTypes.Length)} field(s)");
        return null;
    }

    /// <summary>The object operand has to be a reference to EXACTLY the type the instruction names.
    /// Carrying both is deliberate: the type in the instruction stream makes the field index check
    /// possible at load time without data-flow analysis. That is why the verifier has to enforce here
    /// that the two do not drift apart, or the bytecode reader later checks against the wrong
    /// layout.</summary>
    /// <summary>
    /// The operand holds an object of this layout — a class OR a struct.
    ///
    /// <para>Both are the same slot array at runtime and field access is the same array access. The
    /// difference between value and reference semantics lies not in the access but in the binding points
    /// (<c>structcopy</c>), so <c>ldfld</c> and <c>stfld</c> may accept both.</para>
    /// </summary>
    private bool RequireObject(TempId obj, TypeId type, string what, BlockId block, int index)
    {
        var actual = TypeOf(obj);
        if (actual is IrRefType r && r.Type == type) return true;
        if (actual is IrStructType v && v.Type == type) return true;

        Report(block, index, $"{what} expects {obj} to hold type {type}, found {Show(actual)}");
        return false;
    }

    /// <summary>How a layout looks at a value: as a reference or as a value type. The Types table
    /// decides, not the caller; two opinions about it would be a <c>structcopy</c> on a class or a shared
    /// struct instance.</summary>
    private IrType LayoutTypeOf(TypeId type) =>
        _module.Types[type.Value].IsStruct ? new IrStructType(type) : new IrRefType(type);

    private void CheckNewObject(NewObject n, BlockId block, int index)
    {
        if (ResolveType(n.Type, "newobj", block, index) is null) return;

        var expected = LayoutTypeOf(n.Type);
        if (!IrType.Equal(n.Result, expected))
            Report(block, index, $"newobj of {n.Type} yields {Show(expected)} " +
                                 $"but the instruction says {Show(n.Result)}");

        RequireDestType(n.Dest, expected, "newobj", block, index);
    }

    private void CheckLoadField(LoadField f, BlockId block, int index)
    {
        if (ResolveType(f.Type, "loadfield", block, index) is not { } def) return;
        if (ResolveField(def, f.Type, f.Field, "loadfield", block, index) is not { } declared) return;
        if (!RequireObject(f.Object, f.Type, "loadfield", block, index)) return;

        // As everywhere in this IR: the Type field on the instruction is a copy for the printer, and the
        // temp table is the authority. Checking both against the declaration is the verifier's core job.
        if (!IrType.Equal(f.FieldType, declared))
            Report(block, index, $"loadfield of {f.Type}{f.Field} is declared {Show(declared)} " +
                                 $"but the instruction says {Show(f.FieldType)}");
        else
            RequireDestType(f.Dest, declared, "loadfield", block, index);
    }

    private void CheckStoreField(StoreField f, BlockId block, int index)
    {
        if (ResolveType(f.Type, "storefield", block, index) is not { } def) return;
        if (ResolveField(def, f.Type, f.Field, "storefield", block, index) is not { } declared) return;
        if (!RequireObject(f.Object, f.Type, "storefield", block, index)) return;

        var actual = TypeOf(f.Value);
        if (!IrType.Equal(declared, actual))
            Report(block, index, $"storefield into {f.Type}{f.Field} takes {Show(declared)}, " +
                                 $"but {f.Value} is {Show(actual)}");
    }

    /// <summary>The array operand has to be an array; returns the element type, or <c>null</c> after a
    /// reported finding.</summary>
    private IrType? RequireArray(TempId array, string what, BlockId block, int index)
    {
        if (TypeOf(array) is IrArrayType a) return a.Element;

        Report(block, index, $"{what} expects {array} to be an array, found {Show(TypeOf(array))}");
        return null;
    }

    /// <summary>An index has to be <c>i64</c>. Not WHETHER it is within bounds — that is a runtime value
    /// and becomes a <c>panic</c> at runtime. The verifier checks the shape, not the program.</summary>
    private void RequireIndex(TempId index, string what, BlockId block, int at)
    {
        if (TypeOf(index) is IrScalarType { Kind: IrScalar.I64 }) return;

        Report(block, at, $"{what} index {index} is {Show(TypeOf(index))}, expected i64");
    }

    private void CheckNewArray(NewArray a, BlockId block, int index)
    {
        RequireDestType(a.Dest, new IrArrayType(a.Element), "newarr", block, index);

        for (var i = 0; i < a.Elements.Length; i++)
        {
            var actual = TypeOf(a.Elements[i]);
            if (!IrType.Equal(a.Element, actual))
                Report(block, index, $"newarr element {N(i)} is {Show(actual)}, " +
                                     $"expected {Show(a.Element)}");
        }
    }

    private void CheckLoadElem(LoadElem e, BlockId block, int index)
    {
        if (RequireArray(e.Array, "loadelem", block, index) is not { } element) return;
        RequireIndex(e.Index, "loadelem", block, index);

        // As everywhere: the Type field on the instruction is a copy for the printer, and the temp table
        // is the authority.
        if (!IrType.Equal(e.Element, element))
            Report(block, index, $"loadelem yields {Show(element)} but the instruction says " +
                                 $"{Show(e.Element)}");
        else
            RequireDestType(e.Dest, element, "loadelem", block, index);
    }

    private void CheckStoreElem(StoreElem e, BlockId block, int index)
    {
        if (RequireArray(e.Array, "storeelem", block, index) is not { } element) return;
        RequireIndex(e.Index, "storeelem", block, index);

        var actual = TypeOf(e.Value);
        if (!IrType.Equal(element, actual))
            Report(block, index, $"storeelem takes {Show(element)}, but {e.Value} is {Show(actual)}");
    }

    private void CheckArrayLen(ArrayLen a, BlockId block, int index)
    {
        if (RequireArray(a.Array, "arraylen", block, index) is null) return;
        RequireDestType(a.Dest, new IrScalarType(IrScalar.I64), "arraylen", block, index);
    }

    private void CheckArrayConcat(ArrayConcat c, BlockId block, int index)
    {
        if (RequireArray(c.Left, "arrcat", block, index) is not { } left) return;
        if (RequireArray(c.Right, "arrcat", block, index) is not { } right) return;

        if (!IrType.Equal(left, right))
        {
            Report(block, index, $"arrcat joins {Show(left)}[] and {Show(right)}[]");
            return;
        }

        if (!IrType.Equal(c.Element, left))
            Report(block, index, $"arrcat yields {Show(left)}[] but the instruction says " +
                                 $"{Show(c.Element)}[]");
        else
            RequireDestType(c.Dest, new IrArrayType(left), "arrcat", block, index);
    }

    private void CheckArrayRepeat(ArrayRepeat r, BlockId block, int index)
    {
        if (RequireArray(r.Array, "arrrep", block, index) is not { } element) return;
        RequireIndex(r.Count, "arrrep", block, index);

        if (!IrType.Equal(r.Element, element))
            Report(block, index, $"arrrep yields {Show(element)}[] but the instruction says " +
                                 $"{Show(r.Element)}[]");
        else
            RequireDestType(r.Dest, new IrArrayType(element), "arrrep", block, index);
    }

    /// <summary>An optional is not nestable: <c>??T</c> would be indistinguishable from <c>?T</c> in the
    /// runtime representation.</summary>
    private bool RequireNotOptional(IrType inner, string what, BlockId block, int index)
    {
        if (inner is not IrOptionalType) return true;

        Report(block, index, $"{what} of {Show(inner)} — optionals do not nest");
        return false;
    }

    private void CheckOptNone(OptNone n, BlockId block, int index)
    {
        if (!RequireNotOptional(n.Inner, "optnone", block, index)) return;
        RequireDestType(n.Dest, new IrOptionalType(n.Inner), "optnone", block, index);
    }

    private void CheckOptSome(OptSome s, BlockId block, int index)
    {
        if (!RequireNotOptional(s.Inner, "optsome", block, index)) return;

        var actual = TypeOf(s.Value);
        if (!IrType.Equal(s.Inner, actual))
            Report(block, index, $"optsome wraps {Show(actual)} but the instruction says {Show(s.Inner)}");
        else
            RequireDestType(s.Dest, new IrOptionalType(s.Inner), "optsome", block, index);
    }

    private void CheckOptIsSome(OptIsSome i, BlockId block, int index)
    {
        if (TypeOf(i.Option) is not IrOptionalType)
            Report(block, index, $"optissome expects an optional, found {Show(TypeOf(i.Option))}");
        else
            RequireDestType(i.Dest, new IrScalarType(IrScalar.Bool), "optissome", block, index);
    }

    private void CheckOptGet(OptGet g, BlockId block, int index)
    {
        if (TypeOf(g.Option) is not IrOptionalType option)
        {
            Report(block, index, $"optget expects an optional, found {Show(TypeOf(g.Option))}");
            return;
        }

        if (!IrType.Equal(g.Inner, option.Inner))
            Report(block, index, $"optget yields {Show(option.Inner)} but the instruction says " +
                                 $"{Show(g.Inner)}");
        else
            RequireDestType(g.Dest, option.Inner, "optget", block, index);
    }

    /// <summary>
    /// A variant belongs to exactly one enum. Checking that is the core of the enum invariants: an
    /// <c>enumas</c> onto a foreign variant would be a field access with the wrong layout, and the
    /// load-time validation could not catch it, because it only sees that both indices are valid.
    /// </summary>
    private int VariantIndexIn(TypeId enumType, TypeId variant)
    {
        if (enumType.Value < 0 || enumType.Value >= _module.Types.Count) return -1;
        return Array.IndexOf(_module.Types[enumType.Value].Variants, variant);
    }

    private void CheckNewVariant(NewVariant v, BlockId block, int index)
    {
        if (ResolveType(v.Enum, "newvariant", block, index) is not { } enumDef) return;
        if (ResolveType(v.Variant, "newvariant", block, index) is not { } layout) return;

        if (!enumDef.IsEnum)
        {
            Report(block, index, $"newvariant names type {v.Enum} '{enumDef.Name}', which is not an enum");
            return;
        }

        if (VariantIndexIn(v.Enum, v.Variant) < 0)
        {
            Report(block, index, $"variant {v.Variant} '{layout.Name}' does not belong to enum " +
                                 $"{v.Enum} '{enumDef.Name}'");
            return;
        }

        // Slot 0 is the tag and is set by the instruction itself; the arguments are the payload fields
        // from slot 1 on.
        var payload = layout.FieldTypes.Length - 1;
        if (v.Fields.Length != payload)
        {
            Report(block, index, $"newvariant {v.Variant} '{layout.Name}' takes {N(payload)} " +
                                 $"field(s), got {N(v.Fields.Length)}");
            return;
        }

        for (var i = 0; i < v.Fields.Length; i++)
        {
            var actual = TypeOf(v.Fields[i]);
            if (!IrType.Equal(layout.FieldTypes[i + 1], actual))
                Report(block, index, $"newvariant field {N(i)} is {Show(actual)}, " +
                                     $"expected {Show(layout.FieldTypes[i + 1])}");
        }

        RequireDestType(v.Dest, new IrEnumType(v.Enum), "newvariant", block, index);
    }

    private void CheckEnumTag(EnumTag t, BlockId block, int index)
    {
        if (TypeOf(t.Value) is not IrEnumType)
            Report(block, index, $"enumtag expects an enum, found {Show(TypeOf(t.Value))}");
        else
            RequireDestType(t.Dest, new IrScalarType(IrScalar.I64), "enumtag", block, index);
    }

    private void CheckEnumAs(EnumAs a, BlockId block, int index)
    {
        if (TypeOf(a.Value) is not IrEnumType source)
        {
            Report(block, index, $"enumas expects an enum, found {Show(TypeOf(a.Value))}");
            return;
        }

        if (ResolveType(a.Variant, "enumas", block, index) is null) return;

        if (VariantIndexIn(source.Type, a.Variant) < 0)
        {
            Report(block, index, $"enumas narrows to variant {a.Variant}, which does not belong to " +
                                 $"enum {source.Type}");
            return;
        }

        RequireDestType(a.Dest, new IrRefType(a.Variant), "enumas", block, index);
    }

    private void CheckTerminatorTypes(IrTerminator terminator, BlockId block)
        {
            switch (terminator)
            {
                case Return r:
                {
                    var returnsVoid = IsVoid(_fn.ReturnType);
                    if (returnsVoid && r.Value is { } unwanted)
                        ReportTerm(block, $"void function returns a value ({unwanted})");
                    else if (!returnsVoid && r.Value is null)
                        ReportTerm(block, $"function returns {Show(_fn.ReturnType)} " +
                                          "but 'ret' carries no value");
                    else if (r.Value is { } value && !IrType.Equal(_fn.ReturnType, TypeOf(value)))
                        ReportTerm(block, $"returns {value} ({Show(TypeOf(value))}), " +
                                          $"expected {Show(_fn.ReturnType)}");
                    break;
                }

                case CondBranch c:
                    if (!IsBool(TypeOf(c.Cond)))
                        ReportTerm(block, $"condition {c.Cond} is {Show(TypeOf(c.Cond))}, must be bool");
                    break;

                // Only Throwable types are throwable, which the sema checked. What remains here is the
                // shape: a value that is an object at all. Throwing a scalar would be a lowering bug,
                // not a user error.
                case Throw t when TypeOf(t.Value) is not (IrRefType or IrInterfaceType):
                    ReportTerm(block, $"throws {t.Value} ({Show(TypeOf(t.Value))}); " +
                                      "only class and interface values are throwable");
                    break;

                case Throw:
                case EndFinally:
                case Branch:
                case Unreachable:
                    break; // no type conditions

                default:
                    throw new InternalCompilationException(
                        $"ir-verifier: unhandled terminator {terminator.GetType().Name}");
            }
        }

        private void RequireDestType(TempId dest, IrType declared, string what, BlockId block, int index)
        {
            // The Type fields on the instructions are copies for the printer; the temp table is the
            // authority. That the two agree is the verifier's core job.
            var fromTable = TypeOf(dest);
            if (!IrType.Equal(declared, fromTable))
                Report(block, index, $"{what} declares type {Show(declared)} but {dest} is " +
                                     $"{Show(fromTable)} in the temp table");
        }

        // ------------------------------------------------------------------ table lookups

        private bool IsKnownTemp(TempId temp) => temp.Value >= 0 && temp.Value < _fn.Temps.Count;

        /// <summary>The type of a temp comes from the temp table, which is the authority. Call only for
        /// temps that have passed <see cref="IsKnownTemp"/>.</summary>
        private IrType TypeOf(TempId temp) => _fn.Temps[temp.Value].Type;

        /// <summary>null when the local id lies outside the table.</summary>
        private IrType? LocalTypeOf(LocalId local) =>
            local.Value >= 0 && local.Value < _fn.Locals.Count ? _fn.Locals[local.Value].Type : null;

        // ------------------------------------------------------------------ type predicates

        // "Is the type exactly this scalar?" runs through pattern matching rather than IrType.Equal:
        // the question is answerable for every type, and in CheckTables it has to give an answer for a
        // future non-scalar type too, rather than throw. IrType.Equal exists for the other problem —
        // comparing two types against each other.
        private static bool IsVoid(IrType type) => type is IrScalarType { Kind: IrScalar.Void };

        private static bool IsBool(IrType type) => type is IrScalarType { Kind: IrScalar.Bool };

        // The twin of TypeFacts.IsInteger, on IrType instead of LyrType: the verifier has to check
        // bytecode without the sema. 'Char' is included; missing here, the verifier would reject what
        // the sema allows.
        private static bool IsInteger(IrType type) => type is IrScalarType
        {
            Kind: IrScalar.I8 or IrScalar.I16 or IrScalar.I32 or IrScalar.I64
            or IrScalar.U8 or IrScalar.U16 or IrScalar.U32 or IrScalar.U64
            or IrScalar.Char
        };

        private static bool IsFloat(IrType type) =>
            type is IrScalarType { Kind: IrScalar.F32 or IrScalar.F64 };

        private static bool IsNumeric(IrType type) => IsInteger(type) || IsFloat(type);

        private static bool IsStringLike(IrType type) =>
            type is IrScalarType { Kind: IrScalar.String };

        /// <summary>What <c>eq</c> and <c>ne</c> may compare. Ordering comparisons require numeric types
        /// instead; char and string have no ordering.</summary>
        private static bool IsEquatable(IrType type) =>
            IsNumeric(type) || type is IrScalarType
            {
                Kind: IrScalar.Bool or IrScalar.Char or IrScalar.String
            };

        private static bool IsBitwiseOrShift(IrBinKind kind) => kind is
            IrBinKind.Shl or IrBinKind.Shr or
            IrBinKind.BitAnd or IrBinKind.BitOr or IrBinKind.BitXor;

        /// <summary>IntConst is two's-complement encoded and zero-extended to 64 bits, so the bit pattern
        /// is what gets checked, not the signed value range.</summary>
        private static bool FitsWidth(ulong value, IrScalar kind) => kind switch
        {
            IrScalar.I8 or IrScalar.U8 => value <= byte.MaxValue,
            IrScalar.I16 or IrScalar.U16 => value <= ushort.MaxValue,
            IrScalar.I32 or IrScalar.U32 => value <= uint.MaxValue,
            IrScalar.I64 or IrScalar.U64 => true,

            // An IntConst with char type arises from 'c + 1': the untyped literal IS a char, so it stands
            // in the bytecode as an integer constant with char type. Its bound is not that of a machine
            // word but that of Unicode.
            IrScalar.Char => value <= long.MaxValue && Core.Unicode.IsCodepoint((long)value),

            _ => false
        };

        // The code point rule lives in Lyric.Core, where sema, verifier and VM see it together.
        private static bool IsUnicodeScalarValue(int codePoint) => Core.Unicode.IsCodepoint(codePoint);

        private static bool ConstKindMatches(IrConstValue value, IrScalar kind) => value switch
        {
            IntConst => IsInteger(new IrScalarType(kind)),
            FloatConst => IsFloat(new IrScalarType(kind)),
            BoolConst => kind == IrScalar.Bool,
            CharConst => kind == IrScalar.Char,
            StringConst => kind == IrScalar.String,
            _ => throw new InternalCompilationException(
                $"ir-verifier: unhandled const {value.GetType().Name}")
        };

        // ------------------------------------------------------------------ findings and names

        private void Report(string message) => _findings.Add($"{_fn.Name}: {message}");

        private void Report(BlockId block, int index, string message) =>
            Report($"{block}: #{N(index)}: {message}");

        private void ReportTerm(BlockId block, string message) =>
            Report($"{block}: terminator: {message}");

        private void ReportAt(BlockId block, int? index, string message)
        {
            if (index is { } i) Report(block, i, message);
            else ReportTerm(block, message);
        }

        // Formatted invariantly: findings are compared by substring in tests, and a culture with
        // different digit characters would break those assertions on CI.
        private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string N(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>The type name for error messages, through <see cref="IrNames"/> and therefore in the
        /// same spelling as the printer dump — the two get read side by side.</summary>
        /// <remarks>The fallback for non-scalar types is deliberate: <c>Show</c> runs only while building
        /// finding texts, and a throw there would hide exactly the finding being reported. The loud throw
        /// sits in <see cref="IrType.Equal"/>, where it applies to the comparison itself.</remarks>
        private static string Show(IrType type) => type switch
        {
            IrScalarType s => IrNames.Scalar(s.Kind),
            IrRefType r => $"&{r.Type}",
            IrArrayType a => $"{Show(a.Element)}[]",
            IrOptionalType o => $"?{Show(o.Inner)}",
            IrEnumType e => $"enum {e.Type}",
        IrInterfaceType i => $"dyn {i.Type}",
        IrStructType v => $"val {v.Type}",
            _ => type.ToString() ?? type.GetType().Name
        };

        private static string ConstKindName(IrConstValue value) => value switch
        {
            IntConst => "integer",
            FloatConst => "float",
            BoolConst => "bool",
            CharConst => "char",
            StringConst => "string",
            _ => value.GetType().Name
        };
    }
}
