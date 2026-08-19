# Changelog

This file starts at v1.0.0. Before it there was no compatibility promise to describe — neither for
the language nor for the `.lyrbc` format — and a changelog written under those conditions records
churn rather than change. The pre-1.0 releases carry their notes in their annotated tags.

Versions follow `vMAJOR.MINOR.PATCH`, as described in [README](README.md#versioning). Each entry
lists what changed **for someone using the toolchain**: the language, the standard library, the
bytecode format, the command line and the embedding API. Compiler internals are in `git log`.

---

## Unreleased

### Fixed

- **`catch (e: Throwable)` catches now.** It compiled — the exception analysis treats an
  interface catch as handling — and then never caught: the handler carried the interface's
  type id and the runtime compared it against the thrown CLASS, so the exception flew past a
  clause the compiler had accepted. Found by the conformance suite on its first run. The
  explicit `Throwable` catch is the catch-all now, identical to `catch (e)`; a catch naming
  any OTHER interface is refused with a diagnostic until the handler table can express a
  conformance test — the alternative was a clause that silently caught nothing.

## v1.15.0 — 2026-08-19

The freeze prep: the design leftovers settled before the spec freezes semantics. `opaque type`
arrives, the string API becomes methods, iterator chaining gets its documented No — and a
latent function-id collision in the lowering falls. The bytecode format stays **3.2**.

### Fixed

- **A function-id collision in the lowering**, latent since struct-returning natives: the
  global initializer's slot was reserved only for DECLARED globals, but a struct-return buffer
  CREATES one during body lowering — and once a body also requested an extension method (the
  new string methods do), initializer and extension landed on the same id, and calls
  mis-spliced into the wrong function. The initializer draws from the shared id counter now,
  and the function list refuses id holes with a named internal error instead of a silent
  mis-splice.

### Added

- **Strings have methods**: `s.trim()`, `s.split(",")`, `s.contains(x)`, `s.length()` — 26
  methods via `extend string` in `std.string`. The free forms warn as **deprecated** and go
  with 2.0; `concat` and `repeat` stay free (they back `+` and `*`), and the type-directed
  families (`fromXxx`, `parseXxx`) keep their names. The methods come with any import of the
  module; a file needing no free name writes `import std.string as strings;` — and an import
  whose extensions are used no longer counts as unused. `s.length()` stays a call because it
  costs O(n), and every method returns a NEW string.

- **`opaque type`**: an alias with a new IDENTITY over the same layout —
  `pub opaque type Entity = int;`. Nothing converts implicitly in either direction; the explicit
  `as` to exactly the underlying and back is the one crossing; `==`/`!=` compare within one
  alias; arithmetic, ordering, constraint satisfaction and f-string rendering are refused. At
  runtime the value IS its underlying (the cast costs nothing), and a native signature resolves
  the alias to the underlying — an SDK's handle crosses the host boundary as a plain number
  while scripts can neither forge one nor leak it. Neither `opaque` nor `type` is a keyword;
  both stay usable as identifiers.

## v1.14.0 — 2026-08-19

The std polish, born from a line-by-line audit. The string module stops being quadratic, the
print family collapses to one generic concept, the collections learn the operations daily use
kept reaching for, arrays cross the native boundary as parameters for the first time — and two
new modules arrive: `std.random` and `std.time`. The bytecode format stays **3.2**.

### Added

- **`print`, `println`, `eprint` and `eprintln` take any `Display` value**: `println(42)`,
  `println(true)`. The string forms keep working unchanged — a string displays as itself — and
  `write`/`writeln` warn as **deprecated**; they were the same thing under a second name.
  Bytecode compiled before this release keeps running.

- **Collections round out**: `List` gains `insert`, `removeAt`, `first`, `last`, `reverse` and
  `swap`; `Map` gains `getOr`, `clear` and `entries` — key and value in ONE walk, without the
  second probe per key; `Set` gains `clear` and is `Iterable`, so `for (v in set)` walks it
  directly. `clear` on all three keeps the backing for reuse; the values are released all the
  same.

- **`std.iter` gains `flatMap`, `chunks`, `reduce` and `first`.**

- **Arrays cross the native boundary as parameters** — the bytecode format always allowed it
  (§3 type grammar; format stays **3.2**), the registry just never used it. On top of it:
  `std.io.file.writeBytes` and `appendBytes` (the write side readBytes was waiting for),
  `std.string.utf8Encode` and `utf8Decode` — the strict bridge: invalid bytes answer `null`
  instead of the U+FFFD replacement `readText` documents — and `fromChars` became one native
  call instead of one string per character.

- **`std.random`**: the generator moved out of `std.math` — randomness is not arithmetic —
  and gained what it was missing there: `shuffle` (Fisher–Yates over a `List`), `choice` and
  `nextGaussian`. Deterministic, seeded by the caller, no capability. The `std.math.Random`
  twin stays one release as a deprecated migration path.

- **`std.time`**: `Instant` and `Duration` as value structs over epoch milliseconds —
  `b.since(a)`, `a.plus(d)`, and `iso()` rendering UTC ISO 8601 with floor semantics, so an
  instant before 1970 lands in the right day. Gated by `osAccess`, the same bit as `std.os`:
  reading the clock is a question to the environment, and a new bit would be a contract change.
  The subtraction is a named method, not an operator — `Instant - Instant` yields a Duration,
  and the operator interfaces are homogeneous by the v1.13 decision.

### Fixed

- **`std.string` stops being quadratic.** `StringBuilder.build` and `join` folded left and
  copied the whole result once per piece — both are one native join now; `replace` moves
  untouched stretches as whole substrings; the searches, parsers and trims index a character
  array instead of calling O(n) `charAt` per position. Same results, different cost curve.

- Audit rests: `std.fmt` loses its German locals, `std.io.file` a torn doc fragment, and
  `std.io.console` sorts a native above the "written in Lyric" divider it contradicted.

## v1.13.0 — 2026-08-19

The language gaps close. Interface inheritance arrives — one parent, implied through the whole
chain — compound assignment reaches through the operator interfaces, block lambdas infer their
return type, and `std.core` becomes the import-free root of the library. One question got its
documented No: heterogeneous operator arithmetic. The bytecode format stays **3.2**.

### Added

- **Interface inheritance**: an interface may declare one parent — `interface Labeled :: [Named]`.
  Conforming to the child implies conforming to the whole chain: implementing types provide the
  chain's abstract members (a missing one names the implying interface), inherit its default
  methods, satisfy parent constraints, and carry into parent-typed interface values. A value of
  the child's interface type answers the parent's members too. The rules: at most one parent
  (several requirements side by side are constraints: `<T :: [A, B]>`), only interfaces, no
  cycles (`LYR-SEM0078`), and no redeclaring a chain member (`LYR-SEM0079`) — an inherited
  member keeps its declaring interface, so the same call cannot dispatch two ways. What a chain
  does NOT add: a child interface *value* does not convert to the parent's type — conformance is
  implied for the implementing type; take the concrete value through the parent directly.
  `std.core` does not adopt `Hashable :: [Equatable]` yet: changing what every conforming type
  must implement is a breaking cut reserved for 2.0.

- **Compound assignment reaches through the operator interfaces**: `v += w` on a type conforming
  to `Add<T>` now compiles for variable targets (locals, captured variables) instead of
  reporting `LYR-SEM0003`. Field and element targets stay written out — the desugaring would
  evaluate the object or the index twice, and that stays visible in source.

- **Block lambdas infer their return type**: `(x: int) => { return x * 2; }` needs neither an
  annotation nor a context anymore — the type comes from the body's `return` statements,
  unified like match arms (`return null;` widens to the optional). This also closes the
  open-generic case: `apply(5, (n) => { … })` binds `U` from the block. A non-void inferred
  lambda still needs return coverage, and disagreeing returns are one error at the lambda.

### Changed

- **`LYR-PAR0039` retired**: `interface B :: [A]` parses since this release; everything the
  parent list may not be is a semantic message now, not a parse error.

- **`newStringBuilder` warns as deprecated** — the piece v1.12 had to leave out. `std.core`
  imports nothing anymore: its extensions use private duplicates of six string natives
  (`fromInt` through `charAt`; the registry binds both names to the same host function), which
  makes `std.core` the library's root — and `import std.core { Deprecated }` inside
  `std.string` legal. Public API is unchanged; existing bytecode keeps running.

### Fixed

- **`@Deprecated` keeps its promise**: it emits no metadata row and roots nothing. Previously a
  non-generic deprecated function survived dead-code pruning in every importing program — dead
  code carried along exactly because it was marked for removal.

## v1.12.0 — 2026-08-19

The standard library grows up. Every public item is documented, constructors live on the types,
the first real deprecations start their clock, and the library tests itself — in Lyric. Two
compiler fixes came out of the work. The bytecode format stays **3.2**.

### Added

- **Constructors on the types**: `List<T>.empty()`, `Map<K, V>.empty()`, `Set<T>.empty()`,
  `StringBuilder.new()` and `Random.seeded(seed)`. The free functions `emptyList`, `emptyMap`,
  `emptySet` and `newRandom` still work and warn as **deprecated** — the first real uses of
  `@Deprecated`, and their removal lands with the next major. (`newStringBuilder` points at its
  successor in the documentation; its attribute waits on `std.core` visibility inside
  `std.string`, where the import would be a cycle.)

- **`std.io.file.readBytes`**: the whole content as raw bytes, undecoded — the answer
  `readText` cannot give, because its UTF-8 decoding turns invalid bytes into U+FFFD. Writing
  bytes is not there yet: an array has never crossed the native boundary as a parameter, and
  that machinery is a change of its own.

- **The standard library tests itself.** `stdlib-tests/` holds behavioral tests written in
  Lyric and run by `lyric test`; the build runs them, and both repository invariants —
  formatted, and compiling in silence — cover the directory.

- **Every public item of the standard library is documented** — hover and the reference site
  answer everywhere — and a test pins completeness, not a count: new API without documentation
  is a red build.

- **A bare import that shadows a builtin type warns** (`LYR-SEM0077`): `import std.string;`
  binds the name `string`, and the annotation then names the module. The warning says the way
  out; using the shadowed name as a type is now a proper error naming the trap — previously it
  CRASHED the compiler on the local-annotation path.

### Changed

- **`@Deprecated` may sit on generic declarations** — the one exception to the
  no-attributes-on-generics rule, because its consumer is the compiler and no metadata row is
  involved; none is emitted there.

- **A static call on a generic instance substitutes the caller's type parameter**:
  `List<T>.empty()` inside your own generic function works now; previously the lowering met the
  bare `T` and failed with an internal error.

- Two parameters of `std.string.replace`/`replaceFirst` are named `replacement` (signature help
  used to show a German name); every remaining German local and section header in the library
  is English now.

## v1.11.0 — 2026-08-19

Attributes stop being decoration. One is now read by the compiler — `@Deprecated` — and one by
a new tool: `lyric test` runs every function marked `@Test`. The bytecode format stays **3.2**,
and no existing program changes meaning.

### Added

- **`@Deprecated`, the first attribute the compiler reads.** From `std.core`, on a function, a
  type or a module: every use warns (`LYR-SEM0076`) at the use site, the note points at the
  attribute, and `message` says what to use instead. Resolved by IDENTITY — a struct someone
  else names `Deprecated` deprecates nothing. Uses inside anything itself deprecated are exempt,
  so a deprecated function may keep calling its deprecated siblings; a deprecated module warns
  at the imports that pull it in. It changes diagnostics and nothing else — the same module
  compiles either way. Editors strike deprecated uses through.

  With this the compiler-read attribute set becomes part of the language contract: `@Deprecated`
  is in it, everything else stays inert.

- **`lyric test` — tests, the Go shape.** Tests live under `tests/` (or the `testRoot` your
  `lyric.json` names) and only the test runner ever compiles them; production builds never see
  a test file. A test is a top-level function marked `@Test` from the new **`std.test`**, fails
  by panicking, and runs in a **fresh instance** — module state cannot leak between tests.

  ```bash
  lyric test
  ```

  `std.test` ships the marker and the assertions: `assertTrue`, and `assertEq` over
  `[Equatable<T>, Display]`, naming both values when they differ. The report is plain text, one
  line per test; the exit code is `0` when everything passed and `1` otherwise. No `tests/`
  directory means no tests and exits 0; a testRoot named explicitly and missing is an error.
  Guide chapter 20 covers it.

  The runner is `lyrtest`, the tenth binary, and it drives the compiled module through the
  embedding API — the attribute rows for discovery, a call handle per test: the same machinery
  a host uses, now with a consumer that is not a test of it.

- **`HostOptions.SourceRoot`** in the embedding API: a host may compile a file whose imports
  resolve against a directory other than the file's own — the test runner compiling `tests/`
  against `src/` is the case that added it.

### Not in this release

- **Test filters, parallel execution, expectPanic, fixtures and setup/teardown, JSON output,
  editor test integration** — deliberately; each is an idea issue, none blocks running tests.
- **Suppressing a deprecation warning in code**: the mechanism would be another compiler-read
  attribute, and the set grows by decision. `--deny-warnings` still means what it says.

## v1.10.0 — 2026-08-19

The compiler learns to speak below "error". Four severities, warnings that matter, notes that
point at places, and a CI gate. The language, the bytecode format (**3.2**) and the embedding
API are unchanged; every program that compiled still compiles — some now hear about themselves.

### Added

- **Warnings.** A local binding, loop variable, catch binding or pattern binding that is never
  referenced (`LYR-SEM0071` — naming it `_` is the opt-out; parameters and the shorthand field
  pattern `Rect { w, h }` are deliberately exempt). An imported name nobody in the file uses
  (`LYR-SEM0072`). A statement control flow can never reach (`LYR-SEM0073`). And a static
  extension method called through an instance (`LYR-SEM0074`): that form is **deprecated** and
  becomes an error in the next major — the warning is the clock. Warnings stay silent over a
  program with errors, and never fail a build by themselves.

- **`--deny-warnings`** on `check` and `build`, for CI: the warnings keep their severity in the
  output, one closing error (`LYR-CLI0016`) carries the policy into the exit code, and a denied
  build writes no artifact. The `lyric.json` unknown-key warnings are real diagnostics now
  (`LYR-CLI0017`) and count toward the gate.

- **Notes on diagnostics.** A duplicate declaration points back at the first one, a missing
  interface method points at the member it fails (in whatever file it lives), an unknown name
  suggests the single closest candidate in scope, and an unknown member suggests from the same
  list completion offers. Rendered indented under the caret block in text — deliberately not in
  the head-line format a problem matcher reads — as a `notes` array in `--json` (only when
  present, so existing consumers read what they always read), and as related information in
  editors.

- **The first hint.** `LYR-SEM0075`: a `var` through which nothing is ever changed — no
  reassignment, no field or element write, no `mut` call, not handed over by reference — could
  be a `let`. A `var` that documents mutation keeps its `var`.

- **An error that was silent misbehavior**: two files claiming one module name is `LYR-RES0007`
  with a note at the first claim, instead of a shadow registration nothing could explain.

- **Editors draw the difference**: unused and unreachable code fades, the deprecated instance
  form is struck through, and every note is a click away. The severity `info` exists on the
  wire for what later versions will say at it.

- **Guide chapter 19** documents the contract: severities, codes as stable identifiers, the
  gate, and what warns today. The repository holds itself to it — the standard library, the
  examples and the templates check in silence, and a test keeps them there.

### Changed

- **The editor clients live in their own repositories** ([vscode-lyric](https://github.com/lyriclang/vscode-lyric),
  [jetbrains-lyric](https://github.com/lyriclang/jetbrains-lyric)) and release their
  installables there, on their own cadence. Toolchain releases v1.8.0 through v1.9.1 carried
  them beside the archives; from this release on they are found there. The project moved to the
  `lyriclang` organization.

- Two messages stopped promising futures: the duplicate-function hint lost its "in v1"
  (overloading was rejected for good in v1.5.0), and the block-lambda limit `LYR-SEM0046` now
  states the problem in the message and its way out in a note.

## v1.9.1 — 2026-08-19

The formatter reaches the editor. `textDocument/formatting` is served by the language server —
format on save works wherever the editor offers it, in VS Code and the JetBrains IDEs alike,
with no client update needed.

The answer is one whole-document edit off the buffer as it stands, an empty list when the file
already has the shape, and NO edits for a buffer that does not parse: the formatter never
writes a guess over broken text, behind the editor's gesture either. The editor's tab settings
are deliberately ignored — one shape is the tool's contract, in every surface it has.

The toolchain is otherwise unchanged.

## v1.9.0 — 2026-08-19

Two tools. `lyric pack` turns a program into one standalone executable, and `lyric fmt` gives
every Lyric file the one shape there is. The language, the standard library, the bytecode
format and the embedding API are untouched; the format stays **3.2**, and a `.lyrbc` built by
1.8.0 packs and runs unchanged.

### Added

- **`lyric pack app.lyr` — a program becomes one file.**

  ```bash
  lyric pack app.lyr
  ./app arg1 arg2
  ```

  The result is a copy of a prebuilt stub runtime with the compiled module and a 24-byte footer
  appended — a byte copy, no linker, no .NET on the target machine. The packed program owns its
  whole command line (no `--` protocol, no wrapper options), runs with every capability like
  any standalone program, exits with `main`'s return value, and its panics name your lines —
  same runtime, same bytes, same backtraces as `lyric run`.

  Two new binaries carry it: `lyrpack`, which packs a `.lyrbc` and nothing else, and `lyrstub`,
  the runtime half of a packed program. The release archives hold the platform's stub under
  `stubs/<rid>/`; a bare stub started directly explains itself instead of failing obscurely,
  and a truncated pack is reported as damaged rather than executed. The format is specified in
  [`docs/Pack.md`](docs/Pack.md), the guide's chapter 17 says what to know before shipping.
  The release pipeline packs an example and RUNS the result on Windows and Linux before an
  archive exists.

- **`lyric fmt` — the formatter.** In place for files and directories, `--check` for CI (writes
  nothing, exits nonzero when anything would change), `--stdin` for editors. No style options.

  What it keeps: every comment (trailing ones trailing), your blank lines capped at one, your
  literal spellings (`0xFF`, `1_000_000`). What it decides: line breaks against the 100-column
  limit, trailing commas exactly where the grammar allows them and only in broken layout, a
  blank line after the module header and between declarations with bodies. A file that does
  not parse is reported and left byte-for-byte untouched.

  The repository holds itself to it: the standard library, the examples and the templates are
  formatted, and a test fails when they stop being it.

### Changed

- **The standard library, the examples and the project templates are reformatted** with the
  new formatter. No signature, no name and no behaviour changed — the test suite verifies the
  reformatted sources compile to the same programs.

### Not in this release

- **Packed executables that run on macOS.** A Mach-O declares its own extent; appended bytes
  put the file beyond it, and `codesign` refuses the result — found by the release pipeline's
  own gate, recorded in [`docs/Pack.md`](docs/Pack.md). The fix is a real Mach-O segment for
  the payload, deno's route; until then macOS packs for the OTHER platforms via `--stub`.
- **Cross-platform packing sugar**: a pack is for one platform, and a foreign platform packs
  via `--stub` with that platform's stub out of its archive. No `--target` until someone
  needs it.
- **A trimmed stub**: 73.5 MB self-contained today; trimming measures 13.0 MB and survives a
  smoke test, but one smoke is not a gate. Decision material for the next scope check.
- **Capability narrowing at pack time**: a packed program runs with everything, like any
  standalone program. Narrowing is a footer field for a future minor.
- **`textDocument/formatting` in the language server**: the formatter lives in the library the
  server already uses; the wiring is a later slice.
- **Format-on-save configuration, formatter style flags**: deliberately never.

## v1.8.1 — 2026-08-18

A one-line fix for the JetBrains plugin (now 1.2.1): the TextMate bundle provider was registered
under an extension-point namespace that does not exist, so `.lyr` files rendered uncolored while
everything the language server answers worked. The point is declared by the TextMate plugin but
qualified under `com.intellij`; the registration moved there, and highlighting appears. The
toolchain itself is unchanged.

## v1.8.0 — 2026-08-18

The editors catch up with the compiler. No language change and no format change: the language
server compiles the PROJECT instead of the open buffer, answers everything an editor asks, and
two installable clients ship beside the toolchain — the VS Code extension as a `.vsix`, and a
new JetBrains plugin.

### Changed

- **The language server compiles the project, not the buffer.** Under a `lyric.json`, every
  `.lyr` file beneath the source root is one compilation: find-references works in BOTH
  directions (standing on a declaration finds the uses in files that import it), files nobody
  has open get their errors into the Problems panel, a deleted file has its squiggles withdrawn,
  and a change behind the editor — a branch switch, another tool — is picked up through file
  watches. A file outside any project is compiled from itself, as before. Measured: a 14-file
  project checks in the same time as a single file — the standard library dominates either way.

- **Find references and semantic highlighting underline the name, not the expression** — `x`
  instead of `p.x`, `Point` instead of `Point { x = 1 }`.

### Added

- **Rename** (`F2` / `Shift+F6`), project-wide: the declaration, every use, and the `import`
  clauses that carry the name. What cannot be renamed says why — the standard library, a module,
  a built-in. Whether the NEW name collides is left to the compile that follows immediately; its
  diagnostics are the conflict analysis. Applied edits recompile clean, pinned by test.
- **Workspace symbols**: every declaration of the project, searched by name.
- **Semantic highlighting**: every name colored by what the compiler resolved it to — a type in
  an annotation, an initializer and an attribute alike; `let` bindings as readonly. An
  unresolved name stays uncolored, which is the honest signal.
- **Signature help** while typing a call — the declaration as written, the active parameter
  following the commas. **Folding** with the closing line kept visible. **Inlay hints** for the
  inferred type of unannotated bindings and loop variables.
- **The VS Code extension grew up**: a restart command, a status item that shows the server
  state and version, a `lyric: build` task wired to the Problems panel, snippets — and the
  extension ships as `vscode-lyric-<version>.vsix` on every release.
- **A JetBrains plugin** (`jetbrains-lyric-<version>.zip`): the same server in CLion, IntelliJ
  IDEA, Rider and the other commercial IDEs, 2026.1 or newer — diagnostics, completion,
  navigation, rename, semantic highlighting, signature help, folding and inlay hints through
  the platform's own LSP integration. Install from disk; neither client is on a marketplace.

The interpreter stops allocating — and so does the native boundary. No language change and no
format change: the same programs compile to faster, smaller modules, run with far fewer heap
allocations, and a host SDK can now put `Vec2` in a native signature. The numbers below come
from `tools/Bench` (new in this release), Release, per operation, against v1.6.0.

### Changed

- **A function call no longer allocates.** Frames are pooled per function; a call went from
  **176 B to 0 B** and from ~50 ns to ~8 ns. Deep recursion still works, exceptions and panics
  unwind exactly as before.

- **Small functions are inlined.** A direct call to a function of roughly a dozen instructions —
  a `Vec2.add`, an iterator's `next`, a getter — is replaced by its body. Callers and callees
  with `try`/`defer` are left alone, recursion stays a call, and a function that always throws
  keeps its frame.

- **Objects that never leave their function are dissolved into locals.** A `Vec2` built, read
  and assigned inside a loop costs **0 bytes**: construction plus method call went from
  352 B / 271 ns to **0 B / 8 ns**, the operator form (`a + b`) from 352 B / 252 ns to
  **0 B / 6 ns**. This is what makes vector arithmetic through the v1.5.0 operators usable in a
  per-frame game loop.

- **`for-in` no longer allocates its iterator.** A range or array loop runs at **0 B per
  element** (208 B before); a range loop is ~3× faster than in v1.6.0. The `Iterable` route
  through an interface is devirtualized where the concrete iterator is provable — a
  `Set`/`Map` loop now calls its `next` directly instead of dispatching per element.

- **Modules got smaller.** A function whose every call was inlined is removed; the six-function
  `examples/arith.lyr` compiles to two. An attributed function always survives — the row is a
  promise to the host.

- **A panic in an inlined function names the caller's frame with the callee's line.** The line
  is right, the frame above it is gone — the trade every optimizing compiler makes. A
  deliberate `panic(...)` keeps its full backtrace, because a function that never returns is
  not inlined.

### Added

- **The native boundary takes and returns value structs, without allocating.** A native
  signature may use a `struct` declared in the same native module (scalar and string fields
  only). The declaration stays fully typed on the script side; on the wire it is flattened:

  ```lyr
  module engine.geo;

  pub struct Vec2 { x: float, y: float }

  pub fn setPosition(entity: int, at: Vec2);
  pub fn positionOf(entity: int): Vec2;
  ```

  A struct parameter crosses as its fields — the host registers the delegate it would have
  written for scalars. A struct return comes back through a buffer the runtime owns
  (`NativeRegistry.RegisterStructReturning`): the implementation fills one value per field, the
  script sees an ordinary value, and value semantics keeps the shared buffer invisible. Layout
  disagreements between host and SDK are load errors with the import's name in them.

  Measured: a `Vec2` built fresh and passed in, or received back, costs **0 B per call** —
  the answer to the embedding question that produced this milestone (Erato's `positionOf`).

- **A native call no longer allocates its argument array.** The `LyrValue[]` handed to an
  implementation is pooled and reused; a one-argument crossing went from 40 B to 0 B, a
  four-argument one from 88 B to 0 B. The array is therefore a LOAN: read it during the call,
  copy values out, never store it — documented on `NativeRegistry`, and every implementation in
  the standard registry already complied.

- **`tools/Bench`** — the in-process measurement harness behind all numbers above:
  `dotnet run -c Release --project tools/Bench`. Allocated bytes and nanoseconds per operation,
  scalar-loop baseline subtracted, round-robin against JIT tiering, raw-registry boundary
  probes.

### Not in this release

- **Struct returns through the embedding layer's delegates.** `RegisterStructReturning` is a
  `NativeRegistry` surface; a `LangVm.RegisterNative` overload that marshals a C# struct is
  sugar for a later release — the raw form is the one a game host uses anyway.
- **Structs from other modules in native signatures** — the struct must live in the module that
  declares the native, which is where an SDK's value types belong.
- **Escape analysis across surviving calls** — a struct passed to or returned from a *Lyric*
  function that stays a function still allocates.
- **The remaining optional ops in `for-in`**: a range loop still runs ~1.9× a hand-written
  `while`; the gap is `optsome`/`optissome`/`optget` and block hops, peephole material.
- **Value structs as a language feature.** Deliberately: a `struct` already HAS value
  semantics; this release makes the representation keep that promise everywhere it matters,
  with no new mechanism.

## v1.6.0 — 2026-08-18

Attributes. A program can say things about itself that a tool outside it can read — which
functions a host should call, what a script-declared type looks like, what a module is. The
bytecode format goes **3.1 → 3.2**; both new sections are skippable, so a 1.5.0 runtime loads a
1.6.0 module and runs it unchanged.

### Added

- **Attributes, on a function, a type and the module header.** An attribute is a struct type;
  where it may sit is the marker interface it declares — `OnModule`, `OnType` or `OnFunction`, all
  new in `std.core`:

  ```lyr
  import std.core { OnType, OnFunction };

  pub struct Component :: [OnType] { }
  pub struct System :: [OnFunction] { order: int = 0 }

  @Component
  pub struct Health { value: int, max: int }

  @System { order = 10 }
  pub fn damageTick(dt: float): void { }
  ```

  Conformance decides, not the name — no struct becomes an attribute by accident, the same nominal
  rule the operators follow. The arguments are the struct initializer restricted to literals; a
  field the use does not write carries the field's literal default, and a field with neither is an
  error at the use site, not a hole in the metadata.

  **An attribute describes; it does nothing.** No attribute in this release is read by the
  compiler, and a runtime that ignores them runs the program unchanged.

- **Bytecode format 3.2.** Section 11 holds the rows — target, attribute type, one value per field
  in field order, always complete. Section 12 holds field names, ONLY for types a row references:
  everywhere else the rule stands that field names are not in the bytecode, but a host reading
  `@Component struct Health` needs `value` and `max`, or it has learned a shape it cannot name.

  An attributed function survives dead-code elimination: the row is a promise that the index is
  valid, and the host is a caller the reachability analysis cannot see — the same standing as the
  entry point.

- **The embedding API reads the rows.** `ScriptModule.Attributes` answers **before**
  `Instantiate` — for foreign bytes, the module row is how a host decides whether to load at all.
  A hit is a call handle: `instance.CallVoid(use, …)` calls by the index the row carries, so a
  typo in a script is a compile error instead of a function nobody finds. `FieldsOf` yields the
  named, typed shape of an attributed type.

- **The tools show them.** `lyric disasm` prints each row with its field names, `lyrvm info`
  counts them, and hovering `@System` in an editor answers with the struct.

### Fixed

- **A duplicate field in a struct initializer crashed the compiler.** `P { x = 1, x = 2 }` passed
  the type checker and died in the lowering with an internal exception instead of an error
  message. It is a diagnostic now (`LYR-SEM0070`), reported at each repeated field, in struct and
  class initializers and in enum struct-variant initializers alike. Found while building the
  attribute checks, which validate their arguments the same way.

### Changed

- **`@name` at declaration position is no longer "attributes arrive later".** It parses; what the
  name resolves to is the sema's question, so `@test` is now `unknown type 'test'` instead of
  `LYR-PAR0038`. That code stays on parameters, where attributes remain rejected, with a message
  that no longer promises the future. The reserved expression form `@name(args)` leaves the
  grammar; `LYR-SEM0053` now says an attribute is not an expression.

### Not in this release

- **Attributes on parameters, fields and members** — top-level declarations and the module header
  only.
- **Attributes the compiler reads** (`@Deprecated`, `@MustUse`, `@Inline`): the moment one
  attribute changes compilation, the attribute set becomes part of the language contract and the
  stability promise. That is a separate decision, deliberately not smuggled in here.
- **Runtime application**, Python-decorator style: there is no mechanism by which an attribute
  wraps or replaces its target.
- **Qualified attribute names**: names are the bytecode's type names and therefore unqualified.
  An SDK owns its attribute names the way it owns its native names.
- **Completion after `@`.**

## v1.5.0 — 2026-08-18

Operators on your types. Everything resolves through the one mechanism this language has for
polymorphism — the interface a type declares — so there is no operator declaration syntax, no new
opcode, and the `.lyrbc` format stays **3.1**.

### Added

- **`==` and `!=` on every type conforming to `Equatable<T>`.** The operator *is* the method:
  `a == b` calls `a.equals(b)`, and `a != b` negates it.

  ```lyr
  struct Point :: [Equatable<Point>] {
      x: int,
      fn equals(other: Point): bool { return this.x == other.x; }
  }

  let same = a == b;
  ```

  Conformance is required, not the method alone: a type with an `equals` nobody declared as
  `Equatable` stays rejected, so no method becomes an operator by accident of its name.

- **`<`, `<=`, `>` and `>=` on every type conforming to `Ordered<T>`** — one `compare` method,
  negative/zero/positive, and all four operators read its sign. **`string < string` works**, through
  the conformance the standard library has carried since v1.0; its rejection had promised exactly
  this change.

- **`+`, `-`, `*` and `/` on types conforming to `Add<T>`, `Sub<T>`, `Mul<T>` and `Div<T>`** — four
  new `std.core` interfaces, one method each, homogeneous: `T op T` gives `T`. The built-in numerics
  conform, `string` to `Add` alone, so a generic function constrained on `Add<T>` serves an `int`, a
  `string` and your vector type in one program.

- **`as` beyond the numerics converts through `Into<T>`.** `x as T` is `x.into()` where the
  operand's type declares the conformance. Explicit only, one target per type, total conversions
  only — a conversion that can fail belongs in a named function returning an optional. The numeric
  casts keep their opcodes and are not overridable.

- **`s *= 3` and `xs *= 2` work.** The repetition overloads of `*` had no compound form — an
  accident of how compounds were checked, recorded as a limit. The compound check rework below
  delivered them.

### Fixed

- **A compound assignment never checked its operator.** `p += p` on a struct passed the compiler and
  produced an integer addition of two references at runtime — the `s += "x"` class of bug fixed in
  v1.1.0, one type over. `s &= s` and `f <<= f` passed the same way. A compound is now typed as the
  binary it carries: whatever `a = a + b` says, `a += b` says too.

### Not in this release

- **Heterogeneous operands** (`Vec2 * float`): needs a two-parameter interface and a rule for
  multiple conformances to one generic interface.
- **Compound assignment through the operator interfaces** (`v += w` on a `Vec2`): the compound
  lowering evaluates the target's address once and cannot yet route through a call. The diagnostic
  says to write `v = v + w`.
- **`%` on user types**, and unary `-`: no interface exists for either, deliberately.
- **A conversion out of a builtin** (`extend int :: [Into<Cents>]`): the orphan rule stops it, and
  the rule does not look into type arguments. A named function takes its place.
- **Method overloading**, considered and rejected: constraints plus generics are this language's
  overloading, and the standard library says so itself.

## v1.4.0 — 2026-08-17

Completion, and a standard library that says what it does. The language, the command line, the
embedding API and the `.lyrbc` format are untouched; the format stays **3.1**.

### Added

- **Completion.** After a `.` the members of what stands before it; anywhere else the names in
  scope.

  ```lyr
  let p = Point { x = 1 };
  p.          // x, y, and every method, extension and interface default the type has
  ```

  The member list is the one the compiler would accept, not an approximation of it: extension methods
  and interface default methods are in it, which matters because **every string method of this
  standard library is an extension** — a list without them would be empty on a `string`.

  In scope: locals, parameters, type parameters, what the module declares and imports, and the
  builtins. Inner names shadow outer ones, a binding is not offered inside its own initializer, and a
  loop variable is not offered in the loop head.

  Each item carries its kind and, when the declaration has one, the `///` block above it.

  It works while the file does not parse, which is the state it is asked in. The trigger character
  is `.`; everything else is the editor asking on its own.

- **The standard library documents itself where it did not.** `std.io.console`, `std.core` and
  `std.option` held 33 public declarations and no documentation at all, so hovering `println` showed
  a signature and nothing else. All three are written now, interface members included.

### Fixed

- **A struct initializer is a reference to its type.** Asking for the definition of `Point` in
  `let p = Point { x = 1 };` used to answer nothing, and find-references did not list it. Both do
  now, and hovering it reports the type.

  v1.3.0 listed this under *Not in this release* because recording it made the type checker read
  `Pair<int> { a = 6 }.a` as a static member access. The receiver question is answered from the
  expression's type now rather than from that table, so the two no longer collide.

### Not in this release

- **Completion does not offer keywords.** `if`, `return` and the rest are not symbols; an editor gets
  them from the grammar it highlights with.
- **Completion after `import` does not offer module paths.** That is a different source — the file
  system — and not the scope.
- **A field reference still marks the whole member access**: asking for references to `x` marks
  `p.x`. Use sites carry no span for their name alone.
- **References and completion stop at the compilation.** The server compiles the file you are in, so
  another file of your project that imports it is not searched.

## v1.3.1 — 2026-08-17

### Fixed

- **Eight diagnostics pointed at a document that does not exist.** Five named `Sprache.md`, which has
  been [`docs/Grammar.md`](docs/Grammar.md) for some time, and the sections they cited were wrong as
  well — §10 and §11 of a document that has seven. Following either reference led nowhere twice:

  ```
  attributes are not part of v1 (Sprache.md §10); '@test' and 'lyric test' arrive after v1.0
  ```

  ```
  attributes are not part of v1; '@test' and 'lyric test' arrive later
  ```

  Rather than repair the citations, they are gone. **A diagnostic names what is wrong, not where to
  read about it** — a citation ages in two ways at once, and both had already happened here. Where a
  reference carried information (`§11 allows none or one 'string[]'`), the message now says it
  outright.

  The affected codes are `LYR-PAR0038`, `LYR-PAR0039`, `LYR-SEM0053`, the lowering's *main* check,
  the bytecode reader's global check and two entry-point findings of the IR verifier. **No code
  changed and no behaviour changed** — only the wording, so a program that compiled still compiles
  and one that did not still fails, with the same code on the same span.

## v1.3.0 — 2026-08-17

Everything in this release is the language server. The language, the standard library, the command
line and the `.lyrbc` format are untouched; the format stays **3.1**.

### Added

- **Hover shows the documentation you wrote.** A `///` block above a declaration appears under its
  signature, for declarations in the file you are editing and in every module it reads:

  ```
  fn cpuCount() -> int

  How many cores the machine has, for programs that split their work. The VM itself is
  single-threaded.
  ```

  The text goes through unchanged — there is no doc-comment vocabulary in the grammar, so nothing is
  interpreted, and nothing is composed from the signature. A declaration without a block is shown
  exactly as before.

- **An editor can show the outline of a file** (`textDocument/documentSymbol`). Types carry their
  fields, methods, variants and static constants as children; imports, parameters and locals are
  left out, because an outline says what a file offers.

  It reads the syntax and resolves nothing, which is why **a file with type errors still has an
  outline** — the moment you most want one.

  Only the nested form is produced. An editor that does not announce
  `hierarchicalDocumentSymbolSupport` gets no outline rather than the deprecated flat one.

- **Find all references** (`textDocument/references`), with or without the declaration itself. The
  answer covers the program reachable from the file you are in, so a call into the standard library
  is found in the module that declares it.

### Changed

- **Go to definition selects the NAME of a declaration**, not the start of it. Previously the cursor
  landed on the first character of the whole declaration — on `for` for a loop variable, on `catch`
  for a catch binding. An editor that announces `linkSupport` now also receives the full extent of
  the declaration beside the name, so a peek window shows the declaration and puts the cursor on its
  name.

### Fixed

- **Go to definition on a struct initializer no longer jumps somewhere else.** In
  `let p = Point { x = 1 };`, asking about `Point` used to land on `p` — the enclosing binding, which
  is not what the cursor was on. It now answers with nothing. What it *cannot* yet do is answer with
  `Point`; see below.

### Not in this release

- **A struct initializer is not a reference to its type.** `Point { … }` is bound to no symbol, so
  neither find-references nor go-to-definition sees it. An annotation (`let p: Point`) is found.
- **A field reference marks the whole member access**: asking for references to `x` marks `p.x`, not
  the `x` in it. Use sites carry no span for their name alone.
- **References stop at the compilation.** The server compiles the file you are in; another file of
  your project that imports it is not part of that compile, and its uses are therefore not listed.
- **No completion.** It is the first question asked at a position where the text does not parse,
  which is a compiler topic rather than another editor feature.

## v1.2.0 — 2026-08-17

### Changed

- **Only a field needs the `,` that separates members of a struct or class.** A bodiless method used
  to need a semicolon *and* a comma in a row:

  ```lyr
  class Builder {
      fn addExecutable(entry: string, output: string): Artifact;,   // no longer
      fn addTest(entry: string): Artifact;
  }
  ```

  The rule is strictly more permissive, so every file that was valid stays valid — the comma is still
  accepted where it is now optional. Two fields still need one, or `a: int b: int` would read as a
  single field of a type nobody wrote.

### Added

- **`lyric new` writes a project that builds.**

  ```bash
  lyric new myapp          # lyric.json, build.lyr, .gitignore, src/main.lyr
  lyric new mylib --lib    # lyric.json, src/mylib.lyr — nothing to build
  ```

  Two shapes and two flags rather than a template system: with two variants a discovery mechanism is
  more machinery than content. The name becomes a module name, so it has to be one, and an existing
  directory that holds something is refused rather than merged into.

  The templates are embedded in the binary, so nothing can go missing beside it — and they are real
  `.lyr` files in the repository, which the test suite compiles. `__name__` is a valid Lyric
  identifier, so a template is compilable Lyric rather than text with holes in it.

  It is the one command the driver runs itself: it writes files and compiles nothing.

- **A project may be built by a script, `build.lyr`.** `lyric build` without a file argument runs it
  and compiles what it declares:

  ```lyr
  import std.build { addExecutable };

  pub fn build() {
      let app = addExecutable("src/main.lyr", "out/app.lyrbc");
      app.sourceMap(false);

      addExecutable("tools/mktex.lyr", "out/mktex.lyrbc");
  }
  ```

  Every artifact is compiled whole, from its entry file; there is no link step and nothing is shared
  between two of them but the source on disk. `lyric build` **with** a file still means "compile this
  file" and is unchanged.

  Nothing is compiled while the script runs — it collects, and the compiles happen once `build` has
  returned. That is why an option set on the following line still applies, and why a source file the
  script generates is finished before anything reads it.

  It is a Lyric program with the whole standard library and every capability, so it may write files
  and start processes. Relative paths in it resolve against the directory holding `build.lyr`, not
  against the directory the build was started from. **`lyric build` in a repository you did not write
  runs code you did not write**, as `make` and `cmake` do.

  New binary `lyrbuild`, the second after `lyrrepl` that holds both the front end and the runtime: a
  build script has to run, and what it collects has to be compiled afterwards.

- **A project may say where its modules are, in a `lyric.json`.**

  ```json
  {
    // where our own modules live
    "sourceRoot": "src",
    "nativeRoots": { "engine": "sdk" },
  }
  ```

  `sourceRoot` replaces "the directory of the entry file" as the module root, and `nativeRoots` maps
  a module path segment to a directory whose modules may declare functions without a body. The file
  is searched for upwards from the file being compiled, and comments and trailing commas are allowed
  in it.

  **This closes the gap v1.1.0 shipped with**: `lyric check` and `lyric build` now see the native
  roots a host declares, so a script written against an SDK no longer compiles in the host and fails
  on the command line.

  Both keys are optional and **without the file nothing changes** — that is what makes it an addition
  rather than a new requirement. A key nobody knows is a warning rather than an error, so a file
  written for a later version still loads.

  It is read and never executed, which is what lets an editor learn a project's layout without
  running anything from it.

- **The language server follows a program across its files.** Editing a module now refreshes the
  diagnostics of every open file that imports it, and a dependency is read from the editor's buffer
  rather than from its last save. Both halves are needed: an overlay nobody re-reads shows nothing,
  and a cascade over stale text refreshes to the same answer.

  What a file depends on is taken from the compilation itself, not from the imports in its text —
  the resolver already followed them, transitively and through the project's roots, and a second
  answer to that question would be the one that is wrong.

  The cascade goes one level. Two modules may import each other, which is a diagnostic rather than a
  crash, so a transitive one would not terminate.

  `CompilerOptions.SourceOverlay` is the seam, and it is not editor-specific: it says "compile as if
  these files held this text", which a host embedding the compiler can use for the same reason.

- **The language server reads `lyric.json`.** An import of a host SDK no longer shows as an unknown
  module in an editor while the same script runs correctly in the host — the second half of what
  v1.1.0 listed as not in it.

  A broken project file does not stop the analysis. The editor keeps getting diagnostics, resolved
  by the plain rules, and the reason goes to the client's log; publishing nothing would leave an
  earlier state on screen with no hint that anything happened. The message names the project file
  rather than appearing as an error inside the file being edited, and it is said once per change
  rather than once per keystroke.

  **Still not there**: editing a module does not refresh the diagnostics of the file that imports
  it. The server analyses one buffer at a time.

## v1.1.0 — 2026-08-15

Bytecode format **3.1**. A minor of the format may only add skippable sections, so a 1.0 runtime
reads a module built by this release and a 1.1 runtime reads one built by 1.0 — with one caveat
below.

### Added

- **A host may ship its API as `.lyr` files instead of generating it.** `HostOptions.NativeRoots`
  names directories whose modules may declare functions without a body, keyed by the module path
  segment they own, and `LangVm.RegisterNative` supplies the implementations under the same qualified
  names. Until now every host function went through `RegisterFunction`, which derives the declaration
  from the delegate — right for a handful, and for an SDK it means the same signature lives in the
  C# call and in whatever documents the API.

  Whether a module may declare a native follows the ROOT it came from, never its content, so naming
  a file well enough is not a way into the host. A module in such a root may hold ordinary Lyric code
  beside its declarations.

- **A program may consist of several files.** A module path becomes a file path under the directory
  of the entry file: `import shapes.circle` reads `shapes/circle.lyr` beside it. Until now only the
  standard library could be imported, so every program was one file.

  Three rules come with it. A file must agree with the path it was loaded from, or the header is an
  error — previously such a file registered under the name its header claimed and the import that
  pulled it in reported *cannot find module* about a file it had just read. `std` resolves against
  the standard library alone, so nothing beside your program can take its place. And only standard
  library modules declare functions without a body; in your own modules a missing body is a compiler
  error rather than a failure at load time.

  Everything still compiles into one `.lyrbc`. There is no separate compilation step per file.

- **A panic names the line it happened on**, not just the function:

  ```
  panic [LYR-VM0002]: division by zero
      in main.divide (app.lyr:3)
      in main.main (app.lyr:8)
  ```

  The innermost frame points at the instruction that failed, every frame below it at the call it was
  waiting on.

- **The SourceMap section of the bytecode format now has a payload.** It was reserved and named in
  3.0 and never written. It maps a byte offset in a function's code to a file and a line, one row per
  position change.

- **`lyrc build --no-source-map`** leaves the section out. Without it the file is byte for byte what
  the same build produced before the section existed, so stripping costs nothing else. The section is
  written by default: the moment a line number is wanted is the moment nobody planned for it.

  Paths are stored relative to the entry file's directory, and a file outside it — the standard
  library sits beside the toolchain — keeps its bare name. Nothing absolute reaches the file, so a
  module does not carry the directory layout of the machine that built it.

### Fixed

- **`s += "x"` on a string silently produced the empty string.** `+` on a `string` is a call to
  `std.string.concat` and on an array an `arrcat` instruction, but the compound forms emitted a bare
  `add` with the operand type next to it. Nothing rejected that in a release build, and the runtime
  read the two strings as integers, so the variable ended up empty and the program kept running:

  ```lyr
  var line = "";
  line += "0F ";     // line was "" afterwards, not "0F "
  ```

  Affected were a local, a captured variable and a coroutine local. On an **array** the same
  instruction produced a value with no reference, and the next access to it ended the process with a
  host exception instead of a panic. A field (`obj.s += "x"`) and an array element
  (`xs[0] += "x"`) were reported as `LYR-IR0001` rather than miscompiled, and now work as well.

  `s = s + "x"` was correct throughout and is unchanged. **Existing `.lyrbc` files are unaffected**:
  the format and its specification were right, the compiler was not.

  `s *= 3` and `xs *= 2` stay rejected — a separate rule in the type checker demands that the right
  operand be assignable to the left, which does not hold for repetition. `s = s * 3` works.

- **A runtime accepted an arithmetic opcode with a type it cannot compute on.** §5 of the format says
  `add` through `rem` require a numeric type; the reader checked indices only and never the type tag,
  so a module carrying `add string` passed `lyrvm verify` and ran. That is why the bug above could
  reach an output at all — the IR verifier that does catch it runs in debug builds only. Such a
  module is now rejected at load time with `LYR-BC0005`.

- **A reader rejected a section id it did not know**, with `LYR-BC0003`, instead of skipping it. That
  is the mechanism the format's forward compatibility rests on, and it had never run, because nothing
  had ever written an unknown section.

  **This is the caveat above**: a 1.0.1 runtime cannot read a module carrying a SourceMap, even
  though the format says it must. Building with `--no-source-map` produces a module those runtimes
  accept.

### Not in this release

- **The command line does not know native roots.** `HostOptions.NativeRoots` reaches the compiler
  through the embedding API alone, so `lyric check` and the language server report an unknown module
  for an import a host resolves at runtime. Scripts written against an SDK run correctly and look
  wrong in an editor.
- **The language server does not know multi-file programs.** It compiles the buffer it was given, so
  editing `util.lyr` does not refresh the diagnostics of the `app.lyr` that imports it. Reopening or
  editing the importing file does.

Both need a place where a project says what it consists of, and putting that on the command line
would make a third place where the layout is written down.

## v1.0.1 — 2026-08-14

### Fixed

- **A module with both a module-level `let` and a `try`/`catch` compiled to a file that would not
  load.** The compiler wrote the Globals section (id 10) ahead of the Handlers section (id 9), and
  section ids must ascend strictly, so `lyric run` and `lyrvm verify` rejected the compiler's own
  output with `LYR-BC0005`. `lyric check` and `lyric build` reported success beforehand, which is
  what made it look like a runtime problem rather than an emitter one.

  Only a module carrying both sections was affected; either one on its own was written correctly and
  is unchanged. No `.lyrbc` file that used to be valid changes — the format and its specification
  were already right and the writer was not, so the bytecode format stays **3.0**.

## v1.0.0 — 2026-08-14

The first release with a compatibility promise. Everything below describes the state it ships, not
a change against v0.9.0: there is no earlier entry to compare against.

From here on the `.lyrbc` format and the language carry the promise the versioning describes: a
minor may add, a major may break.

### Language

The whole grammar in [`docs/Grammar.md`](docs/Grammar.md) compiles and runs: functions, structs and
classes, enums with `match`, interfaces with default methods and `::` conformance, generics with
constraints, optionals, exceptions with `throws` and `defer`, closures, coroutines, modules, and
`extend` blocks on own and primitive types.

Fixed against v0.9.0, each of them a case that used to be refused or to fail late:

- An **argument position** now carries an expected type, so `f(Opt.Some(5))` names its instance
  instead of requiring `f(Opt<int>.Some(5))`.
- A **generic struct initializer** takes its instance from the surrounding type:
  `let p: P<int> = P { v = 1 }`. Written type arguments still win, and there is still no inference
  from the field values.
- A **`type` alias** works in every position — as a return type and a field type too, not only as a
  parameter type and a local annotation.
- **`static fn`** is allowed in an enum and an `extend` body. An interface member stays non-static:
  it is reached through a vtable slot, which takes a receiver.
- A **cyclic type alias** (`type A = B; type B = A;`) is a diagnostic instead of ending the compiler
  process.

### Toolchain

- `lyric` (driver), `lyrc` (compiler), `lyrvm` (runtime), `lyrrepl` (interactive prompt), and
  `lyrembed.dll` for a C# host.
- **`lyric check` answers the same question as `lyric build`.** It used to stop after type checking
  and report `ok` for programs the backend could not express.
- Releases ship a **self-contained archive per platform** (`win-x64`, `linux-x64`, `osx-arm64`) that
  runs without a .NET install.

### Documentation

- A static documentation site generated by `tools/DocGen`: the guide, both specifications, and a
  standard library reference generated from the `.lyr` signatures. One frozen directory per version.

### Not in this release

- No interface inheritance; require both interfaces side by side.
- No operator overloading, so `==` and `<` on user types stay ordinary methods.
- No attributes (`@test` and the rest).
- The source map section of the bytecode format is reserved but not written, so a panic names the
  function rather than the line.
