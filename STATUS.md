# Lyric — Current State

> This file is the **only** one in the project that changes often. It is updated
> after every finished slice. Claude reads it at the start of a session to know
> where we stand.
>
> Keep the content short. Anything already committed can go —
> `git log --oneline` is the history, not this file.

---

## Current milestone

**v1.0.0 through v1.8.0 are released** — annotated tags on the remote, each with a release page.
M0–M10 are finished and tagged (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`). From v1.8.0 a
release carries the three toolchain archives plus two installables: the `.vsix` and the JetBrains
plugin zip.

**M18 — the formatter — is BUILT** (2026-08-19, branch `feature/m18-lyrfmt`, stacked on M17,
four slices). The delivery list, ticked point by point as the milestone rule demands:

- [x] the lexer keeps comments on request; the compile path stays byte-identical (slice 1)
- [x] the document algebra and its renderer — only the renderer measures columns (slice 1)
- [x] the whole AST prints: literals from their spans, parentheses re-derived from §6.1 (slice 2)
- [x] comments survive — all three forms, one positional mechanism; blank lines are the
      user's, capped at one (slice 3)
- [x] the corpus invariants: every `.lyr` in the repository formats, is stable, reparses to
      the same tree, loses no comment (slice 3)
- [x] `lyrfmt` in place / `--check` / `--stdin`, `lyric fmt` in the driver (slice 4)
- [x] the repository formats ITSELF, and a test holds it formatted from now on (slice 4)
- [x] guide chapter 18; CONTRIBUTING's "no formatter in v1" clause retired (slice 4)

**M17 — packing: a program becomes one file — is BUILT** (2026-08-18, branch
`feature/m17-lyrpack`, three slices, PR #50). `lyric pack app.lyr` produces a standalone
executable: a prebuilt stub runtime with the `.lyrbc` and a 24-byte footer appended — a byte
copy, no linker. Two new binaries (`lyrpack`, packer, references Core ALONE; `lyrstub`, the
runtime half of a packed program), format contract in `docs/Pack.md`, guide chapter 17, and
both workflows pack-and-run an example on every platform before archiving.

**M16 — the tooling milestone — is CLOSED** (decided and built 2026-08-18, at the post-v1 pace):
the language server learned the project, the editors learned the server. The delivery list,
ticked point by point as the milestone rule demands:

- [x] project-wide compilation (PR #43)
- [x] rename (PR #44)
- [x] workspace symbols (PR #44)
- [x] semantic tokens (PR #45)
- [x] signature help (PR #46)
- [x] folding (PR #46)
- [x] inlay hints (PR #46)
- [x] restart command and status item in VS Code (PR #47)
- [x] task provider with problem matcher (PR #47)
- [x] snippets (PR #47)
- [x] `.vsix` in the release (PR #47)
- [x] the JetBrains thin plugin (PR #48)

The PR stack #43 ← #44 ← #45 ← #46 ← #47 ← #48 is merged and shipped as **v1.8.0**.

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

4125 tests green **in Debug and Release**, bytecode format **3.2**, **nine** binaries
(`lyrstub`, `lyrpack` and `lyrfmt` are the three new ones) plus `lyrembed.dll`, version
**1.9.0**.

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

- [x] **M18 — the formatter** (2026-08-19, four slices, `feature/m18-lyrfmt`, 4125 tests green
  in Debug and Release). gofmt's shape for Lyric: parse, print from the tree, no style options.
  - **Wadler under everything**: the node formatters say what belongs together and where
    breaking is allowed; ONLY the renderer measures columns (width 100, indent 4 — the numbers
    CONTRIBUTING already had). `IfBroken` carries the trailing commas of broken layout, exactly
    where the grammar permits one.
  - **The AST decides two things.** Literals lost their spelling, so every literal prints from
    its source span. And there are no parenthesis nodes, so parentheses re-derive from §6.1 —
    redundant ones GO (`(a && b) || c` loses its pair; `&` beats `==` here, unlike C), needed
    ones return, and the corpus invariant is what proves no meaning moved.
  - **Comments are one positional stream** — line, block and doc under a single cursor,
    consumed at sequence boundaries; trailing stays trailing, the group glued to an item
    travels with it, a comment inside an expression surfaces at the statement boundary (the
    documented trade). Blank lines are the user's, capped at one; imports collapse, top-level
    declarations breathe, bodiless natives group.
  - **The repository is the test.** Four invariants over every `.lyr` in stdlib, examples and
    templates — formats, stable, same tree, no comment lost — and since the corpus was
    reformatted with its own tool, `The_repository_is_formatted` holds the gofmt property
    permanently. Two style rules were corrected against the corpus before freezing it; the
    goldens pin taste, the corpus pins correctness.

- [x] **M17 — packing** (2026-08-18, three slices, `feature/m17-lyrpack`, 3947 tests green in
  Debug and Release). A `.lyrbc` plus a prebuilt single-file stub plus a 24-byte footer = one
  executable; the stub reads the module out of its own file and runs it with every capability.
  - **The pack owns nothing it does not need**: `lyrpack` references Core alone (the
    architecture test states it positively), packs `.lyrbc` only, and validates nothing beyond
    the extension — the module meets exactly the reader it was paired with, at first start.
    `lyric pack app.lyr` is the driver composing lyrc and lyrpack, the `run` pattern.
  - **A packed program owns its argv** — no `--` protocol, no stub flag, not even `--help`; the
    footer's absent/damaged answers are distinct so a bare stub and a truncated download get
    different messages; a failed pack deletes its half-written output.
  - **Measured**: dev stub (framework-dependent single file) 0.36 MB; self-contained win-x64
    stub **73.5 MB**, packed hello 73.6 MB. `PublishTrimmed` takes the stub to **13.0 MB** with
    no trim warnings and survives a Set/f-string/argv smoke — shipped UNTRIMMED anyway: the
    only gate so far is that smoke, and 60 MB is not worth an unverified runtime. Decision
    material for the scope check.
  - **The release gates itself**: both workflows publish, pack `examples/hello.lyr` and RUN the
    result on each platform — on macOS after the ad-hoc re-sign the guide documents, so the
    documented shipping workflow is executed, not merely written down.

- [x] **M16 slice 6 — the JetBrains thin plugin, and the milestone closes** (2026-08-18, stacked
  on #47). `tooling/jetbrains-lyric`: ~200 lines of Kotlin, and every one of them is wiring.
  - **The plan's verification came first and corrected the baseline**: the platform's LSP
    integration gained find references and semantic tokens in 2024.2, folding and inlay hints in
    2025.2, signature help and workspace symbols in 2025.3, rename in **2026.1** — so the
    baseline is 2026.1 (`sinceBuild 261`), not the 2023.2 the plan guessed; that floor would have
    kept only diagnostics, completion and the jump. Commercial IDEs only (`com.intellij.modules.lsp`);
    LSP4IJ deliberately unsupported.
  - The DEPRECATED API names (`LspServerSupportProvider`) on purpose: documented as preserved and
    fully functional, while the renamed successor exists only from 2026.1.4 — pinning to a point
    release for a rename of the same API would be baseline for nothing.
  - **The grammar has one home**: the build copies the TextMate bundle from `../vscode-lyric` at
    packaging time (the test suite pins that copy against the lexer) and the provider extracts it
    for the IDE's TextMate machinery at runtime. One setting — the toolchain directory, else
    PATH, the extension's own ladder.
  - **Verified by building**: the 17 KB zip holds the jar, the grammar and no Kotlin stdlib
    (`compileOnly` — the IDE ships its own, a second copy is a classloader conflict on a timer).
    `verifyPluginStructure` green; the runtime behavior is a manual checklist in the plugin's
    README, because headless IDE tests cost more harness than this plugin has code. CI builds it
    on every push; the release attaches `jetbrains-lyric-<version>.zip`.

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

**M17 and M18 shipped together as v1.9.0** (2026-08-19) — the PR stack #50 (packing) ← #51
(formatter), merged in that order; release commit and annotated tag explicitly delegated for
this release, normally the maintainer's step. The release workflow gates itself: it packs and
runs an example on every platform before an archive exists.

**M17's deliberate limits**: one platform per pack (a foreign platform packs via `--stub` with
that platform's stub out of its archive — no `--target` until someone needs it); the stub ships
untrimmed (measured 73.5 → 13.0 MB, decision material above); capability narrowing at pack time
is a footer field for a future minor. **And one limit the release gate found rather than the
plan**: a packed Mach-O fails codesign's strict validation, so macOS cannot RUN packed programs
yet — the payload has to become a real Mach-O segment (deno's route). The pipeline documents
the state honestly: macOS verifies pack-succeeds plus the signed bare stub, Windows and Linux
run the packed result.

**M18's deliberate limits**: precedence-redundant parentheses vanish (the AST has no node for
them — keeping them means a parser change, material for the scope check if it itches); a
comment inside an expression surfaces at its statement's end; the language server does not
serve `textDocument/formatting` yet — the formatter lives in `lyrfe`, so wiring it into `lyrls`
is a slice, not a design.

**Deviation from the plan, recorded**: no own `Lyric.Formatting` library — the formatter is a
namespace in `lyrfe`, because both consumers (lyrfmt, lyrls) already share that assembly and a
fourth library bought naming trouble for zero separation.

**Noticed twice while testing, unresolved**: one LSP test fails sporadically under full-suite
load (different configs, different runs, never reproducible alone — 254/254 green in isolation
every time). Worth a look before it becomes a CI lottery.

**M16 is closed and released as v1.8.0.** What remains from it: the first manual run of the
JetBrains checklist (plugin README) against the released zip, in a 2026.1+ IDE.

The open points for the **2026-09-06 scope check** stand unchanged: heterogeneous arithmetic,
compound assignment through the interfaces, the static-extension asymmetry, the first
compiler-read attribute, the `for-in` peephole, Erato's A4 (an opaque `Entity`) and the E4-side
adoption — plus, new from this milestone: parameter-name inlay hints, semantic-token deltas if a
measurement ever asks, and a duplicate-module diagnostic for two files claiming one name.

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

`fmt, cli: lyrfmt ships, lyric fmt drives it, and the repository formats itself` (closes M18's
delivery list; the release commit follows on main)

---

## How to maintain this file

- After every slice: extend `## Recently finished`, update `## What we are working on`.
- **At most four entries under `## Recently finished`.** The fifth goes — it stands in `git log`.
  This rule existed already; it was ignored for 1088 lines.
- On a milestone change: enter the new milestone at the top.
- Finished points under `## Still open` are to be **deleted**, not struck through.
- **Never** plan new features here.
