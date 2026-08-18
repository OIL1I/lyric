using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// Dissolves objects that never leave their frame into one local per field, and forwards locals
/// that are stored exactly once. The two passes feed each other and run to a small fixpoint:
/// forwarding exposes an allocation's field traffic, scalarizing one object turns the copy taken
/// from it into pure local traffic, which forwarding then folds again.
///
/// <para>WHY AFTER INLINING, AND ONLY THERE USEFUL: a value a method builds and returns escapes
/// that method — but not the caller it was inlined into. This is HotSpot's order (escape analysis
/// behind the inliner), for HotSpot's reason.</para>
///
/// <para>WHAT MAKES IT SOUND HERE. Locals are frame-private — no call, no other frame and no
/// object can read them — so the whole analysis is a walk over one function's instructions. A
/// temp is defined exactly once, so every derived pointer has exactly one source. And an object
/// is only ever scalarized while provably in SOLE OWNERSHIP: every store into its local is a
/// private allocation, every read from it is a field access, and anything else — a call
/// argument, a return, a store into another object or a second local, an interface value, a
/// throw — disqualifies the group. Under sole ownership, one set of field locals per LOCAL is
/// reference semantics and value semantics at once, which is why classes (the iterators) and
/// structs (`Vec2`) take the same path.</para>
///
/// <para>A <c>structcopy</c> is treated as an allocation whose init copies every field from its
/// source — but only for types without struct-typed fields, because the runtime copy is deep and
/// the field-wise one is not.</para>
///
/// <para>Functions with handlers are skipped whole, as in the inliner: a catch slot is a local
/// written by the unwinder, which is exactly the kind of invisible writer this analysis assumes
/// away.</para>
/// </summary>
internal static class ScalarReplacement
{
    private const int MaxRounds = 4;

    public static void Run(IrModule module)
    {
        foreach (var function in module.Functions)
        {
            if (function.Handlers.Count > 0) continue;

            var any = false;
            for (var round = 0; round < MaxRounds; round++)
            {
                var changed = ForwardLocals(function);
                changed |= Scalarize(function, module);
                any |= changed;
                if (!changed) break;
            }

            // Deleting a load deletes its dest's definition, and the temp table is dense with
            // every entry defined — the verifier holds both. Renumber once at the end.
            if (any) CompactTemps(function);
        }
    }

    /// <summary>Rebuilds the temp table from the definitions that remain and renumbers every
    /// reference, so the two invariants the verifier checks — density, defined exactly once —
    /// survive the deletions above.</summary>
    private static void CompactTemps(IrFunction function)
    {
        var map = new Dictionary<int, TempId>();
        var kept = new List<IrTemp>();

        foreach (var block in function.Blocks)
            foreach (var op in block.Insts)
                if (IrShape.DestOf(op) is { } dest && !map.ContainsKey(dest.Value))
                {
                    var renamed = new TempId(kept.Count);
                    map[dest.Value] = renamed;
                    kept.Add(new IrTemp(renamed, function.Temps[dest.Value].Type));
                }

        TempId Map(TempId temp) => map[temp.Value];

        foreach (var block in function.Blocks)
        {
            for (var i = 0; i < block.Insts.Count; i++)
                block.Insts[i] = IrShape.Rewrite(block.Insts[i], Map, id => id);
            if (block.Terminator is { } terminator)
                block.Terminator = IrShape.Rewrite(terminator, Map, id => id);
        }

        function.Temps.Clear();
        function.Temps.AddRange(kept);
    }

    // ------------------------------------------------------------------ local forwarding

