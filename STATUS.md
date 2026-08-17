# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**v1.0.0, v1.0.1 and v1.1.0 are released** — annotated tags on the remote, each with a release page
and three archives. M0–M10 are finished and tagged (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`).

**M11, the language server, is done in the sense v1.1.0 left it**: diagnostics while you type, what a
name under the cursor is, where it was declared — and since the project system, a program followed
across its files.

**M12, the project system, is what v1.2.0 ships**: `lyric.json` says what a project is, `build.lyr`
says what to build, `lyric new` writes one, and the tools read all of it.

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

- [x] **Source maps — a panic names the line** (2026-08-15). Merged as PR #4.
  - Section 6 had been reserved and described since format 3.0 and never written. It now maps a byte
    offset in a function's code to a file and a line, **one row per position CHANGE** — a loop body
    is dozens of instructions across a handful of lines. Format **3.1**, `--no-source-map` leaves it
    out, and without it the file is byte for byte what it was before the section existed.
  - **Byte offsets, not instruction indices.** An index is not a notion of the format: it
    presupposes a runtime that decodes into an array before it runs, and `docs/Bytecode.md` claims a
    second runtime can be built from it alone.
  - **Nothing in the front end had to grow a field.** Every IR instruction has carried a `Span` since
    the lowering was built, every `BytecodeInstruction` its `.Offset`, and `Prepared.From` already
    built an offset table. One field was missing — `Prepared.Index`, because a `BytecodeFunction`
    does not know where it sits. The third time this project has found the data already there.
  - **The blocker was older than the slice**: a reader could not SKIP a section it does not know.
    `default: break` consumed nothing and the trailing-byte check three lines later rejected the
    payload it was meant to step over. That is the mechanism the format's forward compatibility rests
    on, and it had never run, because nothing had ever written an unknown section. Consequence for
    users is under `## Still open`.
  - **The obvious test measured nothing.** With `return a / b` the faulting `div` is followed by the
    `retval` of the same statement, so `Ip` and `Ip - 1` give one line and a wrong implementation
    stays green — measured by writing `Ip` and watching it pass. The test that holds pulls the
    expression onto its own line, where the two answers differ by one.
  - The row carries **no column**, and a minor may only add skippable sections, so that is fixed
    until a major. Deliberate: no consumer today, and a column beside this section later would be a
    second mechanism for the same thing.

- [x] **M11 slice 3 — go to definition** (2026-08-14). Merged as PR #3.
  **Merged into `main` as PR #3** together with slices 1 and 2, after CI was green on Linux and
  Windows — the platform-dependent halves of the URI tests had only ever run here.
  - **It needed nothing from the front end.** `Symbol.Declaration` holds the node and every node
    carries a span, so the feature is a lookup on top of the search slice 2 built. The estimate held
    for once, and the reason is worth keeping: the two things slice 2 had to add were the expensive
    half of both slices.
  - **A target in another file is the ordinary case**, not an edge one — every call into the
    standard library lands there — and it needed no second mechanism, because `StdlibLoader` reads
    those files from disk with their real paths. The URI is built from the path, except when the
    target is the requested document, where the client's own spelling goes back: it asked about that
    string, and a rebuilt one is a different string for the same file.
  - **The jump lands on the START of a declaration**, not on its name. A struct with twenty members
    is a twenty-line span and selecting all of it is noise. The AST records no span for a name
    alone, and searching the text for it would be a second, weaker way of knowing where it is. A
    name span per declaration node would fix it properly; not built.
  - **A symbol without a declaration stops the search** rather than falling outwards. `int` is
    declared in no file, and offering the enclosing binding would send the reader somewhere they
    did not ask about.
  - Span-to-range moved into `SpanMapper`, which three features now share, in three named forms
    rather than one with a flag: diagnostics widen an empty span because a zero-width squiggle is
    invisible, hover takes it as it stands, a jump collapses it to its start.

- [x] **M11 slice 2 — hover** (2026-08-14). 3212 tests green, Debug and Release.
  - The cursor gets the binding form and type of a local, a parameter's type, a function's
    signature, what kind of type a type name names, and the plain type of a subexpression that
    binds to no symbol at all. Rendered with `TypeFacts.Display`, the function the diagnostics use,
    so hover and an error message never disagree about what a type is called.
  - **`CompileResult` dropped everything the front end had learned.** It carried diagnostics and
    bytes; the `Compilation`, the bindings and the types were built and thrown away. Everything a
    tool can say ABOUT a program rather than about its output is in those three tables. They come
    out as one `SemanticModel` rather than three fields — a binding table from one run beside a type
    table from another is a fault nobody would find.
  - **`AstChildren` is a switch, not reflection.** A reflective walk handles a new node without
    being told, which reads as the better property until a node holds its children in a shape the
    walk does not recognise: it is silently skipped. The throwing `default` makes an added node a
    build-time question, and the test asks the ASSEMBLY which node types exist rather than trusting
    a list that would drift.
  - **That test found a node I did not know existed**: `GlobalInitStmt`, which the lowering
    synthesises. It is not syntax, a case for it would mean the AST knows about the lowering, and
    meeting one during a syntax walk means something leaked — so the walk refuses it, and a test
    holds that too.
  - **Type names inside a function body were resolved and dropped.** The resolver binds the ones it
    walks — those in declarations — while an annotation on a local is reached only from the sema's
    `ResolveType`, which computed the symbol and discarded it. It now writes into the SAME table,
    so one question has one answer whoever asked it. Found by a test, not by reading.
  - **A generic call shows the DECLARED signature**, type parameters included. `T` at a call site is
    `int`, and showing that would be better — but the substitution lives in a private function of
    the type checker, and a second one in the server would be a second answer to what `T` became.
    Recorded as a limit with a test that measures it, rather than half-substituted.
  - **The last analysis that produced a model is kept per document.** Mid-edit is the normal state
    of a file someone is looking things up in; answering only from text that parses would go silent
    exactly when it is wanted. Errors do not disqualify a model — a program with a type error still
    has resolved names everywhere around it.

- [x] **M11 slice 1 — diagnostics in the editor** (2026-08-14). 3102 tests green, Debug and Release.
  - `lyrls` and `lyrlsp.dll` beside it: the base protocol, the lifecycle state machine, an overlay
    of open buffers, and a bridge to `SourceCompiler.Check`. The server compiles the BUFFER, not the
    file on disk. The VS Code extension carries a language client and with it a build step it did
    not have — one npm dependency, pinned by a lock file.
  - **The measurement corrected the design before it was built.** A full compile IN THIS PROCESS
    costs 7 to 16 ms for a hundred lines, standard library included. The debounce was planned at
    200 ms and is 50: at that price it, and not the compiler, was the dominant latency. It settles
    the incremental question for now — a query graph would optimise a number with an order of
    magnitude of slack.
  - **`System.Uri` cannot read the URI an editor sends.** It leaves `%3A` encoded, so `LocalPath`
    answers `/c:/Users/x.lyr` — not a path, and no file API rejects it loudly. `AbsoluteUri` does
    not normalise the two spellings into one either, so it is useless as a key. A document's
    identity is therefore its PATH, and the URI is echoed back verbatim rather than rebuilt.
  - **The standard library is reparsed every time, deliberately.** Caching its ASTs looks free —
    they are immutable — and is not: `Span` carries a `FileId`, and that is an index into ONE
    `SourceManager`. A cached AST in a fresh manager points every span at whatever file holds that
    index. Not a crash; a diagnostic in the wrong place.
  - **The suite was green and the thing did not start.** A language client appends `--stdio` to a
    server it launches as an executable, and the argument parser rejected it. There was a test that
    unknown arguments are a usage error and NONE that the argument the client actually sends is
    accepted — the process test invoked the server in a form that never occurs. The flag is named in
    no file of this repository, which is exactly why it was invisible. Pinned now.
  - **Release caught what Debug hid.** `Lyrls` existed locally only because it had been built by
    hand; no test project pulled it in, so a clean Release run had no directory to inspect. The
    fix is one `ReferenceOutputAssembly="false"` line, and the rule now stands in the project file:
    every binary an architecture test INSPECTS has to be referenced there.

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

**M11, the language server.** Slices 1 to 3 ship diagnostics, hover and go-to-definition. What
remains is document symbols with find-references, and completion. Neither is built.

**Document symbols is the cheap one.** It is a walk over the entry module's declarations, and
`AstChildren` already provides the walk; nothing has to be resolved. It needs a name span to be
useful, which is the same gap the jump has — see below.

**Find-references is the expensive one, and the reason has not moved.** Neither `BindingResult` nor
the reference table of `TypeResult` can be enumerated, so "who else points at this symbol" cannot be
asked without a reverse index. That is an addition to the FRONT END, not to the server.

**Three limits recorded with tests rather than as intentions**: a generic call shows the declared
signature because the substitution is private to the type checker; there is no documentation to show
at all, because `///` is a token kind that reaches no AST node; and a jump lands on the START of a
declaration, because no node records a span for its NAME. The third is the one worth fixing — a name
span per declaration would improve the jump and document symbols at once.

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
  from Claude and the code from the user. What to watch is whether the understanding of the code
  keeps up with its size. The changelog starts at `v1.0.0`; before it the annotated tag message is
  the release note.
- **At the end of every milestone the delivery list is to be ticked off point by point, not the exit
  criterion alone.** M5 and M6 each silently failed to deliver part of their items; the gap disguised
  itself as a clean diagnostic. For the same reason **six** gates were re-cut in M7, because they
  required language features of later slices.

## Last relevant commit

`Merge pull request #4 from OIL1I/feature/source-map`

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
