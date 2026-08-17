# Changelog

This file starts at v1.0.0. Before it there was no compatibility promise to describe — neither for
the language nor for the `.lyrbc` format — and a changelog written under those conditions records
churn rather than change. The pre-1.0 releases carry their notes in their annotated tags.

Versions follow `vMAJOR.MINOR.PATCH`, as described in [README](README.md#versioning). Each entry
lists what changed **for someone using the toolchain**: the language, the standard library, the
bytecode format, the command line and the embedding API. Compiler internals are in `git log`.

---

## v1.2.0 — unreleased

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