    /// <summary>
    /// A local with exactly one store is a name for the stored temp: every load's dest becomes
    /// that temp, loads and store disappear.
    ///
    /// <para>Soundness leans on definite assignment: with a single store, every load the program
    /// can reach is dominated by it — a path around the store would read an unassigned local,
    /// which the sema rejects for written code and the lowering never builds for its synthetic
    /// locals. The verifier re-checks the substituted uses in Debug, so a violation of this
    /// reasoning is a loud test failure rather than a wrong value.</para>
    ///
    /// <para>Parameters are excluded — their store is the calling convention, invisible
    /// here.</para>
    /// </summary>
    private static bool ForwardLocals(IrFunction function)
    {
        var storeCount = new Dictionary<int, int>();
        var storedValue = new Dictionary<int, TempId>();
        foreach (var block in function.Blocks)
            foreach (var op in block.Insts)
                if (op is StoreLocal s)
                {
                    storeCount[s.Local.Value] = storeCount.GetValueOrDefault(s.Local.Value) + 1;
                    storedValue[s.Local.Value] = s.Value;
                }

        var forwardable = new HashSet<int>();
        foreach (var (local, count) in storeCount)
            if (count == 1 && local >= function.ParamCount)
                forwardable.Add(local);
        if (forwardable.Count == 0) return false;

        // Load dest -> the temp the local holds. Chains (a load feeding another forwarded local)
        // resolve below; a cycle cannot pass definite assignment, but the guard costs nothing.
        var substitution = new Dictionary<int, TempId>();
        foreach (var block in function.Blocks)
            foreach (var op in block.Insts)
                if (op is LoadLocal l && forwardable.Contains(l.Local.Value))
                    substitution[l.Dest.Value] = storedValue[l.Local.Value];

        TempId Resolve(TempId temp)
        {
            var seen = 0;
            while (substitution.TryGetValue(temp.Value, out var next))
            {
                temp = next;
                if (++seen > substitution.Count)
                    throw new InternalCompilationException(
                        $"ir: forwarding cycle through t{temp.Value} in '{function.Name}'");
            }
            return temp;
        }

        foreach (var block in function.Blocks)
        {
            for (var i = block.Insts.Count - 1; i >= 0; i--)
            {
                switch (block.Insts[i])
                {
                    case LoadLocal l when forwardable.Contains(l.Local.Value):
                    case StoreLocal s when forwardable.Contains(s.Local.Value):
                        block.Insts.RemoveAt(i);
                        break;

                    case var op:
                        block.Insts[i] = IrShape.Rewrite(op, Resolve, id => id);
                        break;
                }
            }

            if (block.Terminator is { } terminator)
                block.Terminator = IrShape.Rewrite(terminator, Resolve, id => id);
        }

        return true;
    }

    // ------------------------------------------------------------------ scalarization

    /// <summary>How one instruction touches one object-holding temp.</summary>
    private enum Touch
    {
        FieldRead, FieldWrite, Commit, CopySource, Escape,
    }

    private sealed record TempUse(Touch Touch, IrInst Op);

    private sealed class Allocation
    {
        public required IrOp Def;              // NewObject or StructCopy
        public required TempId Temp;
        public required TypeId Type;
        public StoreLocal? Commit;             // at most one; null for a bare temp
        public bool Viable = true;
    }

