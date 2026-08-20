# Lyric

A statically typed, GC-managed application language with an embeddable bytecode VM.

![CI](https://github.com/lyriclang/lyric/actions/workflows/ci.yml/badge.svg)

Source files use `.lyr`, compiled modules use `.lyrbc`.

## Status

The compiler, the bytecode VM and the standard library work end to end; every construct in
[`docs/Grammar.md`](docs/Grammar.md) compiles and runs. From v1.0 the language and the `.lyrbc`
format carry the promise the versioning describes: a minor may add, a major may break.

Current version: **2.3.0**, bytecode format **3.3**.

## Targets

- **Standalone applications** — CLI tools, desktop applications, servers.
- **Embedded scripting** — the VM as a library in a C# host, with capability-gated access to
  files, network and OS.

## Example

```lyr
import std.io.console { println };
import std.collections { List };

enum Shape {
    Circle(float),
    Rectangle(float, float);

    fn area(): float {
        return match (this) {
            Circle(r)       => 3.14159 * r * r,
            Rectangle(w, h) => w * h,
        };
    }
}

fn main(): int {
    let shapes = List<Shape>.empty();
    shapes.push(Shape.Circle(2.5));
    shapes.push(Shape.Rectangle(3.0, 4.0));

    for (s in shapes) {
        println(f"area = {s.area():N2}");
    }
    return 0;
}
```

```
area = 19.63
area = 12.00
```

The [`examples/`](examples/) directory has 22 programs; the test suite runs every one of them.

## Requirements

.NET 10 SDK.

## Build and run

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project src/Lyric.Cli -- run examples/hello.lyr
```

Publish the toolchain into one directory:

```bash
dotnet msbuild build/publish.proj
```

The output lands in `artifacts/publish/`. Pass `-p:PublishRoot=<dir>` to publish elsewhere; the
target directory is wiped first. What the toolchain itself contributes, and nothing else:

```
lyric.exe  lyrc.exe               driver and compiler
lyrvm.exe  lyrrepl.exe            runtime and interactive prompt
lyrbuild.exe                       build runner, for a build.lyr
lyrpack.exe                        packs a module into one executable
lyrfmt.exe                         formatter
lyrtest.exe                        runs a project's @Test functions
lyrls.exe                          language server, for editors
lyrdbg.exe                         debug adapter, for editors
lyrcore.dll                        diagnostics and the bytecode reader
lyrfe.dll                          lexer through emitter
lyrrt.dll                          interpreter
lyrembed.dll                       host API
lyrlsp.dll                         language server protocol
lyrdap.dll                         debug adapter protocol
*.runtimeconfig.json               framework version to load
stdlib/                            standard library, as .lyr source
stubs/<rid>/lyrstub.exe            what lyrpack packs a program into
```

`lyrvm.exe` ships neither `lyrfe.dll` nor `stdlib/`: a runtime consumes bytecode, not source.

Without a runtime identifier this is a portable build of about 1.6 MB that needs a .NET 10 runtime
on the target machine — the quick form for local work. Naming one produces a build **for** that
platform which brings its own runtime, about 79 MB, and runs straight out of the archive:

```bash
dotnet msbuild build/publish.proj -p:Rid=linux-x64
```

That is what a release ships, one archive per platform.

## Binaries

| Binary | Role |
|---|---|
| `lyric` | Driver: `run`, `build`, `pack`, `fmt`, `test`, `check`, `disasm`, `repl` — dispatches to the tools below |
| `lyrc` | Compiler: `build`, `check`, and the `lower`/`parse`/`tokenize` dumps |
| `lyrvm` | Runtime: `run`, `disasm`, `verify` on `.lyrbc` |
| `lyrrepl` | Interactive prompt |
| `lyrbuild` | Runs a `build.lyr` and compiles what it declares |
| `lyrpack` | Packs a compiled module and the stub runtime into one standalone executable |
| `lyrfmt` | The formatter: in place, `--check` for CI, `--stdin` for editors — no style options |
| `lyrtest` | Runs every function marked `@Test` in the project's test root, one fresh instance per test |
| `lyrls` | Language server over stdio, started by an editor: diagnostics, hover, go to definition, outline, find references, completion, formatting |
| `lyrdbg` | Debug adapter over stdio, started by an editor: breakpoints, stepping, stack and variables |

`lyrembed.dll` is the host library: compile and run Lyric from C#.

```
$ lyric repl
Lyric 2.3.0 — :help for commands, :quit to leave
lyr> let x = 5
lyr> x * 2
10
```

Declarations persist across entries; statements run once. `:list` shows the session, `:reset`
clears it.

`.lyrbc` is a specified format, so a third-party runtime can replace `lyrvm`. Point the driver at
it with `lyric run app.lyr --vm ./their-runtime`, or set `LYRIC_VM`.

## Repository layout

```
lyric/
├── src/
│   ├── Lyric.Core/       → lyrcore.dll   diagnostics, source manager, bytecode reader
│   ├── Lyric.Frontend/   → lyrfe.dll     lexer, parser, resolver, sema, IR, emitter
│   ├── Lyric.Vm/         → lyrrt.dll     interpreter
│   ├── Lyric.Embedding/  → lyrembed.dll  host API
│   ├── Lyric.Lsp/        → lyrlsp.dll    language server protocol and analysis
│   ├── Lyrc/             → lyrc.exe
│   ├── Lyrvm/            → lyrvm.exe
│   ├── Lyrrepl/          → lyrrepl.exe
│   ├── Lyrbuild/         → lyrbuild.exe
│   ├── Lyrls/            → lyrls.exe
│   ├── Lyric.Dap/        → lyrdap.dll    debug adapter protocol
│   ├── Lyrdbg/           → lyrdbg.exe    debug adapter, for editors
│   ├── Lyrpack/          → lyrpack.exe   packs a module into one executable
│   ├── Lyrstub/          → lyrstub.exe   the runtime half of a packed program
│   ├── Lyrfmt/           → lyrfmt.exe    formatter
│   ├── Lyrtest/          → lyrtest.exe   runs a project's @Test functions
│   └── Lyric.Cli/        → lyric.exe
├── stdlib/               standard library, written in Lyric
├── tests/                xUnit test projects
├── examples/             22 example programs, plus embedded-host/
├── build/                publish.proj
├── tooling/              textmate/ — the editor grammar, pinned against the lexer by the tests
├── tools/                DocGen, the documentation site generator
└── docs/                 specifications and documentation sources
```

## Documentation

| Document | Contents |
|---|---|
| [`docs/guide/`](docs/guide/) | User guide — start here to learn the language |
| [`docs/Grammar.md`](docs/Grammar.md) | Formal grammar |
| [`docs/Bytecode.md`](docs/Bytecode.md) | Formal `.lyrbc` format specification |
| [`docs/Pack.md`](docs/Pack.md) | The pack format: how a program becomes one executable |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Contribution rules and process |
| [`CHANGELOG.md`](CHANGELOG.md) | What changed per release, from v1.0.0 on |

Every Lyric snippet in the guide is compiled by the test suite.

These sources are also the input of the documentation site. `tools/DocGen` renders the guide, both
specifications and a standard library reference generated from the `.lyr` signatures:

```bash
dotnet run --project tools/DocGen -- site . artifacts/site nightly
```

One directory per version, side by side; a release is frozen once written. `.github/workflows/docs.yml`
publishes `nightly` after a green nightly build and a `vX.Y.Z` directory on a release tag.

## Versioning

From v1.0 the project follows semantic versioning with three components, `vMAJOR.MINOR.PATCH`:

| Component | Increments on |
|---|---|
| MAJOR | incompatible language, standard library or bytecode format change |
| MINOR | backwards-compatible additions |
| PATCH | backwards-compatible fixes |

Before v1.0 the bytecode format may change incompatibly with a major bump of its own version,
which is independent of the toolchain version.

## Branches

| Branch | Purpose |
|---|---|
| `main` | Always green. Every commit passes CI on Linux and Windows. |
| `feature/<name>` | New work. Merged into `main` through a pull request. |
| `fix/<name>` | Corrections. Same process. |

CI runs on `main`, on `feature/**` and `fix/**`, and on every pull request against `main`.

## Releases

Two channels:

- **Stable** — created by pushing an annotated tag `vX.Y.Z`. The release workflow verifies on
  Linux and Windows, packages a self-contained build for `win-x64`, `linux-x64` and `osx-arm64`,
  and publishes the archives as a GitHub release. Each one runs without a .NET install.
- **Nightly** — built from `main` once a day and published as the `nightly` prerelease. The
  `nightly` tag moves to the commit that was built. No compatibility promise.

The editor clients live in their own repositories and release on their own cadence:
[vscode-lyric](https://github.com/lyriclang/vscode-lyric) (the `.vsix`) and
[jetbrains-lyric](https://github.com/lyriclang/jetbrains-lyric) (the plugin zip). Toolchain
releases v1.8.0 through v1.9.1 carried both beside the archives; from here on they are found
there.

## License

[MIT](LICENSE)
