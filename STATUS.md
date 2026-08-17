# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**v1.0.0 through v1.3.0 are released** — annotated tags on the remote, each with a release page and
three archives. M0–M10 are finished and tagged (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`).

**M12, the project system, is what v1.2.0 shipped**: `lyric.json` says what a project is, `build.lyr`
says what to build, `lyric new` writes one, and the tools read all of it.

**M11, the language server, has one delivery point left: completion.** It ships diagnostics while you
type, what a name under the cursor is, where it was declared, a program followed across its files,
documentation on hover, the outline of a file, and every place a name occurs. **v1.3.0 shipped all
of that**; completion is v1.4.0 and closes the milestone.

3478 tests green **in Debug and Release**, bytecode format **3.1**, **six** binaries plus
`lyrembed.dll`, version **1.3.0**.

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

- [x] **v1.3.0 slice 4 — find all references** (2026-08-17). 3478 tests green. Merged as
  PR #21, and it completed v1.3.0.
  - `textDocument/references`, with `includeDeclaration`. The front end gained **two enumerations
    and nothing else**: `BindingResult.All` and `TypeResult.AllReferences`. The reverse index is
    built per REQUEST and kept nowhere.
  - **The measurement decided that.** A program pulling in eight standard library modules has 802
    entries in the binding table and ~3000 typed expressions. Against a 7–16 ms compile that has
    already run, a dictionary over a few thousand entries is noise — a cached index would be state
    to invalidate in exchange for microseconds. I had planned for a cache and did not build one.
  - **Both tables, because they answer for different nodes.** The resolver binds names in type
    position, the sema binds expressions; `let p: Point` stands in one and an array annotation in
    the other. Either alone gives a list that is half right.
  - **The sema's table holds declarations as well as uses** — a `BindingStmt`, a `Param`, a
    `ForInStmt` and the pattern bindings are bound to the symbol they THEMSELVES declare, for the
    definite-assignment analysis. They are told apart by `ReferenceEquals(symbol.Declaration, node)`
    — no new flag, and it is the same line `includeDeclaration` draws.
  - **An imported name has two symbols**, and every use in the importing file binds to the FIRST.
    Comparing against the target alone finds nothing; the comparison goes through the import
    binding. Found by a test, not by reading.
  - **A wrong answer that predates this slice, now fixed.** Standing on `Point` in
    `let p = Point { … }` used to JUMP TO `p`: the initializer is bound to nothing, so the walk
    outwards reached the enclosing binding. `NodeFinder.Answers` now requires the cursor to be on a
    declaration's NAME before that declaration answers — which is what slice 1's name span is for.
  - **Recording the struct initializer's type looked like a one-line fix and is not.** Adding
    `BindRef(si, ts)` broke two tests and a guide chapter: `TypeChecker` reads the SAME table at the
    member-access path to decide whether a receiver is a TYPE, so the entry turned
    `Pair<int> { a = 6 }.a` into a static member access. Reverted. **`_refs` carries more meaning
    than its name says**, and separating "what does this refer to" from "what kind of receiver is
    this" is a change of its own.
  - Two limits measured rather than intended: a field use marks the whole member access (`p.x`,
    because a use site has no name span), and the answer covers the program reachable **from this
    buffer** — a project file that imports the current one is not in that compilation.

- [x] **v1.3.0 slice 2 — an editor can show what a file declares** (2026-08-17). 3454 tests
  green. Merged as PR #20.
  - `textDocument/documentSymbol`, nested: types carry their fields, methods, variants and static
    constants as children. Both ranges come from slice 1 — the declaration's span is what the editor
    reveals, the name span what the cursor lands on — and the protocol's containment rule holds by
    construction rather than by a check.
  - **Nothing is resolved.** Not only cheap: an outline is read WHILE the file is broken, and one
    that empties on a type error goes dark exactly when it is wanted. A test compiles a program with
    a type error and still expects the full outline; it is green only as long as no binding or type
    table is consulted.
  - **Rejected: building it from `Compilation.Modules[i].Members`.** That table *is* the list of what
    a module offers, and it is the wrong source twice — it has no source order, so the outline would
    stand in hash order, and it exists only after resolution, which gives up the property above.
  - **Only the nested form.** The protocol's flat alternative is deprecated, has no children and
    needs a container name per entry; two answer shapes with different semantics is not the same as
    one with a flag. A client that does not announce `hierarchicalDocumentSymbolSupport` gets
    **null**. That is a claim about editors, not about the code: no current one is in that position.
  - **Three of the ten `SymbolKind` mappings are a choice, not a translation** — the enum is closed
    and was written for other languages. A type alias is `Class`, an `extend` block is `Namespace`,
    and a function is `Method` only inside a type body. Each carries the reason beside it.
  - **The method-versus-function distinction comes from the WALK.** The first attempt read it off
    the declaration and was wrong: a body of `;` is a native at the top level and an abstract member
    inside an interface. Only the caller knows which list it is descending. A test pins the bodiless
    top-level case.
  - `detail` stays empty, and the field is absent from the type rather than present and null.
    Filling it needs a printer for `TypeNode`, and a second one beside `TypeFacts.Display` would be
    a second answer to what a type is called.

- [x] **v1.3.0 slice 3 — documentation reaches the model, and hover shows it** (2026-08-17). 3426
  tests green. Merged as PR #19.
  - The blocks were collected, looked up and consumed by `tools/DocGen` already; the table just died
    with the `Parser` instance that built it. Hover shows it under the signature, for the file being
    edited **and** every module it reads.
  - **Keyed by node identity**, like every other side table here. The parser collects by source
    OFFSET, the only key available while there are no nodes yet — and an offset counts within one
    file, so two modules both have one at 42. A compilation-wide table keyed that way answers with
    another module's documentation. A test puts two documented declarations at the same offset.
  - **The loader boundary carries it**, rather than a sink threaded through construction.
    `LoadedModule` replaces the `(Ast, IsNative)` tuple, so a loader that produces no documentation
    does not compile. The exception is `AddModule`, where the parameter is optional: ~90 call sites
    are tests with nothing to pass. **That one place is covered by a test rather than the compiler**,
    and it is worth saying rather than claiming the seam is total.
  - `ParsedModule.Parse` is the single place that parses AND binds blocks to nodes, because the
    translation from offset to identity has to happen while the parser is in hand. Its walk uses
    `INamedDecl` as the predicate — **slice 1 already answered which nodes can carry a name, and
    that is the same set that can carry a block.**
  - **This slice found a hole in slice 1.** `GlobalBindingDecl` and `StaticBindingDecl` declare their
    name through the `BindingStmt` they WRAP, so they have no `Name` of their own and the
    assembly-driven test could not see them — while a symbol declares from the wrapper. Both
    implement `INamedDecl` now, which also makes the jump to a global land on its name instead of on
    `pub`. The limit is recorded in that test's docstring: *which nodes a symbol can declare from* is
    not a question the assembly can answer.
  - The text goes through **unchanged**. There is no doc-comment vocabulary in the grammar, so there
    is nothing to interpret, and composing a summary from a signature would be the server writing
    documentation rather than showing it.
  - A declaration without a block is answered **byte for byte** as before — pinned with `Assert.Equal`
    on the whole string, not a `DoesNotContain`. That is what makes the slice additive.
  - Noticed on the way: **`std/io/console.lyr` carries no doc comment at all.** The 166 `///` lines
    of the standard library sit in eight other files, so `println` still hovers without prose.

## Measurements

Numbers instead of opinions. Taken 2026-08-07, Release, 100 000 iterations, adjusted for a scalar
loop of the same length.

| What | Bytes per operation |
|---|---|
| Struct construction **plus** method call (`Vec2.add`) | **352 B** |
| call only (`fn step(a: float): float`) | **176 B** |
| struct construction only | **112 B** |
| scalar baseline | 9 064 B *in total* |

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

**v1.3.0 is released** — name spans (#18), hover documentation (#19), document symbols (#20) and
find references (#21). Additive throughout: no language change, no format change, no new binary.

**Next is v1.4.0: completion**, the last delivery point of M11. Nothing has been designed for it yet.

It is the first question asked at a position where the text does **not** parse — error tolerance, or
a "what is admissible here" mechanism. A front-end topic of its own size rather than a fifth
provider, which is why v1.3.0 was split off instead of the delivery list being re-cut. The next scope
check is **2026-09-06**.

**One limit stays after v1.3.0**: a generic call shows the DECLARED signature, because the
substitution is private to the type checker and a second one in the server would be a second answer
to what `T` became. Measured by a test rather than left as an intention.

**The open question to answer before E4**: the lifetime and identity of a host object across the
boundary — does the host keep it alive or the VM? That is the one place in M10 where I have no
answer yet, and it belongs asked before E4 starts.

**`gh-pages` exists on the remote**, so `docs.yml` has run against GitHub. Whether the Pages
*setting* points at that branch is not visible from here; if the site does not answer, that switch
is the thing to check.

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
- **`string < string` and `==` on user types are rejected** (`LYR-SEM0003` / `LYR-SEM0055`).
  Deliberate and temporary: operator overloading is the first topic after v1.0, and the diagnostic
  points at it. Until then an ordinary method.

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
- **`TypeResult._refs` answers two questions with one table.** It is read as "what does this node
  refer to" by the language server, as "is this receiver a TYPE" by the member-access path of the
  type checker (`TypeChecker.cs:1770`), and as "which declaration is this" by the definite-assignment
  analysis. Adding an entry is therefore not additive: recording a struct initializer's type there
  turned `Pair<int> { a = 6 }.a` into a static member access and stopped a guide chapter compiling.
  - Consequence today: **nothing knows that `Point { … }` names a type**, so it is found by neither
    find-references nor go-to-definition. Measured by a test in each.
  - Splitting the receiver-kind question out of the table would fix it. That is a change in the type
    checker, not in the server, and it belongs decided before anything else wants to add to `_refs`.
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

`Merge pull request #21 from OIL1I/feature/find-references` (`e642c20`)

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