    private static bool Scalarize(IrFunction function, IrModule module)
    {
        // ---- gather: every temp's uses, every allocation, every local's stores and loads.
        var uses = new Dictionary<int, List<TempUse>>();
        void Note(TempId temp, Touch touch, IrInst op) =>
            (uses.TryGetValue(temp.Value, out var list)
                ? list
                : uses[temp.Value] = new List<TempUse>()).Add(new TempUse(touch, op));

        var allocations = new Dictionary<int, Allocation>();
        var localStores = new Dictionary<int, List<StoreLocal>>();
        var localLoads = new Dictionary<int, List<LoadLocal>>();

        foreach (var block in function.Blocks)
        {
            foreach (var op in block.Insts)
            {
                switch (op)
                {
                    case NewObject n:
                        allocations[n.Dest.Value] = new Allocation
                        {
                            Def = n, Temp = n.Dest, Type = n.Type,
                        };
                        continue;

                    // A copy is an allocation whose init reads the source — but the runtime copy
                    // is DEEP across struct-typed fields, and the field-wise one below is not.
                    case StructCopy c:
                        Note(c.Value, Touch.CopySource, c);
                        if (!module.Types[c.Type.Value].FieldTypes
                                .Any(t => t is IrStructType))
                            allocations[c.Dest.Value] = new Allocation
                            {
                                Def = c, Temp = c.Dest, Type = c.Type,
                            };
                        continue;

                    case LoadField f:
                        Note(f.Object, Touch.FieldRead, f);
                        continue;

                    case StoreField f:
                        Note(f.Object, Touch.FieldWrite, f);
                        Note(f.Value, Touch.Escape, f); // stored INTO something: it travels
                        continue;

                    case StoreLocal s:
                        Note(s.Value, Touch.Commit, s);
                        (localStores.TryGetValue(s.Local.Value, out var stores)
                            ? stores
                            : localStores[s.Local.Value] = new List<StoreLocal>()).Add(s);
                        continue;

                    case LoadLocal l:
                        (localLoads.TryGetValue(l.Local.Value, out var loads)
                            ? loads
                            : localLoads[l.Local.Value] = new List<LoadLocal>()).Add(l);
                        continue;

                    default:
                        foreach (var operand in IrShape.OperandsOf(op))
                            Note(operand, Touch.Escape, op);
                        continue;
                }
            }

            if (block.Terminator is { } terminator)
                foreach (var operand in IrShape.OperandsOf(terminator))
                    Note(operand, Touch.Escape, terminator);
        }

        // ---- an allocation is viable while its uses are field traffic plus at most one commit.
        foreach (var allocation in allocations.Values)
        {
            foreach (var use in uses.GetValueOrDefault(allocation.Temp.Value) ?? [])
                switch (use.Touch)
                {
                    case Touch.FieldRead or Touch.FieldWrite:
                        break;
                    case Touch.Commit when allocation.Commit is null:
                        allocation.Commit = (StoreLocal)use.Op;
                        break;
                    default:
                        allocation.Viable = false;
                        break;
                }

            // A bare NewObject must cover every field right where it stands — the lowering emits
            // exactly that — or a read of an unwritten string field would see a null reference
            // where the runtime zero-fills to "".
            if (allocation is { Viable: true, Def: NewObject n }
                && !InitCoversAllFields(function, n, module))
                allocation.Viable = false;
        }

        // ---- a local is a group when everything flowing in is a viable allocation of one type
        // and everything flowing out is field traffic. Conditions knock each other out, so
        // iterate to a fixpoint: a copy that stays heap needs its source as a real object.
        var groups = new Dictionary<int, TypeId>();
        bool derivedOk;
        do
        {
            derivedOk = true;
            groups.Clear();

            foreach (var (local, stores) in localStores)
            {
                if (local < function.ParamCount) continue;

                TypeId? type = null;
                var viable = true;
                foreach (var store in stores)
                {
                    if (allocations.TryGetValue(store.Value.Value, out var allocation)
                        && allocation.Viable
                        && ReferenceEquals(allocation.Commit, store)
                        && (type is null || type.Value.Value == allocation.Type.Value)
                        && CommitPathIsClean(function, allocation, local))
                        type = allocation.Type;
                    else { viable = false; break; }
                }
                if (!viable || type is null) continue;

                foreach (var load in localLoads.GetValueOrDefault(local) ?? [])
                    foreach (var use in uses.GetValueOrDefault(load.Dest.Value) ?? [])
                        if (use.Touch is not (Touch.FieldRead or Touch.FieldWrite)
                            && !(use.Touch is Touch.CopySource
                                 && allocations.TryGetValue(((StructCopy)use.Op).Dest.Value,
                                     out var copy) && copy.Viable))
                            viable = false;

                if (viable) groups[local] = type.Value;
            }

            // A committed allocation whose local formed no group is not scalarizable after all;
            // taking it out can strip another group of a viable copy, hence the loop.
            foreach (var allocation in allocations.Values)
                if (allocation is { Viable: true, Commit: { } commit }
                    && !groups.ContainsKey(commit.Local.Value))
                {
                    allocation.Viable = false;
                    derivedOk = false;
                }

            // A copy whose SOURCE is a group load is fine — its init reads the group's locals.
            // A copy whose source stays a heap object is fine too. What does not work is a viable
            // copy of a source that a group DELETES without serving it: that cannot happen,
            // because a group only forms when every copy taken from it is itself viable — checked
            // above — so nothing more to do here.
        } while (!derivedOk);

        var bare = allocations.Values
            .Where(a => a is { Viable: true, Commit: null })
            .ToList();
        if (groups.Count == 0 && bare.Count == 0) return false;

        // ---- rewrite. Field locals per group local and per bare temp.
        var fieldLocals = new Dictionary<int, LocalId[]>();   // keyed by group LOCAL id
        var bareLocals = new Dictionary<int, LocalId[]>();    // keyed by allocation TEMP id

        LocalId[] Declare(string name, TypeId type)
        {
            var def = module.Types[type.Value];
            var ids = new LocalId[def.FieldTypes.Length];
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = new LocalId(function.Locals.Count);
                function.Locals.Add(new IrLocal(ids[i], $"{name}.{def.FieldNames[i]}",
                    def.FieldTypes[i]));
            }
            return ids;
        }

        foreach (var (local, type) in groups)
            fieldLocals[local] = Declare(function.Locals[local].Name, type);
        foreach (var allocation in bare)
            bareLocals[allocation.Temp.Value] = Declare($"t{allocation.Temp.Value}",
                allocation.Type);

        // Which field-local array an OBJECT temp stands for: a group load or the bare temp.
        var standsFor = new Dictionary<int, (LocalId[] Fields, TypeId Type)>();
        foreach (var (local, type) in groups)
        {
            foreach (var load in localLoads.GetValueOrDefault(local) ?? [])
                standsFor[load.Dest.Value] = (fieldLocals[local], type);
            foreach (var store in localStores[local])
                standsFor[store.Value.Value] = (fieldLocals[local], type);
        }
        foreach (var allocation in bare)
            standsFor[allocation.Temp.Value] = (bareLocals[allocation.Temp.Value],
                allocation.Type);

