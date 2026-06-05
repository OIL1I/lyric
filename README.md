# Lyric

A statically typed, GC-managed application language with an embeddable bytecode VM.

![CI](https://github.com/OIL1I/lyric/actions/workflows/ci.yml/badge.svg)

> **Status: pre-alpha.** Lyric is in early development. The language design is
> frozen for v1.0 (see [`docs/Sprache.md`](docs/Sprache.md)), but no working
> compiler exists yet. Current milestone: M0 (project scaffolding).

## What is Lyric?

Lyric is designed for two use cases that share the same language and runtime:

- **Standalone applications** — CLI tools, desktop apps, servers.
- **Embedded scripting** — drop the VM into a C# host (game engine, editor,
  build tool) and let users write scripts with controlled capabilities.

The design borrows from C# (familiar surface syntax), Swift/Rust (modern
type system, no classical inheritance, pattern matching), and Lua/Wren
(capability-based sandbox, embeddability).

### Quick taste

```lyr
import std.io.console;

pub struct Vector3 :: [Equatable] {
    x: float,
    y: float,
    z: float,

    fn length(): float {
        return sqrt(this.x*this.x + this.y*this.y + this.z*this.z);
    }

    fn equals(other: Vector3): bool {
        return this.x == other.x && this.y == other.y && this.z == other.z;
    }
}

pub enum Shape {
    Circle(float),
    Rectangle(float, float),
    Empty;

    fn area(): float {
        return match (this) {
            Circle(r) => pi * r * r,
            Rectangle(w, h) => w * h,
            Empty => 0.0,
        };
    }
}

fn main(): int {
    let v = Vector3 { x = 1.0, y = 2.0, z = 2.0 };
    console.println(f"|v| = {v.length():N2}");

    let s = Shape.Circle(2.5);
    console.println(f"area = {s.area():N2}");
    return 0;
}
```

## Documentation

| File | Purpose |
|---|---|
| [`docs/Doku.md`](docs/Doku.md) | User-facing documentation with examples (start here) |
| [`docs/Sprache.md`](docs/Sprache.md) | Formal language specification (EBNF) |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Milestones, architecture, design decisions |
| [`docs/IDEAS.md`](docs/IDEAS.md) | Post-v1 idea pile (no commitments) |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Project rules and process |

## Project layout (planned)

```
lyric/
├── src/
│   ├── Lyric.Core/         Diagnostics, SourceManager, Span
│   ├── Lyric.Lexing/       Tokenizer
│   ├── Lyric.Parsing/      Recursive-descent + Pratt
│   ├── Lyric.Sema/         Type checker, generics monomorphization
│   ├── Lyric.Bytecode/     Bytecode format, serializer
│   ├── Lyric.Vm/           Interpreter
│   ├── Lyric.Stdlib/       Standard library bindings
│   ├── Lyric.Embedding/    Host API for embedders
│   └── Lyric.Cli/          CLI entry point
├── stdlib/                 Stdlib source (.lyr files)
├── tests/                  xUnit test projects
├── examples/               Example programs
├── tooling/                Editor integration (TextMate grammar, etc.)
└── docs/                   Documentation
```

See [`docs/ROADMAP.md`](docs/ROADMAP.md) for the M0–M10 milestone plan.

## Building (once M0 is complete)

```bash
dotnet build
dotnet test
dotnet run --project src/Lyric.Cli -- --version
```

## Why "Lyric"?

The name is a placeholder picked during initial design. It is short, typeable,
and not collision-prone with established language names. The file extension is
`.lyr`. If a better name surfaces before v1.0, it will be renamed via
search-and-replace; nothing in the design depends on the name.

## License

[MIT](LICENSE)
