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
| `std.io.console` | `print`, `println`, `readLine` |
| `std.io.file` | reading and writing files — requires `fileAccess` |
| `std.os` | environment, process, exit — requires `osAccess` |
| `std.build` | `addExecutable` — only a `build.lyr` run by `lyric build` can use it |

## Collections

`List<T>` grows; `T[]` does not.

```lyr
import std.io.console { println };
import std.collections { emptyList };

fn main(): int {
    let items = emptyList<string>();
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