        foreach (var block in function.Blocks)
        {
            var insts = block.Insts;
            for (var i = insts.Count - 1; i >= 0; i--)
            {
                switch (insts[i])
                {
                    // The allocation itself vanishes; its init stores below write the locals.
                    case NewObject n when standsFor.ContainsKey(n.Dest.Value):
                        insts.RemoveAt(i);
                        break;

                    // A scalarized copy: read the source field-wise at this very position — from
                    // the source's locals when it is scalarized too, from the object otherwise.
                    case StructCopy c when standsFor.TryGetValue(c.Dest.Value, out var copy):
                    {
                        insts.RemoveAt(i);
                        var def = module.Types[copy.Type.Value];
                        var at = i;
                        for (var f = 0; f < copy.Fields.Length; f++)
                        {
                            var temp = new TempId(function.Temps.Count);
                            function.Temps.Add(new IrTemp(temp, def.FieldTypes[f]));

                            IrOp read = standsFor.TryGetValue(c.Value.Value, out var source)
                                ? new LoadLocal(temp, source.Fields[f], def.FieldTypes[f], c.Span)
                                : new LoadField(temp, c.Value, copy.Type, new FieldId(f),
                                    def.FieldTypes[f], c.Span);
                            insts.Insert(at++, read);
                            insts.Insert(at++, new StoreLocal(copy.Fields[f], temp, c.Span));
                        }
                        break;
                    }

                    case LoadField f when standsFor.TryGetValue(f.Object.Value, out var it):
                        insts[i] = new LoadLocal(f.Dest, it.Fields[f.Field.Value], f.FieldType,
                            f.Span);
                        break;

                    case StoreField f when standsFor.TryGetValue(f.Object.Value, out var it):
                        insts[i] = new StoreLocal(it.Fields[f.Field.Value], f.Value, f.Span);
                        break;

                    // The commit and the group loads carry nothing anymore: the locals ARE the
                    // object now.
                    case StoreLocal s when groups.ContainsKey(s.Local.Value):
                    case LoadLocal l when groups.ContainsKey(l.Local.Value):
                        insts.RemoveAt(i);
                        break;
                }
            }
        }

        return true;
    }

    /// <summary>The lowering's construction pattern: every field of the type is stored, starting
    /// directly behind the <c>newobj</c>, before anything else touches the program.</summary>
    private static bool InitCoversAllFields(IrFunction function, NewObject alloc, IrModule module)
    {
        var needed = module.Types[alloc.Type.Value].FieldTypes.Length;
        if (needed == 0) return true;

        foreach (var block in function.Blocks)
        {
            var at = block.Insts.IndexOf(alloc);
            if (at < 0) continue;

            var seen = new HashSet<int>();
            for (var i = at + 1; i < block.Insts.Count && seen.Count < needed; i++)
            {
                if (block.Insts[i] is StoreField f && f.Object == alloc.Dest)
                    seen.Add(f.Field.Value);
                else
                    break;
            }
            return seen.Count == needed;
        }

        return false;
    }

    /// <summary>
    /// From the allocation's init to its commit, nothing may read the target local's OLD object:
    /// with shared field locals the init writes early, and a read in between would see the new
    /// values under the old name. The walk is linear — within the block, then across
    /// single-predecessor branches, which is the shape the inliner's continuation has.
    /// </summary>
    private static bool CommitPathIsClean(IrFunction function, Allocation allocation, int local)
    {
        var commit = allocation.Commit!;

        // Temps that currently hold the local's old object: its loads.
        var derived = new HashSet<int>();
        foreach (var block in function.Blocks)
            foreach (var op in block.Insts)
                if (op is LoadLocal l && l.Local.Value == local)
                    derived.Add(l.Dest.Value);

        var preds = new int[function.Blocks.Count];
        foreach (var block in function.Blocks)
            if (block.Terminator is { } t)
                foreach (var successor in IrShape.SuccessorsOf(t))
                    preds[successor.Value]++;

        var current = function.Blocks.First(b => b.Insts.Contains(allocation.Def));
        var index = current.Insts.IndexOf(allocation.Def) + 1;

        for (var hops = 0; hops < 8; hops++)
        {
            for (; index < current.Insts.Count; index++)
            {
                var op = current.Insts[index];
                if (ReferenceEquals(op, commit)) return true;

                if (op is LoadLocal load && load.Local.Value == local) return false;
                if (op is StoreLocal store && store.Local.Value == local) return false;

                foreach (var operand in IrShape.OperandsOf(op))
                    if (derived.Contains(operand.Value))
                        return false;
            }

            if (current.Terminator is Branch b && preds[b.Target.Value] == 1)
            {
                current = function.Blocks[b.Target.Value];
                index = 0;
                continue;
            }

            return false;
        }

        return false;
    }
}
