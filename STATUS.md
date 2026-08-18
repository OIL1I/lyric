# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**v1.0.0 through v1.7.0 are released** — annotated tags on the remote, each with a release page and
three archives. M0–M10 are finished and tagged (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`).

**M16 — the tooling milestone — is current** (decided 2026-08-18, at the post-v1 pace): the
language server learns the project, the editors learn the server. Slices, in order: workspace
compilation (**done**, PR #43) → rename + workspace symbols (**done**, PR #44) → semantic tokens
(**done**, PR #45) → signature help + folding + inlay hints (**done**, PR #46) → the VS Code
extension rounded off (**done**, stacked on #46) → a JetBrains thin plugin over the platform's
LSP API, deliberately no PSI.

**M14 and M15 are what v1.7.0 shipped, both built 2026-08-18**: the interpreter stops allocating
(frame pooling, inlining, scalar replacement, devirtualization) and the native boundary learns
value structs at 0 B per call. PRs #41 and #42, details under *Recently finished*.

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

3878 tests green **in Debug and Release**, bytecode format **3.2**, **six** binaries plus
`lyrembed.dll`, version **1.7.0**.

**What this state can do**: the whole language of the grammar compiles and runs; a standard library
that largely carries itself (`Map`, `Set`, merge sort, all iterator adapters and the string hash are
written in Lyric); six tools including the REPL, the build runner and a language server that
compiles the PROJECT — references across files in both directions, diagnostics for files nobody has
open, disk changes behind the editor picked up through file watches; a VS Code extension with live
diagnostics; a project that scaffolds, declares its own layout and builds itself with a Lyric
script; and an embedding API with which a C# host loads scripts, sandboxes them, calls functions
out of them and hands its own functions, types and value structs in.

> **The file had grown to 1088 lines by 2026-08-07** and contradicted itself in three places. It has
> been cut back to its own maintenance rule: recent slices, open points, design context. Everything
> else stands in `git log`.

## Recently finished

- [x] **M16 slice 5 — the VS Code extension rounded off** (2026-08-18, stacked on #46). No server
  change; the extension catches up with what the server can do, and the release learns to ship it.
  - **`lyric.restartServer`** over the existing restart chain — the way out of a hung or updated
    server without a window reload. A **language status item** shows starting/running-with-version/
    failed; a failed start stops being an invisible toast, and clicking the item retries.
  - **A task provider** (`lyric: build`) for every workspace folder with a `lyric.json`: runs the
    project's `build.lyr` through the driver, diagnostics land in the Problems panel via the
    `$lyric` problem matcher. The matcher's regex was verified against `DiagnosticEngine.RenderText`
    first — `path:line:col: severity[CODE]: message` — the precondition the plan named.
  - **Snippets** for the declaration forms, written against the grammar (`match (x) { P => v, }`),
    and **`vsce package` in the release workflow**: from the next tag every release carries an
    installable `vscode-lyric-<version>.vsix` beside the archives — verified by a local dry run
    (324 files, 471 KB), including the LICENSE copy vsce insists on. Marketplace publishing stays
    a separate decision; the release asset is the distribution.

- [x] **M16 slice 4 — signature help, folding, inlay hints** (2026-08-18, PR #46). 3912
  tests green in Debug and Release, 13 new.
  - **Signature help off the CURRENT buffer** (the completion argument: the model the keystroke
    invalidated is the one that would answer about the text before it). The label is the
    declaration AS WRITTEN, sliced from the declaration's own source — parameter names and types
    verbatim, each parameter a substring of the whole, which is what the client's highlight
    matches. The active parameter follows the argument spans; a callee that is a function VALUE
    shows the FnType's shape with no invented parameter names. `activeSignature` is always 0 — no
    overloading, by Rule 2.
  - **Folding** is syntax off the last-good tree (regions must not snap open on a type error):
    declarations, blocks, matches; the closing line stays visible; one range per start line, so
    `fn f() {` — declaration AND body — renders one fold control, not two.
  - **Inlay hints for bindings only**: the inferred type of an unannotated `let`/`var` and of
    every loop variable, from the symbol the definite-assignment analysis already binds. A written
    annotation silences the hint; an `ErrorType` shows nothing — the squiggle owns that spot.
    Parameter-name hints are a different feature with a different noise budget, deliberately not
    half-built here.

- [x] **M16 slice 3 — semantic tokens** (2026-08-18, PR #45). 3899 tests green in Debug
  and Release.
  - Every NAME colored by what the compiler resolved it to, from the same two tables and the same
    name spans the references and the rename use — one switch (`NameSpans`), three consumers.
    Keywords, literals and comments stay with the TextMate grammar, which knows them lexically.
  - Full-document only, no delta and no range form: the cost is one walk over one file, not a
    number that buys bookkeeping. The legend: namespace, type, enum, interface, typeParameter,
    parameter, variable, property, enumMember, function, method; modifiers declaration, static,
    readonly (a `let`).
  - An operator use colors nothing — the synthesized node's invalid span, doing for colors what
    it does for rename edits (pinned: no token ever contains `+`). An unresolved name gets NO
    token: uncolored is visibly "the compiler does not know".
  - Import-clause names carry the target's color, through the same special case the rename walks.
    Method vs function is the member-table line; the initializer field, `this.x` and `p.x` are
    all the FIELD (pinned by decoded-token tests, not by raw deltas).

- [x] **M16 slice 2 — rename and workspace symbols** (2026-08-18, stacked on #43). 3893 tests
  green in Debug and Release.
  - **The AST learned where names stand at USE sites**: `MemberExpr`, `NamedType`,
    `StructInitExpr`, `TypePathExpr`, `StructInitField`, `AttributeNode` and the selective import
    clause now record the span of the name alone, the same `INamedDecl.NameSpan` idea on the other
    side. A node the sema synthesizes carries an INVALID span — an operator use writes no name, so
    a rename edits nothing there and `a + b` survives renaming `add` (pinned by test).
  - **Rename is the reference walk plus the import clauses**, which no table carries: `import util
    { value }` declares a binding rather than using the target, and forgetting it breaks every
    importer. End-to-end pinned: the returned WorkspaceEdit, APPLIED, recompiles clean with the
    old name gone.
  - **Refusals carry reasons** (surfaced via `-32803`): the standard library, a module, a built-in,
    an unknown node form (refuse loudly rather than corrupt quietly), and outside a project any
    rename that would leave the file. NO collision analysis, on purpose — the compile that follows
    is the conflict analysis.
  - **The sema now binds an initializer's fields** (`x = 1` in `Point { x = 1 }`) — found because
    the rename test demanded completeness; references gained the same sites, and every reference
    site narrowed from the node to the name (`p.x` → `x`), four pinned tests updated.
  - `workspace/symbol`: the outline walk, flattened over every live compilation, stdlib excluded,
    case-insensitive substring, capped at 512.

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

**The workspace compile confirms it** (2026-08-18, in-process, Release, 15 runs): a 14-file
project through `CheckProject` measures **median 41.7 ms** against **40.1 ms** for a single file
of it. The standard library dominates; the project's size is in the noise, and the incremental
compiler stays unwarranted at project scale too.

## What we are working on

**M16 slice 6 — the JetBrains thin plugin — is next, and it closes the milestone.** A Gradle
project under `tooling/jetbrains-lyric`: the `.lyr` file type, the existing TextMate grammar
bundled, an `LspServerSupportProvider` that starts `lyrls` with the same lookup the VS Code
extension uses (explicit path → beside the driver → PATH). To verify at slice start, not from
memory: which LSP features the platform API consumes at the chosen baseline. Commercial IDEs
only; LSP4IJ support is deliberately not built. Verification is `verifyPlugin` plus a manual
checklist — headless IDE tests are not worth their harness. A PSI plugin stays off the table:
a second frontend in Kotlin with a permanent lag is the mechanism this project does not add.

**Not renameable, recorded**: a module (rename the file), an enum variant's payload field (no
symbol exists for it), anything whose declaring module is native. Renaming across `build.lyr` is
not covered — the build script sits outside the source root and compiles as its own unit; its
diagnostics say so on the next compile.

**Erato's A2 is answered in its useful direction** — the host declares the value types in its
SDK, the script uses them, nothing allocates. What remains on the register's list for Lyric is
A4 (an opaque `Entity`) and the E4-side adoption. The other open points — heterogeneous
arithmetic, compound assignment through the interfaces, the static-extension asymmetry,
the first compiler-read attribute, the `for-in` peephole — stay material for the **2026-09-06**
scope check.

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

`vscode: the extension catches up with its server, and the release ships it` (stacked on PR #46)

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
