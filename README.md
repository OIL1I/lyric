# Lyric

A statically typed, GC-managed application language with an embeddable bytecode VM.

![CI](https://github.com/OIL1I/lyric/actions/workflows/ci.yml/badge.svg)

> **Status: pre-alpha, but it runs.** The compiler, the bytecode VM and the standard library
> work end to end — every construct in [`docs/Sprache.md`](docs/Sprache.md) compiles and
> executes. There is an interactive prompt (`lyric repl`) and a VS Code extension. What is
> missing before v1.0 is the embedding API (milestone M10).
>
> Do not depend on it yet: `.lyrbc` has no compatibility promise before v1.0 (ADR-013), and the
> language may still change where the spec turns out to be wrong.

## What is Lyric?

Lyric is designed for two use cases that share the same language and runtime:

- **Standalone applications** — CLI tools, desktop apps, servers.
- **Embedded scripting** — drop the VM into a C# host (game engine, editor,
  build tool) and let users write scripts with controlled capabilities.

The design borrows from C# (familiar surface syntax), Swift/Rust (modern
type system, no classical inheritance, pattern matching), and Lua/Wren
(capability-based sandbox, embeddability).

### Quick taste

This program runs as-is — `lyric run taste.lyr`:

```lyr
import std.io.console { println };
import std.math { sqrt, pi };
import std.collections { emptyList };

struct Vector3 {
    x: float,
    y: float,
    z: float,

    fn length(): float {
        return sqrt(this.x * this.x + this.y * this.y + this.z * this.z);
    }
}

enum Shape {
    Circle(float),
    Rectangle(float, float),
    Empty;

    fn area(): float {
        return match (this) {
            Circle(r)       => pi * r * r,
            Rectangle(w, h) => w * h,
            Empty           => 0.0,
        };
    }
}

fn main(): int {
    let v = Vector3 { x = 1.0, y = 2.0, z = 2.0 };
    println(f"|v| = {v.length():N2}");

    let shapes = emptyList<Shape>();
    shapes.push(Shape.Circle(2.5));
    shapes.push(Shape.Rectangle(3.0, 4.0));

    for (s in shapes) {
        println(f"area = {s.area():N2}");
    }
    return 0;
}
```

```
|v| = 3.00
area = 19.63
area = 12.00
```

### What works today

Classes, structs with value semantics, enums with payloads and `match`, interfaces with vtable
dispatch, generics via monomorphization, closures, coroutines, exceptions with `defer`, tuples,
`extend` blocks, optionals with flow narrowing — and a standard library with strings, formatting,
collections, math, files and OS access behind capabilities.

The [`examples/`](examples/) directory has 22 programs, all of them exercised by the test suite.
[`examples/wc.lyr`](examples/wc.lyr) is a word-count clone that produces the same numbers as
POSIX `wc`.

## Documentation

| File | Purpose |
|---|---|
| [`docs/Doku.md`](docs/Doku.md) | User-facing documentation with examples (start here) |
| [`docs/Sprache.md`](docs/Sprache.md) | Formal language specification (EBNF) |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Milestones, architecture, design decisions |
| [`docs/IDEAS.md`](docs/IDEAS.md) | Post-v1 idea pile (no commitments) |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Project rules and process |

## Project layout

```
lyric/
├── src/
│   ├── Lyric.Core/          → lyrcore.dll  Diagnostics, SourceManager, Span,
│   │                                       and the read side of the bytecode format
│   ├── Lyric.Frontend/      → lyrfe.dll    Everything between source and bytes:
│   │                                       Lexing, AST, Parsing, Resolver, Sema,
│   │                                       Ir, Emit (write side), Compiler
│   ├── Lyric.Vm/            → lyrrt.dll    Interpreter
│   ├── Lyrc/                → lyrc.exe     the compiler
│   ├── Lyrvm/               → lyrvm.exe    the bundled runtime
│   ├── Lyrrepl/             → lyrrepl.exe  the interactive prompt
│   └── Lyric.Cli/           → lyric.exe    the driver
├── stdlib/                 Stdlib source (.lyr files)
├── tests/                  xUnit test projects
├── examples/               Example programs
├── build/                  publish.proj — the shipping definition
├── tooling/                VS Code extension: TextMate grammar + run command
└── docs/                   Documentation
```

See [`docs/ROADMAP.md`](docs/ROADMAP.md) for the M0–M10 milestone plan.

## The four binaries

Like `dotnet`/`csc` or `cargo`/`rustc`, the toolchain separates the friendly driver from the
tools it drives. In daily use you only need `lyric`.

| Binary | Role |
|---|---|
| `lyric` | Driver. `run`, `build`, `check`, `disasm`, `repl` — it dispatches, it does not compile |
| `lyrc` | Compiler. `build`, `check`, plus the `lower`/`parse`/`tokenize` debug dumps |
| `lyrvm` | Runtime. `run`, `disasm`, `verify` on `.lyrbc` only — it does not compile |
| `lyrrepl` | The interactive prompt. The only tool that holds both sides at once (ADR-021) |

Because `.lyrbc` is a specified format ([`docs/Bytecode.md`](docs/Bytecode.md)), a third party can
write their own runtime. Point the driver at it with `lyric run app.lyr --vm ./their-runtime`, or
set `LYRIC_VM`. What such a runtime has to honor is the four-point runner contract in
[`docs/Bytecode.md` §9](docs/Bytecode.md).

## Building

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project src/Lyric.Cli -- run examples/hello.lyr
```

Or, after publishing:

```bash
lyric run examples/wc.lyr -- README.md
```

### The prompt

```
$ lyric repl
Lyric 0.9.0 — :help for commands, :quit to leave
lyr> let x = 5
lyr> x * 2
10
lyr> fn double(n: int): int { return n * 2; }
lyr> double(21)
42
```

Declarations stay for later entries; statements run once. `:list` shows what the session
remembers, `:reset` forgets it.

### Shipping

One command publishes all four binaries into a single directory:

```bash
dotnet msbuild build/publish.proj
```

The result lands in `artifacts/publish/` and is framework-dependent — it needs a
.NET 10 runtime on the target machine. What ends up there, and nothing else:

```
lyric.exe  lyrc.exe               driver and compiler
lyrvm.exe  lyrrepl.exe            runtime and interactive prompt
lyrcore.dll                        diagnostics + the read side of the bytecode format
lyrfe.dll                          everything between source and bytes
lyrrt.dll                          the interpreter
*.runtimeconfig.json               which framework version to load
stdlib/                            the standard library, as .lyr source
```

No PDBs, no `.deps.json`, no XML doc files. `lyrvm.exe` deliberately ships
neither `lyrfe.dll` nor `stdlib/`: a runtime gets finished bytes, never source
(ADR-013, ADR-017). A test enforces that.

Pass `-p:PublishRoot=<dir>` to publish elsewhere. The target directory is wiped
first — a publish directory that grows across refactors accumulates DLLs under
names that no longer exist.

## Why "Lyric"?

The name is a placeholder picked during initial design. It is short, typeable,
and not collision-prone with established language names. The file extension is
`.lyr`. If a better name surfaces before v1.0, it will be renamed via
search-and-replace; nothing in the design depends on the name.

## License

[MIT](LICENSE)
