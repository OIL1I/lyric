# The standard library

The standard library is written in Lyric and ships as source alongside the toolchain.

| Module | Contents |
|---|---|
| `std.core` | `panic`, `assert`, `todo`, `unreachable`, `Exception`, `Display`, `Hashable`, `Equatable`, `Ordered` |
| `std.string` | inspection, search, split, join, trim, pad, parsing, `StringBuilder` |
| `std.fmt` | number formatting, padding, alignment, tables |
| `std.math` | `sqrt`, `pi`, `abs`, `min`, `max`, rounding, trigonometry |
| `std.collections` | `List<T>`, `Map<K, V>`, `Set<T>`, `Indexable<T>`, sorting |
| `std.iter` | `Iterator<T>`, `Iterable<T>`, adapters, `sum` |
| `std.option` | `map`, `andThen`, `filter`, `zip`, `contains`, `toArray`, `iter`, `expect` |
| `std.io.console` | `print`, `println`, `readLine` — the writers take any `Display` value: `println(42)` |
| `std.io.file` | reading and writing files — requires `fileAccess` |
| `std.os` | environment, process, exit — requires `osAccess` |
| `std.random` | `Random.seeded`, `shuffle`, `choice`, `nextGaussian` — deterministic, no capability |
| `std.time` | `Instant`, `Duration`, ISO 8601 — requires `osAccess` |
| `std.build` | `addExecutable` — only a `build.lyr` run by `lyric build` can use it |

## Collections

`List<T>` grows; `T[]` does not.

```lyr
import std.io.console { println };
import std.collections { List };

fn main(): int {
    let items = List<string>.empty();
    items.push("first");
    items.push("second");

    for (item in items) {
        println(item);
    }

    let copy = items.toArray();
    return copy.length;
}
```

## Iteration

Anything implementing `Iterable<T>` works with `for-in`, including your own types.

```lyr
import std.io.console { println };
import std.iter { Iterator, Iterable };

class Countdown :: [Iterable<int>] {
    from: int,

    fn iter(): Iterator<int> {
        return CountdownIter { remaining = this.from };
    }
}

class CountdownIter :: [Iterator<int>] {
    remaining: int,

    mut fn next(): ?int {
        if (this.remaining <= 0) { return null; }
        let value = this.remaining;
        this.remaining = this.remaining - 1;
        return value;
    }
}

fn main(): int {
    var total = 0;
    for (n in Countdown { from = 3 }) {
        total = total + n;
    }
    println(f"{total}");
    return total;
}
```

An `Iterator<T>` yields `?T` and signals the end with `null`. `Iterable<T>` hands out a fresh
iterator per call, so two loops over the same collection do not interfere.

## Capabilities

`std.io.file`, `std.io.net` and `std.os` require a capability. A standalone run grants
everything; a host grants explicitly, and a module that requires more than it is granted is
rejected before it runs.

## Strings have methods

Since v1.15 the string API is method-shaped: `s.trim()`, `s.split(",")`, `s.contains(x)`,
`s.length()`. The free forms (`trim(s)`, …) still work and warn as deprecated; they go with the
next major. `concat` and `repeat` stay free — they back `+` and `*` — and the type-directed
families (`fromInt`, `parseInt`, …) keep their names.

The methods come with the module: any `import std.string { … }` makes them visible, and a file
that needs no free name imports the module under an alias, which avoids shadowing the builtin
type name:

```lyr
import std.string as strings;

fn main(): int {
    return "  Grüße  ".trim().length();
}
```

Two things worth knowing: `s.length()` is a call BECAUSE it costs O(n) — a property would
promise a stored answer this type does not have — and every method returns a NEW string;
`s.trim();` as a statement does nothing, because a Lyric string has nothing to mutate.

## Constructors live on the types

A container comes from its type: `List<int>.empty()`, `Map<string, int>.empty()`,
`Set<int>.empty()`, `StringBuilder.new()`, `Random.seeded(42)` — the latter from `std.random`
since v1.14, where `shuffle`, `choice` and `nextGaussian` live beside it. The old free functions
(`emptyList`, `emptyMap`, `emptySet`, `newRandom`) and the `std.math.Random` twin still work and
warn as deprecated; they disappear with the next major version.

## Editing the standard library

The toolchain reads the standard library from **beside its own binary** — a copy made at build
time — or from `LYRIC_STDLIB` when that is set. Editing the sources in the repository therefore
changes nothing until the next `dotnet build` refreshes the copies, and a published toolchain
froze its copy when it was packaged. When an edit "is not found", it is almost always this:
point `LYRIC_STDLIB` at the repository's `stdlib/` while working on it.

The library also holds itself accountable: every public item is documented (a test pins
completeness), every file is formatted and compiles without a single diagnostic, and
`stdlib-tests/` carries its behavioral tests — written in Lyric, run by `lyric test`.
