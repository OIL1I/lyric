# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**v1.0.0 through v1.4.0 are released** — annotated tags on the remote, each with a release page and
three archives. M0–M10 are finished and tagged (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`).

**M12, the project system, is what v1.2.0 shipped**: `lyric.json` says what a project is, `build.lyr`
says what to build, `lyric new` writes one, and the tools read all of it.

**M11, the language server, is CLOSED.** Diagnostics while you type, what a name under the cursor is,
where it was declared, a program followed across its files, documentation on hover, the outline of a
file, every place a name occurs, and completion. v1.3.0 shipped the first seven, v1.4.0 the last.

3684 tests green **in Debug and Release**, bytecode format **3.1**, **six** binaries plus
`lyrembed.dll`, version **1.4.0**.

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

- [x] **v1.5.0 slice 1 — `==` is `equals`, written as mathematics** (2026-08-17). 3704 tests green,
  Debug and Release. Not merged.
  - `==` and `!=` work on every type conforming to `Equatable<T>`. **No new syntax, no new opcode,
    no format change**: the checker builds the call `a.equals(b)` from synthetic nodes, checks it
    through the ordinary member path, and stores it in `TypeResult`; the lowering emits the stored
    call instead of a `BinOp`, and `!=` negates the result.
  - **The synthetic-call route is what made it a day instead of a week.** The member path already
    settles every receiver shape — plain struct, enum, conformance via `extend`, generic instance,
    constrained type parameter — and the desugar inherits all of them. All five have end-to-end
    tests, and the constraint case monomorphizes: the same generic function serves a struct and an
    `int`, the `int` through the stdlib's own `extend`.
  - **Conformance is required, not the method alone** — the pinned design decision. A type with an
    `equals` nobody declared as `Equatable` stays rejected; otherwise any method of that name would
    silently become an operator. Nominal, as rustc's `PartialEq`, not structural.
  - The synthetic nodes reuse the REAL operand nodes, which is what makes each operand lower exactly
    once — held by a test that counts closure calls. They hang in no tree, so syntax walks never
    meet them; their span is the operator expression, so everything reported or mapped lands on what
    the user wrote.
  - The precedent was already in the lowering: `+` on `string` is a call to `std.string.concat`.
    This generalizes "operator becomes call" from two hard-wired stdlib helpers to the interface
    the type declares.
  - `LYR-SEM0059` keeps its code and now names the fix: *declare the type with
    `:: [Equatable<Point>]`*. Optionals keep their own rule and their own message — `?T` compares
    against `null`, the backend has no optional equality, and the desugar does not unwrap.
  - Without a standard library there is no `Equatable` to desugar through; the compile degrades into
    the ordinary diagnostic rather than a crash. Pinned by a test that checks without a module
    loader.

- [x] **v1.4.0 slice 4 — the standard library says what it does** (2026-08-17). 3684 tests green.
  Merged as PR #27, and it completed v1.4.0.
  - `std/io/console.lyr`, `std/core.lyr` and `std/option.lyr` had **33 `pub` declarations and not one
    line of documentation** between them, so `println` — the most used function in the language —
    hovered empty. It no longer does.
  - **The explanations largely existed and reached nothing.** All three files carried good `//`
    comments; a `//` is not a doc comment, so hover, completion and the generated reference saw
    none of it. The work was mostly deciding which half is which: **what a caller needs became
    `///`, what a maintainer needs stayed `//`.** The reachability note in `console.lyr` and the
    reason `float` has no `Hashable` are not answers to "what does this do".
  - Interface members are documented too — `show`, `equals`, `hash`, `compare`. They are what
    completion offers on a constrained type, and a list of four bare names says nothing.
  - **The repository already held a test for this debt**, and it was better than the one I would have
    written: `DocCoverageTests` is a RATCHET on the documented share, and a second test named the
    three bare modules outright. Documented items went **70 → 110 of 346**; the floor is raised and
    the module list is now empty, kept as an assertion rather than deleted so a new module without a
    line shows up at once.
  - The generated reference changed in documentation and line numbers only — **no signature and no
    name moved**, checked against the snapshot diff rather than assumed.

- [x] **v1.4.0 slice 3 — completion for names in scope** (2026-08-17). 3684 tests green. Merged as
  PR #26.
  - **No new front-end table.** The sema builds its scope chains while checking and drops them, so
    there is none to ask at a position — but the shape of a scope is the shape of the tree, and the
    symbol behind each declaration is already in the reference table. Slice 4 of v1.3.0 called those
    entries *declarations hiding in the reference table* and had to filter them out; here they are
    exactly the answer.
  - **The context rule is the load-bearing part.** Being inside a member expression is not enough:
    in `foo.bar` the cursor on `foo` is inside one too, and what belongs there are the names in
    scope. Member context is the marker sitting PAST the target, which is where the dot is.
  - Before this slice that case answered **null by accident** — the marker corrupts the receiver's
    name, so it resolved to nothing and the member list came back empty. A test now says which
    context each side of the dot is.
  - Shadowing falls out of the walk order: inner to outer, first name wins, which is what a scope
    chain does. A binding is visible from the END of its statement, so `let x = x;` does not offer
    `x` to itself.
  - **The honest cost, stated where it is paid**: the scoping rules are now known in two places, the
    sema while it checks and this walk. Keeping the sema's chains alive would be the better answer
    as soon as there is a second consumer. Today there is one.
  - `NodeFinder.DeclaredSymbol` moved out of `ReferenceProvider`, which is where both callers now
    read it rather than each keeping a copy.

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

**v1.4.0 is released** — the receiver question out of the reference table (#24), completion after a
dot (#25), completion for names in scope (#26), and the standard library documenting itself (#27).
With it **M11 is closed**, and v1.3.0 before it shipped name spans, hover documentation, document
symbols and find-references.

**v1.5.0 — operators on your types**, decided 2026-08-17. Everything desugars through `std.core`
interfaces; no new syntax, no new opcode, the format stays 3.1. Method overloading was considered
and REJECTED — constraints plus generics are this language's overloading, and the stdlib says so in
`console.lyr`; interface inheritance stays out; implicit conversions stay out for good.

| Slice | What | State |
|---|---|---|
| 1 | `==`/`!=` via `Equatable<T>` | **done, unmerged** |
| 2 | `<` `<=` `>` `>=` via `Ordered<T>` — closes `string < string` | next |
| 3 | `+ - * /` via new `std.core` interfaces, homogeneous (`T op T -> T`) | |
| 4 | `as` to user types via an `Into`-style interface, explicit only | |

Heterogeneous arithmetic (`Vec2 * float`) needs a two-parameter interface and a coherence rule for
multiple conformances to the same generic interface; deliberately not in v1.5.0.

The next scope check is **2026-09-06**.

**One limit stays**: a generic call shows the DECLARED signature, because the
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

`Merge pull request #30 from OIL1I/fix/ci-setup-dotnet-rest` (`ed5fb8d`)

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
