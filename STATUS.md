# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**v1.0.0 through v1.3.1 are released** — annotated tags on the remote, each with a release page and
three archives. M0–M10 are finished and tagged (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`).

**M12, the project system, is what v1.2.0 shipped**: `lyric.json` says what a project is, `build.lyr`
says what to build, `lyric new` writes one, and the tools read all of it.

**M11, the language server, has one delivery point left: completion.** It ships diagnostics while you
type, what a name under the cursor is, where it was declared, a program followed across its files,
documentation on hover, the outline of a file, and every place a name occurs. **v1.3.0 shipped all
of that**; completion is v1.4.0 and closes the milestone.

3626 tests green **in Debug and Release**, bytecode format **3.1**, **six** binaries plus
`lyrembed.dll`, version **1.3.1**.

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

- [x] **v1.4.0 slice 2 — completion for members after `.`** (2026-08-17). 3664 tests green, Debug
  and Release. Not merged.
  - The text at the cursor does not parse, so the question is asked of a program that does: a
    synthetic identifier is inserted at the cursor and the buffer compiled again. `foo.` becomes
    `foo.__lyric_completion__`, a member access whose member does not exist — and **the member name
    is never read**, only the receiver, which resolves either way.
  - **Measured: median 12.7 ms per request**, min 9.0, max 18.3, for a whole compile with the
    standard library. Against a keystroke the user made deliberately that is affordable, and it is
    what buys not rebuilding the one component 438 tests hang on.
  - Which members a type has is asked of a new `MemberFacts` in the front end, not assembled in the
    server. A list put together from the public surface would miss **extensions and interface
    defaults**, and on a `string` it would be empty — every string method of this standard library
    is an extension. **A completion list that omits a callable method teaches the reader it does not
    exist.**
  - **The enumeration and the lookup are separate code and can drift.** A test holds them together
    from one side: every member offered for a type with all four sources is written into a real
    program, and none may come back as `LYR-SEM0012`.
  - The other side is not symmetric, and that is a finding rather than a decision: **a STATIC
    extension is callable through an INSTANCE today.** The lookup's instance path falls through to
    the extension without checking, while the type path rejects a non-static one explicitly.
    Completion does not offer it. Pinned by a test that records which way round it currently is.
  - Slice 1 is what made the receiver readable: `TypeOf(mem.Target)` is a `NonValueType` for a name
    and the value's type otherwise, so static and instance sides fall out of the same answer the
    type checker uses.

- [x] **v1.4.0 slice 1 — a member access asks the type, not the table** (2026-08-17). 3636 tests
  green, Debug and Release. Not merged.
  - **The plan was wrong and the code said so.** I had planned to split `_refs` into two tables,
    declarations from uses, at roughly forty sites across the lowering and the flow analysis. It was
    not needed: `CheckTarget` already returns a `NonValueType(Symbol, Kind, Instance)` for a type or
    module name, and `CheckMember` then asked the reference table THE SAME QUESTION a line later.
  - Its own docstring wrote the redundancy out — *"CheckMember continues through the symbol anyway,
    not through the type"*. Two answers to one question, and the second was the wrong one: the table
    knows that `Point { … }` mentions `Point`, not that it BUILDS one.
  - The switch now runs over the receiver's TYPE. **~30 lines instead of forty sites**, and the
    delicate code was never touched.
  - With nothing left inferring a receiver's kind from the table, `BindRef(si, ts)` is safe —
    the change that broke two tests and a guide chapter when it was tried alone. **Both limits v1.3.0
    shipped are closed**: a struct initializer is a reference to its type, and the jump on it lands
    on the type instead of answering nothing.
  - The edge that had to survive: an unresolvable import as a receiver reported once, not twice. It
    falls through to `InstanceMemberOf`, which has no case for an error type and reports nothing, and
    the existing `IsError` check returns. Pinned by a test.
  - **Not done, and now with a reason rather than an intention**: splitting `_refs` by declaration
    versus use has no consumer. The lowering matches on symbol kind, the server compares
    `symbol.Declaration` against the node, and both work. Work without a complainant, through the
    most delicate code in the project.

- [x] **v1.3.1 — a diagnostic names what is wrong, not where to read about it** (2026-08-17). 3626
  tests green. Released; PR #23.
  - Eight messages cited a document. Five named `Sprache.md`, which has been `docs/Grammar.md` for
    some time, and the section numbers were wrong as well — §10 and §11 of a document with seven.
    Someone following either was sent nowhere twice.
  - **The citations are gone rather than repaired.** A citation ages in two ways at once, the file
    name and the section number, and both had already happened. Where the reference carried
    information (`§11 allows none or one 'string[]'`) the message now says it outright.
  - **The rule has a test**, because a rule nobody checks is a preference. It scans the string
    LITERALS of `src/` for `§` or a `.md` name; comments and XML documentation are free to cite,
    and they do. Verified by putting an offender back and watching it go red — a mechanical test
    that has never failed is a test nobody has seen work.
  - No code and no behaviour changed: same diagnostic codes, same spans, different wording.

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

**v1.4.0 — completion**, the last delivery point of M11, decided 2026-08-17.

| Slice | What | State |
|---|---|---|
| 1 | A member access asks the type, not the table | PR #24 |
| 2 | Completion: members after `.` | **done, unmerged** |
| 3 | Completion: names in scope | next |
| 4 | Documentation for the three undocumented stdlib files | |

**The mechanism is a completion MARKER, not an error-tolerant parser.** A request inserts a synthetic
identifier at the cursor and compiles through `CompilerOptions.SourceOverlay`, which has existed
since v1.2.0: `foo.` becomes `foo.__marker__`, which parses, and the sema resolves the receiver.
One compile per request at 7–16 ms, against a parser rebuild that 438 parsing tests hang on.

Rejected: answering from the LAST GOOD model. Its spans point into the text from before the
keystroke, and a `FileId` is an index into one `SourceManager` — see §Design decisions.

**Slice 4 is writing, not compiler work**: `std/io/console.lyr`, `std/core.lyr` and `std/option.lyr`
hold **33 `pub` declarations and no documentation at all**, so `println` hovers empty. v1.3.0 built
the feature that shows it.

The next scope check is **2026-09-06**.

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

`Merge pull request #24 from OIL1I/feature/member-receiver` (`f4b7990`)

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
