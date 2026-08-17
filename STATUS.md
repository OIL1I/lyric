# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**v1.0.0 through v1.2.0 are released** — annotated tags on the remote, each with a release page and
three archives. M0–M10 are finished and tagged (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`).

**M12, the project system, is what v1.2.0 shipped**: `lyric.json` says what a project is, `build.lyr`
says what to build, `lyric new` writes one, and the tools read all of it.

**M11, the language server, is NOT closed.** It ships diagnostics while you type, what a name under
the cursor is, where it was declared, and a program followed across its files. Three delivery points
are open: document symbols, find-references, completion. They are what v1.3.0 and v1.4.0 take —
see `## What we are working on`.

3313 tests green **in Debug and Release**, bytecode format **3.1**, **six** binaries plus
`lyrembed.dll`, version **1.2.0**.

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

- [x] **v1.3.0 slice 3 — documentation reaches the model, and hover shows it** (2026-08-17). 3426
  tests green, Debug and Release. Not merged.
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

- [x] **v1.3.0 slice 1 — a declaration records where its name stands** (2026-08-17). 3405 tests
  green. Merged as PR #18.
  - `INamedDecl` over thirteen nodes, and a jump now SELECTS the name instead of pointing at the
    start of the declaration. The severe cases were never the structs: a `ForInStmt` and a
    `CatchClause` span their bodies, so the jump used to select the whole loop.
  - **The plural case did not exist.** `BindingPattern` and `FieldPattern` are nodes of their own
    with their own spans, and `LocalSymbol.Declaration` points straight at them, so destructuring
    and match bindings already selected the name exactly. Imports are out for a different reason:
    `DefinitionProvider` redirects an `ImportBindingSymbol` to its target, so the import node is
    never a jump target. Nothing needed a `string[]` beside a `Span[]`.
  - **`NameSpan` is a `required init` property, not a positional parameter.** Every declaration
    record ends in `Span Span`; a second one beside it is two same-typed neighbours that can be
    swapped without anything going red. `required` makes omission a build error and the name at the
    construction site makes the swap impossible.
  - **The protocol had already modelled this.** `LocationLink` carries `targetRange` and
    `targetSelectionRange` — the declaration and the name — which is exactly the pair now in hand.
    A client announcing `linkSupport` gets both; one that does not gets the NAME, because with one
    range the useful one is where the cursor lands. `SpanMapper.ToStart` is gone: its own docstring
    said it existed because the target was a whole declaration, and that premise fell away.
  - **The totality test found `LambdaParam`**, which I had not counted. It carries a name, the sema
    binds it to a symbol, and it was missing from the twelve I planned for. The test asks the
    ASSEMBLY which nodes carry a `Name` and demands each either records a name span or stands in a
    short list of nodes that USE a name rather than declare one.
  - The two invariants — the name lies inside the declaration, and the text at the name span IS the
    name — are asserted over all **55** `.lyr` files the repository ships, not on fixtures alone.
    The text comparison is the half that catches an off-by-one; containment alone would not, because
    a wrong span inside the declaration is still inside it.

- [x] **The language server follows a program across its files** (2026-08-17). Merged as PR #16.
  - Editing a module refreshes the diagnostics of every open file that imports it, and a dependency
    is read from the editor's BUFFER rather than from its last save. **Both halves are needed and
    are separated on purpose**: an overlay nobody re-reads shows nothing, a cascade over stale text
    refreshes to the same answer. One test drives each.
  - **What a file depends on comes from the compilation itself**, not from the imports in its text.
    The resolver already followed them, transitively and through the project's roots, and a second
    answer to that question would be the one that is wrong.
  - **The cascade goes ONE level.** Two modules may import each other — a diagnostic rather than a
    crash, so both still compile — and a transitive cascade over such a pair would not terminate.
  - `CompilerOptions.SourceOverlay` is the seam, and it sits on the compiler rather than in the
    server: "compile as if these files held this text" is not an editor-specific idea. It closes the
    last of the three rough edges v1.1.0 shipped with.
  - **The fourth test is the counter-check**: a module nobody imports starts no cascade. Without it
    the other three would pass on a server that re-analyses every open document on every keystroke.

- [x] **`lyric new` writes a project that builds** (2026-08-17). Merged as PR #15.
  - Two shapes and two flags, as `zig init` and `cargo new` have them, rather than a template
    system: **with two variants a discovery mechanism is more machinery than content.**
  - **The templates are embedded in the binary** so nothing can go missing beside it the way a
    stdlib directory can — and they stay real files in the repository, which is what lets the test
    suite COMPILE them. `__name__` is a valid Lyric identifier, so a template is compilable Lyric
    rather than text with holes in it, and a template that stopped building is a red test instead of
    a first impression.
  - `gitignore` is stored without its dot so it does not take effect in the repository that ships it.
  - **The one command the driver runs itself**: it compiles nothing and executes nothing, so it needs
    no library and breaks no contract the driver's project file states.
  - **Found while writing the tests**: `Toolchain` starts every tool in the repository root, so a
    scaffolding test that set the TEST process's directory wrote four projects into the repository.
    `RunIn` gives the child its own working directory, which is the thing that actually decides
    where `new` writes.


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

**v1.3.0 — the editor understands your program.** Four of M11's open delivery points, decided
2026-08-17. Additive throughout: no language change, no format change, no new binary.

| Slice | What | Touches | State |
|---|---|---|---|
| 1 | A name span per declaration node | AST and parser | **done**, PR #18 |
| 3 | Doc comments reach the `SemanticModel`, hover shows them | one front-end seam, server | **done, unmerged** |
| 2 | `textDocument/documentSymbol` | server only | next |
| 4 | `textDocument/references` and the reverse index under it | **front end** | |

Slice 3 was pulled ahead of slice 2 on 2026-08-17: the two do not depend on each other, both sit on
the name span slice 1 delivered, and hover is used far more often than a symbol outline.

**Slice 2 is the cheap one.** A walk over the entry module's declarations; `AstChildren` already
provides the walk and nothing has to be resolved.

**Slice 4 is the expensive one, and the reason has not moved.** Neither `BindingResult` nor the
reference table of `TypeResult` can be enumerated, so "who else points at this symbol" cannot be
asked without a reverse index. That is an addition to the FRONT END, not to the server.

**Completion is deliberately not in v1.3.0.** It is the first question asked at a position where the
text does NOT parse — error tolerance, or a "what is admissible here" mechanism. That is a front-end
topic of its own size, not a fifth provider, and binding it to four small slices makes the date
unpredictable. It is v1.4.0, and M11 closes there. The release is split rather than the delivery
list: re-cutting a list is only legitimate in the scope check, and the next one is **2026-09-06**.

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

`Merge pull request #18 from OIL1I/feature/name-spans` (`41f9392`)

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
