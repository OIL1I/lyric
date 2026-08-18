# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**v1.0.0 through v1.6.0 are released** — annotated tags on the remote, each with a release page and
three archives. M0–M10 are finished and tagged (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`).

**M13, attributes, is what v1.6.0 shipped**: a program says things about itself that a host can
read. Format 3.2, and the 7-day rule was retired with it — it was pre-v1 scope discipline, and v1.0
has shipped; from here the pace is our own.

**M12, the project system, is what v1.2.0 shipped**: `lyric.json` says what a project is, `build.lyr`
says what to build, `lyric new` writes one, and the tools read all of it.

**v1.5.0 shipped operators**: `==`, the orderings, arithmetic and `as` all resolve through the
interface a type declares — no operator syntax, no new opcode. Method overloading was considered and
rejected; the constraint mechanism is this language's overloading.

**M11, the language server, is CLOSED.** Diagnostics while you type, what a name under the cursor is,
where it was declared, a program followed across its files, documentation on hover, the outline of a
file, every place a name occurs, and completion. v1.3.0 shipped the first seven, v1.4.0 the last.

3830 tests green **in Debug and Release**, bytecode format **3.2**, **six** binaries plus
`lyrembed.dll`, version **1.6.0**.

**All three limitations v1.1.0 shipped with are closed.** The command line knows native roots, the
language server reads the project file, and editing a module refreshes the file that imports it.

**What this state can do**: the whole language of the grammar compiles and runs; a standard library
that largely carries itself (`Map`, `Set`, merge sort, all iterator adapters and the string hash are
written in Lyric); six tools including the REPL, the language server and the build runner; a VS Code
extension with live diagnostics; a project that scaffolds, declares its own layout and builds itself
with a Lyric script; and an embedding API with which a C# host loads scripts, sandboxes them, calls
functions out of them and hands its own functions and types in.

> **The file had grown to 1088 lines by 2026-08-07** and contradicted itself in three places. It has
> been cut back to its own maintenance rule: recent slices, open points, design context. Everything
> else stands in `git log`.

## Recently finished

- [x] **M15 — the boundary learns values** (2026-08-18). Four slices, same day as M14, stacked on
  its branch; details under *What we are working on*. The one measured sentence: a native call
  allocates nothing anymore — argument arrays pooled (40/88 → 0 B), `Vec2` in as fields (0 B),
  `Vec2` out through a hidden buffer that value semantics makes unobservable (0 B), layouts
  checked at load. Erato's A2, answered.

- [x] **M14 — the interpreter stops allocating** (2026-08-18). Five slices in one day, 3855 tests
  green in Debug and Release, branch `feature/m14-allocations`. No language change, no format
  change.
  - **Slice 0**: `tools/Bench`, the in-process harness — every later gate is a diff against its
    baseline. Round-robin against JIT tiering, minima, scalar loop subtracted.
  - **Slice 1**: frame pooling. Rent from a per-function free list, recycle on return and handled
    unwind, arrays cleared so nothing stays alive. Coroutines need no exception: state machines,
    no frame survives a yield. Call: 176 → 0 B.
  - **Slice 2**: the inliner. Splice via renumbering — the phi-free IR makes it cheap. Not
    inlined: handlers on either side, self calls, never-returning callees (the verifier caught
    that one as an orphaned continuation), >24 ops. Backtrace trade documented in the changelog.
  - **Slice 3**: scalar replacement. Local forwarding plus sole-ownership scalarization, classes
    and structs alike; `structcopy` becomes a field-wise init except across struct-typed fields
    (deep copy). `Vec2.add` + assignment: 352 B/271 ns → 0 B/8.4 ns. THE ORDER MATTERED: a
    returned value escapes its function but not the caller it was inlined into.
  - **Slice 4**: devirtualization. A `callvirt` whose receiver is one provable `mkiface` becomes
    the direct call, then the pipeline runs once more. A default-method slot keeps the fat
    pointer — `this` in a default method dispatches virtually; the verifier caught the wrong
    receiver before any test did.

- [x] **M13 — attributes: metadata a host can read** (2026-08-18). Four slices in one day, 3822
  tests green in Debug and Release. Merged as PR #38, released as **v1.6.0**.
  - An attribute is a STRUCT; where it may sit is the marker it declares (`OnModule`/`OnType`/
    `OnFunction`, new in `std.core`) — conformance, not the name, the same nominal rule as the
    operators. Targets: module header, top-level fn, struct/class/enum. Nothing the compiler reads:
    the set stays open exactly because every attribute is inert.
  - **Format 3.1 → 3.2**, two skippable sections. Attributes (11): complete rows — one value per
    field in field order, unwritten fields carry their literal default, so no field index is stored
    and no consumer resolves a default. Names (12): field names ONLY for types a row references.
    **Measured against the pinned 1.5.0 lyrvm**: it verifies and runs a 3.2 module; an
    attribute-free module differs in the two version bytes alone.
  - **An attributed function is a reachability root** — the row is a promise to a caller the
    analysis cannot see, same standing as the entry point. Rows follow the pruning renumbering,
    pinned by NAME because an off-by-one keeps the numbers plausible.
  - Host surface: `ModuleAttributes` on the RAW module — the module row is how a host decides
    whether to load foreign bytes, so the query must not presuppose binding. An `AttributeUse` is a
    call handle (index, not name, per frame). `FieldsOf` answers the component case. Attribute
    names are unqualified, like every type name in the bytecode; the guide says an SDK owns them.
  - The reserved expression form `@name(args)` left the grammar; `LYR-PAR0038` narrowed to
    parameters and stopped promising the future. Doc ratchet 120 → 123 of 359.

- [x] **v1.5.0 slice 4 — a cast is a conversion the type declared** (2026-08-18). 3744 tests green.
  Merged as PR #34, and it completed v1.5.0.
  - `x as T` beyond the numerics desugars through `Into<T>` from `std.core`: `x.into()`, checked and
    stored exactly as the operators are. The numeric branch stands first and is not overridable —
    `1 as float` never desugars, whatever conformances exist.
  - **Three boundaries, each deliberate and each tested.** Explicit only — an implicit conversion is
    a second, invisible mechanism beside the visible one. ONE target per type — `into` is a member
    name, a type has one member of a name, and the second conformance is a duplicate-member
    diagnostic, measured rather than asserted in prose. And total only — `Into` returns `T`, not
    `?T`; a conversion that can fail is a named function returning an optional.
  - **The orphan rule guards conversions too, and it corrected the plan.** `extend int ::
    [Into<Cents>]` is `LYR-SEM0041` — the rule looks at the extended type and the interface, not at
    the interface's type ARGUMENTS, so a local `Cents` does not rescue it. A conversion OUT of a
    builtin therefore has no home, and the planned "primitive source through a user extend" was
    wrong. Pinned as a limit with an address instead.
  - Conversions chain — `(a as B) as C` — and the cast agrees with the written `into()` in the same
    program. The operand lowers once; same seam, same guarantees.
  - Doc ratchet 118 → 120 of 356.

- [x] **v1.5.0 slice 3 — arithmetic through interfaces, and a hole it dug up first** (2026-08-18).
  3732 tests green, Debug and Release. Not merged.
  - **The slice began as a bug fix.** Planning the compound forms exposed that a compound assignment
    never checked its OPERATOR: `p += p` on a struct passed the sema — the only rule was
    right-assignable-to-left, which any value of the target's own type satisfies — and the lowering
    emitted an integer `add` over two references. The `s += "x"` bug of v1.1.0, one type over,
    invisible in Release where the verifier does not run. `s &= s` and `f <<= f` passed the same
    way. Measured first, then fixed with failing tests, own commit.
  - **The fix types a compound as the binary it carries**: a synthesized `target op value` runs
    through `CheckBinary` — the same rules as the written form, not a second copy. `??=`, `&&=` and
    `||=` keep the old path; their meaning is not "apply the operator to both sides".
  - **The fix delivered `s *= 3` and `xs *= 2`.** Their rejection was an accident of the
    assignability rule, pinned by a v1.1.0 test whose own comment predicted this moment: *"the
    moment the sema rule is corrected this test goes red and says so."* It did; it now runs the
    repetition and checks the output. The lowering had been ready since every compound was routed
    through `EmitBinary`.
  - Then the slice itself: `+ - * /` desugar through `Add<T>`, `Sub<T>`, `Mul<T>`, `Div<T>` — four
    new `std.core` interfaces, one method each, homogeneous. The built-in numerics and `string`
    (`Add` only) conform via `extend`, so a generic `total<T :: [Add<T>]>` serves an `int`, a
    `string` and a `Vec2` in one program. `%` stays numeric-only, deliberately.
  - `Vec2` is the test receiver on purpose: the type of the project's own measurements, whose
    method-call cost is exactly what the operator now costs.
  - **A compound through an interface is a diagnostic, not support**: the compound lowering
    evaluates the target's address once and cannot yet route through a call — a silent second
    evaluation of the receiver would be the wrong surprise. The message says `write it out:
    'a = a + b'`, and a test holds it.
  - Doc ratchet 110 → 118 of 354; the snapshot gained documentation only, no signature and no name
    moved.

## Measurements

Numbers instead of opinions. Since 2026-08-18 they come from `tools/Bench` — in-process, Release,
100 000 operations per case, minima over nine runs, a scalar loop of the same shape subtracted.
`dotnet run -c Release --project tools/Bench` reproduces them. The M14 baseline:

| Case | ns/op adj. | B/op adj. |
|---|---:|---:|
| call (`fn step(a: float): float`) | 49.9 | **176** |
| struct construction only | 60.6 | 56 |
| construction **plus** method call (`Vec2.add`) | 271.0 | **352** |
| the same through `a + b` (`Add<Vec2>`) | 252.3 | 352 |
| `for-in` over a range (against `while`) | 143.2 | **208** |
| `for-in` over an array (against `while`) | 153.1 | 208 |
| `Set.iter()`, the `callvirt` route (against `while`) | 420.9 | 229 |

One correction to the old numbers: the 112 B for "construction only" was a four-field shape; the
two-field `Vec2` is 56 B. And one to the harness: the interpreter loop is one shared method that
tiered compilation keeps improving while the harness runs, so the cases are measured round-robin
over three cycles, minimum per case — sequentially, the scalar baseline came out slower than the
loop doing the same work plus a call.

**After slice 1 (frame pooling):** call **176 → 0 B**; `for-in` range **208 → 0.1 B** — the
208 B were the frame trio alone; a `Some` over a scalar never allocated, disproving slice 0's
guess. **After slice 2 (inlining), adjusted ns/op:** `Vec2.add` 271 → **112**, `for-in` range
143 → **68**, array 153 → **94**; the ~7 ns residue on a bare call is the spliced
parameter/return traffic through locals.

**After slice 3 (scalar replacement), against the slice-0 baseline, adjusted:**

| Case | baseline | now |
|---|---:|---:|
| construction only | 60.6 ns / 56 B | **18.2 ns / 0 B** |
| `Vec2.add` plus assignment | 271.0 ns / 352 B | **8.4 ns / 0 B** |
| the same through `a + b` | 252.3 ns / 352 B | **6.1 ns / 0 B** |
| `for-in` over a range | 143.2 ns / 208 B | **40.5 ns / 0 B** |
| `for-in` over an array | 153.1 ns / 208 B | **109.7 ns / 0.3 B** |

The `Vec2` gate is met: expression-shaped struct code allocates NOTHING and runs ~30–40× the
baseline. The range loop is 0 B but 1.85× a `while` — the optional ops and the extra block hops
remain; honest, and material for a later peephole rather than this milestone.

**After slice 4 (devirtualization):** the `Set.iter()` loop carries no `callvirt` anymore — the
receiver's single `mkiface` proves the concrete type, and the loop direct-calls
`SetIterator<int>.next` (too big to inline; the call is pooled). The gate was structural and is
met; the time gain drowns in the probing work the loop actually does. One find on the way, caught
by the verifier: a DEFAULT-method slot takes the fat pointer, not the concrete value — `this` in
a default method dispatches virtually.

**The VM is allocation-free at its core** — a loop with floating-point arithmetic allocates nothing
worth mentioning over 100 000 passes. Everything above that is calls and objects.

**Half the bytes have nothing to do with structs**: `Frame.For` allocates three objects per call
(frame, slots, stack). That fixes the order for a later optimization — **frame pooling, then
inlining, then scalar replacement**, not the other way round: the value built in `add` **escapes**
(it is returned), so escape analysis without prior inlining finds nothing. **None of it is built in
v1.**

Within the frame budget: 1000 entities × 10 vector operations × 60 fps ≈ 211 MB/s, roughly one gen0
collection per frame. Gen0 is short — **no reason to move vector mathematics behind natives.**

Measured further: `for-in` over a range costs **1.28×** against a `while` loop. The verifier is
**~50 %** of the lowering time in Debug, not ~90 % — the old claim never had a source. A Release
profile is still outstanding.

**Compiler latency, in-process** (2026-08-14, Release, 15 runs). A full `Check`, standard library
included:

| What | median | min |
|---|---|---|
| 6 lines | 14.3 ms | 7.1 ms |
| ~85 lines with a standard library import | 16.0 ms | 9.2 ms |

The same work through `lyrc check` measures **181 ms** and **212 ms**, against **40 ms** for
`--version` alone. The difference is process start and JIT warm-up: a long-lived process pays it
once, a batch invocation pays it every time. **Measure in the process that will do the work.** The
batch number is an upper bound, and here it was off by a factor of ten in the direction that would
have bought an incremental compiler nobody needs.

## What we are working on

**v1.5.0 is released** — equality (#31), ordering (#32), arithmetic plus the compound-assign fix
(#33) and conversion (#34). One mechanism throughout: the interface the type declares.

**v1.6.0 is released** — M13, attributes (#36, PR #38), all four slices in one day. Erato re-pins
at its own pace: a 1.5.0 runtime loads 3.2 modules, so nothing forces the update.

**M14 is finished** (decided 2026-08-18 ahead of the scope check as a deliberate reaction to the
Erato findings, done the same day — all five slices, slice 4 included rather than cut). The
changelog carries a v1.7.0 entry as *unreleased*; merging the PR and tagging is the maintainer's
call. What M14 deliberately did NOT do: value structs as a language feature (a `struct` already
has value semantics; the representation now keeps the promise), a JIT, and the native boundary.

**M15 — the boundary learns values — is finished** (2026-08-18, `feature/m15-boundary`, stacked
on the M14 branch). All four slices; the v1.7.0 changelog entry covers both milestones and stays
*unreleased* until the maintainer merges and tags.

- **Slice 0**: boundary probes in `tools/Bench`, through the RAW registry path a game host uses
  — the embedding layer's per-call boxing would have buried the figure. Baseline: 40 B per
  one-argument crossing, 88 B per four-argument one.
- **Slice 1**: the argument buffers are pooled (per arity, stack-shaped for reentrancy, cleared
  on recycle, abandoned on throw). The array is a documented LOAN to the implementation. 40/88
  → **0 B**. This is the fix for the GC tail Erato's SoA measurement exposed.
- **Slice 2**: struct parameters flatten — the .lyr declaration is the typed façade, the wire
  and the binder see scalars, the call site emits field loads. Flattening removes the escape,
  so the M14 scalarizer dissolves the operand: a fresh `Vec2` per call measures **0 B**.
- **Slice 3**: struct returns through a hidden out-buffer — one module-owned instance per
  import, passed as trailing wire argument (`0x45` was always legal in import rows; spec now
  says so), host fills fields in order, call site copies out. VALUE SEMANTICS is the safety
  argument: any binding copies, so the shared buffer is unobservable — pinned by the
  two-calls-one-buffer and escaping-result tests. `positionOf(e): Vec2` measures **0 B**.
  `RegisterStructReturning` checks the layout at load.
- What M15 deliberately did not do: embedding-layer delegate sugar for struct returns,
  cross-module structs in native signatures, and structs with reference fields — each a
  boundary-rule decision, recorded in the changelog.

**Erato's A2 is answered in its useful direction** — the host declares the value types in its
SDK, the script uses them, nothing allocates. What remains on the register's list for Lyric is
A4 (an opaque `Entity`) and the E4-side adoption. The other open points — heterogeneous
arithmetic, compound assignment through the interfaces, the static-extension asymmetry,
project-wide references, the first compiler-read attribute, the `for-in` peephole — stay
material for the **2026-09-06** scope check.

**One limit stays**: a generic call shows the DECLARED signature, because the
substitution is private to the type checker and a second one in the server would be a second answer
to what `T` became. Measured by a test rather than left as an intention.

**The open question to answer before E4**: the lifetime and identity of a host object across the
boundary — does the host keep it alive or the VM? That is the one place in M10 where I have no
answer yet, and it belongs asked before E4 starts.

## Still open

**Language gaps still open:**

- **A block lambda does not deliver its return type to the inference**: `(n: int) => n` binds `U`,
  `(n: int) => { return n; }` does not. *Not a gap but a documented limit* — `LYR-SEM0046` says so
  and suggests the annotation, and that works. It stands here because I wrongly reported it as a bug
  on 2026-08-08.

- **There is no interface inheritance** (`interface A :: [B]` is `LYR-PAR0039` with a message that
  names the way out). Noticed while building the constraint rules, which presupposed it. Whether it
  is worth having is open — `Hashable` would need it only to imply `Equatable`. No program is
  unwritable without it: `std.core` requires both side by side.

**Tooling and format:**

- **A `v1.0.1` runtime cannot read a module with a source map.** The skip that lets a reader step
  over a section it does not know was broken until 3.1, so the forward compatibility the format
  promises does not hold for the one release before it. `--no-source-map` produces a module those
  runtimes accept. Nothing can be done on their side; it is recorded so the next format addition is
  not mistaken for the same bug.
- **A module without `main` keeps the whole well-known standard library.** Measured 2026-08-15: a
  library module exporting one `int` function compiles to **7886 bytes and ~54 functions** from
  `std.string`, `std.core`, `std.iter`, `std.fmt` and `std.collections`, none of which it uses. The
  same file with a `main` that uses `println` is **315 bytes and one function**.
  - Not a bug in what the reachability analysis does — it trims from the ENTRY POINT, and a library
    has none, so nothing is unreachable. `WellKnownModules` loads those five unconditionally because
    the f-string lowering calls into them.
  - The roots for a library would sensibly be its `pub` declarations. **Whether that is the right
    rule is a decision, not a measurement**: it would make a library's surface decide its contents,
    and a host calling an unexported function through the embedding API would then find it missing.
  - It is the point at which a binary library would carry half a standard library with it, so it
    belongs answered before that is ever a goal.
- **A STATIC extension method is callable through an INSTANCE.** `p.make()` compiles when `make` is
  a `static fn` in an `extend` block, because the lookup's instance path falls through to the
  extension without checking — while the type path rejects a non-static extension explicitly with
  `LYR-SEM0055`. The asymmetry reads as an oversight rather than a rule; completion does not offer
  it. Measured by a test that records the current direction, so changing it starts there.
- **`TypeResult._refs` still holds declarations beside uses**, because the definite-assignment
  analysis binds a `BindingStmt`, a `Param`, a `ForInStmt` and the pattern bindings to the symbol
  they themselves declare. Splitting the two apart has **no consumer**: the lowering matches on
  symbol kind, the server compares `symbol.Declaration` against the node. Recorded so the next reader
  of that table knows what is in it, not as work waiting to be done.
  - *The receiver-kind question is out of it since v1.4.0 slice 1*, which is what made the table safe
    to add to.
- **Section byte sizes are missing from `lyrvm info`**: the reader discards them after parsing.
  Retrofitting them would mean extending the model with provenance data — a decision of its own.
- **Measure the verifier share in a Release profile** — the Debug numbers are riddled with JIT
  warm-up and serve only as an order of magnitude.

## Design decisions (context)

- AST = immutable records; symbols = mutable classes; binding and types through side tables
  (Roslyn style).
- Builtins as the root scope; two-pass declaration; structured flow analysis (no CFG).
- Type system rules in `docs/Grammar.md`; **`ErrorType` means exclusively "already reported here"** —
  not "unknown". Checked mechanically.
- Generics: monomorphization. The only option that fits this VM — C# reifies and needs a JIT, Java
  erases and pays with boxing; both presuppose that the runtime knows types, and a Lyric value
  carries no type tag.
- **A value carries no type tag.** Every opcode carries its tag in the instruction stream, and the
  dispatch stays static. From that follows the fat-pointer pattern shared by interfaces, closures and
  coroutines: a reference plus a word in `LyrValue`.
- **IR**: the type fields on the instructions are copies for the printer, the temp table is the
  authority — that the two agree is the core job of the verifier.
- **Total functions over today's type universe throw in the `default`** rather than returning a
  substitute value (`IrType.Equal`, `IrNames.*`, `TypeLowering.Lower`, `IrPrinter.TypeStr`,
  `IrBinKind.FromAst`). The throw names the place to follow up when extending. The exception is
  `IrVerifier.Show` — there a throw would hide the finding. *(A `default` that silently does nothing
  has already desynchronised the instruction stream once: `CodeDecoder.SkipType`.)*
- **`IrShape` is the single source for operands, dest and successors**, **`IrNames` the single one
  for scalar names and mnemonics.** Two copies of those switch blocks would be silently wrong code.
- **Lowering**: statements return "does the control flow fall through?"; values crossing block
  boundaries travel through (possibly synthetic) locals, never through temps — **which is exactly why
  this IR needs no phi**. Block density and `Entry == bb0` are structurally guaranteed in the
  `BlockBuilder` rather than checked.
- **Two error classes in the lowering**: valid Lyric the backend state cannot do → `LYR-IR0001` with
  a position; an internal inconsistency → `InternalCompilationException`. **Deliberately exactly one
  IR code** — codes are stable identifiers, the gaps are temporary. `LYR-IR0002..0010` stay free.
  Likewise: a retired number (`LYR-CLI0007`) is **never** issued again.
- **A `FileId` is an index into ONE `SourceManager`, and a `Span` carries one.** That couples every
  span to the manager it was made in, which is why the compiler builds a fresh manager per run and
  why parsed ASTs cannot be shared between runs however immutable they are. Anything long-lived that
  wants to cache across compiles has to cache the manager with them.
- **Line endings are a test contract, not a taste**: `.gitattributes` forces `eol=lf` in the working
  tree as well, because the goldens compare span offsets. **Do not remove it** — without it 14 golden
  tests fail in every fresh clone and the `windows-latest` job breaks.
- **Working mode** (scope check 2026-08-02, still in force): Claude plans *and* implements, the
  maintainer reviews — a deliberate deviation from `CLAUDE.md` §Collaboration, where the plan comes
  from Claude and the code from the user. **`CLAUDE.md` names this entry as the one that overrides
  it**, so the deviation lives in one place and is lifted by deleting this bullet. What to watch is
  whether the understanding of the code keeps up with its size. The changelog starts at `v1.0.0`;
  before it the annotated tag message is the release note.
- **At the end of every milestone the delivery list is to be ticked off point by point, not the exit
  criterion alone.** M5 and M6 each silently failed to deliver part of their items; the gap disguised
  itself as a clean diagnostic. For the same reason **six** gates were re-cut in M7, because they
  required language features of later slices.

## Last relevant commit

`Merge pull request #38 from OIL1I/feature/attributes`

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
