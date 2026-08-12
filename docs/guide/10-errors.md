# Errors

Lyric separates two kinds of failure.

- A **panic** is a programming error: an index out of range, an unwrap of an absent value. It is
  not catchable and ends the process with exit code `101`.
- An **exception** is an expected condition that a caller may handle.

## Panics

```lyr
import std.core { panic, assert };

fn take(items: int[], index: int): int {
    if (index < 0 || index >= items.length) {
        panic("index out of range");
    }
    return items[index];
}

fn main(): int {
    assert(true, "this holds");
    return take([1, 2, 3], 1);
}
```

`std.core` also offers `todo` and `unreachable` for the same purpose.

## Exceptions

A function that can throw declares `throws`. A caller either handles it or declares `throws`
itself.

`throws` without a type means `Throwable` — anything. Naming a type narrows the declaration, and a
`catch` for that type then covers the call completely.

```lyr
import std.core { Exception };

fn parse(text: string): int throws Exception {
    if (text == "") {
        throw Exception { text = "empty input" };
    }
    return 1;
}

fn main(): int {
    try {
        let n = parse("");
        return n;
    } catch (e: Exception) {
        return 0;
    }
}
```

A `catch` may bind by type, bind everything, or bind nothing:

```lyr
import std.core { Exception };

fn risky(): int throws {
    throw Exception { text = "no" };
}

fn main(): int {
    try {
        return risky();
    } catch (e: Exception) {
        return 1;
    } catch (other) {
        return 2;
    }
}
```

`main` cannot declare `throws`; an exception that reaches it aborts the process.

## Cleanup

`defer` runs when the scope ends, on the normal path and while unwinding:

```lyr
import std.core { Exception };
import std.io.console { println };

fn work(): int throws Exception {
    defer println("released");

    throw Exception { text = "failed" };
}

fn main(): int {
    try {
        return work();
    } catch (e: Exception) {
        return 0;
    }
}
```

There is no `finally`; `defer` covers it.

## Custom exception types

Any type that satisfies `Throwable` can be thrown. `Throwable` is built in and needs no import;
`std.core.Exception` is the ready-made implementation, whose field is `text` and whose method is
`message()`.

```lyr
class NotFound :: [Throwable] {
    what: string,
    fn message(): string { return "not found: " + this.what; }
}

fn lookup(key: string): int throws NotFound {
    throw NotFound { what = key };
}

fn main(): int {
    try {
        return lookup("key");
    } catch (e: NotFound) {
        return 0;
    }
}
```
